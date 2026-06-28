namespace GilMaster.Models;

/// <summary>
/// A cross-world resale opportunity: buy an item cheap somewhere on your data centre,
/// then sell it on your home world (the only place you can list).
/// </summary>
public sealed class FlipResult
{
    public uint   ItemId    { get; init; }
    public ushort IconId    { get; init; }
    public string Name      { get; init; } = string.Empty;
    public bool   Hq        { get; init; }

    public string BuyWorld  { get; init; } = string.Empty; // cheapest world to buy from
    public long   BuyPrice  { get; init; }                 // cheapest unit price on the DC

    public long   BuyAvailable { get; init; }              // units grabbable near the cheapest price

    public string HomeWorld { get; init; } = string.Empty;
    public long   HomeGoing { get; init; }                 // realistic home sale price
    public long   HomeFloor { get; init; }                 // cheapest current home listing
    public double Velocity  { get; init; }                 // home/DC sales per day

    // Market board takes a 5% cut from the seller on every sale.
    public const double MarketTax = 0.05;

    // Gross per-unit difference (before tax).
    public long Margin => HomeGoing - BuyPrice;

    // What you actually pocket per unit: home sale minus the 5% tax, minus your buy cost.
    public long NetMargin => (long)(HomeGoing * (1 - MarketTax)) - BuyPrice;
    public double NetMarginPct => BuyPrice > 0 ? (double)NetMargin / BuyPrice * 100.0 : 0;

    // Best-case profit on the whole cheap stack you could grab.
    public long NetStackProfit => NetMargin > 0 ? NetMargin * BuyAvailable : 0;
}
