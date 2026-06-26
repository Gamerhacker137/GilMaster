using Dalamud.Configuration;
using System;

namespace GilMaster;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

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
    public int SortColumn { get; set; } = 5;          // 0=Item,1=Lvl,2=NQ Price,3=HQ Price,4=Sales/day,5=Gil/hr
    public bool SortDescending { get; set; } = true;

    // Gather tab
    public bool ShowAetheryteHints { get; set; } = true;
    public bool ShowInventoryCounts { get; set; } = true;

    // Craft tab
    public bool ShowBasicRotationHints { get; set; } = true;
    public int CraftQuantity { get; set; } = 1;

    // Auto-craft
    public bool EnableAutoCraft { get; set; } = false;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
