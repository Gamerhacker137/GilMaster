namespace GilMaster.Models;

public enum GearSlot
{
    MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Ring,
}

/// <summary>A candidate piece of crafting gear and its crafting stats (HQ values when it can be HQ).</summary>
public sealed class GearPiece
{
    public uint     ItemId   { get; init; }
    public ushort   Icon     { get; init; }
    public string   Name     { get; init; } = string.Empty;
    public GearSlot Slot     { get; init; }

    public int Craftsmanship { get; init; }
    public int Control       { get; init; }
    public int CP            { get; init; }

    public int  ItemLevel  { get; init; }
    public int  EquipLevel { get; init; }
    public bool CanHq      { get; init; }

    public bool   Craftable    { get; set; }
    public uint   RecipeId     { get; set; }
    public string CraftJobName { get; set; } = string.Empty;
    public bool   VendorSold   { get; set; }
    public long   MarketPrice  { get; set; }  // 0 = not priced yet

    // Raw stat total — the natural ranking for "best base gear" (higher ilvl wins on all three).
    public int StatTotal => Craftsmanship + Control + CP;
}
