using System;
using System.Collections.Generic;
using System.Linq;
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

    public void Save() => configuration.Save();
}
