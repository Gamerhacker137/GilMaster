using Dalamud.Plugin.Services;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GilMaster.Models;
using System;
using System.Linq;

namespace GilMaster.Core;

/// <summary>
/// Orchestrates the steps needed to begin crafting a specific item:
///  1. Equip the correct gearset (if needed)
///  2. Open the Crafting Log to the correct recipe
///  3. Click the Synthesis button
/// Then CraftExecutor takes over from step 1 of the synth.
/// </summary>
public sealed class CraftStarter : IDisposable
{
    public enum Phase { Idle, WaitingForJobSwitch, OpeningLog, WaitingForLog, ClickingSynthesis, Done, Error }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string StatusText { get; private set; } = string.Empty;
    public event Action? OnChanged;

    private ProfitableItem? targetItem;
    private DateTime phaseStarted;
    private const double PhaseTimeout = 8.0; // seconds before giving up
    internal static bool synthNodesDumped;
    private DateTime lastButtonDump = DateTime.MinValue;

    public CraftStarter() { Service.Framework.Update += Tick; }

    public void Begin(ProfitableItem item)
    {
        targetItem = item;
        CurrentPhase = Phase.WaitingForJobSwitch;
        phaseStarted = DateTime.Now;
        StatusText = "Checking job...";
        OnChanged?.Invoke();
    }

    public void Cancel()
    {
        CurrentPhase = Phase.Idle;
        StatusText = string.Empty;
        targetItem = null;
        OnChanged?.Invoke();
    }

    public bool IsActive => CurrentPhase is not (Phase.Idle or Phase.Done or Phase.Error);

    private unsafe void Tick(IFramework fw)
    {
        // Always dump RecipeNote buttons when it's open, every 5s, for diagnostics
        if ((DateTime.Now - lastButtonDump).TotalSeconds >= 5.0)
        {
            var notePtr2 = Service.GameGui.GetAddonByName("RecipeNote").Address;
            if (notePtr2 != nint.Zero)
            {
                lastButtonDump = DateTime.Now;
                synthNodesDumped = false; // force re-dump via TryClickSynthesis path OR inline below
                var ab = (AtkUnitBase*)notePtr2;
                if (ab->IsVisible)
                {
                    var sb2 = new System.Text.StringBuilder("[GilMaster] RecipeNote buttons (periodic):");
                    for (var n = 0; n < ab->UldManager.NodeListCount; n++)
                    {
                        var node = ab->UldManager.NodeList[n];
                        if (node == null || !node->IsVisible()) continue;
                        var btn = node->GetAsAtkComponentButton();
                        if (btn == null) continue;
                        var label2 = btn->ButtonTextNode != null
                            ? btn->ButtonTextNode->NodeText.ToString()
                            : "";
                        sb2.Append($" [{node->NodeId}:{(btn->IsEnabled ? "ON" : "off")}:\"{label2}\"]");
                    }
                    Service.Log.Debug(sb2.ToString());
                }
            }
        }

        if (CurrentPhase is Phase.Idle or Phase.Done or Phase.Error) return;
        if (targetItem is null) { Fail("No target item"); return; }

        // Timeout guard
        if ((DateTime.Now - phaseStarted).TotalSeconds > PhaseTimeout)
        {
            Fail($"Timed out in phase {CurrentPhase}");
            return;
        }

        var requiredJobId = (uint)(targetItem.CraftJobId + 8); // CRP=8, BSM=9, … CUL=15

        switch (CurrentPhase)
        {
            case Phase.WaitingForJobSwitch:
                var currentJob = Service.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
                if (currentJob == requiredJobId)
                {
                    // Already on the right job — skip straight to opening the log
                    NextPhase(Phase.OpeningLog, "Opening crafting log...");
                }
                else
                {
                    var gearsetId = FindGearsetForJob(requiredJobId);
                    if (gearsetId < 0)
                    {
                        Fail($"No gearset found for {targetItem.CraftJobName}. Equip a {targetItem.CraftJobName} gearset first.");
                        return;
                    }
                    StatusText = $"Equipping {targetItem.CraftJobName} gearset {gearsetId + 1}...";
                    RaptureGearsetModule.Instance()->EquipGearset(gearsetId);
                    NextPhase(Phase.OpeningLog, "Waiting for job switch...", delay: 1.5);
                }
                break;

            case Phase.OpeningLog:
                // Confirm the job switch completed before opening the log
                var nowJob = Service.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
                if (nowJob != requiredJobId)
                {
                    StatusText = $"Waiting for {targetItem.CraftJobName} switch...";
                    return; // keep waiting (PhaseTimeout guards against a stuck switch)
                }

                // Open the crafting log to the correct recipe via the agent
                OpenRecipeInLog(targetItem.RecipeId);
                NextPhase(Phase.WaitingForLog, "Opening log to recipe...");
                break;

            case Phase.WaitingForLog:
                // Wait for the RecipeNote addon to be visible before trying to click
                var notePtr = Service.GameGui.GetAddonByName("RecipeNote").Address;
                if (notePtr == nint.Zero) return; // not open yet
                NextPhase(Phase.ClickingSynthesis, "Starting synthesis...", delay: 0.5);
                break;

            case Phase.ClickingSynthesis:
                // Best-effort: fire the Synthesize button. If it doesn't take, the
                // user presses Synthesize themselves and CraftExecutor still takes over.
                TryClickSynthesis();
                Done();
                break;
        }
    }

    // ── Recipe/log navigation ────────────────────────────────────────────────

    private static unsafe void OpenRecipeInLog(uint recipeId)
    {
        var agent = AgentRecipeNote.Instance();
        if (agent != null)
        {
            agent->OpenRecipeByRecipeId((ushort)recipeId);
            return;
        }
        // Fallback: just open the log and let the user navigate
        SendCommand("/craftinglog");
    }

    internal static unsafe bool TryClickSynthesis()
    {
        var notePtr = Service.GameGui.GetAddonByName("RecipeNote").Address;
        if (notePtr == nint.Zero)
        {
            Service.Log.Debug("[GilMaster] TryClickSynthesis: RecipeNote not found");
            return false;
        }

        var atkBase = (AtkUnitBase*)notePtr;
        if (!atkBase->IsVisible) return false;

        try
        {
            // Fill HQ materials, then press Synthesize — both via ECommons' proven
            // RecipeNote addon master (the foundation Artisan uses). Far more reliable
            // than scraping nodes ourselves.
            // Apply food/potion first; if one was just used, wait for the buff before synth.
            if (!EnsureConsumables()) return false;

            var master = new AddonMaster.RecipeNote(notePtr);
            if (Plugin.Config.UseHqMaterials) SelectHqMaterials(master);
            master.Synthesize();
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Debug(ex, "[GilMaster] Synthesis click attempt failed");
        }
        return false;
    }

    // Fill in HQ materials before synthesizing, via ECommons' RecipeNote master. We don't
    // have the per-ingredient counts exposed, so we set each of the (max 6) ingredient
    // slots to HQ — the game ignores slots that don't exist or have no HQ available.
    private static void SelectHqMaterials(AddonMaster.RecipeNote master)
    {
        try
        {
            for (uint i = 0; i < 6; i++)
                master.Material(i, true);
        }
        catch (Exception ex) { Service.Log.Debug(ex, "[GilMaster] SelectHqMaterials failed"); }
    }

    // ── Food / potion (absorbed from Artisan's PreCrafting) ─────────────────
    // Well Fed = status 48, Medicated = status 49. Returns true when both configured
    // consumables are active (or none configured); false when one was just used (wait).
    private static DateTime _lastConsume = DateTime.MinValue;

    internal static bool EnsureConsumables()
    {
        var cfg = Plugin.Config;
        var foodOk = UseConsumableIfNeeded(cfg.FoodId, cfg.FoodHq, 48);
        var potOk  = UseConsumableIfNeeded(cfg.PotionId, cfg.PotionHq, 49);
        return foodOk && potOk;
    }

    private static unsafe bool UseConsumableIfNeeded(int itemId, bool hq, uint buffId)
    {
        if (itemId <= 0) return true; // not configured

        var player = Service.Objects.LocalPlayer;
        if (player != null && player.StatusList.Any(s => s.StatusId == buffId)) return true; // active

        if ((DateTime.Now - _lastConsume).TotalSeconds < 3.0) return false; // throttle item use

        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null || inv->GetInventoryItemCount((uint)itemId, hq) <= 0) return true; // none to use

            _lastConsume = DateTime.Now;
            var agent = AgentInventoryContext.Instance();
            if (agent != null)
            {
                agent->UseItem((uint)itemId + (hq ? 1_000_000u : 0));
                Service.Log.Information($"[GilMaster] Using {(hq ? "HQ " : "")}consumable {itemId}");
            }
        }
        catch (Exception ex) { Service.Log.Debug(ex, "[GilMaster] consumable use failed"); }
        return false; // used (or tried) — wait for the buff
    }

    // ── Gearset helpers ──────────────────────────────────────────────────────

    private static unsafe int FindGearsetForJob(uint classJobId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return -1;

        for (var i = 0; i < 100; i++)
        {
            var gs = module->GetGearset(i);
            if (gs == null) continue;
            if (!gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (gs->ClassJob == classJobId) return i;
        }
        return -1;
    }

    // ── Chat command ─────────────────────────────────────────────────────────

    private static unsafe void SendCommand(string cmd)
    {
        var str = Utf8String.FromString(cmd);
        try { UIModule.Instance()->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }

    // ── Phase helpers ────────────────────────────────────────────────────────

    private void NextPhase(Phase next, string status, double delay = 0)
    {
        // If a delay is needed, stay in current phase until it elapses
        if (delay > 0 && (DateTime.Now - phaseStarted).TotalSeconds < delay) return;
        CurrentPhase = next;
        phaseStarted = DateTime.Now;
        StatusText = status;
        OnChanged?.Invoke();
    }

    private void Done()
    {
        CurrentPhase = Phase.Done;
        StatusText = "Recipe ready — press Synthesize if it didn't auto-start. Auto-craft will take over.";
        Service.ToastGui.ShowNormal(StatusText);
        OnChanged?.Invoke();
    }

    private void Fail(string reason)
    {
        CurrentPhase = Phase.Error;
        StatusText = $"Error: {reason}";
        Service.ToastGui.ShowError(StatusText);
        Service.Log.Warning($"[GilMaster] CraftStarter failed: {reason}");
        OnChanged?.Invoke();
    }

    public void Dispose() { Service.Framework.Update -= Tick; }
}
