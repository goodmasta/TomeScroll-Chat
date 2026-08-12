using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Finds a player character by name+world among currently-rendered game objects - shared by every
/// feature that, like the game's own right-click actions, only works on someone actually nearby
/// (<see cref="FriendRequestService"/>, <see cref="AdventurerPlateService"/>), unlike tells/party
/// invites/translation which all work purely by name from a chat message alone.
/// </summary>
public static class NearbyPlayerLookup
{
    public static IGameObject? Find(IObjectTable objectTable, string name, string world)
    {
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.Pc || obj is not IPlayerCharacter pc)
                continue;

            if (!string.Equals(obj.Name.TextValue, name, StringComparison.Ordinal))
                continue;

            var objWorld = pc.HomeWorld.ValueNullable?.Name.ToString();
            if (string.Equals(objWorld, world, StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }
}
