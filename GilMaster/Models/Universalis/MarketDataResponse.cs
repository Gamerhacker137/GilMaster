using System.Collections.Generic;
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
}
