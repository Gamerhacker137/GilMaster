using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using GilMaster.Core;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Crafting Lists — a named, persisted batch of items to craft in one go (idea
/// borrowed from Artisan). Building a list composes a single combined queue (shared
/// sub-components summed and netted against your inventory) and hands it to the Queue
/// tab, which already drives Artisan or the built-in crafter.
/// </summary>
public sealed class ListsTab
{
    private int selectedListIndex = -1;
    private string addSearch = string.Empty;
    private string prevAddSearch = string.Empty;
    private List<(uint Id, string Name)> addMatches = [];

    public void Draw()
    {
        var config = Plugin.Config;
        var lists  = config.CraftLists;

        // ── List management row ───────────────────────────────────────────
        if (ImGui.Button("+ New list"))
        {
            lists.Add(new CraftList { Name = $"List {lists.Count + 1}" });
            selectedListIndex = lists.Count - 1;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Add GC mission list")) AddGcMissionList();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Build a list from your Grand Company's daily crafting Supply missions.\nOpen the GC Supply window first (Personnel Officer). The list auto-removes itself\nonce you craft it from the Queue.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        var preview = selectedListIndex >= 0 && selectedListIndex < lists.Count
            ? lists[selectedListIndex].Name : "(select a list)";
        if (ImGui.BeginCombo("##listsel", preview))
        {
            for (var i = 0; i < lists.Count; i++)
                if (ImGui.Selectable($"{lists[i].Name} ({lists[i].Items.Count})##l{i}", i == selectedListIndex))
                    selectedListIndex = i;
            ImGui.EndCombo();
        }

        if (selectedListIndex < 0 || selectedListIndex >= lists.Count)
        {
            ImGui.Separator();
            ImGui.TextDisabled(lists.Count == 0
                ? "Create a list, then add the items you want to mass-craft."
                : "Pick a list above, or create a new one.");
            ImGui.TextDisabled("Tip: right-click any item in the Find tab to add it to a list.");
            return;
        }

        var list = lists[selectedListIndex];

        // ── Rename / delete ───────────────────────────────────────────────
        ImGui.SetNextItemWidth(220);
        var nameBuf = list.Name;
        if (ImGui.InputText("##listname", ref nameBuf, 64)) { list.Name = nameBuf; config.Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Delete list"))
        {
            lists.RemoveAt(selectedListIndex);
            selectedListIndex = -1;
            config.Save();
            return;
        }

        ImGui.Separator();

        // ── Add item ──────────────────────────────────────────────────────
        ImGui.TextUnformatted("Add item:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##listadd", "Search a craftable item", ref addSearch, 64);
        if (addSearch != prevAddSearch)
        {
            prevAddSearch = addSearch;
            addMatches = addSearch.Length >= 3 ? CraftQueue.SearchCraftable(addSearch, 15) : [];
        }
        if (addMatches.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##listaddres", "(pick to add)"))
            {
                foreach (var (id, name) in addMatches)
                    if (ImGui.Selectable(name)) AddToList(list, id, name);
                ImGui.EndCombo();
            }
        }
        else if (addSearch.Length is > 0 and < 3)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(3+ chars)");
        }

        // ── Items table ───────────────────────────────────────────────────
        if (list.Items.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Empty list — search above to add items.");
            return;
        }

        if (ImGui.BeginTable("##listitems", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, 240)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Qty",  ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("##rm",  ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableHeadersRow();

            CraftListItem? toRemove = null;
            foreach (var entry in list.Items)
            {
                ImGui.PushID((int)entry.ItemId);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                DrawItemIcon(entry.ItemId);
                ImGui.SameLine();
                ImGui.TextUnformatted(entry.Name);

                ImGui.TableNextColumn();
                var have = CraftQueue.GetItemCount(entry.ItemId);
                var haveColor = have >= entry.Quantity
                    ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                ImGui.TextColored(haveColor, have.ToString());

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100);
                var qty = entry.Quantity;
                if (ImGui.InputInt("##qty", ref qty, 1, 10))
                {
                    entry.Quantity = Math.Clamp(qty, 1, 9999);
                    config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x")) toRemove = entry;

                ImGui.PopID();
            }

            ImGui.EndTable();

            if (toRemove != null)
            {
                list.Items.Remove(toRemove);
                config.Save();
            }
        }

        // ── Summary + build ───────────────────────────────────────────────
        var totalUnits = list.Items.Sum(i => i.Quantity);
        ImGui.TextDisabled($"{list.Items.Count} item(s), {totalUnits} unit(s) total.");

        var artisan = Plugin.Artisan;
        var queueRunning = Plugin.CraftQueueExecutor.CurrentState is
            CraftQueueExecutor.State.SwitchingJob or
            CraftQueueExecutor.State.OpeningRecipe or
            CraftQueueExecutor.State.Running;

        if (queueRunning) ImGui.BeginDisabled();

        List<(uint, int)> Targets() => list.Items.Select(i => (i.ItemId, i.Quantity)).ToList();

        if (artisan.IsAvailable)
        {
            // Artisan is the better crafter — make it the one-click default for a whole list.
            if (ImGui.Button("Craft with Artisan"))
            {
                var q = Plugin.CraftQueue;
                q.BuildMulti(Targets());
                q.SourceList = list;
                if (q.Entries.Count > 0 && q.Missing.Count == 0)
                {
                    var n = artisan.CraftAll(q.Entries);
                    if (n > 0)
                        Service.ToastGui.ShowNormal($"Sent '{list.Name}' ({n} step{(n == 1 ? "" : "s")}) to Artisan.");
                    else
                    {
                        MainWindow.SwitchToQueue();
                        Service.ToastGui.ShowError("Couldn't reach Artisan — opened the Queue tab.");
                    }
                }
                else
                {
                    MainWindow.SwitchToQueue();
                    Service.ToastGui.ShowNormal(q.Entries.Count == 0
                        ? "Nothing to craft — you already have everything."
                        : "Missing materials — see the Queue tab.");
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Resolve the whole list into one queue (shared materials merged,\nitems you already have skipped) and hand it straight to Artisan.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Open in Queue"))
            {
                Plugin.CraftQueue.BuildMulti(Targets());
                Plugin.CraftQueue.SourceList = list;
                MainWindow.SwitchToQueue();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Build the queue and review it on the Queue tab before crafting.");
        }
        else
        {
            if (ImGui.Button("Build & open Queue"))
            {
                Plugin.CraftQueue.BuildMulti(Targets());
                Plugin.CraftQueue.SourceList = list;
                MainWindow.SwitchToQueue();
                Service.ToastGui.ShowNormal($"Queued '{list.Name}' — check the Queue tab.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Resolve the whole list into one queue and switch to the Queue tab to craft it.\nTip: install Artisan for one-click, hands-free crafting.");
        }

        if (queueRunning) ImGui.EndDisabled();

        if (queueRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(queue is running)");
        }
    }

    // Build (or rebuild) the "GC mission" list from the open GC Supply window.
    private void AddGcMissionList()
    {
        var items = GrandCompanyMission.ReadSupplyItems();
        if (items.Count == 0)
        {
            Service.ToastGui.ShowError("No craftable GC mission items — open the GC Supply window (Personnel Officer) first.");
            return;
        }

        var lists = Plugin.Config.CraftLists;
        lists.RemoveAll(l => l.IsGcMission || l.Name == GrandCompanyMission.ListName);

        var gc = new CraftList { Name = GrandCompanyMission.ListName, IsGcMission = true };
        foreach (var (id, qty, name) in items)
            gc.Items.Add(new CraftListItem { ItemId = id, Name = name, Quantity = qty });

        lists.Add(gc);
        selectedListIndex = lists.Count - 1;
        Plugin.Config.Save();
        Service.ToastGui.ShowNormal($"Built '{gc.Name}' from your GC missions ({gc.Items.Count} item(s)).");
    }

    // Add an item to a list, or bump its quantity if it's already there.
    private static void AddToList(CraftList list, uint itemId, string name)
    {
        var existing = list.Items.Find(i => i.ItemId == itemId);
        if (existing != null) existing.Quantity = Math.Min(9999, existing.Quantity + 1);
        else list.Items.Add(new CraftListItem { ItemId = itemId, Name = name, Quantity = 1 });
        Plugin.Config.Save();
    }

    // Called from the Find tab's right-click menu.
    public static void AddItemToList(int listIndex, uint itemId, string name)
    {
        var lists = Plugin.Config.CraftLists;
        if (listIndex < 0 || listIndex >= lists.Count) return;
        AddToList(lists[listIndex], itemId, name);
    }

    private static void DrawItemIcon(uint itemId)
    {
        var size = ImGui.GetTextLineHeight();
        var icon = Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0;
        if (icon == 0) { ImGui.Dummy(new Vector2(size, size)); return; }
        try
        {
            var tex = Service.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new Vector2(size, size));
        }
        catch { ImGui.Dummy(new Vector2(size, size)); }
    }
}
