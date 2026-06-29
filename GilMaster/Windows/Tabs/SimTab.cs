using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Crafting bot / simulator — runs the solver against recipes across the whole game and
/// scores the outcomes (HQ = points, failures = lost points). Pure offline simulation.
/// </summary>
public sealed class SimTab
{
    private static readonly string[] JobNames = ["All jobs", "Carpenter", "Blacksmith", "Armorer", "Goldsmith", "Leatherworker", "Weaver", "Alchemist", "Culinarian"];
    private int craftsmanship = 3800;
    private int control       = 3600;
    private int cp            = 600;
    private int level         = 100;
    private int maxRecipes    = 250;
    private int jobSel        = 0;     // 0 = all; 1..8 = CRP..CUL
    private bool onlyMyLevel  = true;  // only recipes at/below the level above
    private bool tryHard      = true;  // escalate effort + try other algorithms to reach HQ
    private bool failuresOnly;
    private bool initialised;

    public void Draw()
    {
        var sim = Plugin.CraftSim;

        // First open: attune to the player's current job, level and stats.
        if (!initialised) { initialised = true; ReadMyStats(); }

        ImGui.TextWrapped("Run the crafting solver against recipes and score it: " +
                          "completed crafts earn points (full HQ = +10), failures lose points (-10).");
        ImGui.Separator();

        // ── Stats ─────────────────────────────────────────────────────────
        ImGui.TextDisabled("Crafter stats to simulate with:");
        ImGui.SetNextItemWidth(120); ImGui.InputInt("Craftsmanship##sim", ref craftsmanship, 50, 200);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120); ImGui.InputInt("Control##sim", ref control, 50, 200);
        ImGui.SetNextItemWidth(120); ImGui.InputInt("CP##sim", ref cp, 10, 50);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120); ImGui.InputInt("Level##sim", ref level, 1, 5);
        craftsmanship = Math.Max(1, craftsmanship); control = Math.Max(1, control);
        cp = Math.Max(1, cp); level = Math.Clamp(level, 1, 100);

        ImGui.SameLine();
        if (ImGui.Button("Use my character")) ReadMyStats();

        // ── Scope: job + level ────────────────────────────────────────────
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("Job##sim", JobNames[jobSel]))
        {
            for (var i = 0; i < JobNames.Length; i++)
                if (ImGui.Selectable(JobNames[i], i == jobSel)) jobSel = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Only my level (≤ level above)##sim", ref onlyMyLevel);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only simulate recipes you can actually make at the level set above.\nUncheck to test the whole level range with these stats.");

        ImGui.Checkbox("Keep trying for HQ (escalate + try other solvers)##sim", ref tryHard);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a recipe doesn't reach HQ, retry with a heavier search and a different\nalgorithm to find a way. Slower, but learns better rotations — which the live\ncrafter then reuses. Completed rotations are cached for real crafting.");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Max recipes (0 = all)##sim", ref maxRecipes, 50, 250);
        if (maxRecipes < 0) maxRecipes = 0;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Caps how many recipes to simulate, sampled evenly across the level range.\n0 = every matching recipe (slow if the scope is wide).");

        ImGui.Separator();

        // ── Run controls ──────────────────────────────────────────────────
        if (sim.IsRunning)
        {
            if (ImGui.Button("Cancel##sim")) sim.Cancel();
            ImGui.SameLine();
            ImGui.ProgressBar(sim.Progress, new Vector2(220, 0));
        }
        else
        {
            if (ImGui.Button("Run simulation")) sim.Run(craftsmanship, control, cp, level, maxRecipes, jobSel - 1, onlyMyLevel, tryHard);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(sim.Status);

        ImGui.TextDisabled($"Saved rotations (used by the crafter): {Core.RotationCache.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##rotcache")) Core.RotationCache.Clear();

        // ── Scoreboard ────────────────────────────────────────────────────
        if (sim.Done > 0)
        {
            ImGui.Separator();
            var scoreColor = sim.TotalScore >= 0 ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f);
            ImGui.TextColored(scoreColor, $"SCORE: {sim.TotalScore:N0}");
            ImGui.SameLine(); ImGui.TextDisabled($"   over {sim.Done:N0} crafts");

            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"HQ: {sim.HqCount}");
            ImGui.SameLine(); ImGui.TextColored(new Vector4(0.8f, 0.9f, 1f, 1f), $"  Completed: {sim.CompletedCount}");
            ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"  Failed: {sim.FailedCount}");
            if (sim.Done > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"  ({100f * sim.HqCount / sim.Done:F0}% HQ, avg {(float)sim.TotalScore / sim.Done:F1}/craft)");
            }

            ImGui.SameLine();
            ImGui.Checkbox("Failures only", ref failuresOnly);
        }

        // ── Results table ─────────────────────────────────────────────────
        var rows = failuresOnly ? sim.Results.Where(r => !r.Completed).ToList() : sim.Results.ToList();
        if (rows.Count == 0) return;

        if (ImGui.BeginTable("##simresults", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",    ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Job",     ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("Lvl",     ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("Score",   ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableHeadersRow();

            // Show worst first so problem recipes surface.
            foreach (var r in rows.OrderBy(r => r.Score).Take(500))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(r.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextDisabled(r.JobName);
                ImGui.TableSetColumnIndex(2); ImGui.Text(r.Level.ToString());
                ImGui.TableSetColumnIndex(3);
                var c = !r.Completed ? new Vector4(1f, 0.4f, 0.4f, 1f)
                      : r.Outcome == "HQ" ? new Vector4(0.3f, 1f, 0.4f, 1f)
                      : new Vector4(0.85f, 0.9f, 1f, 1f);
                ImGui.TextColored(c, r.Outcome);
                ImGui.TableSetColumnIndex(4);
                ImGui.TextColored(r.Score >= 0 ? new Vector4(0.6f, 0.9f, 0.6f, 1f) : new Vector4(1f, 0.5f, 0.5f, 1f),
                    r.Score.ToString());
            }
            ImGui.EndTable();
        }
    }

    private unsafe void ReadMyStats()
    {
        try
        {
            var player = Service.Objects.LocalPlayer;
            var ui = UIState.Instance();
            if (player != null && ui != null)
            {
                var cms = (int)ui->PlayerState.Attributes[70]; // Craftsmanship
                var ctl = (int)ui->PlayerState.Attributes[71]; // Control
                if (cms > 0) craftsmanship = cms;
                if (ctl > 0) control = ctl;
                cp = (int)player.MaxCp;
                level = player.Level;

                // If you're on a crafting job, scope the sim to it (ClassJob 8..15 = CRP..CUL).
                var job = (int)player.ClassJob.RowId;
                if (job is >= 8 and <= 15) jobSel = job - 7; // jobSel: 1..8 = CRP..CUL
            }
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Sim: ReadMyStats failed"); }
    }
}
