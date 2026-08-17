using System;
using System.Collections.Generic;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Sends a fixed, player-written "away message" (<see cref="Configuration.AutoReplyMessage"/> - not
/// AI-generated) automatically as a whisper whenever an incoming whisper arrives (if
/// <see cref="Configuration.AutoReplyToWhispers"/>) or the player's own name is mentioned in one of
/// <see cref="MentionChannels"/> (if <see cref="Configuration.AutoReplyToMentions"/>) - the same
/// name-mention detection <see cref="Windows.ChatMessageRenderer"/>'s own highlight already uses
/// ("по аналогии с тегом", per the explicit user request this was built for). Entirely inert while
/// <see cref="Configuration.AutoReplyEnabled"/> is off, which it is by default - this is the highest-
/// risk feature in the plugin so far (it sends real, visible-to-others messages with no per-message
/// confirmation), so it stays opt-in and is configured from the main window's title bar rather than
/// buried in Settings, per explicit user request.
///
/// <para>A mention-triggered reply always goes back as a *whisper* to whoever mentioned the player,
/// never posted into the public/group channel itself - far less disruptive/spammy than auto-shouting
/// back into Say/Yell/Shout/Linkshell chat.</para>
///
/// <para><b>Two independent safety guards</b> against this becoming spammy or looping forever:
/// <see cref="Configuration.AutoReplyCooldownMinutes"/> (a per-sender cooldown - the main defence
/// against a runaway back-and-forth if the other side also has some kind of auto-reply/bot), and a
/// small fixed <see cref="MinGapBetweenReplies"/> between *any* two auto-sends regardless of sender
/// (protects against the game's own chat-spam guard - see <c>ChatCaptureService.ChatSystemErrorMarkers</c> -
/// if many *different* senders trigger this within a short window, e.g. several people shouting the
/// player's name back to back). Excess triggers within either window are just dropped, not queued -
/// simpler, and avoids ever producing an obviously-bot-like burst-then-trickle reply pattern.</para>
/// </summary>
public sealed class AutoReplyService : IDisposable
{
    /// <summary>Public/group channels the "mentioned" trigger watches - Say/Yell/Shout plus every
    /// group channel (Party/Cross-world Party/Alliance/Free Company/Linkshells/Cross-world Linkshells),
    /// per explicit user request. Deliberately excludes Novice Network/PvP Team/system channels -
    /// not asked for, and being name-dropped there is a much less common "someone's trying to reach
    /// me" signal than the game's main social channels.</summary>
    public static readonly XivChatType[] MentionChannels =
    {
        XivChatType.Say, XivChatType.Yell, XivChatType.Shout,
        XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance, XivChatType.FreeCompany,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    };

    private static readonly TimeSpan MinGapBetweenReplies = TimeSpan.FromSeconds(5);

    private readonly ChatCaptureService chatCaptureService;
    private readonly ChatSendService chatSendService;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;
    private readonly IPluginLog log;

    private readonly Dictionary<string, DateTime> lastReplyBySender = new();
    private DateTime lastReplyAt = DateTime.MinValue;

    public AutoReplyService(ChatCaptureService chatCaptureService, ChatSendService chatSendService, Configuration configuration, NotificationService notificationService, IPluginLog log)
    {
        this.chatCaptureService = chatCaptureService;
        this.chatSendService = chatSendService;
        this.configuration = configuration;
        this.notificationService = notificationService;
        this.log = log;

        chatCaptureService.RawMessageReceived += OnRawMessage;
    }

    private void OnRawMessage(XivChatType chatType, string senderName, string senderKey, string body)
    {
        try
        {
            Handle(chatType, senderName, senderKey, body);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: auto-reply handling failed");
        }
    }

    private void Handle(XivChatType chatType, string senderName, string senderKey, string body)
    {
        if (!configuration.AutoReplyEnabled || string.IsNullOrEmpty(senderKey) || string.IsNullOrWhiteSpace(configuration.AutoReplyMessage))
            return;

        var localPlayerKey = Plugin.GetLocalPlayerKey();
        var localPlayerName = localPlayerKey?.Split('@')[0];

        // Never reply to the player's own messages - an outgoing tell, or their own name showing up
        // as the "sender" some other way, would otherwise be indistinguishable from someone else
        // legitimately triggering this.
        var isOwn = chatType == XivChatType.TellOutgoing ||
                    (!string.IsNullOrEmpty(localPlayerKey) && senderKey == localPlayerKey) ||
                    (!string.IsNullOrEmpty(localPlayerName) && senderName == localPlayerName);
        if (isOwn)
            return;

        var triggered = chatType == XivChatType.TellIncoming
            ? configuration.AutoReplyToWhispers
            : configuration.AutoReplyToMentions
              && Array.IndexOf(MentionChannels, chatType) >= 0
              && !string.IsNullOrEmpty(localPlayerName)
              && Windows.ChatMessageRenderer.ContainsMention(body, localPlayerName);

        if (!triggered)
            return;

        var now = DateTime.UtcNow;
        if (now - lastReplyAt < MinGapBetweenReplies)
            return;

        var cooldown = TimeSpan.FromMinutes(Math.Max(0, configuration.AutoReplyCooldownMinutes));
        if (lastReplyBySender.TryGetValue(senderKey, out var lastToThisSender) && now - lastToThisSender < cooldown)
            return;

        lastReplyAt = now;
        lastReplyBySender[senderKey] = now;

        chatSendService.Send($"/tell {senderKey}", configuration.AutoReplyMessage);
        notificationService.Show($"Auto-reply sent to {senderName}.", NotificationSeverity.Info);
        log.Info("TomeScrollChat: auto-replied to {SenderKey} (triggered by {ChatType})", senderKey, chatType);
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
    }
}
