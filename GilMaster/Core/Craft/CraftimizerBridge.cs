using Craftimizer.Simulator;
using Craftimizer.Simulator.Actions;
using Craftimizer.Solver;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CSolver = Craftimizer.Solver.Solver;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace GilMaster.Core.Craft;

/// <summary>
/// Bridge between GilMaster's live game state and Craftimizer's real crafting
/// simulator + solver. Builds a <see cref="SimulationInput"/> from the recipe sheet
/// and the player's stats, runs the solver on a background thread, and returns a
/// concrete action list that GilMaster's executor can fire via ActionManager.
///
/// Replaces the old hand-rolled CraftSim + beam/MCTS planner: Craftimizer's simulator
/// models every action, condition, combo, and buff correctly, and its solver
/// (StepwiseGenetic by default) is far stronger than anything we'd maintain ourselves.
/// </summary>
public static class CraftimizerBridge
{
    /// <summary>
    /// Build a Craftimizer <see cref="SimulationInput"/> from a recipe row and the
    /// player's live crafting stats. Returns null if the recipe can't be read.
    /// Must be called on the framework thread (reads Lumina sheets).
    /// </summary>
    public static SimulationInput? BuildInput(
        int craftsmanship, int control, int cp, int level,
        uint recipeId, int startingQuality = 0, bool isSpecialist = false)
    {
        var sheet = Service.DataManager.GetExcelSheet<LuminaRecipe>();
        var opt   = sheet.GetRowOrDefault(recipeId);
        if (opt is not { } recipe) return null;

        var table = recipe.RecipeLevelTable.Value;

        // Mirrors Craftimizer's RecipeData.cs derivation (ignoring WKS-adjustable
        // level and collectables — handled by the solver's defaults for normal crafts).
        var recipeInfo = new RecipeInfo
        {
            IsExpert        = recipe.IsExpert,
            ClassJobLevel   = table.ClassJobLevel,
            ConditionsFlag  = table.ConditionsFlag,
            MaxDurability   = table.Durability * recipe.DurabilityFactor / 100,
            MaxQuality      = (recipe.CanHq || recipe.RequiredQuality > 0)
                                ? (int)table.Quality * recipe.QualityFactor / 100 : 0,
            MaxProgress     = table.Difficulty * recipe.DifficultyFactor / 100,
            QualityModifier  = table.QualityModifier,
            QualityDivider   = table.QualityDivider,
            ProgressModifier = table.ProgressModifier,
            ProgressDivider  = table.ProgressDivider,
        };

        var stats = new CharacterStats
        {
            Craftsmanship      = craftsmanship,
            Control            = control,
            CP                 = cp,
            Level              = level,
            CanUseManipulation = level >= 65,   // Manipulation unlocks at 65
            HasSplendorousBuff = false,         // (Splendorous tool detection not wired yet)
            IsSpecialist       = isSpecialist,  // enables Heart and Soul / Careful Observation / Quick Innovation
        };

        return new SimulationInput(stats, recipeInfo, startingQuality);
    }

    /// <summary>
    /// The OPENING plan for a LIVE craft: a strong Raphael A* solve (the same brain Artisan runs
    /// live), escalating from a fast pass to Raphael until full HQ. Runs on a background thread
    /// while the greedy opener covers the first steps; the executor caches the result keyed by
    /// recipe+stats so the next craft of this recipe starts instantly. Returns the combo-expanded
    /// rotation, or empty on failure.
    /// </summary>
    public static List<ActionType> SolveOpening(SimulationInput input, CancellationToken token = default)
    {
        var best = SolveBest(input, tryHard: true, token);
        return best is { } s ? s.Actions.ToList() : new List<ActionType>();
    }

    /// <summary>
    /// Run the solver to completion on the calling thread (intended to be a background
    /// Task). Returns the full, combo-expanded rotation, or an empty list on failure.
    /// </summary>
    public static List<ActionType> Solve(SimulationInput input, CancellationToken token = default)
        => SolveFrom(new SimulationState(input), token);

    /// <summary>
    /// Run the solver from an arbitrary <see cref="SimulationState"/> — including a live
    /// mid-craft state — and return the combo-expanded best continuation. This is what
    /// lets the executor re-solve adaptively from the real progress/quality/durability/
    /// condition/buffs instead of following a rotation planned blindly at step 1.
    /// </summary>
    public static List<ActionType> SolveFrom(SimulationState state, CancellationToken token = default)
        => SolveFromConfig(state, SolverConfig.RecipeNoteDefault with
        {
            MaxThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        }, token);

    // Time-bounded config for per-step live re-solves: it must land fast (well under the
    // gap between craft actions) so the executor always acts on a FRESH decision instead of
    // a stale plan. The full config is for the opening plan only.
    private static readonly SolverConfig LiveConfig = SolverConfig.RecipeNoteDefault with
    {
        MaxThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        MaxTimeMs      = 600,
    };

    /// <summary>Fast, time-bounded re-solve from the live mid-craft state (per-step decisions).</summary>
    public static List<ActionType> SolveFromLive(SimulationState state, CancellationToken token = default)
        => SolveFromConfig(state, LiveConfig, token);

    private static List<ActionType> SolveFromConfig(SimulationState state, SolverConfig config, CancellationToken token)
    {
        var solver = new CSolver(config, state) { Token = token };
        solver.Start();
        var solution = solver.GetSafeTask().GetAwaiter().GetResult();
        // GetTask returns the SanitizedSolution, so Actions is already combo-expanded.
        return solution is { } s ? s.Actions.ToList() : new List<ActionType>();
    }

    /// <summary>
    /// Assemble a live <see cref="SimulationState"/> from the values GilMaster reads off
    /// the synthesis window + the player's status list, so the solver can continue from
    /// exactly where the craft is now.
    /// </summary>
    public static SimulationState BuildLiveState(
        SimulationInput input, int progress, int quality, int durability, int cp,
        Craftimizer.Simulator.Condition condition, Effects effects, ActionStates actionStates, int stepCount)
    {
        var s = new SimulationState(input)
        {
            Progress      = progress,
            Quality       = quality,
            Durability    = durability,
            CP            = cp,
            Condition     = condition,
            ActiveEffects = effects,
            ActionStates  = actionStates,
            StepCount     = stepCount,
        };
        return s;
    }

    // A fast solver config for bulk simulation/benchmarking — fewer iterations + a hard
    // time cap so we can score thousands of recipes in reasonable time.
    private static readonly SolverConfig FastConfig = SolverConfig.RecipeNoteDefault with
    {
        Iterations     = 20_000,
        MaxThreadCount = 1,        // many small solves in parallel across recipes instead
        MaxTimeMs      = 1_500,
    };

    /// <summary>
    /// Solve a recipe and return the solver's final simulated <see cref="SimulationState"/>
    /// (progress / quality / HQ%), for offline benchmarking. Uses the fast config.
    /// </summary>
    public static SimulationState? SolveStateFast(SimulationInput input, CancellationToken token = default)
        => SolveWith(input, FastConfig, token)?.State;

    /// <summary>
    /// Try to reach HQ, escalating effort and trying DIFFERENT search strategies until it
    /// succeeds (or runs out of ideas): a fast pass first, then a heavier genetic search,
    /// then the Raphael solver — keeping whichever attempt got the most quality. Returns the
    /// best solver solution (its Actions are the rotation; State has progress/quality/HQ%).
    /// </summary>
    public static SolverSolution? SolveBest(SimulationInput input, bool tryHard, CancellationToken token = default)
    {
        var best = SolveWith(input, FastConfig, token);
        if (!tryHard || IsFullHq(best)) return best;

        best = Best(best, SolveWith(input, StrongConfig, token));   // heavier genetic search
        if (IsFullHq(best)) return best;

        best = Best(best, SolveWith(input, RaphaelConfig, token));  // a different algorithm
        return best;
    }

    private static readonly SolverConfig StrongConfig = SolverConfig.RecipeNoteDefault with
    {
        MaxThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        MaxTimeMs      = 6_000,
    };

    private static readonly SolverConfig RaphaelConfig = SolverConfig.EditorDefault with
    {
        MaxThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        MaxTimeMs      = 6_000,
    };

    private static SolverSolution? SolveWith(SimulationInput input, SolverConfig config, CancellationToken token)
    {
        var solver = new CSolver(config, new SimulationState(input)) { Token = token };
        solver.Start();
        return solver.GetSafeTask().GetAwaiter().GetResult();
    }

    private static bool IsFullHq(SolverSolution? s)
        => s is { } sol && sol.State.Input.Recipe.MaxQuality > 0
        && sol.State.Progress >= sol.State.Input.Recipe.MaxProgress
        && sol.State.Quality  >= sol.State.Input.Recipe.MaxQuality;

    // Prefer a completed craft, then higher quality.
    private static SolverSolution? Best(SolverSolution? a, SolverSolution? b)
    {
        if (a is not { } x) return b;
        if (b is not { } y) return a;
        var xc = x.State.Progress >= x.State.Input.Recipe.MaxProgress;
        var yc = y.State.Progress >= y.State.Input.Recipe.MaxProgress;
        if (xc != yc) return xc ? a : b;
        return y.State.Quality > x.State.Quality ? b : a;
    }

    // ── Action execution mapping ───────────────────────────────────────────────

    /// <summary>
    /// The base game action id for an action type, and whether it's a CraftAction
    /// (per-job, id >= 100000) vs a regular Action. GilMaster's executor resolves the
    /// per-job id from this and fires it via ActionManager.
    /// </summary>
    public static (uint BaseId, bool IsCraft, string Label) ToExecution(ActionType action)
    {
        var baseId = action.Base().ActionId;
        return (baseId, baseId >= 100000, action.ToString());
    }

    private static readonly Dictionary<(uint, int), uint> _craftIdCache = new();

    /// <summary>
    /// Resolve a base CraftAction id (e.g. 100001) to its per-job game action id via the
    /// CraftAction sheet. Covers every craft action the solver can emit, not just a
    /// hardcoded subset. Returns the base id unchanged if it isn't a CraftAction row.
    /// </summary>
    public static uint ResolveCraftId(uint baseId, int jobId)
    {
        var key = (baseId, jobId);
        if (_craftIdCache.TryGetValue(key, out var cached)) return cached;

        var resolved = baseId;
        var opt = Service.DataManager.GetExcelSheet<CraftAction>().GetRowOrDefault(baseId);
        if (opt is { } row)
        {
            var perJob = jobId switch
            {
                8  => row.CRP.RowId, 9  => row.BSM.RowId, 10 => row.ARM.RowId,
                11 => row.GSM.RowId, 12 => row.LTW.RowId, 13 => row.WVR.RowId,
                14 => row.ALC.RowId, 15 => row.CUL.RowId, _  => 0u,
            };
            if (perJob != 0) resolved = perJob;
        }

        _craftIdCache[key] = resolved;
        return resolved;
    }
}
