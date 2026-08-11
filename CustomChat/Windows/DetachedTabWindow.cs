using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;

namespace CustomChat.Windows;

/// <summary>One floating window for a tab popped out of the main window - or a whisper conversation
/// configured to open this way by default (see <see cref="Configuration.OpenWhispersInSeparateWindow"/>).</summary>
public sealed class DetachedTabWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    public ChatTabConfig Tab { get; }
    private string inputText = string.Empty;

    public DetachedTabWindow(Plugin plugin, ChatTabConfig tab)
        : base($"{tab.Name}###CustomChatTab_{tab.Id}")
    {
        this.plugin = plugin;
        Tab = tab;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = tab.DetachedWindowSize ?? new Vector2(420, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        ShowCloseButton = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        WindowName = $"{Tab.Name}###CustomChatTab_{Tab.Id}";

        if (ImGui.SmallButton("Reattach to main window"))
        {
            plugin.SetTabDetached(Tab, false);
            return;
        }

        using (var child = ImRaii.Child("Messages", new Vector2(0, -28), false))
        {
            if (child.Success)
            {
                var messages = plugin.TabMessageBuffer.GetMessages(Tab);
                ChatMessageRenderer.DrawMessages(Tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.OpenTellToKey, Plugin.GetLocalPlayerKey());

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                    ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.SetNextItemWidth(-1);
        var send = ImGui.InputText($"##input_{Tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(Tab, inputText);
            inputText = string.Empty;
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    /// <summary>Closing the floating window (X button) reattaches the tab to the main window rather than deleting it.</summary>
    public override void OnClose() => plugin.SetTabDetached(Tab, false);

    public void Dispose()
    {
    }
}
