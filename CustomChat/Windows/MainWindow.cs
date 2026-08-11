using System;
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

    // Discord-style "last read position": which tab the content area is currently showing, a frozen
    // divider index into that tab's message list (set once when switching in, not updated as the
    // player reads further down - see DrawContent), and one-shot scroll requests.
    private Guid? contentTabId;
    private int dividerIndex = -1;
    private bool pendingScrollToDivider;
    private bool pendingScrollToBottom;

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
        Flags |= ImGuiWindowFlags.NoCollapse;
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

        // Whisper tabs share one colour for both blink and count; regular tabs have them configured
        // separately (both settings in Settings > General, both default to red).
        var config = Plugin.Configuration;
        var blinkColor = tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelBlinkColor;
        var countColor = tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelUnreadCountColor;

        var isBlinking = tab.UnreadCount > 0 && tab.ShouldNotify;
        var nameColor = isBlinking
            ? Vector4.Lerp(BlinkBase, blinkColor, (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f)
            : BlinkBase;

        var namePos = new Vector2(itemMin.X + 4, textY);
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
                if (pendingScrollToBottom)
                {
                    ImGui.SetScrollY(ImGui.GetScrollMaxY());
                    pendingScrollToBottom = false;
                    dividerIndex = -1;
                }

                var messages = plugin.TabMessageBuffer.GetMessages(tab);
                var lastVisible = ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.OpenTellToKey, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider);
                pendingScrollToDivider = false;

                // Unread count shrinks as messages actually scroll into view, not all at once on open.
                if (lastVisible >= 0)
                {
                    var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                    if (newUnread < tab.UnreadCount)
                        tab.UnreadCount = newUnread;
                }

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                    ImGui.SetScrollHereY(1f);
            }
        }

        if (ImGui.Button("Jump to bottom"))
            pendingScrollToBottom = true;

        DrawInputRow(tab);
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
