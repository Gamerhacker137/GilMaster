using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;

namespace GilMaster.Core;

/// <summary>
/// Drives the game's batch Quick Synthesis for NQ crafts — far faster than the per-step solved
/// loop for trivial filler / sub-components. Fires the RecipeNote "Quick Synthesis" callback,
/// confirms the quantity dialog, then watches the SynthesisSimple window's progress (AtkValues
/// 3=current, 4=max, the same indices Artisan reads) and closes it when done. Falls back to the
/// normal crafter when quick synth isn't available for the recipe (e.g. never crafted before).
/// </summary>
public static class QuickSynth
{
    public enum Status { Working, Done, Unavailable }

    private static DateTime _next;
    private static DateTime _phaseStart;
    private static bool _firedOpen;   // fired the RecipeNote quick-synth callback
    private static bool _sawBatch;    // the SynthesisSimple batch window appeared

    public static bool CanQuickSynth(uint recipeId)
        => Service.DataManager.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is { } r && r.CanQuickSynth;

    public static void Reset()
    {
        _firedOpen  = false;
        _sawBatch   = false;
        _next       = DateTime.MinValue;
        _phaseStart = DateTime.Now;
    }

    public static unsafe Status Process(int quantity)
    {
        if (DateTime.Now < _next) return Status.Working;
        _next = DateTime.Now.AddMilliseconds(400);
        try
        {
            // 3) Batch window open → drive it to completion, then close it.
            var simplePtr = Service.GameGui.GetAddonByName("SynthesisSimple", 1).Address;
            if (simplePtr != nint.Zero)
            {
                _sawBatch = true;
                var w = (AtkUnitBase*)simplePtr;
                if (w->AtkValuesCount > 4)
                {
                    var cur = w->AtkValues[3].Int;
                    var max = w->AtkValues[4].Int;
                    if (max > 0 && cur >= max)
                    {
                        var v = stackalloc AtkValue[1];
                        v[0] = new AtkValue { Type = AtkValueType.Int, Int = -1 }; // close
                        w->FireCallback(1, v);
                        Reset();
                        return Status.Done;
                    }
                }
                return Status.Working; // still crafting
            }

            // 2) Quantity dialog open → confirm the count.
            var dlgPtr = Service.GameGui.GetAddonByName("SynthesisSimpleDialog", 1).Address;
            if (dlgPtr != nint.Zero)
            {
                var d = (AtkUnitBase*)dlgPtr;
                var v = stackalloc AtkValue[3];
                v[0] = new AtkValue { Type = AtkValueType.Int,  Int  = Math.Clamp(quantity, 1, 99) };
                v[1] = new AtkValue { Type = AtkValueType.Bool, Byte = 1 };
                v[2] = new AtkValue { Type = AtkValueType.Bool, Byte = 1 };
                d->FireCallback(3, v);
                return Status.Working;
            }

            // 1) RecipeNote open → fire the "Quick Synthesis" callback (9), once.
            var notePtr = Service.GameGui.GetAddonByName("RecipeNote", 1).Address;
            if (notePtr != nint.Zero && !_firedOpen)
            {
                var n = (AtkUnitBase*)notePtr;
                var v = stackalloc AtkValue[1];
                v[0] = new AtkValue { Type = AtkValueType.Int, Int = 9 };
                n->FireCallback(1, v);
                _firedOpen = true;
                return Status.Working;
            }
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Quick synth step failed"); }

        // Fired the open but no dialog/batch ever appeared → not available for this recipe.
        if (_firedOpen && !_sawBatch && (DateTime.Now - _phaseStart).TotalSeconds > 4)
            return Status.Unavailable;

        return Status.Working;
    }
}
