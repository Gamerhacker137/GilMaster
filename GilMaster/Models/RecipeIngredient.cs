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
        if (IsShopBuyable && ShopPrice > 0)
            return ShopPrice;
        if (MarketPriceFetched && MarketMinPrice > 0)
            return MarketMinPrice;
        return 0;
    }
}
