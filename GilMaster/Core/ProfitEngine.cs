using GilMaster.Models;
using GilMaster.Models.Universalis;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

public static class CraftJobNames
{
    public static readonly string[] Names = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];
    public static readonly string[] FullNames = ["Carpenter", "Blacksmith", "Armorer", "Goldsmith", "Leatherworker", "Weaver", "Alchemist", "Culinarian"];
    public static string Short(int id) => id < Names.Length ? Names[id] : "???";
    public static string Full(int id) => id < FullNames.Length ? FullNames[id] : "Unknown";
}

public sealed class ProfitEngine : IDisposable
{
    private readonly UniversalisClient universalis = new();
    private CancellationTokenSource? scanCts;

    public IReadOnlyList<ProfitableItem> Results { get; private set; } = [];
    public bool IsScanning { get; private set; }
    public string ScanStatus { get; private set; } = "Ready";
    public float ScanProgress { get; private set; }
    public event System.Action? OnResultsUpdated;

    // ── Item-name search (independent of the job/level scan) ─────────────────
    public IReadOnlyList<ProfitableItem> SearchResults { get; private set; } = [];
    public bool IsSearching { get; private set; }
    public string SearchStatus { get; private set; } = string.Empty;
    private CancellationTokenSource? searchCts;

    private static string CacheFile =>
        Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "last_scan.json");

    public void StartScan(int craftJobId, int playerLevel, string worldOrDc, Configuration config)
    {
        scanCts?.Cancel();
        scanCts = new CancellationTokenSource();
        var ct = scanCts.Token;

        IsScanning = true;
        ScanStatus = "Scanning...";
        ScanProgress = 0f;
        Results = [];
        OnResultsUpdated?.Invoke();

        Task.Run(() => RunScan(craftJobId, playerLevel, worldOrDc, config, ct), ct);
    }

    public void CancelScan()
    {
        scanCts?.Cancel();
        IsScanning = false;
        ScanStatus = "Cancelled";
    }

    private async Task RunScan(int craftJobId, int playerLevel, string worldOrDc, Configuration config, CancellationToken ct)
    {
        try
        {
            ScanStatus = "Reading recipes...";

            var recipeSheet = Service.DataManager.GetExcelSheet<Recipe>();
            var itemSheet = Service.DataManager.GetExcelSheet<Item>();
            var minLevel = Math.Max(1, playerLevel - 10);
            var maxLevel = playerLevel + config.LevelBuffer;

            var candidates = new List<(uint recipeId, uint itemId, int level, int amountResult)>();
            foreach (var recipe in recipeSheet)
            {
                ct.ThrowIfCancellationRequested();
                if ((int)recipe.CraftType.RowId != craftJobId) continue;

                var level = (int)recipe.RecipeLevelTable.Value.ClassJobLevel;
                if (level < minLevel || level > maxLevel) continue;

                var item = recipe.ItemResult.ValueNullable;
                if (item is null || item.Value.IsUntradable || item.Value.Name.IsEmpty) continue;

                var amountResult = Math.Max(1, (int)recipe.AmountResult);
                candidates.Add((recipe.RowId, recipe.ItemResult.RowId, level, amountResult));
            }

            if (candidates.Count == 0)
            {
                ScanStatus = "No recipes found for this job/level.";
                IsScanning = false;
                OnResultsUpdated?.Invoke();
                return;
            }

            ScanStatus = $"Fetching market data for {candidates.Count} items on {worldOrDc}...";
            ScanProgress = 0.1f;

            var itemIds = candidates.Select(c => c.itemId).Distinct();
            var marketData = await universalis.GetBatchAsync(itemIds, worldOrDc, ct).ConfigureAwait(false);

            ScanProgress = 0.7f;
            ScanStatus = "Scoring results...";

            var results = new List<ProfitableItem>();
            foreach (var (recipeId, itemId, level, amountResult) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!marketData.TryGetValue(itemId, out var data) || !data.HasData) continue;

                var velocity = config.PreferHq ? data.SaleVelocityHq + data.SaleVelocityNq : data.SaleVelocity;
                if (velocity < config.MinSaleVelocity) continue;

                var minPrice = config.PreferHq && data.MinPriceHq > 0 ? data.MinPriceHq : data.MinPrice;
                if (minPrice <= 0) continue;

                var item = itemSheet.GetRowOrDefault(itemId);
                results.Add(new ProfitableItem
                {
                    ItemId = itemId,
                    RecipeId = recipeId,
                    Name = item?.Name.ExtractText() ?? $"Item#{itemId}",
                    RecipeLevel = level,
                    CraftJobId = craftJobId,
                    CraftJobName = CraftJobNames.Full(craftJobId),
                    AmountResult = amountResult,
                    MinListingPrice = data.MinPrice,
                    MinListingHqPrice = data.MinPriceHq,
                    SaleVelocity = data.SaleVelocity,
                    SaleVelocityHq = data.SaleVelocityHq,
                    EstimatedMaterialCost = 0,
                    HasActiveListings = data.UnitsForSale > 0,
                });
            }

            var ranked = results.OrderByDescending(r => r.ProfitScore).Take(config.ScanResultLimit).ToList();
            Results = ranked;
            ScanProgress = 0.85f;
            ScanStatus = "Fetching material costs...";
            OnResultsUpdated?.Invoke();

            // Enrich top 10 results with actual material costs
            var enrichTasks = ranked.Take(10).Select(item =>
                EnrichMaterialCostAsync(item, worldOrDc, config.AssumeGatherableFree, ct));
            await Task.WhenAll(enrichTasks).ConfigureAwait(false);

            // Re-rank with real material costs
            Results = [.. ranked.OrderByDescending(r => r.ProfitScore)];
            ScanProgress = 1f;
            ScanStatus = $"Done — {Results.Count} results" + (config.ScanDatacenter ? " (DC)" : "");

            SaveCache(Results);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "ProfitEngine scan failed");
            ScanStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            OnResultsUpdated?.Invoke();
        }
    }

    // ── Item-name search ─────────────────────────────────────────────────────
    // Finds craftable items by name across all crafting jobs, regardless of the
    // current job/level filter. Used by the Find tab's search box so the user can
    // jump straight to a specific item (e.g. "brass ingot") and craft it.

    public void StartSearch(string query, string worldOrDc, Configuration config)
    {
        searchCts?.Cancel();
        searchCts = new CancellationTokenSource();
        var ct = searchCts.Token;

        IsSearching = true;
        SearchStatus = "Searching...";
        SearchResults = [];
        OnResultsUpdated?.Invoke();

        Task.Run(() => RunSearch(query, worldOrDc, config, ct), ct);
    }

    public void ClearSearch()
    {
        searchCts?.Cancel();
        IsSearching = false;
        SearchResults = [];
        SearchStatus = string.Empty;
        OnResultsUpdated?.Invoke();
    }

    private const int SearchMatchCap = 60;

    private async Task RunSearch(string query, string worldOrDc, Configuration config, CancellationToken ct)
    {
        try
        {
            var needle = query.Trim().ToLowerInvariant();
            if (needle.Length < 2)
            {
                SearchStatus = "Type at least 2 characters.";
                IsSearching = false;
                OnResultsUpdated?.Invoke();
                return;
            }

            var recipeSheet = Service.DataManager.GetExcelSheet<Recipe>();

            // (recipeId, itemId, level, amountResult, craftJobId, name, startsWith)
            var matches = new List<(uint recipeId, uint itemId, int level, int amountResult, int craftJobId, string name, bool startsWith)>();
            foreach (var recipe in recipeSheet)
            {
                ct.ThrowIfCancellationRequested();

                var item = recipe.ItemResult.ValueNullable;
                if (item is null || item.Value.IsUntradable || item.Value.Name.IsEmpty) continue;

                var name = item.Value.Name.ExtractText();
                var lower = name.ToLowerInvariant();
                if (!lower.Contains(needle)) continue;

                matches.Add((
                    recipe.RowId,
                    recipe.ItemResult.RowId,
                    (int)recipe.RecipeLevelTable.Value.ClassJobLevel,
                    Math.Max(1, (int)recipe.AmountResult),
                    (int)recipe.CraftType.RowId,
                    name,
                    lower.StartsWith(needle)));
            }

            if (matches.Count == 0)
            {
                SearchStatus = $"No craftable item matches \"{query}\".";
                IsSearching = false;
                OnResultsUpdated?.Invoke();
                return;
            }

            // Best matches first: prefix matches, then shortest name, then alphabetical
            var ordered = matches
                .OrderByDescending(m => m.startsWith)
                .ThenBy(m => m.name.Length)
                .ThenBy(m => m.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var truncated = ordered.Count > SearchMatchCap;
            var top = ordered.Take(SearchMatchCap).ToList();

            SearchStatus = $"Pricing {top.Count} match(es)...";
            OnResultsUpdated?.Invoke();

            var marketData = await universalis
                .GetBatchAsync(top.Select(m => m.itemId).Distinct(), worldOrDc, ct)
                .ConfigureAwait(false);

            var results = new List<ProfitableItem>();
            foreach (var m in top)
            {
                ct.ThrowIfCancellationRequested();
                marketData.TryGetValue(m.itemId, out var data);
                results.Add(new ProfitableItem
                {
                    ItemId = m.itemId,
                    RecipeId = m.recipeId,
                    Name = m.name,
                    RecipeLevel = m.level,
                    CraftJobId = m.craftJobId,
                    CraftJobName = CraftJobNames.Full(m.craftJobId),
                    AmountResult = m.amountResult,
                    MinListingPrice = data?.MinPrice ?? 0,
                    MinListingHqPrice = data?.MinPriceHq ?? 0,
                    SaleVelocity = data?.SaleVelocity ?? 0,
                    SaleVelocityHq = data?.SaleVelocityHq ?? 0,
                    EstimatedMaterialCost = 0,
                    HasActiveListings = (data?.UnitsForSale ?? 0) > 0,
                });
            }

            SearchResults = results;
            SearchStatus = $"{results.Count} match(es)" + (truncated ? $" (showing first {SearchMatchCap})" : "");
            OnResultsUpdated?.Invoke();

            // Enrich the first few with real material costs so profit shows up
            var enrichTasks = results.Take(10)
                .Select(item => EnrichMaterialCostAsync(item, worldOrDc, config.AssumeGatherableFree, ct));
            await Task.WhenAll(enrichTasks).ConfigureAwait(false);
            OnResultsUpdated?.Invoke();
        }
        catch (OperationCanceledException)
        {
            SearchStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "ProfitEngine search failed");
            SearchStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
            OnResultsUpdated?.Invoke();
        }
    }

    public async Task EnrichMaterialCostAsync(ProfitableItem item, string worldOrDc, bool assumeGatherablesFree, CancellationToken ct = default)
    {
        var ingredients = Plugin.RecipeResolver.ResolveFlat(item.RecipeId);
        if (ingredients.Length == 0) return;

        var needsPricing = ingredients
            .Where(i => !i.IsShopBuyable && !(assumeGatherablesFree && i.IsGatherable))
            .Select(i => i.ItemId)
            .Distinct()
            .ToList();

        Dictionary<uint, MarketDataResponse> prices = [];
        if (needsPricing.Count > 0)
            prices = await universalis.GetBatchAsync(needsPricing, worldOrDc, ct).ConfigureAwait(false);

        long total = 0;
        foreach (var ing in ingredients)
        {
            if (prices.TryGetValue(ing.ItemId, out var data))
            {
                ing.MarketMinPrice = data.MinPrice;
                ing.MarketPriceFetched = true;
            }
            total += ing.EstimatedUnitCost(assumeGatherablesFree) * ing.Quantity;
        }

        item.EstimatedMaterialCost = total;
    }

    public bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return false;
            var json = File.ReadAllText(CacheFile);
            var cached = JsonSerializer.Deserialize<List<ProfitableItem>>(json);
            if (cached is { Count: > 0 })
            {
                Results = cached;
                ScanStatus = "Loaded from cache — press Scan to refresh";
                OnResultsUpdated?.Invoke();
                return true;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Failed to load scan cache");
        }
        return false;
    }

    private static void SaveCache(IReadOnlyList<ProfitableItem> results)
    {
        try
        {
            var json = JsonSerializer.Serialize(results);
            File.WriteAllText(CacheFile, json);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Failed to save scan cache");
        }
    }

    public void Dispose()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
        searchCts?.Cancel();
        searchCts?.Dispose();
        universalis.Dispose();
    }
}
