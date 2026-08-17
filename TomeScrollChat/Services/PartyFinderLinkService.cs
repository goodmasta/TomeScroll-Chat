using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TomeScrollChat.Services;

/// <summary>
/// Opens a Party Finder listing link (see <see cref="Windows.ChatMessageRenderer"/>) via the native
/// <c>AgentLookingForGroup.OpenListing(ulong)</c> call, the same entry point the game's own chat log
/// uses when you click one - opens the native listing detail window directly, no need to search/
/// re-derive it by hand. <c>ListingId</c> on the captured <c>PartyFinderPayload</c> is a 32-bit id;
/// <c>OpenListing</c> takes a 64-bit one, but a plain widening conversion is all that's needed (the
/// listing id itself doesn't carry extra high bits of its own to lose).
/// </summary>
public sealed unsafe class PartyFinderLinkService
{
    private readonly IPluginLog log;

    public PartyFinderLinkService(IPluginLog log)
    {
        this.log = log;
    }

    public void OpenListing(uint listingId)
    {
        try
        {
            AgentLookingForGroup.Instance()->OpenListing(listingId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to open Party Finder listing {ListingId}", listingId);
        }
    }
}
