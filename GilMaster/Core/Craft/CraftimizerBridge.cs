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
        uint recipeId, int startingQuality = 0)
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
            HasSplendorousBuff = false,         // conservative defaults; refine later if needed
            IsSpecialist       = false,
        };

        return new SimulationInput(stats, recipeInfo, startingQuality);
    }

    /// <summary>
    /// Run the solver to completion on the calling thread (intended to be a background
    /// Task). Returns the full, combo-expanded rotation, or an empty list on failure.
    /// </summary>
    public static List<ActionType> Solve(SimulationInput input, CancellationToken token = default)
    {
        var state = new SimulationState(input);

        // Craftimizer's macro-generation default (StepwiseGenetic, 100k iters, target HQ),
        // with a capped thread count so the solve doesn't starve the game thread.
        var config = SolverConfig.RecipeNoteDefault with
        {
            MaxThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        };

        var solver = new CSolver(config, state) { Token = token };
        solver.Start();
        var solution = solver.GetSafeTask().GetAwaiter().GetResult();
        // GetTask returns the SanitizedSolution, so Actions is already combo-expanded.
        return solution is { } s ? s.Actions.ToList() : new List<ActionType>();
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
