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
    // Auto-created from a GC delivery mission — removes itself after a successful queue craft.
    public bool IsGcMission { get; set; }
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

    // Backload progress in the solver: build ALL quality first and finish progress LAST, so a craft
    // can never complete before quality is done (this is Artisan's default). Off = the solver may
    // interleave progress, which can end an HQ craft early at partial quality.
    public bool BackloadProgress { get; set; } = true;

    // Craft tab
    public bool ShowBasicRotationHints { get; set; } = true;
    public int CraftQuantity { get; set; } = 1;
    public bool UseHqMaterials { get; set; } = true; // auto-fill HQ mats before synthesizing
    // You can craft recipes above your class level in FFXIV (with a penalty). This is how
    // many levels above your level still count as craftable (e.g. lv18 + 7 → up to lv25).
    public int CraftLevelBuffer { get; set; } = 7;

    // Furniture tab: only show furnishings you can craft at your current crafter levels
    // (+ the above-level buffer). Off = show every furnishing regardless of level.
    public bool FurnitureMyLevelOnly { get; set; } = true;

    // Auto-repair equipped gear (self-repair with dark matter) before each queued craft when
    // durability drops below RepairPercent, so a long batch can't run gear to 0% and start
    // failing crafts. Broken gear that can't be self-repaired stops the batch with a message.
    public bool AutoRepair   { get; set; } = true;
    public int  RepairPercent { get; set; } = 30;

    // Use the game's batch Quick Synthesis for NQ crafts of eligible recipes (much faster than
    // the per-step loop). Only applies when "Prefer HQ" is off; falls back to the normal crafter
    // if quick synth isn't available for the recipe.
    public bool QuickSynthNq { get; set; } = true;

    // Auto-use food/potion before crafting (0 = none). HQ flags pick the HQ version.
    public int  FoodId    { get; set; }
    public bool FoodHq    { get; set; }
    public string FoodName   { get; set; } = "";
    public int  PotionId  { get; set; }
    public bool PotionHq  { get; set; }
    public string PotionName { get; set; } = "";

    // Advanced: reveal the developer Sim/benchmark tab (off by default — its only end-user value,
    // warming the rotation cache, is exposed as a Settings button).
    public bool ShowSimTab { get; set; } = false;

    // Crafter watcher: passively record every synthesis (yours OR Artisan's) step-by-step so you can
    // diff how a craft actually ran against what our Raphael solver would do — used to harden the
    // executor (e.g. the buff-firing timing). When off, no recording and the Watch tab is hidden.
    public bool ShowCraftWatcher { get; set; } = true;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
