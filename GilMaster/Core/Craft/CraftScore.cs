using GilMaster.Models;
using System;

namespace GilMaster.Core.Craft;

/// <summary>
/// Scores a recorded craft for the head-to-head scoreboard: HQ wins big, a failed craft (materials
/// wasted) is a penalty, partial quality scores proportionally (quality% = HQ chance). Ties break on
/// efficiency (fewer steps / less CP). This is what makes GilMaster-vs-Artisan a real contest — and
/// since both run the same solver, any GilMaster loss is an execution or solver-input bug.
/// </summary>
public static class CraftScore
{
    public readonly record struct Result(int Points, string Grade, bool Hq, bool Failed);

    public static Result Score(CraftTrace t)
    {
        if (!t.Completed)                     return new Result(-50, "FAIL", false, true);      // mats wasted
        // A recipe that can't be HQ'd is a success at NQ — 0 quality is the intended outcome, not a
        // fail. (This is why "Roof Tile" finishing at 0% shouldn't be penalised.)
        if (t.MaxQuality == 0 || !t.CanHq)    return new Result( 50, "NQ (no HQ)", false, false);
        var qpct = Math.Clamp((float)t.FinalQuality / t.MaxQuality, 0f, 1f);
        var q    = (int)MathF.Round(qpct * 100);
        var hq   = t.ReachedMaxQuality || qpct >= 1f;
        return new Result(hq ? 100 : q, hq ? "HQ" : $"{q}%", hq, false);
    }

    /// <summary>Compare two runs of the same recipe: &gt;0 = a beats b, &lt;0 = b beats a, 0 = tie.
    /// Higher points win; equal points break on fewer steps, then less CP spent.</summary>
    public static int Compare(CraftTrace a, CraftTrace b)
    {
        var sa = Score(a); var sb = Score(b);
        if (sa.Points != sb.Points) return sa.Points - sb.Points;
        if (a.Steps.Count != b.Steps.Count) return b.Steps.Count - a.Steps.Count; // fewer steps wins
        return 0;
    }
}
