namespace GilMaster.Models;

/// <summary>A housing furnishing with its market intelligence for the Furniture tab.</summary>
public sealed class FurnitureItem
{
    public uint   ItemId   { get; init; }
    public ushort IconId   { get; init; }
    public string Name     { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty; // ItemUICategory (Tables, Rugs, …)
    public bool   Exterior { get; init; }                 // outdoor/yard vs interior
    public bool   Dyeable  { get; init; }

    public bool   Craftable   { get; init; }
    public uint   RecipeId    { get; init; }
    public string CraftJobName { get; init; } = string.Empty;

    public long   GoingPrice { get; init; }   // 7-day median sale price
    public long   MinListing { get; init; }   // cheapest current listing
    public int    Sellers    { get; init; }   // active listings (competition)
    public long   WeeklySold { get; init; }   // units sold in the last 7 days
    public sbyte  TrendDir   { get; init; }
    public double TrendPct   { get; init; }

    public long   MaterialCost { get; set; }  // set by enrichment for craftables (0 = unknown)

    // Headline "what's worth selling" metric: price × how much it moves per week.
    public long RevenuePerWeek => GoingPrice * WeeklySold;

    // Net profit per craft (only meaningful for craftables we've costed).
    public long NetPerCraft => Craftable && MaterialCost > 0 ? GoingPrice - MaterialCost : GoingPrice;

    public int CompetitionTier =>
        Sellers <= 0  ? 0 :
        Sellers <= 4  ? 1 :
        Sellers <= 12 ? 2 :
                        3;
}
