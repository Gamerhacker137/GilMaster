using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace GilMaster.Core;

/// <summary>
/// Materia helper — detects fully-spiritbonded (100%) equipped gear and opens the
/// game's own Materia Extraction window with one click (the same general action the
/// in-game menu fires, id 14). Reading spiritbond and firing the action are both safe,
/// supported operations.
///
/// NOTE: fully hands-free extraction (auto-clicking the in-game list + confirm dialog)
/// is intentionally NOT done here — that requires firing native UI callbacks that can
/// crash the client if wrong, and it can't be verified outside the game. Once verified
/// in-game it can be layered on top of this.
/// </summary>
public sealed unsafe class MateriaExtractor
{
    // Equipped slots that can hold materia (index 5 = obsolete belt slot, skipped).
    private static readonly int[] Slots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    private const uint MateriaExtractionAction = 14; // General Action: Materia Extraction

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
        get
        {
            try { return Service.GameGui.GetAddonByName("Materialize", 1).Address != nint.Zero; }
            catch { return false; }
        }
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
}
