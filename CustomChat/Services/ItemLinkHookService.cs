using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Hooks the native <c>AgentChatLog::LinkItem(uint itemId)</c> function - the same function the game
/// calls internally whenever the ordinary right-click "Link" action is used on an item, from any
/// context (inventory, examine window, crafting/gathering log, etc. - everywhere "Link" appears, not
/// just the inventory bag) - so plain "Link" keeps working while the native chat log (whose hidden
/// textbox that action would otherwise write into) is suppressed.
/// </summary>
/// <remarks>
/// Superseded an earlier approach that added a separate "Link (Custom Chat)" context menu entry -
/// the user specifically wanted the ordinary "Link" button itself to work, not a second one to
/// remember to use, and this also covers every native "Link" context, not just inventory right-clicks
/// (<c>IContextMenu</c>'s <c>ContextMenuType.Inventory</c> only fires for the bag). The item id the
/// game passes here already carries FFXIV's usual HQ offset (an HQ item's id = its base/NQ id +
/// 1,000,000 - the same convention Dalamud's own <see cref="Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload"/>
/// decodes via its ItemId/RawItemId split), so it's decoded back into a base id + IsHq flag before
/// being queued, matching what
/// <see cref="Dalamud.Game.Text.SeStringHandling.SeStringBuilder.AddItemLink"/> expects.
///
/// The hook never suppresses or alters the original call - it only observes the item id after
/// forwarding to the real function first, so anything else the game does internally when "Link" is
/// used (setting <c>AgentChatLog.LinkedItem</c>/<c>ContextItemId</c>, etc.) keeps working exactly as
/// before. Detour logic is wrapped in try/catch so a failure here can never break the native call.
/// The hooked address comes from FFXIVClientStructs' own sig-scanned
/// <c>AgentChatLog.Addresses.LinkItem</c> (resolved the same way Dalamud/FFXIVClientStructs resolve
/// every native member function they wrap), not a hand-rolled signature.
/// </remarks>
public sealed unsafe class ItemLinkHookService : IDisposable
{
    private const uint HqOffset = 1_000_000;

    private delegate void LinkItemDelegate(AgentChatLog* agent, uint itemId);

    private readonly Hook<LinkItemDelegate>? hook;
    private readonly IPluginLog log;
    private readonly Action<uint, bool> onLinkItem;

    public ItemLinkHookService(IGameInteropProvider gameInteropProvider, IPluginLog log, Action<uint, bool> onLinkItem)
    {
        this.log = log;
        this.onLinkItem = onLinkItem;

        try
        {
            var address = AgentChatLog.Addresses.LinkItem.Value;
            hook = gameInteropProvider.HookFromAddress<LinkItemDelegate>(address, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to hook AgentChatLog.LinkItem - the native \"Link\" button won't be redirected into chat");
        }
    }

    private void Detour(AgentChatLog* agent, uint itemId)
    {
        try
        {
            var isHq = itemId is >= HqOffset and < HqOffset * 2;
            var baseId = isHq ? itemId - HqOffset : itemId;
            onLinkItem(baseId, isHq);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to handle a native item link (item id {ItemId})", itemId);
        }
        finally
        {
            hook!.Original(agent, itemId);
        }
    }

    public void Dispose() => hook?.Dispose();
}
