using Dalamud.Bindings.ImGui;
using GilMaster.Core;
using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

public sealed class FindTab
{
    private ProfitableItem? selected;
    private string searchQuery = string.Empty;

    private static readonly string[] JobNames = CraftJobNames.FullNames;

    // Columns: Item, Lvl, NQ Price, HQ Price, Sales/day, Gil/hr
    private static readonly string[] ColNames = ["Item", "Lvl", "NQ Price", "HQ Price", "Sales/day", "Gil/hr"];

    public ProfitableItem? SelectedItem => selected;

    public void Draw()
    {
        var config = Plugin.Config;
        var engine = Plugin.ProfitEngine;
        var searchTarget = GetScanTarget(config);

        // ── Item search row ───────────────────────────────────────────
        ImGui.SetNextItemWidth(220);
        var triggered = ImGui.InputTextWithHint("##itemsearch", "Search any craftable item (e.g. brass ingot)",
            ref searchQuery, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        using (var _ = new DisableScope(engine.IsSearching || string.IsNullOrWhiteSpace(searchQuery) || string.IsNullOrEmpty(searchTarget)))
        {
            if (ImGui.Button(engine.IsSearching ? "Searching..." : "Search##itemsearch-btn") || triggered)
                engine.StartSearch(searchQuery, searchTarget, config);
        }

        var inSearchMode = engine.IsSearching || engine.SearchResults.Count > 0
                           || (!string.IsNullOrEmpty(engine.SearchStatus) && !string.IsNullOrWhiteSpace(searchQuery));
        if (inSearchMode)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##itemsearch-clear"))
            {
                searchQuery = string.Empty;
                engine.ClearSearch();
                inSearchMode = false;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(engine.SearchStatus);
        }

        ImGui.Separator();

        // ── Controls row ──────────────────────────────────────────────
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Job##find-job", JobNames[config.SelectedCraftJob]))
        {
            for (var i = 0; i < JobNames.Length; i++)
            {
                if (ImGui.Selectable(JobNames[i], config.SelectedCraftJob == i))
                {
                    config.SelectedCraftJob = i;
                    config.Save();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        var lvl = config.ManualLevelOverride;
        if (ImGui.InputInt("Level##find-lvl", ref lvl, 1, 5))
        {
            config.ManualLevelOverride = Math.Clamp(lvl, 0, 100);
            config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(config.ManualLevelOverride == 0 ? "(auto)" : string.Empty);
        ImGui.SameLine();

        var dcScan = config.ScanDatacenter;
        if (ImGui.Checkbox("DC##scan-dc", ref dcScan))
        {
            config.ScanDatacenter = dcScan;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scan whole datacenter instead of home world");

        ImGui.SameLine();

        var effectiveLevel = GetEffectiveLevel(config);
        var scanTarget = GetScanTarget(config);

        using (var disabled = new DisableScope(engine.IsScanning || string.IsNullOrEmpty(scanTarget)))
        {
            if (ImGui.Button(engine.IsScanning ? "Scanning..." : "Scan"))
                engine.StartScan(config.SelectedCraftJob, effectiveLevel, scanTarget, config);
        }

        if (engine.IsScanning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) engine.CancelScan();
            ImGui.SameLine();
            ImGui.ProgressBar(engine.ScanProgress, new Vector2(160, 0));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(engine.ScanStatus);

        if (string.IsNullOrEmpty(scanTarget))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not logged in. Enter the game first.");
            return;
        }

        ImGui.Separator();

        // ── Filters row ───────────────────────────────────────────────
        ImGui.SetNextItemWidth(100);
        var vel = (float)config.MinSaleVelocity;
        if (ImGui.SliderFloat("Min sales/day##vel", ref vel, 0.5f, 30f, "%.1f"))
        {
            config.MinSaleVelocity = vel;
            config.Save();
        }

        ImGui.SameLine();
        var free = config.AssumeGatherableFree;
        if (ImGui.Checkbox("Gathered = free##free", ref free)) { config.AssumeGatherableFree = free; config.Save(); }

        ImGui.Separator();

        // ── Results table ─────────────────────────────────────────────
        var sourceList = inSearchMode ? engine.SearchResults : engine.Results;
        var results = GetSorted(sourceList, config);
        if (results.Count == 0)
        {
            ImGui.TextDisabled(inSearchMode
                ? (engine.IsSearching ? "Searching..." : "No matching craftable items.")
                : "No results yet — press Scan to start.");
            return;
        }

        if (inSearchMode)
            ImGui.TextDisabled("Showing search results. Click an item to plan it, then craft from the Craft tab.");

        // NQ/HQ mode toggle hint
        ImGui.TextDisabled("Tip: check \"Prefer HQ\" in the Craft tab to see HQ-optimized profit. Gil/hr assumes NQ unless HQ price is shown.");

        if (ImGui.BeginTable("##results", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Lvl",      ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("NQ Price", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("HQ Price", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Sales/day",ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Gil/hr",   ImGuiTableColumnFlags.WidthFixed, 72);

            // Clickable sort headers
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var col = 0; col < ColNames.Length; col++)
            {
                ImGui.TableSetColumnIndex(col);
                var label = ColNames[col] + (config.SortColumn == col ? (config.SortDescending ? " ▼" : " ▲") : "");
                if (ImGui.Selectable(label, false, ImGuiSelectableFlags.SpanAllColumns))
                {
                    if (config.SortColumn == col)
                        config.SortDescending = !config.SortDescending;
                    else
                    {
                        config.SortColumn = col;
                        config.SortDescending = true;
                    }
                    config.Save();
                }
            }

            foreach (var item in results)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);

                var isSelected = selected?.ItemId == item.ItemId;
                if (ImGui.Selectable(item.Name, isSelected,
                    ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, 0)))
                {
                    selected = item;
                    MainWindow.SwitchToGather(item);
                }
                if (ImGui.IsItemHovered() && item.AmountResult > 1)
                    ImGui.SetTooltip($"Yields {item.AmountResult}× per synth");

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(item.RecipeLevel.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{item.MinListingPrice:N0}");

                ImGui.TableSetColumnIndex(3);
                if (item.MinListingHqPrice > 0)
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{item.MinListingHqPrice:N0}");
                else
                    ImGui.TextDisabled("—");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text($"{item.SaleVelocity:F1}");
                if (item.SaleVelocityHq > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"/{item.SaleVelocityHq:F1}");
                }

                ImGui.TableSetColumnIndex(5);
                // Show NQ / HQ gil/hr side by side so user can compare
                var nqGph = item.GetGilPerHour(false);
                var hqGph = item.GetGilPerHour(true);
                var scoreColor = nqGph > 50000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                                 nqGph > 10000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                                 new Vector4(0.7f, 0.7f, 0.7f, 1f);
                ImGui.TextColored(scoreColor, $"{nqGph / 1000:F0}k");
                if (item.MinListingHqPrice > 0 && hqGph != nqGph)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"/{hqGph / 1000:F0}k");
                }
            }

            ImGui.EndTable();
        }
    }

    private static List<ProfitableItem> GetSorted(IReadOnlyList<ProfitableItem> source, Configuration config)
    {
        // Default sort (col 5) uses NQ gil/hr; HQ is shown as supplementary
        IEnumerable<ProfitableItem> ordered = config.SortColumn switch
        {
            0 => source.OrderBy(i => i.Name),
            1 => source.OrderBy(i => i.RecipeLevel),
            2 => source.OrderBy(i => i.MinListingPrice),
            3 => source.OrderBy(i => i.MinListingHqPrice),
            4 => source.OrderBy(i => i.SaleVelocity),
            _ => source.OrderBy(i => i.GetGilPerHour(false)), // col 5 = NQ gil/hr
        };
        if (config.SortDescending) ordered = ordered.Reverse();
        return [.. ordered];
    }

    private static int GetEffectiveLevel(Configuration config)
    {
        if (config.ManualLevelOverride > 0) return config.ManualLevelOverride;
        return Service.Objects.LocalPlayer?.Level ?? 1;
    }

    private static string GetScanTarget(Configuration config)
    {
        if (Service.PlayerState.ContentId == 0) return string.Empty;
        if (config.ScanDatacenter)
        {
            var dc = Service.PlayerState.CurrentWorld.Value.DataCenter.Value.Name.ExtractText();
            return string.IsNullOrEmpty(dc) ? string.Empty : dc;
        }
        return Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
    }

    private readonly struct DisableScope : IDisposable
    {
        private readonly bool pushed;
        public DisableScope(bool disable) { pushed = disable; if (pushed) ImGui.BeginDisabled(); }
        public void Dispose() { if (pushed) ImGui.EndDisabled(); }
    }
}
