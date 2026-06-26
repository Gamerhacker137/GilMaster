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

    /// <summary>
    /// A realistic unit sale price: the median of what the item ACTUALLY sold for
    /// recently. Median ignores lowball undercuts and the odd overpriced flip, so it
    /// reflects the going rate far better than the single cheapest listing (MinPrice).
    /// Falls back to the current average, then the cheapest listing, if no sales exist.
    /// </summary>
    public long RealisticPrice(bool preferHq)
    {
        IEnumerable<MarketDataRecentHistory> sales = RecentHistory.Where(h => h.PricePerUnit > 0);
        if (preferHq)
        {
            var hq = sales.Where(h => h.Hq).ToList();
            if (hq.Count > 0) sales = hq;
        }

        var prices = sales.Select(h => h.PricePerUnit).OrderBy(p => p).ToList();
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
}
