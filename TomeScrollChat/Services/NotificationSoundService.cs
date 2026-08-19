using System;
using System.IO;
using System.Reflection;
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
/// <para>With no custom sound configured, the "standard" sound is a short alert clip bundled with the
/// plugin itself (embedded resource, see the csproj - originally supplied as <c>freealert.mp3</c>,
/// transcoded to PCM WAV since <c>PlaySound</c> can't decode mp3) rather than a generic Windows system
/// sound, per explicit user request to use that specific clip as the preset. Extracted to
/// <see cref="defaultSoundPath"/> (inside the plugin's own config directory) once per construction,
/// since <c>PlaySound</c> needs a real file path, not an in-memory stream - always re-extracted rather
/// than only-if-missing, so an updated bundled clip in a future build can't be shadowed by a stale copy
/// left over from an older one. If extraction ever fails (disk full, permissions, etc.), Windows' own
/// built-in "SystemAsterisk" scheme sound is the last-resort fallback, so a real machine/disk problem
/// still ends up with *something* audible rather than silence. Only a user-provided file
/// (<see cref="Configuration.CustomNotificationSoundPath"/>) ever overrides the bundled default.</para>
///
/// <para><b>WAV only</b> for the *custom* sound slot, not "any" format as initially asked for -
/// <c>PlaySound</c> itself only ever understood uncompressed WAV; supporting compressed formats (mp3,
/// ogg) as a *custom* pick would need a real decoding library. This matches Windows' own
/// custom-notification-sound picker (Settings > Sound), which has the same restriction, so it's a
/// familiar constraint rather than an unusual one. The bundled default itself started as an mp3 but
/// that conversion happened once, ahead of time, at build-authoring time - not something this service
/// does at runtime.</para>
/// </summary>
public sealed class NotificationSoundService
{
    private const string DefaultSoundResourceName = "TomeScrollChat.Assets.DefaultNotification.wav";

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ALIAS = 0x00010000;

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? soundName, IntPtr hmod, uint flags);

    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly string defaultSoundPath;

    public NotificationSoundService(string configDirectory, Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        defaultSoundPath = Path.Combine(configDirectory, "default-notification-sound.wav");
        ExtractDefaultSound();
    }

    private void ExtractDefaultSound()
    {
        try
        {
            using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultSoundResourceName);
            if (resourceStream == null)
            {
                log.Warning("TomeScrollChat: embedded default notification sound resource not found ({Name})", DefaultSoundResourceName);
                return;
            }

            using var fileStream = File.Create(defaultSoundPath);
            resourceStream.CopyTo(fileStream);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to extract the default notification sound");
        }
    }

    /// <summary>Called from <see cref="NotificationService.Show"/> - a no-op while
    /// <see cref="Configuration.NotificationSoundEnabled"/> is off. <paramref name="overridePath"/>
    /// (e.g. <see cref="Configuration.CustomWhisperNotificationSoundPath"/>, from
    /// <see cref="WhisperNotificationService"/>) takes priority over
    /// <see cref="Configuration.CustomNotificationSoundPath"/> when set - lets one specific kind of
    /// notification (whispers, per explicit user request) sound different from every other one, while
    /// everything else still shares the one general sound.</summary>
    public void PlayIfEnabled(string? overridePath = null)
    {
        if (configuration.NotificationSoundEnabled)
            Play(overridePath);
    }

    /// <summary>Settings' "Test sound" buttons - always plays the given (or general, if
    /// <paramref name="overridePath"/> is null) sound regardless of
    /// <see cref="Configuration.NotificationSoundEnabled"/>, so the player can preview a file before
    /// actually turning the feature on.</summary>
    public void PlayPreview(string? overridePath = null) => Play(overridePath);

    private void Play(string? overridePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (File.Exists(overridePath))
                {
                    PlaySound(overridePath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                    return;
                }

                log.Warning("TomeScrollChat: override notification sound not found ({Path}) - falling back to the general sound", overridePath);
            }

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

            if (File.Exists(defaultSoundPath))
            {
                PlaySound(defaultSoundPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                return;
            }

            // Bundled default failed to extract for some reason - Windows' own scheme sound as a
            // last-resort fallback, so there's still something audible rather than total silence.
            PlaySound("SystemAsterisk", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to play notification sound");
        }
    }
}
