using System;

namespace GilMaster.Core.Craft;

public enum CraftSkill
{
    BasicSynthesis, CarefulSynthesis, Groundwork, DelicateSynthesis, MuscleMemory, Reflect,
    BasicTouch, StandardTouch, AdvancedTouch, PreparatoryTouch, PrudentTouch,
    ByregotsBlessing, TrainedFinesse,
    Veneration, Innovation, WasteNot, WasteNot2, GreatStrides, Manipulation,
    MastersMend, Observe,
}

public readonly record struct CrafterStats(int Craftsmanship, int Control, int CP, int Level);

public readonly record struct RecipeStats(
    int MaxProgress, int MaxQuality, int MaxDurability,
    int ProgressDivider, int QualityDivider, int ProgressModifier, int QualityModifier);

public struct SimState
{
    public int Progress, MaxProgress;
    public int Quality, MaxQuality;
    public int Durability, MaxDurability;
    public int CP, MaxCP;
    public int Step;
    // Buff durations — uses N+1 on apply so end-of-step decrement gives N active turns
    public int Veneration, Innovation, WasteNot, Manipulation, GreatStrides, MuscleMemory;
    public int InnerQuiet; // 0-10 stacks
    public bool LastWasBasicTouch, LastWasStandardTouch, LastWasObserve;
    public bool FirstStep;

    public readonly bool IsCompleted => Progress >= MaxProgress;
    public readonly bool IsFailed    => Durability <= 0 && !IsCompleted;

    public static SimState FromStats(in CrafterStats stats, in RecipeStats recipe) => new()
    {
        MaxProgress  = recipe.MaxProgress,
        MaxQuality   = recipe.MaxQuality,
        MaxDurability = recipe.MaxDurability,
        Durability   = recipe.MaxDurability,
        MaxCP        = stats.CP,
        CP           = stats.CP,
        Step         = 1,
        FirstStep    = true,
    };
}

public static class CraftSim
{
    public static readonly CraftSkill[] AllSkills = (CraftSkill[])Enum.GetValues(typeof(CraftSkill));

    public static int CpCost(CraftSkill skill, in SimState s) => skill switch
    {
        CraftSkill.BasicSynthesis    => 0,
        CraftSkill.CarefulSynthesis  => 7,
        CraftSkill.Groundwork        => 18,
        CraftSkill.DelicateSynthesis => 32,
        CraftSkill.MuscleMemory      => 6,
        CraftSkill.Reflect           => 6,
        CraftSkill.BasicTouch        => 18,
        CraftSkill.StandardTouch     => s.LastWasBasicTouch ? 18 : 32,
        CraftSkill.AdvancedTouch     => (s.LastWasStandardTouch || s.LastWasObserve) ? 18 : 46,
        CraftSkill.PreparatoryTouch  => 40,
        CraftSkill.PrudentTouch      => 25,
        CraftSkill.ByregotsBlessing  => 24,
        CraftSkill.TrainedFinesse    => 32,
        CraftSkill.Veneration        => 18,
        CraftSkill.Innovation        => 18,
        CraftSkill.WasteNot          => 56,
        CraftSkill.WasteNot2         => 98,
        CraftSkill.GreatStrides      => 32,
        CraftSkill.Manipulation      => 96,
        CraftSkill.MastersMend       => 88,
        CraftSkill.Observe           => 7,
        _ => 0,
    };

    private static int BaseDurCost(CraftSkill skill) => skill switch
    {
        CraftSkill.PreparatoryTouch  => 20,
        CraftSkill.Groundwork        => 20,
        CraftSkill.PrudentTouch      => 5,
        CraftSkill.TrainedFinesse    => 0,
        CraftSkill.Veneration or CraftSkill.Innovation or
        CraftSkill.WasteNot  or CraftSkill.WasteNot2  or
        CraftSkill.GreatStrides or CraftSkill.Manipulation or
        CraftSkill.MastersMend or CraftSkill.Observe => 0,
        _ => 10,
    };

    public static bool IsValid(CraftSkill skill, in SimState s)
    {
        if (s.CP < CpCost(skill, s)) return false;
        if (skill is CraftSkill.MuscleMemory or CraftSkill.Reflect && !s.FirstStep) return false;
        if (skill == CraftSkill.ByregotsBlessing && s.InnerQuiet == 0) return false;
        if (skill == CraftSkill.TrainedFinesse && s.InnerQuiet < 10) return false;
        if (skill == CraftSkill.PrudentTouch && s.WasteNot > 0) return false;
        // Avoid reapplying buffs that are still healthy
        if (skill == CraftSkill.Veneration   && s.Veneration   > 1) return false;
        if (skill == CraftSkill.Innovation   && s.Innovation   > 1) return false;
        if (skill == CraftSkill.WasteNot     && s.WasteNot     > 2) return false;
        if (skill == CraftSkill.WasteNot2    && s.WasteNot     > 2) return false;
        if (skill == CraftSkill.GreatStrides && s.GreatStrides > 1) return false;
        if (skill == CraftSkill.Manipulation && s.Manipulation > 1) return false;
        if (skill == CraftSkill.MastersMend  && s.Durability >= s.MaxDurability) return false;
        // Must survive the durability cost
        var dur = BaseDurCost(skill);
        if (s.WasteNot > 0 && dur > 0) dur /= 2;
        if (s.Durability - dur < 0) return false;
        return true;
    }

    public static SimState Apply(CraftSkill skill, in SimState s, in CrafterStats stats, in RecipeStats recipe)
    {
        var n = s;
        n.FirstStep          = false;
        n.LastWasBasicTouch   = false;
        n.LastWasStandardTouch = false;
        n.LastWasObserve      = false;
        n.CP -= CpCost(skill, s);

        switch (skill)
        {
            case CraftSkill.BasicSynthesis:
                n.Progress = Clamp(n.Progress + ProgressGain(in stats, in recipe, 120, in s), n.MaxProgress);
                if (s.MuscleMemory > 0) n.MuscleMemory = 0;
                break;
            case CraftSkill.CarefulSynthesis:
                n.Progress = Clamp(n.Progress + ProgressGain(in stats, in recipe, 180, in s), n.MaxProgress);
                if (s.MuscleMemory > 0) n.MuscleMemory = 0;
                break;
            case CraftSkill.Groundwork:
            {
                // Half efficiency if durability is below the action's full cost
                var eff = s.Durability < 20 ? 180 : 360;
                n.Progress = Clamp(n.Progress + ProgressGain(in stats, in recipe, eff, in s), n.MaxProgress);
                if (s.MuscleMemory > 0) n.MuscleMemory = 0;
                break;
            }
            case CraftSkill.DelicateSynthesis:
                n.Progress     = Clamp(n.Progress + ProgressGain(in stats, in recipe, 100, in s), n.MaxProgress);
                if (s.MuscleMemory > 0) n.MuscleMemory = 0;
                n.Quality      = Clamp(n.Quality  + QualityGain(in stats, in recipe, 100, in s), n.MaxQuality);
                n.InnerQuiet   = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.MuscleMemory:
                // 300% progress; sets MM buff (doubling for next progress action)
                n.Progress    = Clamp(n.Progress + ProgressGain(in stats, in recipe, 300, in s), n.MaxProgress);
                n.MuscleMemory = 6; // decremented to 5 this step → active for next 5 turns
                break;
            case CraftSkill.Reflect:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 100, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 3, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.BasicTouch:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 100, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                n.LastWasBasicTouch = true;
                break;
            case CraftSkill.StandardTouch:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 125, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                n.LastWasStandardTouch = true;
                break;
            case CraftSkill.AdvancedTouch:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 150, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.PreparatoryTouch:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 200, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 2, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.PrudentTouch:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 100, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.ByregotsBlessing:
            {
                var eff   = 100 + 20 * s.InnerQuiet;
                n.Quality = Clamp(n.Quality + QualityGain(in stats, in recipe, eff, in s), n.MaxQuality);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                n.InnerQuiet = 0;
                break;
            }
            case CraftSkill.TrainedFinesse:
                n.Quality    = Clamp(n.Quality + QualityGain(in stats, in recipe, 100, in s), n.MaxQuality);
                n.InnerQuiet = Math.Min(n.InnerQuiet + 1, 10);
                if (s.GreatStrides > 0) n.GreatStrides = 0;
                break;
            case CraftSkill.Veneration:   n.Veneration   = 5; break; // 4+1
            case CraftSkill.Innovation:   n.Innovation   = 5; break;
            case CraftSkill.WasteNot:     n.WasteNot     = 5; break;
            case CraftSkill.WasteNot2:    n.WasteNot     = 9; break; // 8+1
            case CraftSkill.GreatStrides: n.GreatStrides  = 4; break; // 3+1
            case CraftSkill.Manipulation: n.Manipulation  = 9; break; // 8+1
            case CraftSkill.MastersMend:  n.Durability = Math.Min(n.Durability + 30, n.MaxDurability); break;
            case CraftSkill.Observe:      n.LastWasObserve = true; break;
        }

        // Spend durability
        var durCost = BaseDurCost(skill);
        if (s.WasteNot > 0 && durCost > 0) durCost /= 2;
        n.Durability -= durCost;

        // Manipulation restores from PREVIOUS turns being active (not the turn it was applied)
        if (s.Manipulation > 0)
            n.Durability = Math.Min(n.Durability + 5, n.MaxDurability);

        // Decrement all buff durations
        if (n.Veneration   > 0) n.Veneration--;
        if (n.Innovation   > 0) n.Innovation--;
        if (n.WasteNot     > 0) n.WasteNot--;
        if (n.GreatStrides > 0) n.GreatStrides--;
        if (n.MuscleMemory > 0) n.MuscleMemory--;
        if (n.Manipulation > 0) n.Manipulation--;

        n.Step++;
        return n;
    }

    public static int ProgressGain(in CrafterStats stats, in RecipeStats recipe, int efficiency, in SimState s)
    {
        var baseVal = stats.Craftsmanship * 10 / recipe.ProgressDivider + 2;
        baseVal     = baseVal * recipe.ProgressModifier / 100;
        var gain    = baseVal * efficiency / 100;
        if (s.Veneration   > 0) gain = gain * 3 / 2;
        if (s.MuscleMemory > 0) gain += gain; // doubles
        return gain;
    }

    public static int QualityGain(in CrafterStats stats, in RecipeStats recipe, int efficiency, in SimState s)
    {
        var baseVal = stats.Control * 10 / recipe.QualityDivider + 35;
        baseVal     = baseVal * recipe.QualityModifier / 100;
        var eff     = efficiency;
        if (s.Innovation  > 0) eff += 50;
        if (s.GreatStrides > 0) eff += 100;
        var gain = baseVal * eff / 100;
        gain     = gain * (100 + 10 * s.InnerQuiet) / 100;
        return gain;
    }

    private static int Clamp(int value, int max) => value > max ? max : value;
}
