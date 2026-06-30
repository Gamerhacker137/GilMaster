namespace GilMaster.Models;

public sealed class RecipeIngredient
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public bool CanBeHq { get; init; }

    public bool IsGatherable { get; init; }
    public bool IsCraftable { get; init; }
    public bool IsShopBuyable { get; init; }
    public long ShopPrice { get; init; }

    public long MarketMinPrice { get; set; }    // populated after Universalis fetch
    public bool MarketPriceFetched { get; set; }

    public RecipeIngredient[] SubIngredients { get; init; } = [];

    public long EstimatedUnitCost(bool assumeGatherablesFree)
    {
        if (assumeGatherablesFree && IsGatherable)
            return 0;

        // Buy from whichever is cheaper — the NPC vendor or the market board.
        long shop   = IsShopBuyable && ShopPrice > 0 ? ShopPrice : 0;
        long market = MarketPriceFetched && MarketMinPrice > 0 ? MarketMinPrice : 0;
        if (shop > 0 && market > 0) return System.Math.Min(shop, market);
        if (shop > 0)   return shop;
        if (market > 0) return market;
        return 0;
    }

    /// <summary>True when an NPC vendor sells this — useful for the shopping list.</summary>
    public bool VendorSold => IsShopBuyable && ShopPrice > 0;
}
