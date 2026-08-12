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

    // "Select text" mode: swaps the rich message rendering for a read-only plain-text transcript
    // (native ImGui click-drag selection + Ctrl+C) - see MainWindow's field comment for the same flag.
    private bool selectionMode;
    private string transcriptText = string.Empty;
    private int transcriptMessageCount = -1;

    // Ctrl+F "search in this tab" - see MainWindow's field comment for the same flag.
    private bool searchMode;
    private string searchQuery = string.Empty;
    private bool focusSearchInput;

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

        // All scrolling happens inside the "Messages" child - see MainWindow's constructor for why
        // the window itself should never grow its own second, outer scrollbar.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

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

        // Same Ctrl+F handling as MainWindow.DrawContent - see there for the reasoning.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F))
        {
            searchMode = true;
            selectionMode = false;
            focusSearchInput = true;
        }

        if (searchMode)
            DrawSearchBar();

        // Leave room for the two rows below (Jump to bottom/Emotes buttons, then the input box).
        var bottomReserve = ImGui.GetFrameHeightWithSpacing() * 2f;
        using (var child = ImRaii.Child("Messages", new Vector2(0, -bottomReserve), false))
        {
            if (child.Success)
            {
                ImGui.SetWindowFontScale(Plugin.Configuration.FontSize / 14f);
                var messages = plugin.TabMessageBuffer.GetMessages(Tab);

                if (selectionMode)
                {
                    if (transcriptMessageCount != messages.Count)
                    {
                        transcriptText = ChatMessageRenderer.BuildTranscript(messages);
                        transcriptMessageCount = messages.Count;
                    }

                    ImGui.InputTextMultiline($"##transcript_{Tab.Id}", ref transcriptText, transcriptText.Length + 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
                }
                else
                {
                    if (pendingScrollToBottom)
                    {
                        ImGui.SetScrollY(ImGui.GetScrollMaxY());
                        pendingScrollToBottom = false;
                        dividerIndex = -1;
                    }

                    var wasScrollingToDivider = pendingScrollToDivider;
                    var lastVisible = ChatMessageRenderer.DrawMessages(Tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, plugin.OpenTellToKey, plugin.SendPartyInvite, plugin.SendFriendRequest, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider, searchMode ? searchQuery : null);
                    pendingScrollToDivider = false;

                    if (!searchMode && lastVisible >= 0)
                    {
                        var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                        if (newUnread < Tab.UnreadCount)
                        {
                            Tab.UnreadCount = newUnread;
                            plugin.TabManager.Save();
                        }
                    }

                    // Never auto-follow to the bottom on the same frame we just scrolled to the "New
                    // messages" divider - see MainWindow's DrawContent for why. Also skipped while
                    // searching - see MainWindow's DrawContent for that reasoning too.
                    if (!searchMode && !wasScrollingToDivider && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                        ImGui.SetScrollHereY(1f);
                }
            }
        }

        var iconSize = ImGui.GetFrameHeight();
        var toolbarSpacing = ImGui.GetStyle().ItemSpacing.X;
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

        ImGui.SetNextItemWidth(-(iconSize * 2 + toolbarSpacing));

        // Re-focus has to happen right before InputText (offset 0 = "the very next widget") rather
        // than after, since after now runs through the icon buttons/popup - an unpredictable number of
        // widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }

        var send = ImGui.InputText($"##input_{Tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine(0, 0);
        bool selectClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            selectClicked = ImGui.Button($"{FontAwesomeIcon.ICursor.ToIconString()}##selecttoggle_{Tab.Id}", new Vector2(iconSize, iconSize));
        if (selectClicked)
        {
            selectionMode = !selectionMode;
            if (selectionMode)
                searchMode = false;
            transcriptMessageCount = -1;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectionMode ? "Back to normal chat view" : "Select & copy text");

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

    /// <summary>Same behaviour as MainWindow.DrawSearchBar - see there for the reasoning.</summary>
    private void DrawSearchBar()
    {
        var closeSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(closeSize + ImGui.GetStyle().ItemSpacing.X));

        if (focusSearchInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearchInput = false;
        }

        ImGui.InputTextWithHint($"##search_{Tab.Id}", "Search in this tab...", ref searchQuery, 200);

        var escapePressed = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape);

        ImGui.SameLine(0, 0);
        var closeClicked = ImGui.Button($"X##searchclose_{Tab.Id}", new Vector2(closeSize, closeSize));

        if (escapePressed || closeClicked)
        {
            searchMode = false;
            searchQuery = string.Empty;
        }

        ImGui.Separator();
    }

    /// <summary>Closing the floating window (X button) reattaches the tab to the main window rather than deleting it.</summary>
    public override void OnClose() => plugin.SetTabDetached(Tab, false);

    public void Dispose()
    {
    }
}
