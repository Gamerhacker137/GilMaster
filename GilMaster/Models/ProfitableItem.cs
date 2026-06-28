namespace GilMaster.Models;

public sealed class ProfitableItem
{
    public uint ItemId { get; init; }
    public uint RecipeId { get; init; }
    public ushort IconId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int RecipeLevel { get; init; }
    public int CraftJobId { get; init; }
    public string CraftJobName { get; init; } = string.Empty;
    public int AmountResult { get; init; } = 1;  // items produced per synth

    public long MinListingPrice { get; init; }
    public long MinListingHqPrice { get; init; }
    // Realistic going sale price (median of recent actual sales) — robust to lowball listings.
    public long RealisticNqPrice { get; init; }
    public long RealisticHqPrice { get; init; }
    public double SaleVelocity { get; init; }
    public double SaleVelocityHq { get; init; }
    public long RecentUnitsSold { get; init; }

    public long EstimatedMaterialCost { get; set; }

    // Prices used for profit/display: realistic sale price, falling back to the cheapest listing.
    public long DisplayNqPrice => RealisticNqPrice > 0 ? RealisticNqPrice : MinListingPrice;
    public long DisplayHqPrice => RealisticHqPrice > 0 ? RealisticHqPrice
                                : MinListingHqPrice > 0 ? MinListingHqPrice
                                : DisplayNqPrice;

    // Profit per synth — accounts for yield (AmountResult items per craft)
    public long ProfitNq => DisplayNqPrice * AmountResult - EstimatedMaterialCost;
    public long ProfitHq => DisplayHqPrice * AmountResult - EstimatedMaterialCost;

    // Legacy — prefer HQ price when available (used for backward-compat and cache serialisation)
    public long Profit => ProfitHq;

    // Ranking score: realistic profit × how fast it sells = gil/day you can actually capture.
    // This weights both "sells for a lot" and "lots of people buy it" (demand).
    public double ProfitScore => ProfitHq * BestVelocity;

    // Daily market revenue flow (price × demand), ignoring craft cost — pure "hot item" signal.
    public double RevenuePerDay => DisplayHqPrice * AmountResult * BestVelocity;

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

    // ── Competition signal ────────────────────────────────────────────────
    // Number of separate listings currently undercutting each other on the board.
    // Few sellers on a high-value item = easy money; a crowded board = a price war.
    public int  ActiveListings { get; init; }
    public long UnitsForSale   { get; init; }

    // 0 = wide open, 1 = light, 2 = busy, 3 = saturated. Used for colour-coding.
    public int CompetitionTier =>
        ActiveListings <= 0  ? 0 :
        ActiveListings <= 4  ? 1 :
        ActiveListings <= 12 ? 2 :
                               3;
}
