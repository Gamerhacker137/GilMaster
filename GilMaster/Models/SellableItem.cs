namespace GilMaster.Models;

/// <summary>A marketable item sitting in your inventory, with what it's worth and the price to list at.</summary>
public sealed class SellableItem
{
    public uint   ItemId { get; init; }
    public ushort IconId { get; init; }
    public string Name   { get; init; } = string.Empty;

    public int    HaveQty   { get; init; }   // how many you're holding
    public bool   HaveHq    { get; init; }   // you have at least one HQ

    public long   FloorNq   { get; init; }   // cheapest NQ listing on your world
    public long   FloorHq   { get; init; }   // cheapest HQ listing
    public long   GoingNq   { get; init; }   // realistic NQ sale price (median of recent sales)
    public long   GoingHq   { get; init; }   // realistic HQ sale price

    public int    Sellers   { get; init; }   // active listings on the board
    public double Velocity  { get; init; }   // sales per day

    public sbyte  TrendDir  { get; init; }   // -1 falling, 0 flat, +1 rising
    public double TrendPct  { get; init; }

    // The price to undercut the current floor by 1 (the relevant quality you're holding).
    public long SuggestedPrice
    {
        get
        {
            var floor = HaveHq && FloorHq > 0 ? FloorHq : FloorNq;
            if (floor > 1) return floor - 1;
            // No one's selling — fall back to the going rate so you don't list for nothing.
            return HaveHq && GoingHq > 0 ? GoingHq : GoingNq;
        }
    }

    // The price you'd realistically get per unit (for valuing the stack).
    public long UnitValue => HaveHq && GoingHq > 0 ? GoingHq : GoingNq > 0 ? GoingNq : SuggestedPrice;

    // What the whole stack is worth at the going rate.
    public long StackValue => UnitValue * HaveQty;

    public int CompetitionTier =>
        Sellers <= 0  ? 0 :
        Sellers <= 4  ? 1 :
        Sellers <= 12 ? 2 :
                        3;
}
