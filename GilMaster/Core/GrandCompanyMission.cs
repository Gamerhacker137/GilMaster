using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Reads the crafting items requested by your Grand Company's daily Supply &amp; Provisioning
/// missions (from the open GC Supply window) so they can be turned into a crafting list.
/// </summary>
public static class GrandCompanyMission
{
    public const string ListName = "GC mission";

    /// <summary>
    /// The craftable items the GC wants today (item id + quantity). Requires the GC Supply
    /// window to be open (talk to your Grand Company's Personnel Officer). Returns empty if
    /// the window isn't open / there's nothing craftable.
    /// </summary>
    public static unsafe List<(uint ItemId, int Qty, string Name)> ReadSupplyItems()
    {
        var result = new List<(uint, int, string)>();
        try
        {
            var agent = AgentGrandCompanySupply.Instance();
            if (agent == null || agent->SupplyProvisioningData == null) return result;

            var itemSheet = Service.DataManager.GetExcelSheet<Item>();
            foreach (ref readonly var item in agent->SupplyProvisioningData->SupplyData)
            {
                if (item.ItemId == 0 || item.NumRequested == 0) continue;
                if (Plugin.RecipeResolver.FindRecipeFor(item.ItemId) == null) continue; // craftable only

                var name = itemSheet.GetRowOrDefault(item.ItemId)?.Name.ExtractText() ?? $"Item#{item.ItemId}";
                result.Add((item.ItemId, item.NumRequested, name));
            }
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Read GC supply failed"); }
        return result;
    }

    /// <summary>
    /// Reads the open GC Supply window and (re)builds the persisted "GC mission" craft list,
    /// replacing any prior GC list so repeated clicks always mirror the current missions.
    /// Returns the built list, or null if nothing craftable is listed (window closed / empty).
    /// Shared by the Lists tab button and the in-game GC-window overlay.
    /// </summary>
    public static CraftList? BuildGcList()
    {
        var items = ReadSupplyItems();
        if (items.Count == 0) return null;

        var lists = Plugin.Config.CraftLists;
        lists.RemoveAll(l => l.IsGcMission || l.Name == ListName);

        var gc = new CraftList { Name = ListName, IsGcMission = true };
        foreach (var (id, qty, name) in items)
            gc.Items.Add(new CraftListItem { ItemId = id, Name = name, Quantity = qty });

        lists.Add(gc);
        Plugin.Config.Save();
        return gc;
    }
}
