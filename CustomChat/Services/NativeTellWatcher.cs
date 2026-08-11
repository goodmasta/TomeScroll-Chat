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
    private readonly IPluginLog log;
    private readonly Func<string?> getLocalHomeWorld;
    private readonly Action<string, string> onTellTargetChanged;
    private ulong lastContentId;

    public NativeTellWatcher(IFramework framework, IPluginLog log, Func<string?> getLocalHomeWorld, Action<string, string> onTellTargetChanged)
    {
        this.framework = framework;
        this.log = log;
        this.getLocalHomeWorld = getLocalHomeWorld;
        this.onTellTargetChanged = onTellTargetChanged;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return;

        // ContentId is a reliable non-string signal (0 = no pending tell target); TellWorld can be
        // genuinely empty for a same-world target (no "@World" needed for those), so it can't be
        // used to gate detection the way an earlier version of this did.
        var contentId = shell->ContentId;
        if (contentId == 0)
        {
            lastContentId = 0;
            return;
        }

        if (contentId == lastContentId)
            return;

        lastContentId = contentId;

        var name = shell->TellName.ToString();
        var world = shell->TellWorld.ToString();
        if (string.IsNullOrEmpty(world))
            world = getLocalHomeWorld() ?? string.Empty;

        log.Info("CustomChat: native tell target detected - name='{Name}' world='{World}' contentId={ContentId}", name, world, contentId);

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
        {
            log.Warning("CustomChat: native tell target had no usable name/world (name='{Name}' world='{World}'), ignoring", name, world);
            return;
        }

        onTellTargetChanged(name, world);
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
