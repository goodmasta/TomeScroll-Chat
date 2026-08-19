using System;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace TomeScrollChat.Services;

/// <summary>
/// Plays a short sound whenever <see cref="NotificationService.Show"/> shows a popup toast (Settings >
/// Notifications - <see cref="Configuration.NotificationSoundEnabled"/>, on by default) - added per
/// explicit user request. Uses <c>winmm.dll</c>'s <c>PlaySound</c> directly via P/Invoke rather than
/// pulling in an audio-library NuGet dependency (NAudio, etc.) just for a single short blip - the same
/// Win32 API Windows itself has used to play system/scheme sounds and short WAV clips for decades, so
/// it's always present and needs nothing bundled or installed.
///
/// <para>With no custom sound configured, the "standard" sound is Windows' own built-in "SystemAsterisk"
/// scheme sound - a named alias <c>PlaySound</c> resolves against whatever sound scheme the player has
/// configured in Windows itself (Settings > Sound > More sound settings), so it always exists with
/// nothing shipped by this plugin, and even matches the player's own OS-level customization if they've
/// already changed it there. Only a user-provided file (<see cref="Configuration.CustomNotificationSoundPath"/>)
/// ever overrides that.</para>
///
/// <para><b>WAV only</b>, not "any" format as initially asked for - <c>PlaySound</c> itself only ever
/// understood uncompressed WAV; supporting compressed formats (mp3, ogg) would need a real decoding
/// library. This matches Windows' own custom-notification-sound picker (Settings > Sound), which has the
/// same restriction, so it's a familiar constraint rather than an unusual one.</para>
/// </summary>
public sealed class NotificationSoundService
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ALIAS = 0x00010000;

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? soundName, IntPtr hmod, uint flags);

    private readonly Configuration configuration;
    private readonly IPluginLog log;

    public NotificationSoundService(Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
    }

    /// <summary>Called from <see cref="NotificationService.Show"/> - a no-op while
    /// <see cref="Configuration.NotificationSoundEnabled"/> is off.</summary>
    public void PlayIfEnabled()
    {
        if (configuration.NotificationSoundEnabled)
            Play();
    }

    /// <summary>Settings' "Test sound" button - always plays the currently-configured sound (default or
    /// custom), regardless of <see cref="Configuration.NotificationSoundEnabled"/>, so the player can
    /// preview a file before actually turning the feature on.</summary>
    public void PlayPreview() => Play();

    private void Play()
    {
        try
        {
            var customPath = configuration.CustomNotificationSoundPath;
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                if (File.Exists(customPath))
                {
                    PlaySound(customPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                    return;
                }

                log.Warning("TomeScrollChat: custom notification sound not found ({Path}) - falling back to the standard sound", customPath);
            }

            PlaySound("SystemAsterisk", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to play notification sound");
        }
    }
}
