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

    /// <summary>Whether new messages make this tab blink and show a red unread count in the sidebar.
    /// Whisper tabs always behave this way regardless of this flag (see
    /// <see cref="ChatTabConfig.ShouldNotify"/>) - this only matters for regular tabs, where it's an
    /// opt-in setting.</summary>
    public bool NotifyOnNewMessage { get; set; }

    /// <summary>Session-only unread counter, shown in the sidebar - never persisted.</summary>
    [JsonIgnore]
    public int UnreadCount { get; set; }

    /// <summary>Whisper tabs always notify; regular tabs only if the user opted in.</summary>
    [JsonIgnore]
    public bool ShouldNotify => IsPmTab || NotifyOnNewMessage;
}
