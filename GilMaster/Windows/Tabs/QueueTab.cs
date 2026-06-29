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

    // "What can I make?" inventory scan results
    private List<CraftableSuggestion> makeableResults = [];
    private bool makeableScanned;

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
        quantity = Math.Clamp(quantity, 1, 9999);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Quantity to craft");

        ImGui.SameLine();
        bool canMax = selectedItemId != 0 && !isRunning;
        if (!canMax) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Max"))
        {
            var max = queue.CalcMaxCraftable(selectedItemId);
            quantity = Math.Max(1, max);
            // CalcMaxCraftable already called Build() with the result — queue is ready
        }
        if (!canMax) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Calculate maximum craftable from current inventory");

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

        // ── "What can I make?" — scan inventory for everything craftable now ──
        if (isRunning) ImGui.BeginDisabled();
        if (ImGui.Button("What can I make?"))
        {
            makeableResults = queue.FindCraftableFromInventory();
            makeableScanned = true;
        }
        if (isRunning) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan your inventory and list everything you have the materials to craft right now (any job, your level or below).");

        // Retainer/alt-aware counting via Allagan Tools.
        DrawRetainerToggle(sameLine: true);

        if (makeableResults.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{makeableResults.Count} craftable:");

            if (ImGui.BeginTable("##makeable", 4,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 180)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Job",  ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Max",  ImGuiTableColumnFlags.WidthFixed, 45);
                ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableHeadersRow();

                foreach (var s in makeableResults)
                {
                    ImGui.PushID((int)s.ItemId);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(s.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(s.JobName);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(s.MaxQuantity.ToString());

                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("Queue 1"))
                    {
                        selectedItemId   = s.ItemId;
                        selectedItemName = s.Name;
                        quantity         = 1;
                        queue.Build(s.ItemId, 1);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Max##q"))
                    {
                        selectedItemId   = s.ItemId;
                        selectedItemName = s.Name;
                        quantity         = s.MaxQuantity;
                        queue.Build(s.ItemId, s.MaxQuantity);
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.Separator();
        }
        else if (makeableScanned)
        {
            ImGui.TextDisabled("Nothing craftable from your current inventory.");
            ImGui.Separator();
        }

        // ── Materia extraction ────────────────────────────────────────────
        DrawMateriaSection();

        // ── Food & potion auto-use ────────────────────────────────────────
        DrawConsumablesSection();

        // ── Empty state ───────────────────────────────────────────────────
        if (queue.IsEmpty)
        {
            ImGui.TextDisabled("Search for a craftable item, then click Build Queue.");
            ImGui.TextDisabled("Sub-components (e.g. undyed cloth before the hat) are queued automatically.");
            return;
        }

        // Cache crafter levels for this frame; collect any step you can't craft yet.
        var levelCache = new Dictionary<int, int>();
        int LvlFor(int jobId) => levelCache.TryGetValue(jobId, out var v)
            ? v : (levelCache[jobId] = CraftQueue.GetCrafterLevel(jobId));
        var lvlBuffer = Plugin.Config.CraftLevelBuffer;
        var levelBlocked = queue.Entries.Where(e => LvlFor(e.JobId) + lvlBuffer < e.RecipeLevel).ToList();

        // ── Crafting tree (main item first, sub-components nested underneath) ──
        ImGui.TextUnformatted("Crafting tree:");
        ImGui.SameLine();
        ImGui.TextDisabled("(sub-items are crafted first)");

        var currentItemId = executor.CurrentState == CraftQueueExecutor.State.Running
            && executor.CurrentIndex >= 0 && executor.CurrentIndex < queue.Entries.Count
            ? queue.Entries[executor.CurrentIndex].ItemId : 0u;

        var uid = 0;
        foreach (var root in queue.BuildDisplayTree())
            DrawTreeNode(root, queue, currentItemId, LvlFor, ref uid);

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

        // ── Crafter level too low ─────────────────────────────────────────
        if (levelBlocked.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Crafter level too low:");
            ImGui.Indent();
            foreach (var e in levelBlocked)
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f),
                    $"{e.Name} — needs {e.JobName} Lv {e.RecipeLevel} (you're {LvlFor(e.JobId)})");
            ImGui.Unindent();
            ImGui.TextDisabled("Level that craft up first, or remove this item from the queue.");
        }

        ImGui.Separator();

        // ── Controls ──────────────────────────────────────────────────────
        var artisan = Plugin.Artisan;
        var artisanAvail = artisan.IsAvailable;
        bool canStart = queue.Entries.Count > 0 && queue.Missing.Count == 0 && levelBlocked.Count == 0;

        if (isRunning)
        {
            // Built-in executor is running.
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Running:");
            ImGui.SameLine();
            ImGui.TextUnformatted(executor.StatusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##qstop"))
                executor.Stop();
        }
        else if (artisanAvail && artisan.IsActive)
        {
            // Artisan is crafting our hand-off.
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), "Artisan crafting…");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##artisanstop"))
                artisan.Stop();
        }
        else
        {
            if (artisanAvail)
            {
                // Preferred path: drive Artisan (battle-tested solver + executor).
                if (!canStart) ImGui.BeginDisabled();
                if (ImGui.Button("Craft with Artisan"))
                {
                    var n = artisan.CraftAll(queue.Entries);
                    if (n > 0)
                        Service.ToastGui.ShowNormal($"Sent {n} craft step{(n == 1 ? "" : "s")} to Artisan.");
                    else
                        Service.ToastGui.ShowError("Couldn't reach Artisan — try the built-in crafter.");
                }
                if (!canStart) ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Hand the whole queue to Artisan (recommended).\nSub-components are crafted before the items that need them.");

                ImGui.SameLine();
                ImGui.TextDisabled("|");
                ImGui.SameLine();
                if (!canStart) ImGui.BeginDisabled();
                if (ImGui.SmallButton("Built-in##start"))
                    executor.Start(new List<CraftQueueEntry>(queue.Entries));
                if (!canStart) ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Use GilMaster's own crafter instead of Artisan.");
            }
            else
            {
                if (!canStart) ImGui.BeginDisabled();
                if (ImGui.Button("Start Queue"))
                    executor.Start(new List<CraftQueueEntry>(queue.Entries));
                if (!canStart) ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled("Tip: install Artisan for more reliable automated crafting.");
            }

            if (queue.Missing.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Gather/buy missing materials first.");
            }
            else if (levelBlocked.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Crafter level too low (see above).");
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

    // Toggle: include retainer/alt inventory (Allagan Tools) in all "have" counts.
    public static void DrawRetainerToggle(bool sameLine = false)
    {
        var config = Plugin.Config;
        if (Plugin.AllaganTools.IsAvailable)
        {
            if (sameLine) ImGui.SameLine();
            var inc = config.IncludeRetainerInventory;
            if (ImGui.Checkbox("Count retainers/alts##incret", ref inc))
            {
                config.IncludeRetainerInventory = inc;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Use Allagan Tools to count materials stored on your retainers and alt\ncharacters too — so mats parked on a retainer still count as \"have\".");
        }
        else if (config.IncludeRetainerInventory)
        {
            // Was enabled but Allagan Tools is gone — counts silently fall back to bags.
            if (sameLine) ImGui.SameLine();
            ImGui.TextDisabled("(install Allagan Tools for retainer counts)");
        }
    }

    // Food / potion picker — auto-applied before crafting (absorbed from Artisan).
    private string foodSearch = "", potionSearch = "";
    private string prevFood = "\0", prevPotion = "\0";
    private List<(uint Id, string Name)> foodMatches = [], potionMatches = [];

    private void DrawConsumablesSection()
    {
        var cfg = Plugin.Config;
        if (!ImGui.CollapsingHeader("Food & potion (auto-use before crafting)")) return;

        DrawConsumablePicker("Food", ref foodSearch, ref prevFood, ref foodMatches,
            cfg.FoodId, cfg.FoodName, cfg.FoodHq,
            (id, name) => { cfg.FoodId = (int)id; cfg.FoodName = name; cfg.Save(); },
            hq => { cfg.FoodHq = hq; cfg.Save(); });

        DrawConsumablePicker("Potion", ref potionSearch, ref prevPotion, ref potionMatches,
            cfg.PotionId, cfg.PotionName, cfg.PotionHq,
            (id, name) => { cfg.PotionId = (int)id; cfg.PotionName = name; cfg.Save(); },
            hq => { cfg.PotionHq = hq; cfg.Save(); });

        ImGui.TextDisabled("Leave blank for none. The item is used before each craft if the buff isn't active.");
        ImGui.Separator();
    }

    private static void DrawConsumablePicker(string label, ref string search, ref string prev,
        ref List<(uint Id, string Name)> matches, int curId, string curName, bool curHq,
        Action<uint, string> onPick, Action<bool> onHq)
    {
        ImGui.TextDisabled($"{label}:");
        ImGui.SameLine();
        ImGui.TextColored(curId > 0 ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f),
            curId > 0 ? curName : "(none)");
        if (curId > 0)
        {
            ImGui.SameLine();
            var hq = curHq;
            if (ImGui.Checkbox($"HQ##{label}hq", ref hq)) onHq(hq);
            ImGui.SameLine();
            if (ImGui.SmallButton($"clear##{label}")) onPick(0, "");
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint($"##{label}search", $"search {label.ToLower()}…", ref search, 64);
        if (search != prev)
        {
            prev = search;
            matches = search.Length >= 3 ? Core.FlipEngine.Search(search, 12) : [];
        }
        if (matches.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(240);
            if (ImGui.BeginCombo($"##{label}res", "(pick)"))
            {
                foreach (var (id, name) in matches)
                    if (ImGui.Selectable(name)) { onPick(id, name); search = ""; prev = "\0"; }
                ImGui.EndCombo();
            }
        }
    }

    // Materia helper — spot 100%-spiritbonded gear and open the extraction window.
    private static void DrawMateriaSection()
    {
        var mx = Plugin.MateriaExtractor;
        if (!ImGui.CollapsingHeader("Materia extraction")) return;

        var ready = mx.ReadyCount();
        if (ready > 0)
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{ready} piece(s) at 100% spiritbond — ready to extract.");
        else
            ImGui.TextDisabled("No equipped gear at 100% spiritbond yet.");

        if (mx.IsActive)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Auto-extracting…");
            ImGui.SameLine();
            if (ImGui.Button("Stop##mxstop")) mx.Stop();
        }
        else
        {
            if (ready == 0) ImGui.BeginDisabled();
            if (ImGui.Button("Extract all materia")) mx.Start();
            if (ready == 0) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically pull materia from every 100%-spiritbonded piece, one after another.\nStops on its own when nothing's left or your bags are full.");

            ImGui.SameLine();
            if (ImGui.SmallButton(mx.WindowOpen ? "Close window" : "Open window"))
                mx.ToggleWindow();
        }

        if (!string.IsNullOrEmpty(mx.Status))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(mx.Status);
        }
        ImGui.Separator();
    }

    // Render one node of the crafting tree (and its sub-components), file-tree style.
    private static void DrawTreeNode(QueueTreeNode node, CraftQueue queue, uint currentItemId,
        Func<int, int> lvlFor, ref int uid)
    {
        var myId  = uid++;
        var entry = queue.Entries.Find(e => e.ItemId == node.ItemId);
        var done  = node.IsCraftable ? (node.CraftCount == 0 || (entry?.IsComplete ?? false)) : node.Satisfied;
        var current     = node.ItemId == currentItemId;
        var levelTooLow = node.IsCraftable && entry != null
            && lvlFor(entry.JobId) + Plugin.Config.CraftLevelBuffer < node.RecipeLevel;

        var color = levelTooLow      ? new Vector4(1f, 0.3f, 0.3f, 1f)  :
                    current          ? new Vector4(1f, 0.9f, 0.2f, 1f)  :
                    done             ? new Vector4(0.3f, 1f, 0.4f, 1f)  :
                    node.IsCraftable ? new Vector4(1f, 1f, 1f, 1f)      :
                                       new Vector4(1f, 0.7f, 0.45f, 1f); // raw material

        string action = !node.IsCraftable
            ? (node.Satisfied ? $"have {node.Have}" : $"gather/buy {node.QuantityNeeded - node.Have} (have {node.Have})")
            : node.CraftCount > 0 ? $"craft {node.CraftCount} (have {node.Have})" : $"have {node.Have}";
        string tag   = node.IsCraftable ? $"  [{node.JobName}]" : "  [Gather/Buy]";
        string label = $"{node.Name}  —  {action}{tag}";
        if (levelTooLow) label += $"  ⚠ needs Lv {node.RecipeLevel}";

        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (node.Children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.Bullet;

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        var open = ImGui.TreeNodeEx($"{label}##{myId}", flags);
        ImGui.PopStyleColor();

        if (node.Children.Count > 0 && open)
        {
            foreach (var child in node.Children)
                DrawTreeNode(child, queue, currentItemId, lvlFor, ref uid);
            ImGui.TreePop();
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
