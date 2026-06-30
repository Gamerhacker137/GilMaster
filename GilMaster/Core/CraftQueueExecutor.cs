using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Drives a <see cref="CraftQueue"/> entry-by-entry:
///   1. Switch to the required crafting job (if not already on it)
///   2. Open the recipe in the crafting log
///   3. Hand off to <see cref="CraftExecutor"/> for the actual synthesis loop
///   4. Advance to the next entry when the batch completes
/// </summary>
public sealed class CraftQueueExecutor : IDisposable
{
    public enum State { Idle, Repairing, SwitchingJob, OpeningRecipe, Running, Done, Error }

    public State  CurrentState { get; private set; } = State.Idle;
    public string StatusText   { get; private set; } = "Idle";
    public int    CurrentIndex { get; private set; }

    public event Action? OnChanged;

    private List<CraftQueueEntry>? _queue;
    private DateTime _phaseStarted;
    private const double PhaseTimeout = 12.0;

    // Running uses a no-progress watchdog instead of the flat phase timeout — a real
    // synthesis (and especially a batch of them) legitimately takes minutes. The watchdog
    // resets every time the craft executor advances, so it only fires on a genuine stall.
    private DateTime _runningWatchdog;
    private string   _lastExecutorStatus = "";
    private const double RunningStallTimeout = 30.0;

    // Error-flood breaker: a burst of game error toasts (an action repeatedly rejected — out of
    // CP, durability, bad state) aborts the batch fast instead of waiting out the 30s watchdog.
    private readonly Queue<DateTime> _recentErrors = new();
    private const double ErrorFloodWindow = 10.0;
    private const int    ErrorFloodCount  = 5;

    // Which entry we've already run the repair gate for (so it fires at most once per entry).
    private int _repairedForIndex = -1;

    public CraftQueueExecutor()
    {
        Service.Framework.Update += Tick;
        Service.ToastGui.ErrorToast += OnErrorToast;
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        // Only count errors while we're actively driving a craft batch.
        if (CurrentState is State.Running or State.OpeningRecipe or State.SwitchingJob)
            _recentErrors.Enqueue(DateTime.Now);
    }

    public void Start(List<CraftQueueEntry> entries)
    {
        _queue       = entries;
        CurrentIndex = 0;
        _recentErrors.Clear();
        _repairedForIndex = -1;
        BeginEntry(0);
    }

    public void Stop()
    {
        Plugin.CraftExecutor.Stop();
        CurrentState = State.Idle;
        StatusText   = "Stopped.";
        _queue       = null;
        OnChanged?.Invoke();
    }

    private unsafe void Tick(IFramework fw)
    {
        if (CurrentState is State.Idle or State.Done or State.Error) return;
        if (_queue == null) return;

        // Flat timeout only for the quick setup phases. Running is bounded by its own
        // no-progress watchdog below, since synthesis legitimately takes minutes.
        if (CurrentState is State.SwitchingJob or State.OpeningRecipe
            && (DateTime.Now - _phaseStarted).TotalSeconds > PhaseTimeout)
        {
            Fail($"Timed out in {CurrentState}");
            return;
        }

        switch (CurrentState)
        {
            case State.Repairing:
            {
                var rs = RepairManager.ProcessRepair(Plugin.Config.RepairPercent);
                if (rs is RepairManager.RepairStatus.Done or RepairManager.RepairStatus.CannotRepair)
                {
                    _repairedForIndex = CurrentIndex; // gate done — don't loop it
                    BeginEntry(CurrentIndex);         // proceed to the actual craft setup
                }
                else if ((DateTime.Now - _phaseStarted).TotalSeconds > 30.0)
                {
                    _repairedForIndex = CurrentIndex;
                    Service.Log.Warning("[GilMaster] Auto-repair timed out — continuing.");
                    BeginEntry(CurrentIndex);
                }
                break;
            }

            case State.SwitchingJob:
            {
                var needed  = (uint)_queue[CurrentIndex].JobId;
                var current = Service.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
                if (current == needed)
                {
                    OpenRecipeAndArm();
                }
                break;
            }

            case State.OpeningRecipe:
            {
                // Give the recipe log a moment to open, then arm CraftExecutor
                if ((DateTime.Now - _phaseStarted).TotalSeconds >= 1.5)
                    ArmExecutor();
                break;
            }

            case State.Running:
            {
                // Error-flood breaker: prune errors outside the window, abort on a burst.
                while (_recentErrors.Count > 0 && (DateTime.Now - _recentErrors.Peek()).TotalSeconds > ErrorFloodWindow)
                    _recentErrors.Dequeue();
                if (_recentErrors.Count >= ErrorFloodCount)
                {
                    _recentErrors.Clear();
                    Fail("Too many game errors in a few seconds — aborting (check CP, gear durability, or the rotation).");
                    break;
                }

                var ex     = Plugin.CraftExecutor.CurrentState;
                var status = Plugin.CraftExecutor.StatusText;

                if (ex == CraftExecutor.State.Failed)
                {
                    Fail($"Craft failed — '{_queue[CurrentIndex].Name}' ran out of durability before finishing. " +
                         "FFXIV consumes the materials on a failed synthesis, so the batch is stopped rather than " +
                         "advancing over lost mats. Check gear durability / rotation and retry.");
                    break;
                }

                if (ex == CraftExecutor.State.Done)
                {
                    _queue[CurrentIndex].QuantityCrafted = _queue[CurrentIndex].QuantityToCraft;
                    var next = CurrentIndex + 1;
                    if (next >= _queue.Count)
                    {
                        CurrentState = State.Done;
                        StatusText   = "All done!";
                        Service.ToastGui.ShowNormal("[GilMaster] Crafting queue complete!");
                        RemoveGcSourceList();
                        OnChanged?.Invoke();
                    }
                    else
                    {
                        BeginEntry(next);
                    }
                    break;
                }

                // Any change in the executor's status = progress; reset the watchdog and
                // surface the live step in our own status text.
                if (status != _lastExecutorStatus)
                {
                    _lastExecutorStatus = status;
                    _runningWatchdog    = DateTime.Now;
                    StatusText = $"[{CurrentIndex + 1}/{_queue.Count}] {status}";
                    OnChanged?.Invoke();
                }

                if (ex == CraftExecutor.State.Idle && (DateTime.Now - _runningWatchdog).TotalSeconds > 8.0)
                    Fail("CraftExecutor stopped unexpectedly — did synthesis close?");
                else if ((DateTime.Now - _runningWatchdog).TotalSeconds > RunningStallTimeout)
                    Fail($"No crafting progress for {RunningStallTimeout:0}s — is the synthesis window open?");
                break;
            }
        }
    }

    private unsafe void BeginEntry(int index)
    {
        if (_queue == null || index >= _queue.Count) { FinishQueue(); return; }

        CurrentIndex  = index;
        _phaseStarted = DateTime.Now;
        var entry = _queue[index];

        StatusText = $"[{index + 1}/{_queue.Count}] {entry.Name} ({entry.JobName})";

        // Repair gate (once per entry): top up durability before crafting so a long batch can't
        // run gear to 0% and start failing crafts.
        if (Plugin.Config.AutoRepair && _repairedForIndex != index
            && RepairManager.MinEquippedPercent() < Plugin.Config.RepairPercent
            && RepairManager.CanSelfRepairAll())
        {
            SetState(State.Repairing, $"Repairing gear ({RepairManager.MinEquippedPercent()}%)...");
            return;
        }
        // Refuse to craft on broken gear we can't self-repair — it would just fail and burn mats.
        if (RepairManager.IsAnyGearBroken() && !RepairManager.CanSelfRepairAll())
        {
            Fail("Equipped gear is broken (0%) and can't be auto-repaired (no dark matter or too low level). Repair at a mender, then retry.");
            return;
        }
        _repairedForIndex = index;

        // Prerequisite check: make sure this entry's ingredients are actually in our bags
        // before we open the recipe and arm the synth — otherwise it would just stall.
        var shortIng = Plugin.CraftQueue.FirstShortIngredient(entry.ItemId, entry.QuantityToCraft);
        if (shortIng is { } s)
        {
            Fail($"Missing {s.Need - s.Have}× {s.Name} for {entry.Name} (have {s.Have}/{s.Need}). " +
                 "Craft the sub-components or gather/buy the materials first.");
            return;
        }

        var currentJob = Service.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJob == (uint)entry.JobId)
        {
            OpenRecipeAndArm();
        }
        else
        {
            var (ok, err) = SwitchJob(entry.JobId);
            if (!ok) { Fail($"Couldn't switch to {entry.JobName}: {err}."); return; }
            SetState(State.SwitchingJob, $"Switching to {entry.JobName}...");
        }
        OnChanged?.Invoke();
    }

    private unsafe void OpenRecipeAndArm()
    {
        var entry = _queue![CurrentIndex];
        var agent = AgentRecipeNote.Instance();
        if (agent != null)
            agent->OpenRecipeByRecipeId((ushort)entry.RecipeId);
        SetState(State.OpeningRecipe, $"Opening recipe for {entry.Name}...");
    }

    private void ArmExecutor()
    {
        var entry = _queue![CurrentIndex];
        Plugin.CraftExecutor.Start(entry.QuantityToCraft, entry.RecipeId);
        _lastExecutorStatus = "";
        _runningWatchdog    = DateTime.Now;
        SetState(State.Running, $"Crafting {entry.Name} ({entry.QuantityToCraft}×)...");
    }

    private void FinishQueue()
    {
        CurrentState = State.Done;
        StatusText   = "All done!";
        Service.ToastGui.ShowNormal("[GilMaster] Crafting queue complete!");
        RemoveGcSourceList();
        OnChanged?.Invoke();
    }

    // A GC mission list removes itself once it's been crafted from the queue.
    private static void RemoveGcSourceList()
    {
        var src = Plugin.CraftQueue.SourceList;
        if (src is not { IsGcMission: true }) return;
        Plugin.Config.CraftLists.Remove(src);
        Plugin.Config.Save();
        Plugin.CraftQueue.SourceList = null;
        Service.ToastGui.ShowNormal($"GC mission crafted — removed the '{src.Name}' list.");
    }

    // Equip the gearset for a crafter job. Returns a failure reason (instead of silently stalling
    // to the phase timeout) when there's no gearset, the gearset's main tool is missing/broken, or
    // EquipGearset itself fails.
    private static unsafe (bool Ok, string? Error) SwitchJob(int jobId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return (false, "gearset module unavailable");
        for (var i = 0; i < 100; i++)
        {
            var gs = module->GetGearset(i);
            if (gs == null) continue;
            if (!gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (gs->ClassJob != (uint)jobId) continue;

            if (gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                return (false, $"gearset {i + 1} has no main tool (broken or unequipped) — repair/fix it");
            var rc = module->EquipGearset(i);
            return rc < 0 ? (false, $"EquipGearset failed (code {rc}) for gearset {i + 1}") : (true, null);
        }
        return (false, $"no gearset found for this job — create one in the Gear Set list");
    }

    private void SetState(State s, string text)
    {
        CurrentState  = s;
        _phaseStarted = DateTime.Now;
        StatusText    = text;
        OnChanged?.Invoke();
    }

    private void Fail(string reason)
    {
        CurrentState = State.Error;
        StatusText   = $"Error: {reason}";
        Service.Log.Warning($"[GilMaster] CraftQueueExecutor: {reason}");
        Service.ToastGui.ShowError($"[GilMaster] Queue error: {reason}");
        OnChanged?.Invoke();
    }

    public void Dispose()
    {
        Service.Framework.Update -= Tick;
        Service.ToastGui.ErrorToast -= OnErrorToast;
    }
}
