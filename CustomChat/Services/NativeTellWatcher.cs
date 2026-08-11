using System;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CustomChat.Services;

/// <summary>
/// Watches the native chat log's own input textbox (<see cref="AddonChatLog.TextInput"/>) for the
/// "/tell Name[@World] " the game pre-fills there the instant "Send Tell" is picked from any
/// right-click menu, the friends list, or the "R" reply shortcut. Confirmed by direct observation:
/// that text visibly appears in the native input even while this plugin hides the rest of the chat
/// log, so the native input isn't fully inert - reading it directly is the one reliable signal.
/// On the rising edge (text just started with "/tell "/"/t ") this opens/focuses the matching
/// whisper tab in this plugin's own UI and clears the native box, so the pre-filled command ends up
/// in this plugin's chat "instead of" the original one, per the user's request.
/// </summary>
public sealed unsafe class NativeTellWatcher : IDisposable
{
    private static readonly Regex TellPrefix = new(
        @"^/te?ll?\s+(?<name>[A-Za-z'\-]+(?:\s[A-Za-z'\-]+)?)(?:@(?<world>[A-Za-z]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Func<string?> getLocalHomeWorld;
    private readonly Action<string, string> onTellTargetChanged;
    private string lastTriggerKey = string.Empty;

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
        {
            lastTriggerKey = string.Empty;
            return;
        }

        var raw = addon->TextInput->RawString.ToString();
        var match = TellPrefix.Match(raw);
        if (!match.Success)
        {
            lastTriggerKey = string.Empty;
            return;
        }

        var name = match.Groups["name"].Value.Trim();
        var world = match.Groups["world"].Success ? match.Groups["world"].Value : (getLocalHomeWorld() ?? string.Empty);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return;

        var key = $"{name}@{world}";
        if (key == lastTriggerKey)
            return;

        lastTriggerKey = key;
        log.Info("CustomChat: native tell input detected - '{Raw}' -> {Key}", raw, key);

        // Clear the native box so the pre-filled command doesn't sit there once our tab takes over -
        // safe here because we act on the very next frame after the game populates it, before the
        // player could plausibly have typed anything past the auto-filled "/tell Name@World ".
        addon->TextInput->SetText(string.Empty);

        onTellTargetChanged(name, world);
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
