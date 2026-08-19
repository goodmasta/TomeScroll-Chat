using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TomeScrollChat.Services;

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
        set => active = value;
    }

    // Reapplied every frame in *both* directions, not just while hiding - the game apparently doesn't
    // only fight this addon's visibility while something is trying to hide it; unchecking "Hide the
    // game's built-in chat window" was reported live as sometimes not actually bringing the native
    // chat back, which a single one-shot SetVisible(true) in the old Active setter couldn't recover
    // from if the game happened to reset IsVisible back to false on the very same/next frame. SetVisible
    // itself already skips the write when the flag already matches (see below), so enforcing the
    // "should be visible" state continuously costs nothing extra once it's actually settled.
    private void OnFrameworkUpdate(IFramework _) => SetVisible(!active);

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

        // Previously also drove AgentChatLog.ShowAddon()/HideAddon() here on top of the raw IsVisible
        // flip, on the theory that it would suppress input hit-testing more thoroughly - reverted
        // (2026-08-13) as the prime suspect for the native "Link" item-link hook (ItemLinkHookService)
        // never firing despite installing cleanly: the game's own "Link" handling may itself check
        // whether the chat log addon/agent is shown before it ever gets to the point of calling
        // AgentChatLog.LinkItem, so aggressively hiding it here could have been suppressing the
        // native action before it ever reached the hooked function. If this turns out not to be the
        // actual cause, this is where to look again for a stronger suppression - see the memory file
        // for the theory this note is based on, and re-add only behind clear evidence it doesn't
        // regress the same native-Link path a second time.
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        SetVisible(true);
    }
}
