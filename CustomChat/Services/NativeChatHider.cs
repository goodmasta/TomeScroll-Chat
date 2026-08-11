using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomChat.Services;

/// <summary>
/// Hides the game's own chat log addon(s) while this plugin's UI is acting as the chat, the same
/// way ChatTwo does it: the game keeps resetting the addon's visibility on its own, so this has to
/// re-apply every frame rather than being a one-time toggle.
/// </summary>
public sealed unsafe class NativeChatHider : IDisposable
{
    private static readonly string[] AddonNames =
    {
        "ChatLog",
        "ChatLogPanel_0",
        "ChatLogPanel_1",
        "ChatLogPanel_2",
        "ChatLogPanel_3",
    };

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private bool active;

    public NativeChatHider(IFramework framework, IGameGui gameGui)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        framework.Update += OnFrameworkUpdate;
    }

    public bool Active
    {
        get => active;
        set
        {
            active = value;
            if (!active)
                SetVisible(true);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (active)
            SetVisible(false);
    }

    private void SetVisible(bool visible)
    {
        foreach (var name in AddonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null)
                continue;

            if (addon->IsVisible != visible)
                addon->IsVisible = visible;
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        SetVisible(true);
    }
}
