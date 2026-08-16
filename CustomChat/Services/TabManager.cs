using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>Owns the live list of configured tabs: creates the built-in defaults on first run, and handles CRUD, detach/reattach, and whisper-tab lookup/creation.</summary>
public sealed class TabManager
{
    private readonly Configuration configuration;

    public TabManager(Configuration configuration)
    {
        this.configuration = configuration;
        if (configuration.Tabs.Count == 0)
        {
            configuration.Tabs.AddRange(DefaultTabFactory.CreateDefaults());
            configuration.Save();
        }
        else
        {
            MigrateNoviceCommand();
        }
    }

    /// <summary>"/nov" (the default Novice Chat outgoing command up to this version) isn't a command
    /// the game recognises and errors instead of sending - "/n" is the correct one. Existing installs
    /// already have "/nov" saved to disk from first run, so this corrects it in place rather than only
    /// fixing it for brand-new installs via <see cref="DefaultTabFactory"/>.</summary>
    private void MigrateNoviceCommand()
    {
        var changed = false;
        foreach (var tab in configuration.Tabs)
        {
            if (tab.IsBuiltIn && tab.OutgoingChannelCommand == "/nov")
            {
                tab.OutgoingChannelCommand = "/n";
                changed = true;
            }
        }

        if (changed)
            configuration.Save();
    }

    public IReadOnlyList<ChatTabConfig> Tabs => configuration.Tabs;

    public event Action<ChatTabConfig>? TabAdded;
    public event Action<ChatTabConfig>? TabRemoved;

    public ChatTabConfig CreateTab(string name)
    {
        var tab = new ChatTabConfig { Name = name };
        configuration.Tabs.Add(tab);
        configuration.Save();
        TabAdded?.Invoke(tab);
        return tab;
    }

    public void RemoveTab(ChatTabConfig tab)
    {
        if (!configuration.Tabs.Remove(tab))
            return;

        configuration.Save();
        TabRemoved?.Invoke(tab);
    }

    public void SetDetached(ChatTabConfig tab, bool detached)
    {
        if (tab.IsDetached == detached)
            return;

        tab.IsDetached = detached;
        configuration.Save();
    }

    /// <summary>Finds the existing tab for one whisper partner, or creates it (as a main-window tab or its own
    /// floating window, per <see cref="Configuration.OpenWhispersInSeparateWindow"/>).</summary>
    public ChatTabConfig GetOrCreatePmTab(string partnerKey, string displayName)
    {
        var existing = configuration.Tabs.FirstOrDefault(t => t.IsPmTab && t.PmPartnerKey == partnerKey);
        if (existing != null)
            return existing;

        var tab = new ChatTabConfig
        {
            Name = displayName,
            IsPmTab = true,
            PmPartnerKey = partnerKey,
            IsDetached = configuration.OpenWhispersInSeparateWindow,
            OutgoingChannelCommand = $"/tell {partnerKey}",
            Channels = new(DefaultTabFactory.TellChannels),
        };
        configuration.Tabs.Add(tab);
        configuration.Save();
        TabAdded?.Invoke(tab);
        return tab;
    }

    private static readonly XivChatType[] LinkshellChatTypes =
    {
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
    };

    private static readonly XivChatType[] CrossWorldLinkshellChatTypes =
    {
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    };

    /// <summary>Called by <see cref="LinkshellWatcherService"/> with a fresh membership snapshot (8
    /// slots each, null = not currently a member of that slot) - creates/renames/removes the matching
    /// <see cref="ChatTabConfig.IsAutoLinkshellTab"/> tabs to match. Idempotent: calling again with an
    /// unchanged snapshot does nothing; calling with all-null snapshots (e.g. right after the feature
    /// is turned off) removes every remaining auto tab - see <see cref="RemoveAllAutoLinkshellTabs"/>
    /// for the more direct way to do just that.</summary>
    public void SyncAutoLinkshellTabs(IReadOnlyList<string?> linkshellNames, IReadOnlyList<string?> crossWorldNames)
    {
        SyncAutoLinkshellGroup(linkshellNames, LinkshellChatTypes, crossWorld: false, commandPrefix: "/linkshell");
        SyncAutoLinkshellGroup(crossWorldNames, CrossWorldLinkshellChatTypes, crossWorld: true, commandPrefix: "/cwlinkshell");
    }

    private void SyncAutoLinkshellGroup(IReadOnlyList<string?> names, XivChatType[] chatTypes, bool crossWorld, string commandPrefix)
    {
        for (var i = 0; i < names.Count && i < chatTypes.Length; i++)
        {
            var existing = configuration.Tabs.FirstOrDefault(t => t.IsAutoLinkshellTab && t.IsCrossWorldLinkshell == crossWorld && t.LinkshellIndex == i);
            var name = names[i];

            if (string.IsNullOrEmpty(name))
            {
                if (existing != null)
                    RemoveTab(existing);
                continue;
            }

            if (existing == null)
            {
                var tab = new ChatTabConfig
                {
                    Name = name,
                    Channels = new HashSet<XivChatType> { chatTypes[i] },
                    OutgoingChannelCommand = $"{commandPrefix}{i + 1}",
                    IsAutoLinkshellTab = true,
                    IsCrossWorldLinkshell = crossWorld,
                    LinkshellIndex = i,
                };
                configuration.Tabs.Add(tab);
                configuration.Save();
                TabAdded?.Invoke(tab);
            }
            else if (existing.Name != name)
            {
                // Someone with rename permissions on the shell changed its name in-game - keep the
                // tab's own name (which the player may since have customized further) from silently
                // going stale relative to what the native UI now shows.
                existing.Name = name;
                configuration.Save();
            }
        }
    }

    /// <summary>Immediately removes every auto-created linkshell tab regardless of current membership -
    /// the "Auto-create linkshell tabs" setting's own off-switch calls this directly rather than
    /// waiting for the next <see cref="SyncAutoLinkshellTabs"/> poll to notice the setting changed.</summary>
    public void RemoveAllAutoLinkshellTabs()
    {
        foreach (var tab in configuration.Tabs.Where(t => t.IsAutoLinkshellTab).ToList())
            RemoveTab(tab);
    }

    public void Save() => configuration.Save();
}
