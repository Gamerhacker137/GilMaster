using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>
/// "What's worth gathering at my level?" — scans every gatherable item reachable at the player's
/// Miner/Botanist level, prices it on the market, and ranks by revenue/week (price × weekly
/// demand). Mirrors FurnitureEngine. Each result carries its best node location to go gather.
/// </summary>
public sealed class GatherProfitEngine : IDisposable
{
    private readonly UniversalisClient universalis = new();
    private CancellationTokenSource? cts;

    public IReadOnlyList<GatherProfitItem> Results { get; private set; } = [];
    public bool   IsScanning { get; private set; }
    public string Status     { get; private set; } = "Ready";
    public float  Progress   { get; private set; }
    public event System.Action? OnUpdated;

    /// <param name="jobFilter">0 = Miner, 1 = Botanist, -1 = both.</param>
    /// <param name="minerLevel">Player's Miner level (caps which mining/quarrying nodes show).</param>
    /// <param name="botanistLevel">Player's Botanist level (caps logging/harvesting nodes).</param>
    public void Scan(string worldOrDc, int jobFilter, int minerLevel, int botanistLevel)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        IsScanning = true;
        Status = "Finding gatherables...";
        Progress = 0f;
        Results = [];
        OnUpdated?.Invoke();
        Task.Run(() => Run(worldOrDc, jobFilter, minerLevel, botanistLevel, cts.Token), cts.Token);
    }

    public void Cancel()
    {
        cts?.Cancel();
        IsScanning = false;
        Status = "Cancelled";
    }

    private static bool MatchesJob(int gatheringType, int jobFilter) => jobFilter switch
    {
        0 => gatheringType is 0 or 1, // Miner (mining / quarrying)
        1 => gatheringType is 2 or 3, // Botanist (logging / harvesting)
        _ => gatheringType is 0 or 1 or 2 or 3,
    };

    private async Task Run(string world, int jobFilter, int minerLevel, int botanistLevel, CancellationToken ct)
    {
        try
        {
            int LevelFor(int gType) => gType is 0 or 1 ? minerLevel : botanistLevel;

            // Candidate items: gatherable, with a node reachable at this level for its job.
            var candidates = new List<(uint ItemId, GatherNode Node)>();
            foreach (var id in Plugin.GatheringLocator.GatherableItemIds)
            {
                var nodes = Plugin.GatheringLocator.GetNodesForItem(id);
                if (nodes.Count == 0) continue;
                var best = nodes
                    .Where(n => MatchesJob(n.GatheringType, jobFilter) && n.RequiredLevel <= LevelFor(n.GatheringType))
                    .OrderBy(n => n.RequiredLevel)
                    .FirstOrDefault();
                if (best != null) candidates.Add((id, best));
            }

            if (candidates.Count == 0)
            {
                Status = "Nothing gatherable at that level/job.";
                IsScanning = false; OnUpdated?.Invoke();
                return;
            }

            Status = $"Pricing {candidates.Count} gatherables on {world}...";
            Progress = 0.05f;
            OnUpdated?.Invoke();

            var data = await universalis.GetBatchAsync(
                candidates.Select(c => c.ItemId).Distinct(), world, ct,
                (done, total) =>
                {
                    Progress = 0.05f + 0.90f * done / Math.Max(1, total);
                    Status = $"Pricing {done}/{total} on {world}...";
                    OnUpdated?.Invoke();
                }).ConfigureAwait(false);

            var results = new List<GatherProfitItem>();
            foreach (var (itemId, node) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!data.TryGetValue(itemId, out var md) || !md.HasData) continue;
                var going = md.RealisticPrice(false);
                if (going <= 0) continue;

                results.Add(new GatherProfitItem
                {
                    ItemId = itemId, Name = node.ItemName, RequiredLevel = node.RequiredLevel,
                    GatheringType = node.GatheringType,
                    Zone = node.ZoneName, TerritoryId = node.TerritoryId, MapId = node.MapId,
                    RawX = node.RawX, RawZ = node.RawZ, DisplayX = node.DisplayX, DisplayY = node.DisplayY,
                    IsTimed = node.IsTimed, UptimeBitfield = node.UptimeBitfield,
                    Aetheryte = node.ClosestAetheryteName, AetheryteId = node.AetheryteId,
                    GoingPrice = going,
                    WeeklySold = md.UnitsSold7d,
                    Sellers = md.Listings.Count,
                    TrendDir = md.RecentTrend(false).Direction,
                });
            }

            Results = [.. results.OrderByDescending(r => r.RevenuePerWeek)];
            Progress = 1f;
            Status = $"Done — {Results.Count} gatherables priced.";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { Service.Log.Error(ex, "Gather profit scan failed"); Status = $"Error: {ex.Message}"; }
        finally { IsScanning = false; OnUpdated?.Invoke(); }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        universalis.Dispose();
    }
}
