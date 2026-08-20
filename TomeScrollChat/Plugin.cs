using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;
using TomeScrollChat.Services;
using TomeScrollChat.Services.CrossDc;
using TomeScrollChat.Windows;

namespace TomeScrollChat;

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
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/tomescrollc";

    internal static Configuration Configuration { get; private set; } = null!;

    public TabManager TabManager { get; }
    public ChatHistoryService ChatHistoryService { get; }
    public ChatCaptureService ChatCaptureService { get; }
    public ChatSendService ChatSendService { get; }
    public EmoteService EmoteService { get; }

    /// <summary>General-purpose Gemini API wrapper - see its own doc comment. Public so future
    /// features beyond translation can use it directly, not just <see cref="TranslationService"/>.</summary>
    public GeminiService GeminiService { get; }

    /// <summary>General-purpose in-plugin popup notifications (<see cref="Windows.NotificationOverlay"/>
    /// draws whatever's queued here) - public so any future feature can call
    /// <c>plugin.NotificationService.Show(...)</c> directly, same reasoning as <see cref="GeminiService"/>.</summary>
    public NotificationService NotificationService { get; }

    /// <summary>Public for the same reason as <see cref="NotificationService"/> - Settings' "Test
    /// sound" button calls <see cref="Services.NotificationSoundService.PlayPreview"/> directly.</summary>
    public NotificationSoundService NotificationSoundService { get; }

    public TranslationService TranslationService { get; }
    public TabMessageBuffer TabMessageBuffer { get; }
    public FriendListService FriendListService { get; }
    public PartyInviteService PartyInviteService { get; }
    public FriendRequestService FriendRequestService { get; }
    public AdventurerPlateService AdventurerPlateService { get; }
    public ItemTooltipService ItemTooltipService { get; }
    public ItemContextService ItemContextService { get; }
    public PartyFinderLinkService PartyFinderLinkService { get; }
    public QuestLinkService QuestLinkService { get; }
    public DialogueTranslationService DialogueTranslationService { get; }
    public AutoTranslatePhraseService AutoTranslatePhraseService { get; }
    public FriendOnlineWatcherService FriendOnlineWatcherService { get; }
    public AiReplyService AiReplyService { get; }
    public AutoReplyService AutoReplyService { get; }

    /// <summary>Public so Settings > Cross-DC can read <see cref="CrossDcRelayService.IsConnected"/>/
    /// <see cref="CrossDcRelayService.UserId"/>/<see cref="CrossDcRelayService.LastError"/> directly,
    /// same reasoning as <see cref="NotificationService"/> above.</summary>
    public CrossDcRelayService CrossDcRelayService { get; }
    private readonly WhisperNotificationService whisperNotificationService;
    private readonly MentionNotificationService mentionNotificationService;
    private readonly NativeChatHider nativeChatHider;
    private readonly NativeChatInputWatcher nativeChatInputWatcher;
    private readonly NativeItemLinkWatcher nativeItemLinkWatcher;
    private readonly NativePartyFinderLinkWatcher nativePartyFinderLinkWatcher;
    private readonly NativeQuestLinkWatcher nativeQuestLinkWatcher;
    private readonly LinkshellWatcherService linkshellWatcherService;
    private readonly EnterToChatService enterToChatService;

    public readonly WindowSystem WindowSystem = new("TomeScrollChat");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly NotificationOverlay notificationOverlay;
    private readonly DialogueTranslationWindow dialogueTranslationWindow;
    private readonly Dictionary<Guid, DetachedTabWindow> detachedWindows = new();
    private readonly CommandInfo commandInfo;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        TabManager = new TabManager(Configuration);
        // Cleans up a popped-out floating window for any tab removed from anywhere, including paths
        // that don't already do this inline themselves (SetTabDetached/CloseAllWhisperTabs do, so
        // this is a harmless no-op for those) - added specifically for SyncAutoLinkshellTabs removing
        // a linkshell tab out from under the player while it happens to be detached.
        TabManager.TabRemoved += OnTabRemoved;
        ChatHistoryService = new ChatHistoryService(PluginInterface.ConfigDirectory.FullName, Configuration.MaxHistoryBytes, Log);
        NotificationSoundService = new NotificationSoundService(PluginInterface.ConfigDirectory.FullName, Configuration, Log);
        NotificationService = new NotificationService(NotificationSoundService);
        ChatCaptureService = new ChatCaptureService(ChatGui, Log, Configuration, TabManager, ChatHistoryService, NotificationService);
        ChatSendService = new ChatSendService(Log);
        EmoteService = new EmoteService(PluginInterface.ConfigDirectory.FullName, TextureProvider, Log);
        GeminiService = new GeminiService(Log, Configuration, NotificationService);
        TranslationService = new TranslationService(PluginInterface.ConfigDirectory.FullName, Log, Configuration, ChatHistoryService, GeminiService, NotificationService);
        TabMessageBuffer = new TabMessageBuffer(ChatHistoryService);
        var worldIdResolver = new WorldIdResolver(DataManager, Log);
        FriendListService = new FriendListService(worldIdResolver, Log);
        PartyInviteService = new PartyInviteService(worldIdResolver, Log);
        FriendRequestService = new FriendRequestService(ObjectTable, TargetManager, ChatSendService);
        AdventurerPlateService = new AdventurerPlateService(ObjectTable, FriendListService);
        ItemTooltipService = new ItemTooltipService(GameGui, Log);
        ItemContextService = new ItemContextService(Log);
        PartyFinderLinkService = new PartyFinderLinkService(Log);
        QuestLinkService = new QuestLinkService(Log);
        DialogueTranslationService = new DialogueTranslationService(Framework, GameGui, ToastGui, Log, Configuration, TranslationService);
        AutoTranslatePhraseService = new AutoTranslatePhraseService(DataManager, Log);
        AutoTranslatePhraseService.Preload(); // off the main thread - expanding every dictionary category can be slow enough to hitch the UI if it first happened on-demand when Tab is pressed
        FriendOnlineWatcherService = new FriendOnlineWatcherService(Framework, ClientState, GameGui, Log, Configuration, FriendListService, NotificationService);
        AiReplyService = new AiReplyService(PluginInterface.ConfigDirectory.FullName, GeminiService, Configuration, Log, NotificationService);
        AutoReplyService = new AutoReplyService(Framework, ChatCaptureService, ChatSendService, Configuration, NotificationService, Log);
        whisperNotificationService = new WhisperNotificationService(ChatCaptureService, Configuration, NotificationService, NotificationSoundService, Log);
        mentionNotificationService = new MentionNotificationService(ChatCaptureService, Configuration, NotificationService, Log);
        nativeChatHider = new NativeChatHider(Framework, GameGui) { Active = Configuration.HideNativeChat };
        CrossDcRelayService = new CrossDcRelayService(PluginInterface.ConfigDirectory.FullName, Configuration, Log);
        // Auto-(re)connects on startup if the feature was already enabled in a previous session - a
        // no-op if it's still Disabled, same method Settings > Cross-DC calls on every later change.
        CrossDcRelayService.Reconcile();

        ChatCaptureService.MessageRouted += OnMessageRouted;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        notificationOverlay = new NotificationOverlay(NotificationService, mainWindow);
        dialogueTranslationWindow = new DialogueTranslationWindow(Configuration, DialogueTranslationService);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(notificationOverlay);
        WindowSystem.AddWindow(dialogueTranslationWindow);

        enterToChatService = new EnterToChatService(Framework, KeyState, mainWindow.RequestFocusInput);
        nativeChatInputWatcher = new NativeChatInputWatcher(Framework, GameGui, Log, GetLocalHomeWorldName, OpenTellTo, mainWindow.PrefillInput);
        nativeItemLinkWatcher = new NativeItemLinkWatcher(Framework, GameGui, Log, AttachItemLink);
        nativePartyFinderLinkWatcher = new NativePartyFinderLinkWatcher(Framework, GameGui, Log, AttachPartyFinderLink);
        nativeQuestLinkWatcher = new NativeQuestLinkWatcher(Framework, GameGui, Log, AttachQuestLink);
        linkshellWatcherService = new LinkshellWatcherService(Framework, Log, Configuration, TabManager);

        foreach (var tab in TabManager.Tabs)
        {
            if (tab.IsDetached)
                CreateDetachedWindow(tab);
        }

        commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the TomeScroll Chat window. Use '/tomescrollc config' for settings, '/tomescrollc version' to check the loaded build, '/tomescrollc frienddebug' to test friend online/offline notifications.",
        };
        CommandManager.AddHandler(CommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += DrawAll;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Dalamud's own "Toggle UI" hotkey (Ctrl+Shift+U by default) hides every plugin window at
        // once, which would otherwise still be able to hide the chat window despite everything
        // MainWindow itself does to stay open - this is the one place that has to be disabled at
        // the UiBuilder level rather than per-window.
        PluginInterface.UiBuilder.DisableUserUiHide = true;

        // Fixed 2026-08-19: DialogueTranslationWindow's own DrawConditions() unconditionally returns
        // true during a cutscene (the whole point of that window), but it still reportedly hid mid-
        // cutscene anyway - turned out Dalamud auto-hides *every* plugin window during cutscenes at
        // the UiBuilder level, upstream of any individual window's DrawConditions ever being consulted,
        // unless disabled here. Same reasoning as DisableUserUiHide just above: has to be a UiBuilder-
        // level flag, not something any per-window check can override on its own. This also means
        // MainWindow's own Configuration.HideChatDuringCutscenes toggle is now the *only* thing
        // deciding whether the main chat hides during cutscenes (as it always should have been) -
        // Dalamud's own forced hide would have silently overridden that setting when it was off.
        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        RefreshEmotes();

        // Logs the same info "/tomescrollc version" prints to chat, once at startup - so which build is
        // actually running is visible straight in /xllog, without needing to remember to run the
        // command every time after a rebuild (this exact gap cost several debugging rounds earlier).
        Log.Info(BuildVersionString());
    }

    private void OnMessageRouted(ChatTabConfig tab, ChatMessageRecord record)
    {
        TabMessageBuffer.Append(tab, record);

        // Fixed 2026-08-17: this used to increment unconditionally, including for the player's own
        // outgoing tells (every send echoes back through the same capture pipeline as an incoming
        // message). For a whisper tab that's not just wrong "unread" accounting - MainWindow's
        // sidebar bubbles a PM tab with UnreadCount > 0 to the top of the PM group (see DrawSidebar's
        // OrderBy/ThenBy), and the visibility-based decay (see the "Discord-style unread tracking"
        // system) usually brings it back to 0 within a frame or two once the just-sent message
        // scrolls into view - net effect: sending a tell to a friend briefly bumped that tab to the
        // top of the list and back, reported live as the sidebar/chat "blinking" specifically on
        // send, for whisper tabs only (regular channel tabs aren't reordered by unread at all, so the
        // same bug there was invisible). Only channel tabs never hit this path for their own outgoing
        // messages in the first place (SendFromTab doesn't loop the sent text back through capture
        // for non-PM tabs), so this check only ever actually matters for whispers - kept general
        // rather than gated on tab.IsPmTab so it can't silently regress if that changes.
        if (!IsOwnMessage(record))
            mainWindow.NotifyUnread(tab);
    }

    /// <summary>Whether a captured message is the local player's own outgoing message - same check
    /// <see cref="Windows.ChatMessageRenderer.DrawMessage"/> uses to show "You" instead of a name.</summary>
    private static bool IsOwnMessage(ChatMessageRecord record)
    {
        var localPlayerKey = GetLocalPlayerKey();
        var localPlayerName = localPlayerKey?.Split('@')[0];
        return record.ChatType == XivChatType.TellOutgoing ||
               (!string.IsNullOrEmpty(localPlayerKey) && record.SenderKey == localPlayerKey) ||
               (!string.IsNullOrEmpty(localPlayerName) && record.SenderName == localPlayerName);
    }

    /// <summary>Sends text typed into a tab's input box, routed through that tab's outgoing channel
    /// command (or plain text if none is set). For whisper tabs, also primes
    /// <see cref="ChatCaptureService.PendingOutgoingTellTarget"/> so the sent tell round-trips back
    /// into the same conversation even if the game's own echo doesn't resolve a player payload.</summary>
    public void SendFromTab(ChatTabConfig tab, string text, IReadOnlyList<PendingItemLink>? attachments = null, IReadOnlyList<PendingPartyFinderLink>? partyFinderAttachments = null, IReadOnlyList<PendingAutoTranslateLink>? autoTranslateAttachments = null, IReadOnlyList<PendingQuestLink>? questAttachments = null)
    {
        if (tab.IsPmTab && tab.PmPartnerKey != null)
            ChatCaptureService.PendingOutgoingTellTarget = tab.PmPartnerKey;

        ChatSendService.Send(tab.OutgoingChannelCommand, text, attachments, partyFinderAttachments, autoTranslateAttachments, questAttachments);
    }

    /// <summary>Queues an item link and inserts a "&lt;link&gt;" placeholder into the compose box -
    /// the native "Link" action's handler (see <see cref="NativeItemLinkWatcher"/>). Resolves a
    /// display name from the Item sheet purely for the placeholder's own click-to-copy text; if that
    /// ever fails for some reason (e.g. a row id from an unusually new patch not yet in the local
    /// sheet), falls back to a generic label rather than dropping the link - the actual outgoing
    /// payload doesn't depend on this name at all when <paramref name="rawPayloadBytes"/> is present
    /// (the game's own captured bytes are used as-is; the name would only matter for the
    /// SeStringBuilder-reconstructed fallback).</summary>
    private void AttachItemLink(uint itemId, bool isHq, byte[]? rawPayloadBytes)
    {
        var name = ResolveItemName(itemId) ?? $"Item #{itemId}";
        mainWindow.AttachItemLink(new PendingItemLink(itemId, isHq, name, rawPayloadBytes));
    }

    /// <summary>Queues a Party Finder listing link and inserts a "&lt;pflink&gt;" placeholder into the
    /// compose box - the native Party Finder window's own "Relay" action's handler (see
    /// <see cref="NativePartyFinderLinkWatcher"/>).</summary>
    private void AttachPartyFinderLink(ulong listingId, string leaderName, byte[]? rawPayloadBytes) =>
        mainWindow.AttachPartyFinderLink(new PendingPartyFinderLink(listingId, leaderName, rawPayloadBytes));

    /// <summary>Queues a quest link and inserts a "&lt;questlink&gt;" placeholder into the compose box -
    /// the native Quest Journal's own "Link in Chat" action's handler (see
    /// <see cref="NativeQuestLinkWatcher"/>).</summary>
    private void AttachQuestLink(uint questId, string questName, byte[]? rawPayloadBytes) =>
        mainWindow.AttachQuestLink(new PendingQuestLink(questId, questName, rawPayloadBytes));

    private static string? ResolveItemName(uint itemId)
    {
        try
        {
            var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ToString();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TomeScrollChat: failed to resolve item name for {ItemId}", itemId);
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
    /// the right-click menu's "Whisper (TomeScroll Chat)" handler.</summary>
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

    /// <summary>Opens a clicked Party Finder listing link - see <see cref="Models.ChatPayloadLink"/>
    /// for how these are captured in the first place.</summary>
    public void OpenPartyFinderLink(PartyFinderPayload payload) => PartyFinderLinkService.OpenListing(payload.ListingId);

    /// <summary>Opens a clicked quest link straight to it in the native Quest Journal - see
    /// <see cref="Models.ChatPayloadLink"/> for how these are captured in the first place.</summary>
    public void OpenQuestLink(QuestPayload payload) => QuestLinkService.OpenQuest(payload.Quest.RowId);

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
            Log.Warning(ex, "TomeScrollChat: failed to export tab {Tab}", tab.Name);
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
            Log.Warning(ex, "TomeScrollChat: exported chat history but failed to open Explorer to show it");
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

    /// <summary>Cleans up a popped-out floating window for any tab removed from anywhere. Existing
    /// removal call sites that already handle this inline (<see cref="SetTabDetached"/>,
    /// <see cref="CloseAllWhisperTabs"/>) end up removing the window from <c>detachedWindows</c>
    /// before this ever runs, so it's a harmless no-op for those - it's here specifically for removal
    /// paths that don't already do it themselves, like <see cref="TabManager.SyncAutoLinkshellTabs"/>
    /// removing a linkshell tab out from under the player while it happens to be detached.</summary>
    private void OnTabRemoved(ChatTabConfig tab)
    {
        if (detachedWindows.Remove(tab.Id, out var window))
        {
            WindowSystem.RemoveWindow(window);
            window.Dispose();
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

    /// <summary>Settings > Cross-DC's handler for every change that affects the relay connection (the
    /// enable toggle, switching Managed/SelfHosted, editing the self-hosted URL) - reconciles the live
    /// connection against whatever the config now says.</summary>
    public void ApplyCrossDcRelaySettings() => CrossDcRelayService.Reconcile();

    /// <summary>Wipes all stored chat history from disk and clears the in-memory scrollback that
    /// every open tab/window is currently showing, so the UI reflects it immediately.</summary>
    public void ClearAllHistory()
    {
        ChatHistoryService.ClearAll();
        TabMessageBuffer.ClearAll();
    }

    /// <summary>Resets every setting to its default value, reapplies the handful that have side effects
    /// elsewhere beyond just being read live each frame, and - per explicit user request, 2026-08-17 -
    /// also resets <see cref="TabManager"/> back to the five built-in tabs a brand-new install starts
    /// with (see <see cref="Services.TabManager.ResetToDefaults"/>; any custom tabs, and any per-tab
    /// colour override, are gone after this). The Settings "Reset settings to defaults" handler.</summary>
    public void ResetSettingsToDefaults()
    {
        Configuration.ResetToDefaults();
        TabManager.ResetToDefaults();
        Configuration.Save();

        ApplyNativeChatHidden();
        ChatHistoryService.SetMaxBytes(Configuration.MaxHistoryBytes);
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
            Log.Warning(ex, "TomeScrollChat: emote refresh failed");
        }
    }

    /// <summary>Wraps <see cref="WindowSystem.Draw"/> so <see cref="ItemTooltipService"/> can track
    /// item-link hovers across every window that draws messages this frame (main window plus any
    /// number of detached whisper windows) as one unit, rather than each window's own message-drawing
    /// call opening/closing the tooltip independently - which could flicker it shut between windows
    /// even while an item link is continuously hovered in just one of them.</summary>
    private void DrawAll()
    {
        ItemTooltipService.BeginFrame();
        WindowSystem.Draw();
        ItemTooltipService.EndFrame();
    }

    public void Dispose()
    {
        // Persist final unread counts (see ChatTabConfig.UnreadCount) - they aren't saved on every
        // increment to avoid a disk write per chat line, only here and on explicit "mark as read"
        // actions, so a clean unload/reload doesn't lose them.
        TabManager.Save();

        PluginInterface.UiBuilder.Draw -= DrawAll;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);

        ChatCaptureService.MessageRouted -= OnMessageRouted;

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        foreach (var window in detachedWindows.Values)
            window.Dispose();

        TabManager.TabRemoved -= OnTabRemoved;

        CrossDcRelayService.Dispose();
        nativeChatHider.Dispose();
        nativeChatInputWatcher.Dispose();
        nativeItemLinkWatcher.Dispose();
        nativePartyFinderLinkWatcher.Dispose();
        nativeQuestLinkWatcher.Dispose();
        DialogueTranslationService.Dispose();
        FriendOnlineWatcherService.Dispose();
        AutoReplyService.Dispose();
        whisperNotificationService.Dispose();
        mentionNotificationService.Dispose();
        linkshellWatcherService.Dispose();
        enterToChatService.Dispose();
        EmoteService.Dispose();
        TranslationService.Dispose();
        GeminiService.Dispose();
        ChatCaptureService.Dispose();
        ChatHistoryService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (trimmed.Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            PrintVersion();
            return;
        }

        if (trimmed.Equals("frienddebug", StringComparison.OrdinalIgnoreCase))
        {
            FriendOnlineWatcherService.DebugCheckAndNotify();
            return;
        }

        ToggleMainUi();
    }

    /// <summary>"/tomescrollc version" - shows the loaded assembly's version and its on-disk build time
    /// as a popup toast (<see cref="NotificationService"/>), not a chat line - easier to actually
    /// notice, and doesn't get lost/scrolled past in whichever tab happens to be selected. Given a
    /// longer-than-default duration since this is diagnostic text meant to actually be read, not just
    /// glanced at. Same string also gets logged once at startup (see the constructor) so which build
    /// is actually running is visible in /xllog without needing to run this command at all.</summary>
    private void PrintVersion() => NotificationService.Show(BuildVersionString(), NotificationSeverity.Info, TimeSpan.FromSeconds(12));

    /// <summary>Builds the "/tomescrollc version" string. The assembly's Version rarely changes between
    /// commits in this project (it's not bumped per-build), so on its own it can't answer "is this
    /// actually the build I just compiled" - the file's last-write time can, since that changes on
    /// every rebuild. Uses <see cref="IDalamudPluginInterface.AssemblyLocation"/>, not
    /// <c>typeof(Plugin).Assembly.Location</c> - Dalamud loads dev plugins from an in-memory byte array
    /// (so it can rebuild the DLL on disk without holding a file lock on it), which leaves the CLR's
    /// own <c>Assembly.Location</c> empty; <c>PluginInterface.AssemblyLocation</c> is the actual
    /// on-disk path/timestamp Dalamud loaded from, independent of that. Also includes the exact git
    /// commit this build was compiled from (embedded at build time by <c>TomeScrollChat.csproj</c>'s
    /// <c>SetGitCommitHash</c> target as an <see cref="AssemblyMetadataAttribute"/>) - the one piece of
    /// this that can't drift or be mis-set by hand, since it's read straight from git.</summary>
    private static string BuildVersionString()
    {
        var assembly = typeof(Plugin).Assembly;
        var version = assembly.GetName().Version;
        var commitHash = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitCommitHash")?.Value ?? "unknown";
        var file = PluginInterface.AssemblyLocation;
        var buildTime = file.Exists ? file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : "unknown";
        return $"TomeScroll Chat v{version} (commit {commitHash}), built {buildTime}, loaded from {file.FullName}";
    }

    private void ToggleConfigUi() => configWindow.Toggle();

    /// <summary>The main chat window is always open and can't be closed - this just brings it to front.</summary>
    private void ToggleMainUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.RequestFocus = true;
    }
}
