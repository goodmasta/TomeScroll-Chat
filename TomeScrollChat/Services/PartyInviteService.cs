using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TomeScrollChat.Services;

/// <summary>
/// Sends a party invite by name+world, via the same native entry point the vanilla `/invite
/// Name@World` slash command and the target/party-list right-click "Invite to Party" both ultimately
/// call: <c>InfoProxyPartyInvite.InviteToParty(contentId, name, worldId)</c> (confirmed to exist via
/// reflection against the installed FFXIVClientStructs, alongside the by-content-id overloads used
/// for e.g. Party Finder). Content id is passed as 0 (unknown) since a chat message's sender is
/// usually not actually targetable/nearby - the name+world overload is exactly what makes the plain
/// slash command work cross-zone without needing to target anyone first, and this mirrors that.
/// </summary>
public sealed unsafe class PartyInviteService
{
    private readonly WorldIdResolver worldIdResolver;
    private readonly IPluginLog log;

    public PartyInviteService(WorldIdResolver worldIdResolver, IPluginLog log)
    {
        this.worldIdResolver = worldIdResolver;
        this.log = log;
    }

    public bool Invite(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return false;

        var worldId = worldIdResolver.Resolve(world);
        if (worldId == null)
        {
            log.Warning("TomeScrollChat: couldn't resolve world '{World}' for a party invite to {Name}", world, name);
            return false;
        }

        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            if (proxy == null)
                return false;

            return proxy->InviteToParty(0, name, worldId.Value);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a party invite to {Name}@{World}", name, world);
            return false;
        }
    }
}
