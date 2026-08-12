namespace CustomChat.Models;

/// <summary>An item link queued to be appended to the next outgoing message from the compose box -
/// see <see cref="Services.ItemLinkContextMenuService"/> for how these get queued and
/// <see cref="Services.ChatSendService"/> for how they're actually sent.</summary>
public sealed record PendingItemLink(uint ItemId, bool IsHq, string DisplayName);
