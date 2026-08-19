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
/// <para>Passes <see cref="Configuration.CustomWhisperNotificationSoundPath"/> through to
/// <see cref="NotificationService.Show"/> as a sound override, per explicit follow-up user request to
/// tell whispers apart from every other notification by sound alone - falls back to the general
/// notification sound (and from there, the bundled default) when that's empty, same as any other
/// notification.</para>
/// </summary>
public sealed class WhisperNotificationService : IDisposable
{
    /// <summary>Toast text is a single line in a small popup - long messages are cut off rather than
    /// blowing out the notification's size, same idea as <c>GeminiService.Truncate</c> for a log line.</summary>
    private const int PreviewMaxLength = 80;

    private readonly ChatCaptureService chatCaptureService;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;
    private readonly IPluginLog log;

    public WhisperNotificationService(ChatCaptureService chatCaptureService, Configuration configuration, NotificationService notificationService, IPluginLog log)
    {
        this.chatCaptureService = chatCaptureService;
        this.configuration = configuration;
        this.notificationService = notificationService;
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
        notificationService.Show($"{senderName}: {preview}", NotificationSeverity.Info, soundOverridePath: configuration.CustomWhisperNotificationSoundPath);
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
    }
}
