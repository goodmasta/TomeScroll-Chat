using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Watches <c>AgentChatLog.LinkedItem</c> every frame for the item the native right-click "Link"
/// action last set, the same polling approach <see cref="NativeChatInputWatcher"/> already uses for
/// other <c>AgentChatLog</c> state this plugin has no event for.
/// </summary>
/// <remarks>
/// Replaced an earlier attempt that hooked <c>AgentChatLog::LinkItem(uint itemId)</c> directly via
/// <c>IGameInteropProvider</c> - it installed cleanly (confirmed via its own log line) but never once
/// fired despite repeated live testing, meaning that isn't actually the function the native "Link"
/// action calls. Rather than guess at another function to hook blind (a wrong guess there risks
/// crashing the game, unlike a wrong guess here, which just does nothing), this reads the *result*
/// state the action leaves behind instead - <c>LinkedItem</c> is exactly what its name says, and its
/// well-defined accessor methods (<c>IsEmpty</c>/<c>GetBaseItemId</c>/<c>IsHighQuality</c>) don't
/// require knowing which internal code path actually populated it. Matches the same approach ChatTwo
/// itself uses for this feature (per the user, who pointed at ChatTwo's implementation directly): a
/// literal "&lt;link&gt;" placeholder appears in the compose box the moment an item is linked, and gets
/// swapped for the real link only at send time (see <see cref="ChatSendService"/>) - this only has to
/// notice *that* an item was linked and which one, not synthesize the link itself.
/// </remarks>
public sealed unsafe class NativeItemLinkWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly Action<uint, bool> onItemLinked;
    private uint lastItemId;
    private bool lastIsHq;

    public NativeItemLinkWatcher(IFramework framework, Action<uint, bool> onItemLinked)
    {
        this.framework = framework;
        this.onItemLinked = onItemLinked;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null || agent->LinkedItem.IsEmpty())
            return;

        var itemId = agent->LinkedItem.GetBaseItemId();
        var isHq = agent->LinkedItem.IsHighQuality();
        if (itemId == 0 || (itemId == lastItemId && isHq == lastIsHq))
            return;

        lastItemId = itemId;
        lastIsHq = isHq;
        onItemLinked(itemId, isHq);
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
