using System.Text.Json.Serialization;

namespace GilMaster.Models.Universalis;

public sealed class MarketDataRecentHistory
{
    [JsonPropertyName("hq")]
    public bool Hq { get; set; }

    [JsonPropertyName("pricePerUnit")]
    public long PricePerUnit { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }
}
