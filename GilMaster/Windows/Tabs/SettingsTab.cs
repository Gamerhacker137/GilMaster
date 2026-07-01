using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GilMaster.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// One home for every global preference, grouped — replaces the toggles that used to be
/// scattered across Find / Gather / Craft / Queue / Sim. Opened by the cog (OpenConfigUi)
/// or the Settings tab.
/// </summary>
public sealed class SettingsTab
{
    private string foodSearch = "", potionSearch = "";
    private string prevFood = "\0", prevPotion = "\0";
    private List<(uint Id, string Name)> foodMatches = [], potionMatches = [];

    public void Draw()
    {
        var c = Plugin.Config;

        ImGui.TextWrapped("Settings apply everywhere in GilMaster. Hover any option for what it does.");
        ImGui.Spacing();

        // ── Scanning ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Scanning", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var dc = c.ScanDatacenter;
            if (ImGui.Checkbox("Scan the whole datacenter (instead of just your home world)", ref dc))
            { c.ScanDatacenter = dc; c.Save(); }
            Tip("Price items across every world on your DC. Slower, but shows cross-world opportunities.");

            var lim = c.ScanResultLimit;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("Max results to rank", ref lim, 5, 25))
            { c.ScanResultLimit = Math.Clamp(lim, 5, 500); c.Save(); }
            Tip("How many of the top profitable recipes the Find tab keeps after a scan.");

            var vel = (float)c.MinSaleVelocity;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputFloat("Min sales per day", ref vel, 0.5f, 1f, "%.1f"))
            { c.MinSaleVelocity = Math.Max(0, vel); c.Save(); }
            Tip("Hide items that sell slower than this — avoids 'high price, never sells' traps.");

            var freeGather = c.AssumeGatherableFree;
            if (ImGui.Checkbox("Treat gatherable materials as free", ref freeGather))
            { c.AssumeGatherableFree = freeGather; c.Save(); }
            Tip("On: gatherable mats cost 0 in profit math (you farm them). Off: they're costed at market price.");
        }

        // ── Crafting ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Crafting", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var hq = c.PreferHq;
            if (ImGui.Checkbox("Prefer HQ outcome", ref hq)) { c.PreferHq = hq; c.Save(); }
            Tip("On: solve for HQ (max quality). Off: fast NQ crafting (and Quick Synthesis when available).");

            var buf = c.CraftLevelBuffer;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("Craft above-level by", ref buf, 1, 5))
            { c.CraftLevelBuffer = Math.Clamp(buf, 0, 50); c.Save(); }
            Tip("FFXIV lets you craft recipes above your class level. This many levels above your level still\n" +
                "count as craftable — e.g. 7 means at lv18 you can make up to lv25. Used by Find, the Queue,\n'What can I make?', Furniture, and the sim.");

            var hqMat = c.UseHqMaterials;
            if (ImGui.Checkbox("Auto-fill HQ materials before crafting", ref hqMat)) { c.UseHqMaterials = hqMat; c.Save(); }
            Tip("When starting a synth, select HQ copies of materials you have (a head start on quality).");

            var hints = c.ShowBasicRotationHints;
            if (ImGui.Checkbox("Show basic rotation hints", ref hints)) { c.ShowBasicRotationHints = hints; c.Save(); }
            Tip("Static level-banded crafting tips. The live crafter doesn't need them; off by preference.");

            if (Plugin.AllaganTools.IsAvailable)
            {
                var ret = c.IncludeRetainerInventory;
                if (ImGui.Checkbox("Count retainer / alt inventory (Allagan Tools)", ref ret))
                { c.IncludeRetainerInventory = ret; c.Save(); }
                Tip("Include materials stored on retainers and alt characters in all 'have' counts.");
            }
            else ImGui.TextDisabled("Install Allagan Tools to count retainer/alt inventory.");
        }

        // ── Automation & consumables ──────────────────────────────────────
        if (ImGui.CollapsingHeader("Automation & consumables", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var repair = c.AutoRepair;
            if (ImGui.Checkbox("Auto-repair gear before a queued craft", ref repair)) { c.AutoRepair = repair; c.Save(); }
            Tip("Self-repair with dark matter when durability drops below the threshold. Broken gear that can't\nbe self-repaired stops the batch with a message.");
            if (c.AutoRepair)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                var rp = c.RepairPercent;
                if (ImGui.InputInt("below %##rep", ref rp, 5, 10)) { c.RepairPercent = Math.Clamp(rp, 1, 99); c.Save(); }
            }

            var qs = c.QuickSynthNq;
            if (ImGui.Checkbox("Quick-synth NQ crafts (when 'Prefer HQ' is off)", ref qs)) { c.QuickSynthNq = qs; c.Save(); }
            Tip("Use the game's fast batch Quick Synthesis for eligible NQ recipes instead of crafting one\naction at a time. Falls back to the normal crafter when a recipe can't be quick-synthed.");

            var watch = c.ShowCraftWatcher;
            if (ImGui.Checkbox("Record crafts in the Watch tab", ref watch)) { c.ShowCraftWatcher = watch; c.Save(); }
            Tip("Passively record every synthesis — yours or Artisan's — step by step in the Watch tab, so you can\ndiff how a craft ran against what our solver would do. Off = no recording and the Watch tab is hidden.");

            var backload = c.BackloadProgress;
            if (ImGui.Checkbox("Backload progress (finish progress last)", ref backload)) { c.BackloadProgress = backload; c.Save(); }
            Tip("Solve rotations that build ALL quality first and finish progress LAST — like Artisan — so a\ncraft can't complete before quality is done. Turning this off can end an HQ craft early at\npartial quality. Recommended: on.");

            ImGui.Spacing();
            ImGui.TextDisabled("Auto-use before crafting (leave blank for none):");
            DrawConsumablePicker("Food", ref foodSearch, ref prevFood, ref foodMatches,
                c.FoodId, c.FoodName, c.FoodHq,
                (id, n) => { c.FoodId = (int)id; c.FoodName = n; c.Save(); }, h => { c.FoodHq = h; c.Save(); });
            DrawConsumablePicker("Potion", ref potionSearch, ref prevPotion, ref potionMatches,
                c.PotionId, c.PotionName, c.PotionHq,
                (id, n) => { c.PotionId = (int)id; c.PotionName = n; c.Save(); }, h => { c.PotionHq = h; c.Save(); });
        }

        // ── Gathering ─────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Gathering"))
        {
            var aeth = c.ShowAetheryteHints;
            if (ImGui.Checkbox("Show nearest-aetheryte hints", ref aeth)) { c.ShowAetheryteHints = aeth; c.Save(); }
            var inv = c.ShowInventoryCounts;
            if (ImGui.Checkbox("Show inventory 'have' counts on the gather plan", ref inv)) { c.ShowInventoryCounts = inv; c.Save(); }
        }

        // ── Advanced / debug ──────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Advanced / debug"))
        {
            ImGui.TextDisabled($"Learned rotations (used by the crafter): {RotationCache.Count}");
            var sim = Plugin.CraftSim;
            if (sim.IsRunning)
            {
                if (ImGui.Button("Cancel warmup")) sim.Cancel();
                ImGui.SameLine(); ImGui.ProgressBar(sim.Progress, new Vector2(200, 0));
                ImGui.SameLine(); ImGui.TextDisabled(sim.Status);
            }
            else if (ImGui.Button("Warm up rotations for my level"))
                WarmRotations();
            Tip("Solve every craftable recipe at/below your level in the background and cache the best rotation,\n" +
                "so the live crafter starts instantly with a strong line. Takes a while; runs once.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear cache")) RotationCache.Clear();

            ImGui.Spacing();
            var showSim = c.ShowSimTab;
            if (ImGui.Checkbox("Show the Sim / benchmark tab (developer)", ref showSim)) { c.ShowSimTab = showSim; c.Save(); }
            Tip("Reveals the recipe-solver scoreboard tab. Not needed for normal use.");
        }

        // ── About ─────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("About"))
        {
            ImGui.TextUnformatted("GilMaster");
            ImGui.SameLine(); ImGui.TextDisabled("— find, gather, craft, and sell the most profitable items.");
            ImGui.TextDisabled("Workflow:  Find → Gather → Craft → Sell.");
            ImGui.Spacing();
            ImGui.TextDisabled("Built on the work of: MarketBoardPlugin · GatherBuddy · Craftimizer · Artisan · ECommons · Allagan Tools.");
        }
    }

    private unsafe void WarmRotations()
    {
        try
        {
            var player = Service.Objects.LocalPlayer;
            var ui = UIState.Instance();
            if (player == null || ui == null) return;
            int cms = (int)ui->PlayerState.Attributes[70];
            int ctl = (int)ui->PlayerState.Attributes[71];
            int cp = (int)player.MaxCp;
            int level = player.Level;
            var job = (int)player.ClassJob.RowId;
            int jobFilter = job is >= 8 and <= 15 ? job - 8 : -1; // -1 = all crafters
            if (cms <= 0 || ctl <= 0) { Service.ToastGui.ShowError("Get on a crafter job first."); return; }
            Plugin.CraftSim.Run(cms, ctl, cp, level, 0, jobFilter, onlyMyLevel: true, tryHard: true);
        }
        catch (Exception ex) { Service.Log.Warning(ex, "[GilMaster] Warm rotations failed"); }
    }

    private static void Tip(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
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
            matches = search.Length >= 3 ? FlipEngine.Search(search, 12) : [];
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
}
