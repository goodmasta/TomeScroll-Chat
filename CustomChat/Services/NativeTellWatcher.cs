using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace CustomChat.Services;

/// <summary>
/// Detects when the game sets a pending whisper target on its own shell module - which happens for
/// the native right-click "Send Tell" (any context: target, party list, friends list, ...) as well
/// as the "R" reply-to-last-tell shortcut and manually typed "/tell" commands - and opens/focuses
/// the matching whisper tab in this plugin instead. This is what actually makes the *native*
/// "Send Tell" flow work with this plugin: that flow normally focuses the game's own chat input,
/// which stays hidden while this plugin is acting as the chat window, so without this the native
/// option would never visibly do anything.
/// </summary>
public sealed unsafe class NativeTellWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly Action<string, string> onTellTargetChanged;
    private string lastName = string.Empty;
    private string lastWorld = string.Empty;

    public NativeTellWatcher(IFramework framework, Action<string, string> onTellTargetChanged)
    {
        this.framework = framework;
        this.onTellTargetChanged = onTellTargetChanged;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return;

        var name = shell->TellName.ToString();
        var world = shell->TellWorld.ToString();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
        {
            // Cleared (e.g. tell sent, or the player cancelled) - the next non-empty value is a
            // fresh target even if it happens to match what we saw before this reset.
            lastName = string.Empty;
            lastWorld = string.Empty;
            return;
        }

        if (name == lastName && world == lastWorld)
            return;

        lastName = name;
        lastWorld = world;
        onTellTargetChanged(name, world);
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
