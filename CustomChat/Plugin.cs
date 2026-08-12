using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using CustomChat.Models;
using CustomChat.Services;
using CustomChat.Windows;

namespace CustomChat;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;

    private const string CommandName = "/customchat";

    internal static Configuration Configuration { get; private set; } = null!;

    public TabManager TabManager { get; }
    public ChatHistoryService ChatHistoryService { get; }
    public ChatCaptureService ChatCaptureService { get; }
    public ChatSendService ChatSendService { get; }
    public EmoteService EmoteService { get; }
    public TranslationService TranslationService { get; }
    public TabMessageBuffer TabMessageBuffer { get; }
    public FriendListService FriendListService { get; }
    public PartyInviteService PartyInviteService { get; }
    public FriendRequestService FriendRequestService { get; }
    public AdventurerPlateService AdventurerPlateService { get; }
    public WindowsNotificationService WindowsNotificationService { get; }
    private readonly NativeChatHider nativeChatHider;
    private readonly NativeChatInputWatcher nativeChatInputWatcher;
    private readonly ItemLinkContextMenuService itemLinkContextMenuService;
    private readonly EnterToChatService enterToChatService;

    public readonly WindowSystem WindowSystem = new("CustomChat");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly Dictionary<Guid, DetachedTabWindow> detachedWindows = new();
    private readonly CommandInfo commandInfo;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        TabManager = new TabManager(Configuration);
        ChatHistoryService = new ChatHistoryService(PluginInterface.ConfigDirectory.FullName, Configuration.MaxHistoryBytes, Log);
        ChatCaptureService = new ChatCaptureService(ChatGui, Log, TabManager, ChatHistoryService);
        ChatSendService = new ChatSendService(Log);
        EmoteService = new EmoteService(PluginInterface.ConfigDirectory.FullName, TextureProvider, Log);
        TranslationService = new TranslationService(Log);
        TabMessageBuffer = new TabMessageBuffer(ChatHistoryService);
        var worldIdResolver = new WorldIdResolver(DataManager, Log);
        FriendListService = new FriendListService(worldIdResolver, Log);
        PartyInviteService = new PartyInviteService(worldIdResolver, Log);
        FriendRequestService = new FriendRequestService(ObjectTable, TargetManager, ChatSendService);
        AdventurerPlateService = new AdventurerPlateService(ObjectTable, FriendListService);
        WindowsNotificationService = new WindowsNotificationService(Log) { Enabled = Configuration.NotifyWhisperInWindows };
        nativeChatHider = new NativeChatHider(Framework, GameGui) { Active = Configuration.HideNativeChat };

        ChatCaptureService.MessageRouted += OnMessageRouted;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);

        enterToChatService = new EnterToChatService(Framework, KeyState, mainWindow.RequestFocusInput);
        nativeChatInputWatcher = new NativeChatInputWatcher(Framework, GameGui, Log, GetLocalHomeWorldName, OpenTellTo, mainWindow.PrefillInput);
        itemLinkContextMenuService = new ItemLinkContextMenuService(ContextMenu, AttachItemLink);

        foreach (var tab in TabManager.Tabs)
        {
            if (tab.IsDetached)
                CreateDetachedWindow(tab);
        }

        commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Custom Chat window. Use '/customchat config' for settings.",
        };
        CommandManager.AddHandler(CommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Dalamud's own "Toggle UI" hotkey (Ctrl+Shift+U by default) hides every plugin window at
        // once, which would otherwise still be able to hide the chat window despite everything
        // MainWindow itself does to stay open - this is the one place that has to be disabled at
        // the UiBuilder level rather than per-window.
        PluginInterface.UiBuilder.DisableUserUiHide = true;

        RefreshEmotes();
    }

    private void OnMessageRouted(ChatTabConfig tab, ChatMessageRecord record)
    {
        TabMessageBuffer.Append(tab, record);
        mainWindow.NotifyUnread(tab);

        // Only the incoming half - an outgoing tell echoing back into the same PM tab shouldn't
        // notify about a message the player just sent themselves.
        if (record.ChatType == XivChatType.TellIncoming)
        {
            var senderName = string.IsNullOrEmpty(record.SenderName) ? "Unknown" : record.SenderName;
            WindowsNotificationService.ShowTell(senderName, record.Body);
        }
    }

    /// <summary>Sends text typed into a tab's input box, routed through that tab's outgoing channel
    /// command (or plain text if none is set). For whisper tabs, also primes
    /// <see cref="ChatCaptureService.PendingOutgoingTellTarget"/> so the sent tell round-trips back
    /// into the same conversation even if the game's own echo doesn't resolve a player payload.</summary>
    public void SendFromTab(ChatTabConfig tab, string text, IReadOnlyList<PendingItemLink>? attachments = null)
    {
        if (tab.IsPmTab && tab.PmPartnerKey != null)
            ChatCaptureService.PendingOutgoingTellTarget = tab.PmPartnerKey;

        ChatSendService.Send(tab.OutgoingChannelCommand, text, attachments);
    }

    /// <summary>Queues an item link as a compose-box attachment - the inventory right-click "Link
    /// (Custom Chat)" handler (see <see cref="ItemLinkContextMenuService"/>). Resolves a display name
    /// from the Item sheet purely for the chip UI/link text; if that ever fails for some reason (e.g. a
    /// row id from an unusually new patch not yet in the local sheet), falls back to a generic label
    /// rather than dropping the link - the actual outgoing payload only needs the id/HQ flag, not the
    /// name, to be a valid, clickable link.</summary>
    private void AttachItemLink(uint itemId, bool isHq)
    {
        var name = ResolveItemName(itemId) ?? $"Item #{itemId}";
        mainWindow.AttachItemLink(new PendingItemLink(itemId, isHq, name));
    }

    private static string? ResolveItemName(uint itemId)
    {
        try
        {
            var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ToString();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CustomChat: failed to resolve item name for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>The local character's own home world name, used as a fallback when the game's native
    /// tell-target state leaves the world blank (it does that for same-world targets, since "/tell"
    /// doesn't need an "@World" suffix for those).</summary>
    private static string? GetLocalHomeWorldName() => PlayerState.IsLoaded ? PlayerState.HomeWorld.ValueNullable?.Name.ToString() : null;

    /// <summary>The local character's own "Name@World" key, used by <see cref="Windows.ChatMessageRenderer"/>
    /// to show "You" instead of the player's own name in every tab, including whispers.</summary>
    public static string? GetLocalPlayerKey()
    {
        if (!PlayerState.IsLoaded)
            return null;

        var world = PlayerState.HomeWorld.ValueNullable?.Name.ToString();
        return string.IsNullOrEmpty(world) ? null : $"{PlayerState.CharacterName}@{world}";
    }

    /// <summary>Opens (creating if necessary) the whisper tab for this player and brings it to front -
    /// the right-click menu's "Whisper (Custom Chat)" handler.</summary>
    public void OpenTellTo(string name, string world) => OpenTellToKey($"{name}@{world}");

    /// <summary>Same as <see cref="OpenTellTo"/> but from an already-combined "Name@World" key - used by
    /// both the context menu and the in-chat "Send Tell" right-click on a message's sender name.</summary>
    public void OpenTellToKey(string partnerKey)
    {
        var displayName = partnerKey.Split('@')[0];
        var tab = TabManager.GetOrCreatePmTab(partnerKey, displayName);

        if (tab.IsDetached)
        {
            CreateDetachedWindow(tab);
            if (detachedWindows.TryGetValue(tab.Id, out var window))
                window.RequestFocus = true;
        }
        else
        {
            mainWindow.IsOpen = true;
            mainWindow.SelectTab(tab.Id);
            mainWindow.RequestFocus = true;
        }
    }

    /// <summary>Sends a party invite to a "Name@World" key - the message context menu's "Send Party
    /// Invite" handler. There's no ImGui-side confirmation the invite actually reached anyone (unlike
    /// opening a tab, which is visibly immediate), so this shows a toast either way.</summary>
    public void SendPartyInvite(string partnerKey)
    {
        var at = partnerKey.IndexOf('@');
        if (at <= 0)
            return;

        var name = partnerKey[..at];
        var world = partnerKey[(at + 1)..];
        var sent = PartyInviteService.Invite(name, world);
        ToastGui.ShowNormal(sent ? $"Party invite sent to {name}." : $"Couldn't send a party invite to {name}.");
    }

    /// <summary>Sends a friend request - the message context menu's "Send Friend Request" handler.
    /// "/friendlist add "Name@World"" (a literal name) turned out to not be valid syntax - the game
    /// rejected it with "invalid argument, please specify a valid placeholder", confirming there
    /// really is no by-name path (see <see cref="FriendRequestService"/> for the full story and the
    /// placeholder-based workaround it uses instead). Shows a toast either way, since whether the
    /// player is actually nearby enough to target is genuine, useful information the player can't
    /// otherwise get from this UI.</summary>
    public void SendFriendRequest(string partnerKey)
    {
        var at = partnerKey.IndexOf('@');
        if (at <= 0)
            return;

        var name = partnerKey[..at];
        var world = partnerKey[(at + 1)..];
        var sent = FriendRequestService.TrySend(name, world);
        ToastGui.ShowNormal(sent ? $"Friend request sent to {name}." : $"{name} isn't nearby - can't send a friend request.");
    }

    /// <summary>Opens a player's Adventurer Plate - the message context menu's "View Adventurer
    /// Plate" handler. Same "has to actually be nearby" limitation as <see cref="SendFriendRequest"/>
    /// - see <see cref="AdventurerPlateService"/>.</summary>
    public void ViewAdventurerPlate(string partnerKey)
    {
        var at = partnerKey.IndexOf('@');
        if (at <= 0)
            return;

        var name = partnerKey[..at];
        var world = partnerKey[(at + 1)..];
        if (!AdventurerPlateService.TryOpen(name, world))
            ToastGui.ShowNormal($"{name} isn't nearby - can't open their Adventurer Plate.");
    }

    /// <summary>Opens the map at a clicked map/flag coordinate link - see
    /// <see cref="Models.ChatPayloadLink"/> for how these are captured in the first place.</summary>
    public void OpenMapLink(MapLinkPayload payload)
    {
        if (!GameGui.OpenMapWithMapLink(payload))
            ToastGui.ShowError("Couldn't open that map link.");
    }

    /// <summary>Exports a tab's *entire* stored history (not just what's currently buffered in
    /// memory - reads straight from <see cref="ChatHistoryService"/>) to a plain-text file under the
    /// plugin's config directory, then reveals it in Explorer - the sidebar tab context menu's
    /// "Export to file..." handler.</summary>
    public void ExportTabToFile(ChatTabConfig tab)
    {
        var routingKey = tab.IsPmTab ? tab.PmPartnerKey : tab.Id.ToString();
        if (string.IsNullOrEmpty(routingKey))
            return;

        string path;
        int count;

        // Reading history and writing the file is the part that actually matters - kept in its own
        // try/catch so a failure here (and only here) reports "failed to export". Revealing the file
        // in Explorer below is a separate, best-effort convenience: launching an external process from
        // inside the game can fail for reasons that have nothing to do with whether the export itself
        // worked (e.g. an elevation mismatch between the game and the shell), and treating that as the
        // whole export having failed was misleading - the file was already written successfully.
        try
        {
            var messages = ChatHistoryService.LoadRecent(routingKey, int.MaxValue);
            var lines = messages.Select(m =>
            {
                var time = m.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                return string.IsNullOrEmpty(m.SenderName) ? $"[{time}] {m.Body}" : $"[{time}] {m.SenderName}: {m.Body}";
            });

            var exportDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "exports");
            Directory.CreateDirectory(exportDir);

            var invalidChars = Path.GetInvalidFileNameChars();
            var safeName = new string(tab.Name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            path = Path.Combine(exportDir, fileName);
            File.WriteAllLines(path, lines);
            count = messages.Count;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CustomChat: failed to export tab {Tab}", tab.Name);
            // The exception message is included directly in the toast (not just /xllog) since this
            // couldn't be reproduced/tested locally - if it fails again, the toast itself should say
            // why instead of needing a follow-up round-trip to go dig up the log line.
            ToastGui.ShowError($"Failed to export chat history: {ex.Message}");
            return;
        }

        ToastGui.ShowNormal($"Exported {count} message(s) to {Path.GetFileName(path)}.");

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CustomChat: exported chat history but failed to open Explorer to show it");
        }
    }

    public void SetTabDetached(ChatTabConfig tab, bool detached)
    {
        TabManager.SetDetached(tab, detached);

        if (detached)
        {
            CreateDetachedWindow(tab);
        }
        else if (detachedWindows.Remove(tab.Id, out var window))
        {
            WindowSystem.RemoveWindow(window);
            window.Dispose();
        }
    }

    /// <summary>Closes every whisper tab/window at once (attached and popped-out alike). Like closing
    /// one individually, this never deletes message history - only the tabs, which reopen with their
    /// full history the next time a message (or a detected native "Send Tell") needs them.</summary>
    public void CloseAllWhisperTabs()
    {
        foreach (var tab in TabManager.Tabs.Where(t => t.IsPmTab).ToList())
        {
            if (tab.IsDetached && detachedWindows.Remove(tab.Id, out var window))
            {
                WindowSystem.RemoveWindow(window);
                window.Dispose();
            }

            TabManager.RemoveTab(tab);
        }
    }

    private void CreateDetachedWindow(ChatTabConfig tab)
    {
        if (detachedWindows.ContainsKey(tab.Id))
            return;

        var window = new DetachedTabWindow(this, tab);
        detachedWindows[tab.Id] = window;
        WindowSystem.AddWindow(window);
    }

    public void OpenTabEditor(Guid tabId)
    {
        configWindow.IsOpen = true;
        configWindow.FocusTab(tabId);
    }

    /// <summary>Opens/focuses the settings window - the main chat window's title bar gear button.</summary>
    public void OpenSettings()
    {
        configWindow.IsOpen = true;
        configWindow.RequestFocus = true;
    }

    public void ApplyNativeChatHidden() => nativeChatHider.Active = Configuration.HideNativeChat;

    /// <summary>Wipes all stored chat history from disk and clears the in-memory scrollback that
    /// every open tab/window is currently showing, so the UI reflects it immediately.</summary>
    public void ClearAllHistory()
    {
        ChatHistoryService.ClearAll();
        TabMessageBuffer.ClearAll();
    }

    /// <summary>Resets every setting (not tabs - see <see cref="Configuration.ResetToDefaults"/>) to
    /// its default value and reapplies the handful that have side effects elsewhere beyond just being
    /// read live each frame - the Settings "Reset settings to defaults" handler.</summary>
    public void ResetSettingsToDefaults()
    {
        Configuration.ResetToDefaults();
        Configuration.Save();

        ApplyNativeChatHidden();
        ChatHistoryService.SetMaxBytes(Configuration.MaxHistoryBytes);
        WindowsNotificationService.Enabled = Configuration.NotifyWhisperInWindows;
    }

    /// <summary>Startup load: uses the disk-cached manifest if it's still within the configured TTL,
    /// same as before - fast, and doesn't hit BTTV/7TV/the standard-emoji CDN on every launch.</summary>
    public void RefreshEmotes()
    {
        _ = RunEmoteRefresh(() => EmoteService.EnsureLoadedAsync(
            Configuration.BttvEnabled,
            Configuration.SevenTvEnabled,
            TimeSpan.FromHours(Configuration.EmoteCacheTtlHours)));
    }

    /// <summary>The Settings "Refresh emotes now" button's handler. Unlike <see cref="RefreshEmotes"/>,
    /// this always rebuilds the emote list from scratch (BTTV/7TV/standard emoji) regardless of the
    /// disk cache's age - otherwise clicking it while the cache is still within its TTL (e.g. right
    /// after adding more entries to the standard emoji catalog and reloading the plugin) would just
    /// reload the same stale cached list and look like nothing happened.</summary>
    public void ForceRefreshEmotes()
    {
        _ = RunEmoteRefresh(() => EmoteService.RefreshAsync(Configuration.BttvEnabled, Configuration.SevenTvEnabled));
    }

    private async System.Threading.Tasks.Task RunEmoteRefresh(Func<System.Threading.Tasks.Task> refresh)
    {
        try
        {
            await refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CustomChat: emote refresh failed");
        }
    }

    public void Dispose()
    {
        // Persist final unread counts (see ChatTabConfig.UnreadCount) - they aren't saved on every
        // increment to avoid a disk write per chat line, only here and on explicit "mark as read"
        // actions, so a clean unload/reload doesn't lose them.
        TabManager.Save();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);

        ChatCaptureService.MessageRouted -= OnMessageRouted;

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        foreach (var window in detachedWindows.Values)
            window.Dispose();

        nativeChatHider.Dispose();
        nativeChatInputWatcher.Dispose();
        itemLinkContextMenuService.Dispose();
        enterToChatService.Dispose();
        EmoteService.Dispose();
        TranslationService.Dispose();
        WindowsNotificationService.Dispose();
        ChatCaptureService.Dispose();
        ChatHistoryService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        ToggleMainUi();
    }

    private void ToggleConfigUi() => configWindow.Toggle();

    /// <summary>The main chat window is always open and can't be closed - this just brings it to front.</summary>
    private void ToggleMainUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.RequestFocus = true;
    }
}
