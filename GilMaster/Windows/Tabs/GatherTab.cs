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

    // Standalone "find any node" search (independent of the Find-tab target)
    private string materialSearch = string.Empty;
    private string prevMaterialSearch = string.Empty;
    private List<(uint Id, string Name)> materialMatches = [];
    private uint selectedMaterialId;
    private List<GatherNode> materialNodes = [];
    private static Dictionary<uint, string>? gatherableNames;

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

            var best = PickBestNode(nodes);
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
                UptimeBitfield      = best.UptimeBitfield,
                TimedUptimeInfo     = best.TimedUptimeInfo,
                ClosestAetheryteName = best.ClosestAetheryteName,
                AetheryteId         = best.AetheryteId,
            });
        }

        // Sort: gatherable right now first (untimed or a timed node currently up),
        // grouped by zone for an efficient route; timed-but-down nodes last by soonest.
        gatherPlan = gatherPlan
            .OrderBy(n => AvailableNow(n) ? 0 : 1)
            .ThenBy(n => AvailableNow(n) ? 0 : MinutesToUp(n))
            .ThenBy(n => n.ZoneName, StringComparer.Ordinal)
            .ThenBy(n => n.RequiredLevel)
            .ToList();
    }

    // A node is gatherable now if it isn't timed, or it's timed and currently up.
    private static bool AvailableNow(GatherNode n)
        => !n.IsTimed || NodeUptime.LiveStatus(n.UptimeBitfield).IsUp;

    private static int MinutesToUp(GatherNode n)
        => n.IsTimed ? NodeUptime.LiveStatus(n.UptimeBitfield).MinutesToChange : 0;

    // Prefer a node available right now (lowest level among those); otherwise the timed
    // node coming up soonest.
    private static GatherNode PickBestNode(IReadOnlyList<GatherNode> nodes)
    {
        GatherNode? bestAvail = null; var bestAvailLvl = int.MaxValue;
        GatherNode? bestSoon  = null; var bestSoonMins = int.MaxValue;

        foreach (var n in nodes)
        {
            if (AvailableNow(n))
            {
                if (n.RequiredLevel < bestAvailLvl) { bestAvail = n; bestAvailLvl = n.RequiredLevel; }
            }
            else
            {
                var mins = NodeUptime.LiveStatus(n.UptimeBitfield).MinutesToChange;
                if (mins < bestSoonMins) { bestSoon = n; bestSoonMins = mins; }
            }
        }

        return bestAvail ?? bestSoon ?? nodes.OrderBy(n => n.RequiredLevel).First();
    }

    public void Draw()
    {
        var config = Plugin.Config;

        // Standalone node finder — type any gatherable and see its best node.
        DrawMaterialSearch();

        if (targetItem is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Or pick an item in the Find tab to see its full crafting gather plan.");
            return;
        }

        ImGui.Separator();
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

        // ── Route: zones in gather order, each with a one-click teleport ──────
        DrawRoute();

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
            ImGui.TableSetupColumn("##act",   ImGuiTableColumnFlags.WidthFixed, 145);
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
                if (node.AetheryteId != 0)
                {
                    if (ImGui.SmallButton("TP")) AetheryteData.Teleport(node.AetheryteId);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Teleport to {node.ClosestAetheryteName ?? "the nearest aetheryte"}");
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton("Map")) OpenMap(node);
                ImGui.SameLine();
                if (ImGui.SmallButton("Chat")) PrintToChat(node);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    // Compact route summary: distinct zones in plan order, each with its teleport
    // aetheryte and a one-click teleport button.
    private void DrawRoute()
    {
        if (gatherPlan is null || gatherPlan.Count == 0) return;

        // Distinct (territory, aetheryte) stops in current plan order.
        var stops = new List<(string Zone, string Aeth, uint AethId)>();
        foreach (var n in gatherPlan)
        {
            if (n.AetheryteId == 0) continue;
            if (stops.Exists(s => s.AethId == n.AetheryteId)) continue;
            stops.Add((n.ZoneName, n.ClosestAetheryteName ?? "Aetheryte", n.AetheryteId));
        }
        if (stops.Count == 0) return;

        ImGui.TextDisabled($"Route ({stops.Count} stop{(stops.Count == 1 ? "" : "s")}):");
        for (var i = 0; i < stops.Count; i++)
        {
            var (zone, aeth, aethId) = stops[i];
            ImGui.SameLine();
            ImGui.PushID($"route{i}");
            if (ImGui.SmallButton(aeth)) AetheryteData.Teleport(aethId);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Teleport to {aeth} ({zone})");
            ImGui.PopID();
            if (i < stops.Count - 1)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("→");
            }
        }
        ImGui.Spacing();
    }

    // ── Standalone node finder ────────────────────────────────────────────────
    private void DrawMaterialSearch()
    {
        ImGui.TextUnformatted("Find a node:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##matsearch", "Search any gatherable (e.g. mythrite ore)", ref materialSearch, 64);

        if (materialSearch != prevMaterialSearch)
        {
            prevMaterialSearch = materialSearch;
            materialMatches.Clear();
            if (materialSearch.Length >= 3)
            {
                gatherableNames ??= BuildGatherableIndex();
                materialMatches = gatherableNames
                    .Where(kv => kv.Value.Contains(materialSearch, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Value.Length)
                    .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                    .Take(15)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }
        }

        if (materialMatches.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260);
            var preview = selectedMaterialId != 0 && gatherableNames != null
                ? gatherableNames.GetValueOrDefault(selectedMaterialId, "(select)")
                : "(select)";
            if (ImGui.BeginCombo("##matresults", preview))
            {
                foreach (var (id, name) in materialMatches)
                    if (ImGui.Selectable(name, id == selectedMaterialId))
                        SelectMaterial(id);
                ImGui.EndCombo();
            }
        }
        else if (materialSearch.Length is > 0 and < 3)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(3+ chars)");
        }

        if (selectedMaterialId == 0) return;

        if (materialNodes.Count == 0)
        {
            ImGui.TextDisabled("No known node for that item.");
            return;
        }

        // Best node banner + a compact table of every node, best first.
        var best = materialNodes[0];
        ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "Best node:");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{best.ZoneName} (X:{best.DisplayX:F1} Y:{best.DisplayY:F1})");
        if (best.IsTimed)
        {
            ImGui.SameLine();
            var (up, _) = NodeUptime.LiveStatus(best.UptimeBitfield);
            ImGui.TextColored(up ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.3f, 1f),
                $"[{NodeUptime.LiveLabel(best.UptimeBitfield)}]");
        }

        if (ImGui.BeginTable("##matnodes", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, 150)))
        {
            ImGui.TableSetupColumn("Zone",   ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Lvl",    ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("Coords", ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("##act",  ImGuiTableColumnFlags.WidthFixed, 145);
            ImGui.TableHeadersRow();

            foreach (var node in materialNodes)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(node.ZoneName);
                if (node.ClosestAetheryteName != null)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({node.ClosestAetheryteName})");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(node.RequiredLevel.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(node.DisplayX > 0 ? $"X:{node.DisplayX:F1} Y:{node.DisplayY:F1}" : "?");

                ImGui.TableSetColumnIndex(3);
                if (node.IsTimed)
                {
                    var (up, _) = NodeUptime.LiveStatus(node.UptimeBitfield);
                    ImGui.TextColored(up ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.3f, 1f),
                        NodeUptime.LiveLabel(node.UptimeBitfield));
                }
                else
                {
                    ImGui.TextDisabled("always");
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.PushID($"mat{node.TerritoryId}_{node.RawX}_{node.RawZ}");
                if (node.AetheryteId != 0)
                {
                    if (ImGui.SmallButton("TP")) AetheryteData.Teleport(node.AetheryteId);
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton("Map")) OpenMap(node);
                ImGui.SameLine();
                if (ImGui.SmallButton("Chat")) PrintToChat(node);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void SelectMaterial(uint id)
    {
        selectedMaterialId = id;
        materialNodes = Plugin.GatheringLocator.GetNodesForItem(id)
            .OrderBy(n => AvailableNow(n) ? 0 : 1)
            .ThenBy(n => AvailableNow(n) ? 0 : MinutesToUp(n))
            .ThenBy(n => n.RequiredLevel)
            .ToList();
    }

    // Gatherable items that actually have node data, keyed by item id → name.
    private static Dictionary<uint, string> BuildGatherableIndex()
    {
        var dict = new Dictionary<uint, string>();
        foreach (var id in Plugin.GatheringLocator.GatherableItemIds)
        {
            var nodes = Plugin.GatheringLocator.GetNodesForItem(id);
            if (nodes.Count > 0) dict[id] = nodes[0].ItemName;
        }
        return dict;
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
