using FFXIVClientStructs.FFXIV.Client.Game.UI; // UIState
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>The chosen NPC vendor for an item — preferring your current city, then an unlocked one.</summary>
public readonly record struct VendorSource(
    string Npc, string City, uint TerritoryId, uint Price, bool IsCurrentCity, bool? IsUnlocked);

/// <summary>The cheapest world to buy an item on the market board, and its unit price.</summary>
public readonly record struct BoardSource(string World, long Price);

/// <summary>
/// "Where should I actually buy this?" — picks the best NPC vendor (current city &gt; an unlocked
/// city &gt; any) and can compare it to the cheapest world on the market board, so a missing
/// material is sourced the cheapest/most-convenient way.
/// </summary>
public static class VendorSourcing
{
    // territoryId -> a gating aetheryte id (any aetheryte in that territory). Built once.
    private static Dictionary<uint, uint>? _terrToAeth;

    private static Dictionary<uint, uint> BuildTerrToAeth()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var a in Service.DataManager.GetExcelSheet<Aetheryte>())
        {
            if (!a.IsAetheryte) continue;               // skip aethernet-only shards
            var terr = a.Territory.RowId;
            if (terr != 0 && !map.ContainsKey(terr)) map[terr] = a.RowId;
        }
        return map;
    }

    private static uint GatingAetheryte(uint territoryId)
    {
        // Capital sub-zones (e.g. Limsa Upper Decks) gate through a main-territory aetheryte.
        if (AetheryteData.ZoneToAetheryte.TryGetValue(territoryId, out var forced)) return forced;
        _terrToAeth ??= BuildTerrToAeth();
        return _terrToAeth.TryGetValue(territoryId, out var a) ? a : 0;
    }

    // null = couldn't determine (not logged in / unknown territory).
    private static unsafe bool? IsTerritoryUnlocked(uint territoryId)
    {
        try
        {
            var aeth = GatingAetheryte(territoryId);
            if (aeth == 0) return null;
            var ui = UIState.Instance();
            if (ui == null) return null;
            return ui->IsAetheryteUnlocked(aeth);
        }
        catch { return null; }
    }

    /// <summary>
    /// Best NPC vendor for an item: one in your current city &gt; one in an unlocked city &gt; any.
    /// Null if no NPC sells it. Price is the fixed gil-shop price (same in every city).
    /// </summary>
    public static VendorSource? ResolveVendor(uint itemId)
    {
        var price = VendorPrices.Get(itemId);
        if (price == 0) return null;
        var all = VendorLocations.GetAll(itemId);
        if (all.Count == 0) return null;

        uint here = Service.ClientState.TerritoryType;
        VendorSource Make(VendorLocations.VendorLocation l) =>
            new(l.Npc, l.Zone, l.TerritoryId, price, here != 0 && l.TerritoryId == here, IsTerritoryUnlocked(l.TerritoryId));

        // 1) a vendor in your current city — no travel needed.
        foreach (var l in all)
            if (here != 0 && l.TerritoryId == here) return Make(l);

        // 2) a vendor in a city you've unlocked (can teleport to).
        foreach (var l in all)
            if (IsTerritoryUnlocked(l.TerritoryId) == true) return Make(l);

        // 3) otherwise the first known vendor (locked / unknown).
        return Make(all[0]);
    }

    /// <summary>Cheapest world to buy this item (NQ) on the datacenter, and its price.</summary>
    public static async Task<BoardSource?> CheapestWorldOnDc(uint itemId, string dc, CancellationToken ct = default)
    {
        var data = await Plugin.Universalis.GetItemAsync(itemId, dc, ct).ConfigureAwait(false);
        if (data is null || !data.HasData) return null;
        var cheapest = data.Listings
            .Where(l => l.PricePerUnit > 0 && !l.Hq)
            .OrderBy(l => l.PricePerUnit)
            .FirstOrDefault();
        return cheapest is { PricePerUnit: > 0 }
            ? new BoardSource(cheapest.WorldName ?? "?", cheapest.PricePerUnit)
            : null;
    }
}
