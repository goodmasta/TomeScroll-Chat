using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace TomeScrollChat.Models;

public enum ChatPayloadLinkType
{
    MapLink,
    Item,
    PartyFinder,

    /// <summary>An auto-translate dictionary phrase (<see cref="Dalamud.Game.Text.SeStringHandling.Payloads.AutoTranslatePayload"/>) -
    /// unlike the other three, this one's display text (wrapped in the same guillemets the native
    /// chat log uses, see <see cref="Services.ChatCaptureService.BuildBodyAndPayloadLinks"/>) is
    /// *inserted* into <see cref="ChatMessageRecord.Body"/> at capture time rather than merely located
    /// in it - the game gives it no separate display <c>TextPayload</c> of its own to find the way a
    /// map/item link does, so without this it doesn't appear in the flattened body at all. Purely
    /// display styling - no click action, unlike the other three.</summary>
    AutoTranslate,
}

/// <summary>
/// A rich SeString payload (map/flag coordinate link, item link, auto-translate phrase) found in a
/// message at capture time, with its position in <see cref="ChatMessageRecord.Body"/> so it can be
/// rendered/styled as its own widget instead of plain text. <see cref="ChatMessageRecord.Body"/> is
/// flattened plain text built by <see cref="Services.ChatCaptureService.BuildBodyAndPayloadLinks"/>
/// (equivalent to <c>SeString.TextValue</c> - only <c>TextPayload</c> text - for every message that
/// doesn't use the auto-translate dictionary; see <see cref="ChatPayloadLinkType.AutoTranslate"/> for
/// the one case that isn't) by the time it's captured, so this is the only place that structure
/// survives. <see cref="MapLink"/>/<see cref="Item"/>/<see cref="PartyFinder"/> themselves aren't
/// directly persisted (Dalamud's SDK payload types don't round-trip through SQLite/JSON as-is) -
/// <see cref="Services.ChatHistoryService"/> stores just enough raw data (territory/map ids + raw
/// X/Y, item id + kind, or listing id + link type) to reconstruct an equivalent payload object on
/// reload via the same constructors used to build one from scratch elsewhere in this project.
/// <see cref="ChatPayloadLinkType.AutoTranslate"/> needs none of that - its display text is already
/// baked into <see cref="ChatMessageRecord.Body"/> itself, so only <see cref="Start"/>/<see cref="Length"/>/
/// <see cref="Type"/> round-trip for it.
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

    /// <summary>Set when <see cref="Type"/> is <see cref="ChatPayloadLinkType.PartyFinder"/> - its
    /// <c>ListingId</c> is passed to <see cref="Services.PartyFinderLinkService"/> on click to open the
    /// native listing detail directly, same as clicking it in the native chat log would.</summary>
    public PartyFinderPayload? PartyFinder { get; init; }
}
