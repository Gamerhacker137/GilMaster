using System.Text.Json.Serialization;

namespace GilMaster.Models.Universalis;

public sealed class MarketDataListing
{
    [JsonPropertyName("hq")]
    public bool Hq { get; set; }

    [JsonPropertyName("pricePerUnit")]
    public long PricePerUnit { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }

    [JsonPropertyName("retainerName")]
    public string? RetainerName { get; set; }

    [JsonPropertyName("lastReviewTime")]
    public long LastReviewTime { get; set; }
}
