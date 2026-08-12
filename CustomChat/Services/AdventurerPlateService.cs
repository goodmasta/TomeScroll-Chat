using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Opens a player's Adventurer Plate via <c>AgentCharaCard.OpenCharaCard(GameObject*)</c> (confirmed
/// via reflection - the agent also has an overload taking a raw content id, but nothing here can
/// resolve an arbitrary chat sender's content id, only their name+world). Same real-world
/// constraint as <see cref="FriendRequestService"/>: the player has to actually be rendered nearby,
/// same as the game's own right-click "View Adventurer Plate" would require.
/// </summary>
public sealed unsafe class AdventurerPlateService
{
    private readonly IObjectTable objectTable;

    public AdventurerPlateService(IObjectTable objectTable)
    {
        this.objectTable = objectTable;
    }

    /// <returns>False if the player isn't currently visible nearby - the caller should tell the user
    /// that, rather than silently doing nothing.</returns>
    public bool TryOpen(string name, string world)
    {
        var obj = NearbyPlayerLookup.Find(objectTable, name, world);
        if (obj == null)
            return false;

        var agent = AgentCharaCard.Instance();
        if (agent == null)
            return false;

        agent->OpenCharaCard((GameObject*)obj.Address);
        return true;
    }
}
