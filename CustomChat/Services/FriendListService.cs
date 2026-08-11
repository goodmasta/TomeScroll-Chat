using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace CustomChat.Services;

/// <summary>
/// Looks up whether a chat message's sender is on the local player's friends list, for the
/// configurable emoji marker prefix. The game's <see cref="InfoProxyFriendList"/> entries don't
/// carry the character's name directly (only content id / world), so rather than enumerating and
/// resolving names ourselves, this uses the game's own <c>GetEntryByName</c> lookup - which needs a
/// world *row id*, not a name, so a small Name-&gt;World-id index is built once from the World
/// Excel sheet via <see cref="IDataManager"/>.
/// </summary>
public sealed unsafe class FriendListService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private Dictionary<string, ushort>? worldIdsByName;

    public FriendListService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;

        try
        {
            // Best-effort: the friend list's native data may only be populated once the game has
            // fetched it - this nudges that along, but if the player has opened their own friend
            // list at least once this session it's likely already loaded regardless.
            InfoProxyFriendList.Instance()->RequestData();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to request friend list data");
        }
    }

    /// <summary>Same as <see cref="IsFriend"/> but from an already-combined "Name@World" key.</summary>
    public bool IsFriendKey(string key)
    {
        var at = key.IndexOf('@');
        return at > 0 && IsFriend(key[..at], key[(at + 1)..]);
    }

    public bool IsFriend(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return false;

        try
        {
            var worldId = ResolveWorldId(world);
            if (worldId == null)
                return false;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return false;

            var entry = proxy->GetEntryByName(name, worldId.Value);
            return entry != null && entry->ContentId != 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: friend list lookup failed for {Name}@{World}", name, world);
            return false;
        }
    }

    private ushort? ResolveWorldId(string worldName)
    {
        worldIdsByName ??= BuildWorldIndex();
        return worldIdsByName.TryGetValue(worldName, out var id) ? id : null;
    }

    private Dictionary<string, ushort> BuildWorldIndex()
    {
        var dict = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
            if (sheet != null)
            {
                foreach (var world in sheet)
                {
                    var name = world.Name.ToString();
                    if (!string.IsNullOrEmpty(name))
                        dict[name] = (ushort)world.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to build world name index for friend list lookups");
        }

        return dict;
    }
}
