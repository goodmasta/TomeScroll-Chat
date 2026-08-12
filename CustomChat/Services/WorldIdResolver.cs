using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Several native lookups/calls (friend list entries, party invites) need a world *row id*, not a
/// world name - this builds that Name-&gt;id index once from the World Excel sheet and shares it,
/// rather than every caller building its own copy (originally lived only in
/// <see cref="FriendListService"/>, extracted once <see cref="PartyInviteService"/> needed the same
/// lookup).
/// </summary>
public sealed class WorldIdResolver
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private Dictionary<string, ushort>? worldIdsByName;

    public WorldIdResolver(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public ushort? Resolve(string worldName)
    {
        if (string.IsNullOrEmpty(worldName))
            return null;

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
            log.Warning(ex, "CustomChat: failed to build world name index");
        }

        return dict;
    }
}
