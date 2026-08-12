using System;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Watches the native chat log's own input textbox (<see cref="AddonChatLog.TextInput"/>) for any
/// slash command typed into it and redirects it into this plugin's own UI instead - confirmed by
/// direct observation that this textbox is functionally alive and capturing keystrokes even while
/// the rest of the chat log addon is force-hidden (<see cref="NativeChatHider"/>), so simply hiding
/// the addon doesn't stop "/" (the game's own "open chat with '/' pre-filled" keybind) or Enter
/// (see <see cref="EnterToChatService"/> for the other half of this) from being captured there
/// instead of by this plugin's ImGui input box.
///
/// Three cases:
/// - "/tell "/"/t " (a name typically pre-filled instantly by the game itself, not hand-typed, the
///   moment "Send Tell" is picked from any right-click menu, the friends list, or "R") opens/focuses
///   the matching whisper tab, same as before this watcher also handled general commands.
/// - Anything else starting with "/" is redirected as-is into the main window's current tab input
///   box (<see cref="Windows.MainWindow.PrefillInput"/>), so e.g. "/party hello" typed directly
///   (not via this plugin's own input) still ends up going through <see cref="ChatSendService"/>
///   instead of being submitted by the native chat straight past this plugin.
/// - Anything that doesn't start with "/" at all - most notably an item or map/flag link inserted via
///   a right-click "Link" context menu action (inventory, map, etc.), which the game writes as raw
///   encoded SeString payload bytes directly into this same textbox regardless of what actually has
///   ImGui keyboard focus - is redirected the same way. That encoded text round-trips fine through a
///   plain C# string and back out through <see cref="ChatSendService"/>'s own
///   <c>UIModule.ProcessChatBoxEntry</c> call (the payload marker bytes it uses are all in the
///   printable-ASCII range, so nothing gets lost going C# string -> UTF8 -> native Utf8String), so no
///   special parsing is needed here - just forward the raw text through untouched. Before this was
///   added, any such link silently sat in the hidden native box and never reached the plugin's own
///   input at all (reported as "right-click -> Link on an item doesn't do anything").
///
/// In every case the native box is cleared and the log window hidden immediately, unconditionally -
/// see the original tell-only version's history for why that has to happen on every detection, not
/// just outside some cooldown (a same-target cooldown still exists, but only gates the tell
/// *callback*, never this cleanup).
/// </summary>
public sealed unsafe class NativeChatInputWatcher : IDisposable
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
    private readonly Action<string> onGenericCommand;
    private string lastHandledTellKey = string.Empty;
    private DateTime suppressSameTellKeyUntil = DateTime.MinValue;

    public NativeChatInputWatcher(IFramework framework, IGameGui gameGui, IPluginLog log, Func<string?> getLocalHomeWorld, Action<string, string> onTellTargetChanged, Action<string> onGenericCommand)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.getLocalHomeWorld = getLocalHomeWorld;
        this.onTellTargetChanged = onTellTargetChanged;
        this.onGenericCommand = onGenericCommand;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var addon = gameGui.GetAddonByName<AddonChatLog>("ChatLog");
        if (addon == null || addon->TextInput == null)
            return;

        var raw = addon->TextInput->RawString.ToString();
        if (raw.Length == 0)
            return;

        if (raw[0] == '/')
        {
            var tellMatch = TellPrefix.Match(raw);
            if (tellMatch.Success)
            {
                HandleTell(addon, tellMatch, raw);
                return;
            }

            // A bare "/", still mid-typed with nothing after it yet, still gets redirected into the
            // plugin's input as an empty-ish "/" anyway, which is harmless and exactly what "press '/'
            // to start typing a command" should do regardless.
        }

        // Either a "/" command (any other than a tell), or non-command content that has no business
        // being here at all - an item/map link inserted via a right-click "Link" action being the
        // main case (see the class doc comment). Either way it leaked into the hidden native box
        // instead of the plugin's own ImGui input, so redirect it there as-is.
        ClearNativeInput(addon);
        log.Info("CustomChat: native chat input detected - '{Raw}' redirected to plugin input", raw);
        onGenericCommand(raw);
    }

    private void HandleTell(AddonChatLog* addon, Match match, string raw)
    {
        ClearNativeInput(addon);

        var name = match.Groups["name"].Value.Trim();
        var world = match.Groups["world"].Success ? match.Groups["world"].Value : (getLocalHomeWorld() ?? string.Empty);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return;

        var key = $"{name}@{world}";

        // Guards against the game re-populating the box a frame or two after we clear it while it
        // still thinks a tell to this same target is "in progress" - without this, that flicker could
        // reopen a tab the player had just closed.
        if (key == lastHandledTellKey && DateTime.UtcNow < suppressSameTellKeyUntil)
            return;

        lastHandledTellKey = key;
        suppressSameTellKeyUntil = DateTime.UtcNow + SameTargetCooldown;
        log.Info("CustomChat: native tell input detected - '{Raw}' -> {Key}", raw, key);

        onTellTargetChanged(name, world);
    }

    private static void ClearNativeInput(AddonChatLog* addon)
    {
        addon->TextInput->SetText(string.Empty);
        var agent = AgentChatLog.Instance();
        if (agent != null)
            agent->HideLogWindow();
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
