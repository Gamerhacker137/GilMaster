using FFXIVClientStructs.FFXIV.Client.Game.UI;   // UIState
using FFXIVClientStructs.FFXIV.Client.UI;        // AddonSynthesis
using Lumina.Excel.Sheets;
using System;

namespace GilMaster.Core.Craft;

/// <summary>
/// Read-only view of a live synthesis — the same AtkValue indices, status IDs, and recipe
/// derivation the executor uses, factored out so the passive <see cref="CraftWatcher"/> can read
/// the exact state the executor sees WITHOUT driving anything.
/// </summary>
public static class SynthReader
{
    public readonly record struct Snapshot(
        uint Progress, uint MaxProgress,
        uint Quality, uint MaxQuality,
        uint Durability, uint MaxDurability,
        int Condition, uint Step, bool Valid);

    // Crafting buff step-counters (Status.Param), matching the executor / Artisan.
    public readonly record struct Statuses(
        int InnerQuiet, int Veneration, int Innovation, int GreatStrides,
        int WasteNot, int WasteNot2, int Manipulation, int MuscleMemory,
        int FinalAppraisal);

    private static readonly string[] ConditionNames =
        ["Normal", "Good", "Excellent", "Poor", "Centered", "Sturdy", "Pliant", "Malleable", "Primed", "Good Omen", "Robust"];

    public static string ConditionName(int c) => c >= 0 && c < ConditionNames.Length ? ConditionNames[c] : $"Cond{c}";

    public static unsafe Snapshot ReadSynth()
    {
        var ptr = Service.GameGui.GetAddonByName("Synthesis").Address;
        if (ptr == nint.Zero) return default;
        var addon = (AddonSynthesis*)ptr;
        if (!addon->AtkUnitBase.IsVisible) return default;
        if (addon->AtkUnitBase.AtkValuesCount < 18) return default;
        var v = addon->AtkUnitBase.AtkValues;
        if (v[6].UInt == 0) return default;
        return new Snapshot(
            Progress: v[5].UInt, MaxProgress: v[6].UInt,
            Quality: v[9].UInt, MaxQuality: v[17].UInt,
            Durability: v[7].UInt, MaxDurability: v[8].UInt,
            Condition: (int)v[12].UInt,
            Step: v[15].UInt, Valid: true);
    }

    public static int ReadCp() => (int)(Service.Objects.LocalPlayer?.CurrentCp ?? 0);

    public static Statuses ReadStatuses()
    {
        int iq = 0, ven = 0, inn = 0, gs = 0, wn = 0, wn2 = 0, manip = 0, mm = 0, fa = 0;
        var player = Service.Objects.LocalPlayer;
        if (player == null) return default;
        foreach (var s in player.StatusList)
        {
            switch (s.StatusId)
            {
                case 251:  iq    = s.Param; break;
                case 252:  wn    = s.Param; break;
                case 257:  wn2   = s.Param; break;
                case 2226: ven   = s.Param; break;
                case 2189: inn   = s.Param; break;
                case 254:  gs    = s.Param; break;
                case 2191: mm    = s.Param; break;
                case 1164: manip = s.Param; break;
                case 2190: fa    = s.Param; break;
            }
        }
        return new Statuses(iq, ven, inn, gs, wn, wn2, manip, mm, fa);
    }

    public static unsafe (int Craftsmanship, int Control, int Cp, int Level)? CrafterStats()
    {
        var player = Service.Objects.LocalPlayer;
        if (player == null) return null;
        try
        {
            var ui = UIState.Instance();
            if (ui == null) return null;
            var craftsmanship = (int)ui->PlayerState.Attributes[70]; // 70 = Craftsmanship
            var control       = (int)ui->PlayerState.Attributes[71]; // 71 = Control
            if (craftsmanship <= 0 || control <= 0) return null;
            return (craftsmanship, control, (int)player.MaxCp, player.Level);
        }
        catch { return null; }
    }

    /// <summary>Match the current job's recipes to the synth's max prog/qual/dur — same logic the executor uses.</summary>
    public static (uint Id, string Name, bool CanHq) DetectRecipe(Snapshot state)
    {
        try
        {
            var player = Service.Objects.LocalPlayer;
            if (player == null) return (0, "", true);
            var craftType = (int)player.ClassJob.RowId - 8; // 0=CRP…7=CUL
            if (craftType is < 0 or > 7) return (0, "", true);

            foreach (var recipe in Service.DataManager.GetExcelSheet<Recipe>())
            {
                if ((int)recipe.CraftType.RowId != craftType) continue;
                var lvl     = recipe.RecipeLevelTable.Value;
                var maxProg = (int)(lvl.Difficulty * recipe.DifficultyFactor / 100u);
                var maxQual = (int)(lvl.Quality    * recipe.QualityFactor    / 100u);
                var maxDur  = (int)(lvl.Durability * recipe.DurabilityFactor / 100u);
                if (maxProg == (int)state.MaxProgress && maxQual == (int)state.MaxQuality && maxDur == (int)state.MaxDurability)
                    return (recipe.RowId, recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? $"Recipe#{recipe.RowId}",
                            recipe.CanHq || recipe.RequiredQuality > 0);
            }
        }
        catch (Exception ex) { Service.Log.Debug(ex, "[GilMaster] SynthReader.DetectRecipe failed"); }
        return (0, "", true);
    }
}
