using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace GilMaster.Models.Universalis;

public sealed class MarketDataResponse
{
    [JsonPropertyName("itemID")]
    public uint ItemId { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }

    [JsonPropertyName("listings")]
    public List<MarketDataListing> Listings { get; set; } = [];

    [JsonPropertyName("recentHistory")]
    public List<MarketDataRecentHistory> RecentHistory { get; set; } = [];

    [JsonPropertyName("currentAveragePrice")]
    public double CurrentAveragePrice { get; set; }

    [JsonPropertyName("currentAveragePriceNQ")]
    public double CurrentAveragePriceNq { get; set; }

    [JsonPropertyName("currentAveragePriceHQ")]
    public double CurrentAveragePriceHq { get; set; }

    [JsonPropertyName("regularSaleVelocity")]
    public double SaleVelocity { get; set; }

    [JsonPropertyName("nqSaleVelocity")]
    public double SaleVelocityNq { get; set; }

    [JsonPropertyName("hqSaleVelocity")]
    public double SaleVelocityHq { get; set; }

    [JsonPropertyName("minPrice")]
    public long MinPrice { get; set; }

    [JsonPropertyName("minPriceNQ")]
    public long MinPriceNq { get; set; }

    [JsonPropertyName("minPriceHQ")]
    public long MinPriceHq { get; set; }

    [JsonPropertyName("hasData")]
    public bool HasData { get; set; }

    [JsonPropertyName("unitsForSale")]
    public long UnitsForSale { get; set; }

    [JsonPropertyName("unitsSold")]
    public long UnitsSold { get; set; }

    // ── Derived market signals ────────────────────────────────────────────────

    private const long WeekSeconds = 7L * 24 * 3600;
    private static long WeekAgo => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - WeekSeconds;

    /// <summary>
    /// A realistic unit sale price: the median of what the item ACTUALLY sold for over the
    /// LAST 7 DAYS (not just the last sale). Median ignores lowball undercuts and the odd
    /// overpriced flip, so it reflects the going rate far better than the cheapest listing.
    /// If there are too few sales in the past week, it widens to all available history;
    /// failing that, it falls back to the current average, then the cheapest listing.
    /// </summary>
    public long RealisticPrice(bool preferHq)
    {
        IEnumerable<MarketDataRecentHistory> all = RecentHistory.Where(h => h.PricePerUnit > 0);
        if (preferHq)
        {
            var hq = all.Where(h => h.Hq).ToList();
            if (hq.Count > 0) all = hq;
        }
        var allList = all.ToList();

        // Prefer the past week; if it's thin (< 3 sales), use whatever history we have.
        var week = allList.Where(h => h.Timestamp >= WeekAgo).ToList();
        var use  = week.Count >= 3 ? week : allList;

        var prices = use.Select(h => h.PricePerUnit).OrderBy(p => p).ToList();
        if (prices.Count > 0)
            return prices[prices.Count / 2]; // median

        var avg = preferHq && CurrentAveragePriceHq > 0 ? CurrentAveragePriceHq
                : CurrentAveragePrice    > 0 ? CurrentAveragePrice
                : CurrentAveragePriceNq;
        if (avg > 0) return (long)avg;

        return preferHq && MinPriceHq > 0 ? MinPriceHq : MinPrice;
    }

    /// <summary>Units actually sold across the recent-history window — a raw demand signal.</summary>
    public long RecentUnitsSold => RecentHistory?.Sum(h => h.Quantity) ?? 0;

    /// <summary>Units sold in the last 7 days — the weekly demand signal.</summary>
    public long UnitsSold7d => RecentHistory?.Where(h => h.Timestamp >= WeekAgo).Sum(h => h.Quantity) ?? 0;

    /// <summary>
    /// Recent price trend: compares the median of the newer half of recent sales against
    /// the older half. Returns direction (-1 falling, 0 flat, +1 rising) and the % change.
    /// A &gt;5% swing counts as a real move; anything inside that is "flat".
    /// </summary>
    public (sbyte Direction, double Pct) RecentTrend(bool preferHq)
    {
        var sales = RecentHistory.Where(h => h.PricePerUnit > 0).ToList();
        if (preferHq)
        {
            var hq = sales.Where(h => h.Hq).ToList();
            if (hq.Count >= 6) sales = hq;
        }
        if (sales.Count < 6) return (0, 0);

        var ordered = sales.OrderBy(h => h.Timestamp).ToList(); // oldest → newest
        var half = ordered.Count / 2;
        var older = ordered.Take(half).Select(h => h.PricePerUnit).OrderBy(p => p).ToList();
        var newer = ordered.Skip(ordered.Count - half).Select(h => h.PricePerUnit).OrderBy(p => p).ToList();

        long oldMed = older[older.Count / 2];
        long newMed = newer[newer.Count / 2];
        if (oldMed <= 0) return (0, 0);

        var pct = (double)(newMed - oldMed) / oldMed * 100.0;
        sbyte dir = (sbyte)(pct > 5 ? 1 : pct < -5 ? -1 : 0);
        return (dir, pct);
    }
}
