using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Text;
using Newtonsoft.Json;

namespace CustomChat.Models;

/// <summary>
/// A single user-configured tab: an arbitrary set of chat channels, optionally narrowed by a
/// keyword/regex filter, optionally pinned to one whisper partner, optionally popped out into
/// its own floating window.
/// </summary>
[Serializable]
public sealed class ChatTabConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Tab";

    public HashSet<XivChatType> Channels { get; set; } = new();

    /// <summary>True while this tab is shown in a floating window instead of the main window's tab strip.</summary>
    public bool IsDetached { get; set; }

    /// <summary>True for a tab auto-created for one whisper conversation (see <see cref="PmPartnerKey"/>).</summary>
    public bool IsPmTab { get; set; }

    /// <summary>"Name@World" of the whisper partner this tab is pinned to, when <see cref="IsPmTab"/>.</summary>
    public string? PmPartnerKey { get; set; }

    /// <summary>Additional text filter applied on top of channel membership.</summary>
    public ChatTabFilterMode FilterMode { get; set; } = ChatTabFilterMode.None;

    public string FilterPattern { get; set; } = string.Empty;

    /// <summary>Outgoing slash-command prefix used when the player types in this tab (e.g. "/p", "/fc", "/tell Name@World"). Empty = use the game's currently active default channel.</summary>
    public string OutgoingChannelCommand { get; set; } = string.Empty;

    /// <summary>Built-in tabs created on first run (Party/General/FC/Novice/Log) - protects against accidental deletion prompts, not from deletion itself.</summary>
    public bool IsBuiltIn { get; set; }

    public Vector2? DetachedWindowPos { get; set; }

    public Vector2? DetachedWindowSize { get; set; }

    /// <summary>Per-channel colour overrides (ABGR uint as ImGui expects). Empty = use global defaults from <see cref="Configuration"/>.</summary>
    public Dictionary<XivChatType, uint> ColorOverrides { get; set; } = new();

    /// <summary>Emote code (see <see cref="EmoteDefinition"/>) shown as a small icon before this tab's
    /// name in the sidebar. Null = no icon. A real emote image, not literal Unicode text, since
    /// Dalamud's UI font has no colour-emoji glyphs to type into the name field directly.</summary>
    public string? IconEmoji { get; set; }

    /// <summary>Per-tab override for the sidebar blink colour. Null = use <see cref="Configuration.ChannelBlinkColor"/>
    /// (or <see cref="Configuration.WhisperNotifyColor"/> for whisper tabs).</summary>
    public Vector4? BlinkColorOverride { get; set; }

    /// <summary>Per-tab override for the sidebar unread-count colour. Null = use <see cref="Configuration.ChannelUnreadCountColor"/>
    /// (or <see cref="Configuration.WhisperNotifyColor"/> for whisper tabs).</summary>
    public Vector4? UnreadCountColorOverride { get; set; }

    /// <summary>Per-tab override for the sidebar name's resting (non-blinking) colour. Null = plain
    /// white, or - for a whisper tab - the partner's <see cref="Configuration.PlayerTabColors"/> entry
    /// if one's been set (by nickname, so it follows the player even into a whisper tab recreated
    /// later), falling back to white if neither is set.</summary>
    public Vector4? TabColorOverride { get; set; }

    /// <summary>Per-tab override for message body text colour, applied on top of (takes priority
    /// over) the per-channel <see cref="ColorOverrides"/> - a single coarse "make every message in
    /// this tab this colour" knob rather than tuning each channel individually. Null = use the
    /// per-channel colour as before.</summary>
    public Vector4? MessageTextColorOverride { get; set; }

    /// <summary>True for a tab auto-created for one joined linkshell/cross-world linkshell (see
    /// <see cref="Services.LinkshellWatcherService"/>/<see cref="Services.TabManager.SyncAutoLinkshellTabs"/>) -
    /// created the moment membership is detected, removed the moment it isn't (left/kicked), same
    /// self-healing lifecycle as a whisper tab, and only exists at all while
    /// <see cref="Configuration.AutoLinkshellTabs"/> is on.</summary>
    public bool IsAutoLinkshellTab { get; set; }

    /// <summary>True if <see cref="IsAutoLinkshellTab"/> tracks a cross-world linkshell (CWLS1-8)
    /// rather than a regular one (LS1-8) - which of the two 8-slot native lists
    /// <see cref="LinkshellIndex"/> is an index into.</summary>
    public bool IsCrossWorldLinkshell { get; set; }

    /// <summary>0-7 slot index within the native LS1-8/CWLS1-8 list this auto-tab tracks - meaningless
    /// unless <see cref="IsAutoLinkshellTab"/>.</summary>
    public int LinkshellIndex { get; set; }

    /// <summary>Whether new messages make this tab's name blink in the sidebar. Whisper tabs always
    /// blink regardless of this flag (see <see cref="ChatTabConfig.ShouldNotify"/>) - this only
    /// matters for regular tabs, where it's an opt-in setting. Doesn't affect whether the "(N)" unread
    /// count itself is shown at all - see <see cref="MuteUnreadIndicator"/> for that.</summary>
    public bool NotifyOnNewMessage { get; set; }

    /// <summary>Fully mutes this tab's unread indicator in the sidebar - no "(N)" count, no blink,
    /// regardless of <see cref="NotifyOnNewMessage"/>/<see cref="ShouldNotify"/> or whether it's a
    /// whisper tab (which otherwise always notifies). <see cref="UnreadCount"/> itself keeps
    /// incrementing as normal underneath - only the *display* is suppressed, so nothing's lost if
    /// this gets turned back off later.</summary>
    public bool MuteUnreadIndicator { get; set; }

    /// <summary>Auto-requests a translation (into <see cref="Configuration.TranslateTargetLanguage"/>)
    /// for every message drawn in this tab, instead of needing "Translate" picked by hand per message
    /// from the right-click menu - that manual path still exists and still works regardless.
    /// Deliberately only toggleable from Settings > Tabs, not the per-message menu or a sidebar
    /// quick-toggle - a "translate everything" switch is coarse/expensive (kicks off an API call per
    /// message) enough that it shouldn't be one accidental click away.</summary>
    public bool AutoTranslate { get; set; }

    /// <summary>Stops this tab's messages from being persisted to the on-disk SQLite history at all
    /// (see <see cref="Services.ChatCaptureService"/>) - they still show up live for the rest of the
    /// current session (this only gates the database write, not <see cref="Services.TabMessageBuffer"/>/
    /// the UI), they just won't survive a reload/restart and won't be included in "Export to file".
    /// Each tab writes its own independent row per message (even when several tabs match the same raw
    /// chat line), so this only affects this specific tab, not anything else the same message also
    /// routed to. Existing already-saved history isn't touched when this is turned on - only future
    /// messages stop being written.</summary>
    public bool DisableLogging { get; set; }

    /// <summary>Unread counter shown in the sidebar. Persisted so it survives a plugin reload/game
    /// restart - see <see cref="Plugin.Dispose"/> (saved on unload) and the places in
    /// <c>Windows/MainWindow.cs</c> that clear it (saved immediately, since those are infrequent
    /// user actions, unlike the increment on every incoming message, which deliberately does not
    /// save every time to avoid a disk write per chat line).</summary>
    public int UnreadCount { get; set; }

    /// <summary>Whisper tabs always notify; regular tabs only if the user opted in.</summary>
    [JsonIgnore]
    public bool ShouldNotify => IsPmTab || NotifyOnNewMessage;
}
