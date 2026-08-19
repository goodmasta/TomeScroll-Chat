namespace TomeScrollChat.Models;

/// <summary>A quest link queued to be substituted for a "&lt;questlink&gt;" placeholder in the compose
/// box - see <see cref="Services.NativeQuestLinkWatcher"/> for how these get queued and
/// <see cref="Services.ChatSendService"/> for how they're actually sent. Uses the game's own raw
/// payload bytes exclusively (see <see cref="RawPayloadBytes"/>), same reasoning as
/// <see cref="PendingPartyFinderLink"/>: <see cref="PendingItemLink"/>'s own manually-reconstructed
/// fallback (<c>SeStringBuilder.AddItemLink</c>) already showed that a hand-built payload can display
/// correctly but silently lose its rich payload on the round trip through the server/client, becoming
/// plain text - not worth risking that same failure mode for a link type that's easy enough to capture
/// the game's own already-encoded bytes for instead.</summary>
public sealed record PendingQuestLink(uint QuestId, string QuestName, byte[]? RawPayloadBytes);
