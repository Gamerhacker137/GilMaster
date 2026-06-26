using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using GilMaster.Core.Craft;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using CSActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;

namespace GilMaster.Core;

public sealed class CraftExecutor : IDisposable
{
    private enum SynthCondition { Normal, Good, Excellent, Poor, Centered, Sturdy, Pliant, Malleable, Primed, GoodOmen, Robust }

    private record struct SynthState(
        uint Progress, uint MaxProgress,
        uint Quality, uint MaxQuality,
        uint Durability, uint MaxDurability,
        SynthCondition Condition, uint StepCount, bool Valid);

    private record struct ActionDef(uint BaseId, bool IsCraftAction, string Label);

    // ── Symbolic base IDs for CraftActions (resolved to class-specific via CraftAction sheet)
    private static class Act
    {
        public const uint BasicSynthesis    = 100001;
        public const uint BasicTouch        = 100002;
        public const uint MastersMend       = 100003;
        public const uint StandardTouch     = 100004;
        public const uint Observe           = 100010;
        public const uint PreciseTouch      = 100128;
        public const uint CarefulSynthesis  = 100203;
        public const uint ByregotsBlessing  = 100339;
        public const uint MuscleMemory      = 100379;
        public const uint Groundwork        = 100800;
        public const uint AdvancedTouch     = 100801;
        public const uint PreparatoryTouch  = 100802;
        public const uint PrudentTouch      = 100803;
        public const uint TrainedFinesse    = 100804;
        public const uint Reflect           = 100805;
        public const uint DelicateSynthesis = 100806;
        // Regular (non-class-specific) action IDs
        public const uint Manipulation = 4574;
        public const uint WasteNot     = 4631;
        public const uint WasteNot2    = 4639;
        public const uint Veneration   = 19297;
        public const uint Innovation   = 19004;
        public const uint GreatStrides = 260;
    }

    public enum State { Idle, WaitingForSynth, Executing, Done }

    private int craftsRemaining;
    private DateTime lastActionAt = DateTime.MinValue;
    private const double StepDelay = 3.5;
    private bool wasInCraft;
    private uint lastLoggedStep = uint.MaxValue;
    private uint lastNotUsableStep = uint.MaxValue;
    private bool loggedUnreadable;
    private bool synthActive;
    private DateTime waitingSince = DateTime.MinValue;

    private Dictionary<uint, uint>? idCache;
    private int cachedJobId = -1;

    // Rotation plan produced by RotationPlanner before each craft
    private uint currentRecipeId;
    private CraftSkill[]? plannedRotation;
    private int planStep;

    public State CurrentState { get; private set; } = State.Idle;
    public string StatusText { get; private set; } = "Idle";
    public string? Recommendation { get; private set; }
    public bool InSynthesis { get; private set; }

    public event System.Action? OnChanged;

    public CraftExecutor() { Service.Framework.Update += Tick; }

    public void Start(int quantity, uint recipeId = 0)
    {
        craftsRemaining  = quantity;
        currentRecipeId  = recipeId;
        plannedRotation  = null;
        planStep         = 0;
        lastActionAt     = DateTime.MinValue;
        lastLoggedStep   = uint.MaxValue;
        lastNotUsableStep = uint.MaxValue;
        loggedUnreadable = false;
        CurrentState     = State.WaitingForSynth;
        StatusText       = $"Open crafting window and start synth (×{quantity})...";
        Service.Log.Information($"[GilMaster] Auto-craft armed for {quantity} craft(s), recipeId={recipeId}.");
        OnChanged?.Invoke();
    }

    public void Stop()
    {
        CurrentState    = State.Idle;
        StatusText      = "Stopped.";
        Recommendation  = null;
        InSynthesis     = false;
        plannedRotation = null;
        planStep        = 0;
        OnChanged?.Invoke();
    }

    private unsafe void Tick(IFramework fw)
    {
        var inCraft = Service.Condition[ConditionFlag.Crafting];

        if (!inCraft && wasInCraft)
        {
            InSynthesis     = false;
            Recommendation  = null;
            idCache         = null;
            lastLoggedStep  = uint.MaxValue;
            lastNotUsableStep = uint.MaxValue;
            loggedUnreadable = false;
            synthActive     = false;

            if (CurrentState == State.Executing)
            {
                craftsRemaining--;
                if (craftsRemaining <= 0)
                {
                    CurrentState = State.Done;
                    StatusText = "All crafts complete!";
                }
                else
                {
                    CurrentState    = State.WaitingForSynth;
                    StatusText      = $"Craft done — start next synth ({craftsRemaining} left)...";
                    waitingSince    = DateTime.MinValue;
                    plannedRotation = null;
                    planStep        = 0;
                    CraftStarter.synthNodesDumped = false;
                }
                OnChanged?.Invoke();
            }
        }

        wasInCraft = inCraft;

        if (CurrentState == State.WaitingForSynth)
        {
            if (waitingSince == DateTime.MinValue) waitingSince = DateTime.Now;
            if ((DateTime.Now - waitingSince).TotalSeconds >= 1.0)
            {
                CraftStarter.TryClickSynthesis();
                waitingSince = DateTime.Now;
            }
        }

        if (!inCraft) return;

        var state = ReadSynthState();
        InSynthesis = state.Valid;
        if (!state.Valid)
        {
            if (synthActive && CurrentState == State.Executing)
            {
                synthActive = false;
                craftsRemaining--;
                if (craftsRemaining <= 0)
                {
                    CurrentState = State.Done;
                    StatusText = "All crafts complete!";
                }
                else
                {
                    CurrentState    = State.WaitingForSynth;
                    StatusText      = $"Craft done — start next synth ({craftsRemaining} left)...";
                    lastActionAt    = DateTime.Now;
                    waitingSince    = DateTime.MinValue;
                    plannedRotation = null;
                    planStep        = 0;
                    CraftStarter.synthNodesDumped = false;
                }
                lastLoggedStep    = uint.MaxValue;
                lastNotUsableStep = uint.MaxValue;
                OnChanged?.Invoke();
            }

            if (!loggedUnreadable)
            {
                Service.Log.Information("[GilMaster] Synthesis addon not readable yet — waiting.");
                loggedUnreadable = true;
            }
            return;
        }

        loggedUnreadable = false;
        synthActive      = true;

        if (CurrentState == State.WaitingForSynth)
        {
            CurrentState = State.Executing;
            lastActionAt = DateTime.Now;
            waitingSince = DateTime.MinValue;
        }

        // Plan on step 1 (once per craft, only when we have a recipe ID)
        if (state.StepCount == 1 && plannedRotation == null && currentRecipeId != 0)
            BuildPlan();

        var buffs  = ReadBuffs();
        bool isOverride;
        var next   = PickAction(state, buffs, out isOverride);

        Recommendation = next?.Label ?? "(none)";
        OnChanged?.Invoke();

        if (state.StepCount != lastLoggedStep)
        {
            lastLoggedStep = state.StepCount;
            var planInfo = plannedRotation != null
                ? $"plan[{planStep}/{plannedRotation.Length}]"
                : "greedy";
            Service.Log.Information(
                $"[GilMaster] Step {state.StepCount} | Prog {state.Progress}/{state.MaxProgress}" +
                $" Qual {state.Quality}/{state.MaxQuality} Dur {state.Durability}/{state.MaxDurability}" +
                $" Cond {state.Condition} | {planInfo} | decided: {next?.Label ?? "(none)"}");
        }

        if (CurrentState != State.Executing) return;
        if (Service.Condition[ConditionFlag.Occupied]) return;
        if ((DateTime.Now - lastActionAt).TotalSeconds < StepDelay) return;
        if (next == null) return;

        var act         = next.Value;
        var resolvedId  = ResolveId(act.BaseId, act.IsCraftAction);
        var actionType  = act.IsCraftAction ? CSActionType.CraftAction : CSActionType.Action;
        var status      = ActionManager.Instance()->GetActionStatus(actionType, resolvedId);

        if (status == 0)
        {
            var used = ActionManager.Instance()->UseAction(actionType, resolvedId, 0xE0000000UL);
            lastActionAt = DateTime.Now;
            StatusText   = $"[Step {state.StepCount}] {act.Label}";
            Service.Log.Information($"[GilMaster] -> {act.Label} (id {resolvedId}); used={used}");
            OnChanged?.Invoke();

            // Advance plan step only when we executed a planned action (not a condition override or greedy)
            if (!isOverride && plannedRotation != null)
                planStep++;
        }
        else
        {
            lastActionAt = DateTime.Now - TimeSpan.FromSeconds(StepDelay - 1.0);
            if (state.StepCount != lastNotUsableStep)
            {
                lastNotUsableStep = state.StepCount;
                Service.Log.Warning($"[GilMaster] {act.Label} (id {resolvedId}) status={status} — retrying");
            }
        }
    }

    // ── Action selection ─────────────────────────────────────────────────────

    private unsafe ActionDef? PickAction(SynthState state, Buffs buffs, out bool isOverride)
    {
        // Special-condition overrides (Excellent, Poor, Pliant, Good)
        var cond = GetConditionOverride(state, buffs);
        if (cond != null)
        {
            isOverride = true;
            return cond;
        }

        // Follow planned rotation
        if (plannedRotation != null && planStep < plannedRotation.Length)
        {
            var skill = plannedRotation[planStep];
            var def   = SkillToActionDef(skill);
            if (def != null)
            {
                // Check the action is usable right now (might not be on first frame)
                var rid = ResolveId(def.Value.BaseId, def.Value.IsCraftAction);
                var at  = def.Value.IsCraftAction ? CSActionType.CraftAction : CSActionType.Action;
                if (ActionManager.Instance()->GetActionStatus(at, rid) == 0 ||
                    ActionManager.Instance()->GetActionStatus(at, rid) == 571 /* GCD */)
                {
                    isOverride = false;
                    return def;
                }
            }
            // Plan step unusable — skip it
            Service.Log.Warning($"[GilMaster] plan[{planStep}] {skill} not usable, skipping");
            planStep++;
        }

        // Greedy fallback
        isOverride = true;
        return DecideAction(state, buffs);
    }

    private unsafe ActionDef? GetConditionOverride(SynthState s, Buffs b)
    {
        float qPct = s.MaxQuality > 0 ? (float)s.Quality / s.MaxQuality : 0f;
        return s.Condition switch
        {
            // Excellent: maximise the quality window
            SynthCondition.Excellent when b.IQ >= 1 && qPct >= 0.3f && CanUseCraft(Act.ByregotsBlessing)
                => Def(Act.ByregotsBlessing, true, "Byregot's (Excellent!)"),
            SynthCondition.Excellent
                => Def(Act.BasicTouch, true, "Basic Touch (Excellent!)"),
            // Poor: waste nothing
            SynthCondition.Poor
                => Def(Act.Observe, true, "Observe (Poor)"),
            // Good: Precise Touch
            SynthCondition.Good when CanUseCraft(Act.PreciseTouch)
                => Def(Act.PreciseTouch, true, "Precise Touch (Good)"),
            // Pliant: cheap Manipulation
            SynthCondition.Pliant when !b.Manipulation && CanUseAction(Act.Manipulation)
                => Def(Act.Manipulation, false, "Manipulation (Pliant)"),
            _ => null,
        };
    }

    // ── Planning ─────────────────────────────────────────────────────────────

    private unsafe void BuildPlan()
    {
        var stats  = ReadCrafterStats();
        if (stats == null) return;
        var recipe = ReadRecipeStats(currentRecipeId);
        if (recipe == null) return;

        var preferHq = Plugin.Config.PreferHq;
        var learned  = GetLearnedSkills(stats.Value);
        var plan     = RotationPlanner.Plan(stats.Value, recipe.Value, learned, preferHq);

        if (plan == null || plan.Count == 0)
        {
            Service.Log.Warning(preferHq
                ? "[GilMaster] Planner found no HQ rotation — using greedy fallback."
                : "[GilMaster] NQ mode — skipping planner, using CarefulSynthesis loop.");
            return;
        }

        plannedRotation = [.. plan];
        planStep        = 0;
        Service.Log.Information(
            $"[GilMaster] {(preferHq ? "HQ" : "NQ")} plan ({plannedRotation.Length} steps): " +
            string.Join(" → ", plannedRotation.Select(s => s.ToString())));
    }

    private static unsafe CrafterStats? ReadCrafterStats()
    {
        var player = Service.Objects.LocalPlayer;
        if (player == null) return null;
        try
        {
            var ui = UIState.Instance();
            if (ui == null) return null;
            // BaseParam IDs: 70 = Craftsmanship, 71 = Control
            var craftsmanship = (int)ui->PlayerState.Attributes[70];
            var control       = (int)ui->PlayerState.Attributes[71];
            if (craftsmanship <= 0 || control <= 0) return null;
            return new CrafterStats(craftsmanship, control, (int)player.MaxCp, player.Level);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] ReadCrafterStats failed");
            return null;
        }
    }

    private static RecipeStats? ReadRecipeStats(uint recipeId)
    {
        if (recipeId == 0) return null;
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<Recipe>();
            var opt   = sheet.GetRowOrDefault(recipeId);
            if (opt is null) return null;
            var rec   = opt.Value;
            var lvl   = rec.RecipeLevelTable.Value;

            var maxProg = (int)(lvl.Difficulty  * rec.DifficultyFactor  / 100u);
            var maxQual = (int)(lvl.Quality      * rec.QualityFactor     / 100u);
            var maxDur  = (int)(lvl.Durability   * rec.DurabilityFactor  / 100u);
            var pDiv    = (int)lvl.ProgressDivider;
            var qDiv    = (int)lvl.QualityDivider;
            var pMod    = lvl.ProgressModifier > 0 ? (int)lvl.ProgressModifier : 100;
            var qMod    = lvl.QualityModifier  > 0 ? (int)lvl.QualityModifier  : 100;

            if (maxProg <= 0 || maxDur <= 0 || pDiv <= 0) return null;
            return new RecipeStats(maxProg, maxQual, maxDur, pDiv, qDiv, pMod, qMod);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] ReadRecipeStats failed");
            return null;
        }
    }

    private unsafe HashSet<CraftSkill> GetLearnedSkills(CrafterStats stats)
    {
        var learned = new HashSet<CraftSkill>();
        if (idCache == null) ResolveId(Act.BasicSynthesis, true); // force cache build

        foreach (var skill in CraftSim.AllSkills)
        {
            var def = SkillToActionDef(skill);
            if (def == null) continue;

            uint status;
            if (def.Value.IsCraftAction)
            {
                var rid = ResolveId(def.Value.BaseId, true);
                // If resolution failed (returned the synthetic base ID), skip — not available for this job
                if (rid == def.Value.BaseId && def.Value.BaseId >= 100000) continue;
                status = ActionManager.Instance()->GetActionStatus(CSActionType.CraftAction, rid);
            }
            else
            {
                status = ActionManager.Instance()->GetActionStatus(CSActionType.Action, def.Value.BaseId);
            }
            // 573 = not learned, 579 = wrong class — exclude only those
            if (status != 573 && status != 579)
                learned.Add(skill);
        }

        // Always include the absolute basics
        learned.Add(CraftSkill.BasicSynthesis);
        learned.Add(CraftSkill.BasicTouch);
        return learned;
    }

    private static ActionDef? SkillToActionDef(CraftSkill skill)
    {
        return skill switch
        {
            CraftSkill.BasicSynthesis    => Def(Act.BasicSynthesis,    true,  "Basic Synthesis"),
            CraftSkill.CarefulSynthesis  => Def(Act.CarefulSynthesis,  true,  "Careful Synthesis"),
            CraftSkill.Groundwork        => Def(Act.Groundwork,        true,  "Groundwork"),
            CraftSkill.DelicateSynthesis => Def(Act.DelicateSynthesis, true,  "Delicate Synthesis"),
            CraftSkill.MuscleMemory      => Def(Act.MuscleMemory,      true,  "Muscle Memory"),
            CraftSkill.Reflect           => Def(Act.Reflect,           true,  "Reflect"),
            CraftSkill.BasicTouch        => Def(Act.BasicTouch,        true,  "Basic Touch"),
            CraftSkill.StandardTouch     => Def(Act.StandardTouch,     true,  "Standard Touch"),
            CraftSkill.AdvancedTouch     => Def(Act.AdvancedTouch,     true,  "Advanced Touch"),
            CraftSkill.PreparatoryTouch  => Def(Act.PreparatoryTouch,  true,  "Preparatory Touch"),
            CraftSkill.PrudentTouch      => Def(Act.PrudentTouch,      true,  "Prudent Touch"),
            CraftSkill.ByregotsBlessing  => Def(Act.ByregotsBlessing,  true,  "Byregot's Blessing"),
            CraftSkill.TrainedFinesse    => Def(Act.TrainedFinesse,     true,  "Trained Finesse"),
            CraftSkill.Veneration        => Def(Act.Veneration,        false, "Veneration"),
            CraftSkill.Innovation        => Def(Act.Innovation,        false, "Innovation"),
            CraftSkill.WasteNot          => Def(Act.WasteNot,          false, "Waste Not"),
            CraftSkill.WasteNot2         => Def(Act.WasteNot2,         false, "Waste Not II"),
            CraftSkill.GreatStrides      => Def(Act.GreatStrides,      false, "Great Strides"),
            CraftSkill.Manipulation      => Def(Act.Manipulation,      false, "Manipulation"),
            CraftSkill.MastersMend       => Def(Act.MastersMend,       true,  "Master's Mend"),
            CraftSkill.Observe           => Def(Act.Observe,           true,  "Observe"),
            _ => null,
        };
    }

    // ── Buff reading ─────────────────────────────────────────────────────────

    private record struct Buffs(int IQ, bool Veneration, bool Innovation, bool WasteNot, bool Manipulation, bool MuscleMemory);

    private static Buffs ReadBuffs()
    {
        int iq = 0; bool ven = false, inn = false, wn = false, manip = false, mm = false;
        var player = Service.Objects.LocalPlayer;
        if (player == null) return default;
        foreach (var s in player.StatusList)
        {
            switch (s.StatusId)
            {
                case 251:  iq    = s.Param; break;
                case 2226: ven   = true; break;
                case 2189: inn   = true; break;
                case 252: case 257: wn = true; break;
                case 1164: manip = true; break;
                case 2191: mm    = true; break;
            }
        }
        return new Buffs(iq, ven, inn, wn, manip, mm);
    }

    // ── Greedy fallback (used when no plan is available) ─────────────────────
    //
    // NQ: pure CarefulSynthesis loop.
    // HQ: quality-first rotation using buff combos and the BT→ST→AT chain.

    private ActionDef? DecideAction(SynthState s, Buffs b)
    {
        var preferHq = Plugin.Config.PreferHq;

        // ── NQ fast path ─────────────────────────────────────────────────────
        if (!preferHq)
        {
            return CanUseCraft(Act.CarefulSynthesis)
                ? Def(Act.CarefulSynthesis, true, "Careful Synthesis")
                : Def(Act.BasicSynthesis,   true, "Basic Synthesis");
        }

        // ── HQ fallback rotation ─────────────────────────────────────────────
        var step = (int)s.StepCount;
        float pPct = s.MaxProgress > 0 ? (float)s.Progress / s.MaxProgress : 0f;
        float qPct = s.MaxQuality  > 0 ? (float)s.Quality  / s.MaxQuality  : 0f;

        // Step 1 opener: Reflect (3 IQ instantly) > Muscle Memory > Veneration
        if (step == 1)
        {
            if (CanUseCraft(Act.Reflect))      return Def(Act.Reflect,      true,  "Reflect");
            if (CanUseCraft(Act.MuscleMemory)) return Def(Act.MuscleMemory, true,  "Muscle Memory");
            if (CanUseAction(Act.Veneration))  return Def(Act.Veneration,   false, "Veneration");
            return Def(Act.BasicSynthesis, true, "Basic Synthesis");
        }

        // Emergency: low durability
        if (s.Durability <= 10)
        {
            if (CanUseCraft(Act.MastersMend)) return Def(Act.MastersMend, true, "Master's Mend!");
            return Def(Act.BasicSynthesis, true, "Basic Synthesis");
        }

        // Conditions already handled by GetConditionOverride

        // Durability management
        if (s.Durability <= 25 && !b.Manipulation && CanUseAction(Act.Manipulation))
            return Def(Act.Manipulation, false, "Manipulation");
        if (s.Durability <= 20 && CanUseCraft(Act.MastersMend))
            return Def(Act.MastersMend, true, "Master's Mend");

        // Finish: max IQ → GreatStrides + Byregot's, then close out progress
        if (qPct >= 0.85f || b.IQ >= 10)
        {
            if (b.IQ >= 1 && CanUseCraft(Act.ByregotsBlessing))
                return Def(Act.ByregotsBlessing, true, "Byregot's Blessing");
            if (pPct < 1f)
                return CanUseCraft(Act.CarefulSynthesis)
                    ? Def(Act.CarefulSynthesis, true, "Careful Synthesis")
                    : Def(Act.BasicSynthesis, true, "Basic Synthesis");
        }

        // Quality phase — build IQ to 10 with buff-supported combo chain
        if (pPct < 0.50f)
        {
            // Need more progress first; Veneration + progress
            if (!b.Veneration && CanUseAction(Act.Veneration))
                return Def(Act.Veneration, false, "Veneration");
            if (CanUseCraft(Act.CarefulSynthesis))
                return Def(Act.CarefulSynthesis, true, "Careful Synthesis");
        }

        // Innovation before touch combo
        if (!b.Innovation && CanUseAction(Act.Innovation))
            return Def(Act.Innovation, false, "Innovation");

        // Touch combo: BT(18)→ST(18)→AT(18) = 54 CP for 375% quality vs 54 CP for 300% (3×BT)
        if (CanUseCraft(Act.AdvancedTouch))
            return Def(Act.AdvancedTouch, true, "Advanced Touch");
        if (CanUseCraft(Act.StandardTouch))
            return Def(Act.StandardTouch, true, "Standard Touch");
        if (CanUseCraft(Act.PrudentTouch) && !b.WasteNot)
            return Def(Act.PrudentTouch, true, "Prudent Touch");
        return Def(Act.BasicTouch, true, "Basic Touch");
    }

    private static ActionDef Def(uint id, bool craft, string label) => new(id, craft, label);

    // ── AddonSynthesis state reading ─────────────────────────────────────────

    private static unsafe SynthState ReadSynthState()
    {
        var ptr = Service.GameGui.GetAddonByName("Synthesis").Address;
        if (ptr == nint.Zero) return default;
        var addon = (AddonSynthesis*)ptr;
        if (!addon->AtkUnitBase.IsVisible) return default;
        if (addon->AtkUnitBase.AtkValuesCount < 18) return default;
        var v = addon->AtkUnitBase.AtkValues;
        if (v[6].UInt == 0) return default;
        return new SynthState(
            Progress: v[5].UInt, MaxProgress: v[6].UInt,
            Quality:  v[9].UInt, MaxQuality:  v[17].UInt,
            Durability: v[7].UInt, MaxDurability: v[8].UInt,
            Condition: (SynthCondition)v[12].UInt,
            StepCount: v[15].UInt, Valid: true);
    }

    // ── Action availability ─────────────────────────────────────────────────

    private unsafe bool CanUseCraft(uint baseId)
    {
        var rid = ResolveId(baseId, true);
        // Synthetic base IDs (100000+) are never real game IDs.
        // If resolution failed (rid == baseId), treat as not available.
        if (rid == baseId && baseId >= 100000) return false;
        return ActionManager.Instance()->GetActionStatus(CSActionType.CraftAction, rid) == 0;
    }

    private static unsafe bool CanUseAction(uint baseId)
        => ActionManager.Instance()->GetActionStatus(CSActionType.Action, baseId) == 0;

    // ── CraftAction base ID → class-specific ID ─────────────────────────────

    private int cachedLevel = -1; // rebuild plan when level changes

    private uint ResolveId(uint baseId, bool isCraftAction)
    {
        if (!isCraftAction) return baseId;
        var player = Service.Objects.LocalPlayer;
        if (player == null) return baseId;
        var jobId = (int)player.ClassJob.RowId;
        var level = player.Level;
        if (idCache == null || cachedJobId != jobId)
        {
            cachedJobId   = jobId;
            cachedLevel   = level;
            idCache       = BuildIdCache(jobId);
        }
        else if (level != cachedLevel)
        {
            // Level up — invalidate plan so it's rebuilt with newly learned skills
            cachedLevel     = level;
            plannedRotation = null;
            planStep        = 0;
        }
        // r == 0 means the skill doesn't exist for this job → return baseId as fallback
        // (CanUseCraft will detect baseId >= 100000 and return false)
        return idCache.TryGetValue(baseId, out var r) && r != 0 ? r : baseId;
    }

    private static readonly Dictionary<uint, string> BaseIdNames = new()
    {
        [Act.BasicSynthesis]    = "Basic Synthesis",
        [Act.BasicTouch]        = "Basic Touch",
        [Act.MastersMend]       = "Master's Mend",
        [Act.StandardTouch]     = "Standard Touch",
        [Act.Observe]           = "Observe",
        [Act.PreciseTouch]      = "Precise Touch",
        [Act.CarefulSynthesis]  = "Careful Synthesis",
        [Act.ByregotsBlessing]  = "Byregot's Blessing",
        [Act.MuscleMemory]      = "Muscle Memory",
        [Act.Groundwork]        = "Groundwork",
        [Act.AdvancedTouch]     = "Advanced Touch",
        [Act.PreparatoryTouch]  = "Preparatory Touch",
        [Act.PrudentTouch]      = "Prudent Touch",
        [Act.TrainedFinesse]    = "Trained Finesse",
        [Act.Reflect]           = "Reflect",
        [Act.DelicateSynthesis] = "Delicate Synthesis",
    };

    private static Dictionary<uint, uint> BuildIdCache(int jobId)
    {
        var nameToBase = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BaseIdNames) nameToBase[kv.Value] = kv.Key;

        var cache = new Dictionary<uint, uint>();
        var sheet = Service.DataManager.GetExcelSheet<CraftAction>();
        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (!nameToBase.TryGetValue(name, out var baseId)) continue;
            var resolvedId = jobId switch
            {
                8  => row.CRP.RowId, 9  => row.BSM.RowId, 10 => row.ARM.RowId,
                11 => row.GSM.RowId, 12 => row.LTW.RowId, 13 => row.WVR.RowId,
                14 => row.ALC.RowId, 15 => row.CUL.RowId, _  => 0u,
            };
            cache[baseId] = resolvedId; // 0 = skill not in sheet for this job
        }
        Service.Log.Information($"[GilMaster] idCache built for job={jobId}: " +
            string.Join(" | ", cache.Select(kv => $"{kv.Key}→{kv.Value}")));
        return cache;
    }

    public void Dispose() { Service.Framework.Update -= Tick; }
}
