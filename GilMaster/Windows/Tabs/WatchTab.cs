using Dalamud.Bindings.ImGui;
using GilMaster.Core.Craft;
using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Crafter watcher — shows every recorded synthesis (yours or Artisan's) step by step so you can
/// see how a craft actually ran, and diff it against what our Raphael solver would do. The point
/// is hardening the executor: a buff that took seconds to fire shows up as a big Gap, and a
/// side-by-side with our solve shows where our rotation diverges.
/// </summary>
public sealed class WatchTab
{
    private int selected;

    // "Compare vs our Raphael solve" state — solved off-thread, cached for the selected trace.
    private uint compareRecipe;
    private Task<List<string>>? compareTask;
    private List<string>? compareResult;

    public void Draw()
    {
        var watcher = Plugin.CraftWatcher;

        ImGui.TextWrapped("Records every synthesis — yours or Artisan's — action by action, so you can compare how a "
                        + "craft ran against what our solver would do. Big Gap(ms) on a step = the action was slow to fire.");

        // Live recording indicator.
        if (watcher.IsRecording)
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f),
                $"● Recording: {watcher.LiveRecipe} — {watcher.LiveSteps} step(s)");
        }
        else
        {
            ImGui.TextDisabled("Idle — start a craft (GilMaster or Artisan) to capture it.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear all")) { watcher.Clear(); selected = 0; compareResult = null; compareTask = null; }

        ImGui.Separator();

        if (ImGui.BeginTabBar("##watchmode"))
        {
            if (ImGui.BeginTabItem("Traces"))     { DrawTraces();     ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Scoreboard")) { DrawScoreboard(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawTraces()
    {
        var traces = Plugin.CraftWatcher.Traces;
        if (traces.Count == 0)
        {
            ImGui.TextDisabled("No recorded crafts yet.");
            return;
        }

        // ── Trace picker ─────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(-1);
        var labels = traces.Select(TraceLabel).ToArray();
        if (selected >= labels.Length) selected = 0;
        if (ImGui.BeginCombo("##tracepick", labels[selected]))
        {
            for (var i = 0; i < labels.Length; i++)
                if (ImGui.Selectable(labels[i], i == selected)) { selected = i; compareResult = null; compareTask = null; }
            ImGui.EndCombo();
        }

        var t = traces[selected];

        // ── Header ───────────────────────────────────────────────────────────
        var srcColor = t.Source == "GilMaster" ? new Vector4(0.5f, 0.8f, 1f, 1f) : new Vector4(1f, 0.8f, 0.4f, 1f);
        ImGui.TextColored(srcColor, t.Source);
        ImGui.SameLine();
        ImGui.TextUnformatted($"· {t.RecipeName} ({t.JobName})  ·  {t.Steps.Count} steps  ·  {t.DurationMs / 1000f:F0}s");
        ImGui.TextDisabled($"Stats: cms {t.Craftsmanship} · ctrl {t.Control} · cp {t.Cp} · lv {t.Level}   |   "
                         + $"Prog {t.FinalProgress}/{t.MaxProgress} · Qual {t.FinalQuality}/{t.MaxQuality} ({t.QualityPct * 100:F0}%)");

        var (resTxt, resCol) = t.Completed
            ? (t.ReachedMaxQuality ? ("Completed · max quality", new Vector4(0.3f, 1f, 0.4f, 1f))
                                   : ("Completed", new Vector4(0.7f, 0.9f, 0.7f, 1f)))
            : ("Did not complete", new Vector4(1f, 0.5f, 0.5f, 1f));
        ImGui.TextColored(resCol, resTxt);

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy trace")) ImGui.SetClipboardText(BuildTraceText(t));

        ImGui.SameLine();
        var canCompare = t.RecipeId != 0 && t.Craftsmanship > 0 && Plugin.Config.PreferHq;
        if (!canCompare) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Compare vs our Raphael solve")) StartCompare(t);
        if (!canCompare) ImGui.EndDisabled();
        if (ImGui.IsItemHovered() && !canCompare)
            ImGui.SetTooltip(t.RecipeId == 0 ? "Recipe couldn't be identified for this trace."
                : !Plugin.Config.PreferHq ? "Turn on Prefer HQ to solve a quality rotation." : "");

        PollCompare();

        ImGui.Separator();

        // ── Step table ───────────────────────────────────────────────────────
        DrawSteps(t);

        // ── Comparison panel ─────────────────────────────────────────────────
        if (compareRecipe == t.RecipeId && (compareTask != null || compareResult != null))
            DrawCompare(t);
    }

    // ── Scoreboard: GilMaster vs Artisan, recipe by recipe ───────────────────
    private void DrawScoreboard()
    {
        var traces = Plugin.CraftWatcher.Traces.Where(t => t.RecipeId != 0).ToList();
        if (traces.Count == 0)
        {
            ImGui.TextDisabled("No scored crafts yet — craft with GilMaster and Artisan to compare.");
            return;
        }

        ImGui.TextWrapped("HQ = 100, partial quality = quality%, a failed craft = −50. Both use the same Raphael "
                        + "solver, so any GilMaster loss is an execution or solver-input bug — that's the whole point.");

        // Best run per (recipe, source).
        static CraftTrace? Best(IEnumerable<CraftTrace> xs) =>
            xs.OrderByDescending(x => CraftScore.Score(x).Points).ThenBy(x => x.Steps.Count).FirstOrDefault();

        int gWins = 0, aWins = 0, ties = 0, gPts = 0, aPts = 0;
        var rows = new List<(string Recipe, CraftTrace? G, CraftTrace? A)>();
        foreach (var grp in traces.GroupBy(t => t.RecipeId))
        {
            var g = Best(grp.Where(t => t.Source == "GilMaster"));
            var a = Best(grp.Where(t => t.Source != "GilMaster"));
            rows.Add((grp.First().RecipeName, g, a));
            if (g != null) gPts += CraftScore.Score(g).Points;
            if (a != null) aPts += CraftScore.Score(a).Points;
            if (g != null && a != null)
            {
                var c = CraftScore.Compare(g, a);
                if (c > 0) gWins++; else if (c < 0) aWins++; else ties++;
            }
        }

        // Tally banner.
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), $"GilMaster {gPts}");
        ImGui.SameLine(); ImGui.TextDisabled("vs");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), $"Artisan {aPts}");
        ImGui.SameLine(); ImGui.TextDisabled($"   ·   head-to-head {gWins}–{aWins}" + (ties > 0 ? $" ({ties} tie)" : ""));
        ImGui.Spacing();

        if (!ImGui.BeginTable("##score", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Recipe",    ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("GilMaster", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Artisan",   ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Winner",    ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableHeadersRow();

        foreach (var (recipe, g, a) in rows.OrderBy(r => r.Recipe, StringComparer.Ordinal))
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(recipe);

            ImGui.TableSetColumnIndex(1);
            DrawScoreCell(g);

            ImGui.TableSetColumnIndex(2);
            DrawScoreCell(a);

            ImGui.TableSetColumnIndex(3);
            if (g != null && a != null)
            {
                var c = CraftScore.Compare(g, a);
                if (c > 0) ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "GilMaster");
                else if (c < 0) ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Artisan");
                else ImGui.TextDisabled("tie");
            }
            else if (g == null) ImGui.TextDisabled("(run GilMaster)");
            else ImGui.TextDisabled("(run Artisan)");
        }
        ImGui.EndTable();
    }

    private static void DrawScoreCell(CraftTrace? t)
    {
        if (t == null) { ImGui.TextDisabled("—"); return; }
        var r = CraftScore.Score(t);
        var col = r.Failed ? new Vector4(1f, 0.45f, 0.4f, 1f)
                : r.Hq     ? new Vector4(0.3f, 1f, 0.4f, 1f)
                           : new Vector4(0.85f, 0.85f, 0.6f, 1f);
        ImGui.TextColored(col, $"{r.Points}  {r.Grade}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{t.Steps.Count} steps · {t.DurationMs / 1000f:F0}s · qual {t.FinalQuality}/{t.MaxQuality}");
    }

    private void DrawSteps(CraftTrace t)
    {
        var tableHeight = compareResult != null ? new Vector2(0, 260) : new Vector2(0, -1);
        if (!ImGui.BeginTable("##steps", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, tableHeight))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#",      ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Cond",   ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Prog",   ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Qual",   ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Dur",    ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("CP",     ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Gap ms", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("Buffs",  ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableHeadersRow();

        foreach (var s in t.Steps)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextDisabled(s.Step.ToString());

            ImGui.TableSetColumnIndex(1);
            if (s.Exact) ImGui.TextUnformatted(s.Action);
            else ImGui.TextColored(new Vector4(0.75f, 0.75f, 0.75f, 1f), s.Action); // best-effort label
            if (!s.Exact && ImGui.IsItemHovered())
                ImGui.SetTooltip("Inferred from the CP cost + stat delta (not a buff), so the exact touch/synthesis name may be approximate.");

            ImGui.TableSetColumnIndex(2);
            var special = s.Condition != "Normal";
            if (special) ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), s.Condition);
            else ImGui.TextDisabled(s.Condition);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(s.DProgress > 0 ? $"+{s.DProgress}" : s.Progress.ToString());

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(s.DQuality > 0 ? $"+{s.DQuality}" : s.Quality.ToString());

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(s.Durability.ToString());

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(s.Cp.ToString());

            ImGui.TableSetColumnIndex(7);
            // The stall signal: a step that sat for seconds before its action landed.
            var gapCol = s.GapMs >= 4000 ? new Vector4(1f, 0.4f, 0.4f, 1f)
                       : s.GapMs >= 2500 ? new Vector4(1f, 0.85f, 0.3f, 1f)
                                         : new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(gapCol, s.GapMs.ToString());

            ImGui.TableSetColumnIndex(8);
            ImGui.TextDisabled(s.Buffs);
        }

        ImGui.EndTable();
    }

    private void DrawCompare(CraftTrace t)
    {
        ImGui.Spacing();
        if (compareResult == null)
        {
            ImGui.TextDisabled("Solving our Raphael rotation…");
            return;
        }

        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "Our Raphael solve (optimal for these stats):");
        var actual = t.Steps.Select(s => s.Action).ToList();
        var ours   = compareResult;

        if (ImGui.BeginTable("##cmp", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 230)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#",             ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Actual (run)",  ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("Ours (solved)", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableHeadersRow();

            var n = Math.Max(actual.Count, ours.Count);
            for (var i = 0; i < n; i++)
            {
                ImGui.TableNextRow();
                var a = i < actual.Count ? actual[i] : "";
                var o = i < ours.Count ? ours[i] : "";
                var diverge = !Same(a, o);

                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled((i + 1).ToString());

                ImGui.TableSetColumnIndex(1);
                if (diverge && a.Length > 0) ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f), a);
                else ImGui.TextUnformatted(a);

                ImGui.TableSetColumnIndex(2);
                if (diverge && o.Length > 0) ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f), o);
                else ImGui.TextUnformatted(o);
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Orange = the run diverged from our solve. Touch/synthesis names in the run are inferred, "
                         + "so treat exact-name mismatches loosely; focus on buff order and where extra/missing actions appear.");
    }

    // Loose match — the run's inferred names may differ in exact touch tier, so compare the family.
    private static bool Same(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        return Family(a) == Family(b);
    }

    private static string Family(string s)
    {
        if (s.Contains("Touch") || s == "Reflect" || s.Contains("Byregot") || s.Contains("Trained Finesse")) return "quality";
        if (s.Contains("Synthesis") || s.Contains("Groundwork") || s.Contains("Muscle Memory") || s.Contains("Trained Eye")) return "progress";
        return s; // buffs / mend / observe compare by exact name
    }

    private void StartCompare(CraftTrace t)
    {
        compareRecipe = t.RecipeId;
        compareResult = null;
        // BuildInput reads Lumina sheets — do it here on the framework (draw) thread, then solve off-thread.
        var input = CraftimizerBridge.BuildInput(t.Craftsmanship, t.Control, t.Cp, t.Level, t.RecipeId);
        if (input == null) { compareResult = ["(couldn't build solver input)"]; return; }
        compareTask = Task.Run(() =>
            CraftimizerBridge.SolveOpening(input)
                .Select(a => CraftimizerBridge.ToExecution(a).Label)
                .ToList());
    }

    private void PollCompare()
    {
        if (compareTask is { IsCompleted: true })
        {
            compareResult = compareTask.Status == TaskStatus.RanToCompletion ? compareTask.Result : ["(solve failed)"];
            compareTask = null;
        }
    }

    private static string TraceLabel(CraftTrace t)
    {
        var q = t.MaxQuality > 0 ? $" · {t.QualityPct * 100:F0}%hq" : "";
        var ok = t.Completed ? "" : " · FAILED";
        return $"{t.TimeLabel} · {t.RecipeName} · {t.Source} · {t.Steps.Count} steps{q}{ok}";
    }

    private static string BuildTraceText(CraftTrace t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {t.RecipeName} ({t.JobName}) — {t.Source} @ {t.TimeLabel} ===");
        sb.AppendLine($"Stats: cms {t.Craftsmanship} ctrl {t.Control} cp {t.Cp} lv {t.Level}");
        sb.AppendLine($"Max: prog {t.MaxProgress} qual {t.MaxQuality} dur {t.MaxDurability}");
        sb.AppendLine($"Result: prog {t.FinalProgress}/{t.MaxProgress} qual {t.FinalQuality}/{t.MaxQuality} "
                    + $"({t.QualityPct * 100:F0}%) {(t.Completed ? "completed" : "FAILED")} in {t.DurationMs / 1000f:F0}s");
        sb.AppendLine();
        foreach (var s in t.Steps)
            sb.AppendLine($"{s.Step,3}  {s.Action,-22} {s.Condition,-9} prog {s.Progress,-5} qual {s.Quality,-6} "
                        + $"dur {s.Durability,-3} cp {s.Cp,-3} cost {s.CpCost,-3} gap {s.GapMs,5}ms  {s.Buffs}");
        return sb.ToString();
    }
}
