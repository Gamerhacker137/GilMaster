using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using GilMaster.Core;
using GilMaster.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace GilMaster.Windows.Tabs;

public sealed class GatherTab
{
    private ProfitableItem? targetItem;
    private List<GatherNode>? gatherPlan;
    private RecipeIngredient[]? ingredients;

    public void SetTarget(ProfitableItem item)
    {
        targetItem = item;
        RebuildPlan();
    }

    private void RebuildPlan()
    {
        if (targetItem is null) return;
        ingredients = Plugin.RecipeResolver.Resolve(targetItem.RecipeId);
        gatherPlan = [];

        foreach (var ing in ingredients)
        {
            if (!ing.IsGatherable) continue;
            var nodes = Plugin.GatheringLocator.GetNodesForItem(ing.ItemId);
            if (nodes.Count == 0) continue;

            var best = nodes.OrderBy(n => n.RequiredLevel).First();
            gatherPlan.Add(new GatherNode
            {
                ItemId              = best.ItemId,
                ItemName            = best.ItemName,
                QuantityNeeded      = ing.Quantity,
                TerritoryId         = best.TerritoryId,
                ZoneName            = best.ZoneName,
                PlaceName           = best.PlaceName,
                GatheringType       = best.GatheringType,
                RawX                = best.RawX,
                RawZ                = best.RawZ,
                MapId               = best.MapId,
                DisplayX            = best.DisplayX,
                DisplayY            = best.DisplayY,
                RequiredLevel       = best.RequiredLevel,
                IsUnspoiled         = best.IsUnspoiled,
                TimedUptimeInfo     = best.TimedUptimeInfo,
                ClosestAetheryteName = best.ClosestAetheryteName,
            });
        }
    }

    public void Draw()
    {
        if (targetItem is null)
        {
            ImGui.TextDisabled("Select an item in the Find tab first.");
            return;
        }

        var config = Plugin.Config;

        ImGui.Text("Target: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1, 0.85f, 0.3f, 1), targetItem.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"(lv{targetItem.RecipeLevel} {targetItem.CraftJobName})");

        ImGui.Separator();

        // ── Ingredient overview ───────────────────────────────────────
        if (ingredients is { Length: > 0 })
        {
            ImGui.TextUnformatted("Ingredients:");
            ImGui.Indent();
            foreach (var ing in ingredients)
            {
                var tag   = ing.IsGatherable ? "[Gather]" : ing.IsCraftable ? "[Craft]" : ing.IsShopBuyable ? "[Shop]" : "[Market]";
                var color = ing.IsGatherable  ? new Vector4(0.4f, 1f, 0.4f, 1f) :
                            ing.IsCraftable   ? new Vector4(0.4f, 0.8f, 1f, 1f) :
                            ing.IsShopBuyable ? new Vector4(1f, 0.9f, 0.4f, 1f) :
                                                new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(color, tag);
                ImGui.SameLine();
                ImGui.Text($"{ing.Quantity}× {ing.Name}");

                // Show how many the player already has
                if (config.ShowInventoryCounts)
                {
                    var have = GetInventoryCount(ing.ItemId);
                    if (have > 0)
                    {
                        ImGui.SameLine();
                        var color2 = have >= ing.Quantity
                            ? new Vector4(0.3f, 1f, 0.4f, 1f)
                            : new Vector4(1f, 0.7f, 0.3f, 1f);
                        ImGui.TextColored(color2, $"(have {have})");
                    }
                }
            }
            ImGui.Unindent();
        }

        ImGui.Separator();

        // ── Gathering plan ────────────────────────────────────────────
        if (gatherPlan is null || gatherPlan.Count == 0)
        {
            ImGui.TextDisabled("No gatherable materials — everything can be purchased.");
            return;
        }

        ImGui.TextUnformatted("Gathering plan:");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy List##copylist"))
            CopyShoppingList();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy a shopping list to clipboard — shows what to gather, buy from vendor, and buy from market.");
        ImGui.Spacing();

        if (ImGui.BeginTable("##gatherplan", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",    ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Need",    ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Have",    ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Zone",    ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableSetupColumn("Coords",  ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("##act",   ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();

            foreach (var node in gatherPlan)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);

                // Item name — orange if timed/unspoiled
                if (node.IsUnspoiled)
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.1f, 1f), node.ItemName);
                else
                    ImGui.Text(node.ItemName);

                if (node.IsUnspoiled && ImGui.IsItemHovered())
                    ImGui.SetTooltip(node.IsTimed
                        ? $"Timed node\nWindows: {node.TimedUptimeInfo}\n{NodeUptime.LiveLabel(node.UptimeBitfield)}"
                        : "Unspoiled node");

                // Live uptime — green when up now, yellow with the countdown when not.
                if (node.IsTimed)
                {
                    var (up, _) = NodeUptime.LiveStatus(node.UptimeBitfield);
                    ImGui.SameLine();
                    ImGui.TextColored(
                        up ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.3f, 1f),
                        $"[{NodeUptime.LiveLabel(node.UptimeBitfield)}]");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(node.QuantityNeeded.ToString());

                ImGui.TableSetColumnIndex(2);
                var have = GetInventoryCount(node.ItemId);
                var haveColor = have >= node.QuantityNeeded
                    ? new Vector4(0.3f, 1f, 0.4f, 1f)
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                ImGui.TextColored(haveColor, have.ToString());

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(node.ZoneName);
                if (node.ClosestAetheryteName != null && config.ShowAetheryteHints)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({node.ClosestAetheryteName})");
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(node.DisplayX > 0 ? $"X:{node.DisplayX:F1} Y:{node.DisplayY:F1}" : "?");

                ImGui.TableSetColumnIndex(5);
                ImGui.PushID((int)node.ItemId);
                if (ImGui.SmallButton("Map")) OpenMap(node);
                ImGui.SameLine();
                if (ImGui.SmallButton("Chat")) PrintToChat(node);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private static unsafe int GetInventoryCount(uint itemId)
    {
        try { return (int)InventoryManager.Instance()->GetInventoryItemCount(itemId); }
        catch { return 0; }
    }

    private static void OpenMap(GatherNode node)
    {
        try
        {
            var payload = new MapLinkPayload(node.TerritoryId, node.MapId, node.RawX, node.RawZ);
            Service.GameGui.OpenMapWithMapLink(payload);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to open map link"); }
    }

    private static void PrintToChat(GatherNode node)
    {
        try
        {
            var payload = new MapLinkPayload(node.TerritoryId, node.MapId, node.RawX, node.RawZ);
            var seString = new SeString(
                new TextPayload($"[GilMaster] {node.ItemName} — {node.ZoneName} "),
                payload,
                new TextPayload($" ({node.GatheringTypeName}) lv{node.RequiredLevel}"),
                RawPayload.LinkTerminator);
            Service.ChatGui.Print(seString);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to print location to chat"); }
    }

    private void CopyShoppingList()
    {
        if (targetItem is null || ingredients is null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"=== Shopping List: {targetItem.Name} ×{Plugin.Config.CraftQuantity} ===");
        sb.AppendLine();

        var gather  = ingredients.Where(i => i.IsGatherable).ToList();
        var vendor  = ingredients.Where(i => !i.IsGatherable && i.IsShopBuyable).ToList();
        var market  = ingredients.Where(i => !i.IsGatherable && !i.IsShopBuyable && !i.IsCraftable).ToList();
        var crafted = ingredients.Where(i => i.IsCraftable).ToList();

        if (gather.Count > 0)
        {
            sb.AppendLine("[GATHER]");
            foreach (var ing in gather)
            {
                var nodes = Plugin.GatheringLocator.GetNodesForItem(ing.ItemId);
                var node  = nodes.Count > 0 ? nodes[0] : null;
                var where = node != null
                    ? $"{node.ZoneName} (X:{node.DisplayX:F1} Y:{node.DisplayY:F1}){(node.IsUnspoiled ? " ⚠ Unspoiled" : "")}"
                    : "location unknown";
                sb.AppendLine($"  {ing.Quantity}× {ing.Name}  —  {where}");
            }
            sb.AppendLine();
        }

        if (crafted.Count > 0)
        {
            sb.AppendLine("[CRAFT FIRST]");
            foreach (var ing in crafted)
                sb.AppendLine($"  {ing.Quantity}× {ing.Name}");
            sb.AppendLine();
        }

        if (vendor.Count > 0)
        {
            sb.AppendLine("[BUY FROM VENDOR]");
            foreach (var ing in vendor)
            {
                var cost = ing.ShopPrice > 0 ? $"  ({ing.ShopPrice:N0}g ea = {ing.ShopPrice * ing.Quantity:N0}g)" : "";
                sb.AppendLine($"  {ing.Quantity}× {ing.Name}{cost}");
            }
            sb.AppendLine();
        }

        if (market.Count > 0)
        {
            sb.AppendLine("[BUY FROM MARKET BOARD]");
            foreach (var ing in market)
            {
                var priceStr = ing.MarketPriceFetched && ing.MarketMinPrice > 0
                    ? $"  (~{ing.MarketMinPrice:N0}g ea = ~{ing.MarketMinPrice * ing.Quantity:N0}g)"
                    : "";
                sb.AppendLine($"  {ing.Quantity}× {ing.Name}{priceStr}");
            }
            sb.AppendLine();
        }

        if (targetItem.EstimatedMaterialCost > 0)
        {
            sb.AppendLine($"Est. material cost:  {targetItem.EstimatedMaterialCost:N0} gil");
            sb.AppendLine($"Est. profit (NQ):    {targetItem.ProfitNq:N0} gil");
            if (targetItem.MinListingHqPrice > 0)
                sb.AppendLine($"Est. profit (HQ):    {targetItem.ProfitHq:N0} gil");
        }

        ImGui.SetClipboardText(sb.ToString());
        Service.ToastGui.ShowNormal("Shopping list copied to clipboard!");
    }
}
