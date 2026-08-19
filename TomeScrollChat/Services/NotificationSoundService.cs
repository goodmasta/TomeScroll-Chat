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
/// <para>Two bundled default clips ship as embedded resources (see the csproj), each originally
/// supplied as an mp3 and transcoded to PCM WAV since <c>PlaySound</c> can't decode mp3 - the general
/// default (<see cref="DefaultSoundPath"/>, from <c>freealert.mp3</c>) and a second one specifically for
/// whispers (<see cref="DefaultWhisperSoundPath"/>, from <c>freelaert2.mp3</c>), per explicit follow-up
/// user request to tell whispers apart from every other notification by sound alone even with nothing
/// custom configured. Both are extracted to the plugin's own config directory once per construction,
/// since <c>PlaySound</c> needs a real file path, not an in-memory stream - always re-extracted rather
/// than only-if-missing, so an updated bundled clip in a future build can't be shadowed by a stale copy
/// left over from an older one. <see cref="WhisperNotificationService"/> resolves which single path to
/// actually use (its own custom pick, else <see cref="DefaultWhisperSoundPath"/>) and passes that
/// through <see cref="NotificationService.Show"/> as the sound override - this service itself only ever
/// sees one effective override path per call, with its own further fallback (general custom sound, then
/// <see cref="DefaultSoundPath"/>, then Windows' own built-in "SystemAsterisk" scheme sound as the
/// final last-resort) applying if that override turns out to be missing.</para>
///
/// <para><b>WAV only</b> for the *custom* sound slots, not "any" format as initially asked for -
/// <c>PlaySound</c> itself only ever understood uncompressed WAV; supporting compressed formats (mp3,
/// ogg) as a *custom* pick would need a real decoding library. This matches Windows' own
/// custom-notification-sound picker (Settings > Sound), which has the same restriction, so it's a
/// familiar constraint rather than an unusual one. The bundled defaults themselves started as mp3s but
/// that conversion happened once, ahead of time, at build-authoring time - not something this service
/// does at runtime.</para>
/// </summary>
public sealed class NotificationSoundService
{
    private const string DefaultSoundResourceName = "TomeScrollChat.Assets.DefaultNotification.wav";
    private const string DefaultWhisperSoundResourceName = "TomeScrollChat.Assets.DefaultWhisperNotification.wav";

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ALIAS = 0x00010000;

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? soundName, IntPtr hmod, uint flags);

    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly string defaultSoundPath;
    private readonly string defaultWhisperSoundPath;

    /// <summary>The plugin's own bundled general-notification default (see the class doc comment) -
    /// exposed so Settings can show/preview it and <see cref="Play"/> can fall back to it.</summary>
    public string DefaultSoundPath => defaultSoundPath;

    /// <summary>The plugin's own bundled whisper-specific default - exposed so
    /// <see cref="WhisperNotificationService"/> can use it when no custom whisper sound is set, and
    /// Settings can show/preview it directly.</summary>
    public string DefaultWhisperSoundPath => defaultWhisperSoundPath;

    public NotificationSoundService(string configDirectory, Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        defaultSoundPath = Path.Combine(configDirectory, "default-notification-sound.wav");
        defaultWhisperSoundPath = Path.Combine(configDirectory, "default-whisper-notification-sound.wav");
        ExtractEmbeddedSound(DefaultSoundResourceName, defaultSoundPath);
        ExtractEmbeddedSound(DefaultWhisperSoundResourceName, defaultWhisperSoundPath);
    }

    private void ExtractEmbeddedSound(string resourceName, string destinationPath)
    {
        try
        {
            using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                log.Warning("TomeScrollChat: embedded notification sound resource not found ({Name})", resourceName);
                return;
            }

            using var fileStream = File.Create(destinationPath);
            resourceStream.CopyTo(fileStream);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to extract embedded notification sound ({Name})", resourceName);
        }
    }

    /// <summary>Called from <see cref="NotificationService.Show"/> - a no-op while
    /// <see cref="Configuration.NotificationSoundEnabled"/> is off. <paramref name="overridePath"/>
    /// (e.g. <see cref="WhisperNotificationService"/>'s resolved custom-or-bundled-whisper-default)
    /// takes priority over <see cref="Configuration.CustomNotificationSoundPath"/> when set/valid - lets
    /// one specific kind of notification sound different from every other one, while everything else
    /// still shares the one general sound.</summary>
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
            if (TryPlayFile(overridePath))
                return;

            if (TryPlayFile(configuration.CustomNotificationSoundPath))
                return;

            if (TryPlayFile(defaultSoundPath))
                return;

            // Every bundled/custom option failed (extraction problem, disk/permissions issue, etc.) -
            // Windows' own scheme sound as the final fallback, so there's still something audible.
            PlaySound("SystemAsterisk", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to play notification sound");
        }
    }

    /// <summary>Plays <paramref name="path"/> if it's set and actually exists, returning whether it did -
    /// callers chain this to fall through their own list of candidates in priority order.</summary>
    private bool TryPlayFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!File.Exists(path))
        {
            log.Warning("TomeScrollChat: notification sound not found ({Path}) - trying the next fallback", path);
            return false;
        }

        PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        return true;
    }
}
