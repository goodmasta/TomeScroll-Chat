using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Adds a "Whisper (Custom Chat)" entry to the game's default right-click menu (chat, party list,
/// friend list, target of target, etc.). The game's own native "Send Tell" option works by
/// pre-filling the native chat input box - which this plugin keeps hidden while it's acting as the
/// chat window, so clicking *that* one does nothing visible and is not this plugin's doing. This
/// entry is the one that actually opens/focuses the whisper tab inside the custom UI; it's placed
/// above the native items (negative <see cref="MenuItem.Priority"/>) with its own separator so it
/// isn't mistaken for the native "Send Tell" right below it.
/// </summary>
public sealed class ContextMenuService : IDisposable
{
    private readonly Plugin plugin;
    private readonly IContextMenu contextMenu;
    private readonly IPluginLog log;

    public ContextMenuService(Plugin plugin, IContextMenu contextMenu, IPluginLog log)
    {
        this.plugin = plugin;
        this.contextMenu = contextMenu;
        this.log = log;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        log.Debug("CustomChat: context menu opened, type={MenuType}, target={TargetType}", args.MenuType, args.Target?.GetType().Name ?? "null");

        if (args.MenuType != ContextMenuType.Default)
            return;
        if (args.Target is not MenuTargetDefault target)
            return;
        if (string.IsNullOrEmpty(target.TargetName) || !target.TargetHomeWorld.IsValid)
        {
            log.Debug("CustomChat: default menu target not usable (name='{Name}', homeWorldValid={Valid})", target.TargetName, target.TargetHomeWorld.IsValid);
            return;
        }

        var name = target.TargetName;
        var world = target.TargetHomeWorld.Value.Name.ToString();

        args.AddMenuItem(new MenuItem
        {
            Name = "Whisper (Custom Chat)",
            Priority = -1,
            OnClicked = _ =>
            {
                log.Debug("CustomChat: Whisper (Custom Chat) clicked for {Name}@{World}", name, world);
                plugin.OpenTellTo(name, world);
            },
        });
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;
}
