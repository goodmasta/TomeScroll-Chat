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
    private static readonly Vector4 UnreadRed = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 BlinkBase = new(1f, 1f, 1f, 1f);

    private readonly Plugin plugin;
    private Guid? selectedTabId;
    private string inputText = string.Empty;

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
        {
            selectedTabId = tab.Id;
            tab.UnreadCount = 0;
        }

        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var textY = itemMin.Y + (itemMax.Y - itemMin.Y - ImGui.GetTextLineHeight()) / 2f;
        var drawList = ImGui.GetWindowDrawList();

        var isBlinking = tab.UnreadCount > 0 && tab.ShouldNotify;
        var nameColor = isBlinking
            ? Vector4.Lerp(BlinkBase, UnreadRed, (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f)
            : BlinkBase;

        var namePos = new Vector2(itemMin.X + 4, textY);
        drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(nameColor), tab.Name);

        if (tab.UnreadCount > 0)
        {
            var countPos = new Vector2(namePos.X + ImGui.CalcTextSize(tab.Name).X + 4, textY);
            drawList.AddText(countPos, ImGui.ColorConvertFloat4ToU32(UnreadRed), $"({tab.UnreadCount})");
        }
    }

    private void DrawTabContextMenu(ChatTabConfig tab)
    {
        using (ImRaii.Disabled(tab.UnreadCount == 0))
        {
            if (ImGui.MenuItem("Mark all as read"))
                tab.UnreadCount = 0;
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

        using (var child = ImRaii.Child("Messages", new Vector2(0, -28), false))
        {
            if (child.Success)
            {
                var messages = plugin.TabMessageBuffer.GetMessages(tab);
                ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.OpenTellToKey, Plugin.GetLocalPlayerKey());

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                    ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.SetNextItemWidth(-1);
        var send = ImGui.InputText($"##input_{tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(tab, inputText);
            inputText = string.Empty;
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    /// <summary>Marks a tab's unread counter without switching to it - called by <see cref="Plugin"/> when a
    /// message lands in a tab that isn't currently selected/visible.</summary>
    public void NotifyUnread(ChatTabConfig tab)
    {
        if (tab.Id != selectedTabId || !IsOpen)
            tab.UnreadCount++;
    }

    /// <summary>Switches the sidebar selection to this tab (clearing its unread count) - e.g. when the
    /// right-click "Send Tell" menu item opens a whisper conversation.</summary>
    public void SelectTab(Guid tabId)
    {
        selectedTabId = tabId;
        foreach (var tab in plugin.TabManager.Tabs)
        {
            if (tab.Id == tabId)
            {
                tab.UnreadCount = 0;
                break;
            }
        }
    }

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
