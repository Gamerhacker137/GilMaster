using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using static ECommons.GenericHelpers; // TryGetAddonByName

namespace GilMaster.Core;

/// <summary>
/// Self-repair automation so a long crafting batch can't run equipped gear to 0% and start
/// failing crafts. Reads equipped durability, repairs with dark matter via the game's Repair
/// window (General Action 6) — clicking "Repair All" + confirming through ECommons' AddonMaster,
/// the same way Artisan's RepairManager does. Read-only checks (min %, broken, can-self-repair)
/// also gate crafting so we never start a craft on broken gear we can't fix.
/// </summary>
public static unsafe class RepairManager
{
    public enum RepairStatus { Done, Working, CannotRepair }

    private const uint RepairGeneralAction = 6; // General Action: Repair
    private static DateTime _nextStep;

    // Lowest durability % across equipped gear (Condition is 0..30000; /300 = 0..100%).
    public static int MinEquippedPercent()
    {
        try
        {
            var eq = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            if (eq == null) return 100;
            ushort min = ushort.MaxValue;
            for (var i = 0; i < eq->Size; i++)
            {
                var it = eq->GetInventorySlot(i);
                if (it != null && it->ItemId > 0 && it->Condition < min) min = it->Condition;
            }
            return min == ushort.MaxValue ? 100 : min / 300;
        }
        catch { return 100; }
    }

    public static bool IsAnyGearBroken() => MinEquippedPercent() <= 0;

    /// <summary>True only if EVERY degraded equipped item can be self-repaired (dark matter + level).</summary>
    public static bool CanSelfRepairAll()
    {
        try
        {
            var eq = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            if (eq == null) return false;
            var anyDegraded = false;
            for (var i = 0; i < eq->Size; i++)
            {
                var it = eq->GetInventorySlot(i);
                if (it == null || it->ItemId == 0 || it->Condition >= 30000) continue;
                anyDegraded = true;
                if (!CanRepairItem(it->ItemId)) return false;
            }
            return anyDegraded; // nothing degraded → nothing to repair → not "can repair"
        }
        catch { return false; }
    }

    private static bool CanRepairItem(uint itemId)
    {
        var sheet = Service.DataManager.GetExcelSheet<Item>();
        if (sheet.GetRowOrDefault(itemId) is not { } item) return false;
        if (item.ClassJobRepair.RowId == 0) return false;
        if (item.ItemRepair.ValueNullable is not { } repair) return false;

        if (!HasDarkMatterOrBetter(repair.Item.RowId)) return false;
        // Need the repair job at the gear's level - 10 (the game's self-repair rule).
        var jobLevel = CraftQueue.GetCrafterLevel((int)item.ClassJobRepair.RowId);
        return Math.Max(item.LevelEquip - 10, 1) <= jobLevel;
    }

    private static bool HasDarkMatterOrBetter(uint darkMatterId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return false;
        foreach (var dm in Service.DataManager.GetExcelSheet<ItemRepairResource>())
        {
            if (dm.Item.RowId < darkMatterId) continue;          // a higher grade also works
            if (inv->GetInventoryItemCount(dm.Item.RowId) > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Drive the self-repair window. Call each tick while in a repair phase: opens the Repair
    /// window, clicks Repair All, confirms the dialog, throttled. Returns Done once durability is
    /// back above <paramref name="targetPercent"/>, or CannotRepair when self-repair isn't possible.
    /// </summary>
    public static RepairStatus ProcessRepair(int targetPercent)
    {
        if (MinEquippedPercent() >= targetPercent) { CloseRepairWindow(); return RepairStatus.Done; }
        if (!CanSelfRepairAll()) return RepairStatus.CannotRepair;
        if (DateTime.Now < _nextStep) return RepairStatus.Working;
        _nextStep = DateTime.Now.AddMilliseconds(700);

        try
        {
            // Confirm the "repair all?" dialog if it's up.
            if (TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var yn) && yn->AtkUnitBase.IsVisible)
            {
                new AddonMaster.SelectYesno((nint)yn).Yes();
                return RepairStatus.Working;
            }
            // Repair window open → click Repair All (when enabled).
            if (TryGetAddonByName<AddonRepair>("Repair", out var rep) && rep->AtkUnitBase.IsVisible)
            {
                if (rep->RepairAllButton != null && rep->RepairAllButton->IsEnabled)
                    new AddonMaster.Repair((nint)rep).RepairAll();
                return RepairStatus.Working;
            }
            // Otherwise open the repair window via the general action.
            var am = ActionManager.Instance();
            if (am != null) am->UseAction(ActionType.GeneralAction, RepairGeneralAction);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Auto-repair step failed"); }
        return RepairStatus.Working;
    }

    private static void CloseRepairWindow()
    {
        try
        {
            if (TryGetAddonByName<AddonRepair>("Repair", out var rep) && rep->AtkUnitBase.IsVisible)
                rep->AtkUnitBase.Close(true);
        }
        catch { /* harmless — next phase replaces it */ }
    }
}
