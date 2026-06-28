using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using GilMaster.Core;
using GilMaster.Models;
using System;
using System.Collections.Generic;
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
            ImGui.TextDisabled("Search an item to check whether it's worth buying elsewhere and reselling at home.");
            return;
        }

        // ── Results ───────────────────────────────────────────────────────
        if (ImGui.BeginTable("##flipresults", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",        ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Buy @",       ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("From",        ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Home sells",  ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Margin/ea",   ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Sells/day",   ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            foreach (var r in engine.Results)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawIcon(r.IconId);
                ImGui.SameLine();
                ImGui.TextUnformatted(r.Hq ? $"{r.Name}  HQ" : r.Name);

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{r.BuyPrice:N0}");

                ImGui.TableSetColumnIndex(2);
                var sameWorld = string.Equals(r.BuyWorld, r.HomeWorld, StringComparison.OrdinalIgnoreCase);
                ImGui.TextColored(sameWorld ? new Vector4(0.7f, 0.7f, 0.7f, 1f) : new Vector4(0.4f, 0.9f, 1f, 1f), r.BuyWorld);
                if (sameWorld && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Cheapest is already on your home world — no travel needed, but no cross-world edge either.");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{r.HomeGoing:N0}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Home going rate (recent sales). Current home floor: {(r.HomeFloor > 0 ? r.HomeFloor.ToString("N0") : "none listed")}.");

                ImGui.TableSetColumnIndex(4);
                var margin = r.Margin;
                var color = margin <= 0 ? new Vector4(1f, 0.45f, 0.4f, 1f) :
                            margin > 50_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                            margin > 5_000  ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                              new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(color, $"{margin:N0}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(margin > 0
                        ? $"Buy on {r.BuyWorld} for {r.BuyPrice:N0}, sell at home ~{r.HomeGoing:N0} → +{margin:N0}/ea ({r.MarginPct:F0}%).\nWatch the home floor and competition before relisting."
                        : "No profit at the current home rate.");

                ImGui.TableSetColumnIndex(5);
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
