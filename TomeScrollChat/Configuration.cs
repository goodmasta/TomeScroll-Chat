using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using TomeScrollChat.Models;

namespace TomeScrollChat;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<ChatTabConfig> Tabs { get; set; } = new();

    /// <summary>False = new whisper conversations open as a tab in the main window (default). True = each opens its own floating window.</summary>
    public bool OpenWhispersInSeparateWindow { get; set; }

    /// <summary>Hide the game's own chat log addon(s) while this plugin is active.</summary>
    public bool HideNativeChat { get; set; } = true;

    /// <summary>Redacts player/FC/linkshell names in the chat display for screenshots.</summary>
    public bool ScreenshotMode { get; set; }

    public float FontSize { get; set; } = 14f;

    public bool OpenLinksOnClick { get; set; } = true;

    public bool BttvEnabled { get; set; } = true;

    public bool SevenTvEnabled { get; set; } = true;

    /// <summary>Optional Twitch channel (login name) whose BTTV/7TV channel emotes are loaded in addition to the global sets.</summary>
    public string EmoteTwitchChannel { get; set; } = string.Empty;

    public float EmoteScale { get; set; } = 1.0f;

    /// <summary>Hours a cached emote set/image is considered fresh before being re-fetched.</summary>
    public int EmoteCacheTtlHours { get; set; } = 24;

    /// <summary>Hard cap, in bytes, on total chat history stored on disk (default 1 GiB). Oldest messages are pruned first.</summary>
    public long MaxHistoryBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    private static readonly Vector4 DefaultNotifyRed = new(1f, 0.15f, 0.15f, 1f);

    /// <summary>Sidebar name pulse colour for regular (non-whisper) tabs with new messages - only used
    /// for tabs that opted in via <see cref="ChatTabConfig.NotifyOnNewMessage"/>.</summary>
    public Vector4 ChannelBlinkColor { get; set; } = DefaultNotifyRed;

    /// <summary>Sidebar unread-count colour for regular tabs - independent of the blink colour above.</summary>
    public Vector4 ChannelUnreadCountColor { get; set; } = DefaultNotifyRed;

    /// <summary>Single colour used for both the blink and the unread count on whisper tabs (which
    /// always notify) - one shared setting rather than split blink/count like regular tabs.</summary>
    public Vector4 WhisperNotifyColor { get; set; } = DefaultNotifyRed;

    /// <summary>Per-player sidebar tab colour presets, keyed by "Name@World" - see
    /// <see cref="ChatTabConfig.TabColorOverride"/> for how this combines with a tab's own explicit
    /// override. Settable from Settings > Players or a message's right-click menu ("Set Tab Colour").
    /// User data like <see cref="Tabs"/>, not a preference - deliberately left alone by
    /// <see cref="ResetToDefaults"/>.</summary>
    public Dictionary<string, Vector4> PlayerTabColors { get; set; } = new();

    /// <summary>Per-player nickname colour overrides, keyed by "Name@World" - takes priority over the
    /// auto-generated hash colour from <see cref="Utility.PlayerColorPalette"/>. Settable from
    /// Settings > Players or a message's right-click menu ("Set Message Colour"). User data, not a
    /// preference - deliberately left alone by <see cref="ResetToDefaults"/>.</summary>
    public Dictionary<string, Vector4> PlayerMessageColors { get; set; } = new();

    /// <summary>Whether friends get the emoji marker prefix in chat at all.</summary>
    public bool FriendMarkerEnabled { get; set; } = true;

    /// <summary>Master toggle for <see cref="Services.FriendOnlineWatcherService"/> - pops an in-game
    /// popup (<see cref="Services.NotificationService"/>) when a watched friend (see
    /// <see cref="FriendOnlineNotifyAll"/>/<see cref="FriendOnlineNotifyKeys"/>) comes online or goes
    /// offline. Also what gates the auto-open/close of the native Friend List window on login (see
    /// that service's own doc comment for why) - off by default, since that's a real (if brief) native
    /// UI flash the player should opt into, not something that just starts happening.</summary>
    public bool FriendOnlineNotifyEnabled { get; set; }

    /// <summary>When on, every current friend is watched (see <see cref="FriendOnlineNotifyEnabled"/>)
    /// regardless of <see cref="FriendOnlineNotifyKeys"/> - "Select All" in Settings > Players sets
    /// this rather than snapshotting every current friend's key into that set, so a friend added later
    /// is automatically included too.</summary>
    public bool FriendOnlineNotifyAll { get; set; }

    /// <summary>Specific friends to watch (see <see cref="FriendOnlineNotifyEnabled"/>), keyed
    /// "Name@World" same as <see cref="PlayerTabColors"/> - only consulted when
    /// <see cref="FriendOnlineNotifyAll"/> is off. User data, not a preference - deliberately left
    /// alone by <see cref="ResetToDefaults"/>, same reasoning as <see cref="PlayerTabColors"/>.</summary>
    public HashSet<string> FriendOnlineNotifyKeys { get; set; } = new();

    /// <summary>A loaded BTTV/7TV/standard emote *code* (not a literal character) drawn as an actual
    /// image before the name of any sender who's on the friends list - picked from the same emote
    /// picker used for the chat input (see <see cref="Windows.ConfigWindow"/>). Rendered as a real
    /// image via <see cref="Services.EmoteService"/> rather than a Unicode glyph: Dalamud's UI font
    /// doesn't have glyphs for most emoji pictographs (confirmed - they rendered as a fallback "="
    /// glyph in testing), and even a font that did wouldn't render multi-colour emoji correctly since
    /// ImGui's text rendering is single-colour per glyph. Empty = no marker shown at all, regardless of
    /// <see cref="FriendMarkerEnabled"/>. Defaults to <c>"star"</c> (a built-in standard-emoji code, see
    /// <see cref="Services.Emotes.StandardEmojiCatalog"/> - decided while going through every setting's
    /// default with the user, 2026-08-17) so the marker actually shows out of the box rather than being
    /// enabled but invisible until the player picks one themselves.</summary>
    public string FriendMarkerEmoji { get; set; } = "star";

    /// <summary>ISO 639-1 code (see <see cref="Utility.TranslationLanguageCatalog"/>) messages are
    /// translated into via the "Translate" context-menu item - the source language is always
    /// auto-detected per message, only the target is configurable.</summary>
    public string TranslateTargetLanguage { get; set; } = "en";

    /// <summary>Which backend <see cref="Services.TranslationService"/> uses - see
    /// <see cref="Models.TranslationEngine"/> for what each option means.</summary>
    public TranslationEngine TranslationEngine { get; set; } = TranslationEngine.GoogleFree;

    /// <summary>Google Gemini API key (Settings > General) - required for
    /// <see cref="Models.TranslationEngine.Gemini"/> and any future AI-backed feature built on
    /// <see cref="Services.GeminiService"/>. Deliberately excluded from <see cref="ResetToDefaults"/> -
    /// a credential the user typed in, not a preference to silently wipe on a settings reset.</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>Gemini model id used for every <see cref="Services.GeminiService"/> call - see
    /// <see cref="Services.GeminiService.DefaultModel"/> for the built-in default. A plain editable
    /// string rather than a hardcoded choice, since Google's current model lineup will keep moving on.</summary>
    public string GeminiModel { get; set; } = Services.GeminiService.DefaultModel;

    /// <summary>Instruction text sent to Gemini ahead of the message being replied to and (if
    /// <see cref="AiReplyMemoryEnabled"/>) the remembered exchange history - see
    /// <see cref="Services.AiReplyService"/>. Editable in Settings so the player can steer tone/persona
    /// without a code change.</summary>
    public string AiReplyPrompt { get; set; } = Services.AiReplyService.DefaultPrompt;

    /// <summary>Whether <see cref="Services.AiReplyService"/> includes its remembered past (original
    /// message, generated reply) pairs as extra context on every new generation - added per explicit
    /// user request ("нейронка должна запоминать историю моих прошлых запросов на генерацию ответа и
    /// учитывать её в генерации новых ответов"), so replies stay consistent with earlier ones instead
    /// of each generation starting from a blank slate. Defaults to off (decided while going through
    /// every setting's default with the user, 2026-08-17) - an explicit opt-in for the extra context/
    /// prompt size, not assumed wanted from the start.</summary>
    public bool AiReplyMemoryEnabled { get; set; } = false;

    /// <summary>How many of the most recent (original message, generated reply) pairs
    /// <see cref="Services.AiReplyService"/> keeps and feeds back as context - older entries are
    /// dropped once this cap is hit. Kept modest by default since every remembered pair adds to the
    /// prompt sent on *every* future generation.</summary>
    public int AiReplyMemoryLimit { get; set; } = 10;

    /// <summary>Master switch for <see cref="Services.AutoReplyService"/> - off by default (unlike
    /// most toggles in this plugin) since it *sends real messages on the player's behalf without
    /// per-message confirmation*, the highest-risk feature in this plugin so far. Configured via the
    /// main window's title bar robot-icon button, not buried in Settings, per explicit user request.</summary>
    public bool AutoReplyEnabled { get; set; } = false;

    /// <summary>Default for <see cref="AutoReplyMessage"/> - exposed as its own constant (rather than
    /// just the property initializer) so the "Reset to default" button in the auto-reply popup
    /// (<c>Windows.MainWindow.DrawAutoReplyPopup</c>) has something to reset back to.</summary>
    public const string DefaultAutoReplyMessage = "Busy IRL, I'll reply as soon as I can.";

    /// <summary>The fixed text sent back automatically - not AI-generated, a plain preset string the
    /// player writes themselves (an "away message", same concept as classic IM auto-responders).</summary>
    public string AutoReplyMessage { get; set; } = DefaultAutoReplyMessage;

    /// <summary>Whether an incoming whisper triggers an auto-reply.</summary>
    public bool AutoReplyToWhispers { get; set; } = true;

    /// <summary>Whether the player's own name being mentioned in one of
    /// <see cref="Services.AutoReplyService.MentionChannels"/> (Say/Yell/Shout/Party/FC/Alliance/
    /// Linkshells - "по аналогии с тегом", the same name-mention detection <c>ChatMessageRenderer</c>'s
    /// own highlight already uses) triggers an auto-reply, sent as a whisper to whoever mentioned them
    /// (never posted back into the public channel itself - far less spammy/disruptive). Defaults to
    /// off (decided while going through every setting's default with the user, 2026-08-17) - only
    /// whispers trigger an auto-reply out of the box; public/group-channel mentions are a much higher-
    /// volume, less-certain-it's-really-for-you trigger, left as an explicit opt-in.</summary>
    public bool AutoReplyToMentions { get; set; } = false;

    /// <summary>Minimum minutes between two auto-replies to the *same* sender - the main defence
    /// against a runaway back-and-forth if the other side also has some kind of auto-reply/bot (without
    /// this, two such accounts could in principle keep replying to each other indefinitely). A second,
    /// fixed (not user-configurable) global minimum gap between *any* two auto-sends also exists in
    /// <see cref="Services.AutoReplyService"/> itself, protecting against the game's own chat-spam
    /// guard when many *different* senders trigger this in a short window.</summary>
    public int AutoReplyCooldownMinutes { get; set; } = 5;

    /// <summary>Fades the main chat window and any popped-out tab windows to
    /// <see cref="InactiveWindowAlpha"/> while they don't have keyboard focus - same idea as the
    /// game's own native chat log fading out when you're not actively looking at it.</summary>
    public bool FadeWindowWhenInactive { get; set; } = true;

    /// <summary>Background opacity (0 = invisible, 1 = fully opaque) applied while unfocused, when
    /// <see cref="FadeWindowWhenInactive"/> is on. Only the window's background panel fades - message
    /// text/images stay fully visible, so the chat is still readable while faded.</summary>
    public float InactiveWindowAlpha { get; set; } = 0.35f;

    /// <summary>Fully hides the main chat window and any popped-out tab windows (not drawn at all,
    /// unlike <see cref="FadeWindowWhenInactive"/> which just dims the background) while a cutscene
    /// is playing.</summary>
    public bool HideChatDuringCutscenes { get; set; } = true;

    /// <summary>Pops an in-game popup toast (<see cref="Services.NotificationService"/>) whenever the
    /// game reports a slash command you typed doesn't exist - easy to miss otherwise, especially with
    /// <see cref="HideNativeChat"/> on and no tab showing the "Error" channel. See
    /// <see cref="Services.ChatCaptureService.LooksLikeInvalidCommandError"/> for how this is
    /// detected (a text match, English clients only).</summary>
    public bool NotifyOnInvalidCommand { get; set; } = true;

    /// <summary>Whether <see cref="Services.NotificationService.Show"/> plays a short sound alongside
    /// every popup toast, via <see cref="Services.NotificationSoundService"/> - on by default per
    /// explicit user request. Turning this off silences the sound entirely without affecting the popups
    /// themselves.</summary>
    public bool NotificationSoundEnabled { get; set; } = true;

    /// <summary>Path to a user-picked <c>.wav</c> file to play instead of the plugin's own bundled
    /// default alert sound (empty here, hence that default - see
    /// <see cref="Services.NotificationSoundService"/> for what that default is and why WAV-only rather
    /// than "any" format for this custom slot). Not cleared/validated here when the file goes missing -
    /// <see cref="Services.NotificationSoundService"/> itself just falls back to the default sound for
    /// that one play rather than erroring or silently resetting this setting.</summary>
    public string CustomNotificationSoundPath { get; set; } = string.Empty;

    /// <summary>Auto-creates one tab per joined linkshell/cross-world linkshell (see
    /// <see cref="Services.LinkshellWatcherService"/>), removed the moment you leave/get kicked from
    /// one. Turning this off removes every such tab immediately (see
    /// <see cref="Services.TabManager.RemoveAllAutoLinkshellTabs"/>) - manually created linkshell tabs
    /// (added the normal way, via "Add tab") are untouched either way, only ones this feature itself
    /// created.</summary>
    public bool AutoLinkshellTabs { get; set; } = true;

    /// <summary>Shows an eye icon in the main chat window's title bar that collapses it down to just
    /// the title bar (stronger than <see cref="FadeWindowWhenInactive"/>'s dimming - the body isn't
    /// drawn at all). The button itself stays visible while collapsed, so clicking it again is always
    /// how to bring the chat back - see <see cref="Windows.MainWindow"/>.</summary>
    public bool ShowHideChatButton { get; set; } = true;

    /// <summary>Off by default (explicit opt-in) - automatically triggers the same collapse the eye
    /// button does after the main chat window has gone <see cref="AutoHideChatSeconds"/> without being
    /// focused. Only the eye button (not merely refocusing the window) un-collapses it again - see
    /// <see cref="Windows.MainWindow.PreDraw"/>.</summary>
    public bool AutoHideChatWhenInactive { get; set; }

    /// <summary>Seconds of the main chat window not being focused before <see cref="AutoHideChatWhenInactive"/>
    /// triggers.</summary>
    public float AutoHideChatSeconds { get; set; } = 60f;

    /// <summary>Turns on <see cref="Services.DialogueTranslationService"/>/<see cref="Windows.DialogueTranslationWindow"/> -
    /// a separate window showing nothing but translated NPC dialogue, cutscene subtitles, and quest
    /// toasts (see <see cref="Services.DialogueTranslationService"/>'s own doc comment for the exact
    /// sources), translated into <see cref="TranslateTargetLanguage"/> via whichever engine
    /// <see cref="TranslationEngine"/>/<see cref="Services.TranslationService.ActiveEngine"/> already
    /// resolves to - no separate engine choice for this feature.</summary>
    public bool EnableDialogueTranslationWindow { get; set; }

    /// <summary>Auto-collapses <see cref="Windows.DialogueTranslationWindow"/> (not drawn at all, same
    /// as <see cref="HideChatDuringCutscenes"/>'s effect on the main chat) once
    /// <see cref="DialogueTranslationAutoHideSeconds"/> pass without a new translated line - reappears
    /// immediately the moment the next one arrives.</summary>
    public bool DialogueTranslationAutoHide { get; set; } = true;

    /// <summary>Seconds of no new translated dialogue line before <see cref="DialogueTranslationAutoHide"/>
    /// hides the window.</summary>
    public float DialogueTranslationAutoHideSeconds { get; set; } = 30f;

    /// <summary>Resets every *setting* below to its default value - deliberately leaves
    /// <see cref="Tabs"/> itself alone, that's a separate step
    /// (<see cref="Services.TabManager.ResetToDefaults"/>, called alongside this one by
    /// <see cref="Plugin.ResetSettingsToDefaults"/>) since resetting tab *existence* has to go through
    /// <see cref="Services.TabManager"/> to fire its <c>TabRemoved</c>/<c>TabAdded</c> events correctly
    /// (closing any open detached tab window along the way) - a plain <see cref="Configuration"/>
    /// method has no way to do that itself. Doesn't call <see cref="Save"/> itself, and some of these
    /// have side effects elsewhere that need reapplying - see <see cref="Plugin.ResetSettingsToDefaults"/>.
    /// Field list has to be kept in sync by hand whenever a new setting is added.</summary>
    public void ResetToDefaults()
    {
        var defaults = new Configuration();

        OpenWhispersInSeparateWindow = defaults.OpenWhispersInSeparateWindow;
        HideNativeChat = defaults.HideNativeChat;
        ScreenshotMode = defaults.ScreenshotMode;
        FontSize = defaults.FontSize;
        OpenLinksOnClick = defaults.OpenLinksOnClick;
        BttvEnabled = defaults.BttvEnabled;
        SevenTvEnabled = defaults.SevenTvEnabled;
        EmoteTwitchChannel = defaults.EmoteTwitchChannel;
        EmoteScale = defaults.EmoteScale;
        EmoteCacheTtlHours = defaults.EmoteCacheTtlHours;
        MaxHistoryBytes = defaults.MaxHistoryBytes;
        ChannelBlinkColor = defaults.ChannelBlinkColor;
        ChannelUnreadCountColor = defaults.ChannelUnreadCountColor;
        WhisperNotifyColor = defaults.WhisperNotifyColor;
        FriendMarkerEnabled = defaults.FriendMarkerEnabled;
        FriendOnlineNotifyEnabled = defaults.FriendOnlineNotifyEnabled;
        // Fixed 2026-08-17 (found during a full-codebase cleanup audit): this was the one property
        // missing from this list - "watch all friends" stayed stuck at whatever it was before a
        // settings reset instead of returning to its false default, unlike every other preference here.
        FriendOnlineNotifyAll = defaults.FriendOnlineNotifyAll;
        FriendMarkerEmoji = defaults.FriendMarkerEmoji;
        TranslateTargetLanguage = defaults.TranslateTargetLanguage;
        TranslationEngine = defaults.TranslationEngine;
        GeminiModel = defaults.GeminiModel;
        AiReplyPrompt = defaults.AiReplyPrompt;
        AiReplyMemoryEnabled = defaults.AiReplyMemoryEnabled;
        AiReplyMemoryLimit = defaults.AiReplyMemoryLimit;
        AutoReplyEnabled = defaults.AutoReplyEnabled;
        AutoReplyMessage = defaults.AutoReplyMessage;
        AutoReplyToWhispers = defaults.AutoReplyToWhispers;
        AutoReplyToMentions = defaults.AutoReplyToMentions;
        AutoReplyCooldownMinutes = defaults.AutoReplyCooldownMinutes;
        FadeWindowWhenInactive = defaults.FadeWindowWhenInactive;
        InactiveWindowAlpha = defaults.InactiveWindowAlpha;
        HideChatDuringCutscenes = defaults.HideChatDuringCutscenes;
        NotifyOnInvalidCommand = defaults.NotifyOnInvalidCommand;
        NotificationSoundEnabled = defaults.NotificationSoundEnabled;
        CustomNotificationSoundPath = defaults.CustomNotificationSoundPath;
        AutoLinkshellTabs = defaults.AutoLinkshellTabs;
        ShowHideChatButton = defaults.ShowHideChatButton;
        AutoHideChatWhenInactive = defaults.AutoHideChatWhenInactive;
        AutoHideChatSeconds = defaults.AutoHideChatSeconds;
        EnableDialogueTranslationWindow = defaults.EnableDialogueTranslationWindow;
        DialogueTranslationAutoHide = defaults.DialogueTranslationAutoHide;
        DialogueTranslationAutoHideSeconds = defaults.DialogueTranslationAutoHideSeconds;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
