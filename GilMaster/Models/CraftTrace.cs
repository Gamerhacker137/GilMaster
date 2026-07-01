using System.Collections.Generic;

namespace GilMaster.Models;

/// <summary>One recorded synthesis — the step-by-step story of how a craft actually ran, so it can
/// be diffed against what our Raphael solver would do.</summary>
public sealed class CraftTrace
{
    public uint   RecipeId      { get; set; }
    public string RecipeName    { get; set; } = "";
    public string JobName       { get; set; } = "";
    public string Source        { get; set; } = "";   // "GilMaster" or "Artisan / manual"
    public string TimeLabel     { get; set; } = "";   // wall-clock the craft started (HH:mm:ss)

    public int  Craftsmanship { get; set; }
    public int  Control       { get; set; }
    public int  Cp            { get; set; }
    public int  Level         { get; set; }

    public uint MaxProgress   { get; set; }
    public uint MaxQuality    { get; set; }
    public uint MaxDurability { get; set; }

    public uint FinalProgress { get; set; }
    public uint FinalQuality  { get; set; }
    public bool Completed        { get; set; }
    public bool ReachedMaxQuality { get; set; }
    public long DurationMs       { get; set; }

    public List<CraftStepRecord> Steps { get; set; } = [];

    public float QualityPct => MaxQuality > 0 ? (float)FinalQuality / MaxQuality : 0f;
}

/// <summary>One action within a <see cref="CraftTrace"/>, reconstructed from the per-step state delta.</summary>
public sealed class CraftStepRecord
{
    public int    Step        { get; set; }
    public string Action      { get; set; } = "";
    public bool   Exact       { get; set; }        // true when identified unambiguously (buffs)
    public string Condition   { get; set; } = "";
    public uint   Progress    { get; set; }        // AFTER the action
    public uint   Quality     { get; set; }
    public uint   Durability  { get; set; }
    public int    Cp          { get; set; }
    public int    DProgress   { get; set; }
    public int    DQuality    { get; set; }
    public int    CpCost      { get; set; }
    public int    GapMs       { get; set; }        // time spent on this step before the action landed (stall signal)
    public string Buffs       { get; set; } = "";  // compact buff readout when the action fired
}
