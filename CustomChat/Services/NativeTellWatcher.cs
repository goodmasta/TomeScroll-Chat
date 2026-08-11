using System;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Watches the native chat log's own input textbox (<see cref="AddonChatLog.TextInput"/>) for the
/// "/tell Name[@World] " the game pre-fills there the instant "Send Tell" is picked from any
/// right-click menu, the friends list, or the "R" reply shortcut. Confirmed by direct observation:
/// that text visibly appears in the native input even while this plugin hides the rest of the chat
/// log, so the native input isn't fully inert - reading it directly is the one reliable signal.
/// On the rising edge (text just started with "/tell "/"/t ") this opens/focuses the matching
/// whisper tab in this plugin's own UI, clears the native box, and closes the native tell
/// composition (<see cref="AgentChatLog.HideLogWindow"/>) so the game stops re-populating that box
/// on its own - without that, clearing it once wasn't enough: the game kept restoring the text a
/// frame or two later while it still considered a tell "in progress", which could make this watcher
/// see a fresh rising edge and reopen the tab even after the player had already closed it.
/// </summary>
public sealed unsafe class NativeTellWatcher : IDisposable
{
    private static readonly Regex TellPrefix = new(
        @"^/te?ll?\s+(?<name>[A-Za-z'\-]+(?:\s[A-Za-z'\-]+)?)(?:@(?<world>[A-Za-z]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly TimeSpan SameTargetCooldown = TimeSpan.FromSeconds(2);

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Func<string?> getLocalHomeWorld;
    private readonly Action<string, string> onTellTargetChanged;
    private string lastHandledKey = string.Empty;
    private DateTime suppressSameKeyUntil = DateTime.MinValue;

    public NativeTellWatcher(IFramework framework, IGameGui gameGui, IPluginLog log, Func<string?> getLocalHomeWorld, Action<string, string> onTellTargetChanged)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.getLocalHomeWorld = getLocalHomeWorld;
        this.onTellTargetChanged = onTellTargetChanged;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var addon = gameGui.GetAddonByName<AddonChatLog>("ChatLog");
        if (addon == null || addon->TextInput == null)
            return;

        var raw = addon->TextInput->RawString.ToString();
        var match = TellPrefix.Match(raw);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value.Trim();
        var world = match.Groups["world"].Success ? match.Groups["world"].Value : (getLocalHomeWorld() ?? string.Empty);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return;

        var key = $"{name}@{world}";

        // Guards against the game re-populating the box a frame or two after we clear it while it
        // still thinks a tell to this same target is "in progress" - without this, that flicker could
        // reopen a tab the player had just closed.
        if (key == lastHandledKey && DateTime.UtcNow < suppressSameKeyUntil)
            return;

        lastHandledKey = key;
        suppressSameKeyUntil = DateTime.UtcNow + SameTargetCooldown;
        log.Info("CustomChat: native tell input detected - '{Raw}' -> {Key}", raw, key);

        addon->TextInput->SetText(string.Empty);

        // Actually close the native tell composition (not just clear the visible text) so the game
        // stops treating a tell as still being written and re-filling the box on its own.
        var agent = AgentChatLog.Instance();
        if (agent != null)
            agent->HideLogWindow();

        onTellTargetChanged(name, world);
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
