using System;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Reacts to an incoming whisper with a popup toast (<see cref="Configuration.NotifyOnWhisper"/>, off by
/// default), its sound (<see cref="Configuration.WhisperSoundEnabled"/>, on by default), or both -
/// **the two are independent**, per explicit follow-up user request to be able to keep the sound while
/// turning the popup itself off. Deliberately separate from <see cref="AutoReplyService"/> - this is
/// "tell me about it", not "send something back", so it works standalone without opting into auto-reply
/// at all, and listens to the same <see cref="ChatCaptureService.RawMessageReceived"/> event (fires
/// exactly once per real chat event, not once per matching tab) for the same reason
/// <see cref="AutoReplyService"/> does.
///
/// <para>The popup (<see cref="NotificationService.Show"/>, called with <c>playSound: false</c> here)
/// and the sound (<see cref="NotificationSoundService.PlayIfEnabled"/>, called directly) are two
/// completely separate calls, each gated by its own config flag - not one call with the other bolted
/// on, since that would make it impossible to have one without the other. Either way, the sound path
/// resolves the same: <see cref="Configuration.CustomWhisperNotificationSoundPath"/> if set, else
/// <see cref="NotificationSoundService.DefaultWhisperSoundPath"/> - a bundled clip distinct from the
/// plugin's general default (see that class's own doc comment), which itself still falls further back
/// (general custom sound, then the general bundled default, then Windows' own scheme sound) if the
/// resolved path turns out to be missing. The master <see cref="Configuration.NotificationSoundEnabled"/>
/// switch still silences it regardless of <see cref="Configuration.WhisperSoundEnabled"/>.</para>
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

    private void OnRawMessage(XivChatType chatType, string senderName, string senderKey, string body, bool isFromLocalPlayer)
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
        if (chatType != XivChatType.TellIncoming || string.IsNullOrWhiteSpace(body))
            return;

        if (configuration.NotifyOnWhisper)
        {
            var preview = body.Length > PreviewMaxLength ? body[..PreviewMaxLength] + "..." : body;
            notificationService.Show($"{senderName}: {preview}", NotificationSeverity.Info, playSound: false);
        }

        if (configuration.WhisperSoundEnabled)
        {
            var sound = !string.IsNullOrWhiteSpace(configuration.CustomWhisperNotificationSoundPath)
                ? configuration.CustomWhisperNotificationSoundPath
                : notificationSoundService.DefaultWhisperSoundPath;
            notificationSoundService.PlayIfEnabled(sound);
        }
    }

    public void Dispose()
    {
        chatCaptureService.RawMessageReceived -= OnRawMessage;
    }
}
