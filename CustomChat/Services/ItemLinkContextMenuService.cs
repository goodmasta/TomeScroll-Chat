using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Adds a "Link (Custom Chat)" entry to the inventory right-click menu (bags, retainer, loot list,
/// etc. - anywhere Dalamud reports <see cref="ContextMenuType.Inventory"/>) so item links keep
/// working while the native chat log is hidden.
/// </summary>
/// <remarks>
/// The game's own "Link" action writes the encoded item link straight into the (hidden, and not
/// reliably "focused" while <see cref="NativeChatHider"/> suppresses it) native ChatLog textbox -
/// not something worth depending on. Worse, even when something does land there, the raw payload
/// bytes involved pack a 32-bit item id using arbitrary byte values, not just printable ASCII -
/// reading it out via <c>Utf8String.ToString()</c> and later re-encoding it via
/// <c>Encoding.UTF8.GetBytes</c> (the only way text reaches <see cref="ChatSendService"/>) risks
/// silently corrupting it. This sidesteps all of that: it reads the item id/HQ flag straight from
/// Dalamud's own typed <see cref="MenuTargetInventory.TargetItem"/> (no native-textbox scraping at
/// all) and queues a link built via
/// <see cref="Dalamud.Game.Text.SeStringHandling.SeStringBuilder.AddItemLink"/>, which
/// <see cref="ChatSendService"/> appends to the outgoing message as raw bytes, never as a C# string -
/// so the link that reaches other players is fully native and clickable, with no risk of the
/// UTF8 round-trip corrupting it.
/// </remarks>
public sealed class ItemLinkContextMenuService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly Action<uint, bool> onLinkItem;

    public ItemLinkContextMenuService(IContextMenu contextMenu, Action<uint, bool> onLinkItem)
    {
        this.contextMenu = contextMenu;
        this.onLinkItem = onLinkItem;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory)
            return;
        if (args.Target is not MenuTargetInventory target)
            return;
        if (target.TargetItem is not { } item || item.IsEmpty)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Link (Custom Chat)",
            OnClicked = _ => onLinkItem(item.BaseItemId, item.IsHq),
        });
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;
}
