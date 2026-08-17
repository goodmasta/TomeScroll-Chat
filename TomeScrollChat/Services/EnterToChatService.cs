using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace TomeScrollChat.Services;

/// <summary>
/// Mimics the vanilla game's "press Enter to open chat" keybind - since the native chat log (and
/// its input box) stays force-hidden while this plugin is active, that native keybind no longer
/// does anything visible on its own. Watches the raw key state (not ImGui's own per-widget key
/// handling), so this fires even when no ImGui window currently has focus, same as the native
/// keybind does. Fires once per press (not on held-key repeat), and only when nothing else is
/// already capturing text input, so it doesn't steal focus away from some other text field the
/// player is mid-way through confirming.
/// </summary>
public sealed class EnterToChatService : IDisposable
{
    private readonly IFramework framework;
    private readonly IKeyState keyState;
    private readonly Action onEnterPressed;
    private bool wasDown;

    public EnterToChatService(IFramework framework, IKeyState keyState, Action onEnterPressed)
    {
        this.framework = framework;
        this.keyState = keyState;
        this.onEnterPressed = onEnterPressed;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var isDown = keyState[VirtualKey.RETURN];
        if (isDown && !wasDown && !ImGui.GetIO().WantTextInput)
            onEnterPressed();

        wasDown = isDown;
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
