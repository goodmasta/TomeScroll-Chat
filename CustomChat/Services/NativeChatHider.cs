using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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

        // On top of the raw IsVisible flip above (which only the panel addons have, and which past
        // reports showed isn't fully reliable on its own - the native "Send Tell" text input stayed
        // interactive despite ChatLog's IsVisible being force-false every frame, see
        // NativeChatInputWatcher's history), also drive AgentChatLog's own ShowAddon/HideAddon - the
        // "real" show/hide toggle the game itself uses (every AtkUnitBase-backed agent has this same
        // pair), which is more likely to also suppress input hit-testing, not just rendering. Purely
        // additive/best-effort: if the agent isn't ready yet this frame this just no-ops, and nothing
        // else here depends on it succeeding.
        var agent = AgentChatLog.Instance();
        if (agent == null || !agent->IsAddonReady())
            return;

        if (visible && agent->IsAddonHidden())
            agent->ShowAddon();
        else if (!visible && agent->IsAddonShown())
            agent->HideAddon();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        SetVisible(true);
    }
}
