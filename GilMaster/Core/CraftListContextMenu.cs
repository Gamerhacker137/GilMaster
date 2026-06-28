using Dalamud.Game.Gui.ContextMenu;
using GilMaster.Windows.Tabs;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Adds a "GilMaster Crafting List" entry to the in-game item right-click menu (inventory,
/// market board, recipe log, …). It opens a submenu listing every GilMaster crafting list
/// the item can be added to, plus "New list" to start one — so you can build lists straight
/// from the game, the way Artisan's "Artisan Crafting List" works.
/// </summary>
public sealed class CraftListContextMenu : IDisposable
{
    private const ushort PrefixColor = 706; // matches the gold prefix Artisan/AllaganTools use
    private const char   PrefixChar  = 'G';

    public void Enable()  => Service.ContextMenu.OnMenuOpened += OnMenuOpened;
    public void Dispose() => Service.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        var itemId = ResolveItemId();
        if (itemId == 0) return;

        // Only offer the option for craftable items — it's a crafting list, after all.
        if (Plugin.RecipeResolver.FindRecipeFor(itemId) == null) return;

        var name = GetItemName(itemId);
        var submenu = new MenuItem
        {
            Name        = "GilMaster Crafting List",
            IsSubmenu   = true,
            PrefixChar  = PrefixChar,
            PrefixColor = PrefixColor,
        };
        submenu.OnClicked += clicked => OpenListSubmenu(clicked, itemId, name);
        args.AddMenuItem(submenu);
    }

    private void OpenListSubmenu(IMenuItemClickedArgs args, uint itemId, string name)
    {
        var items = new List<MenuItem>();
        var lists = Plugin.Config.CraftLists;

        for (var i = 0; i < lists.Count; i++)
        {
            var idx = i;
            items.Add(new MenuItem
            {
                Name        = $"Add to: {lists[i].Name}",
                PrefixChar  = PrefixChar,
                PrefixColor = PrefixColor,
                OnClicked   = _ => AddToList(idx, itemId, name),
            });
        }

        items.Add(new MenuItem
        {
            Name        = lists.Count == 0 ? "Create a list & add" : "+ New list & add",
            PrefixChar  = PrefixChar,
            PrefixColor = PrefixColor,
            OnClicked   = _ => NewListAndAdd(itemId, name),
        });

        args.OpenSubmenu(items);
    }

    private static void AddToList(int listIndex, uint itemId, string name)
    {
        ListsTab.AddItemToList(listIndex, itemId, name);
        var listName = listIndex >= 0 && listIndex < Plugin.Config.CraftLists.Count
            ? Plugin.Config.CraftLists[listIndex].Name : "list";
        Service.ToastGui.ShowNormal($"Added {name} to {listName}.");
    }

    private static void NewListAndAdd(uint itemId, string name)
    {
        var lists = Plugin.Config.CraftLists;
        lists.Add(new CraftList { Name = $"List {lists.Count + 1}" });
        ListsTab.AddItemToList(lists.Count - 1, itemId, name);
        Service.ToastGui.ShowNormal($"Created '{lists[^1].Name}' and added {name}.");
    }

    // The item the context menu is for is the one under the cursor; HoveredItem covers
    // inventory, market board, recipe log and chat-link menus reliably.
    private static uint ResolveItemId()
    {
        var hovered = Service.GameGui.HoveredItem;
        if (hovered == 0 || hovered >= 2_000_000) return 0; // 0 / event-item / collectable
        return (uint)(hovered % 500_000);                   // strip the HQ offset
    }

    private static string GetItemName(uint itemId)
    {
        var row = Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return row?.Name.ExtractText() ?? $"Item#{itemId}";
    }
}
