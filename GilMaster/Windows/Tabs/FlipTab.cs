using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using GilMaster.Core;
using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Flip tab — buy cheap anywhere on the data centre, sell on your home world.
/// Look up any marketable item to see the cheapest world to buy and your home margin.
/// </summary>
public sealed class FlipTab
{
    private string search = string.Empty;
    private string prevSearch = string.Empty;
    private List<(uint Id, string Name)> matches = [];

    public void Draw()
    {
        var engine = Plugin.FlipEngine;
        var (dc, home) = GetWorlds();

        ImGui.TextDisabled("Buy cheapest on your data centre → sell on your home world.");
        if (string.IsNullOrEmpty(dc) || string.IsNullOrEmpty(home))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not logged in. Enter the game first.");
            return;
        }
        ImGui.TextDisabled($"DC: {dc}  ·  Home (sell): {home}");

        // ── Scan latest Find results for flips ────────────────────────────
        var findIds = Plugin.ProfitEngine.Results.Select(r => r.ItemId).Distinct().ToList();
        var canScan = !engine.Busy && findIds.Count > 0;
        if (!canScan) ImGui.BeginDisabled();
        if (ImGui.Button($"Scan Find results ({findIds.Count})"))
            engine.Scan(findIds, dc, home);
        if (!canScan) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Check every item from your latest Find scan for a buy-elsewhere / sell-at-home flip.\nRun a Find scan first to populate candidates. Only profitable flips are kept.");
        if (engine.Busy)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##flipscan")) engine.CancelScan();
            ImGui.SameLine();
            ImGui.ProgressBar(engine.Progress, new Vector2(150, 0));
        }

        // ── Search ────────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##flipsearch", "Search any marketable item", ref search, 64);
        if (search != prevSearch)
        {
            prevSearch = search;
            matches = search.Length >= 3 ? FlipEngine.Search(search, 15) : [];
        }
        if (matches.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(280);
            if (ImGui.BeginCombo("##flipres", "(pick to check)"))
            {
                foreach (var (id, name) in matches)
                    if (ImGui.Selectable(name)) engine.Lookup(id, dc, home);
                ImGui.EndCombo();
            }
        }
        else if (search.Length is > 0 and < 3)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(3+ chars)");
        }

        if (engine.Results.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##flipclear")) engine.Clear();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(engine.Busy ? "Pricing..." : engine.Status);

        ImGui.Separator();

        if (engine.Results.Count == 0)
        {
            ImGui.TextDisabled("Search an item, or \"Scan Find results\", to spot buy-elsewhere / sell-at-home flips.");
            return;
        }

        // ── Results ───────────────────────────────────────────────────────
        if (ImGui.BeginTable("##flipresults", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",        ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Buy @",       ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupColumn("From",        ImGuiTableColumnFlags.WidthFixed, 86);
            ImGui.TableSetupColumn("Home sells",  ImGuiTableColumnFlags.WidthFixed, 86);
            ImGui.TableSetupColumn("Net/ea",      ImGuiTableColumnFlags.WidthFixed, 84);
            ImGui.TableSetupColumn("Stack",       ImGuiTableColumnFlags.WidthFixed, 84);
            ImGui.TableSetupColumn("Sells/day",   ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            foreach (var r in engine.Results)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawIcon(r.IconId);
                ImGui.SameLine();
                ImGui.TextUnformatted(r.Hq ? $"{r.Name}  HQ" : r.Name);
                ItemActions.ContextMenu($"##flipctx{r.ItemId}", r.ItemId, r.Name, ItemActions.HasRecipe(r.ItemId));

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{r.BuyPrice:N0}");
                if (r.BuyAvailable > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"×{r.BuyAvailable}");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"~{r.BuyAvailable} unit(s) available within 5% of this price on {r.BuyWorld}.");

                ImGui.TableSetColumnIndex(2);
                var sameWorld = string.Equals(r.BuyWorld, r.HomeWorld, StringComparison.OrdinalIgnoreCase);
                ImGui.TextColored(sameWorld ? new Vector4(0.7f, 0.7f, 0.7f, 1f) : new Vector4(0.4f, 0.9f, 1f, 1f), r.BuyWorld);
                if (sameWorld && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Cheapest is already on your home world — no travel needed, but no cross-world edge either.");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{r.HomeGoing:N0}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Home going rate (recent sales). Current home floor: {(r.HomeFloor > 0 ? r.HomeFloor.ToString("N0") : "none listed")}.");

                // Net per-unit margin AFTER the 5% market tax.
                ImGui.TableSetColumnIndex(4);
                var net = r.NetMargin;
                var color = net <= 0 ? new Vector4(1f, 0.45f, 0.4f, 1f) :
                            net > 50_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                            net > 5_000  ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                           new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(color, $"{net:N0}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(net > 0
                        ? $"Buy on {r.BuyWorld} for {r.BuyPrice:N0}, sell at home ~{r.HomeGoing:N0}.\nAfter the 5% market tax: +{net:N0}/ea ({r.NetMarginPct:F0}%). Gross {r.Margin:N0}.\nWatch the home floor and competition before relisting."
                        : $"No profit after the 5% market tax (gross would be {r.Margin:N0}).");

                // Best-case profit on the whole cheap stack.
                ImGui.TableSetColumnIndex(5);
                var stack = r.NetStackProfit;
                if (stack > 0)
                {
                    var sColor = stack > 500_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                                 stack >  50_000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                                   new Vector4(0.8f, 0.8f, 0.8f, 1f);
                    ImGui.TextColored(sColor, stack >= 1_000_000 ? $"{stack / 1_000_000f:F1}M" : $"{stack / 1000f:F0}k");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"If you buy all ~{r.BuyAvailable} cheap unit(s) and sell at the home rate: ~{stack:N0} net.\nReality check against Sells/day — don't flood a slow market.");
                }
                else ImGui.TextDisabled("—");

                ImGui.TableSetColumnIndex(6);
                ImGui.Text($"{r.Velocity:F1}");
            }

            ImGui.EndTable();
        }
    }

    private static (string Dc, string Home) GetWorlds()
    {
        if (Service.PlayerState.ContentId == 0) return ("", "");
        var dc = Service.PlayerState.CurrentWorld.Value.DataCenter.Value.Name.ExtractText();
        var home = Service.Objects.LocalPlayer?.HomeWorld.Value.Name.ExtractText()
                   ?? Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
        return (dc, home);
    }

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
}
