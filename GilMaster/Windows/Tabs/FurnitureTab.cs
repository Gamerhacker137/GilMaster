using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Furniture-selling intelligence: scan housing furnishings and rank by gil potential
/// (revenue/week = 7-day median price × weekly demand), with competition, trend, dyeable,
/// interior/exterior, craft job and net margin.
/// </summary>
public sealed class FurnitureTab
{
    private bool craftableOnly = true;
    private bool dyeableOnly;
    private int  scope;          // 0 = all, 1 = interior, 2 = exterior
    private int  minWeeklySold;
    private int  sortCol = 6;    // 0 name 1 cat 2 going 3 sold 4 sellers 5 net 6 rev/wk
    private static readonly string[] Scopes = ["All", "Interior", "Exterior"];

    // Extended filters (the "Filters" panel).
    private string nameFilter = "";
    private string categoryFilter = "";   // "" = all categories
    private int    jobFilter = -1;         // -1 = all, 0..7 = CRP..CUL
    private int    minPrice, maxPrice;     // going-price band (0 = no limit)
    private int    minNet;                 // min net/craft
    private int    maxSellers;             // hide over-saturated (0 = no limit)
    private bool   risingOnly;             // only rising-trend items

    public void Draw()
    {
        var engine = Plugin.FurnitureEngine;
        var target = GetTarget(Plugin.Config);

        ImGui.TextWrapped("Furniture-selling intelligence — what housing furnishings are worth crafting & selling. " +
                          "Ranked by revenue/week (going price × how many sell in a week).");
        ImGui.Separator();

        // ── Controls ──────────────────────────────────────────────────────
        using (new Disable(engine.IsScanning || string.IsNullOrEmpty(target)))
        {
            if (ImGui.Button(engine.IsScanning ? "Scanning..." : "Scan furniture"))
                engine.Scan(target, craftableOnly, BuildMyLevels());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Price every marketable furnishing on " + (string.IsNullOrEmpty(target) ? "your world" : target) +
                             ".\nPulls the housing item set straight from the game, so it's complete.");

        ImGui.SameLine();
        ImGui.TextDisabled(Plugin.Config.ScanDatacenter ? "[DC]" : "[World]");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan scope (change in Settings ▸ Scanning): " +
                (Plugin.Config.ScanDatacenter ? "whole datacenter" : "your home world"));

        ImGui.SameLine();
        if (ImGui.Checkbox("Craftable only##furn", ref craftableOnly)) { }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only furnishings you can craft (re-scan to apply). Off = include flip-only items too.");

        if (engine.IsScanning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##furn")) engine.Cancel();
            ImGui.SameLine();
            ImGui.ProgressBar(engine.Progress, new Vector2(160, 0));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(engine.Status);

        if (string.IsNullOrEmpty(target))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not logged in. Enter the game first.");
            return;
        }

        // ── Filters ───────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("Where##furnscope", Scopes[scope]))
        {
            for (var i = 0; i < Scopes.Length; i++)
                if (ImGui.Selectable(Scopes[i], i == scope)) scope = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Dyeable only##furn", ref dyeableOnly);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Min sold/wk##furn", ref minWeeklySold, 1, 10);
        if (minWeeklySold < 0) minWeeklySold = 0;

        ImGui.SameLine();
        var myLevelOnly = Plugin.Config.FurnitureMyLevelOnly;
        if (ImGui.Checkbox("Only what I can craft##furn", ref myLevelOnly))
        { Plugin.Config.FurnitureMyLevelOnly = myLevelOnly; Plugin.Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            var lv = BuildMyLevels();
            var crafters = string.Join("  ", Enumerable.Range(0, 8).Select(j => $"{JobShort[j]} {lv.GetValueOrDefault(j, 0)}"));
            ImGui.SetTooltip($"Hide furnishings above your crafter level (+{Plugin.Config.CraftLevelBuffer} above-level buffer).\n" +
                             "More appear as you level your crafters up. Flip-only items are unaffected.\n\n" +
                             $"Your crafters:  {crafters}");
        }

        ImGui.TextDisabled("Sellers: ");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "few = open");
        ImGui.SameLine(); ImGui.TextDisabled("·");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "many = price war");
        ImGui.SameLine(); ImGui.TextDisabled("· 🖌 = dyeable");

        DrawExtraFilters(engine.Results);

        var rows = Filter(engine.Results);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(engine.IsScanning ? "Scanning..." : "No results — press \"Scan furniture\".");
            return;
        }

        // ── Table ─────────────────────────────────────────────────────────
        if (ImGui.BeginTable("##furnresults", 8,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Category",  ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableSetupColumn("Job",       ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Going",     ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("Sold/wk",   ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("Sellers",   ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableSetupColumn("Net",       ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Rev/wk",    ImGuiTableColumnFlags.WidthFixed, 78);

            var names = new[] { "Item", "Category", "Job", "Going", "Sold/wk", "Sellers", "Net", "Rev/wk" };
            var cols  = new[] { 0, 1, -1, 2, 3, 4, 5, 6 };
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var c = 0; c < names.Length; c++)
            {
                ImGui.TableSetColumnIndex(c);
                var sortable = cols[c] >= 0;
                var label = names[c] + (sortable && sortCol == cols[c] ? " ▼" : "");
                if (ImGui.Selectable(label, false) && sortable) sortCol = cols[c];
            }

            foreach (var r in Sort(rows))
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawIcon(r.IconId);
                ImGui.SameLine();
                ImGui.TextUnformatted(r.Name);
                if (r.Dyeable) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.7f, 0.6f, 1f, 1f), "🖌"); }
                if (r.TrendDir != 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(r.TrendDir > 0 ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.4f, 1f),
                        r.TrendDir > 0 ? "↑" : "↓");
                }
                DrawContextMenu(r);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled((r.Exterior ? "▢ " : "") + r.Category);

                ImGui.TableSetColumnIndex(2);
                if (r.Craftable) ImGui.TextDisabled(r.CraftJobName); else ImGui.TextDisabled("flip");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{r.GoingPrice:N0}");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(r.WeeklySold.ToString());

                ImGui.TableSetColumnIndex(5);
                var cc = r.CompetitionTier switch
                {
                    0 => new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    1 => new Vector4(0.3f, 1f, 0.4f, 1f),
                    2 => new Vector4(1f, 0.9f, 0.3f, 1f),
                    _ => new Vector4(1f, 0.45f, 0.35f, 1f),
                };
                ImGui.TextColored(cc, r.Sellers > 0 ? r.Sellers.ToString() : "—");

                ImGui.TableSetColumnIndex(6);
                if (r.Craftable && r.MaterialCost > 0)
                {
                    var net = r.NetPerCraft;
                    ImGui.TextColored(net > 0 ? new Vector4(0.6f, 0.9f, 0.6f, 1f) : new Vector4(1f, 0.5f, 0.5f, 1f), $"{net:N0}");
                }
                else ImGui.TextDisabled("—");

                ImGui.TableSetColumnIndex(7);
                var rev = r.RevenuePerWeek;
                var rc = rev > 5_000_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                         rev >   500_000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                           new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(rc, rev >= 1_000_000 ? $"{rev / 1_000_000f:F1}M" : $"{rev / 1000f:F0}k");
            }
            ImGui.EndTable();
        }
    }

    private List<FurnitureItem> Filter(IReadOnlyList<FurnitureItem> src)
    {
        IEnumerable<FurnitureItem> q = src;
        if (dyeableOnly) q = q.Where(r => r.Dyeable);
        if (scope == 1)  q = q.Where(r => !r.Exterior);
        if (scope == 2)  q = q.Where(r => r.Exterior);
        if (minWeeklySold > 0) q = q.Where(r => r.WeeklySold >= minWeeklySold);
        if (Plugin.Config.FurnitureMyLevelOnly)
        {
            var lv = BuildMyLevels();
            var buffer = Plugin.Config.CraftLevelBuffer;
            // Keep flip-only items (no craft level); hide craftables above our level in their job.
            q = q.Where(r => !r.Craftable || r.JobId < 0
                || r.RecipeLevel <= lv.GetValueOrDefault(r.JobId, 99) + buffer);
        }

        // Extended "Filters" panel.
        if (!string.IsNullOrWhiteSpace(nameFilter))
            q = q.Where(r => r.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(categoryFilter))
            q = q.Where(r => r.Category == categoryFilter);
        if (jobFilter >= 0)  q = q.Where(r => r.JobId == jobFilter);
        if (minPrice > 0)    q = q.Where(r => r.GoingPrice >= minPrice);
        if (maxPrice > 0)    q = q.Where(r => r.GoingPrice <= maxPrice);
        if (minNet != 0)     q = q.Where(r => r.NetPerCraft >= minNet);
        if (maxSellers > 0)  q = q.Where(r => r.Sellers > 0 && r.Sellers <= maxSellers);
        if (risingOnly)      q = q.Where(r => r.TrendDir > 0);
        return q.ToList();
    }

    // The "Filters" panel: name search (always shown) + a collapsible with the finer filters.
    private void DrawExtraFilters(IReadOnlyList<FurnitureItem> all)
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##furnname", "Search name…", ref nameFilter, 64);
        ImGui.SameLine();
        var active = ActiveFilterCount();
        if (!ImGui.CollapsingHeader(active > 0 ? $"Filters ({active})##furnfilters" : "Filters##furnfilters"))
            return;

        ImGui.Indent();
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("Category##furncat", string.IsNullOrEmpty(categoryFilter) ? "All categories" : categoryFilter))
        {
            if (ImGui.Selectable("All categories", categoryFilter == "")) categoryFilter = "";
            foreach (var c in all.Select(r => r.Category).Where(c => !string.IsNullOrEmpty(c))
                                  .Distinct().OrderBy(c => c, StringComparer.Ordinal))
                if (ImGui.Selectable(c, c == categoryFilter)) categoryFilter = c;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("Job##furnjob", jobFilter < 0 ? "All jobs" : JobShort[jobFilter]))
        {
            if (ImGui.Selectable("All jobs", jobFilter < 0)) jobFilter = -1;
            for (var j = 0; j < 8; j++)
                if (ImGui.Selectable(JobShort[j], j == jobFilter)) jobFilter = j;
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(130); ImGui.InputInt("Min price##furnminp", ref minPrice, 100, 1000); if (minPrice < 0) minPrice = 0;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130); ImGui.InputInt("Max price##furnmaxp", ref maxPrice, 100, 1000); if (maxPrice < 0) maxPrice = 0;

        ImGui.SetNextItemWidth(130); ImGui.InputInt("Min net##furnminnet", ref minNet, 100, 1000);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130); ImGui.InputInt("Max sellers##furnmaxsell", ref maxSellers, 1, 5); if (maxSellers < 0) maxSellers = 0;
        ImGui.SameLine();
        ImGui.Checkbox("Rising only##furnrise", ref risingOnly);

        if (active > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset filters")) ResetFilters();
        }
        ImGui.Unindent();
    }

    private int ActiveFilterCount()
    {
        var n = 0;
        if (!string.IsNullOrWhiteSpace(nameFilter)) n++;
        if (!string.IsNullOrEmpty(categoryFilter))  n++;
        if (jobFilter >= 0) n++;
        if (minPrice > 0)   n++;
        if (maxPrice > 0)   n++;
        if (minNet != 0)    n++;
        if (maxSellers > 0) n++;
        if (risingOnly)     n++;
        return n;
    }

    private void ResetFilters()
    {
        nameFilter = ""; categoryFilter = ""; jobFilter = -1;
        minPrice = maxPrice = minNet = maxSellers = 0; risingOnly = false;
    }

    // CraftType 0..7 → the player's current level in that crafter class.
    // GetCrafterLevel takes the ClassJob id (8..15), hence + 8.
    private static readonly string[] JobShort = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    private static Dictionary<int, int> BuildMyLevels()
    {
        var d = new Dictionary<int, int>();
        for (var craft = 0; craft < 8; craft++)
            d[craft] = GilMaster.Core.CraftQueue.GetCrafterLevel(craft + 8);
        return d;
    }

    private List<FurnitureItem> Sort(List<FurnitureItem> rows) => sortCol switch
    {
        0 => rows.OrderBy(r => r.Name).ToList(),
        1 => rows.OrderBy(r => r.Category).ThenByDescending(r => r.RevenuePerWeek).ToList(),
        2 => rows.OrderByDescending(r => r.GoingPrice).ToList(),
        3 => rows.OrderByDescending(r => r.WeeklySold).ToList(),
        4 => rows.OrderBy(r => r.Sellers).ToList(),
        5 => rows.OrderByDescending(r => r.NetPerCraft).ToList(),
        _ => rows.OrderByDescending(r => r.RevenuePerWeek).ToList(),
    };

    private void DrawContextMenu(FurnitureItem r)
        => ItemActions.ContextMenu($"##furnctx{r.ItemId}", r.ItemId, r.Name, r.Craftable);

    private static void DrawIcon(ushort iconId)
    {
        var size = ImGui.GetTextLineHeight();
        if (iconId == 0) { ImGui.Dummy(new Vector2(size, size)); return; }
        try
        {
            var tex = Service.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new Vector2(size, size));
        }
        catch { ImGui.Dummy(new Vector2(size, size)); }
    }

    private static string GetTarget(Configuration config)
    {
        if (Service.PlayerState.ContentId == 0) return string.Empty;
        if (config.ScanDatacenter)
        {
            var d = Service.PlayerState.CurrentWorld.Value.DataCenter.Value.Name.ExtractText();
            return string.IsNullOrEmpty(d) ? string.Empty : d;
        }
        return Service.Objects.LocalPlayer?.HomeWorld.Value.Name.ExtractText()
               ?? Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
    }

    private readonly struct Disable : IDisposable
    {
        private readonly bool pushed;
        public Disable(bool d) { pushed = d; if (pushed) ImGui.BeginDisabled(); }
        public void Dispose() { if (pushed) ImGui.EndDisabled(); }
    }
}
