namespace GilMaster.Models;

/// <summary>A gatherable raw material ranked by how much gil it makes — for the gathering profit view.</summary>
public sealed class GatherProfitItem
{
    public uint   ItemId        { get; init; }
    public ushort Icon          { get; init; }
    public string Name          { get; init; } = string.Empty;
    public int    RequiredLevel { get; init; }
    public int    GatheringType { get; init; }   // 0/1 = Miner, 2/3 = Botanist
    public string JobName => GatheringType is 0 or 1 ? "MIN" : "BTN";

    // Best node location for this item (where to gather it).
    public string Zone           { get; init; } = string.Empty;
    public uint   TerritoryId    { get; init; }
    public uint   MapId          { get; init; }
    public float  RawX           { get; init; }
    public float  RawZ           { get; init; }
    public float  DisplayX       { get; init; }
    public float  DisplayY       { get; init; }
    public bool   IsTimed        { get; init; }
    public uint   UptimeBitfield { get; init; }
    public string? Aetheryte     { get; init; }
    public uint   AetheryteId    { get; init; }

    public long  GoingPrice { get; init; }   // 7-day median sale price
    public long  WeeklySold { get; init; }   // units sold in the last 7 days
    public int   Sellers    { get; init; }
    public sbyte TrendDir   { get; init; }

    public long RevenuePerWeek => GoingPrice * WeeklySold;

    public int CompetitionTier =>
        Sellers <= 0 ? 0 : Sellers <= 4 ? 1 : Sellers <= 12 ? 2 : 3;
}
