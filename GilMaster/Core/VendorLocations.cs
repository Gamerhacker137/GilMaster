using Dalamud.Game.Text.SeStringHandling.Payloads; // MapLinkPayload
using Dalamud.Utility;                              // MapUtil.WorldToMap
using Lumina.Excel;                                 // RowRef
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GilMaster.Core;

/// <summary>
/// Maps a gil-shop item id -> the in-world location of the (first) NPC that sells it, so the
/// Queue tab can flag the vendor on the map. Data path (all read from the game's own sheets,
/// no external plugin):
///
///   item id  --GilShopItem (subrow, inverted)-->  GilShop id
///   GilShop id  --scan ENpcBase.ENpcData handler refs-->  vendor ENpcBase
///   ENpcBase id  --ENpcResident.Singular-->  NPC display name
///   ENpcBase id  --scan Level (Type==8, Object.RowId==npc)-->  Territory + Map + world X/Z
///
/// World (X,Z) is converted to nice map coords with Dalamud's MapUtil.WorldToMap (the exact
/// transform Craftimizer uses) before being handed to MapLinkPayload's float "nice coords" ctor.
///
/// Companion to <see cref="VendorPrices"/>: VendorPrices answers "what does it cost?",
/// VendorLocations answers "where do I buy it?". A vendor item can have a price but no Level
/// row (instanced/cutscene NPC) — callers should treat <see cref="Get"/> == null as
/// "price-only, no map flag" and fall back to a chat link.
/// </summary>
public static class VendorLocations
{
    /// <summary>Resolved vendor location for a single item.</summary>
    public readonly record struct VendorLocation(
        string Npc,          // ENpcResident.Singular display name (may be a placeholder if blank)
        string Zone,         // TerritoryType.PlaceName display name
        uint TerritoryId,    // for MapLinkPayload
        uint MapId,          // for MapLinkPayload
        float X,             // nice map X coord (human-readable, e.g. 10.4)
        float Y);            // nice map Y coord

    // itemId -> location. Built once and cached, exactly like VendorPrices._prices.
    private static Dictionary<uint, VendorLocation>? _byItem;

    /// <summary>True if we know an on-map vendor location for this item.</summary>
    public static bool Has(uint itemId)
    {
        _byItem ??= Build();
        return _byItem.ContainsKey(itemId);
    }

    /// <summary>Vendor location for this item, or null if none has a known Level position.</summary>
    public static VendorLocation? Get(uint itemId)
    {
        _byItem ??= Build();
        return _byItem.TryGetValue(itemId, out var loc) ? loc : null;
    }

    public static int Count { get { _byItem ??= Build(); return _byItem.Count; } }

    /// <summary>
    /// Flag the in-game map at this item's vendor. No-op (returns false) if there is no known
    /// Level location — the caller should fall back to a chat link in that case.
    /// </summary>
    public static bool OpenMap(uint itemId)
    {
        var loc = Get(itemId);
        if (loc is not { } l) return false;
        try
        {
            // Float "nice coords" ctor: (territoryTypeId, mapId, niceX, niceY, fudge=0.05f).
            // X/Y are already the MapUtil.WorldToMap output computed in Build().
            var payload = new MapLinkPayload(l.TerritoryId, l.MapId, l.X, l.Y);
            Service.GameGui.OpenMapWithMapLink(payload);
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] Failed to open map link to vendor");
            return false;
        }
    }

    private static Dictionary<uint, VendorLocation> Build()
    {
        var byItem = new Dictionary<uint, VendorLocation>();
        try
        {
            var enpcBase  = Service.DataManager.GetExcelSheet<ENpcBase>();
            var residents = Service.DataManager.GetExcelSheet<ENpcResident>();
            var levels    = Service.DataManager.GetExcelSheet<Level>();

            // 1) ENpc id -> first Level row whose Object points at that ENpc.
            //    Gate on Type==8 (ENpcBase); Object.RowId is only an ENpc id for that discriminator.
            var enpcToLevel = new Dictionary<uint, uint>();
            foreach (var lvl in levels)
            {
                if (lvl.Type != 8) continue;                  // 8 == ENpcBase  // VERIFY: discriminator
                var objId = lvl.Object.RowId;                 // RowRef.RowId readable even untyped
                if (objId != 0 && !enpcToLevel.ContainsKey(objId))
                    enpcToLevel[objId] = lvl.RowId;
            }

            // 2) GilShop id -> (vendor ENpc id, name, levelRowId). Scan every ENpcBase; a handler
            //    ref in ENpcData that resolves to GilShop is a gil-shop the NPC operates.
            var shopToVendor = new Dictionary<uint, (uint enpcId, string name, uint levelRowId)>();
            foreach (var npc in enpcBase)
            {
                foreach (var handler in npc.ENpcData)         // Collection<RowRef>, IEnumerable
                {
                    if (!handler.Is<GilShop>()) continue;     // only gil-shop handlers
                    var shopId = handler.RowId;               // == GilShop id
                    if (shopId == 0 || shopToVendor.ContainsKey(shopId)) continue; // first-NPC-wins

                    var name = residents.GetRowOrDefault(npc.RowId)?.Singular.ExtractText();
                    if (string.IsNullOrEmpty(name)) name = $"Vendor #{npc.RowId}"; // Singular can be blank
                    enpcToLevel.TryGetValue(npc.RowId, out var levelRowId); // 0 == no known location
                    shopToVendor[shopId] = (npc.RowId, name!, levelRowId);
                }
            }

            // 3) item id -> GilShop id (invert GilShopItem subrows, same access as VendorPrices),
            //    then compose with shopToVendor + resolve the Level coords into nice map coords.
            var shop = Service.DataManager.GetSubrowExcelSheet<GilShopItem>();
            foreach (var row in shop)
            {
                var shopId = row.RowId;                       // parent row id == GilShop id
                if (!shopToVendor.TryGetValue(shopId, out var v)) continue; // shop has no mappable NPC
                if (v.levelRowId == 0) continue;              // NPC has no Level row -> no map flag

                var loc = ResolveLevel(levels, v.levelRowId, v.name);
                if (loc is not { } resolved) continue;

                foreach (var sub in row)
                {
                    var id = sub.Item.RowId;
                    if (id == 0 || byItem.ContainsKey(id)) continue; // first vendor wins per item
                    byItem[id] = resolved;
                }
            }

            Service.Log.Information(
                $"[GilMaster] Vendor location index: {byItem.Count} items map-flaggable " +
                $"(from {shopToVendor.Count} gil shops).");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Vendor location index failed"); }
        return byItem;
    }

    // Read a Level row into nice map coords. Mirrors Craftimizer's ResolveLevelData:
    //   MapUtil.WorldToMap(new Vector2(level.X, level.Z), map.OffsetX, map.OffsetY, map.SizeFactor)
    // NOTE: use X and Z (horizontal plane); Level.Y is height. Offsets are passed AS-IS (MapUtil
    // adds them internally — do NOT negate).
    private static VendorLocation? ResolveLevel(ExcelSheet<Level> levels, uint levelRowId, string npc)
    {
        try
        {
            var lvl = levels.GetRowOrDefault(levelRowId);
            if (lvl is not { } level) return null;

            // RowRef.Value throws on invalid rows; guard via ValueNullable.
            if (level.Map.ValueNullable is not { } map) return null;
            if (level.Territory.ValueNullable is not { } territory) return null;

            var zone = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

            // short/ushort widen implicitly to MapUtil's int/uint params — no casts needed.
            Vector2 nice = MapUtil.WorldToMap(
                new Vector2(level.X, level.Z),
                map.OffsetX, map.OffsetY, map.SizeFactor);

            return new VendorLocation(
                npc, zone,
                level.Territory.RowId, level.Map.RowId,
                nice.X, nice.Y);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, $"[GilMaster] Failed to resolve Level row {levelRowId}");
            return null;
        }
    }
}
