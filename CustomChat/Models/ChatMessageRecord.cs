using System;
using System.Collections.Generic;
using Dalamud.Game.Text;

namespace CustomChat.Models;

/// <summary>
/// A captured chat line, already flattened to plain text for storage/rendering. Link spans are
/// re-detected at render time by <see cref="Services.LinkDetector"/> rather than stored, so the
/// detection regex can change without invalidating history.
/// </summary>
public sealed class ChatMessageRecord
{
    /// <summary>SQLite rowid once persisted; 0 for messages not yet written to disk.</summary>
    public long Id { get; set; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public XivChatType ChatType { get; init; }

    public string SenderName { get; init; } = string.Empty;

    /// <summary>"Name@World" when known (players), empty for system/echo/etc.</summary>
    public string SenderKey { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    /// <summary>Which history bucket this belongs to: a tab's <see cref="ChatTabConfig.Id"/> (as string) for
    /// regular tabs, or the whisper partner's "Name@World" key for PM history.</summary>
    public string RoutingKey { get; init; } = string.Empty;

    /// <summary>Map/flag and item links found in <see cref="Body"/> at capture time - see
    /// <see cref="ChatPayloadLink"/> for why this is session-only (empty for anything reloaded from
    /// history).</summary>
    public IReadOnlyList<ChatPayloadLink> PayloadLinks { get; init; } = Array.Empty<ChatPayloadLink>();
}
