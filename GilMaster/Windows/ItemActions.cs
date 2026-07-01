using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Misc; // ItemFinderModule
using GilMaster.Windows.Tabs;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Windows;

/// <summary>
/// One reusable right-click menu + the actions behind it (queue, list, in-game market board,
/// chat link, Universalis / Garland), so every result tab offers the same conveniences instead of
/// each hand-rolling its own. Draw the menu with <see cref="ContextMenu"/> (wraps the popup) or
/// <see cref="DrawMenu"/> (body only, inside your own BeginPopup/EndPopup).
/// </summary>
public static class ItemActions
{
    /// <summary>Full popup: call once per row, right after the row's widgets.</summary>
    public static void ContextMenu(string popupId, uint itemId, string name, bool craftable, System.Action? extra = null)
    {
        if (!ImGui.BeginPopupContextItem(popupId)) return;
        DrawMenu(itemId, name, craftable, extra);
        ImGui.EndPopup();
    }

    /// <summary>Menu body only. <paramref name="extra"/> injects tab-specific items at the top.</summary>
    public static void DrawMenu(uint itemId, string name, bool craftable, System.Action? extra = null)
    {
        ImGui.TextDisabled(name);
        ImGui.Separator();

        extra?.Invoke();

        if (craftable && ImGui.BeginMenu("Add to queue"))
        {
            if (ImGui.MenuItem("1"))  QueueAppend(itemId, 1);
            if (ImGui.MenuItem("5"))  QueueAppend(itemId, 5);
            if (ImGui.MenuItem("10")) QueueAppend(itemId, 10);
            var cq = Math.Max(1, Plugin.Config.CraftQuantity);
            if (cq is not (1 or 5 or 10) && ImGui.MenuItem($"{cq} (craft qty)")) QueueAppend(itemId, cq);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Add to list"))
        {
            var lists = Plugin.Config.CraftLists;
            for (var i = 0; i < lists.Count; i++)
                if (ImGui.MenuItem($"{lists[i].Name}##al{i}")) ListsTab.AddItemToList(i, itemId, name);
            if (lists.Count > 0) ImGui.Separator();
            if (ImGui.MenuItem("+ New list"))
            {
                lists.Add(new CraftList { Name = $"List {lists.Count + 1}" });
                ListsTab.AddItemToList(lists.Count - 1, itemId, name);
            }
            ImGui.EndMenu();
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Search on market board")) SearchMarketBoard(itemId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open this item in the in-game Market Board (stand at a board or retainer bell).\nNot near one? Opens Universalis instead.");
        if (ImGui.MenuItem("Link in chat")) LinkInChat(itemId, name);
        if (ImGui.MenuItem("Copy name"))    ImGui.SetClipboardText(name);

        ImGui.Separator();
        if (ImGui.MenuItem("Open on Universalis"))   OpenUniversalis(itemId);
        if (ImGui.MenuItem("Open on Garland Tools")) OpenGarland(itemId);
    }

    /// <summary>Append (or add to) the live craft queue without disturbing what's already in it.</summary>
    public static void QueueAppend(uint itemId, int qty)
    {
        if (itemId == 0 || qty <= 0) return;
        var q = Plugin.CraftQueue;
        var targets = q.Targets.ToList();
        var idx = targets.FindIndex(t => t.ItemId == itemId);
        if (idx >= 0) targets[idx] = (itemId, Math.Min(9999, targets[idx].Quantity + qty));
        else          targets.Add((itemId, qty));
        q.BuildMulti(targets);
        Service.ToastGui.ShowNormal($"Added {qty}× {ItemName(itemId)} to the craft queue.");
    }

    /// <summary>Open the item in the in-game Market Board search (Artisan uses the same call).</summary>
    public static unsafe void SearchMarketBoard(uint itemId)
    {
        if (itemId == 0) return;
        try { ItemFinderModule.Instance()->SearchForItem(itemId); }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[GilMaster] SearchForItem failed — opening Universalis instead");
            OpenUniversalis(itemId);
        }
    }

    public static void LinkInChat(uint itemId, string name)
    {
        try
        {
            Service.ChatGui.Print(new SeString(
                new ItemPayload(itemId, false),
                new TextPayload($"{(char)SeIconChar.LinkMarker}{name}"),
                RawPayload.LinkTerminator));
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] link item in chat failed"); }
    }

    public static void OpenUniversalis(uint itemId) => Util.OpenLink($"https://universalis.app/market/{itemId}");
    public static void OpenGarland(uint itemId)     => Util.OpenLink($"https://garlandtools.org/db/#item/{itemId}");

    // Items that have a crafting recipe — so "Add to queue" is only offered where it makes sense.
    private static HashSet<uint>? _craftable;
    public static bool HasRecipe(uint itemId)
    {
        _craftable ??= BuildCraftableSet();
        return _craftable.Contains(itemId);
    }

    private static HashSet<uint> BuildCraftableSet()
    {
        var set = new HashSet<uint>();
        foreach (var r in Service.DataManager.GetExcelSheet<Recipe>())
            if (r.ItemResult.RowId != 0) set.Add(r.ItemResult.RowId);
        return set;
    }

    private static string ItemName(uint itemId) =>
        Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item#{itemId}";
}
