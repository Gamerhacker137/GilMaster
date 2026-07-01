using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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

    // "Best to gather at my level" profit scanner state.
    private int profitJob;             // 0 = Both, 1 = Miner, 2 = Botanist
    private int profitMinSold;         // hide gatherables selling fewer than this per week
    private static readonly string[] ProfitJobs = ["Both", "Miner", "Botanist"];

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

    // ── Player gathering levels (Miner = ClassJob 16, Botanist = 17) ───────────
    // GetCrafterLevel returns 99 when not logged in, so nothing is falsely hidden.
    private static int MinerLvl    => CraftQueue.GetCrafterLevel(16);
    private static int BotanistLvl => CraftQueue.GetCrafterLevel(17);

    // The level the player has for whichever job a node needs.
    private static int MyLevelFor(GatherNode n) => n.GatheringType is 0 or 1 ? MinerLvl : BotanistLvl;

    // Can the player actually gather this node right now (high enough level for its job)?
    private static bool Reachable(GatherNode n) => n.RequiredLevel <= MyLevelFor(n);

    // Prefer a node we can gather right now AND have the level for; then any available-now
    // (lowest level); then the timed node coming up soonest; finally the lowest-level node.
    private static GatherNode PickBestNode(IReadOnlyList<GatherNode> nodes)
    {
        GatherNode? bestReady = null; var bestReadyLvl = int.MaxValue;
        GatherNode? bestAvail = null; var bestAvailLvl = int.MaxValue;
        GatherNode? bestSoon  = null; var bestSoonMins = int.MaxValue;

        foreach (var n in nodes)
        {
            if (AvailableNow(n))
            {
                if (Reachable(n) && n.RequiredLevel < bestReadyLvl) { bestReady = n; bestReadyLvl = n.RequiredLevel; }
                if (n.RequiredLevel < bestAvailLvl) { bestAvail = n; bestAvailLvl = n.RequiredLevel; }
            }
            else
            {
                var mins = NodeUptime.LiveStatus(n.UptimeBitfield).MinutesToChange;
                if (mins < bestSoonMins) { bestSoon = n; bestSoonMins = mins; }
            }
        }

        return bestReady ?? bestAvail ?? bestSoon ?? nodes.OrderBy(n => n.RequiredLevel).First();
    }

    public void Draw()
    {
        var config = Plugin.Config;

        // "What's worth gathering at my level right now?" — the headline gather feature.
        DrawProfitScanner();
        ImGui.Separator();

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
            ImGui.TableSetupColumn("##act",   ImGuiTableColumnFlags.WidthFixed, 190);
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

                // Over-level warning — you don't have the gathering level for this node yet.
                if (!Reachable(node))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"(needs lv{node.RequiredLevel})");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Your {(node.GatheringType is 0 or 1 ? "Miner" : "Botanist")} is level {MyLevelFor(node)} — this node needs {node.RequiredLevel}.");
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
                if (GatherBuddyBridge.IsAvailable)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Gath")) GatherBuddyBridge.Gather(node.ItemName);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Tell GatherBuddy to gather {node.ItemName}.");
                }
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

    // ── "Best to gather at my level" profit scanner ───────────────────────────
    private void DrawProfitScanner()
    {
        var engine = Plugin.GatherProfitEngine;
        var target = GetTarget();

        if (!ImGui.CollapsingHeader("Best to gather at my level", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("What raw materials are worth farming right now — only nodes your Miner/Botanist " +
                          "can reach, ranked by revenue/week (price × weekly demand on the board).");

        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("Job##gpjob", ProfitJobs[profitJob]))
        {
            for (var i = 0; i < ProfitJobs.Length; i++)
                if (ImGui.Selectable(ProfitJobs[i], i == profitJob)) profitJob = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"(MIN {MinerLvl} · BTN {BotanistLvl})");

        ImGui.SameLine();
        var disabled = engine.IsScanning || string.IsNullOrEmpty(target);
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(engine.IsScanning ? "Scanning...##gp" : "Scan##gp"))
        {
            var jobFilter = profitJob switch { 1 => 0, 2 => 1, _ => -1 };
            engine.Scan(target, jobFilter, MinerLvl, BotanistLvl);
        }
        if (disabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Price every gatherable you can reach on " +
                             (string.IsNullOrEmpty(target) ? "your world" : target) + ".");

        ImGui.SameLine();
        ImGui.TextDisabled(Plugin.Config.ScanDatacenter ? "[DC]" : "[World]");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan scope (change in Settings ▸ Scanning): " +
                (Plugin.Config.ScanDatacenter ? "whole datacenter" : "your home world"));

        if (engine.IsScanning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##gp")) engine.Cancel();
            ImGui.SameLine();
            ImGui.ProgressBar(engine.Progress, new Vector2(140, 0));
        }
        ImGui.SameLine();
        ImGui.TextDisabled(engine.Status);

        if (string.IsNullOrEmpty(target))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not logged in. Enter the game first.");
            return;
        }

        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Min sold/wk##gp", ref profitMinSold, 1, 10);
        if (profitMinSold < 0) profitMinSold = 0;

        var rows = engine.Results.Where(r => r.WeeklySold >= profitMinSold).ToList();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(engine.IsScanning ? "Scanning..." : "No results yet — press \"Scan\".");
            return;
        }

        if (ImGui.BeginTable("##gpresults", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0, 280)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item",    ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Job",     ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("Lvl",     ImGuiTableColumnFlags.WidthFixed, 34);
            ImGui.TableSetupColumn("Price",   ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Sold/wk", ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("Rev/wk",  ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("##act",   ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var r in rows)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(r.Name);
                ItemActions.ContextMenu($"##gpctx{r.ItemId}", r.ItemId, r.Name, ItemActions.HasRecipe(r.ItemId));
                if (r.TrendDir != 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(r.TrendDir > 0 ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.4f, 1f),
                        r.TrendDir > 0 ? "↑" : "↓");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled(r.JobName);

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(r.RequiredLevel.ToString());

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{r.GoingPrice:N0}");

                ImGui.TableSetColumnIndex(4);
                var cc = r.CompetitionTier switch
                {
                    1 => new Vector4(0.3f, 1f, 0.4f, 1f),
                    2 => new Vector4(1f, 0.9f, 0.3f, 1f),
                    3 => new Vector4(1f, 0.45f, 0.35f, 1f),
                    _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
                };
                ImGui.TextColored(cc, r.WeeklySold.ToString());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{r.Sellers} seller{(r.Sellers == 1 ? "" : "s")} listed — {(r.CompetitionTier <= 1 ? "open market" : "watch for undercutting")}.");

                ImGui.TableSetColumnIndex(5);
                var rev = r.RevenuePerWeek;
                var rc = rev > 5_000_000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                         rev >   500_000 ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                           new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(rc, rev >= 1_000_000 ? $"{rev / 1_000_000f:F1}M" : $"{rev / 1000f:F0}k");

                ImGui.TableSetColumnIndex(6);
                var node = NodeOf(r);
                ImGui.PushID((int)r.ItemId);
                if (r.AetheryteId != 0)
                {
                    if (ImGui.SmallButton("TP")) AetheryteData.Teleport(r.AetheryteId);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Teleport to {r.Aetheryte ?? "the nearest aetheryte"}");
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton("Map")) OpenMap(node);
                ImGui.SameLine();
                if (ImGui.SmallButton("Chat")) PrintToChat(node);
                if (GatherBuddyBridge.IsAvailable)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Gath")) GatherBuddyBridge.Gather(r.Name);
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    // A GatherProfitItem carries its best node's location — rebuild a GatherNode for the
    // shared Map/Chat helpers.
    private static GatherNode NodeOf(GatherProfitItem g) => new()
    {
        ItemId = g.ItemId, ItemName = g.Name, TerritoryId = g.TerritoryId, ZoneName = g.Zone,
        PlaceName = g.Zone, GatheringType = g.GatheringType, RawX = g.RawX, RawZ = g.RawZ,
        MapId = g.MapId, DisplayX = g.DisplayX, DisplayY = g.DisplayY, RequiredLevel = g.RequiredLevel,
        UptimeBitfield = g.UptimeBitfield, ClosestAetheryteName = g.Aetheryte, AetheryteId = g.AetheryteId,
    };

    // Where to price gatherables: home world, or whole datacenter when ScanDatacenter is on.
    private static string GetTarget()
    {
        if (Service.PlayerState.ContentId == 0) return string.Empty;
        if (Plugin.Config.ScanDatacenter)
        {
            var d = Service.PlayerState.CurrentWorld.Value.DataCenter.Value.Name.ExtractText();
            return string.IsNullOrEmpty(d) ? string.Empty : d;
        }
        return Service.Objects.LocalPlayer?.HomeWorld.Value.Name.ExtractText()
               ?? Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
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
            ImGui.TableSetupColumn("##act",  ImGuiTableColumnFlags.WidthFixed, 190);
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
                if (Reachable(node))
                    ImGui.Text(node.RequiredLevel.ToString());
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), node.RequiredLevel.ToString());
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Your {(node.GatheringType is 0 or 1 ? "Miner" : "Botanist")} is level {MyLevelFor(node)} — this node needs {node.RequiredLevel}.");
                }

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
                if (GatherBuddyBridge.IsAvailable)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Gath")) GatherBuddyBridge.Gather(node.ItemName);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Tell GatherBuddy to gather {node.ItemName}.");
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void SelectMaterial(uint id)
    {
        selectedMaterialId = id;
        materialNodes = Plugin.GatheringLocator.GetNodesForItem(id)
            .OrderByDescending(Reachable)                      // nodes you can actually gather first
            .ThenBy(n => AvailableNow(n) ? 0 : 1)
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

    // Route through CraftQueue so the same retainer/alt-aware counting (Allagan Tools)
    // applies everywhere counts are shown.
    private static int GetInventoryCount(uint itemId) => CraftQueue.GetItemCount(itemId);

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
