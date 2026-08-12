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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
