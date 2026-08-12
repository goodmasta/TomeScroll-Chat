using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// Hooks incoming/outgoing chat, classifies each message into every matching regular tab (by
/// channel + optional keyword/regex filter) or into the relevant whisper tab, and forwards it to
/// both the UI (<see cref="MessageRouted"/>) and <see cref="ChatHistoryService"/> for persistence.
/// Subscribes to the raw <c>ChatMessage</c> event (fires unconditionally for every message) rather
/// than <c>ChatMessageUnhandled</c> - the latter only fires once "handled" status has been decided,
/// which turned out not to be a reliable signal to depend on for "did this message happen at all".
/// We don't call <see cref="IHandleableChatMessage.PreventOriginal"/>, so other plugins and the
/// (hidden) native chat log still see every message exactly as before.
/// </summary>
public sealed class ChatCaptureService : IDisposable
{
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly TabManager tabManager;
    private readonly ChatHistoryService historyService;

    /// <summary>Whisper partner ("Name@World") the next outgoing tell should be attributed to if the
    /// game's own sender payload for that event doesn't resolve one. Set by <see cref="ChatSendService"/>
    /// immediately before sending a "/tell" command.</summary>
    public string? PendingOutgoingTellTarget { get; set; }

    public event Action<ChatTabConfig, ChatMessageRecord>? MessageRouted;

    public ChatCaptureService(IChatGui chatGui, IPluginLog log, TabManager tabManager, ChatHistoryService historyService)
    {
        this.chatGui = chatGui;
        this.log = log;
        this.tabManager = tabManager;
        this.historyService = historyService;
        chatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            Handle(message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "CustomChat: failed to process an incoming chat message");
        }
    }

    private void Handle(IChatMessage message)
    {
        var chatType = message.LogKind;
        var senderText = message.Sender.TextValue;
        var senderKey = ExtractSenderKey(message.Sender);
        var body = message.Message.TextValue;
        var payloadLinks = ExtractPayloadLinks(message.Message);
        // IChatMessage.Timestamp reads back 0 for the raw ChatMessage event in the Dalamud version
        // this was tested against (every message showed the same UTC-epoch-in-local-time clock),
        // so this uses wall-clock time at the moment the message is actually handled instead - for
        // a live chat capture that's effectively the same instant anyway.
        var timestamp = DateTime.UtcNow;

        if (chatType is XivChatType.TellIncoming or XivChatType.TellOutgoing)
        {
            HandleTell(chatType, senderText, senderKey, body, payloadLinks, timestamp);
            return;
        }

        foreach (var tab in tabManager.Tabs)
        {
            if (tab.IsPmTab || !tab.Channels.Contains(chatType) || !MatchesFilter(tab, body))
                continue;

            var record = new ChatMessageRecord
            {
                TimestampUtc = timestamp,
                ChatType = chatType,
                SenderName = senderText,
                SenderKey = senderKey,
                Body = body,
                RoutingKey = tab.Id.ToString(),
                PayloadLinks = payloadLinks,
            };
            historyService.Enqueue(record);
            MessageRouted?.Invoke(tab, record);
        }
    }

    private void HandleTell(XivChatType chatType, string senderText, string senderKey, string body, IReadOnlyList<ChatPayloadLink> payloadLinks, DateTime timestamp)
    {
        var partnerKey = !string.IsNullOrEmpty(senderKey)
            ? senderKey
            : chatType == XivChatType.TellOutgoing && !string.IsNullOrEmpty(PendingOutgoingTellTarget)
                ? PendingOutgoingTellTarget!
                : senderText;

        var displayName = partnerKey.Split('@')[0];
        var tab = tabManager.GetOrCreatePmTab(partnerKey, displayName);

        var record = new ChatMessageRecord
        {
            TimestampUtc = timestamp,
            ChatType = chatType,
            SenderName = senderText,
            SenderKey = senderKey,
            Body = body,
            RoutingKey = partnerKey,
            PayloadLinks = payloadLinks,
        };
        historyService.Enqueue(record);
        MessageRouted?.Invoke(tab, record);
    }

    /// <summary>Walks the raw SeString payload sequence to find map/flag and item links, recording
    /// where their auto-generated display text lands in the flattened <c>TextValue</c> string (see
    /// <see cref="ChatPayloadLink"/>). <c>TextValue</c> only concatenates <see cref="TextPayload.Text"/>
    /// from <see cref="TextPayload"/> instances - every other payload (formatting, the link markers
    /// themselves) contributes zero characters - so the display text for a link is whatever
    /// <see cref="TextPayload"/> immediately follows its marker payload, which is how the game itself
    /// always structures these (a couple of formatting payloads, the link marker, one text payload
    /// with the visible name/coordinates, then formatting payloads closing it back out).</summary>
    private static List<ChatPayloadLink> ExtractPayloadLinks(SeString message)
    {
        var links = new List<ChatPayloadLink>();
        var cursor = 0;
        ChatPayloadLinkType? pendingType = null;
        MapLinkPayload? pendingMapLink = null;

        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload textPayload)
            {
                var text = textPayload.Text ?? string.Empty;
                if (pendingType != null && text.Length > 0)
                {
                    links.Add(new ChatPayloadLink
                    {
                        Type = pendingType.Value,
                        Start = cursor,
                        Length = text.Length,
                        MapLink = pendingMapLink,
                    });
                    pendingType = null;
                    pendingMapLink = null;
                }

                cursor += text.Length;
            }
            else if (payload is MapLinkPayload mapLink)
            {
                pendingType = ChatPayloadLinkType.MapLink;
                pendingMapLink = mapLink;
            }
            else if (payload is ItemPayload)
            {
                pendingType = ChatPayloadLinkType.Item;
                pendingMapLink = null;
            }
        }

        return links;
    }

    private static bool MatchesFilter(ChatTabConfig tab, string body) => tab.FilterMode switch
    {
        ChatTabFilterMode.None => true,
        ChatTabFilterMode.Keyword => !string.IsNullOrEmpty(tab.FilterPattern) &&
            body.Contains(tab.FilterPattern, StringComparison.OrdinalIgnoreCase),
        ChatTabFilterMode.Regex => TryRegexMatch(tab.FilterPattern, body),
        _ => true,
    };

    private static bool TryRegexMatch(string pattern, string body)
    {
        if (string.IsNullOrEmpty(pattern))
            return true;

        try
        {
            return Regex.IsMatch(body, pattern);
        }
        catch (ArgumentException)
        {
            // Invalid regex (e.g. still being typed in settings) - don't hide every message over a typo.
            return true;
        }
    }

    private static string ExtractSenderKey(SeString sender)
    {
        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload player)
            {
                var world = TryGetWorldName(player);
                return world != null ? $"{player.PlayerName}@{world}" : player.PlayerName;
            }
        }

        return string.Empty;
    }

    private static string? TryGetWorldName(PlayerPayload player)
    {
        try
        {
            var world = player.World;
            return world.IsValid ? world.Value.Name.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
    }
}
