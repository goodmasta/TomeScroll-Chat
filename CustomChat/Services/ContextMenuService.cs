using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Adds a "Send Tell (Custom Chat)" entry to the game's default right-click menu (chat, party
/// list, friend list, target of target, etc.). The game's own native "Send Tell" option works by
/// pre-filling the native chat input box - which this plugin keeps hidden while it's acting as the
/// chat window, so that option silently does nothing from the player's point of view. This gives
/// an equivalent that opens/focuses the whisper tab inside the custom UI instead.
/// </summary>
public sealed class ContextMenuService : IDisposable
{
    private readonly Plugin plugin;
    private readonly IContextMenu contextMenu;

    public ContextMenuService(Plugin plugin, IContextMenu contextMenu)
    {
        this.plugin = plugin;
        this.contextMenu = contextMenu;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default)
            return;
        if (args.Target is not MenuTargetDefault target)
            return;
        if (string.IsNullOrEmpty(target.TargetName) || !target.TargetHomeWorld.IsValid)
            return;

        var name = target.TargetName;
        var world = target.TargetHomeWorld.Value.Name.ToString();

        args.AddMenuItem(new MenuItem
        {
            Name = "Send Tell (Custom Chat)",
            OnClicked = _ => plugin.OpenTellTo(name, world),
        });
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;
}
