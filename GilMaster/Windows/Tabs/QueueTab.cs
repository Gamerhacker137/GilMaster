using Dalamud.Bindings.ImGui;
using GilMaster.Core;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

public sealed class QueueTab
{
    private string searchText       = string.Empty;
    private int    quantity         = 1;
    private uint   selectedItemId;
    private string selectedItemName = string.Empty;
    private string prevSearch       = string.Empty;
    private List<(uint Id, string Name)> searchResults = [];

    // Built once on first search — index of all craftable items
    private static Dictionary<uint, string>? craftableItemNames;

    public void Draw()
    {
        var queue    = Plugin.CraftQueue;
        var executor = Plugin.CraftQueueExecutor;
        var isRunning = executor.CurrentState is
            CraftQueueExecutor.State.SwitchingJob or
            CraftQueueExecutor.State.OpeningRecipe or
            CraftQueueExecutor.State.Running;

        // ── Search box ────────────────────────────────────────────────────
        ImGui.TextUnformatted("Item:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputText("##qsearch", ref searchText, 64);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("##qqty", ref quantity, 1, 10);
        quantity = Math.Clamp(quantity, 1, 999);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Quantity to craft");

        // Rebuild search results when text changes (3+ chars required)
        if (searchText != prevSearch)
        {
            prevSearch = searchText;
            if (searchText.Length >= 3)
            {
                craftableItemNames ??= BuildCraftableNameIndex();
                var lower = searchText;
                searchResults = craftableItemNames
                    .Where(kv => kv.Value.Contains(lower, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Value)
                    .Take(20)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }
            else
            {
                searchResults.Clear();
            }
        }

        // Results dropdown
        if (searchResults.Count > 0)
        {
            ImGui.SetNextItemWidth(320);
            var previewLabel = selectedItemId != 0 ? selectedItemName : "(select)";
            if (ImGui.BeginCombo("##qresults", previewLabel))
            {
                foreach (var (id, name) in searchResults)
                {
                    bool isSel = selectedItemId == id;
                    if (ImGui.Selectable(name, isSel))
                    {
                        selectedItemId   = id;
                        selectedItemName = name;
                    }
                    if (isSel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        else if (searchText.Length >= 3)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(no results)");
        }
        else if (searchText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(type 3+ chars)");
        }

        // Build Queue button
        ImGui.SameLine();
        bool canBuild = selectedItemId != 0 && !isRunning;
        if (!canBuild) ImGui.BeginDisabled();
        if (ImGui.Button("Build Queue"))
            queue.Build(selectedItemId, quantity);
        if (!canBuild) ImGui.EndDisabled();

        ImGui.Separator();

        // ── Empty state ───────────────────────────────────────────────────
        if (queue.IsEmpty)
        {
            ImGui.TextDisabled("Search for a craftable item, then click Build Queue.");
            ImGui.TextDisabled("Sub-components (e.g. undyed cloth before the hat) are queued automatically.");
            return;
        }

        // ── Crafting steps ────────────────────────────────────────────────
        ImGui.TextUnformatted("Crafting steps:");

        if (ImGui.BeginTable("##qsteps", 5,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed,   18);
            ImGui.TableSetupColumn("Item",   ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Job",    ImGuiTableColumnFlags.WidthFixed,   40);
            ImGui.TableSetupColumn("Have",   ImGuiTableColumnFlags.WidthFixed,   45);
            ImGui.TableSetupColumn("Craft",  ImGuiTableColumnFlags.WidthFixed,   45);
            ImGui.TableHeadersRow();

            for (var i = 0; i < queue.Entries.Count; i++)
            {
                var entry = queue.Entries[i];
                bool isCurrent = executor.CurrentState == CraftQueueExecutor.State.Running
                              && executor.CurrentIndex == i;
                bool isDone    = entry.IsComplete;

                ImGui.TableNextRow();

                // Status icon column
                ImGui.TableNextColumn();
                if (isDone)
                    ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "v");
                else if (isCurrent)
                    ImGui.TextColored(new Vector4(1f, 0.9f, 0.2f, 1f), ">");
                else
                    ImGui.TextDisabled("-");

                // Item name
                ImGui.TableNextColumn();
                var nameColor = isDone    ? new Vector4(0.3f, 1f, 0.4f, 1f)  :
                                isCurrent ? new Vector4(1f, 0.9f, 0.2f, 1f)  :
                                            new Vector4(1f, 1f, 1f, 1f);
                ImGui.TextColored(nameColor, entry.Name);

                // Job
                ImGui.TableNextColumn();
                ImGui.TextDisabled(entry.JobName);

                // Have in bags
                ImGui.TableNextColumn();
                var liveCount = CraftQueue.GetItemCount(entry.ItemId);
                ImGui.Text(liveCount.ToString());

                // Craft count
                ImGui.TableNextColumn();
                if (isDone)
                    ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{entry.QuantityToCraft}");
                else
                    ImGui.TextUnformatted(entry.QuantityToCraft.ToString());
            }
            ImGui.EndTable();
        }

        // ── Missing materials ─────────────────────────────────────────────
        if (queue.Missing.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Need to gather or buy:");
            ImGui.Indent();
            foreach (var m in queue.Missing)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), $"{m.Quantity}x  {m.Name}");
            ImGui.Unindent();
        }

        ImGui.Separator();

        // ── Controls ──────────────────────────────────────────────────────
        if (isRunning)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Running:");
            ImGui.SameLine();
            ImGui.TextUnformatted(executor.StatusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##qstop"))
                executor.Stop();
        }
        else
        {
            bool canStart = queue.Entries.Count > 0 && queue.Missing.Count == 0;
            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button("Start Queue"))
                executor.Start(new List<CraftQueueEntry>(queue.Entries));
            if (!canStart) ImGui.EndDisabled();

            if (queue.Missing.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Gather/buy missing materials first.");
            }

            if (executor.CurrentState == CraftQueueExecutor.State.Done)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "Complete!");
            }
            else if (executor.CurrentState == CraftQueueExecutor.State.Error)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), executor.StatusText);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##qclear"))
            {
                queue.Entries.Clear();
                queue.Missing.Clear();
                executor.Stop();
            }
        }
    }

    // ── Craftable item index ──────────────────────────────────────────────

    private static Dictionary<uint, string> BuildCraftableNameIndex()
    {
        var dict  = new Dictionary<uint, string>();
        var sheet = Service.DataManager.GetExcelSheet<Recipe>();
        foreach (var recipe in sheet)
        {
            var id = recipe.ItemResult.RowId;
            if (id == 0 || dict.ContainsKey(id)) continue;
            var name = recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? "";
            if (!string.IsNullOrEmpty(name))
                dict[id] = name;
        }
        return dict;
    }
}
