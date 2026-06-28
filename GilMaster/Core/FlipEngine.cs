using GilMaster.Models;
using GilMaster.Models.Universalis;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>
/// Flip finder — for an item, finds the cheapest world to BUY it on your data centre and
/// compares against what it sells for on your HOME world (the only place you can list).
/// Respects the FFXIV rule: buy anywhere on the DC, sell only at home.
/// </summary>
public sealed class FlipEngine : IDisposable
{
    private readonly UniversalisClient universalis = new();

    public IReadOnlyList<FlipResult> Results { get; private set; } = [];
    public bool   Busy     { get; private set; }
    public string Status   { get; private set; } = string.Empty;
    public float  Progress { get; private set; }
    public event System.Action? OnUpdated;

    private readonly List<FlipResult> results = [];
    private System.Threading.CancellationTokenSource? scanCts;

    public void Lookup(uint itemId, string dc, string homeWorld)
    {
        if (itemId == 0 || string.IsNullOrEmpty(dc) || string.IsNullOrEmpty(homeWorld)) return;
        Busy = true;
        Status = "Pricing across the data centre...";
        OnUpdated?.Invoke();
        Task.Run(async () =>
        {
            try
            {
                var r = await Analyze(itemId, dc, homeWorld).ConfigureAwait(false);
                if (r != null)
                {
                    results.RemoveAll(x => x.ItemId == r.ItemId);
                    results.Insert(0, r);
                    Results = [.. results];
                    Status = r.NetMargin > 0 ? $"{r.Name}: buy on {r.BuyWorld}, ~{r.NetMargin:N0}/ea after tax" : $"{r.Name}: no worthwhile flip";
                }
                else Status = "No market data for that item.";
            }
            catch (Exception ex)
            {
                Service.Log.Warning(ex, "Flip lookup failed");
                Status = $"Error: {ex.Message}";
            }
            finally { Busy = false; OnUpdated?.Invoke(); }
        });
    }

    /// <summary>
    /// Scan a batch of items (e.g. your latest Find results) for cross-world flips and
    /// keep the profitable ones, ranked by best-case net stack profit.
    /// </summary>
    public void Scan(IReadOnlyList<uint> itemIds, string dc, string homeWorld)
    {
        if (itemIds.Count == 0 || string.IsNullOrEmpty(dc) || string.IsNullOrEmpty(homeWorld)) return;
        scanCts?.Cancel();
        scanCts = new System.Threading.CancellationTokenSource();
        var ct = scanCts.Token;

        Busy = true;
        Progress = 0f;
        Status = $"Scanning {itemIds.Count} item(s) across {dc}...";
        results.Clear();
        Results = [];
        OnUpdated?.Invoke();

        Task.Run(async () =>
        {
            try
            {
                var found = new List<FlipResult>();
                for (var i = 0; i < itemIds.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Progress = (float)i / itemIds.Count;
                    Status = $"Pricing {i + 1}/{itemIds.Count} across {dc}...";
                    OnUpdated?.Invoke();

                    var r = await Analyze(itemIds[i], dc, homeWorld).ConfigureAwait(false);
                    if (r != null && r.NetMargin > 0)
                    {
                        found.Add(r);
                        results.Clear();
                        results.AddRange(found.OrderByDescending(x => x.NetStackProfit));
                        Results = [.. results];
                        OnUpdated?.Invoke();
                    }
                }
                Progress = 1f;
                Status = $"Done — {found.Count} profitable flip(s) of {itemIds.Count} checked.";
            }
            catch (OperationCanceledException) { Status = "Cancelled."; }
            catch (Exception ex) { Service.Log.Warning(ex, "Flip scan failed"); Status = $"Error: {ex.Message}"; }
            finally { Busy = false; OnUpdated?.Invoke(); }
        }, ct);
    }

    public void CancelScan()
    {
        scanCts?.Cancel();
        Busy = false;
        Status = "Cancelled.";
    }

    public void Clear()
    {
        scanCts?.Cancel();
        results.Clear();
        Results = [];
        Status = string.Empty;
        OnUpdated?.Invoke();
    }

    private async Task<FlipResult?> Analyze(uint itemId, string dc, string homeWorld)
    {
        var data = await universalis.GetItemAsync(itemId, dc).ConfigureAwait(false);
        if (data is null || !data.HasData || data.Listings.Count == 0) return null;

        // Pick the quality with the best flip: try NQ first, then HQ.
        var nq = Build(itemId, data, homeWorld, hq: false);
        var hq = Build(itemId, data, homeWorld, hq: true);

        if (nq is null) return hq;
        if (hq is null) return nq;
        return hq.NetMargin > nq.NetMargin ? hq : nq;
    }

    private static FlipResult? Build(uint itemId, MarketDataResponse data, string homeWorld, bool hq)
    {
        var listings = data.Listings.Where(l => l.PricePerUnit > 0 && l.Hq == hq).ToList();
        if (listings.Count == 0) return null;

        // Cheapest place to buy anywhere on the DC.
        var cheapest = listings.OrderBy(l => l.PricePerUnit).First();

        // How many units you could grab on that world within 5% of the cheapest price.
        var cap = cheapest.PricePerUnit * 1.05;
        long buyAvailable = listings
            .Where(l => string.Equals(l.WorldName, cheapest.WorldName, StringComparison.OrdinalIgnoreCase)
                     && l.PricePerUnit <= cap)
            .Sum(l => l.Quantity);

        // Home-world sell signals.
        var homeListings = listings.Where(l => string.Equals(l.WorldName, homeWorld, StringComparison.OrdinalIgnoreCase)).ToList();
        long homeFloor = homeListings.Count > 0 ? homeListings.Min(l => l.PricePerUnit) : 0;

        var homeSales = data.RecentHistory
            .Where(h => h.PricePerUnit > 0 && h.Hq == hq
                     && string.Equals(h.WorldName, homeWorld, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.PricePerUnit).OrderBy(p => p).ToList();
        long homeGoing = homeSales.Count > 0 ? homeSales[homeSales.Count / 2]
                       : homeFloor > 0 ? homeFloor
                       : data.RealisticPrice(hq);

        var item = Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);

        return new FlipResult
        {
            ItemId    = itemId,
            IconId    = item?.Icon ?? 0,
            Name      = item?.Name.ExtractText() ?? $"Item#{itemId}",
            Hq        = hq,
            BuyWorld  = cheapest.WorldName ?? "?",
            BuyPrice  = cheapest.PricePerUnit,
            BuyAvailable = buyAvailable,
            HomeWorld = homeWorld,
            HomeGoing = homeGoing,
            HomeFloor = homeFloor,
            Velocity  = data.SaleVelocity,
        };
    }

    // ── Marketable-item name search (any tradable, board-listable item) ──────
    private static Dictionary<uint, string>? _index;

    public static List<(uint Id, string Name)> Search(string query, int max = 15)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        _index ??= BuildIndex();
        return _index
            .Where(kv => kv.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Length)
            .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static Dictionary<uint, string> BuildIndex()
    {
        var dict = new Dictionary<uint, string>();
        foreach (var item in Service.DataManager.GetExcelSheet<Item>())
        {
            if (item.IsUntradable || item.ItemSearchCategory.RowId == 0) continue;
            var name = item.Name.ExtractText();
            if (!string.IsNullOrEmpty(name)) dict[item.RowId] = name;
        }
        return dict;
    }

    public void Dispose() => universalis.Dispose();
}
