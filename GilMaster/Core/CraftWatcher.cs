using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using GilMaster.Core.Craft;
using GilMaster.Models;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Passive synthesis recorder. It never drives a craft — it just reads the synthesis addon each
/// frame (the same state the executor sees) and reconstructs the action taken on every step from
/// the state delta + status changes. Buffs are identified exactly (each applies a known status);
/// touches/synthesis are best-effort from the CP cost + stat delta. The result is a
/// <see cref="CraftTrace"/> you can diff against our Raphael solve to harden the executor —
/// e.g. spot a buff that took seconds to fire (a big GapMs) where Artisan fired it instantly.
/// </summary>
public sealed class CraftWatcher : IDisposable
{
    private const int MaxTraces = 25;

    private readonly List<CraftTrace> traces = [];
    public IReadOnlyList<CraftTrace> Traces => traces;
    public event System.Action? OnChanged;

    // Live recording readout for the Watch tab.
    public bool    IsRecording => active != null;
    public int     LiveSteps   => active?.Steps.Count ?? 0;
    public string? LiveRecipe  => active?.RecipeName;

    // In-flight craft.
    private CraftTrace? active;
    private SynthReader.Snapshot lastSnap;
    private SynthReader.Statuses lastStat;
    private int lastCp;
    private uint lastStep;
    private DateTime lastStepTime;
    private DateTime startTime;
    private string lastAction = "";

    public CraftWatcher() => Service.Framework.Update += Tick;

    public void Clear() { traces.Clear(); OnChanged?.Invoke(); }

    private void Tick(IFramework fw)
    {
        if (!Plugin.Config.ShowCraftWatcher) { active = null; return; }

        if (!Service.Condition[ConditionFlag.Crafting])
        {
            if (active != null) FinalizeTrace();
            return;
        }

        var snap = SynthReader.ReadSynth();
        if (!snap.Valid) return;

        var stat = SynthReader.ReadStatuses();
        var cp   = SynthReader.ReadCp();
        var now  = DateTime.Now;

        if (active == null)
        {
            Begin(snap, stat, cp, now);
            return;
        }

        // Every crafting action advances the step counter by one — so a step change means exactly
        // one action happened between lastSnap (at lastStep) and now. Reconstruct and record it.
        if (snap.Step != lastStep)
        {
            var (label, exact) = Identify(lastSnap, snap, lastStat, stat, lastCp, cp, lastStep, lastAction);
            active.Steps.Add(new CraftStepRecord
            {
                Step       = (int)lastStep,
                Action     = label,
                Exact      = exact,
                Condition  = SynthReader.ConditionName(lastSnap.Condition),
                Progress   = snap.Progress,
                Quality    = snap.Quality,
                Durability = snap.Durability,
                Cp         = cp,
                DProgress  = (int)snap.Progress - (int)lastSnap.Progress,
                DQuality   = (int)snap.Quality  - (int)lastSnap.Quality,
                CpCost     = lastCp - cp,
                GapMs      = (int)(now - lastStepTime).TotalMilliseconds,
                Buffs      = BuffSummary(lastStat),
            });
            lastAction   = label;
            lastStep     = snap.Step;
            lastStepTime = now;
        }

        lastSnap = snap; lastStat = stat; lastCp = cp;
    }

    private void Begin(SynthReader.Snapshot snap, SynthReader.Statuses stat, int cp, DateTime now)
    {
        var (recipeId, recipeName) = SynthReader.DetectRecipe(snap);
        var stats = SynthReader.CrafterStats() ?? (0, 0, 0, 0);
        var player = Service.Objects.LocalPlayer;

        var st = Plugin.CraftExecutor.CurrentState;
        var source = st is CraftExecutor.State.Executing or CraftExecutor.State.WaitingForSynth
            ? "GilMaster" : "Artisan / manual";

        active = new CraftTrace
        {
            RecipeId      = recipeId,
            RecipeName    = string.IsNullOrEmpty(recipeName) ? $"Recipe#{recipeId}" : recipeName,
            JobName       = player?.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "",
            Source        = source,
            TimeLabel     = now.ToString("HH:mm:ss"),
            Craftsmanship = stats.Item1, Control = stats.Item2, Cp = stats.Item3, Level = stats.Item4,
            MaxProgress   = snap.MaxProgress,
            MaxQuality    = snap.MaxQuality,
            MaxDurability = snap.MaxDurability,
        };
        lastSnap = snap; lastStat = stat; lastCp = cp;
        lastStep = snap.Step; lastStepTime = now; startTime = now; lastAction = "";
    }

    private void FinalizeTrace()
    {
        var t = active!;
        active = null;
        if (t.Steps.Count == 0) return; // nothing meaningful captured

        t.FinalProgress     = lastSnap.Progress;
        t.FinalQuality      = lastSnap.Quality;
        t.Completed         = lastSnap.MaxProgress > 0 && lastSnap.Progress >= lastSnap.MaxProgress;
        t.ReachedMaxQuality = lastSnap.MaxQuality > 0 && lastSnap.Quality >= lastSnap.MaxQuality;
        t.DurationMs        = (long)(DateTime.Now - startTime).TotalMilliseconds;

        traces.Insert(0, t);
        while (traces.Count > MaxTraces) traces.RemoveAt(traces.Count - 1);
        OnChanged?.Invoke();
    }

    // ── Action reconstruction ────────────────────────────────────────────────

    private static string BuffSummary(SynthReader.Statuses s)
    {
        var parts = new List<string>();
        if (s.InnerQuiet    > 0) parts.Add($"IQ{s.InnerQuiet}");
        if (s.Veneration    > 0) parts.Add($"Ven{s.Veneration}");
        if (s.Innovation    > 0) parts.Add($"Inn{s.Innovation}");
        if (s.GreatStrides  > 0) parts.Add("GS");
        if (s.WasteNot      > 0) parts.Add($"WN{s.WasteNot}");
        if (s.WasteNot2     > 0) parts.Add($"WN2·{s.WasteNot2}");
        if (s.Manipulation  > 0) parts.Add($"Manip{s.Manipulation}");
        if (s.MuscleMemory  > 0) parts.Add($"MM{s.MuscleMemory}");
        if (s.FinalAppraisal> 0) parts.Add("FA");
        return string.Join(" ", parts);
    }

    // A buff that went from absent to present identifies its action exactly.
    private static string? NewBuff(SynthReader.Statuses a, SynthReader.Statuses b)
    {
        if (a.Veneration   == 0 && b.Veneration   > 0) return "Veneration";
        if (a.Innovation   == 0 && b.Innovation   > 0) return "Innovation";
        if (a.GreatStrides == 0 && b.GreatStrides > 0) return "Great Strides";
        if (a.WasteNot     == 0 && b.WasteNot     > 0) return "Waste Not";
        if (a.WasteNot2    == 0 && b.WasteNot2    > 0) return "Waste Not II";
        if (a.Manipulation == 0 && b.Manipulation > 0) return "Manipulation";
        if (a.MuscleMemory == 0 && b.MuscleMemory > 0) return "Muscle Memory";
        if (a.FinalAppraisal == 0 && b.FinalAppraisal > 0) return "Final Appraisal";
        return null;
    }

    private static (string, bool) Identify(
        SynthReader.Snapshot prev, SynthReader.Snapshot cur,
        SynthReader.Statuses pStat, SynthReader.Statuses cStat,
        int prevCp, int curCp, uint prevStep, string prevAction)
    {
        int dProg = (int)cur.Progress - (int)prev.Progress;
        int dQual = (int)cur.Quality - (int)prev.Quality;
        int dDur  = (int)cur.Durability - (int)prev.Durability;
        int cpCost = prevCp - curCp;

        // 1) A newly-applied buff is unambiguous.
        if (NewBuff(pStat, cStat) is { } buff) return (buff, true);

        // 2) Step-1 quality opener that grants no lasting buff: Reflect (or Trained Eye).
        if (prevStep <= 1 && dQual > 0)
        {
            if (cpCost >= 200 || dQual >= (int)cur.MaxQuality) return ("Trained Eye", false);
            return ("Reflect", false);
        }

        // 3) Master's Mend — durability restored, no progress/quality.
        if (dDur > 0 && dProg == 0 && dQual == 0) return ("Master's Mend", cpCost >= 80);

        // 4) Delicate Synthesis — advances progress AND quality together.
        if (dProg > 0 && dQual > 0) return ("Delicate Synthesis", true);

        // 5) A touch (quality up).
        if (dQual > 0) return (IdentifyTouch(cpCost, prev.Condition, pStat.InnerQuiet, prevAction), false);

        // 6) A synthesis (progress up).
        if (dProg > 0) return (IdentifySynth(cpCost), false);

        // 7) No progress/quality change.
        if (cpCost < 0) return ("Tricks of the Trade", false); // CP restored
        if (cpCost <= 7) return ("Observe", false);
        return ("(unknown)", false);
    }

    private static string IdentifyTouch(int cpCost, int condition, int prevIq, string prevAction)
    {
        // Precise Touch is only usable on Good/Excellent (condition 1/2).
        if (condition is 1 or 2 && cpCost is >= 14 and <= 20) return "Precise Touch";
        return cpCost switch
        {
            24 => "Byregot's Blessing",
            25 => "Prudent Touch",
            40 => "Preparatory Touch",
            46 => "Advanced Touch",
            32 => prevIq >= 10 ? "Trained Finesse" : "Standard Touch",
            0  => "Hasty Touch",
            18 => prevAction.EndsWith("Basic Touch")    ? "Standard Touch"
                : prevAction.EndsWith("Standard Touch") ? "Advanced Touch"
                : "Basic Touch",
            _  => "Touch (?)",
        };
    }

    private static string IdentifySynth(int cpCost) => cpCost switch
    {
        0        => "Basic Synthesis",
        6        => "Muscle Memory",
        7        => "Careful Synthesis",
        18       => "Groundwork",
        _        => "Synthesis (?)",
    };

    public void Dispose() => Service.Framework.Update -= Tick;
}
