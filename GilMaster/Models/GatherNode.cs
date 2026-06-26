namespace GilMaster.Models;

public sealed class GatherNode
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int QuantityNeeded { get; set; }

    public uint TerritoryId { get; init; }
    public string ZoneName { get; init; } = string.Empty;
    public string PlaceName { get; init; } = string.Empty;

    // 0=Mining, 1=Quarrying, 2=Logging, 3=Harvesting
    public int GatheringType { get; init; }
    public string GatheringTypeName => GatheringType switch
    {
        0 => "Mining",
        1 => "Quarrying",
        2 => "Logging",
        3 => "Harvesting",
        _ => "Unknown",
    };

    // Raw game coordinates for MapLinkPayload
    public float RawX { get; init; }
    public float RawZ { get; init; }
    public uint MapId { get; init; }

    // Approximate display coords (computed from raw + map factor)
    public float DisplayX { get; init; }
    public float DisplayY { get; init; }

    public int RequiredLevel { get; init; }
    public bool IsUnspoiled { get; init; }
    public string? TimedUptimeInfo { get; init; }  // e.g. "00:00–03:00 ET"

    // 24-bit Eorzea-hour uptime mask for timed nodes (0 = always up / untimed).
    // Used to compute live "UP now / up in Xm" status from the real clock.
    public uint UptimeBitfield { get; init; }
    public bool IsTimed => UptimeBitfield != 0 && UptimeBitfield != 0x00FFFFFF;

    public string? ClosestAetheryteName { get; init; }
    public uint    AetheryteId          { get; init; }  // Aetheryte sheet RowId, for teleporting
}
