using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TomeScrollChat.Services;

/// <summary>
/// Watches <c>AgentChatLog.LinkedPartyFinderId</c> every frame for the listing the native Party
/// Finder window's own "Relay" action last set - the same polling approach
/// <see cref="NativeItemLinkWatcher"/> already uses for <c>LinkedItem</c>, reused here because
/// "Relay" populates <c>AgentChatLog</c> the exact same way the item "Link" action populates
/// <c>LinkedItem</c> (confirmed via the metadata tool: <c>LinkedPartyFinderId</c> (UInt64) and
/// <c>LinkedPartyFinderLeaderName</c> sit right next to <c>LinkedItem</c> on the same struct). Without
/// this, "Relay" writes its link straight into the (hidden) native chat log's own input box, which this
/// plugin never reads from - so the click silently did nothing visible from here, while the game still
/// queued the link for its own hidden textbox.
///
/// <para>Captures the raw payload bytes sitting in that same textbox the same frame a new listing id
/// is detected - identical technique to <see cref="NativeItemLinkWatcher"/>, for the same reason: reading
/// the game's own already-encoded bytes sidesteps ever needing to reconstruct the payload (and the
/// <c>PartyFinderLinkType</c> guess that would require) by hand.</para>
/// </summary>
public sealed unsafe class NativePartyFinderLinkWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action<ulong, string, byte[]?> onPartyFinderLinked;
    private ulong lastListingId;

    public NativePartyFinderLinkWatcher(IFramework framework, IGameGui gameGui, IPluginLog log, Action<ulong, string, byte[]?> onPartyFinderLinked)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.onPartyFinderLinked = onPartyFinderLinked;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        var listingId = agent->LinkedPartyFinderId;
        if (listingId == 0 || listingId == lastListingId)
            return;

        lastListingId = listingId;
        var leaderName = agent->LinkedPartyFinderLeaderName.ToString();
        log.Debug("TomeScrollChat: native party finder relay detected - listing {ListingId} ({LeaderName})", listingId, leaderName);
        onPartyFinderLinked(listingId, leaderName, TryCaptureNativePayloadBytes());
    }

    private byte[]? TryCaptureNativePayloadBytes()
    {
        try
        {
            var addon = gameGui.GetAddonByName<AddonChatLog>("ChatLog");
            if (addon == null || addon->TextInput == null)
                return null;

            var span = addon->TextInput->RawString.AsSpan();
            if (span.Length == 0)
                return null;

            var bytes = span.ToArray();

            // Cleared immediately, same as NativeItemLinkWatcher does for the same textbox - otherwise
            // it leaks into NativeChatInputWatcher's own leak-through detection on a later frame.
            addon->TextInput->SetText(string.Empty);
            var chatLogAgent = AgentChatLog.Instance();
            if (chatLogAgent != null)
                chatLogAgent->HideLogWindow();

            return bytes;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to capture the native party finder link payload bytes");
            return null;
        }
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
