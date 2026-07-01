using GilMaster.Core.Craft;
using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilMaster.Core;

/// <summary>
/// Offline crafting bot — runs the Craftimizer solver against craftable recipes across the
/// whole game and scores each outcome: good crafts (completed, high HQ%) earn points, bad
/// ones (failed to complete) lose them. Pure simulation, no game interaction.
///
/// Scoring per recipe:
///   completed + has quality  → +(HQ% / 10)   (0..10; full HQ = +10)
///   completed, no quality     → +5
///   failed (couldn't finish)  → -10
/// </summary>
public sealed class CraftSimRunner : IDisposable
{
    private static readonly string[] JobAbbr = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    private CancellationTokenSource? cts;

    public bool   IsRunning  { get; private set; }
    public string Status     { get; private set; } = "Idle";
    public float  Progress   { get; private set; }

    public int TotalScore { get; private set; }
    public int Done        { get; private set; }   // recipes evaluated
    public int HqCount     { get; private set; }   // reached 100% HQ
    public int CompletedCount { get; private set; }
    public int FailedCount    { get; private set; }

    public IReadOnlyList<SimResult> Results { get; private set; } = [];
    public event System.Action? OnUpdated;

    // jobFilter: craft-type id 0..7 (CRP..CUL), or -1 for all jobs.
    // onlyMyLevel: only include recipes at or below `level`.
    public void Run(int craftsmanship, int control, int cp, int level, int maxRecipes, int jobFilter, bool onlyMyLevel, bool tryHard)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        IsRunning = true;
        Status = "Collecting recipes...";
        Progress = 0f;
        TotalScore = Done = HqCount = CompletedCount = FailedCount = 0;
        Results = [];
        OnUpdated?.Invoke();

        Task.Run(() => Execute(craftsmanship, control, cp, level, maxRecipes, jobFilter, onlyMyLevel, tryHard, ct), ct);
    }

    public void Cancel()
    {
        cts?.Cancel();
        IsRunning = false;
        Status = "Cancelled.";
    }

    private void Execute(int craftsmanship, int control, int cp, int level, int maxRecipes, int jobFilter, bool onlyMyLevel, bool tryHard, CancellationToken ct)
    {
        try
        {
            // Collect craftable recipes (deduped by result item), filtered to the chosen
            // job and (when "only my level") to recipes at or below the crafter level.
            var recipes = new List<(uint RecipeId, uint ItemId, string Name, int Lvl, int Job)>();
            var seen = new HashSet<uint>();
            foreach (var r in Service.DataManager.GetExcelSheet<Recipe>())
            {
                var job = (int)r.CraftType.RowId;
                if (jobFilter >= 0 && job != jobFilter) continue;

                var lvl = (int)r.RecipeLevelTable.Value.ClassJobLevel;
                if (onlyMyLevel && lvl > level + Plugin.Config.CraftLevelBuffer) continue;

                var itemId = r.ItemResult.RowId;
                if (itemId == 0 || !seen.Add(itemId)) continue;
                var name = r.ItemResult.ValueNullable?.Name.ExtractText() ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                recipes.Add((r.RowId, itemId, name, lvl, job));
            }
            recipes.Sort((a, b) => a.Lvl.CompareTo(b.Lvl));
            if (maxRecipes > 0 && recipes.Count > maxRecipes)
            {
                // Sample evenly across the level range so the run spans the whole game.
                var step = (double)recipes.Count / maxRecipes;
                recipes = Enumerable.Range(0, maxRecipes).Select(i => recipes[(int)(i * step)]).ToList();
            }

            var results = new List<SimResult>(recipes.Count);
            for (var i = 0; i < recipes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rec = recipes[i];

                var input = CraftimizerBridge.BuildInput(craftsmanship, control, cp, level, rec.RecipeId);
                int score, hq = 0; bool completed = false; string outcome;
                if (input == null)
                {
                    score = -10; outcome = "ERR";
                }
                else
                {
                    var sol = CraftimizerBridge.SolveBest(input, tryHard, ct);
                    if (sol is { } s)
                    {
                        var st = s.State;
                        var maxProg = st.Input.Recipe.MaxProgress;
                        var maxQual = st.Input.Recipe.MaxQuality;
                        completed = st.Progress >= maxProg && maxProg > 0;
                        hq = maxQual > 0 ? st.HQPercent : 0;
                        if (!completed) { score = -10; outcome = "FAIL"; }
                        else if (maxQual > 0) { score = hq / 10; outcome = hq >= 100 ? "HQ" : $"{hq}%"; }
                        else { score = 5; outcome = "DONE"; }

                        // Feed what we learned back into real crafting: cache the rotation for
                        // any craft we could complete (so the live crafter seeds from it).
                        if (completed) RotationCache.Store(rec.RecipeId, s.Actions, craftsmanship, control, cp, level, startQual: 0);
                    }
                    else { score = -10; outcome = "FAIL"; }
                }

                TotalScore += score;
                Done++;
                if (!completed && outcome is "FAIL" or "ERR") FailedCount++;
                else { CompletedCount++; if (hq >= 100) HqCount++; }

                results.Add(new SimResult
                {
                    ItemId = rec.ItemId, Name = rec.Name,
                    JobName = rec.Job >= 0 && rec.Job < JobAbbr.Length ? JobAbbr[rec.Job] : "?",
                    Level = rec.Lvl, HqPercent = hq, Completed = completed, Score = score, Outcome = outcome,
                });

                if ((i & 7) == 0 || i == recipes.Count - 1)
                {
                    Progress = (float)(i + 1) / recipes.Count;
                    Status = $"Simulating {i + 1}/{recipes.Count} — score {TotalScore:N0}";
                    Results = [.. results];
                    OnUpdated?.Invoke();
                }
            }

            Results = [.. results];
            RotationCache.Save(); // persist what we learned so it survives restarts
            Status = $"Done — {Done} crafts, score {TotalScore:N0} ({HqCount} HQ, {FailedCount} failed); {RotationCache.Count} rotations saved for crafting";
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) { Service.Log.Error(ex, "CraftSim run failed"); Status = $"Error: {ex.Message}"; }
        finally { IsRunning = false; Progress = 1f; OnUpdated?.Invoke(); }
    }

    public void Dispose() { cts?.Cancel(); cts?.Dispose(); }
}
