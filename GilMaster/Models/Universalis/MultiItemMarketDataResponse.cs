using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GilMaster.Models.Universalis;

public sealed class MultiItemMarketDataResponse
{
    [JsonPropertyName("itemIDs")]
    public List<uint> ItemIds { get; set; } = [];

    [JsonPropertyName("items")]
    public Dictionary<string, MarketDataResponse> Items { get; set; } = [];

    [JsonPropertyName("unresolvedItems")]
    public List<uint> UnresolvedItems { get; set; } = [];

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }
}
