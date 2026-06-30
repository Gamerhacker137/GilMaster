using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions; // LeveWork
using FFXIVClientStructs.FFXIV.Client.Game;                        // QuestManager
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Reads the tradecraft levequests you've accepted (from the leve journal in game memory) and
/// the items each one wants you to craft, so they can be turned into a craft list. Accepted leves
/// live in QuestManager.LeveQuests (16 slots; a slot is filled when LeveId != 0). A leve is a
/// craft leve when its DataId resolves to a CraftLeve row, which lists the required items.
/// </summary>
public static class LeveScanner
{
    public const string ListName = "Leve crafts";

    public sealed record LeveInfo(ushort LeveId, string Name, int Level,
        List<(uint ItemId, int Qty, string ItemName, bool Craftable)> Items);

    /// <summary>Accepted craft leves + the leve-allowance counters.</summary>
    public static unsafe (List<LeveInfo> Leves, int Allowances, int Accepted) ReadAcceptedCraftLeves()
    {
        var leves = new List<LeveInfo>();
        int allowances = 0, accepted = 0;
        try
        {
            var qm = QuestManager.Instance();
            if (qm == null) return (leves, 0, 0);
            allowances = qm->NumLeveAllowances;
            accepted   = qm->NumAcceptedLeveQuests;

            var leveSheet = Service.DataManager.GetExcelSheet<Leve>();
            var itemSheet = Service.DataManager.GetExcelSheet<Item>();

            var slots = qm->LeveQuests;
            for (var s = 0; s < slots.Length; s++)
            {
                var leveId = slots[s].LeveId;
                if (leveId == 0) continue;                                  // empty slot
                if (leveSheet.GetRowOrDefault(leveId) is not { } leve) continue;
                if (!leve.DataId.TryGetValue<CraftLeve>(out var craft)) continue; // tradecraft only

                var items = new List<(uint, int, string, bool)>();
                for (var i = 0; i < craft.Item.Count && i < craft.ItemCount.Count; i++)
                {
                    var itemId = craft.Item[i].RowId;
                    if (itemId == 0) continue;
                    var qty  = craft.ItemCount[i];
                    var name = itemSheet.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item#{itemId}";
                    var craftable = Plugin.RecipeResolver.FindRecipeFor(itemId) != null;
                    items.Add((itemId, qty, name, craftable));
                }
                if (items.Count == 0) continue;
                leves.Add(new LeveInfo(leveId, leve.Name.ExtractText(), leve.ClassJobLevel, items));
            }
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Read leves failed"); }
        return (leves, allowances, accepted);
    }

    /// <summary>
    /// (Re)builds the "Leve crafts" list from your accepted craft leves — every craftable item
    /// they need, summed across leves. Returns the list, or null if nothing craftable is required.
    /// </summary>
    public static CraftList? BuildList()
    {
        var (leves, _, _) = ReadAcceptedCraftLeves();
        var totals = new Dictionary<uint, (int Qty, string Name)>();
        foreach (var leve in leves)
            foreach (var (id, qty, name, craftable) in leve.Items)
            {
                if (!craftable) continue;
                totals[id] = totals.TryGetValue(id, out var ex) ? (ex.Qty + qty, name) : (qty, name);
            }
        if (totals.Count == 0) return null;

        var lists = Plugin.Config.CraftLists;
        lists.RemoveAll(l => l.Name == ListName);
        var list = new CraftList { Name = ListName };
        foreach (var (id, (qty, name)) in totals)
            list.Items.Add(new CraftListItem { ItemId = id, Name = name, Quantity = qty });
        lists.Add(list);
        Plugin.Config.Save();
        return list;
    }
}
