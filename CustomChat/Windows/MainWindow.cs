using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;

namespace CustomChat.Windows;

/// <summary>The main chat window: a sidebar of every non-detached tab plus the selected tab's
/// messages and input box. Detached tabs render in their own <see cref="DetachedTabWindow"/> instead.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Guid? selectedTabId;
    private string inputText = string.Empty;
    private string newTabName = string.Empty;

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
                foreach (var tab in plugin.TabManager.Tabs)
                {
                    if (tab.IsDetached)
                        continue;

                    var label = tab.UnreadCount > 0 ? $"{tab.Name} ({tab.UnreadCount})" : tab.Name;
                    var selected = tab.Id == selectedTabId;
                    if (ImGui.Selectable($"{label}##tab_{tab.Id}", selected))
                    {
                        selectedTabId = tab.Id;
                        tab.UnreadCount = 0;
                    }

                    if (ImGui.BeginPopupContextItem($"ctx_{tab.Id}"))
                    {
                        DrawTabContextMenu(tab);
                        ImGui.EndPopup();
                    }
                }
            }
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##newTab", "New tab...", ref newTabName, 64);
        if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(newTabName))
        {
            var tab = plugin.TabManager.CreateTab(newTabName.Trim());
            selectedTabId = tab.Id;
            newTabName = string.Empty;
        }
    }

    private void DrawTabContextMenu(ChatTabConfig tab)
    {
        if (ImGui.MenuItem("Pop out to floating window"))
            plugin.SetTabDetached(tab, true);

        if (ImGui.MenuItem("Edit channels/filter..."))
            plugin.OpenTabEditor(tab.Id);

        ImGui.Separator();
        if (ImGui.MenuItem("Delete tab"))
        {
            plugin.TabManager.RemoveTab(tab);
            if (selectedTabId == tab.Id)
                selectedTabId = null;
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
                ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService);

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
