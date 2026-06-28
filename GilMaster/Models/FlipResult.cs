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

    public string HomeWorld { get; init; } = string.Empty;
    public long   HomeGoing { get; init; }                 // realistic home sale price
    public long   HomeFloor { get; init; }                 // cheapest current home listing
    public double Velocity  { get; init; }                 // home/DC sales per day

    // Per-unit margin buying on the cheapest world and selling at the home going rate.
    public long Margin => HomeGoing - BuyPrice;
    public double MarginPct => BuyPrice > 0 ? (double)Margin / BuyPrice * 100.0 : 0;
}
