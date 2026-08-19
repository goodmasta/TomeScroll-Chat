using System;
using System.Collections.Generic;
using Dalamud.Game.Text;

namespace TomeScrollChat.Models;

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

    /// <summary>Whether this message's sender is the local player, per Dalamud's own
    /// <see cref="Dalamud.Game.Chat.IChatMessage.SourceKind"/> (<c>XivChatRelationKind.LocalPlayer</c>) -
    /// see <see cref="Services.ChatCaptureService"/> for where this is set. The authoritative signal
    /// <see cref="Windows.ChatMessageRenderer"/> prefers for showing "You" instead of the sender's name;
    /// <see cref="SenderName"/>/<see cref="SenderKey"/> string-matching is kept only as a fallback,
    /// since the game doesn't consistently embed a resolvable <c>PlayerPayload</c>/exact-matching name
    /// for the local player's own messages across every channel (confirmed live to silently fail for
    /// Party chat specifically - not reliable enough to depend on alone).</summary>
    public bool IsFromLocalPlayer { get; init; }

    public string Body { get; init; } = string.Empty;

    /// <summary>Which history bucket this belongs to: a tab's <see cref="ChatTabConfig.Id"/> (as string) for
    /// regular tabs, or the whisper partner's "Name@World" key for PM history.</summary>
    public string RoutingKey { get; init; } = string.Empty;

    /// <summary>Map/flag and item links found in <see cref="Body"/> at capture time - see
    /// <see cref="ChatPayloadLink"/>. Persisted to (and restored from) history as of 2026-08-13, so
    /// links stay clickable across a plugin restart, not just for the session that received them.</summary>
    public IReadOnlyList<ChatPayloadLink> PayloadLinks { get; init; } = Array.Empty<ChatPayloadLink>();

    /// <summary>Translation already saved to history for this exact message, if any - set only when
    /// loaded from disk (see <see cref="Services.ChatHistoryService.LoadRecent"/>/
    /// <see cref="Services.ChatHistoryService.SaveTranslation"/>), never for a freshly-captured live
    /// message. <see cref="Services.TranslationService"/> treats this as a cache hit, avoiding a
    /// repeat network request for a message that was already translated in an earlier session.</summary>
    public string? PersistedTranslation { get; init; }

    /// <summary>Target language <see cref="PersistedTranslation"/> was translated into - compared
    /// against the currently-configured target language before trusting the persisted value, so
    /// changing <see cref="Configuration.TranslateTargetLanguage"/> doesn't silently show a
    /// translation into the *previous* target language.</summary>
    public string? PersistedTranslationLanguage { get; init; }
}
