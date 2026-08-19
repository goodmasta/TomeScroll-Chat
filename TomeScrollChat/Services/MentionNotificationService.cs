using System;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Pops an in-game notification (with sound, via the normal <see cref="NotificationService.Show"/>
/// pipeline - respects <see cref="Configuration.NotificationSoundEnabled"/>) whenever the local
/// player's full/first/last name is mentioned by someone else in one of
/// <see cref="AutoReplyService.MentionChannels"/> -
/// the same "someone's trying to reach me" channel set and <see cref="Windows.ChatMessageRenderer.ContainsMention"/>
/// detection <see cref="AutoReplyService"/> already uses for its own "reply when mentioned" trigger, but
/// entirely independent of <see cref="Configuration.AutoReplyEnabled"/> - this is "tell me about it",
/// not "send something back", the same relationship <see cref="WhisperNotificationService"/> has to
/// whisper auto-replies. Listens to the same <see cref="ChatCaptureService.RawMessageReceived"/> event
/// (fires exactly once per real chat event, not once per matching tab) for the same reason.
/// </summary>
public sealed class MentionNotificationService : IDisposable
{
    /// <summary>Toast text is a single line in a small popup - long messages are cut off rather than
    /// blowing out the notification's size, same as <see cref="WhisperNotificationService"/>'s own preview.</summary>
    private const int PreviewMaxLength = 80;

    private readonly ChatCaptureService chatCaptureService;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;
    private readonly IPluginLog log;

    public MentionNotificationService(ChatCaptureService chatCaptureService, Configuration configuration, NotificationService notificationService, IPluginLog log)
    {
        this.chatCaptureService = chatCaptureService;
        this.configuration = configuration;
        this.notificationService = notificationService;
        this.log = log;

        chatCaptureService.RawMessageReceived += OnRawMessage;
    }

    private void OnRawMessage(XivChatType chatType, string senderName, string senderKey, string body, bool isFromLocalPlayer)
    {
        try
        {
            Handle(chatType, senderName, body, isFromLocalPlayer);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: mention notification handling failed");
        }
    }

    private void Handle(XivChatType chatType, string senderName, string body, bool isFromLocalPlayer)
    {
        // isFromLocalPlayer guards against notifying on the player's own message, in case it happens
        // to contain their own name (e.g. replying to someone who just said it) - the same self-mention
        // AutoReplyService.Handle already excludes for its own "reply when mentioned" trigger.
        if (!configuration.NotifyOnMention || isFromLocalPlayer || string.IsNullOrWhiteSpace(body))
            return;

        if (Array.IndexOf(AutoReplyService.MentionChannels, chatType) < 0)
            return;

        var localPlayerName = Plugin.GetLocalPlayerKey()?.Split('@')[0];
        if (string.IsNullOrEmpty(localPlayerName) || !Windows.ChatMessageRenderer.ContainsMention(body, localPlayerName))
            return;

        var preview = body.Length > PreviewMaxLength ? body[..PreviewMaxLength] + "..." : body;
        notificationService.Show($"{senderName}: {preview}", NotificationSeverity.Info);
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
    }
}
