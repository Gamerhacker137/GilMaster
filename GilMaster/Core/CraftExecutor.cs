using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Craftimizer.Simulator;
using Craftimizer.Simulator.Actions;
using GilMaster.Core.Craft;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CSActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using CAction = Craftimizer.Simulator.Actions.ActionType;
using CCondition = Craftimizer.Simulator.Condition;

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
    // How long to hold the first action waiting for the solver, so the plan starts
    // from step 1 instead of desyncing behind a greedy opener.
    private const double PlanGrace = 12.0;
    private bool wasInCraft;
    private uint lastLoggedStep = uint.MaxValue;
    private uint lastNotUsableStep = uint.MaxValue;
    private bool loggedUnreadable;
    private bool synthActive;
    private DateTime waitingSince = DateTime.MinValue;

    private Dictionary<uint, uint>? idCache;
    private int cachedJobId = -1;

    // Rotation plan produced by Craftimizer's solver before each craft
    private uint currentRecipeId;
    private CAction[]? plannedRotation;
    private int planStep;
    // Background solver task — launched on step 1, polled each tick until complete
    private Task<List<CAction>>? _planTask;
    private DateTime _planStartedAt;
    // Cached solver input for the current craft, so adaptive re-solves reuse it.
    private SimulationInput? _input;
    // True when the in-flight solve started from the LIVE mid-craft state (adopt at step 0).
    private bool _pendingIsLive;
    // Combo/usage state, mutated as we execute planned actions, fed back into re-solves.
    private ActionStates _liveActionStates;

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
        _planTask        = null;
        _input           = null;
        _pendingIsLive   = false;
        _liveActionStates = default;
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
        _planTask       = null;
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
                    _planTask       = null;
                    _input          = null;
                    _liveActionStates = default;
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
                    _planTask       = null;
                    _input          = null;
                    _liveActionStates = default;
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

        // Poll the solver background task. We hold the first action until it lands
        // (see PlanGrace below), so a completed plan is normally adopted while still
        // at step 1. If a greedy opener already fired (plan was too slow), the plan's
        // step-1 assumptions no longer match the craft — discard it and stay greedy.
        if (_planTask is { IsCompleted: true })
        {
            var plan    = _planTask.Status == TaskStatus.RanToCompletion ? _planTask.Result : null;
            var wasLive = _pendingIsLive;
            _planTask      = null;
            _pendingIsLive = false;
            // Initial plans assume a fresh craft, so only adopt at step 1. Adaptive
            // re-solves are computed FROM the current state, so adopt them immediately.
            if (plan?.Count > 0 && (wasLive || state.StepCount == 1))
            {
                plannedRotation = [.. plan];
                planStep        = 0;
                Service.Log.Information(
                    $"[GilMaster] {(wasLive ? "Adaptive re-solve" : "Craftimizer plan")} ready " +
                    $"({plannedRotation.Length} steps): " +
                    string.Join(" → ", plannedRotation.Select(s => s.ToString())));
            }
            else if (plan?.Count > 0)
            {
                Service.Log.Warning(
                    $"[GilMaster] Plan arrived after step {state.StepCount} — too late, continuing greedy.");
            }
            else
            {
                Service.Log.Warning("[GilMaster] Solver returned no plan — using greedy fallback.");
            }
            OnChanged?.Invoke();
        }

        // Adaptive re-solve: when the planned rotation is exhausted (or was lost) but the
        // craft isn't finished, solve again FROM THE LIVE STATE instead of greedy-finishing.
        // HQ only — NQ stays on the fast greedy synthesis loop.
        var craftOngoing = state.Progress < state.MaxProgress;
        var planExhausted = plannedRotation != null && planStep >= plannedRotation.Length;
        var planLost      = plannedRotation == null && state.StepCount > 1;
        if (Plugin.Config.PreferHq && _input != null && _planTask == null
            && craftOngoing && (planExhausted || planLost))
        {
            StartLiveResolve(state);
        }

        // Launch the solver on step 1. If we don't have a recipe ID (manual crafting),
        // auto-detect it first. Greedy fallback handles any steps before the plan lands.
        if (state.StepCount == 1 && plannedRotation == null && _planTask == null)
        {
            if (currentRecipeId == 0)
                currentRecipeId = DetectRecipeId(state);
            if (currentRecipeId != 0)
                StartPlanAsync();
        }

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

        // Hold the first action until the solver lands, so the plan starts at step 1.
        // After PlanGrace we give up waiting and let greedy carry the craft.
        if (plannedRotation == null && _planTask is { IsCompleted: false }
            && state.StepCount == 1
            && (DateTime.Now - _planStartedAt).TotalSeconds < PlanGrace)
        {
            StatusText = "Solving optimal rotation...";
            OnChanged?.Invoke();
            return;
        }

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

            // Advance plan step only when we executed a planned action (not a condition
            // override or greedy). Feed the action into the combo/usage state so any
            // adaptive re-solve continues from the correct combo (Basic→Standard→Advanced).
            if (!isOverride && plannedRotation != null && planStep < plannedRotation.Length)
            {
                _liveActionStates.MutateState(plannedRotation[planStep].Base());
                planStep++;
            }
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
            var action = plannedRotation[planStep];
            var def    = ActionTypeToDef(action);
            // Check the action is usable right now (might not be on first frame)
            var rid    = ResolveId(def.BaseId, def.IsCraftAction);
            var at     = def.IsCraftAction ? CSActionType.CraftAction : CSActionType.Action;
            var status = ActionManager.Instance()->GetActionStatus(at, rid);
            if (status == 0 || status == 571 /* GCD */)
            {
                isOverride = false;
                return def;
            }
            // Plan step unusable — skip it
            Service.Log.Warning($"[GilMaster] plan[{planStep}] {action} not usable (status {status}), skipping");
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

    // Read game state on the Framework thread (Lumina sheets + player attributes),
    // then hand the solve off to a background Task. The solver posts its result back
    // via _planTask, polled each tick. StepDelay = 3.5 s and the greedy fallback covers
    // any early steps, so a slightly long solve never stalls the craft.
    private unsafe void StartPlanAsync()
    {
        // NQ uses the fast greedy CarefulSynthesis loop — no need to solve for quality.
        if (!Plugin.Config.PreferHq) return;

        var stats = ReadCrafterStats();
        if (stats == null) return;
        var (craftsmanship, control, cp, level) = stats.Value;

        var input = CraftimizerBridge.BuildInput(craftsmanship, control, cp, level, currentRecipeId);
        if (input == null)
        {
            Service.Log.Warning("[GilMaster] Could not build solver input — greedy fallback.");
            return;
        }

        _input         = input;
        _pendingIsLive = false;
        _planStartedAt = DateTime.Now;
        _planTask = Task.Run(() => CraftimizerBridge.Solve(input));
        Service.Log.Information(
            $"[GilMaster] Craftimizer solve started for recipe {currentRecipeId} " +
            $"(cms={craftsmanship} ctrl={control} cp={cp} lv={level})");
    }

    // Kick a background solve from the LIVE mid-craft state so the next plan reflects the
    // real progress/quality/durability/condition/buffs (adaptive, like Artisan).
    private void StartLiveResolve(SynthState st)
    {
        if (_input is not { } input) return;

        var effects = ReadEffectsStruct();
        var cond    = MapCondition(st.Condition);
        var cp      = (int)(Service.Objects.LocalPlayer?.CurrentCp ?? 0);

        var live = CraftimizerBridge.BuildLiveState(
            input, (int)st.Progress, (int)st.Quality, (int)st.Durability, cp,
            cond, effects, _liveActionStates, (int)st.StepCount);

        _pendingIsLive = true;
        _planTask = Task.Run(() => CraftimizerBridge.SolveFrom(live));
        Service.Log.Information(
            $"[GilMaster] Adaptive re-solve from live state: prog={st.Progress}/{st.MaxProgress} " +
            $"qual={st.Quality}/{st.MaxQuality} dur={st.Durability} cp={cp} cond={st.Condition} " +
            $"iq={effects.InnerQuiet} ven={effects.Veneration} inn={effects.Innovation} " +
            $"gs={effects.GreatStrides} wn={effects.WasteNot}/{effects.WasteNot2} " +
            $"manip={effects.Manipulation} mm={effects.MuscleMemory}");
    }

    // Map GilMaster's synth-condition readout to Craftimizer's Condition enum (same order).
    private static CCondition MapCondition(SynthCondition c) => c switch
    {
        SynthCondition.Good      => CCondition.Good,
        SynthCondition.Excellent => CCondition.Excellent,
        SynthCondition.Poor      => CCondition.Poor,
        SynthCondition.Centered  => CCondition.Centered,
        SynthCondition.Sturdy    => CCondition.Sturdy,
        SynthCondition.Pliant    => CCondition.Pliant,
        SynthCondition.Malleable => CCondition.Malleable,
        SynthCondition.Primed    => CCondition.Primed,
        SynthCondition.GoodOmen  => CCondition.GoodOmen,
        _                        => CCondition.Normal,
    };

    // Read live crafting buffs into Craftimizer's Effects struct. Durations are in steps
    // (the synth buff counter); Inner Quiet is a stack count. Status IDs are the standard
    // crafting-buff ids. Logged each re-solve so the mapping can be verified in-game.
    private static Effects ReadEffectsStruct()
    {
        var fx = new Effects();
        var player = Service.Objects.LocalPlayer;
        if (player == null) return fx;
        foreach (var s in player.StatusList)
        {
            var dur = (byte)Math.Clamp((int)Math.Round(s.RemainingTime), 0, 255);
            switch (s.StatusId)
            {
                case 251:  fx.InnerQuiet     = (byte)Math.Clamp((int)s.Param, 0, 10); break; // stacks
                case 252:  fx.WasteNot       = dur; break;
                case 257:  fx.WasteNot2      = dur; break;
                case 2226: fx.Veneration     = dur; break;
                case 2189: fx.Innovation     = dur; break;
                case 254:  fx.GreatStrides   = dur; break;
                case 2191: fx.MuscleMemory   = dur; break;
                case 1164: fx.Manipulation   = dur; break;
                case 2190: fx.FinalAppraisal = dur; break;
            }
        }
        return fx;
    }

    private static unsafe (int Craftsmanship, int Control, int CP, int Level)? ReadCrafterStats()
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
            return (craftsmanship, control, (int)player.MaxCp, player.Level);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] ReadCrafterStats failed");
            return null;
        }
    }

    // Scan recipes for the current job to find one whose computed max-progress,
    // max-quality, and max-durability match what the synth addon is showing.
    // Called once per craft when currentRecipeId is unknown (manual crafting).
    private static uint DetectRecipeId(SynthState state)
    {
        try
        {
            var player = Service.Objects.LocalPlayer;
            if (player == null) return 0;
            var jobId       = (int)player.ClassJob.RowId;
            var craftTypeId = jobId - 8; // 0=CRP…7=CUL
            if (craftTypeId < 0 || craftTypeId > 7) return 0;

            var sheet = Service.DataManager.GetExcelSheet<Recipe>();
            foreach (var recipe in sheet)
            {
                if ((int)recipe.CraftType.RowId != craftTypeId) continue;
                var lvl     = recipe.RecipeLevelTable.Value;
                var maxProg = (int)(lvl.Difficulty * recipe.DifficultyFactor / 100u);
                var maxQual = (int)(lvl.Quality    * recipe.QualityFactor    / 100u);
                var maxDur  = (int)(lvl.Durability * recipe.DurabilityFactor / 100u);
                if (maxProg == (int)state.MaxProgress &&
                    maxQual == (int)state.MaxQuality  &&
                    maxDur  == (int)state.MaxDurability)
                {
                    Service.Log.Information(
                        $"[GilMaster] Auto-detected recipe {recipe.RowId} " +
                        $"({recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? "?"}) " +
                        $"from synth stats prog={maxProg} qual={maxQual} dur={maxDur}");
                    return recipe.RowId;
                }
            }
            Service.Log.Warning(
                $"[GilMaster] Could not auto-detect recipe (prog={state.MaxProgress} " +
                $"qual={state.MaxQuality} dur={state.MaxDurability} job={jobId}) — greedy fallback.");
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] DetectRecipeId failed");
        }
        return 0;
    }

    // Map a Craftimizer solver ActionType to GilMaster's executable action definition.
    // The base game action id comes straight from Craftimizer's action metadata; it
    // matches the same base ids GilMaster uses, and ResolveId turns craft actions into
    // their per-job game id.
    private static ActionDef ActionTypeToDef(CAction action)
    {
        var (baseId, isCraft, label) = CraftimizerBridge.ToExecution(action);
        return new ActionDef(baseId, isCraft, label);
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
            // Level up detected — log newly available skills, then let the next
            // craft build a fresh plan so any newly learned abilities are included.
            Service.Log.Information(
                $"[GilMaster] Level up: {cachedLevel} → {level}. " +
                "Rotation will be rebuilt from scratch on next synthesis.");
            cachedLevel     = level;
            currentRecipeId = 0; // force re-detect in case recipe stats also changed
            plannedRotation = null;
            planStep        = 0;
        }
        if (idCache.TryGetValue(baseId, out var r) && r != 0) return r;
        // Not in the name-based cache (e.g. a solver action GilMaster has no constant
        // for) — resolve generically via the CraftAction sheet for this job.
        return CraftimizerBridge.ResolveCraftId(baseId, jobId);
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
