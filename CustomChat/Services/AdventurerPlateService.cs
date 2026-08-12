using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Opens a player's Adventurer Plate via <c>AgentCharaCard.OpenCharaCard</c> (confirmed via
/// reflection). Two ways in, tried in order:
/// 1. By content id, if they're a friend (<see cref="FriendListService.TryGetContentId"/>) - works
///    regardless of whether they're actually rendered nearby, confirmed by the user: the game's own
///    friends list UI can open a plate for a friend who isn't nearby, so this can too.
/// 2. By game object, if they're currently rendered nearby (<see cref="NearbyPlayerLookup"/>) -
///    the fallback for anyone not on the friends list, same real-world constraint the game's own
///    right-click "View Adventurer Plate" has outside of a list that already carries a content id.
/// </summary>
public sealed unsafe class AdventurerPlateService
{
    private readonly IObjectTable objectTable;
    private readonly FriendListService friendListService;

    public AdventurerPlateService(IObjectTable objectTable, FriendListService friendListService)
    {
        this.objectTable = objectTable;
        this.friendListService = friendListService;
    }

    /// <returns>False if neither lookup found the player - the caller should tell the user that,
    /// rather than silently doing nothing.</returns>
    public bool TryOpen(string name, string world)
    {
        var agent = AgentCharaCard.Instance();
        if (agent == null)
            return false;

        if (friendListService.TryGetContentId(name, world, out var contentId))
        {
            agent->OpenCharaCard(contentId);
            return true;
        }

        var obj = NearbyPlayerLookup.Find(objectTable, name, world);
        if (obj == null)
            return false;

        agent->OpenCharaCard((GameObject*)obj.Address);
        return true;
    }
}
