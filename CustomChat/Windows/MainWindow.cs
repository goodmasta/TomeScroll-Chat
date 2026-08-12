using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;

namespace CustomChat.Windows;

/// <summary>The main chat window: a sidebar of every non-detached tab plus the selected tab's
/// messages and input box. Detached tabs render in their own <see cref="DetachedTabWindow"/> instead.
/// Tabs are created/deleted from <see cref="ConfigWindow"/>, not here - this window only lets you pop a
/// tab out, jump to its settings, or (whisper tabs only) close the conversation.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 BlinkBase = new(1f, 1f, 1f, 1f);

    private readonly Plugin plugin;
    private Guid? selectedTabId;
    private string inputText = string.Empty;
    private string emoteSearch = string.Empty;
    private bool refocusInput;
    private string? pendingPrefillText;

    // Discord-style "last read position": which tab the content area is currently showing, a frozen
    // divider index into that tab's message list (set once when switching in, not updated as the
    // player reads further down - see DrawContent), and one-shot scroll requests.
    private Guid? contentTabId;
    private int dividerIndex = -1;
    private bool pendingScrollToDivider;
    private bool pendingScrollToBottom;

    // "Select text" mode: swaps the rich message rendering for a read-only plain-text transcript
    // (native ImGui click-drag selection + Ctrl+C) - see DrawContent.
    private bool selectionMode;
    private string transcriptText = string.Empty;
    private int transcriptMessageCount = -1;

    public MainWindow(Plugin plugin)
        : base("Custom Chat###CustomChatMainWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Always-on chat window: no close button, Esc doesn't close it, and OnClose() below
        // refuses the close so nothing (hotkey, another plugin, a stray IsOpen = false) can hide it.
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = true;

        // Always stays on top of other plugin windows instead of getting buried behind them.
        IsTopMost = true;

        // No collapse triangle - a gear button that opens Settings takes its place instead.
        // NoScrollbar/NoScrollWithMouse: all scrolling happens inside the "Messages" child - if the
        // window's own total content height ever slightly exceeds its size (e.g. from a future row
        // added below the message list without perfectly updating the reserved space, as already
        // happened once), it should never grow a second, outer scrollbar of its own.
        Flags |= ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Custom Chat settings"),
            Click = _ => plugin.OpenSettings(),
        });
    }

    /// <summary>Nothing is allowed to close the main chat window - it stays open for the whole session.</summary>
    public override void OnClose() => IsOpen = true;

    /// <summary>Brings the window to front and focuses the current tab's input box - the "press Enter
    /// to open chat" keybind's handler (see <see cref="Services.EnterToChatService"/>).</summary>
    public void RequestFocusInput()
    {
        RequestFocus = true;
        refocusInput = true;
    }

    /// <summary>Same as <see cref="RequestFocusInput"/>, but also seeds the input box with text - the
    /// "typed '/' into the native chat" redirect's handler (see
    /// <see cref="Services.NativeChatInputWatcher"/>): the native input already captured the
    /// character(s) before this fires, so they have to be carried over explicitly rather than just
    /// refocusing an empty box. The text is deliberately applied one frame *after* the focus request
    /// (see <see cref="DrawInputRow"/>), not in the same one - ImGui's InputText selects the entire
    /// buffer when it's given keyboard focus and already-non-empty text on the very same frame, which
    /// made the next keystroke overwrite the redirected "/" instead of continuing after it.</summary>
    public void PrefillInput(string text)
    {
        pendingPrefillText = text;
        RequestFocus = true;
        refocusInput = true;
    }

    public override void Draw()
    {
        using var table = ImRaii.Table("CustomChatLayout", 2, ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Sidebar", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawSidebar();

        ImGui.TableNextColumn();
        DrawContent();
    }

    private void DrawSidebar()
    {
        using (var child = ImRaii.Child("Sidebar", new Vector2(0, -28), true))
        {
            if (child.Success)
            {
                // Snapshot the list: closing a whisper tab from the context menu below mutates
                // TabManager.Tabs mid-draw, which would otherwise throw iterating the live list.
                foreach (var tab in plugin.TabManager.Tabs.ToList())
                {
                    if (tab.IsDetached)
                        continue;

                    DrawTabRow(tab);

                    if (ImGui.BeginPopupContextItem($"ctx_{tab.Id}"))
                    {
                        DrawTabContextMenu(tab);
                        ImGui.EndPopup();
                    }
                }
            }
        }

        var hasPmTabs = plugin.TabManager.Tabs.Any(t => t.IsPmTab);
        using (ImRaii.Disabled(!hasPmTabs))
        {
            if (ImGui.Button("Close All PM", new Vector2(-1, 0)))
            {
                plugin.CloseAllWhisperTabs();
                if (selectedTabId != null && !plugin.TabManager.Tabs.Any(t => t.Id == selectedTabId))
                    selectedTabId = null;
            }
        }
    }

    /// <summary>
    /// One sidebar row: a full-width Selectable with an empty label for the click/hover area, with
    /// the name and unread count painted on top via <see cref="ImDrawList.AddText"/> so the count can
    /// be coloured red and the name can pulse - a single Selectable label can't have mixed colours.
    /// Deliberately uses the draw list directly instead of more ImGui widgets positioned via
    /// SetCursorScreenPos: that approach (tried first) fed back into ImGui's own cursor/layout state
    /// and threw off every following row's horizontal position, drifting further left row by row.
    /// Painting onto the draw list doesn't touch layout state at all, so it can't do that.
    /// </summary>
    private void DrawTabRow(ChatTabConfig tab)
    {
        var selected = tab.Id == selectedTabId;
        if (ImGui.Selectable($"##tab_{tab.Id}", selected))
            selectedTabId = tab.Id;

        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var textY = itemMin.Y + (itemMax.Y - itemMin.Y - ImGui.GetTextLineHeight()) / 2f;
        var drawList = ImGui.GetWindowDrawList();

        // Per-tab override (see Settings > Tabs) if set, otherwise the global default from
        // Settings > General - whisper tabs default to WhisperNotifyColor, regular tabs to the
        // separate blink/count defaults.
        var config = Plugin.Configuration;
        var blinkColor = tab.BlinkColorOverride ?? (tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelBlinkColor);
        var countColor = tab.UnreadCountColorOverride ?? (tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelUnreadCountColor);

        var isBlinking = tab.UnreadCount > 0 && tab.ShouldNotify;
        var nameColor = isBlinking
            ? Vector4.Lerp(BlinkBase, blinkColor, (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f)
            : BlinkBase;

        var textX = itemMin.X + 4;

        if (!string.IsNullOrEmpty(tab.IconEmoji))
        {
            var iconSize = ImGui.GetTextLineHeight();
            var iconTexture = plugin.EmoteService.TryGetTexture(tab.IconEmoji);
            if (iconTexture != null)
            {
                var iconMin = new Vector2(textX, textY);
                drawList.AddImage(iconTexture.Handle, iconMin, iconMin + new Vector2(iconSize, iconSize));
            }

            textX += iconSize + 4;
        }

        // Same friend marker shown next to a friend's name in the message list (see
        // ChatMessageRenderer.DrawMessage) - also shown here so a friend's whisper tab is
        // recognisable in the sidebar itself, not just once you're already looking at their messages.
        if (tab.IsPmTab && config.FriendMarkerEnabled && !string.IsNullOrEmpty(config.FriendMarkerEmoji) &&
            !string.IsNullOrEmpty(tab.PmPartnerKey) && plugin.FriendListService.IsFriendKey(tab.PmPartnerKey))
        {
            var markerSize = ImGui.GetTextLineHeight();
            var markerTexture = plugin.EmoteService.TryGetTexture(config.FriendMarkerEmoji);
            if (markerTexture != null)
            {
                var markerMin = new Vector2(textX, textY);
                drawList.AddImage(markerTexture.Handle, markerMin, markerMin + new Vector2(markerSize, markerSize));
            }

            textX += markerSize + 4;
        }

        var namePos = new Vector2(textX, textY);
        drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(nameColor), tab.Name);

        if (tab.UnreadCount > 0)
        {
            var countPos = new Vector2(namePos.X + ImGui.CalcTextSize(tab.Name).X + 4, textY);
            drawList.AddText(countPos, ImGui.ColorConvertFloat4ToU32(countColor), $"({tab.UnreadCount})");
        }
    }

    private void DrawTabContextMenu(ChatTabConfig tab)
    {
        using (ImRaii.Disabled(tab.UnreadCount == 0))
        {
            if (ImGui.MenuItem("Mark all as read"))
            {
                tab.UnreadCount = 0;
                if (tab.Id == contentTabId)
                    dividerIndex = -1;
                plugin.TabManager.Save();
            }
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Pop out to floating window"))
            plugin.SetTabDetached(tab, true);

        if (tab.IsPmTab)
        {
            // Whisper history is keyed by conversation partner, not by this tab's id, so closing it
            // here never deletes any messages - it just hides the tab until the next message (or the
            // native/in-chat "Send Tell") reopens it. Regular tabs are only managed from settings.
            ImGui.Separator();
            if (ImGui.MenuItem("Close chat"))
            {
                plugin.TabManager.RemoveTab(tab);
                if (selectedTabId == tab.Id)
                    selectedTabId = null;
            }
        }
        else
        {
            if (ImGui.MenuItem("Edit channels/filter..."))
                plugin.OpenTabEditor(tab.Id);
        }
    }

    private void DrawContent()
    {
        var tab = ResolveSelectedTab();
        if (tab == null)
        {
            ImGui.TextDisabled("No tabs yet - create one on the left.");
            return;
        }

        if (contentTabId != tab.Id)
        {
            // Persist wherever the previously-viewed tab's unread count ended up (see the visibility
            // tracking below) before switching what the divider index refers to.
            if (contentTabId != null)
                plugin.TabManager.Save();

            contentTabId = tab.Id;
            var messagesNow = plugin.TabMessageBuffer.GetMessages(tab);
            // Frozen at the tab's unread count *as of opening it* - deliberately not recomputed as
            // reading progresses, so the divider stays put where "new" started, Discord-style.
            dividerIndex = tab.UnreadCount > 0 ? Math.Max(0, messagesNow.Count - tab.UnreadCount) : -1;
            pendingScrollToDivider = dividerIndex >= 0;
        }

        // Leave room for the two rows below (Jump to bottom/Emotes buttons, then the input box) -
        // this used to be a flat -28 for just the input row, and grew the window's own scroll region
        // when the buttons row was added without updating it.
        var bottomReserve = ImGui.GetFrameHeightWithSpacing() * 2f;
        using (var child = ImRaii.Child("Messages", new Vector2(0, -bottomReserve), false))
        {
            if (child.Success)
            {
                // Scoped to just this child (not the sidebar/buttons/whole window) - resets
                // automatically when the child ends. 14pt is the slider's default, i.e. 1x scale.
                ImGui.SetWindowFontScale(Plugin.Configuration.FontSize / 14f);
                var messages = plugin.TabMessageBuffer.GetMessages(tab);

                if (selectionMode)
                {
                    DrawSelectionTranscript(tab, messages);
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
                    var lastVisible = ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, plugin.OpenTellToKey, plugin.SendPartyInvite, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider);
                    pendingScrollToDivider = false;

                    // Unread count shrinks as messages actually scroll into view, not all at once on open.
                    if (lastVisible >= 0)
                    {
                        var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                        if (newUnread < tab.UnreadCount)
                            tab.UnreadCount = newUnread;
                    }

                    // Never auto-follow to the bottom on the same frame we just scrolled to the "New
                    // messages" divider - leftover scroll state from whatever tab was previously shown in
                    // this same child can otherwise read as "already at the bottom" (e.g. clamped down to
                    // the new, shorter content's max) and immediately snap back past the divider.
                    if (!wasScrollingToDivider && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                        ImGui.SetScrollHereY(1f);
                }
            }
        }

        DrawToolbarRow(tab);
        DrawInputRow(tab);
    }

    /// <summary>Read-only plain-text transcript shown instead of the normal rich message list while
    /// "select text" mode is on - see the field comment on <see cref="selectionMode"/>. Rebuilt only
    /// when the message count changes, not every frame.</summary>
    private void DrawSelectionTranscript(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages)
    {
        if (transcriptMessageCount != messages.Count)
        {
            transcriptText = ChatMessageRenderer.BuildTranscript(messages);
            transcriptMessageCount = messages.Count;
        }

        ImGui.InputTextMultiline($"##transcript_{tab.Id}", ref transcriptText, transcriptText.Length + 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
    }

    /// <summary>The row above the input box: a "select text" toggle (left) and a "jump to bottom"
    /// button (right, same size, only actually shown - not just enabled - while there are unread
    /// messages). Both still occupy the row's height even when not drawn as their real button, via
    /// Dummy, so the layout doesn't jump around.</summary>
    private void DrawToolbarRow(ChatTabConfig tab)
    {
        var iconSize = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rightEdge = ImGui.GetWindowContentRegionMax().X;

        ImGui.SetCursorPosX(rightEdge - iconSize * 2 - spacing);
        bool selectClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            selectClicked = ImGui.Button($"{FontAwesomeIcon.ICursor.ToIconString()}##selecttoggle_{tab.Id}", new Vector2(iconSize, iconSize));
        if (selectClicked)
        {
            selectionMode = !selectionMode;
            transcriptMessageCount = -1; // force a rebuild next time selection mode is entered
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectionMode ? "Back to normal chat view" : "Select & copy text");

        ImGui.SameLine(0, spacing);

        if (tab.UnreadCount == 0)
        {
            ImGui.Dummy(new Vector2(iconSize, iconSize));
            return;
        }

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            clicked = ImGui.Button($"{FontAwesomeIcon.AngleDoubleDown.ToIconString()}##jumpbottom_{tab.Id}", new Vector2(iconSize, iconSize));
        if (clicked)
            pendingScrollToBottom = true;
    }

    /// <summary>The message input box with a Telegram/Discord-style emote-picker smiley button
    /// attached flush to its right edge (zero spacing between them, same height) instead of a
    /// separate button on its own row.</summary>
    private void DrawInputRow(ChatTabConfig tab)
    {
        var iconSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(iconSize + ImGui.GetStyle().ItemSpacing.X));

        // Re-focusing after a send has to happen right before InputText is submitted (offset 0 = "the
        // very next widget") - doing it *after*, like before the icon button was added here, would now
        // count back through the button/popup instead of the input box, which is an unpredictable
        // number of widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }
        else if (pendingPrefillText != null)
        {
            // Deliberately not applied on the same frame the focus request above fires (see
            // PrefillInput) - it lands here, one frame later, once the widget is already active.
            inputText = pendingPrefillText;
            pendingPrefillText = null;
        }

        var send = ImGui.InputText($"##input_{tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine(0, 0);
        bool emoteClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            emoteClicked = ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##emotebtn_{tab.Id}", new Vector2(iconSize, iconSize));
        if (emoteClicked)
        {
            emoteSearch = string.Empty;
            ImGui.OpenPopup($"EmotePicker_{tab.Id}");
        }

        EmotePicker.Draw($"EmotePicker_{tab.Id}", plugin.EmoteService, ref emoteSearch, code =>
        {
            inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + code + " ";
        });

        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(tab, inputText);
            inputText = string.Empty;
            refocusInput = true;
        }
    }

    /// <summary>Marks a tab's unread counter - called by <see cref="Plugin"/> for every incoming
    /// message regardless of selection, since even the open tab may be scrolled away from the
    /// bottom; the granular visibility tracking in <see cref="DrawContent"/> brings it back down as
    /// messages actually scroll into view.</summary>
    public void NotifyUnread(ChatTabConfig tab) => tab.UnreadCount++;

    /// <summary>Switches the sidebar selection to this tab - e.g. when the right-click "Send Tell"
    /// menu item opens a whisper conversation.</summary>
    public void SelectTab(Guid tabId) => selectedTabId = tabId;

    private ChatTabConfig? ResolveSelectedTab()
    {
        if (selectedTabId != null)
        {
            foreach (var tab in plugin.TabManager.Tabs)
            {
                if (tab.Id == selectedTabId && !tab.IsDetached)
                    return tab;
            }

            selectedTabId = null;
        }

        foreach (var tab in plugin.TabManager.Tabs)
        {
            if (!tab.IsDetached)
            {
                selectedTabId = tab.Id;
                return tab;
            }
        }

        return null;
    }

    public void Dispose()
    {
    }
}
