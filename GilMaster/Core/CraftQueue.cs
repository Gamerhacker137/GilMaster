using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    public int    RecipeLevel     { get; init; }  // crafter level required for this recipe
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

/// <summary>An item the player has the materials to craft right now.</summary>
public sealed record CraftableSuggestion(uint ItemId, string Name, string JobName, int MaxQuantity);

/// <summary>A node in the queue's nested crafting tree (main item → its sub-components).</summary>
public sealed class QueueTreeNode
{
    public uint   ItemId         { get; init; }
    public string Name           { get; init; } = "";
    public int    QuantityNeeded { get; init; }   // how many this branch needs
    public int    Have           { get; init; }   // currently in inventory
    public int    CraftCount     { get; init; }   // how many to craft for this branch
    public bool   IsCraftable    { get; init; }
    public string JobName        { get; init; } = "";
    public int    RecipeLevel    { get; init; }
    public List<QueueTreeNode> Children { get; } = [];

    public bool Satisfied => Have >= QuantityNeeded;
}

public sealed class CraftQueue
{
    private sealed class RecipeInfo
    {
        public uint                   RecipeId     { get; init; }
        public int                    AmountResult { get; init; } = 1;
        public int                    JobId        { get; init; }
        public string                 JobName      { get; init; } = "";
        public int                    RecipeLevel  { get; init; }
        public (uint ItemId, int Amt)[] Ingredients { get; init; } = [];
    }

    private static readonly string[] CraftJobAbbr = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    // Lazily built on first call to Build()
    private Dictionary<uint, RecipeInfo>? _recipeIndex;

    public List<CraftQueueEntry>  Entries { get; } = [];
    public List<MissingMaterial>  Missing { get; } = [];
    // The top-level items the user asked for — roots of the display tree.
    public List<(uint ItemId, int Quantity)> Targets { get; private set; } = [];
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
        => BuildMulti([(targetItemId, quantity)]);

    // Build a single combined queue for several target items at once (crafting lists).
    // Shared sub-ingredients are accumulated across every target before craft counts
    // are decided, so the queue crafts each intermediate exactly once.
    public void BuildMulti(IReadOnlyList<(uint ItemId, int Quantity)> targets)
    {
        Entries.Clear();
        Missing.Clear();
        Targets = targets.Where(t => t.ItemId != 0 && t.Quantity > 0).ToList();
        _recipeIndex ??= BuildRecipeIndex();

        // Pass 1 — BFS: for each item, compute how many of its ingredients
        // we need and accumulate into `needed`.  Parents are always processed
        // before children in BFS, so a child's total requirement is complete
        // by the time we dequeue it.
        var needed   = new Dictionary<uint, int>();
        var bfsOrder = new List<uint>();
        var bfsQueue = new Queue<uint>();
        var seen     = new HashSet<uint>();
        foreach (var (itemId, quantity) in targets)
        {
            if (itemId == 0 || quantity <= 0) continue;
            needed[itemId] = needed.GetValueOrDefault(itemId) + quantity;
            if (seen.Add(itemId)) bfsQueue.Enqueue(itemId);
        }

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
                RecipeLevel   = ri.RecipeLevel,
                AmountResult  = ri.AmountResult,
                QuantityNeeded = totalNeeded,
                QuantityInBags = inBags,
                QuantityToCraft = craftCount,
            });
        }
    }

    // ── Display tree: main items first, sub-components nested underneath ──
    // Built from the recipe structure of the Targets, netting each branch against
    // current inventory. Sub-components are crafted first (the flat Entries list is
    // leaves-first); this tree is purely the "what feeds what" view for the UI.
    public List<QueueTreeNode> BuildDisplayTree()
    {
        _recipeIndex ??= BuildRecipeIndex();
        var roots = new List<QueueTreeNode>();
        foreach (var (itemId, qty) in Targets)
            roots.Add(BuildNode(itemId, qty, 0));
        return roots;
    }

    private QueueTreeNode BuildNode(uint itemId, int qtyNeeded, int depth)
    {
        var have = GetItemCount(itemId);

        if (depth > 12 || !_recipeIndex!.TryGetValue(itemId, out var ri))
            // Raw material (or depth guard) — a leaf to gather/buy.
            return new QueueTreeNode
            {
                ItemId = itemId, Name = GetItemName(itemId),
                QuantityNeeded = qtyNeeded, Have = have, IsCraftable = false,
            };

        var still      = Math.Max(0, qtyNeeded - have);
        var craftCount = (int)Math.Ceiling((double)still / ri.AmountResult);

        var node = new QueueTreeNode
        {
            ItemId = itemId, Name = GetItemName(itemId),
            QuantityNeeded = qtyNeeded, Have = have,
            CraftCount = craftCount, IsCraftable = true,
            JobName = ri.JobName, RecipeLevel = ri.RecipeLevel,
        };

        // Only expand sub-components we actually need to make (none if we already have enough).
        if (craftCount > 0)
            foreach (var (ingId, ingAmt) in ri.Ingredients)
                if (ingId != 0)
                    node.Children.Add(BuildNode(ingId, ingAmt * craftCount, depth + 1));

        return node;
    }

    // ── Live inventory count (all containers including crystal pouch) ─────
    // During a full-inventory scan (FindCraftableFromInventory) the same materials
    // get queried thousands of times, so an optional memo reads each item from the
    // game only once per scan.
    private static Dictionary<uint, int>? _scanMemo;

    public static int GetItemCount(uint itemId)
    {
        if (_scanMemo != null)
        {
            if (_scanMemo.TryGetValue(itemId, out var cached)) return cached;
            var live = ReadItemCount(itemId);
            _scanMemo[itemId] = live;
            return live;
        }
        return ReadItemCount(itemId);
    }

    // Bag-only count (NOT retainer-aware) — what you can actually craft with right now.
    public static unsafe int GetBagItemCount(uint itemId)
    {
        try { return (int)InventoryManager.Instance()->GetInventoryItemCount(itemId); }
        catch { return 0; }
    }

    // The first direct ingredient that's short for crafting `craftCount` synths of the
    // recipe producing `itemResultId`, checked against bag inventory only (retainer items
    // can't be used to craft). Returns null when every ingredient is present.
    public (string Name, int Need, int Have)? FirstShortIngredient(uint itemResultId, int craftCount)
    {
        _recipeIndex ??= BuildRecipeIndex();
        if (craftCount <= 0 || !_recipeIndex.TryGetValue(itemResultId, out var ri)) return null;

        foreach (var (ingId, amt) in ri.Ingredients)
        {
            if (ingId == 0) continue;
            var need = amt * craftCount;
            var have = GetBagItemCount(ingId);
            if (have < need) return (GetItemName(ingId), need, have);
        }
        return null;
    }

    private static unsafe int ReadItemCount(uint itemId)
    {
        // When enabled, count the whole cross-character stash (active char + retainers
        // + alts) via Allagan Tools, so mats parked on a retainer still count as "have".
        if (Plugin.Config.IncludeRetainerInventory)
        {
            var owned = Plugin.AllaganTools.CountOwned(itemId);
            if (owned >= 0) return (int)owned;
        }
        try { return (int)InventoryManager.Instance()->GetInventoryItemCount(itemId); }
        catch { return 0; }
    }

    // ── Maximum craftable quantity given current inventory ─────────────────
    // Binary-searches using Build() so it handles shared sub-ingredients and
    // craftable intermediates in inventory correctly.
    public int CalcMaxCraftable(uint targetItemId)
    {
        _recipeIndex ??= BuildRecipeIndex();

        Build(targetItemId, 1);
        if (Missing.Count > 0) return 0;

        // Double until crafting fails or we hit 9999
        int upper = 2;
        while (upper <= 9999)
        {
            Build(targetItemId, upper);
            if (Missing.Count > 0) break;
            upper = Math.Min(upper * 2, 10000);
        }

        if (upper > 9999)
        {
            Build(targetItemId, 9999);
            return 9999;
        }

        // Binary search: lo is always craftable, hi is always not
        int lo = upper / 2, hi = upper;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            Build(targetItemId, mid);
            if (Missing.Count == 0) lo = mid;
            else hi = mid;
        }

        Build(targetItemId, lo); // restore Entries/Missing to the max-good state
        return lo;
    }

    // ── "What can I make?" — scan inventory for everything craftable now ──────
    //
    // Walks every craftable recipe, skips ones above the player's level in that
    // craft class, and keeps those whose full material tree is satisfied by current
    // inventory. Uses a one-shot inventory memo so the thousands of lookups stay cheap.
    public List<CraftableSuggestion> FindCraftableFromInventory(int maxResults = 200)
    {
        _recipeIndex ??= BuildRecipeIndex();

        // Read each crafter class level once (jobId 8=CRP … 15=CUL).
        var levels = new Dictionary<int, int>();
        for (var job = 8; job <= 15; job++) levels[job] = GetCrafterLevel(job);

        var results = new List<CraftableSuggestion>();
        _scanMemo = new Dictionary<uint, int>();
        try
        {
            foreach (var (itemId, ri) in _recipeIndex)
            {
                if (levels.GetValueOrDefault(ri.JobId, 99) + Plugin.Config.CraftLevelBuffer < ri.RecipeLevel) continue; // out of reach even with the above-level allowance
                var max = MaxMakeable(itemId);
                if (max <= 0) continue;
                results.Add(new CraftableSuggestion(itemId, GetItemName(itemId), ri.JobName, max));
            }
        }
        finally { _scanMemo = null; }

        return results
            .OrderBy(r => r.JobName, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    // Pure makeability check for `quantity` of an item from current inventory, without
    // disturbing the public Entries/Missing state. Bails out as soon as any raw
    // material is short. Mirrors Build()'s BFS accumulation of shared sub-ingredients.
    private bool CanCraft(uint targetItemId, int quantity)
    {
        var needed   = new Dictionary<uint, int> { [targetItemId] = quantity };
        var bfsQueue = new Queue<uint>();
        var seen     = new HashSet<uint> { targetItemId };
        bfsQueue.Enqueue(targetItemId);

        while (bfsQueue.Count > 0)
        {
            var itemId      = bfsQueue.Dequeue();
            var stillNeeded = Math.Max(0, needed.GetValueOrDefault(itemId) - GetItemCount(itemId));
            if (stillNeeded == 0) continue;
            if (!_recipeIndex!.TryGetValue(itemId, out var ri)) return false; // raw material short

            var craftCount = (int)Math.Ceiling((double)stillNeeded / ri.AmountResult);
            foreach (var (ingId, ingAmt) in ri.Ingredients)
            {
                if (ingId == 0) continue;
                needed[ingId] = needed.GetValueOrDefault(ingId) + ingAmt * craftCount;
                if (seen.Add(ingId)) bfsQueue.Enqueue(ingId);
            }
        }
        return true;
    }

    // Max craftable from inventory via the pure CanCraft check (doesn't touch Entries).
    private int MaxMakeable(uint targetItemId)
    {
        if (!CanCraft(targetItemId, 1)) return 0;

        int upper = 2;
        while (upper <= 9999 && CanCraft(targetItemId, upper))
            upper = Math.Min(upper * 2, 10000);
        if (upper > 9999) return 9999;

        int lo = upper / 2, hi = upper;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (CanCraft(targetItemId, mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    // Player's level in a given crafter class (jobId 8..15). Returns 99 on any failure
    // so a read error never hides craftable items behind the level filter.
    public static unsafe int GetCrafterLevel(int jobId)
    {
        try
        {
            var ps = PlayerState.Instance();
            if (ps == null) return 99;
            var opt = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                             .GetRowOrDefault((uint)jobId);
            if (opt is not { } row) return 99;
            int idx = row.ExpArrayIndex;
            if (idx < 0) return 99;
            return ps->ClassJobLevels[idx];
        }
        catch { return 99; }
    }

    private static string GetItemName(uint itemId)
    {
        var item = Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item?.Name.ExtractText() ?? $"Item#{itemId}";
    }

    // ── Shared craftable-item name search (used by Queue + Lists tabs) ─────
    private static Dictionary<uint, string>? _craftableNames;

    public static List<(uint Id, string Name)> SearchCraftable(string query, int max = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        _craftableNames ??= BuildCraftableNames();
        return _craftableNames
            .Where(kv => kv.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Length)
            .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static Dictionary<uint, string> BuildCraftableNames()
    {
        var dict  = new Dictionary<uint, string>();
        foreach (var recipe in Service.DataManager.GetExcelSheet<Recipe>())
        {
            var id = recipe.ItemResult.RowId;
            if (id == 0 || dict.ContainsKey(id)) continue;
            var name = recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? "";
            if (!string.IsNullOrEmpty(name)) dict[id] = name;
        }
        return dict;
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
                RecipeLevel  = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0,
                Ingredients  = [.. ings],
            };
        }
        return index;
    }
}
