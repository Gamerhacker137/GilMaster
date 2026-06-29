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
    public enum State { Idle, SwitchingJob, OpeningRecipe, Running, Done, Error }

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

    public CraftQueueExecutor() { Service.Framework.Update += Tick; }

    public void Start(List<CraftQueueEntry> entries)
    {
        _queue       = entries;
        CurrentIndex = 0;
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
                var ex     = Plugin.CraftExecutor.CurrentState;
                var status = Plugin.CraftExecutor.StatusText;

                if (ex == CraftExecutor.State.Done)
                {
                    _queue[CurrentIndex].QuantityCrafted = _queue[CurrentIndex].QuantityToCraft;
                    var next = CurrentIndex + 1;
                    if (next >= _queue.Count)
                    {
                        CurrentState = State.Done;
                        StatusText   = "All done!";
                        Service.ToastGui.ShowNormal("[GilMaster] Crafting queue complete!");
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
            SwitchJob(entry.JobId);
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
        OnChanged?.Invoke();
    }

    private static unsafe void SwitchJob(int jobId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return;
        for (var i = 0; i < 100; i++)
        {
            var gs = module->GetGearset(i);
            if (gs == null) continue;
            if (!gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (gs->ClassJob == (uint)jobId) { module->EquipGearset(i); return; }
        }
        Service.Log.Warning($"[GilMaster] CraftQueueExecutor: no gearset found for job {jobId}");
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

    public void Dispose() { Service.Framework.Update -= Tick; }
}
