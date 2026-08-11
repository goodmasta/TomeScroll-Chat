using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
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
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/customchat";

    internal static Configuration Configuration { get; private set; } = null!;

    public TabManager TabManager { get; }
    public ChatHistoryService ChatHistoryService { get; }
    public ChatCaptureService ChatCaptureService { get; }
    public ChatSendService ChatSendService { get; }
    public EmoteService EmoteService { get; }
    public TabMessageBuffer TabMessageBuffer { get; }
    private readonly NativeChatHider nativeChatHider;
    private readonly ContextMenuService contextMenuService;

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
        TabMessageBuffer = new TabMessageBuffer(ChatHistoryService);
        nativeChatHider = new NativeChatHider(Framework, GameGui) { Active = Configuration.HideNativeChat };
        contextMenuService = new ContextMenuService(this, ContextMenu);

        ChatCaptureService.MessageRouted += OnMessageRouted;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);

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

        RefreshEmotes();
    }

    private void OnMessageRouted(ChatTabConfig tab, ChatMessageRecord record)
    {
        TabMessageBuffer.Append(tab, record);
        mainWindow.NotifyUnread(tab);
    }

    /// <summary>Sends text typed into a tab's input box, routed through that tab's outgoing channel
    /// command (or plain text if none is set). For whisper tabs, also primes
    /// <see cref="ChatCaptureService.PendingOutgoingTellTarget"/> so the sent tell round-trips back
    /// into the same conversation even if the game's own echo doesn't resolve a player payload.</summary>
    public void SendFromTab(ChatTabConfig tab, string text)
    {
        if (tab.IsPmTab && tab.PmPartnerKey != null)
            ChatCaptureService.PendingOutgoingTellTarget = tab.PmPartnerKey;

        ChatSendService.Send(tab.OutgoingChannelCommand, text);
    }

    /// <summary>Opens (creating if necessary) the whisper tab for this player and brings it to front -
    /// the "Send Tell (Custom Chat)" right-click menu item's handler.</summary>
    public void OpenTellTo(string name, string world)
    {
        var partnerKey = $"{name}@{world}";
        var tab = TabManager.GetOrCreatePmTab(partnerKey, name);

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

    public void ApplyNativeChatHidden() => nativeChatHider.Active = Configuration.HideNativeChat;

    public void RefreshEmotes()
    {
        _ = RefreshEmotesAsync();
    }

    private async System.Threading.Tasks.Task RefreshEmotesAsync()
    {
        try
        {
            await EmoteService.EnsureLoadedAsync(
                Configuration.BttvEnabled,
                Configuration.SevenTvEnabled,
                TimeSpan.FromHours(Configuration.EmoteCacheTtlHours)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CustomChat: emote refresh failed");
        }
    }

    public void Dispose()
    {
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
        contextMenuService.Dispose();
        EmoteService.Dispose();
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
