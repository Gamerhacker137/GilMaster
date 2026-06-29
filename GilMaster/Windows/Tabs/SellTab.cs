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

/// <summary>
/// Sell tab — scan your bags and see what's worth listing, the competition, and the
/// price to list at (undercut the floor by 1).
/// </summary>
public sealed class SellTab
{
    private int minValue = 1000; // hide low-value clutter

    public void Draw()
    {
        var config = Plugin.Config;
        var engine = Plugin.SellEngine;
        var target = GetTarget(config);

        // ── Controls ──────────────────────────────────────────────────────
        using (new Disable(engine.IsScanning || string.IsNullOrEmpty(target)))
        {
            if (ImGui.Button(engine.IsScanning ? "Scanning..." : "Scan my bags"))
                engine.Scan(target);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Price every marketable item in your inventory (main bags + saddlebag)\nagainst your home world — where you actually list — and show the price to list at.");

        ImGui.SameLine();
        ImGui.TextDisabled($"Listing on {(string.IsNullOrEmpty(target) ? "?" : target)}");

        if (engine.IsScanning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##sell")) engine.Cancel();
            ImGui.SameLine();
            ImGui.ProgressBar(engine.Progress, new Vector2(160, 0));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Min value##sellmin", ref minValue, 100, 1000);
        if (minValue < 0) minValue = 0;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide items whose whole stack is worth less than this.");

        ImGui.SameLine();
        ImGui.TextDisabled(engine.Status);

        if (string.IsNullOrEmpty(target))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not logged in. Enter the game first.");
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled("List@ = undercut the cheapest listing by 1. Sellers: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "few = open");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "many = price war");

        var results = engine.Results.Where(r => r.StackValue >= minValue).ToList();
        if (results.Count == 0)
        {
            ImGui.TextDisabled(engine.IsScanning ? "Scanning..." : "No results — press \"Scan my bags\".");
            return;
        }

        // ── Table ─────────────────────────────────────────────────────────
        if (ImGui.BeginTable("##sellresults", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",      ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Have",      ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Going",     ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("List @",    ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Sellers",   ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("Sells/day", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Stack",     ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();

            foreach (var r in results)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawIcon(r.IconId);
                ImGui.SameLine();
                var label = r.HaveHq ? $"{r.Name}  HQ" : r.Name;
                ImGui.Selectable(label, false, ImGuiSelectableFlags.SpanAllColumns);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        $"Floor: NQ {r.FloorNq:N0}" + (r.FloorHq > 0 ? $" / HQ {r.FloorHq:N0}" : "")
                        + $"\nGoing (7-day median): NQ {r.GoingNq:N0}" + (r.GoingHq > 0 ? $" / HQ {r.GoingHq:N0}" : "")
                        + $"\n{r.Sellers} listing(s) up · ~{r.Sold7d} sold this week (~{r.Velocity:F1}/day)"
                        + (r.TrendDir != 0 ? $"\nPrice {(r.TrendDir > 0 ? "rising" : "falling")} ~{Math.Abs(r.TrendPct):F0}% recently" : "")
                        + "\n(right-click for more)");
                DrawContextMenu(r);
                if (r.TrendDir != 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(r.TrendDir > 0 ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.4f, 1f),
                        r.TrendDir > 0 ? "↑" : "↓");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(r.HaveQty.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{r.UnitValue:N0}");

                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{r.SuggestedPrice:N0}");

                ImGui.TableSetColumnIndex(4);
                var compColor = r.CompetitionTier switch
                {
                    0 => new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    1 => new Vector4(0.3f, 1f, 0.4f, 1f),
                    2 => new Vector4(1f, 0.9f, 0.3f, 1f),
                    _ => new Vector4(1f, 0.45f, 0.35f, 1f),
                };
                ImGui.TextColored(compColor, r.Sellers > 0 ? r.Sellers.ToString() : "—");

                ImGui.TableSetColumnIndex(5);
                ImGui.Text($"{r.Velocity:F1}");

                ImGui.TableSetColumnIndex(6);
                var v = r.StackValue;
                var vColor = v > 500_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                             v >  50_000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                           new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(vColor, v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" : $"{v / 1000f:F0}k");
            }

            ImGui.EndTable();
        }
    }

    private void DrawContextMenu(SellableItem r)
    {
        if (!ImGui.BeginPopupContextItem($"##sellctx{r.ItemId}")) return;
        ImGui.TextDisabled(r.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Link in chat")) LinkItemInChat(r.ItemId, r.Name);
        if (ImGui.MenuItem("Copy name")) ImGui.SetClipboardText(r.Name);
        if (ImGui.MenuItem("Copy list price")) ImGui.SetClipboardText(r.SuggestedPrice.ToString());
        ImGui.Separator();
        if (ImGui.MenuItem("Open on Universalis"))
            Dalamud.Utility.Util.OpenLink($"https://universalis.app/market/{r.ItemId}");
        ImGui.EndPopup();
    }

    private static void LinkItemInChat(uint itemId, string name)
    {
        try
        {
            var seString = new SeString(
                new ItemPayload(itemId, false),
                new TextPayload($"{(char)SeIconChar.LinkMarker}{name}"),
                RawPayload.LinkTerminator);
            Service.ChatGui.Print(seString);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to link item in chat"); }
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

    // You can only list on your HOME world, so the Sell tab always prices there
    // (even if you're currently visiting another world).
    private static string GetTarget(Configuration config)
    {
        if (Service.PlayerState.ContentId == 0) return string.Empty;
        var home = Service.Objects.LocalPlayer?.HomeWorld.Value.Name.ExtractText();
        if (!string.IsNullOrEmpty(home)) return home;
        return Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
    }

    private readonly struct Disable : IDisposable
    {
        private readonly bool pushed;
        public Disable(bool disable) { pushed = disable; if (pushed) ImGui.BeginDisabled(); }
        public void Dispose() { if (pushed) ImGui.EndDisabled(); }
    }
}
