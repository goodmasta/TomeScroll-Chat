using System;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Pops a <see cref="NotificationService"/> toast (sender + a short preview of the message) whenever an
/// incoming whisper arrives - Settings > Notifications (<see cref="Configuration.NotifyOnWhisper"/>, on
/// by default), added per explicit user request ("хочу, чтобы такое уведомление было и когда приходит
/// сообщение в личку"). Deliberately separate from <see cref="AutoReplyService"/> - this is "tell me
/// about it", not "send something back", so it works standalone without opting into auto-reply at all,
/// and listens to the same <see cref="ChatCaptureService.RawMessageReceived"/> event (fires exactly once
/// per real chat event, not once per matching tab) for the same reason <see cref="AutoReplyService"/>
/// does.
///
/// <para>Resolves which sound to pass to <see cref="NotificationService.Show"/> as its override, per
/// explicit follow-up user request to tell whispers apart from every other notification by sound alone,
/// even with nothing custom configured: <see cref="Configuration.CustomWhisperNotificationSoundPath"/>
/// if set, else <see cref="NotificationSoundService.DefaultWhisperSoundPath"/> - a bundled clip distinct
/// from the plugin's general default (see that class's own doc comment). Either way,
/// <see cref="NotificationSoundService"/> itself still falls further back (general custom sound, then
/// the general bundled default, then Windows' own scheme sound) if the resolved path turns out to be
/// missing.</para>
/// </summary>
public sealed class WhisperNotificationService : IDisposable
{
    /// <summary>Toast text is a single line in a small popup - long messages are cut off rather than
    /// blowing out the notification's size, same idea as <c>GeminiService.Truncate</c> for a log line.</summary>
    private const int PreviewMaxLength = 80;

    private readonly ChatCaptureService chatCaptureService;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;
    private readonly NotificationSoundService notificationSoundService;
    private readonly IPluginLog log;

    public WhisperNotificationService(ChatCaptureService chatCaptureService, Configuration configuration, NotificationService notificationService, NotificationSoundService notificationSoundService, IPluginLog log)
    {
        this.chatCaptureService = chatCaptureService;
        this.configuration = configuration;
        this.notificationService = notificationService;
        this.notificationSoundService = notificationSoundService;
        this.log = log;

        chatCaptureService.RawMessageReceived += OnRawMessage;
    }

    private void OnRawMessage(XivChatType chatType, string senderName, string senderKey, string body)
    {
        try
        {
            Handle(chatType, senderName, body);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: whisper notification handling failed");
        }
    }

    private void Handle(XivChatType chatType, string senderName, string body)
    {
        if (!configuration.NotifyOnWhisper || chatType != XivChatType.TellIncoming || string.IsNullOrWhiteSpace(body))
            return;

        var preview = body.Length > PreviewMaxLength ? body[..PreviewMaxLength] + "..." : body;
        var sound = !string.IsNullOrWhiteSpace(configuration.CustomWhisperNotificationSoundPath)
            ? configuration.CustomWhisperNotificationSoundPath
            : notificationSoundService.DefaultWhisperSoundPath;
        notificationService.Show($"{senderName}: {preview}", NotificationSeverity.Info, soundOverridePath: sound);
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
    }
}
