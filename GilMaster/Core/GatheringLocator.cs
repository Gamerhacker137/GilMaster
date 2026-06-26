using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

public sealed class GatheringLocator
{
    private Dictionary<uint, List<GatherNode>> nodesByItem = [];

    public HashSet<uint> GatherableItemIds { get; private set; } = [];

    public GatheringLocator() => Rebuild();

    public void Rebuild()
    {
        try
        {
            nodesByItem = [];
            GatherableItemIds = [];

            var gatheringItemSheet      = Service.DataManager.GetExcelSheet<GatheringItem>();
            var gatheringPointSheet     = Service.DataManager.GetExcelSheet<GatheringPoint>();
            var gatheringPointBaseSheet = Service.DataManager.GetExcelSheet<GatheringPointBase>();
            var exportedGPSheet         = Service.DataManager.GetExcelSheet<ExportedGatheringPoint>();
            var transientSheet          = Service.DataManager.GetExcelSheet<GatheringPointTransient>();
            var territorySheet          = Service.DataManager.GetExcelSheet<TerritoryType>();
            var aetheryteSheet          = Service.DataManager.GetExcelSheet<Aetheryte>();
            var placeNameSheet          = Service.DataManager.GetExcelSheet<PlaceName>();
            var mapMarkerSheet          = Service.DataManager.GetSubrowExcelSheet<MapMarker>();

            // ── Step 1: gatheringItem.RowId → GatheringPoint.RowId[] ──────────
            var itemToPoints = new Dictionary<uint, List<uint>>();
            var itemPointSheet = Service.DataManager.GetSubrowExcelSheet<GatheringItemPoint>();
            foreach (var subrows in itemPointSheet)
            {
                if (subrows.Count == 0) continue;
                var list = new List<uint>();
                foreach (var row in subrows)
                {
                    var ptId = row.GatheringPoint.RowId;
                    if (ptId != 0) list.Add(ptId);
                }
                if (list.Count > 0)
                    itemToPoints[subrows.RowId] = list;
            }

            // ── Step 2: GatheringPoint.RowId → territory + coords + unspoiled ──
            // ExportedGatheringPoint is indexed by GatheringPointBase.RowId
            // GatheringPointTransient.GatheringRarePopTimeTable.RowId != 0 → unspoiled/ephemeral
            var pointInfos = new Dictionary<uint, PointInfo>();
            foreach (var pt in gatheringPointSheet)
            {
                if (pt.TerritoryType.RowId == 0) continue;
                var baseId    = pt.GatheringPointBase.RowId;
                var coordRow  = exportedGPSheet.GetRowOrDefault(baseId);
                var transient = transientSheet.GetRowOrDefault(pt.RowId);
                var isUnspoiled = transient.HasValue && transient.Value.GatheringRarePopTimeTable.RowId != 0;
                pointInfos[pt.RowId] = new PointInfo
                {
                    TerritoryId = pt.TerritoryType.RowId,
                    RawX        = coordRow?.X ?? 0,
                    RawZ        = coordRow?.Y ?? 0,
                    PlaceNameId = pt.PlaceName.RowId,
                    BaseId      = baseId,
                    IsUnspoiled = isUnspoiled,
                };
            }

            // ── Step 3: GatheringPointBase.RowId → gathering type ────────────
            var baseTypes = new Dictionary<uint, int>();
            foreach (var b in gatheringPointBaseSheet)
                baseTypes[b.RowId] = (int)b.GatheringType.RowId;

            // ── Step 4: Build aetheryte table by territory from MapMarkers ───
            // MapMarker.DataType == 3 → aetheryte marker; DataKey.RowId → Aetheryte.RowId
            // MapMarker.X/Y are in NodeToMap scale (×100 of display coords)
            var aethNames = new Dictionary<uint, string>();
            foreach (var aeth in aetheryteSheet)
            {
                if (!aeth.IsAetheryte) continue;
                var name = aeth.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrEmpty(name)) aethNames[aeth.RowId] = name;
            }

            var mapToTerritory = new Dictionary<uint, uint>();
            foreach (var terr in territorySheet)
                if (terr.Map.RowId != 0) mapToTerritory[terr.Map.RowId] = terr.RowId;

            // territory → list of (mapX, mapY, aethName)
            var aethByTerritory = new Dictionary<uint, List<(int x, int y, string name)>>();
            foreach (var outerRow in mapMarkerSheet)
            {
                if (!mapToTerritory.TryGetValue(outerRow.RowId, out var terrId)) continue;
                foreach (var marker in outerRow)
                {
                    if (marker.DataType != 3) continue;          // 3 = aetheryte
                    if (!aethNames.TryGetValue(marker.DataKey.RowId, out var name)) continue;
                    if (!aethByTerritory.TryGetValue(terrId, out var list))
                    {
                        list = [];
                        aethByTerritory[terrId] = list;
                    }
                    list.Add((marker.X, marker.Y, name));
                }
            }

            // ── Step 5: Build node list per gatherable item ───────────────────
            foreach (var gi in gatheringItemSheet)
            {
                var itemId = gi.Item.RowId;
                if (itemId == 0 || itemId >= 1_000_000) continue;

                // GatheringItem.Item is an untyped RowRef
                if (!gi.Item.TryGetValue<Item>(out var itemData)) continue;
                var itemName = itemData.Name.ExtractText();
                if (string.IsNullOrEmpty(itemName)) continue;

                GatherableItemIds.Add(itemId);
                if (!itemToPoints.TryGetValue(gi.RowId, out var pointIds)) continue;

                foreach (var pointId in pointIds)
                {
                    if (!pointInfos.TryGetValue(pointId, out var info)) continue;
                    if (!baseTypes.TryGetValue(info.BaseId, out var gType)) continue;
                    if (gType >= 4) continue; // skip fishing/spearfishing

                    var terrRow = territorySheet.GetRowOrDefault(info.TerritoryId);
                    var zoneName  = terrRow?.PlaceName.Value.Name.ExtractText() ?? $"Zone#{info.TerritoryId}";
                    var placeName = placeNameSheet.GetRowOrDefault(info.PlaceNameId)?.Name.ExtractText() ?? zoneName;

                    uint mapId = 0;
                    ushort sizeFactor = 100;
                    if (terrRow.HasValue)
                    {
                        mapId = terrRow.Value.Map.RowId;
                        if (mapId != 0) sizeFactor = terrRow.Value.Map.Value.SizeFactor;
                    }

                    var (dispX, dispY) = RawToDisplay(info.RawX, info.RawZ, sizeFactor);
                    var aethName = FindClosestAetheryte(info.RawX, info.RawZ, sizeFactor,
                        info.TerritoryId, aethByTerritory);

                    var node = new GatherNode
                    {
                        ItemId               = itemId,
                        ItemName             = itemName,
                        TerritoryId          = info.TerritoryId,
                        ZoneName             = zoneName,
                        PlaceName            = placeName,
                        GatheringType        = gType,
                        RawX                 = info.RawX,
                        RawZ                 = info.RawZ,
                        MapId                = mapId,
                        DisplayX             = dispX,
                        DisplayY             = dispY,
                        RequiredLevel        = (int)gi.GatheringItemLevel.Value.GatheringItemLevel,
                        IsUnspoiled          = info.IsUnspoiled,
                        TimedUptimeInfo      = info.IsUnspoiled ? "Unspoiled node — check an uptime tracker for exact ET window" : null,
                        ClosestAetheryteName = aethName,
                    };

                    if (!nodesByItem.TryGetValue(itemId, out var nodes))
                    {
                        nodes = [];
                        nodesByItem[itemId] = nodes;
                    }

                    if (!nodes.Exists(n => n.TerritoryId == info.TerritoryId &&
                                          Math.Abs(n.RawX - info.RawX) < 200 &&
                                          Math.Abs(n.RawZ - info.RawZ) < 200))
                    {
                        nodes.Add(node);
                    }
                }
            }

            Service.Log.Information($"[GilMaster] GatheringLocator: {GatherableItemIds.Count} gatherable items, {nodesByItem.Count} with node data");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[GilMaster] GatheringLocator.Rebuild failed");
        }
    }

    public IReadOnlyList<GatherNode> GetNodesForItem(uint itemId)
        => nodesByItem.TryGetValue(itemId, out var nodes) ? nodes : [];

    private static (float x, float y) RawToDisplay(float rawX, float rawY, ushort sizeFactor)
    {
        if (sizeFactor == 0) return (0f, 0f);
        float c = sizeFactor / 100.0f;
        float x = (41.0f / c) * ((rawX + 1024) / 2048.0f) + 1.0f;
        float y = (41.0f / c) * ((rawY + 1024) / 2048.0f) + 1.0f;
        return (MathF.Round(x, 1), MathF.Round(y, 1));
    }

    // Find closest aetheryte using MapMarker coordinates (same ×100 scale as NodeToMap)
    private static string? FindClosestAetheryte(
        float rawX, float rawY, ushort sizeFactor,
        uint territoryId, Dictionary<uint, List<(int x, int y, string name)>> aethByTerritory)
    {
        if (!aethByTerritory.TryGetValue(territoryId, out var list)) return null;

        // Convert node raw coords to NodeToMap ×100 scale for comparison
        float c = sizeFactor > 0 ? sizeFactor / 100.0f : 1.0f;
        float nodeX = (41.0f / c) * ((rawX + 1024) / 2048.0f) * 100;
        float nodeY = (41.0f / c) * ((rawY + 1024) / 2048.0f) * 100;

        string? best = null;
        double bestDist = double.MaxValue;
        foreach (var (ax, ay, name) in list)
        {
            var dx = ax - nodeX;
            var dy = ay - nodeY;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = name; }
        }
        return best;
    }

    private readonly struct PointInfo
    {
        public uint  TerritoryId { get; init; }
        public float RawX        { get; init; }
        public float RawZ        { get; init; }
        public uint  PlaceNameId { get; init; }
        public uint  BaseId      { get; init; }
        public bool  IsUnspoiled { get; init; }
    }
}
