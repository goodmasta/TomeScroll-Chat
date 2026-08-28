using System;

namespace TomeScrollChat.Models;

/// <summary>One additional BTTV/7TV channel (beyond the always-loaded global sets) whose emotes
/// <see cref="Services.EmoteService"/> also fetches - Settings &gt; Emotes' "Add channel" list.
///
/// <para>Both BTTV's and 7TV's per-channel lookups are keyed by a numeric Twitch user ID, not a
/// channel/login name - there's no name-based endpoint on either provider's own API, and resolving a
/// name to that ID would otherwise need the official Twitch API (its own app registration/Client-ID)
/// or a third-party name-lookup proxy neither provider offers itself. Asking for the ID directly avoids
/// both, per explicit user request - a numeric Twitch ID is discoverable through any of several
/// third-party lookup sites without needing this plugin to depend on one itself.</para></summary>
[Serializable]
public sealed class EmoteChannelConfig
{
    /// <summary>Numeric Twitch user ID (e.g. "121059319") - not a channel/login name.</summary>
    public string TwitchId { get; set; } = string.Empty;

    /// <summary>Free-text reminder of whose channel this is, for the player's own reference in
    /// Settings only - never sent anywhere, never used to look anything up.</summary>
    public string Label { get; set; } = string.Empty;
}
