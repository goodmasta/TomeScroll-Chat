using System;
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
/// Subscribes to <c>ChatMessageUnhandled</c> rather than the raw <c>ChatMessage</c> event so a
/// message another plugin has already claimed/suppressed isn't shown a second time here.
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
        chatGui.ChatMessageUnhandled += OnChatMessageUnhandled;
    }

    private void OnChatMessageUnhandled(IChatMessage message)
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
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).UtcDateTime;

        if (chatType is XivChatType.TellIncoming or XivChatType.TellOutgoing)
        {
            HandleTell(chatType, senderText, senderKey, body, timestamp);
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
            };
            historyService.Enqueue(record);
            MessageRouted?.Invoke(tab, record);
        }
    }

    private void HandleTell(XivChatType chatType, string senderText, string senderKey, string body, DateTime timestamp)
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
        };
        historyService.Enqueue(record);
        MessageRouted?.Invoke(tab, record);
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
        chatGui.ChatMessageUnhandled -= OnChatMessageUnhandled;
    }
}
