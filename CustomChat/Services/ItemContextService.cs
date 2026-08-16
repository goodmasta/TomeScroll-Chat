using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace CustomChat.Services;

/// <summary>
/// Native actions offered from an item link's left-click context menu (see
/// <see cref="Windows.ChatMessageRenderer"/>) - opening the item search window pre-filled for this
/// item, and opening the recipe search for recipes that use it. Mirrors the same native calls
/// ChatTwo's own item-link context menu uses (<c>GameFunctions.Context.SearchForItem</c>/
/// <c>SearchForRecipesUsingItem</c>), confirmed to exist with matching purpose in this project's own
/// FFXIVClientStructs version via the metadata-reading reflection technique.
/// </summary>
public sealed unsafe class ItemContextService
{
    private readonly IPluginLog log;

    public ItemContextService(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>Opens the native item search window filtered to this item. Takes the *base* item id
    /// (not the HQ/collectible-offset raw one) - this project's FFXIVClientStructs version's
    /// <c>SearchForItem</c> takes the HQ/collectible inclusion as its own separate bool parameter
    /// instead of folding it into the id the way the raw id convention does elsewhere.</summary>
    public void SearchForItem(uint baseItemId)
    {
        try
        {
            ItemFinderModule.Instance()->SearchForItem(baseItemId, true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to open item search for item {ItemId}", baseItemId);
        }
    }

    /// <summary>Opens the recipe search filtered to recipes using this item as a material - a no-op
    /// (empty results) if the item was never used in any recipe, which is harmless, so this doesn't
    /// try to pre-filter which items are worth offering the option for.</summary>
    public void SearchForRecipesUsingItem(uint baseItemId)
    {
        try
        {
            AgentRecipeProductList.Instance()->SearchForRecipesUsingItem(baseItemId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to open recipe search for item {ItemId}", baseItemId);
        }
    }
}
