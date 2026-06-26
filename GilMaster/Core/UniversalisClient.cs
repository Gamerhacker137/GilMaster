using GilMaster.Models.Universalis;
using Polly;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

public sealed class UniversalisClient : IDisposable
{
    private const int MaxItemsPerBatch = 100;

    private readonly HttpClient client;
    private readonly ResiliencePipeline pipeline;
    private bool disposed;

    public UniversalisClient()
    {
        client = new HttpClient
        {
            BaseAddress = new Uri("https://universalis.app/api/v2/"),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GilMaster/1.0");

        pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
                UseJitter = true,
            })
            .Build();
    }

    public async Task<MarketDataResponse?> GetItemAsync(uint itemId, string world, CancellationToken ct = default)
    {
        try
        {
            using var stream = await pipeline.ExecuteAsync(
                async t => await client.GetStreamAsync($"{world}/{itemId}?listings=20&entries=20", t).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            return await JsonSerializer.DeserializeAsync<MarketDataResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Service.Log.Warning(ex, $"Failed to fetch market data for item {itemId}");
            return null;
        }
    }

    // Fetches up to 100 items in a single request. Returns a dict of itemId→response.
    public async Task<Dictionary<uint, MarketDataResponse>> GetBatchAsync(IEnumerable<uint> itemIds, string world, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, MarketDataResponse>();
        var batch = new List<uint>();

        foreach (var id in itemIds)
        {
            batch.Add(id);
            if (batch.Count >= MaxItemsPerBatch)
            {
                await FetchBatch(batch, world, result, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await FetchBatch(batch, world, result, ct).ConfigureAwait(false);

        return result;
    }

    private async Task FetchBatch(List<uint> ids, string world, Dictionary<uint, MarketDataResponse> into, CancellationToken ct)
    {
        var joined = string.Join(",", ids);
        try
        {
            using var stream = await pipeline.ExecuteAsync(
                async t => await client.GetStreamAsync($"{world}/{joined}?listings=5&entries=10", t).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            var response = await JsonSerializer.DeserializeAsync<MultiItemMarketDataResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (response?.Items is null) return;

            foreach (var (key, val) in response.Items)
            {
                if (uint.TryParse(key, out var parsedId))
                    into[parsedId] = val;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Service.Log.Warning(ex, $"Batch fetch failed for {ids.Count} items on {world}");
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            client.Dispose();
            disposed = true;
        }
    }
}
