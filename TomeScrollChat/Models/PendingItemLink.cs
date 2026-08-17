namespace TomeScrollChat.Models;

/// <summary>An item link queued to be substituted for a "&lt;link&gt;" placeholder in the compose box -
/// see <see cref="Services.NativeItemLinkWatcher"/> for how these get queued and
/// <see cref="Services.ChatSendService"/> for how they're actually sent.</summary>
/// <param name="RawPayloadBytes">The raw encoded SE payload bytes captured straight from the game's
/// own native "Link" action (see <see cref="Services.NativeItemLinkWatcher"/>), used as-is when
/// present - this is what actually makes the link survive being sent (a manually reconstructed
/// payload via <c>SeStringBuilder.AddItemLink</c> didn't: it displayed correctly but silently lost
/// its <c>ItemPayload</c> on the round trip through the server/client, becoming plain text). Null
/// falls back to that manual reconstruction, for whatever case failed to capture the native bytes.</param>
public sealed record PendingItemLink(uint ItemId, bool IsHq, string DisplayName, byte[]? RawPayloadBytes = null);
