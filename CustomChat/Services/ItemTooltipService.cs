using System;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomChat.Services;

/// <summary>
/// Opens the game's own native item tooltip/detail window while hovering an item link in chat -
/// the same window the native chat log's own item links use, via <c>AgentItemDetail</c> field
/// manipulation. Reused from ChatTwo's own implementation of this exact feature (per the user's
/// pointer at it) rather than guessed: earlier in this project's history, reflection alone only
/// turned up undocumented public fields on <c>AgentItemDetail</c> with no verified-safe way to set
/// them, which was reason enough at the time to scope item links down to a "copy name to clipboard"
/// fallback instead. ChatTwo's own working code supplied the missing piece - confirmed the exact
/// field values to set, and confirmed (via this project's own metadata-reading reflection technique)
/// that the two fields ChatTwo pokes via raw byte offsets (<c>0x21A</c>/<c>0x21E</c>, presumably
/// undocumented in whatever FFXIVClientStructs version ChatTwo targets) are named <c>Flag2</c>/
/// <c>Flag3</c> in this project's version - used here as named fields instead of hand-computed
/// offsets, which can never read/write outside the struct's real bounds even if the specific
/// flag-to-offset mapping guess turns out wrong (unlike a raw pointer+offset poke, which could).
/// </summary>
public sealed unsafe class ItemTooltipService
{
    private const string AddonName = "ItemDetail";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private uint openRawItemId;
    private uint hoveredRawItemId;
    private ItemKind hoveredKind;

    public ItemTooltipService(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Call from an item link's hover check, every frame it's actually hovered - see
    /// <see cref="Windows.ChatMessageRenderer"/>. Doesn't open anything itself (multiple windows can
    /// draw messages in the same real frame; opening/closing per-draw-call rather than per-frame could
    /// otherwise flicker the tooltip closed between them) - just records the target for
    /// <see cref="EndFrame"/> to act on once the whole frame's drawing is done.</summary>
    public void NotifyHovered(uint rawItemId, ItemKind kind)
    {
        hoveredRawItemId = rawItemId;
        hoveredKind = kind;
    }

    /// <summary>Resets the per-frame hover target - called once per real frame, before any window
    /// draws (see <c>Plugin</c>'s <c>UiBuilder.Draw</c> wrapper).</summary>
    public void BeginFrame() => hoveredRawItemId = 0;

    /// <summary>Opens/closes/switches the native tooltip based on whatever was hovered this frame -
    /// called once per real frame, after every window has drawn.</summary>
    public void EndFrame()
    {
        if (hoveredRawItemId == openRawItemId)
            return;

        if (hoveredRawItemId == 0)
            Close();
        else
            Open(hoveredRawItemId, hoveredKind);
    }

    private void Open(uint rawItemId, ItemKind kind)
    {
        try
        {
            var agent = AgentItemDetail.Instance();
            var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName);
            if (agent == null || addon == null)
                return;

            agent->DetailKind = kind == ItemKind.EventItem ? DetailKind.KeyItem : DetailKind.Item;
            agent->TypeOrId = rawItemId;
            agent->Index = 0;
            agent->Flag1 &= 0xEF;
            agent->ItemId = rawItemId;
            agent->Flag2 = 1;
            agent->Flag3 = 0;
            agent->AddonId = addon->Id;

            AtkStage.Instance()->TooltipManager.TooltipType |= 2;
            addon->Show(false, 15);

            openRawItemId = rawItemId;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to open the native item tooltip for item {ItemId}", rawItemId);
        }
    }

    private void Close()
    {
        try
        {
            // Hide the addon first (matches ChatTwo's own ordering) - avoids the "addon close" sound
            // that plays if the agent event is fired while the addon is still visible.
            var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName);
            if (addon != null)
                addon->Hide(true, false, 0);

            var agent = AgentItemDetail.Instance();
            if (agent == null)
                return;

            var eventData = stackalloc AtkValue[1];
            var atkValues = stackalloc AtkValue[1];
            atkValues->Type = AtkValueType.Int;
            atkValues->Int = -1;
            agent->ReceiveEvent(eventData, atkValues, 1, 1);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to close the native item tooltip");
        }
        finally
        {
            openRawItemId = 0;
        }
    }
}
