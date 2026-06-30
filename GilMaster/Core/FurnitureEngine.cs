using GilMaster.Models;
using GilMaster.Models.Universalis;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>
/// Furniture-selling intelligence — scans every craftable/marketable housing furnishing and
/// ranks them by gil potential. Furnishings are identified authoritatively from the game's
/// HousingFurniture (interior) + HousingYardObject (exterior) sheets, not by guessing item
/// categories. Pricing uses the 7-day median; ranking is revenue/week (price × weekly demand).
/// </summary>
public sealed class FurnitureEngine : IDisposable
{
    private readonly UniversalisClient universalis = new();
    private CancellationTokenSource? cts;

    public IReadOnlyList<FurnitureItem> Results { get; private set; } = [];
    public bool   IsScanning { get; private set; }
    public string Status     { get; private set; } = "Ready";
    public float  Progress   { get; private set; }
    public event System.Action? OnUpdated;

    private sealed record Meta(uint ItemId, ushort Icon, string Name, string Category,
        bool Exterior, bool Dyeable, uint RecipeId, int JobId);

    private static List<Meta>? _index;

    public void Scan(string worldOrDc, bool craftableOnly)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        IsScanning = true;
        Status = "Indexing furnishings...";
        Progress = 0f;
        Results = [];
        OnUpdated?.Invoke();
        Task.Run(() => Run(worldOrDc, craftableOnly, cts.Token), cts.Token);
    }

    public void Cancel()
    {
        cts?.Cancel();
        IsScanning = false;
        Status = "Cancelled";
    }

    private async Task Run(string world, bool craftableOnly, CancellationToken ct)
    {
        try
        {
            _index ??= BuildIndex();
            var candidates = craftableOnly ? _index.Where(m => m.RecipeId != 0).ToList() : _index;
            if (candidates.Count == 0)
            {
                Status = "No furnishings found.";
                IsScanning = false; OnUpdated?.Invoke();
                return;
            }

            Status = $"Pricing {candidates.Count} furnishings on {world}...";
            Progress = 0.05f;
            OnUpdated?.Invoke();

            var data = await universalis.GetBatchAsync(
                candidates.Select(m => m.ItemId).Distinct(), world, ct,
                (done, total) =>
                {
                    Progress = 0.05f + 0.80f * done / Math.Max(1, total);
                    Status = $"Pricing {done}/{total} on {world}...";
                    OnUpdated?.Invoke();
                }).ConfigureAwait(false);

            var results = new List<FurnitureItem>();
            foreach (var m in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!data.TryGetValue(m.ItemId, out var md) || !md.HasData) continue;

                var going = md.RealisticPrice(false);
                if (going <= 0) continue;

                results.Add(new FurnitureItem
                {
                    ItemId = m.ItemId, IconId = m.Icon, Name = m.Name, Category = m.Category,
                    Exterior = m.Exterior, Dyeable = m.Dyeable,
                    Craftable = m.RecipeId != 0, RecipeId = m.RecipeId,
                    CraftJobName = m.JobId >= 0 ? CraftJobNames.Short(m.JobId) : "",
                    GoingPrice = going,
                    MinListing = md.MinPrice,
                    Sellers = md.Listings.Count,
                    WeeklySold = md.UnitsSold7d,
                    TrendDir = md.RecentTrend(false).Direction,
                    TrendPct = md.RecentTrend(false).Pct,
                });
            }

            // Rank by weekly revenue (price × weekly demand) — the "what actually sells for gil" signal.
            var ranked = results.OrderByDescending(r => r.RevenuePerWeek).ToList();
            Results = ranked;
            Progress = 0.9f;
            Status = "Costing top craftables...";
            OnUpdated?.Invoke();

            // Net margin for the top craftable rows (one batched material fetch).
            await EnrichTopCraftables(ranked, world, ct).ConfigureAwait(false);

            Results = [.. ranked.OrderByDescending(r => r.RevenuePerWeek)];
            Progress = 1f;
            Status = $"Done — {Results.Count} furnishings priced.";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { Service.Log.Error(ex, "Furniture scan failed"); Status = $"Error: {ex.Message}"; }
        finally { IsScanning = false; OnUpdated?.Invoke(); }
    }

    private async Task EnrichTopCraftables(List<FurnitureItem> ranked, string world, CancellationToken ct)
    {
        var top = ranked.Where(r => r.Craftable).Take(40).ToList();
        if (top.Count == 0) return;

        var perItem = new List<(FurnitureItem Item, Models.RecipeIngredient[] Ings)>();
        var toPrice = new HashSet<uint>();
        foreach (var f in top)
        {
            var ings = Plugin.RecipeResolver.ResolveFlat(f.RecipeId);
            perItem.Add((f, ings));
            foreach (var ing in ings)
                if (!ing.IsShopBuyable && !ing.IsGatherable) toPrice.Add(ing.ItemId);
        }

        Dictionary<uint, MarketDataResponse> prices = [];
        if (toPrice.Count > 0)
            prices = await universalis.GetBatchAsync(toPrice, world, ct).ConfigureAwait(false);

        foreach (var (item, ings) in perItem)
        {
            long total = 0;
            foreach (var ing in ings)
            {
                if (prices.TryGetValue(ing.ItemId, out var d)) { ing.MarketMinPrice = d.MinPrice; ing.MarketPriceFetched = true; }
                total += ing.EstimatedUnitCost(true) * ing.Quantity; // gathered = free (you farm mats)
            }
            item.MaterialCost = total;
        }
    }

    // ── Furniture index from the housing sheets (built once) ────────────────────
    private static List<Meta> BuildIndex()
    {
        var items   = Service.DataManager.GetExcelSheet<Item>();
        var recipes = Service.DataManager.GetExcelSheet<Recipe>();

        // recipe-by-result-item for craft job lookup
        var recipeByItem = new Dictionary<uint, uint>();
        foreach (var r in recipes)
        {
            var id = r.ItemResult.RowId;
            if (id != 0 && !recipeByItem.ContainsKey(id)) recipeByItem[id] = r.RowId;
        }

        var seen = new HashSet<uint>();
        var list = new List<Meta>();

        void Add(uint itemId, bool exterior)
        {
            if (itemId == 0 || !seen.Add(itemId)) return;
            var row = items.GetRowOrDefault(itemId);
            if (row is not { } it) return;
            if (it.IsUntradable || it.ItemSearchCategory.RowId == 0) return; // not sellable on the board
            var name = it.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) return;

            uint recipeId = recipeByItem.GetValueOrDefault(itemId);
            int job = -1;
            if (recipeId != 0)
            {
                var rec = recipes.GetRowOrDefault(recipeId);
                if (rec is { } rr) job = (int)rr.CraftType.RowId;
            }

            list.Add(new Meta(
                itemId, it.Icon, name,
                it.ItemUICategory.ValueNullable?.Name.ExtractText() ?? "Furnishing",
                exterior, it.DyeCount > 0, recipeId, job));
        }

        foreach (var f in Service.DataManager.GetExcelSheet<HousingFurniture>()) Add(f.Item.RowId, false);
        foreach (var y in Service.DataManager.GetExcelSheet<HousingYardObject>()) Add(y.Item.RowId, true);
        return list;
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        universalis.Dispose();
    }
}
