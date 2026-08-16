using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace CustomChat.Models;

public enum ChatPayloadLinkType
{
    MapLink,
    Item,
}

/// <summary>
/// A rich SeString payload (map/flag coordinate link, item link) found in a message at capture
/// time, with its position in <see cref="ChatMessageRecord.Body"/> so it can be rendered as its own
/// clickable widget instead of plain text. <see cref="ChatMessageRecord.Body"/> is already flattened
/// plain text (<c>SeString.TextValue</c>, which only concatenates <c>TextPayload</c> text - every
/// other payload type, map/item links included, contributes zero characters) by the time it's
/// captured, so this is the only place that structure survives. <see cref="MapLink"/>/<see cref="Item"/>
/// themselves aren't directly persisted (Dalamud's SDK payload types don't round-trip through
/// SQLite/JSON as-is) - <see cref="Services.ChatHistoryService"/> stores just enough raw data
/// (territory/map ids + raw X/Y, or item id + kind) to reconstruct an equivalent payload object on
/// reload via the same constructors used to build one from scratch elsewhere in this project.
/// </summary>
public sealed class ChatPayloadLink
{
    /// <summary>Character offset into <see cref="ChatMessageRecord.Body"/> where this link's display
    /// text starts (the <c>TextPayload</c> the game auto-generates immediately after the link
    /// marker payload itself, e.g. "Coerthas Central Highlands (12.3, 45.6)" or an item's name).</summary>
    public required int Start { get; init; }

    public required int Length { get; init; }

    public required ChatPayloadLinkType Type { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="ChatPayloadLinkType.MapLink"/> - passed
    /// straight to <see cref="Dalamud.Plugin.Services.IGameGui.OpenMapWithMapLink(MapLinkPayload)"/>
    /// on click, no need to re-derive territory/map/coordinates by hand.</summary>
    public MapLinkPayload? MapLink { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="ChatPayloadLinkType.Item"/> - its
    /// <c>RawItemId</c>/<c>Kind</c> are passed to <see cref="Services.ItemTooltipService"/> on hover to
    /// open the real native item tooltip, no need to re-derive the HQ/collectible id offset by hand.</summary>
    public ItemPayload? Item { get; init; }
}
