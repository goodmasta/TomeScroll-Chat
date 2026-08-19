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
/// <para>A triggered reply doesn't send immediately - it's queued for a random delay
/// (<see cref="Configuration.AutoReplyDelayMinSeconds"/>-<see cref="Configuration.AutoReplyDelayMaxSeconds"/>,
/// 1-5s by default) and actually sent from <see cref="OnFrameworkUpdate"/> once that time is up, per
/// explicit user request - makes the reply feel less like an instant, obviously-automated response.</para>
///
/// <para><b>Two independent safety guards</b> against this becoming spammy or looping forever:
/// <see cref="Configuration.AutoReplyCooldownMinutes"/> (a per-sender cooldown - the main defence
/// against a runaway back-and-forth if the other side also has some kind of auto-reply/bot), and a
/// small fixed <see cref="MinGapBetweenReplies"/> between *any* two auto-sends regardless of sender
/// (protects against the game's own chat-spam guard - see <c>ChatCaptureService.ChatSystemErrorMarkers</c> -
/// if many *different* senders trigger this within a short window, e.g. several people shouting the
/// player's name back to back). Both are checked against each candidate reply's *scheduled send time*
/// (trigger time + its random delay), not the trigger time itself - otherwise two triggers spaced far
/// enough apart to each individually pass the check could still end up with their delayed *actual
/// sends* landing closer together than <see cref="MinGapBetweenReplies"/> intends to guarantee, which
/// would defeat the whole point of that guard. Excess triggers within either window are just dropped,
/// not queued/staggered - simpler, and avoids ever producing an obviously-bot-like burst-then-trickle
/// reply pattern.</para>
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

    private readonly record struct PendingReply(string SenderKey, string SenderName, DateTime SendAt);

    private readonly IFramework framework;
    private readonly ChatCaptureService chatCaptureService;
    private readonly ChatSendService chatSendService;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;
    private readonly IPluginLog log;
    private readonly Random random = new();

    private readonly Dictionary<string, DateTime> lastReplyBySender = new();
    private readonly List<PendingReply> pending = new();
    private DateTime lastReplyAt = DateTime.MinValue;

    public AutoReplyService(IFramework framework, ChatCaptureService chatCaptureService, ChatSendService chatSendService, Configuration configuration, NotificationService notificationService, IPluginLog log)
    {
        this.framework = framework;
        this.chatCaptureService = chatCaptureService;
        this.chatSendService = chatSendService;
        this.configuration = configuration;
        this.notificationService = notificationService;
        this.log = log;

        chatCaptureService.RawMessageReceived += OnRawMessage;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnRawMessage(XivChatType chatType, string senderName, string senderKey, string body, bool isFromLocalPlayer)
    {
        try
        {
            Handle(chatType, senderName, senderKey, body, isFromLocalPlayer);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: auto-reply handling failed");
        }
    }

    private void Handle(XivChatType chatType, string senderName, string senderKey, string body, bool isFromLocalPlayer)
    {
        if (!configuration.AutoReplyEnabled || string.IsNullOrEmpty(senderKey) || string.IsNullOrWhiteSpace(configuration.AutoReplyMessage))
            return;

        var localPlayerKey = Plugin.GetLocalPlayerKey();
        var localPlayerName = localPlayerKey?.Split('@')[0];

        // Never reply to the player's own messages - an outgoing tell, or their own name showing up
        // as the "sender" some other way, would otherwise be indistinguishable from someone else
        // legitimately triggering this. isFromLocalPlayer (Dalamud's own XivChatRelationKind.LocalPlayer,
        // see ChatMessageRecord.IsFromLocalPlayer) is the authoritative check; the string-matching
        // fallbacks stay in place for the same reason ChatMessageRenderer keeps them (this channel-name
        // formatting quirk isn't confirmed exhaustive across every channel).
        var isOwn = isFromLocalPlayer ||
                    chatType == XivChatType.TellOutgoing ||
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
        var sendAt = now + NextDelay();

        if (sendAt - lastReplyAt < MinGapBetweenReplies)
            return;

        var cooldown = TimeSpan.FromMinutes(Math.Max(0, configuration.AutoReplyCooldownMinutes));
        if (lastReplyBySender.TryGetValue(senderKey, out var lastToThisSender) && sendAt - lastToThisSender < cooldown)
            return;

        lastReplyAt = sendAt;
        lastReplyBySender[senderKey] = sendAt;
        pending.Add(new PendingReply(senderKey, senderName, sendAt));

        log.Info("TomeScrollChat: auto-reply to {SenderKey} queued for {Delay:0.0}s from now (triggered by {ChatType})", senderKey, (sendAt - now).TotalSeconds, chatType);
    }

    /// <summary>Random delay before a queued reply actually sends - <see cref="Configuration.AutoReplyDelayMinSeconds"/>/
    /// <see cref="Configuration.AutoReplyDelayMaxSeconds"/>, clamped here (not in Settings) so an
    /// accidentally-inverted min/max still produces a sane, non-negative delay instead of throwing.</summary>
    private TimeSpan NextDelay()
    {
        var min = Math.Max(0f, configuration.AutoReplyDelayMinSeconds);
        var max = Math.Max(min, configuration.AutoReplyDelayMaxSeconds);
        var seconds = min + (float)random.NextDouble() * (max - min);
        return TimeSpan.FromSeconds(seconds);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (pending.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var reply = pending[i];
            if (reply.SendAt > now)
                continue;

            pending.RemoveAt(i);
            Send(reply.SenderKey, reply.SenderName);
        }
    }

    private void Send(string senderKey, string senderName)
    {
        try
        {
            chatSendService.Send($"/tell {senderKey}", configuration.AutoReplyMessage);
            notificationService.Show($"Auto-reply sent to {senderName}.", NotificationSeverity.Info);
            log.Info("TomeScrollChat: auto-replied to {SenderKey}", senderKey);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send queued auto-reply to {SenderKey}", senderKey);
        }
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
        framework.Update -= OnFrameworkUpdate;
    }
}
