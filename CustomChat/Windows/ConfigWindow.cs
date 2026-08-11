using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;
using CustomChat.Utility;

namespace CustomChat.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private Guid? focusedTabId;
    private string newTabName = string.Empty;

    public ConfigWindow(Plugin plugin)
        : base("Custom Chat Settings###CustomChatConfigWindow")
    {
        this.plugin = plugin;
        configuration = Plugin.Configuration;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void FocusTab(Guid tabId) => focusedTabId = tabId;

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("CustomChatSettingsTabs");
        if (!tabs.Success)
            return;

        using (var general = ImRaii.TabItem("General"))
        {
            if (general.Success)
                DrawGeneral();
        }

        using (var tabsTab = ImRaii.TabItem("Tabs"))
        {
            if (tabsTab.Success)
                DrawTabsEditor();
        }

        using (var emotes = ImRaii.TabItem("Emotes"))
        {
            if (emotes.Success)
                DrawEmotes();
        }
    }

    private void DrawGeneral()
    {
        var hideNative = configuration.HideNativeChat;
        if (ImGui.Checkbox("Hide the game's built-in chat window", ref hideNative))
        {
            configuration.HideNativeChat = hideNative;
            configuration.Save();
            plugin.ApplyNativeChatHidden();
        }

        var separateWhispers = configuration.OpenWhispersInSeparateWindow;
        if (ImGui.Checkbox("Open new whispers in a separate floating window", ref separateWhispers))
        {
            configuration.OpenWhispersInSeparateWindow = separateWhispers;
            configuration.Save();
        }

        var screenshotMode = configuration.ScreenshotMode;
        if (ImGui.Checkbox("Screenshot mode (hide player names)", ref screenshotMode))
        {
            configuration.ScreenshotMode = screenshotMode;
            configuration.Save();
        }

        var openLinks = configuration.OpenLinksOnClick;
        if (ImGui.Checkbox("Open links in the browser on click", ref openLinks))
        {
            configuration.OpenLinksOnClick = openLinks;
            configuration.Save();
        }

        ImGui.Separator();

        var fontSize = configuration.FontSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Font size", ref fontSize, 10f, 24f, "%.0f"))
        {
            configuration.FontSize = fontSize;
            configuration.Save();
        }

        ImGui.Separator();

        var maxHistoryMb = (int)(configuration.MaxHistoryBytes / (1024 * 1024));
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Max history on disk (MiB)", ref maxHistoryMb, 64, 4096))
        {
            configuration.MaxHistoryBytes = (long)maxHistoryMb * 1024 * 1024;
            configuration.Save();
            plugin.ChatHistoryService.SetMaxBytes(configuration.MaxHistoryBytes);
        }
        ImGui.TextDisabled("Oldest messages are deleted first once this limit is exceeded (emote image cache is separate and not counted here).");
    }

    private void DrawTabsEditor()
    {
        ImGui.TextDisabled("Right-click a tab in the main window to pop it out or jump here; create/delete tabs below.");

        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##newTab", "New tab name...", ref newTabName, 64);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newTabName)))
        {
            if (ImGui.Button("Add tab"))
            {
                var tab = plugin.TabManager.CreateTab(newTabName.Trim());
                newTabName = string.Empty;
                focusedTabId = tab.Id;
            }
        }

        ImGui.Separator();

        foreach (var tab in plugin.TabManager.Tabs.ToList())
        {
            var open = focusedTabId == tab.Id;
            if (open)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            using var node = ImRaii.TreeNode($"{tab.Name}{(tab.IsPmTab ? " (whisper)" : "")}###tabnode_{tab.Id}");
            if (!node.Success)
                continue;

            if (focusedTabId == tab.Id)
                focusedTabId = null;

            DrawTabEditor(tab);
        }
    }

    private void DrawTabEditor(ChatTabConfig tab)
    {
        var name = tab.Name;
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputText($"Name##name_{tab.Id}", ref name, 64))
        {
            tab.Name = name;
            plugin.TabManager.Save();
        }

        if (!tab.IsPmTab)
        {
            using (var child = ImRaii.Child($"channels_{tab.Id}", new Vector2(0, 220), true))
            {
                if (child.Success)
                {
                    foreach (var group in ChatChannelCatalog.Groups)
                    {
                        if (ImGui.CollapsingHeader($"{group.Title}###group_{tab.Id}_{group.Title}"))
                        {
                            foreach (var (type, label) in group.Channels)
                            {
                                var enabled = tab.Channels.Contains(type);
                                if (ImGui.Checkbox($"{label}##ch_{tab.Id}_{type}", ref enabled))
                                {
                                    if (enabled)
                                        tab.Channels.Add(type);
                                    else
                                        tab.Channels.Remove(type);
                                    plugin.TabManager.Save();
                                }
                            }
                        }
                    }
                }
            }

            var filterModeIndex = (int)tab.FilterMode;
            var filterModes = new[] { "No extra filter", "Keyword contains", "Regex" };
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo($"Extra filter##filter_{tab.Id}", ref filterModeIndex, filterModes, filterModes.Length))
            {
                tab.FilterMode = (ChatTabFilterMode)filterModeIndex;
                plugin.TabManager.Save();
            }

            if (tab.FilterMode != ChatTabFilterMode.None)
            {
                var pattern = tab.FilterPattern;
                ImGui.SetNextItemWidth(300);
                if (ImGui.InputText($"Pattern##pattern_{tab.Id}", ref pattern, 200))
                {
                    tab.FilterPattern = pattern;
                    plugin.TabManager.Save();
                }
            }

            var outgoing = tab.OutgoingChannelCommand;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputText($"Outgoing command##out_{tab.Id}", ref outgoing, 32))
            {
                tab.OutgoingChannelCommand = outgoing;
                plugin.TabManager.Save();
            }
            ImGui.TextDisabled("e.g. /p, /fc, /s - what messages typed in this tab are sent as. Empty = plain text (uses the game's current default channel).");
        }

        if (tab.IsDetached)
        {
            if (ImGui.Button($"Reattach to main window##reattach_{tab.Id}"))
                plugin.SetTabDetached(tab, false);
        }
        else
        {
            if (ImGui.Button($"Pop out to floating window##popout_{tab.Id}"))
                plugin.SetTabDetached(tab, true);
        }

        ImGui.SameLine();
        if (tab.IsPmTab)
        {
            // Whisper history is keyed by conversation partner, not this tab's id, so this never
            // deletes any messages - it just hides the tab until the next message reopens it.
            if (ImGui.Button($"Close chat##close_{tab.Id}"))
                plugin.TabManager.RemoveTab(tab);
        }
        else
        {
            if (ImGui.Button($"Delete##delete_{tab.Id}"))
                plugin.TabManager.RemoveTab(tab);
        }
    }

    private void DrawEmotes()
    {
        var bttv = configuration.BttvEnabled;
        if (ImGui.Checkbox("BetterTTV (BTTV) global emotes", ref bttv))
        {
            configuration.BttvEnabled = bttv;
            configuration.Save();
        }

        var sevenTv = configuration.SevenTvEnabled;
        if (ImGui.Checkbox("7TV global emotes", ref sevenTv))
        {
            configuration.SevenTvEnabled = sevenTv;
            configuration.Save();
        }

        var scale = configuration.EmoteScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Emote size scale", ref scale, 0.5f, 3f, "%.1f"))
        {
            configuration.EmoteScale = scale;
            configuration.Save();
        }

        var ttl = configuration.EmoteCacheTtlHours;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Cache refresh interval (hours)", ref ttl, 1, 168))
        {
            configuration.EmoteCacheTtlHours = ttl;
            configuration.Save();
        }

        if (ImGui.Button("Refresh emotes now"))
            plugin.RefreshEmotes();

        ImGui.TextDisabled("Only global emote sets are loaded in this version - per-channel Twitch emotes are not yet supported.");
    }

    public void Dispose()
    {
    }
}
