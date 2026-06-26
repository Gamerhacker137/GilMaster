using System;
using System.Collections.Generic;

namespace GilMaster.Core.Craft;

/// <summary>
/// Beam-search rotation planner optimised for HQ crafting.
///
/// Quality strategy:
///   - The heuristic heavily weights quality already banked PLUS IQ-potential
///     (estimated quality from Byregot's Blessing at current IQ stacks).
///   - This guides the search toward Reflect/MuscleMemory openers → Innovation +
///     BasicTouch→StandardTouch→AdvancedTouch combo chains → GreatStrides +
///     Byregot's finisher, before locking in progress at the end.
///
/// CP-combo awareness:
///   - CraftSim tracks LastWasBasicTouch / LastWasStandardTouch / LastWasObserve
///     so StandardTouch after BasicTouch costs 18 instead of 32 CP (saving 14),
///     and AdvancedTouch after StandardTouch costs 18 instead of 46 CP (saving 28).
///   - States that use combos leave more CP and score higher on the resource term,
///     naturally steering the beam toward combo chains.
///
/// NQ mode: returns null immediately — caller uses a fast CarefulSynthesis loop.
/// </summary>
public static class RotationPlanner
{
    private const int BeamWidth = 20;
    private const int MaxSteps  = 60;

    public static List<CraftSkill>? Plan(
        in CrafterStats stats,
        in RecipeStats  recipe,
        IReadOnlyCollection<CraftSkill> learned,
        bool preferHq = true)
    {
        if (!preferHq) return null;

        var learnedSet = new HashSet<CraftSkill>(learned);
        var initState  = SimState.FromStats(in stats, in recipe);

        var beam = new List<(SimState state, CraftSkill[] path)> { (initState, []) };

        List<CraftSkill>? bestPath  = null;
        float             bestScore = -1f;

        for (var step = 0; step < MaxSteps; step++)
        {
            var candidates = new List<(SimState state, CraftSkill[] path, float score)>();

            foreach (var (state, path) in beam)
            {
                foreach (var skill in CraftSim.AllSkills)
                {
                    if (!learnedSet.Contains(skill))        continue;
                    if (!CraftSim.IsValid(skill, in state)) continue;

                    var next     = CraftSim.Apply(skill, in state, in stats, in recipe);
                    var nextPath = Append(path, skill);

                    if (next.IsCompleted)
                    {
                        var score = (float)next.Quality / next.MaxQuality;
                        if (score > bestScore) { bestScore = score; bestPath = new List<CraftSkill>(nextPath); }
                    }
                    else if (!next.IsFailed)
                    {
                        candidates.Add((next, nextPath, Heuristic(in next, in stats, in recipe)));
                    }
                }
            }

            if (candidates.Count == 0) break;

            candidates.Sort(static (a, b) => b.score.CompareTo(a.score));
            if (candidates.Count > BeamWidth)
                candidates.RemoveRange(BeamWidth, candidates.Count - BeamWidth);

            beam = candidates.ConvertAll(static c => (c.state, c.path));
        }

        return bestPath;
    }

    // ── Heuristic ────────────────────────────────────────────────────────────
    //
    // Quality-first: 55% quality banked + 25% IQ potential + 12% progress + 8% resources.
    //
    // IQ potential: estimates the Byregot's Blessing quality gain at current IQ stacks —
    // this rewards accumulating IQ even before cashing it in, guiding the beam toward
    // touch-combo chains and preventing premature Byregot's use.
    private static float Heuristic(in SimState s, in CrafterStats stats, in RecipeStats recipe)
    {
        if (s.MaxQuality == 0 || s.MaxProgress == 0) return 0f;

        float qualFrac = (float)s.Quality  / s.MaxQuality;
        float progFrac = (float)s.Progress / s.MaxProgress;

        // Estimated quality gain if Byregot's used right now (capped so it doesn't exceed 1.0 total)
        float iqPotential = 0f;
        if (s.InnerQuiet > 0)
        {
            var bbEff    = 100 + 20 * s.InnerQuiet;               // 120% … 300%
            var bbGain   = CraftSim.QualityGain(in stats, in recipe, bbEff, in s);
            var maxGain  = s.MaxQuality - s.Quality;
            var actual   = Math.Min(bbGain, maxGain);
            iqPotential  = s.MaxQuality > 0 ? (float)actual / s.MaxQuality : 0f;
        }

        // Combo-readiness micro-bonus: being set up for a cheap AT saves 28 CP
        float comboBonus = s.LastWasBasicTouch ? 0.02f : s.LastWasStandardTouch ? 0.03f : 0f;

        float cpFrac  = s.MaxCP        > 0 ? (float)s.CP        / s.MaxCP        : 0f;
        float durFrac = s.MaxDurability > 0 ? (float)s.Durability / s.MaxDurability : 0f;

        return qualFrac     * 0.55f
             + iqPotential  * 0.25f
             + progFrac     * 0.12f
             + comboBonus
             + cpFrac       * 0.05f
             + durFrac      * 0.03f;
    }

    private static CraftSkill[] Append(CraftSkill[] path, CraftSkill skill)
    {
        var result = new CraftSkill[path.Length + 1];
        path.CopyTo(result, 0);
        result[path.Length] = skill;
        return result;
    }
}
