using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private string emoteSearch = string.Empty;
    private bool refocusInput;

    // Same Discord-style "last read position" tracking as MainWindow - see its DrawContent for the
    // reasoning. This window only ever shows one fixed tab, so it's frozen once at construction
    // (equivalent to "just opened") rather than re-detected on a tab switch.
    private int dividerIndex;
    private bool pendingScrollToDivider;
    private bool pendingScrollToBottom;

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

        var messagesNow = plugin.TabMessageBuffer.GetMessages(tab);
        dividerIndex = tab.UnreadCount > 0 ? Math.Max(0, messagesNow.Count - tab.UnreadCount) : -1;
        pendingScrollToDivider = dividerIndex >= 0;
    }

    public override void Draw()
    {
        WindowName = $"{Tab.Name}###CustomChatTab_{Tab.Id}";

        if (ImGui.SmallButton("Reattach to main window"))
        {
            plugin.SetTabDetached(Tab, false);
            return;
        }

        // Leave room for the two rows below (Jump to bottom/Emotes buttons, then the input box).
        var bottomReserve = ImGui.GetFrameHeightWithSpacing() * 2f;
        using (var child = ImRaii.Child("Messages", new Vector2(0, -bottomReserve), false))
        {
            if (child.Success)
            {
                if (pendingScrollToBottom)
                {
                    ImGui.SetScrollY(ImGui.GetScrollMaxY());
                    pendingScrollToBottom = false;
                    dividerIndex = -1;
                }

                var messages = plugin.TabMessageBuffer.GetMessages(Tab);
                var lastVisible = ChatMessageRenderer.DrawMessages(Tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.OpenTellToKey, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider);
                pendingScrollToDivider = false;

                if (lastVisible >= 0)
                {
                    var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                    if (newUnread < Tab.UnreadCount)
                    {
                        Tab.UnreadCount = newUnread;
                        plugin.TabManager.Save();
                    }
                }

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                    ImGui.SetScrollHereY(1f);
            }
        }

        var iconSize = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - iconSize);
        if (Tab.UnreadCount == 0)
        {
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        }
        else
        {
            bool jumpClicked;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                jumpClicked = ImGui.Button($"{FontAwesomeIcon.AngleDoubleDown.ToIconString()}##jumpbottom_{Tab.Id}", new Vector2(iconSize, iconSize));
            if (jumpClicked)
                pendingScrollToBottom = true;
        }

        ImGui.SetNextItemWidth(-(iconSize + ImGui.GetStyle().ItemSpacing.X));

        // Re-focus has to happen right before InputText (offset 0 = "the very next widget") rather
        // than after, since after now runs through the icon button/popup - an unpredictable number of
        // widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }

        var send = ImGui.InputText($"##input_{Tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine(0, 0);
        bool emoteClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            emoteClicked = ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##emotebtn_{Tab.Id}", new Vector2(iconSize, iconSize));
        if (emoteClicked)
        {
            emoteSearch = string.Empty;
            ImGui.OpenPopup($"EmotePicker_{Tab.Id}");
        }

        EmotePicker.Draw($"EmotePicker_{Tab.Id}", plugin.EmoteService, ref emoteSearch, code =>
        {
            inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + code + " ";
        });

        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(Tab, inputText);
            inputText = string.Empty;
            refocusInput = true;
        }
    }

    /// <summary>Closing the floating window (X button) reattaches the tab to the main window rather than deleting it.</summary>
    public override void OnClose() => plugin.SetTabDetached(Tab, false);

    public void Dispose()
    {
    }
}
