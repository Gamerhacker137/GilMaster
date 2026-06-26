namespace GilMaster.Models;

public sealed class ProfitableItem
{
    public uint ItemId { get; init; }
    public uint RecipeId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int RecipeLevel { get; init; }
    public int CraftJobId { get; init; }
    public string CraftJobName { get; init; } = string.Empty;
    public int AmountResult { get; init; } = 1;  // items produced per synth

    public long MinListingPrice { get; init; }
    public long MinListingHqPrice { get; init; }
    public double SaleVelocity { get; init; }
    public double SaleVelocityHq { get; init; }

    public long EstimatedMaterialCost { get; set; }

    // Profit per synth — accounts for yield (AmountResult items per craft)
    public long ProfitNq => MinListingPrice * AmountResult - EstimatedMaterialCost;
    public long ProfitHq => (MinListingHqPrice > 0 ? MinListingHqPrice : MinListingPrice) * AmountResult - EstimatedMaterialCost;

    // Legacy — prefer HQ price when available (used for backward-compat and cache serialisation)
    public long Profit => ProfitHq;
    public double ProfitScore => ProfitHq * BestVelocity;

    // Per-quality gil/hour helpers — callers choose which to display
    public long GetGilPerHour(bool preferHq) => (long)((preferHq ? ProfitHq : ProfitNq) * CraftsPerHour);
    private double CraftsPerHour => 3600.0 / EstimatedCraftSeconds;
    private double EstimatedCraftSeconds =>
        RecipeLevel >= 81 ? 63.0 :
        RecipeLevel >= 51 ? 51.0 :
                            39.0;

    public long GetProfit(bool preferHq) => preferHq ? ProfitHq : ProfitNq;

    private double BestVelocity => SaleVelocityHq > 0 ? SaleVelocityHq : SaleVelocity;

    public bool HasActiveListings { get; init; }
}
