using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using CustomChat.Models;

namespace CustomChat;

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

    /// <summary>A loaded BTTV/7TV emote *code* (not a literal character) drawn as an actual image
    /// before the name of any sender who's on the friends list - picked from the same emote picker
    /// used for the chat input (see <see cref="Windows.ConfigWindow"/>). Rendered as a real image via
    /// <see cref="Services.EmoteService"/> rather than a Unicode glyph: Dalamud's UI font doesn't have
    /// glyphs for most emoji pictographs (confirmed - they rendered as a fallback "=" glyph in
    /// testing), and even a font that did wouldn't render multi-colour emoji correctly since ImGui's
    /// text rendering is single-colour per glyph. Empty = no emote picked yet / marker not shown.</summary>
    public string FriendMarkerEmoji { get; set; } = string.Empty;

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

    /// <summary>Resets every *setting* below to its default value - deliberately leaves
    /// <see cref="Tabs"/> itself alone (channels, filters, icons, name - the user's own custom tab
    /// setup, not a preference to reset). Per-tab *colour* overrides are still cleared, just via the
    /// separate <see cref="ResetTabColors"/> (see <see cref="Plugin.ResetSettingsToDefaults"/>, which
    /// calls both) - colours were explicitly requested to reset along with everything else, unlike a
    /// tab's actual existence/channel setup. Doesn't call <see cref="Save"/> itself, and some of these
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
        FriendMarkerEmoji = defaults.FriendMarkerEmoji;
        TranslateTargetLanguage = defaults.TranslateTargetLanguage;
        TranslationEngine = defaults.TranslationEngine;
        GeminiModel = defaults.GeminiModel;
        FadeWindowWhenInactive = defaults.FadeWindowWhenInactive;
        InactiveWindowAlpha = defaults.InactiveWindowAlpha;
        HideChatDuringCutscenes = defaults.HideChatDuringCutscenes;
        NotifyOnInvalidCommand = defaults.NotifyOnInvalidCommand;
        AutoLinkshellTabs = defaults.AutoLinkshellTabs;
        ShowHideChatButton = defaults.ShowHideChatButton;
        AutoHideChatWhenInactive = defaults.AutoHideChatWhenInactive;
        AutoHideChatSeconds = defaults.AutoHideChatSeconds;
    }

    /// <summary>Clears every per-tab colour override (<see cref="ChatTabConfig.ColorOverrides"/>,
    /// <see cref="ChatTabConfig.TabColorOverride"/>, <see cref="ChatTabConfig.BlinkColorOverride"/>,
    /// <see cref="ChatTabConfig.UnreadCountColorOverride"/>,
    /// <see cref="ChatTabConfig.MessageTextColorOverride"/>) on every tab, back to falling through to
    /// the global defaults above - unlike <see cref="ResetToDefaults"/>, this only touches colours,
    /// not the tabs' channels/filters/name/existence. Split out separately since it iterates
    /// <see cref="Tabs"/>, which <see cref="ResetToDefaults"/> otherwise never touches.</summary>
    public void ResetTabColors()
    {
        foreach (var tab in Tabs)
        {
            tab.ColorOverrides.Clear();
            tab.TabColorOverride = null;
            tab.BlinkColorOverride = null;
            tab.UnreadCountColorOverride = null;
            tab.MessageTextColorOverride = null;
        }
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
