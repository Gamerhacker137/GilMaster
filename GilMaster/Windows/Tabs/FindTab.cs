using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
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

    // Columns: Item, Lvl, NQ sale price, HQ sale price, Sales/day, Gil/day (demand×price), Gil/hr, Sellers (competition)
    private static readonly string[] ColNames = ["Item", "Lvl", "NQ Sells", "HQ Sells", "Sales/day", "Gil/day", "Gil/hr", "Sellers"];

    // Quick-sort dropdown options → the column index each maps to.
    private static readonly (string Label, int Col)[] SortOptions =
    [
        ("Income / day", 5),
        ("Income / hr",  6),
        ("Competition",  7),
        ("Level",        1),
        ("Sales / day",  4),
        ("NQ price",     2),
        ("HQ price",     3),
        ("Name",         0),
    ];

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
        using (new DisableScope(config.ScanAllJobs))
        {
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
        }

        ImGui.SameLine();
        var allJobs = config.ScanAllJobs;
        if (ImGui.Checkbox("All jobs##scan-all", ref allJobs)) { config.ScanAllJobs = allJobs; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan every crafting job and ignore the level filter — surfaces the highest-demand,\nhighest-value items across all crafts (furniture, gear, consumables). Slower: prices thousands of items.");

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

        // Scan scope is a global, edited in Settings > Scanning — show it read-only here.
        ImGui.TextDisabled(config.ScanDatacenter ? "[DC]" : "[World]");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan scope (change in Settings ▸ Scanning): " +
                (config.ScanDatacenter ? "whole datacenter" : "your home world"));

        ImGui.SameLine();

        var effectiveLevel = GetEffectiveLevel(config);
        var scanTarget = GetScanTarget(config);

        using (var disabled = new DisableScope(engine.IsScanning || string.IsNullOrEmpty(scanTarget)))
        {
            if (ImGui.Button(engine.IsScanning ? "Scanning..." : "Scan"))
                engine.StartScan(config.SelectedCraftJob, effectiveLevel, scanTarget, config, config.ScanAllJobs);
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

        // ── Level range filter (0 = no limit) ─────────────────────────
        ImGui.SameLine();
        ImGui.TextDisabled("Lvl");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        var minLvl = config.MinLevelFilter;
        if (ImGui.InputInt("##minlvl", ref minLvl, 0, 0)) { config.MinLevelFilter = Math.Clamp(minLvl, 0, 100); config.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide recipes below this level (0 = no limit)");
        ImGui.SameLine();
        ImGui.TextDisabled("–");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(46);
        var maxLvl = config.MaxLevelFilter;
        if (ImGui.InputInt("##maxlvl", ref maxLvl, 0, 0)) { config.MaxLevelFilter = Math.Clamp(maxLvl, 0, 100); config.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide recipes above this level (0 = no limit).\nSet to your level to drop high-level items you can't craft yet.");

        // ── Sort control: pick a metric, flip direction (low↔high) ────
        ImGui.SameLine();
        ImGui.TextDisabled("Sort by");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        var curIdx = Array.FindIndex(SortOptions, o => o.Col == config.SortColumn);
        if (curIdx < 0) curIdx = 0;
        if (ImGui.BeginCombo("##sortby", SortOptions[curIdx].Label))
        {
            for (var i = 0; i < SortOptions.Length; i++)
                if (ImGui.Selectable(SortOptions[i].Label, i == curIdx))
                {
                    config.SortColumn = SortOptions[i].Col;
                    config.Save();
                }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(config.SortDescending ? "▼ High→Low" : "▲ Low→High"))
        {
            config.SortDescending = !config.SortDescending;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Flip the sort direction — e.g. low level vs high level, or low income vs high income.\n(You can also click any column header to sort by it.)");

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

        // NQ/HQ mode toggle hint + competition legend
        ImGui.TextDisabled("Tip: check \"Prefer HQ\" in the Craft tab to see HQ-optimized profit. Gil/hr assumes NQ unless HQ price is shown.");
        ImGui.TextDisabled("Sellers = listings on the board:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "few = open");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "many = price war");

        if (ImGui.BeginTable("##results", 8,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Lvl",      ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("NQ Sells", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("HQ Sells", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Sales/day",ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Gil/day",  ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Gil/hr",   ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Sellers",  ImGuiTableColumnFlags.WidthFixed, 58);

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

                DrawItemIcon(item.IconId);
                ImGui.SameLine();

                var isSelected = selected?.ItemId == item.ItemId;
                if (ImGui.Selectable(item.Name, isSelected,
                    ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, 0)))
                {
                    selected = item;
                    MainWindow.SwitchToGather(item);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        $"{item.CraftJobName} · Lv {item.RecipeLevel}"
                        + (item.AmountResult > 1 ? $" · yields {item.AmountResult}×" : "")
                        + $"\nFloor (cheapest listing): NQ {item.MinListingPrice:N0}"
                        + (item.MinListingHqPrice > 0 ? $" / HQ {item.MinListingHqPrice:N0}" : "")
                        + $"\n~{item.RecentUnitsSold} sold recently"
                        + $"\n{item.ActiveListings} listing(s) up · {item.UnitsForSale} units for sale"
                        + $"\n{CompetitionLabel(item.CompetitionTier)}"
                        + $"\nMat cost/craft: {item.EstimatedMaterialCost:N0}"
                        + (config.AssumeGatherableFree ? " (gathered = free)" : " (mats from market)")
                        + $"\nNet/craft: NQ {item.ProfitNq:N0}"
                        + (item.DisplayHqPrice > item.DisplayNqPrice ? $" · HQ {item.ProfitHq:N0}" : "")
                        + (item.TrendDir != 0 ? $"\nPrice {(item.TrendDir > 0 ? "rising" : "falling")} ~{Math.Abs(item.TrendPct):F0}% recently" : "")
                        + (item.BetterToBuy ? "\n⚠ Cheaper to BUY than craft (mats cost more than the floor price)" : "")
                        + "\n(right-click for more)");

                DrawRowContextMenu(item);

                DrawTrend(item.TrendDir);
                if (item.BetterToBuy)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), "buy");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(item.RecipeLevel.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{item.DisplayNqPrice:N0}");

                ImGui.TableSetColumnIndex(3);
                if (item.DisplayHqPrice > 0 && item.RealisticHqPrice > 0)
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{item.DisplayHqPrice:N0}");
                else
                    ImGui.TextDisabled("—");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text($"{item.SaleVelocity:F1}");
                if (item.SaleVelocityHq > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"/{item.SaleVelocityHq:F1}");
                }

                // Gil/day — realistic price × how fast it sells. This is the headline
                // "hot item" metric: high means it sells a lot AND for a good price.
                ImGui.TableSetColumnIndex(5);
                var perDay = item.RevenuePerDay;
                var dayColor = perDay > 1_000_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                               perDay >   100_000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                                    new Vector4(0.7f, 0.7f, 0.7f, 1f);
                ImGui.TextColored(dayColor, perDay >= 1_000_000 ? $"{perDay / 1_000_000:F1}M" : $"{perDay / 1000:F0}k");

                ImGui.TableSetColumnIndex(6);
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

                // Sellers — competition on the board. Fewer = easier to sell into.
                ImGui.TableSetColumnIndex(7);
                var compColor = item.CompetitionTier switch
                {
                    0 => new Vector4(0.5f, 0.5f, 0.5f, 1f),   // no listings / unknown
                    1 => new Vector4(0.3f, 1f, 0.4f, 1f),     // wide open
                    2 => new Vector4(1f, 0.9f, 0.3f, 1f),     // busy
                    _ => new Vector4(1f, 0.45f, 0.35f, 1f),   // saturated
                };
                ImGui.TextColored(compColor, item.ActiveListings > 0 ? item.ActiveListings.ToString() : "—");
            }

            ImGui.EndTable();
        }
    }

    private static List<ProfitableItem> GetSorted(IReadOnlyList<ProfitableItem> source, Configuration config)
    {
        // Level-range filter (0 = no limit on either end).
        IEnumerable<ProfitableItem> items = source;
        if (config.MinLevelFilter > 0) items = items.Where(i => i.RecipeLevel >= config.MinLevelFilter);
        if (config.MaxLevelFilter > 0) items = items.Where(i => i.RecipeLevel <= config.MaxLevelFilter);

        // Default sort (col 5 = Gil/day) ranks by demand × realistic price — the
        // "what sells a lot for a good price" metric the Find tab is built around.
        IEnumerable<ProfitableItem> ordered = config.SortColumn switch
        {
            0 => items.OrderBy(i => i.Name),
            1 => items.OrderBy(i => i.RecipeLevel),
            2 => items.OrderBy(i => i.DisplayNqPrice),
            3 => items.OrderBy(i => i.DisplayHqPrice),
            4 => items.OrderBy(i => i.SaleVelocity),
            6 => items.OrderBy(i => i.GetGilPerHour(false)), // col 6 = NQ gil/hr
            7 => items.OrderBy(i => i.ActiveListings),        // col 7 = competition (fewest first)
            _ => items.OrderBy(i => i.RevenuePerDay),         // col 5 = Gil/day (demand × price)
        };
        if (config.SortDescending) ordered = ordered.Reverse();
        return [.. ordered];
    }

    // Right-click menu on a result row — the shared item actions plus Find-specific Plan / Craft entries.
    private void DrawRowContextMenu(ProfitableItem item)
    {
        ItemActions.ContextMenu($"##ctx{item.ItemId}", item.ItemId, item.Name, craftable: true, extra: () =>
        {
            if (ImGui.MenuItem("Plan — gather & craft"))
            {
                selected = item;
                MainWindow.SwitchToGather(item);
            }
            if (Plugin.Artisan.IsAvailable && ImGui.MenuItem("Craft 1 with Artisan"))
            {
                var q = Plugin.CraftQueue;
                q.Build(item.ItemId, 1);
                if (q.Entries.Count > 0 && q.Missing.Count == 0)
                {
                    var n = Plugin.Artisan.CraftAll(q.Entries);
                    if (n > 0) Service.ToastGui.ShowNormal($"Sent {item.Name} to Artisan.");
                    else { MainWindow.SwitchToQueue(); Service.ToastGui.ShowError("Couldn't reach Artisan — opened the Queue tab."); }
                }
                else { MainWindow.SwitchToQueue(); Service.ToastGui.ShowNormal("Missing materials — see the Queue tab."); }
            }
            ImGui.Separator();
        });
    }

    // Draw the game item icon at text-line height. Falls back to blank space so
    // the column stays aligned if the icon is missing or can't be loaded.
    private static void DrawItemIcon(ushort iconId)
    {
        var size = ImGui.GetTextLineHeight();
        if (iconId == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }
        try
        {
            var tex = Service.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new Vector2(size, size));
        }
        catch
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    // Inline price-trend arrow (rising green ↑ / falling red ↓; nothing when flat).
    private static void DrawTrend(sbyte dir)
    {
        if (dir == 0) return;
        ImGui.SameLine();
        if (dir > 0) ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "↑");
        else         ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "↓");
    }

    private static string CompetitionLabel(int tier) => tier switch
    {
        0 => "No active listings — be the first seller.",
        1 => "Low competition — easy to sell into.",
        2 => "Busy board — expect to undercut.",
        _ => "Saturated — price war, thin margins.",
    };

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
