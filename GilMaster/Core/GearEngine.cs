using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

/// <summary>
/// Recommends the best crafting gear ("kit") for a crafter job at a given level — highest
/// Craftsmanship / Control / CP you can equip per slot. Reads item stats exactly like
/// Craftimizer's Gearsets (BaseParam 70=Craftsmanship, 71=Control, 11=CP; HQ adds the Special
/// params), maps the slot from EquipSlotCategory, and which crafter jobs can wear it from
/// ClassJobCategory. Then flags each pick as craftable / vendor-sold so you can make or buy it.
/// </summary>
public sealed class GearEngine
{
    private const int ParamCP = 11, ParamCraftsmanship = 70, ParamControl = 71;

    private sealed record Entry(
        uint ItemId, ushort Icon, string Name, GearSlot Slot, byte DohMask,
        int Cms, int Ctrl, int Cp, int Ilvl, int EquipLevel, bool CanHq);

    private static List<Entry>? _index;

    /// <summary>Best gear per slot for a crafter (craftType 0=CRP..7=CUL) at the given level.</summary>
    public Dictionary<GearSlot, GearPiece> BestKit(int craftType, int level, int priority)
    {
        _index ??= BuildIndex();
        var jobBit = (byte)(1 << craftType);

        var result = new Dictionary<GearSlot, GearPiece>();
        foreach (var grp in _index
                     .Where(e => (e.DohMask & jobBit) != 0 && e.EquipLevel <= level)
                     .GroupBy(e => e.Slot))
        {
            var best = grp.OrderByDescending(e => Score(e, priority)).ThenByDescending(e => e.Ilvl).First();
            result[grp.Key] = Enrich(best);
        }
        return result;
    }

    private static int Score(Entry e, int priority) => priority switch
    {
        1 => e.Cms * 3 + e.Ctrl + e.Cp,        // favour Craftsmanship
        2 => e.Cms + e.Ctrl * 3 + e.Cp,        // favour Control
        3 => e.Cms + e.Ctrl + e.Cp * 10,       // favour CP
        _ => e.Cms + e.Ctrl + e.Cp,            // balanced (≈ highest item level)
    };

    private static GearPiece Enrich(Entry e)
    {
        var recipeId = Plugin.RecipeResolver.FindRecipeFor(e.ItemId);
        var jobName  = "";
        if (recipeId is { } rid)
        {
            var rec = Service.DataManager.GetExcelSheet<Recipe>().GetRowOrDefault(rid);
            if (rec is { } r) jobName = CraftJobNames.Short((int)r.CraftType.RowId);
        }
        return new GearPiece
        {
            ItemId = e.ItemId, Icon = e.Icon, Name = e.Name, Slot = e.Slot,
            Craftsmanship = e.Cms, Control = e.Ctrl, CP = e.Cp,
            ItemLevel = e.Ilvl, EquipLevel = e.EquipLevel, CanHq = e.CanHq,
            Craftable = recipeId.HasValue, RecipeId = recipeId ?? 0, CraftJobName = jobName,
            VendorSold = VendorPrices.IsVendorSold(e.ItemId),
        };
    }

    private static List<Entry> BuildIndex()
    {
        var list = new List<Entry>();
        try
        {
            foreach (var item in Service.DataManager.GetExcelSheet<Item>())
            {
                var (cms, ctrl, cp) = ReadStats(item);
                if (cms + ctrl + cp <= 0) continue;            // not crafting gear

                var slot = MapSlot(item.EquipSlotCategory.RowId);
                if (slot is not { } gs) continue;

                if (item.ClassJobCategory.ValueNullable is not { } cjc) continue;
                var mask = DohMask(cjc);
                if (mask == 0) continue;                        // no crafter can equip it

                var name = item.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;

                list.Add(new Entry(
                    item.RowId, item.Icon, name, gs, mask, cms, ctrl, cp,
                    (int)item.LevelItem.RowId, item.LevelEquip, item.CanBeHq));
            }
            Service.Log.Information($"[GilMaster] Gear index: {list.Count} crafting-gear pieces.");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Gear index failed"); }
        return list;
    }

    private static (int Cms, int Ctrl, int Cp) ReadStats(Item item)
    {
        int cms = 0, ctrl = 0, cp = 0;
        void Add(uint param, int val)
        {
            if (param == ParamCraftsmanship) cms += val;
            else if (param == ParamControl)  ctrl += val;
            else if (param == ParamCP)       cp += val;
        }
        foreach (var s in item.BaseParam.Zip(item.BaseParamValue)) Add(s.First.RowId, s.Second);
        if (item.CanBeHq)
            foreach (var s in item.BaseParamSpecial.Zip(item.BaseParamValueSpecial)) Add(s.First.RowId, s.Second);
        return (cms, ctrl, cp);
    }

    private static GearSlot? MapSlot(uint esc) => esc switch
    {
        1 or 13 or 14 => GearSlot.MainHand,
        2  => GearSlot.OffHand,
        3  => GearSlot.Head,
        4  => GearSlot.Body,
        5  => GearSlot.Hands,
        7  => GearSlot.Legs,
        8  => GearSlot.Feet,
        9  => GearSlot.Ears,
        10 => GearSlot.Neck,
        11 => GearSlot.Wrists,
        12 => GearSlot.Ring,
        _  => null,
    };

    private static byte DohMask(ClassJobCategory c)
    {
        byte m = 0;
        if (c.CRP) m |= 1 << 0;
        if (c.BSM) m |= 1 << 1;
        if (c.ARM) m |= 1 << 2;
        if (c.GSM) m |= 1 << 3;
        if (c.LTW) m |= 1 << 4;
        if (c.WVR) m |= 1 << 5;
        if (c.ALC) m |= 1 << 6;
        if (c.CUL) m |= 1 << 7;
        return m;
    }
}
