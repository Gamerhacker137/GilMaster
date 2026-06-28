using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace GilMaster.Core;

/// <summary>
/// Automatic materia extractor — pulls materia from every 100%-spiritbonded equipped
/// piece, hands-free. Ported from Artisan's proven Spiritbond routine + ECommons' button
/// click: open the Materialize window via the game's "Materia Extraction" general action
/// (id 14), select the first ready piece (the window's own callback), confirm the dialog
/// with a real button event, and repeat until nothing's spiritbonded or your bags are full.
/// Runs as a framework-tick state machine with timing gates and a safety timeout.
/// </summary>
public sealed unsafe class MateriaExtractor : IDisposable
{
    // Equipped slots that can hold materia (index 5 = obsolete belt slot, skipped).
    private static readonly int[] Slots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    private const uint   MateriaExtractionAction = 14;  // General Action: Materia Extraction
    private const double StepSeconds  = 0.5;            // gap between UI steps
    private const double SafetyTimeout = 120.0;         // hard stop

    private bool _active;
    private DateTime _nextStep;
    private DateTime _startedAt;

    public bool   IsActive => _active;
    public string Status   { get; private set; } = string.Empty;

    /// <summary>How many equipped pieces are at 100% spiritbond right now.</summary>
    public int ReadyCount()
    {
        try
        {
            var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            if (c == null) return 0;
            var n = 0;
            foreach (var s in Slots)
                if (c->Items[s].SpiritbondOrCollectability == 10000) n++;
            return n;
        }
        catch { return 0; }
    }

    public bool AnyReady => ReadyCount() > 0;

    public bool WindowOpen
    {
        get { try { return Service.GameGui.GetAddonByName("Materialize", 1).Address != nint.Zero; } catch { return false; } }
    }

    private static bool DialogOpen
    {
        get { try { return Service.GameGui.GetAddonByName("MaterializeDialog", 1).Address != nint.Zero; } catch { return false; } }
    }

    private int FreeBagSlots()
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null) return 0;
            var free = 0;
            for (var bag = InventoryType.Inventory1; bag <= InventoryType.Inventory4; bag++)
            {
                var c = inv->GetInventoryContainer(bag);
                if (c == null) continue;
                for (var i = 0; i < c->Size; i++)
                    if (c->Items[i].ItemId == 0) free++;
            }
            return free;
        }
        catch { return 0; }
    }

    /// <summary>Open (or close) the game's Materia Extraction window — the menu's own action.</summary>
    public void ToggleWindow()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am != null) am->UseAction(ActionType.GeneralAction, MateriaExtractionAction);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Materia extraction action failed"); }
    }

    public void Start()
    {
        if (_active) return;
        if (!AnyReady) { Status = "No gear at 100% spiritbond."; return; }
        _active    = true;
        _startedAt = DateTime.Now;
        _nextStep  = DateTime.MinValue;
        Status     = "Extracting…";
        Service.Framework.Update += Tick;
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        Service.Framework.Update -= Tick;
        if (WindowOpen) ToggleWindow(); // close the window we opened
    }

    private void Tick(IFramework _)
    {
        try
        {
            if (DateTime.Now < _nextStep) return;

            if ((DateTime.Now - _startedAt).TotalSeconds > SafetyTimeout)
            {
                Status = "Timed out — stopped for safety.";
                Stop();
                return;
            }
            if (!AnyReady)
            {
                Status = "Done — no spiritbonded gear left.";
                Stop();
                return;
            }
            if (FreeBagSlots() == 0)
            {
                Status = "Stopped — inventory full.";
                Stop();
                return;
            }

            if (DialogOpen)        { ConfirmDialog(); Gate(); return; }
            if (!WindowOpen)       { ToggleWindow();  Gate(); return; }
            SelectFirstSpiritbond();
            Gate();
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "MateriaExtractor tick failed");
            Status = "Error — stopped.";
            Stop();
        }
    }

    private void Gate() => _nextStep = DateTime.Now.AddSeconds(StepSeconds);

    // Select the first spiritbonded item in the Materialize list — Artisan's proven callback.
    private static void SelectFirstSpiritbond()
    {
        var ptr = Service.GameGui.GetAddonByName("Materialize", 1).Address;
        if (ptr == nint.Zero) return;
        var window = (AtkUnitBase*)ptr;

        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int,  Int  = 2 };
        values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = 0 };
        window->FireCallback(1, values);
    }

    // Press "Yes" on the confirmation dialog via a real button event (ported from ECommons).
    private static void ConfirmDialog()
    {
        var ptr = Service.GameGui.GetAddonByName("MaterializeDialog", 1).Address;
        if (ptr == nint.Zero) return;
        var dialog = (AddonMaterializeDialog*)ptr;
        ClickButton(&dialog->AtkUnitBase, dialog->YesButton);
    }

    private static void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null) return;
        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null) return;
        var evt = owner->AtkResNode.AtkEventManager.Event;
        if (evt == null) return;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
    }

    public void Dispose() => Stop();
}
