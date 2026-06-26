using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

public sealed class CraftQueueEntry
{
    public uint   ItemId          { get; init; }
    public uint   RecipeId        { get; init; }
    public string Name            { get; init; } = "";
    public int    JobId           { get; init; }  // ClassJob row ID: 8=CRP..15=CUL
    public string JobName         { get; init; } = "";
    public int    AmountResult    { get; init; } = 1;
    public int    QuantityNeeded  { get; init; }
    public int    QuantityInBags  { get; init; }
    public int    QuantityToCraft { get; set; }
    public int    QuantityCrafted { get; set; }
    public bool   IsComplete      => QuantityCrafted >= QuantityToCraft;
}

public sealed class MissingMaterial
{
    public uint   ItemId   { get; init; }
    public string Name     { get; init; } = "";
    public int    Quantity { get; set; }
}

public sealed class CraftQueue
{
    private sealed class RecipeInfo
    {
        public uint                   RecipeId     { get; init; }
        public int                    AmountResult { get; init; } = 1;
        public int                    JobId        { get; init; }
        public string                 JobName      { get; init; } = "";
        public (uint ItemId, int Amt)[] Ingredients { get; init; } = [];
    }

    private static readonly string[] CraftJobAbbr = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    // Lazily built on first call to Build()
    private Dictionary<uint, RecipeInfo>? _recipeIndex;

    public List<CraftQueueEntry>  Entries { get; } = [];
    public List<MissingMaterial>  Missing { get; } = [];
    public bool IsEmpty => Entries.Count == 0 && Missing.Count == 0;

    // ── Build the crafting queue for targetItemId × quantity ─────────────
    //
    // Uses BFS to accumulate needed quantities across shared sub-ingredients
    // before deciding craft counts — so "yarn needed by hat directly + yarn
    // needed by undyed cloth" is summed correctly before we figure out how
    // many undyed cloth to craft.
    //
    // Result: Entries ordered leaves-first (sub-components before final item).
    public void Build(uint targetItemId, int quantity)
    {
        Entries.Clear();
        Missing.Clear();
        _recipeIndex ??= BuildRecipeIndex();

        // Pass 1 — BFS: for each item, compute how many of its ingredients
        // we need and accumulate into `needed`.  Parents are always processed
        // before children in BFS, so a child's total requirement is complete
        // by the time we dequeue it.
        var needed   = new Dictionary<uint, int> { [targetItemId] = quantity };
        var bfsOrder = new List<uint>();
        var bfsQueue = new Queue<uint>();
        var seen     = new HashSet<uint> { targetItemId };
        bfsQueue.Enqueue(targetItemId);

        while (bfsQueue.Count > 0)
        {
            var itemId = bfsQueue.Dequeue();
            bfsOrder.Add(itemId);

            var totalNeeded = needed.GetValueOrDefault(itemId);
            var inBags      = GetItemCount(itemId);
            var stillNeeded = Math.Max(0, totalNeeded - inBags);
            if (stillNeeded == 0) continue;

            if (!_recipeIndex.TryGetValue(itemId, out var ri)) continue; // raw material

            var craftCount = (int)Math.Ceiling((double)stillNeeded / ri.AmountResult);
            foreach (var (ingId, ingAmt) in ri.Ingredients)
            {
                if (ingId == 0) continue;
                needed[ingId] = needed.GetValueOrDefault(ingId) + ingAmt * craftCount;
                if (seen.Add(ingId))
                    bfsQueue.Enqueue(ingId);
            }
        }

        // Pass 2 — reversed BFS order gives leaves first (sub-components
        // before the items that need them).
        bfsOrder.Reverse();
        foreach (var itemId in bfsOrder)
        {
            var totalNeeded = needed.GetValueOrDefault(itemId);
            var inBags      = GetItemCount(itemId);
            var stillNeeded = Math.Max(0, totalNeeded - inBags);
            if (stillNeeded == 0) continue;

            if (!_recipeIndex.TryGetValue(itemId, out var ri))
            {
                // Raw material — can't craft, needs to be gathered or bought
                var existing = Missing.Find(m => m.ItemId == itemId);
                if (existing != null)
                    existing.Quantity += stillNeeded;
                else
                    Missing.Add(new MissingMaterial
                    {
                        ItemId   = itemId,
                        Name     = GetItemName(itemId),
                        Quantity = stillNeeded,
                    });
                continue;
            }

            var craftCount = (int)Math.Ceiling((double)stillNeeded / ri.AmountResult);
            Entries.Add(new CraftQueueEntry
            {
                ItemId        = itemId,
                RecipeId      = ri.RecipeId,
                Name          = GetItemName(itemId),
                JobId         = ri.JobId,
                JobName       = ri.JobName,
                AmountResult  = ri.AmountResult,
                QuantityNeeded = totalNeeded,
                QuantityInBags = inBags,
                QuantityToCraft = craftCount,
            });
        }
    }

    // ── Live inventory count ──────────────────────────────────────────────
    public static unsafe int GetItemCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;
        int count = 0;
        // Check all four main inventory bags (indices 0-3)
        for (uint bag = 0; bag <= 3; bag++)
        {
            var container = inv->GetInventoryContainer((InventoryType)bag);
            if (container == null) continue;
            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;
                if (slot->ItemId == itemId)
                    count += (int)slot->Quantity;
            }
        }
        return count;
    }

    private static string GetItemName(uint itemId)
    {
        var item = Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item?.Name.ExtractText() ?? $"Item#{itemId}";
    }

    // ── Recipe index — built once per session ─────────────────────────────
    // Maps itemResultId → RecipeInfo so we can resolve the tree without
    // doing a linear scan of 10,000+ recipes for every ingredient.
    private static Dictionary<uint, RecipeInfo> BuildRecipeIndex()
    {
        var index = new Dictionary<uint, RecipeInfo>();
        var sheet = Service.DataManager.GetExcelSheet<Recipe>();

        foreach (var recipe in sheet)
        {
            var resultId = recipe.ItemResult.RowId;
            if (resultId == 0 || index.ContainsKey(resultId)) continue;

            var craftTypeId = (int)recipe.CraftType.RowId;  // 0=CRP … 7=CUL
            var jobId       = craftTypeId + 8;               // ClassJob: CRP=8 … CUL=15
            var jobName     = (uint)craftTypeId < (uint)CraftJobAbbr.Length
                                ? CraftJobAbbr[craftTypeId] : "???";

            var ingRows = recipe.Ingredient.ToArray();
            var ingAmts = recipe.AmountIngredient.ToArray();
            var ings    = new List<(uint, int)>();
            for (int i = 0; i < ingRows.Length && i < ingAmts.Length; i++)
            {
                var ingId = (uint)ingRows[i].RowId;
                var amt   = (int)ingAmts[i];
                if (ingId != 0 && amt > 0) ings.Add((ingId, amt));
            }

            index[resultId] = new RecipeInfo
            {
                RecipeId     = recipe.RowId,
                AmountResult = recipe.AmountResult > 0 ? recipe.AmountResult : 1,
                JobId        = jobId,
                JobName      = jobName,
                Ingredients  = [.. ings],
            };
        }
        return index;
    }
}
