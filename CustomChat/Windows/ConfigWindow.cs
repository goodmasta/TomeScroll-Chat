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
    private string friendMarkerSearch = string.Empty;
    private string tabIconSearch = string.Empty;

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
        ImGui.TextUnformatted("Translation");

        var languages = TranslationLanguageCatalog.Entries;
        var currentIndex = Array.FindIndex(languages, l => l.Code == configuration.TranslateTargetLanguage);
        if (currentIndex < 0)
            currentIndex = 0;

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Translate messages to", languages[currentIndex].Name))
        {
            foreach (var (code, name) in languages)
            {
                if (ImGui.Selectable(name, code == configuration.TranslateTargetLanguage))
                {
                    configuration.TranslateTargetLanguage = code;
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Used by the \"Translate\" item in a message's right-click menu. Source language is always detected automatically.");

        ImGui.Separator();
        ImGui.TextUnformatted("Unread notifications");

        var channelBlink = configuration.ChannelBlinkColor;
        if (ImGui.ColorEdit4("Default channel blink colour", ref channelBlink))
        {
            configuration.ChannelBlinkColor = channelBlink;
            configuration.Save();
        }

        var channelCount = configuration.ChannelUnreadCountColor;
        if (ImGui.ColorEdit4("Default channel unread count colour", ref channelCount))
        {
            configuration.ChannelUnreadCountColor = channelCount;
            configuration.Save();
        }
        ImGui.TextDisabled("Used by tabs with \"Blink + red unread count on new messages\" enabled that haven't set their own colour (see Tabs).");

        var whisperColor = configuration.WhisperNotifyColor;
        if (ImGui.ColorEdit4("Default whisper blink + unread colour", ref whisperColor))
        {
            configuration.WhisperNotifyColor = whisperColor;
            configuration.Save();
        }
        ImGui.TextDisabled("Whisper tabs always blink/show an unread count, and share one colour for both, unless overridden per-tab (see Tabs).");

        ImGui.Separator();

        var friendMarkerEnabled = configuration.FriendMarkerEnabled;
        if (ImGui.Checkbox("Highlight friends with an emote marker", ref friendMarkerEnabled))
        {
            configuration.FriendMarkerEnabled = friendMarkerEnabled;
            configuration.Save();
        }

        using (ImRaii.Disabled(!configuration.FriendMarkerEnabled))
        {
            var markerTexture = string.IsNullOrEmpty(configuration.FriendMarkerEmoji)
                ? null
                : plugin.EmoteService.TryGetTexture(configuration.FriendMarkerEmoji);
            if (markerTexture != null)
            {
                ImGui.Image(markerTexture.Handle, new Vector2(20, 20));
                ImGui.SameLine();
            }

            var label = string.IsNullOrEmpty(configuration.FriendMarkerEmoji)
                ? "Choose marker emote..."
                : $"Marker: {configuration.FriendMarkerEmoji}";
            if (ImGui.Button($"{label}##friendMarkerPicker"))
            {
                friendMarkerSearch = string.Empty;
                ImGui.OpenPopup("FriendMarkerPicker");
            }
        }

        EmotePicker.Draw("FriendMarkerPicker", plugin.EmoteService, ref friendMarkerSearch, code =>
        {
            configuration.FriendMarkerEmoji = code;
            configuration.Save();
        });

        ImGui.TextDisabled("A real emote image (not a text symbol) shown before the name of any sender who's on your friends list.");

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

        ImGui.Spacing();
        if (ImGui.Button("Clear history..."))
            ImGui.OpenPopup("CustomChatClearHistoryConfirm");
        ImGui.TextDisabled("Permanently deletes all stored chat history for every tab and whisper. Cannot be undone.");

        DrawClearHistoryConfirmPopup();
    }

    private void DrawClearHistoryConfirmPopup()
    {
        var open = true;
        if (!ImGui.BeginPopupModal("CustomChatClearHistoryConfirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted("Delete all stored chat history?");
        ImGui.TextDisabled("This removes every saved message for every tab and whisper conversation.\nThis cannot be undone.");
        ImGui.Spacing();

        if (ImGui.Button("Yes, delete everything"))
        {
            plugin.ClearAllHistory();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
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

        // A real emote image next to the tab name in the sidebar, not literal Unicode text - typing
        // an emoji character into the Name field above wouldn't render (Dalamud's UI font has no
        // colour-emoji glyphs), same reasoning as the friend marker in Settings > General.
        var iconTexture = string.IsNullOrEmpty(tab.IconEmoji) ? null : plugin.EmoteService.TryGetTexture(tab.IconEmoji);
        if (iconTexture != null)
        {
            ImGui.Image(iconTexture.Handle, new Vector2(20, 20));
            ImGui.SameLine();
        }

        var iconLabel = string.IsNullOrEmpty(tab.IconEmoji) ? "Choose tab icon..." : $"Icon: {tab.IconEmoji}";
        if (ImGui.Button($"{iconLabel}##tabIconPicker_{tab.Id}"))
        {
            tabIconSearch = string.Empty;
            ImGui.OpenPopup($"TabIconPicker_{tab.Id}");
        }

        EmotePicker.Draw($"TabIconPicker_{tab.Id}", plugin.EmoteService, ref tabIconSearch, code =>
        {
            tab.IconEmoji = code;
            plugin.TabManager.Save();
        });

        if (!string.IsNullOrEmpty(tab.IconEmoji))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear icon##clearIcon_{tab.Id}"))
            {
                tab.IconEmoji = null;
                plugin.TabManager.Save();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Notification colours (this tab)");

        var defaultBlink = tab.IsPmTab ? configuration.WhisperNotifyColor : configuration.ChannelBlinkColor;
        var blink = tab.BlinkColorOverride ?? defaultBlink;
        if (ImGui.ColorEdit4($"Blink colour##blink_{tab.Id}", ref blink))
        {
            tab.BlinkColorOverride = blink;
            plugin.TabManager.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!tab.BlinkColorOverride.HasValue))
        {
            if (ImGui.SmallButton($"Use default##blinkreset_{tab.Id}"))
            {
                tab.BlinkColorOverride = null;
                plugin.TabManager.Save();
            }
        }

        var defaultCount = tab.IsPmTab ? configuration.WhisperNotifyColor : configuration.ChannelUnreadCountColor;
        var count = tab.UnreadCountColorOverride ?? defaultCount;
        if (ImGui.ColorEdit4($"Unread count colour##count_{tab.Id}", ref count))
        {
            tab.UnreadCountColorOverride = count;
            plugin.TabManager.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!tab.UnreadCountColorOverride.HasValue))
        {
            if (ImGui.SmallButton($"Use default##countreset_{tab.Id}"))
            {
                tab.UnreadCountColorOverride = null;
                plugin.TabManager.Save();
            }
        }

        ImGui.Spacing();

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

                                if (!enabled)
                                    continue;

                                // A compact colour swatch (no inline text/label - the checkbox already
                                // has one) for this specific message type, only shown once the channel
                                // is actually part of the tab. Pre-filled from the existing override, or
                                // the built-in default colour when there isn't one yet.
                                ImGui.SameLine();
                                var hasOverride = tab.ColorOverrides.TryGetValue(type, out var packed);
                                var color = hasOverride ? ImGui.ColorConvertU32ToFloat4(packed) : ChatMessageRenderer.GetDefaultColor(type);
                                if (ImGui.ColorEdit4($"##colorch_{tab.Id}_{type}", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaPreview))
                                {
                                    tab.ColorOverrides[type] = ImGui.ColorConvertFloat4ToU32(color);
                                    plugin.TabManager.Save();
                                }

                                if (hasOverride)
                                {
                                    ImGui.SameLine();
                                    if (ImGui.SmallButton($"Reset##colorreset_{tab.Id}_{type}"))
                                    {
                                        tab.ColorOverrides.Remove(type);
                                        plugin.TabManager.Save();
                                    }
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

            var notify = tab.NotifyOnNewMessage;
            if (ImGui.Checkbox($"Blink + red unread count on new messages##notify_{tab.Id}", ref notify))
            {
                tab.NotifyOnNewMessage = notify;
                plugin.TabManager.Save();
            }
        }
        else
        {
            ImGui.TextDisabled("Whisper tabs always blink and show a red unread count - not optional here.");
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
            plugin.ForceRefreshEmotes();

        ImGui.TextDisabled("Only global emote sets are loaded in this version - per-channel Twitch emotes are not yet supported.");

        ImGui.Separator();

        var loaded = plugin.EmoteService.GetLoadedEmotes();
        ImGui.TextUnformatted($"Loaded emotes ({loaded.Count})");
        using (var child = ImRaii.Child("LoadedEmotesList", new Vector2(0, 200), true))
        {
            if (child.Success)
            {
                foreach (var emote in loaded)
                    ImGui.TextUnformatted($"{emote.Code}  [{emote.Provider}]");
            }
        }
    }

    public void Dispose()
    {
    }
}
