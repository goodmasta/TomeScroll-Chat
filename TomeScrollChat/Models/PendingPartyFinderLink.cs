namespace TomeScrollChat.Models;

/// <summary>A Party Finder listing link queued to be substituted for a "&lt;pflink&gt;" placeholder in
/// the compose box - see <see cref="Services.NativePartyFinderLinkWatcher"/> for how these get queued
/// and <see cref="Services.ChatSendService"/> for how they're actually sent. Uses the game's own raw
/// payload bytes exclusively (see <see cref="RawPayloadBytes"/>) - unlike <see cref="PendingItemLink"/>,
/// there's no manual-reconstruction fallback here: a self-built <c>PartyFinderPayload</c> would need to
/// guess at its <c>PartyFinderLinkType</c>, which isn't recoverable from <c>LinkedPartyFinderId</c>
/// alone, and item links already showed that hand-built payloads risk silently losing the real payload
/// on the round trip through the server/client anyway - not worth repeating that risk here.</summary>
public sealed record PendingPartyFinderLink(ulong ListingId, string LeaderName, byte[]? RawPayloadBytes);
