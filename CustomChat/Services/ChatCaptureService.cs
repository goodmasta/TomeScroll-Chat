using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Chat types the game routes command-syntax/target errors through - checked against
    /// both since it's not confirmed which one specifically carries "invalid command" style messages,
    /// and treating either as a candidate costs nothing (see <see cref="LooksLikeInvalidCommandError"/>).</summary>
    private static readonly XivChatType[] CommandErrorChatTypes = { XivChatType.ErrorMessage, XivChatType.SystemError };

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TabManager tabManager;
    private readonly ChatHistoryService historyService;
    private readonly NotificationService notificationService;

    /// <summary>Whisper partner ("Name@World") the next outgoing tell should be attributed to if the
    /// game's own sender payload for that event doesn't resolve one. Set by <see cref="ChatSendService"/>
    /// immediately before sending a "/tell" command.</summary>
    public string? PendingOutgoingTellTarget { get; set; }

    public event Action<ChatTabConfig, ChatMessageRecord>? MessageRouted;

    public ChatCaptureService(IChatGui chatGui, IPluginLog log, Configuration configuration, TabManager tabManager, ChatHistoryService historyService, NotificationService notificationService)
    {
        this.chatGui = chatGui;
        this.log = log;
        this.configuration = configuration;
        this.tabManager = tabManager;
        this.historyService = historyService;
        this.notificationService = notificationService;
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

        if (configuration.NotifyOnInvalidCommand && Array.IndexOf(CommandErrorChatTypes, chatType) >= 0 && LooksLikeInvalidCommandError(body))
            notificationService.Show(body, NotificationSeverity.Error);

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
            if (!tab.DisableLogging)
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
        if (!tab.DisableLogging)
            historyService.Enqueue(record);
        MessageRouted?.Invoke(tab, record);
    }

    /// <summary>Walks the raw SeString payload sequence to find map/flag and item links, recording
    /// where their auto-generated display text lands in the flattened <c>TextValue</c> string (see
    /// <see cref="ChatPayloadLink"/>). <c>TextValue</c> only concatenates <see cref="TextPayload.Text"/>
    /// from <see cref="TextPayload"/> instances - every other payload (formatting, the link markers
    /// themselves) contributes zero characters.
    /// <para>The display text isn't always a single <see cref="TextPayload"/> immediately after the
    /// marker - confirmed via a real received map link (2026-08-13) to actually look like
    /// <c>MapLinkPayload, UIForegroundPayload, UIGlowPayload, TextPayload, UIGlowPayload,
    /// UIForegroundPayload, TextPayload, RawPayload</c>: an icon-glyph <see cref="TextPayload"/> inside
    /// the glow/colour span, then a second <see cref="TextPayload"/> *outside* it (the actual readable
    /// "Place Name (X, Y)" text) before the closing <see cref="RawPayload"/>. The original version of
    /// this method only captured the first <see cref="TextPayload"/> (just the icon glyph) and stopped,
    /// leaving the actual coordinate text plain and unclickable - reported as "other players'
    /// coordinates aren't clickable in chat". Fixed by accumulating every consecutive
    /// <see cref="TextPayload"/> into one combined span, only ending it at the first payload that
    /// isn't text or the two "safe" formatting types (<see cref="UIForegroundPayload"/>/
    /// <see cref="UIGlowPayload"/>) that appear between them here.</para></summary>
    private static List<ChatPayloadLink> ExtractPayloadLinks(SeString message)
    {
        var links = new List<ChatPayloadLink>();
        var cursor = 0;
        ChatPayloadLinkType? pendingType = null;
        MapLinkPayload? pendingMapLink = null;
        ItemPayload? pendingItemLink = null;
        PartyFinderPayload? pendingPartyFinderLink = null;
        var pendingStart = 0;
        var pendingLength = 0;

        void CommitPending()
        {
            if (pendingType != null && pendingLength > 0)
            {
                links.Add(new ChatPayloadLink
                {
                    Type = pendingType.Value,
                    Start = pendingStart,
                    Length = pendingLength,
                    MapLink = pendingMapLink,
                    Item = pendingItemLink,
                    PartyFinder = pendingPartyFinderLink,
                });
            }

            pendingType = null;
            pendingMapLink = null;
            pendingItemLink = null;
            pendingPartyFinderLink = null;
            pendingLength = 0;
        }

        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload textPayload)
            {
                var text = textPayload.Text ?? string.Empty;
                if (pendingType != null && text.Length > 0)
                {
                    if (pendingLength == 0)
                        pendingStart = cursor;
                    pendingLength += text.Length;
                }

                cursor += text.Length;
            }
            else if (payload is MapLinkPayload mapLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.MapLink;
                pendingMapLink = mapLink;
            }
            else if (payload is ItemPayload itemLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.Item;
                pendingItemLink = itemLink;
            }
            else if (payload is PartyFinderPayload pfLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.PartyFinder;
                pendingPartyFinderLink = pfLink;
            }
            else if (payload is not (UIForegroundPayload or UIGlowPayload))
            {
                // Anything else (a RawPayload closing marker, another unrelated payload, ...) ends the
                // current link's span - only text and these two "safe" formatting toggles extend it.
                CommitPending();
            }
        }

        CommitPending();

        return links;
    }

    /// <summary>Best-effort text match for FFXIV's own "invalid slash command" system message
    /// (something like <c>The command "/xyz" does not exist.</c>) - chat type alone
    /// (<see cref="CommandErrorChatTypes"/>) isn't specific enough on its own, since the same error
    /// channels also carry unrelated in-game errors (failed actions, out-of-range targets, etc.) that
    /// would otherwise make <see cref="Configuration.NotifyOnInvalidCommand"/> noisy well beyond just
    /// bad commands. English-client text only - not verified against other game languages.</summary>
    private static bool LooksLikeInvalidCommandError(string body) =>
        body.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

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
