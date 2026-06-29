using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GilMaster;

/// <summary>One item entry in a saved crafting list (idea borrowed from Artisan's lists).</summary>
[Serializable]
public sealed class CraftListItem
{
    public uint   ItemId   { get; set; }
    public string Name     { get; set; } = "";
    public int    Quantity { get; set; } = 1;
}

/// <summary>A named, persisted batch of items to craft in one go.</summary>
[Serializable]
public sealed class CraftList
{
    public string Name { get; set; } = "New List";
    public List<CraftListItem> Items { get; set; } = [];
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Saved crafting lists (Lists tab)
    public List<CraftList> CraftLists { get; set; } = [];

    // Find tab
    public int SelectedCraftJob { get; set; } = 0;
    public int ManualLevelOverride { get; set; } = 0;
    public int LevelBuffer { get; set; } = 5;
    public double MinSaleVelocity { get; set; } = 2.0;
    public int ScanResultLimit { get; set; } = 30;
    public bool PreferHq { get; set; } = true;
    public bool AssumeGatherableFree { get; set; } = true;
    public bool ScanDatacenter { get; set; } = false; // scan whole DC instead of home world
    public bool ScanAllJobs { get; set; } = false;    // scan every crafting job, ignore level filter
    public int SortColumn { get; set; } = 5;          // 0=Item,1=Lvl,2=NQ Price,3=HQ Price,4=Sales/day,5=Gil/day,6=Gil/hr
    public bool SortDescending { get; set; } = true;
    public int MinLevelFilter { get; set; } = 0;      // hide recipes below this level (0 = no limit)
    public int MaxLevelFilter { get; set; } = 0;      // hide recipes above this level (0 = no limit)

    // Gather tab
    public bool ShowAetheryteHints { get; set; } = true;
    public bool ShowInventoryCounts { get; set; } = true;

    // Count items stored on retainers / alts (requires Allagan Tools). When on, Have /
    // "What can I make?" / missing-material checks include your whole cross-character stash.
    public bool IncludeRetainerInventory { get; set; } = false;

    // Craft tab
    public bool ShowBasicRotationHints { get; set; } = true;
    public int CraftQuantity { get; set; } = 1;
    public bool UseHqMaterials { get; set; } = true; // auto-fill HQ mats before synthesizing

    // Auto-craft
    public bool EnableAutoCraft { get; set; } = false;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
