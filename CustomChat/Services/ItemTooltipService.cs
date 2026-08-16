using System;
using Dalamud.Bindings.ImGui;
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

    /// <summary>Opens/closes/switches the native tooltip based on whatever was hovered this frame, and
    /// keeps it following the mouse cursor while it stays open - called once per real frame, after
    /// every window has drawn.</summary>
    public void EndFrame()
    {
        if (hoveredRawItemId != openRawItemId)
        {
            if (hoveredRawItemId == 0)
                Close();
            else
                Open(hoveredRawItemId, hoveredKind);
        }
        else if (hoveredRawItemId != 0)
        {
            // Same item still hovered as last frame - Open() already positioned it once, but a real
            // tooltip has to keep tracking the cursor as it moves, not just where it happened to be the
            // instant the item was first hovered.
            UpdatePosition();
        }
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

            // Best-effort attempt at fixing the tooltip rendering *behind* this plugin's own ImGui
            // window (reported 2026-08-13): a real native tooltip attached via AtkTooltipManager gets
            // drawn through a dedicated always-on-top tooltip pass, but this addon is only opened
            // through the normal window-layer path (Show() above), since attaching a real tooltip needs
            // a target AtkResNode this plugin's ImGui-drawn text doesn't have one of. DepthLayer only
            // orders this addon relative to *other native addons*, not relative to Dalamud's ImGui
            // overlay, so this is a low-confidence try, not a verified fix - if it doesn't help, this
            // native-vs-ImGui compositing order likely isn't something a plugin can control at all.
            addon->SetDepthLayer(15);

            openRawItemId = rawItemId;
            UpdatePosition();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to open the native item tooltip for item {ItemId}", rawItemId);
        }
    }

    /// <summary>Positions the tooltip addon just above the mouse cursor (per explicit request - keeps
    /// it clear of the item link text itself and the cursor, and above rather than below reads more
    /// like a normal tooltip anyway). Native item-detail addons aren't automatically mouse-following
    /// the way this same window is when opened by hovering a real inventory slot - that positioning is
    /// driven by <c>AtkTooltipManager</c> being attached to the slot's own <c>AtkResNode</c>, which chat
    /// text drawn by this plugin's own ImGui doesn't have one of, so this has to be done by hand
    /// instead. Uses the addon's own actual rendered height (<c>GetScaledHeight</c>), not a guessed
    /// fixed offset, so it sits flush above the cursor regardless of how tall a given item's tooltip
    /// turns out to be - on the very first frame it opens the height may still reflect the previously
    /// shown item (or be 0 for the first tooltip ever shown this session), self-correcting within a
    /// frame or two as this runs again every frame the same item stays hovered.</summary>
    private void UpdatePosition()
    {
        try
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName);
            if (addon == null)
                return;

            var mouse = ImGui.GetMousePos();
            const float margin = 12f;
            var height = addon->GetScaledHeight(true);
            addon->SetPosition((short)(mouse.X + 16), (short)(mouse.Y - height - margin));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to reposition the native item tooltip");
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
