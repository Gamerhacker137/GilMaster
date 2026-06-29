using FFXIVClientStructs.FFXIV.Client.Game;
using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>
/// Selling helper — scans your inventory for marketable items and tells you what each
/// is worth, how crowded the board is, and the price to list at (undercut the floor by 1).
/// Completes the find → gather → craft → SELL loop.
/// </summary>
public sealed class SellEngine : IDisposable
{
    private readonly UniversalisClient universalis = new();
    private CancellationTokenSource? cts;

    public IReadOnlyList<SellableItem> Results { get; private set; } = [];
    public bool   IsScanning { get; private set; }
    public string Status     { get; private set; } = "Ready";
    public float  Progress   { get; private set; }
    public event System.Action? OnUpdated;

    public void Scan(string worldOrDc)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        IsScanning = true;
        Status = "Reading inventory...";
        Progress = 0f;
        Results = [];
        OnUpdated?.Invoke();
        Task.Run(() => Run(worldOrDc, cts.Token), cts.Token);
    }

    public void Cancel()
    {
        cts?.Cancel();
        IsScanning = false;
        Status = "Cancelled";
    }

    private async Task Run(string world, CancellationToken ct)
    {
        try
        {
            var stash = ReadStash();
            if (stash.Count == 0)
            {
                Status = "No items in your bags.";
                IsScanning = false;
                OnUpdated?.Invoke();
                return;
            }

            // Keep only items that can actually be sold on the market board.
            var itemSheet = Service.DataManager.GetExcelSheet<Item>();
            var marketable = new List<(uint Id, int Qty, bool Hq, ushort Icon, string Name)>();
            foreach (var (id, info) in stash)
            {
                var row = itemSheet.GetRowOrDefault(id);
                if (row is not { } item) continue;
                if (item.IsUntradable || item.ItemSearchCategory.RowId == 0) continue;
                var name = item.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;
                marketable.Add((id, info.Qty, info.Hq, item.Icon, name));
            }

            if (marketable.Count == 0)
            {
                Status = "Nothing in your bags is sellable on the market board.";
                IsScanning = false;
                OnUpdated?.Invoke();
                return;
            }

            Status = $"Pricing {marketable.Count} item(s) on {world}...";
            Progress = 0.1f;
            OnUpdated?.Invoke();

            var data = await universalis.GetBatchAsync(
                marketable.Select(m => m.Id).Distinct(), world, ct,
                (done, total) =>
                {
                    Progress = 0.1f + 0.85f * done / Math.Max(1, total);
                    Status = $"Pricing {done}/{total} on {world}...";
                    OnUpdated?.Invoke();
                }).ConfigureAwait(false);

            var results = new List<SellableItem>();
            foreach (var m in marketable)
            {
                ct.ThrowIfCancellationRequested();
                if (!data.TryGetValue(m.Id, out var md) || !md.HasData) continue;

                results.Add(new SellableItem
                {
                    ItemId   = m.Id,
                    IconId   = m.Icon,
                    Name     = m.Name,
                    HaveQty  = m.Qty,
                    HaveHq   = m.Hq,
                    FloorNq  = md.MinPriceNq > 0 ? md.MinPriceNq : md.MinPrice,
                    FloorHq  = md.MinPriceHq,
                    GoingNq  = md.RealisticPrice(false),
                    GoingHq  = md.RealisticPrice(true),
                    Sellers  = md.Listings.Count,
                    Velocity = md.SaleVelocity,
                    Sold7d   = md.UnitsSold7d,
                    TrendDir = md.RecentTrend(m.Hq).Direction,
                    TrendPct = md.RecentTrend(m.Hq).Pct,
                });
            }

            Results = [.. results.OrderByDescending(r => r.StackValue)];
            Progress = 1f;
            Status = $"Done — {Results.Count} sellable item(s)";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "Sell scan failed");
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            OnUpdated?.Invoke();
        }
    }

    // Sum quantities (and note HQ presence) across the four main bags + saddlebag.
    private static unsafe Dictionary<uint, (int Qty, bool Hq)> ReadStash()
    {
        var stash = new Dictionary<uint, (int Qty, bool Hq)>();
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null) return stash;

            InventoryType[] bags =
            [
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
                InventoryType.SaddleBag1, InventoryType.SaddleBag2,
            ];

            foreach (var bag in bags)
            {
                var c = inv->GetInventoryContainer(bag);
                if (c == null) continue;
                for (var i = 0; i < c->Size; i++)
                {
                    var item = c->Items[i];
                    if (item.ItemId == 0) continue;
                    var hq = (item.Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                    var prev = stash.GetValueOrDefault(item.ItemId);
                    stash[item.ItemId] = (prev.Qty + item.Quantity, prev.Hq || hq);
                }
            }
        }
        catch (Exception ex) { Service.Log.Warning(ex, "ReadStash failed"); }
        return stash;
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        universalis.Dispose();
    }
}
