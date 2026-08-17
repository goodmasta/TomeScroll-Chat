using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TomeScrollChat.Services;

/// <summary>
/// Polls the game's own linkshell/cross-world-linkshell membership lists - <c>InfoProxyLinkshell</c>
/// and <c>InfoProxyCrossWorldLinkshell</c> (the same native data backing the "Linkshell"/"Cross-world
/// Linkshell" social windows and the LS1-8/CWLS1-8 chat routing itself, found via the metadata-reading
/// technique used elsewhere in this project since neither is documented) - and feeds a fresh snapshot
/// into <see cref="TabManager.SyncAutoLinkshellTabs"/> so a tab appears the moment a shell is joined
/// and disappears the moment it's left/kicked from, without needing any join/leave event (none of
/// these native proxies expose one - polling the resulting list is the only way).
/// </summary>
/// <remarks>
/// A regular linkshell slot (<c>InfoProxyLinkshell.LinkShells[i].Id</c>, non-zero when occupied) only
/// carries an id - its display name needs a separate <c>GetLinkshellName(id)</c> lookup. A
/// cross-world linkshell slot (<c>InfoProxyCrossWorldLinkshell.CrossWorldLinkshells[i].Name</c>)
/// carries its name directly, empty when the slot is unoccupied. Both lists are fixed-size (8 slots,
/// matching LS1-8/CWLS1-8), so a slot index maps 1:1 onto the chat channel/tab it represents - no
/// separate id-to-channel-index resolution needed anywhere else in this feature.
///
/// Throttled to once/second (not every frame like most other watchers in this project) - unlike those,
/// reading a slot's name here always allocates a new C# string via <c>.ToString()</c>, so polling it on
/// every single frame would allocate 16 short-lived strings/frame for a value that only ever changes on
/// an infrequent, deliberate player action (join/leave/rename a shell). A 1-second lag before a new
/// tab appears is imperceptible for that.
/// </remarks>
public sealed unsafe class LinkshellWatcherService : IDisposable
{
    private const long PollIntervalMs = 1000;
    private const int SlotCount = 8;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly TabManager tabManager;

    private long nextPollTick;
    private bool requestedDataOnce;

    public LinkshellWatcherService(IFramework framework, IPluginLog log, Configuration config, TabManager tabManager)
    {
        this.framework = framework;
        this.log = log;
        this.config = config;
        this.tabManager = tabManager;
        framework.Update += OnUpdate;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    private void OnUpdate(IFramework _)
    {
        if (!config.AutoLinkshellTabs)
            return;

        var now = Environment.TickCount64;
        if (now < nextPollTick)
            return;
        nextPollTick = now + PollIntervalMs;

        if (!requestedDataOnce)
        {
            requestedDataOnce = true;
            // Best-effort nudge, same reasoning as FriendListService's own RequestData() call - this
            // data appears to already be populated for normal chat routing regardless, but costs
            // nothing to also request explicitly in case a fresh login hasn't filled it in yet.
            try
            {
                InfoProxyLinkshell.Instance()->RequestData();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "TomeScrollChat: InfoProxyLinkshell.RequestData failed");
            }
        }

        try
        {
            var linkshellNames = ReadLinkshellNames();
            var crossWorldNames = ReadCrossWorldLinkshellNames();
            tabManager.SyncAutoLinkshellTabs(linkshellNames, crossWorldNames);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: linkshell membership poll failed");
        }
    }

    private static string?[] ReadLinkshellNames()
    {
        var names = new string?[SlotCount];
        var proxy = InfoProxyLinkshell.Instance();
        if (proxy == null)
            return names;

        var entries = proxy->LinkShells;
        for (var i = 0; i < entries.Length && i < SlotCount; i++)
        {
            var id = entries[i].Id;
            if (id == 0)
                continue;

            var name = proxy->GetLinkshellName(id).ToString();
            names[i] = string.IsNullOrEmpty(name) ? null : name;
        }

        return names;
    }

    private static string?[] ReadCrossWorldLinkshellNames()
    {
        var names = new string?[SlotCount];
        var proxy = InfoProxyCrossWorldLinkshell.Instance();
        if (proxy == null)
            return names;

        var entries = proxy->CrossWorldLinkshells;
        for (var i = 0; i < entries.Length && i < SlotCount; i++)
        {
            var name = entries[i].Name.ToString();
            names[i] = string.IsNullOrEmpty(name) ? null : name;
        }

        return names;
    }
}
