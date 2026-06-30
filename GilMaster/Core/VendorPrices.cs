using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// NPC vendor (gil shop) prices, read straight from the game's own data — every item sold by
/// a gil-shop NPC and the gil it costs (Item.PriceMid). GilMaster uses this to cost crafting
/// materials at the cheaper of the vendor price vs the market board, so "buy it from the NPC"
/// is reflected in profit/shopping-list numbers. No external plugin needed.
/// </summary>
public static class VendorPrices
{
    private static Dictionary<uint, uint>? _prices;

    /// <summary>Gil price an NPC sells this item for, or 0 if no gil-shop sells it.</summary>
    public static uint Get(uint itemId)
    {
        _prices ??= Build();
        return _prices.TryGetValue(itemId, out var p) ? p : 0u;
    }

    public static bool IsVendorSold(uint itemId) => Get(itemId) > 0;

    public static int Count { get { _prices ??= Build(); return _prices.Count; } }

    private static Dictionary<uint, uint> Build()
    {
        var prices = new Dictionary<uint, uint>();
        var items  = Service.DataManager.GetExcelSheet<Item>();
        try
        {
            // GilShopItem is a subrow sheet: each gil shop has a list of items it sells.
            var shop = Service.DataManager.GetSubrowExcelSheet<GilShopItem>();
            foreach (var row in shop)
                foreach (var sub in row)
                {
                    var id = sub.Item.RowId;
                    if (id == 0 || prices.ContainsKey(id)) continue;
                    var it = items.GetRowOrDefault(id);
                    if (it is { } item && item.PriceMid > 0) prices[id] = item.PriceMid;
                }
            Service.Log.Information($"[GilMaster] Vendor price index: {prices.Count} gil-shop items.");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Vendor price index failed"); }
        return prices;
    }
}
