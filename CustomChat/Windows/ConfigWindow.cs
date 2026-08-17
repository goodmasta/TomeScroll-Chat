using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;
using CustomChat.Services;
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
    private string playerColorNicknameInput = string.Empty;

    // Cached, not recomputed every frame - EstimateAverageBytesPerMessage queries the actual database,
    // so this is throttled rather than hit on every single draw while the slider's just sitting there.
    private double? avgBytesPerMessageCache;
    private DateTime avgBytesPerMessageCacheTime;

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

        using (var players = ImRaii.TabItem("Players"))
        {
            if (players.Success)
                DrawPlayerColors();
        }

        using (var ai = ImRaii.TabItem("AI"))
        {
            if (ai.Success)
                DrawAi();
        }

        using (var notifications = ImRaii.TabItem("Notifications"))
        {
            if (notifications.Success)
                DrawNotifications();
        }

        using (var emotes = ImRaii.TabItem("Emotes"))
        {
            if (emotes.Success)
                DrawEmotes();
        }
    }

    /// <summary>Every "tell me about something" setting in one place: the OS-level tray balloon
    /// (<see cref="Services.WindowsNotificationService"/>), the in-game popup toast
    /// (<see cref="Services.NotificationService"/>), and the default unread blink/count colours tabs
    /// fall back to when they haven't set their own (see Tabs > "Notification colours").</summary>
    private void DrawNotifications()
    {
        var notifyWhisperInWindows = configuration.NotifyWhisperInWindows;
        if (ImGui.Checkbox("Show a Windows notification on incoming tells", ref notifyWhisperInWindows))
        {
            configuration.NotifyWhisperInWindows = notifyWhisperInWindows;
            configuration.Save();
            plugin.WindowsNotificationService.Enabled = notifyWhisperInWindows;
        }
        ImGui.TextDisabled("Needs a small system tray icon to stay present while this is on - a Windows requirement for showing notifications.");

        ImGui.Spacing();
        if (ImGui.Button("Test popup notification"))
            plugin.NotificationService.Show("This is a test notification.", NotificationSeverity.Info);
        ImGui.TextDisabled("A small popup in the top-right corner of the game window itself, not the Windows tray notification above - general-purpose, used to inform you of things as this plugin grows more features.");

        ImGui.Spacing();
        var notifyOnInvalidCommand = configuration.NotifyOnInvalidCommand;
        if (ImGui.Checkbox("Popup when a typed command doesn't exist", ref notifyOnInvalidCommand))
        {
            configuration.NotifyOnInvalidCommand = notifyOnInvalidCommand;
            configuration.Save();
        }
        ImGui.TextDisabled("Easy to miss the game's own \"command does not exist\" error otherwise, especially with the native chat window hidden (General) or no tab showing the Error channel.");

        ImGui.Separator();
        ImGui.TextUnformatted("Unread indicator colours");

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
    }

    /// <summary>AI agent configuration - currently just Gemini (<see cref="Services.GeminiService"/>),
    /// kept on its own tab rather than folded into General since it's not translation-specific: any
    /// future AI-backed feature reuses the same key/model configured here, via
    /// <see cref="Plugin.GeminiService"/> directly. The "Translation engine" picker itself (which
    /// backend translation actually uses, Gemini being one option) stays in General's Translation
    /// section, next to the target-language picker it belongs with.</summary>
    private void DrawAi()
    {
        ImGui.TextUnformatted("Gemini");
        ImGui.TextDisabled("Powers the \"Gemini\" translation engine (General > Translation) - and, going forward, any other AI-backed feature added to this plugin, all sharing this same key/model.");
        ImGui.Spacing();

        var geminiKey = configuration.GeminiApiKey;
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("API key##geminiKey", ref geminiKey, 200, ImGuiInputTextFlags.Password))
        {
            configuration.GeminiApiKey = geminiKey;
            configuration.Save();
        }
        ImGui.TextDisabled("From Google AI Studio (aistudio.google.com/apikey). Stored in this plugin's own config file, nowhere else.");

        var models = GeminiModelCatalog.Entries;
        var currentModelIndex = Array.FindIndex(models, m => m.Id == configuration.GeminiModel);
        var currentModelLabel = currentModelIndex >= 0 ? models[currentModelIndex].Label : configuration.GeminiModel;

        ImGui.SetNextItemWidth(280);
        if (ImGui.BeginCombo("Model##geminiModel", currentModelLabel))
        {
            foreach (var (id, label) in models)
            {
                if (ImGui.Selectable(label, id == configuration.GeminiModel))
                {
                    configuration.GeminiModel = id;
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Curated list of plain text-generation models from ai.google.dev/gemini-api/docs/models - image/video/audio/agent-specific models aren't listed here since this plugin only ever sends/expects plain text.");

        ImGui.Spacing();
        ImGui.TextUnformatted(plugin.GeminiService.IsConfigured ? "Status: configured." : "Status: no API key set.");
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

        var autoLinkshellTabs = configuration.AutoLinkshellTabs;
        if (ImGui.Checkbox("Auto-create a tab per joined linkshell/cross-world linkshell", ref autoLinkshellTabs))
        {
            configuration.AutoLinkshellTabs = autoLinkshellTabs;
            configuration.Save();
            if (!autoLinkshellTabs)
                plugin.TabManager.RemoveAllAutoLinkshellTabs();
        }
        ImGui.TextDisabled("A tab appears the moment you join one, disappears the moment you leave/get kicked. Turning this off removes them immediately.");

        var fadeInactive = configuration.FadeWindowWhenInactive;
        if (ImGui.Checkbox("Fade the chat window when it's not focused", ref fadeInactive))
        {
            configuration.FadeWindowWhenInactive = fadeInactive;
            configuration.Save();
        }

        using (ImRaii.Disabled(!configuration.FadeWindowWhenInactive))
        {
            var inactiveAlpha = configuration.InactiveWindowAlpha;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Inactive opacity", ref inactiveAlpha, 0.05f, 1f, "%.2f"))
            {
                configuration.InactiveWindowAlpha = inactiveAlpha;
                configuration.Save();
            }
        }

        var showHideButton = configuration.ShowHideChatButton;
        if (ImGui.Checkbox("Show a hide-chat (eye) button in the title bar", ref showHideButton))
        {
            configuration.ShowHideChatButton = showHideButton;
            configuration.Save();
        }
        ImGui.TextDisabled("Collapses the chat down to just the title bar - stronger than the fade above. The button stays visible while collapsed, so it's always how to bring it back.");

        var autoHide = configuration.AutoHideChatWhenInactive;
        if (ImGui.Checkbox("Automatically hide the chat after being inactive", ref autoHide))
        {
            configuration.AutoHideChatWhenInactive = autoHide;
            configuration.Save();
        }

        using (ImRaii.Disabled(!configuration.AutoHideChatWhenInactive))
        {
            var autoHideSeconds = configuration.AutoHideChatSeconds;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputFloat("Seconds of inactivity", ref autoHideSeconds, 1f, 10f, "%.0f"))
            {
                configuration.AutoHideChatSeconds = Math.Clamp(autoHideSeconds, 1f, 3600f);
                configuration.Save();
            }
        }
        ImGui.TextDisabled("Off by default. \"Inactive\" means the chat window itself isn't focused, regardless of what else you're doing in-game.");

        var openLinks = configuration.OpenLinksOnClick;
        if (ImGui.Checkbox("Open links in the browser on click", ref openLinks))
        {
            configuration.OpenLinksOnClick = openLinks;
            configuration.Save();
        }

        var hideDuringCutscenes = configuration.HideChatDuringCutscenes;
        if (ImGui.Checkbox("Hide chat during cutscenes", ref hideDuringCutscenes))
        {
            configuration.HideChatDuringCutscenes = hideDuringCutscenes;
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

        ImGui.Spacing();

        var engineNames = new[] { "Google (free)", "MyMemory (free)", "Gemini" };
        var engineIndex = (int)configuration.TranslationEngine;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Translation engine", ref engineIndex, engineNames, engineNames.Length))
        {
            configuration.TranslationEngine = (TranslationEngine)engineIndex;
            configuration.Save();
        }
        ImGui.TextDisabled("Google (free): the default, no key needed - automatically detours through MyMemory for a while after a few requests fail in a row (likely a rate limit), then goes back to trying Google again. MyMemory: always uses that one directly. Gemini: needs an API key below.");

        if (configuration.TranslationEngine == TranslationEngine.Gemini && !plugin.GeminiService.IsConfigured)
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.3f, 1f), "Gemini is selected but no API key is set - see the AI tab.");

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

        if (avgBytesPerMessageCache == null || DateTime.UtcNow - avgBytesPerMessageCacheTime > TimeSpan.FromSeconds(10))
        {
            avgBytesPerMessageCache = plugin.ChatHistoryService.EstimateAverageBytesPerMessage();
            avgBytesPerMessageCacheTime = DateTime.UtcNow;
        }

        var estimatedMessages = (long)(configuration.MaxHistoryBytes / avgBytesPerMessageCache.Value);
        ImGui.TextDisabled($"≈ {estimatedMessages:N0} messages at this size (based on your own average message size so far, ~{avgBytesPerMessageCache.Value:N0} bytes/message - a generic guess until there's real history to measure).");

        ImGui.Spacing();
        if (ImGui.Button("Clear history..."))
            ImGui.OpenPopup("CustomChatClearHistoryConfirm");
        ImGui.TextDisabled("Permanently deletes all stored chat history for every tab and whisper. Cannot be undone.");

        ImGui.Spacing();
        if (ImGui.Button("Reset settings to defaults..."))
            ImGui.OpenPopup("CustomChatResetSettingsConfirm");
        ImGui.TextDisabled("Resets every setting on this page and the Emotes tab. Tabs (channels, colours, filters) are left alone.");

        DrawClearHistoryConfirmPopup();
        DrawResetSettingsConfirmPopup();
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

    private void DrawResetSettingsConfirmPopup()
    {
        var open = true;
        if (!ImGui.BeginPopupModal("CustomChatResetSettingsConfirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted("Reset all settings to their defaults?");
        ImGui.TextDisabled("Tabs, whisper history, and emote cache are not affected - only preferences on this\npage and the Emotes tab (colours, font size, translation language, notifications, etc).");
        ImGui.Spacing();

        if (ImGui.Button("Yes, reset everything"))
        {
            plugin.ResetSettingsToDefaults();
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
        if (tab.IsAutoLinkshellTab)
            ImGui.TextDisabled($"Auto-managed ({(tab.IsCrossWorldLinkshell ? "cross-world linkshell" : "linkshell")} - see Settings > General). Colours below are still yours to customize.");

        var name = tab.Name;
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputText($"Name##name_{tab.Id}", ref name, 64))
        {
            tab.Name = name;
            plugin.TabManager.Save();
        }

        // Sidebar order - within this tab's own group (whisper vs. regular), see TabManager.MoveTab.
        using (ImRaii.Disabled(!plugin.TabManager.CanMoveTab(tab, -1)))
        {
            if (ImGui.SmallButton($"Move up##moveup_{tab.Id}"))
                plugin.TabManager.MoveTab(tab, -1);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!plugin.TabManager.CanMoveTab(tab, 1)))
        {
            if (ImGui.SmallButton($"Move down##movedown_{tab.Id}"))
                plugin.TabManager.MoveTab(tab, 1);
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
        ImGui.TextUnformatted("Sidebar name colour (this tab)");

        var tabColor = tab.TabColorOverride ?? Vector4.One;
        if (ImGui.ColorEdit4($"Tab colour##tabcolor_{tab.Id}", ref tabColor))
        {
            tab.TabColorOverride = tabColor;
            plugin.TabManager.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!tab.TabColorOverride.HasValue))
        {
            if (ImGui.SmallButton($"Use default##tabcolorreset_{tab.Id}"))
            {
                tab.TabColorOverride = null;
                plugin.TabManager.Save();
            }
        }
        if (tab.IsPmTab)
            ImGui.TextDisabled("Overrides the by-nickname colour set in Settings > Players, if any.");

        var bodyColor = tab.MessageTextColorOverride ?? Vector4.One;
        if (ImGui.ColorEdit4($"Message text colour##bodycolor_{tab.Id}", ref bodyColor))
        {
            tab.MessageTextColorOverride = bodyColor;
            plugin.TabManager.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!tab.MessageTextColorOverride.HasValue))
        {
            if (ImGui.SmallButton($"Use default##bodycolorreset_{tab.Id}"))
            {
                tab.MessageTextColorOverride = null;
                plugin.TabManager.Save();
            }
        }
        ImGui.TextDisabled("Overrides the per-channel colours below for every message body in this tab.");

        ImGui.Spacing();

        var mute = tab.MuteUnreadIndicator;
        if (ImGui.Checkbox($"Mute unread indicator##mute_{tab.Id}", ref mute))
        {
            tab.MuteUnreadIndicator = mute;
            plugin.TabManager.Save();
        }
        ImGui.TextDisabled("No \"(N)\" count or blink in the sidebar for this tab, even for whisper tabs (which otherwise always notify). Messages are still counted, just not shown.");

        var autoTranslate = tab.AutoTranslate;
        if (ImGui.Checkbox($"Auto-translate all messages in this tab##autotranslate_{tab.Id}", ref autoTranslate))
        {
            tab.AutoTranslate = autoTranslate;
            plugin.TabManager.Save();
        }
        ImGui.TextDisabled($"Translates every message into \"{configuration.TranslateTargetLanguage}\" (Settings > General) automatically - same as picking \"Translate\" by hand per message, just for all of them. Only togglable here, not per-message or from the sidebar.");

        var disableLogging = tab.DisableLogging;
        if (ImGui.Checkbox($"Don't save this tab's messages to disk##disablelogging_{tab.Id}", ref disableLogging))
        {
            tab.DisableLogging = disableLogging;
            plugin.TabManager.Save();
        }
        ImGui.TextDisabled("Messages still show up normally for the rest of this session - they just won't be written to history, so they won't survive a reload/restart and won't be included in \"Export to file\". Already-saved history isn't deleted by turning this on.");

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
            ImGui.TextDisabled("e.g. /p, /fc, /s - what messages typed in this tab are sent as. Left empty, it auto-fills with the first channel checked below the moment there is one (or the compose box is disabled if this tab has none you can write to) - same picker as the \"Sending to\" label above the compose box.");

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
        if (ImGui.Button($"Export to file...##export_{tab.Id}"))
            plugin.ExportTabToFile(tab);

        ImGui.SameLine();
        if (tab.IsPmTab)
        {
            // Whisper history is keyed by conversation partner, not this tab's id, so this never
            // deletes any messages - it just hides the tab until the next message reopens it.
            if (ImGui.Button($"Close chat##close_{tab.Id}"))
                plugin.TabManager.RemoveTab(tab);
        }
        else if (tab.IsAutoLinkshellTab)
        {
            // Deleting here would just reappear within ~1s (SyncAutoLinkshellTabs still sees the
            // player as a member) - the real "delete" is leaving the shell in-game, or turning the
            // whole feature off in Settings > General.
            ImGui.TextDisabled("Leave the linkshell in-game to remove this tab.");
        }
        else
        {
            if (ImGui.Button($"Delete##delete_{tab.Id}"))
                plugin.TabManager.RemoveTab(tab);
        }
    }

    /// <summary>Same by-nickname colour presets a message's right-click menu writes to (see
    /// <see cref="ChatMessageRenderer"/>'s "Set Tab Colour"/"Set Message Colour"), editable here
    /// directly by typing a "Name@World" - lets a colour be set for someone before they've ever sent
    /// a message, e.g. planning ahead for an FC member.</summary>
    private void DrawPlayerColors()
    {
        ImGui.TextDisabled("Set a sidebar tab colour and/or chat name colour for a player by nickname - the same thing a message's right-click menu does for whoever sent it.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##playerColorNickname", "Name@World", ref playerColorNicknameInput, 64);

        var key = playerColorNicknameInput.Trim();
        var hasNickname = key.Length > 0;
        using (ImRaii.Disabled(!hasNickname))
        {
            var tabColor = (hasNickname && configuration.PlayerTabColors.TryGetValue(key, out var existingTabColor)) ? existingTabColor : Vector4.One;
            ImGui.SetNextItemWidth(200);
            if (ImGui.ColorEdit4("Tab colour##newplayertab", ref tabColor) && hasNickname)
            {
                configuration.PlayerTabColors[key] = tabColor;
                configuration.Save();
            }

            var msgColor = (hasNickname && configuration.PlayerMessageColors.TryGetValue(key, out var existingMsgColor)) ? existingMsgColor : Vector4.One;
            ImGui.SetNextItemWidth(200);
            if (ImGui.ColorEdit4("Message colour##newplayermsg", ref msgColor) && hasNickname)
            {
                configuration.PlayerMessageColors[key] = msgColor;
                configuration.Save();
            }
        }

        ImGui.Separator();

        var keys = configuration.PlayerTabColors.Keys.Concat(configuration.PlayerMessageColors.Keys).Distinct().OrderBy(k => k).ToList();
        ImGui.TextUnformatted($"Configured players ({keys.Count})");
        using (var child = ImRaii.Child("PlayerColorsList", new Vector2(0, 220), true))
        {
            if (child.Success)
            {
                foreach (var playerKey in keys)
                {
                    ImGui.TextUnformatted(playerKey);

                    if (configuration.PlayerTabColors.TryGetValue(playerKey, out var tc))
                    {
                        ImGui.SameLine();
                        ImGui.ColorButton($"##tabswatch_{playerKey}", tc, ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16));
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Clear tab colour##cleartab_{playerKey}"))
                        {
                            configuration.PlayerTabColors.Remove(playerKey);
                            configuration.Save();
                        }
                    }

                    if (configuration.PlayerMessageColors.TryGetValue(playerKey, out var mc))
                    {
                        ImGui.SameLine();
                        ImGui.ColorButton($"##msgswatch_{playerKey}", mc, ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16));
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Clear message colour##clearmsg_{playerKey}"))
                        {
                            configuration.PlayerMessageColors.Remove(playerKey);
                            configuration.Save();
                        }
                    }
                }
            }
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
