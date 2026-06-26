using Dalamud.Bindings.ImGui;
using GilMaster.Core;
using GilMaster.Models;
using System;
using System.Numerics;
using System.Text;

namespace GilMaster.Windows.Tabs;

public sealed class CraftTab
{
    private ProfitableItem? targetItem;
    private RecipeIngredient[]? ingredients;
    private string craftJobName = string.Empty;
    private int recipeLevel;
    private bool preferHq;   // local copy for this tab

    public void SetTarget(ProfitableItem item)
    {
        targetItem = item;
        craftJobName = item.CraftJobName;
        recipeLevel = item.RecipeLevel;
        RefreshIngredients();
    }

    private void RefreshIngredients()
    {
        if (targetItem is null) return;
        var qty = Plugin.Config.CraftQuantity;
        ingredients = Plugin.RecipeResolver.Resolve(targetItem.RecipeId, qty);
    }

    public void Draw()
    {
        if (targetItem is null)
        {
            ImGui.TextDisabled("Select an item in the Find tab first.");

            // Show synthesis helper even without a selected item
            DrawSynthHelper();
            return;
        }

        var config = Plugin.Config;

        // ── Item header ───────────────────────────────────────────────
        ImGui.Text("Crafting: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1, 0.85f, 0.3f, 1), targetItem.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"({craftJobName} lv{recipeLevel})");
        if (targetItem.AmountResult > 1)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"×{targetItem.AmountResult}/synth");
        }

        // ── NQ / HQ toggle ────────────────────────────────────────────
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        preferHq = config.PreferHq;
        if (ImGui.Checkbox("HQ##crafthq", ref preferHq))
        {
            config.PreferHq = preferHq;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle profit display between NQ and HQ target price");

        // ── Quantity selector ─────────────────────────────────────────
        ImGui.SetNextItemWidth(80);
        var qty = config.CraftQuantity;
        if (ImGui.InputInt("×Qty##craftqty", ref qty, 1, 5))
        {
            config.CraftQuantity = Math.Clamp(qty, 1, 99);
            config.Save();
            RefreshIngredients();
        }

        ImGui.Separator();

        // ── Market snapshot ───────────────────────────────────────────
        DrawMarketSnapshot(targetItem, config);

        ImGui.Separator();

        // ── Ingredient list ───────────────────────────────────────────
        ImGui.TextUnformatted($"Ingredients (×{config.CraftQuantity}):");
        if (ingredients is null || ingredients.Length == 0)
        {
            ImGui.TextDisabled("  (none found)");
        }
        else
        {
            DrawIngredientTree(ingredients, 0);
        }

        ImGui.Separator();

        // ── One-click craft starter ───────────────────────────────────
        DrawCraftStarter(config);

        ImGui.Separator();

        // ── Synthesis helper ──────────────────────────────────────────
        DrawSynthHelper();

        ImGui.Separator();

        // ── Auto-craft section ────────────────────────────────────────
        DrawAutoCraft(config);

        // ── Rotation hints ────────────────────────────────────────────
        if (config.ShowBasicRotationHints)
        {
            ImGui.Separator();
            DrawRotationHints();
        }
    }

    private static void DrawMarketSnapshot(ProfitableItem item, Configuration config)
    {
        var preferHq = config.PreferHq;
        ImGui.TextUnformatted("Market snapshot:");
        ImGui.Columns(2, "##craftstats", false);

        ImGui.Text("Min NQ listing:"); ImGui.NextColumn();
        ImGui.Text($"{item.MinListingPrice:N0} gil"); ImGui.NextColumn();

        ImGui.Text("Min HQ listing:"); ImGui.NextColumn();
        if (item.MinListingHqPrice > 0)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"{item.MinListingHqPrice:N0} gil");
        else
            ImGui.TextDisabled("—");
        ImGui.NextColumn();

        ImGui.Text("Sales NQ/HQ:");   ImGui.NextColumn();
        ImGui.Text($"{item.SaleVelocity:F1} / {item.SaleVelocityHq:F1} per day"); ImGui.NextColumn();

        if (item.EstimatedMaterialCost > 0)
        {
            ImGui.Text("Mat cost (×1):"); ImGui.NextColumn();
            ImGui.Text($"{item.EstimatedMaterialCost:N0} gil"); ImGui.NextColumn();

            // NQ profit
            ImGui.Text("Profit NQ:"); ImGui.NextColumn();
            var profitNq = item.ProfitNq;
            var nqColor = profitNq >= 0 ? new Vector4(0.8f, 0.8f, 0.8f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f);
            ImGui.TextColored(nqColor, $"{profitNq:N0} gil");
            ImGui.NextColumn();

            // HQ profit (highlighted in blue if HQ price exists)
            if (item.MinListingHqPrice > 0)
            {
                ImGui.Text("Profit HQ:"); ImGui.NextColumn();
                var profitHq = item.ProfitHq;
                var hqColor = profitHq >= 0 ? new Vector4(0.4f, 0.9f, 1f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f);
                ImGui.TextColored(hqColor, $"{profitHq:N0} gil");
                ImGui.NextColumn();
            }

            // Gil/hr for the currently active preference
            ImGui.Text("Gil/hr est.:"); ImGui.NextColumn();
            var gilHr = item.GetGilPerHour(preferHq && item.MinListingHqPrice > 0);
            var gphColor = gilHr > 200000 ? new Vector4(0.3f, 1f, 0.4f, 1f) :
                           gilHr > 50000  ? new Vector4(1f, 0.9f, 0.3f, 1f) :
                                            new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(gphColor, $"{gilHr:N0} gil/hr");
            if (item.AmountResult > 1)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"(yields {item.AmountResult}×)");
            }
            ImGui.NextColumn();
        }
        ImGui.Columns(1);
    }

    private void DrawCraftStarter(Configuration config)
    {
        if (targetItem is null) return;
        var starter = Plugin.CraftStarter;

        ImGui.TextUnformatted("Quick-start:");
        ImGui.SameLine();

        if (!starter.IsActive)
        {
            if (ImGui.Button($"Craft {targetItem.Name}##qstart"))
            {
                Plugin.CraftExecutor.Start(config.CraftQuantity, targetItem.RecipeId);
                starter.Begin(targetItem);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "1. Switches to the required crafting job (if needed)\n" +
                    "2. Opens the Crafting Log to this recipe\n" +
                    "3. Clicks Synthesis\n" +
                    "4. Auto-craft handles the rest");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), starter.StatusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##qcancel"))
            {
                starter.Cancel();
                Plugin.CraftExecutor.Stop();
            }
        }
    }

    private static void DrawSynthHelper()
    {
        var executor = Plugin.CraftExecutor;
        if (!executor.InSynthesis) return;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.15f, 0.2f, 1f));
        if (ImGui.BeginChild("##synthhelper", new Vector2(-1, 48), true))
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), "Synthesis Helper");
            ImGui.SameLine();
            ImGui.TextDisabled("— next action:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), executor.Recommendation ?? "...");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawAutoCraft(Configuration config)
    {
        var executor = Plugin.CraftExecutor;
        var state = executor.CurrentState;

        ImGui.TextUnformatted("Auto-Craft:");
        ImGui.SameLine();

        if (state == CraftExecutor.State.Idle || state == CraftExecutor.State.Done)
        {
            if (ImGui.Button("Start Auto-Craft"))
                executor.Start(config.CraftQuantity, targetItem?.RecipeId ?? 0);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Executes an adaptive rotation using live synthesis state,\n" +
                    "reacting to conditions (Good/Excellent/Poor/Pliant).\n" +
                    "Start the synth manually in-game — the helper takes it\n" +
                    "from step 1.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "RUNNING");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop")) executor.Stop();
        }

        if (state == CraftExecutor.State.WaitingForSynth || state == CraftExecutor.State.Executing)
        {
            ImGui.Indent();
            ImGui.TextDisabled(executor.StatusText);
            ImGui.Unindent();
        }
        else if (state == CraftExecutor.State.Done)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), executor.StatusText);
        }
        else
        {
            ImGui.TextDisabled(executor.StatusText);
        }
    }

    private void DrawRotationHints()
    {
        ImGui.TextUnformatted("General tips:");
        ImGui.Indent();
        if (recipeLevel <= 50)
        {
            ImGui.BulletText("Use Basic Synthesis to build Progress.");
            ImGui.BulletText("Use Basic Touch for Quality; Inner Quiet stacks boost it.");
            ImGui.BulletText("Careful Synthesis is safer than Basic Synthesis at low durability.");
        }
        else if (recipeLevel <= 80)
        {
            ImGui.BulletText("Open with Muscle Memory + Veneration for fast progress.");
            ImGui.BulletText("Innovation + Touch combo with high IQ stacks for quality.");
            ImGui.BulletText("Manipulation restores durability — use on Pliant condition.");
            ImGui.BulletText("Byregot's Blessing converts IQ stacks into a big Quality hit.");
        }
        else
        {
            ImGui.BulletText("Muscle Memory → Veneration → Waste Not II opener.");
            ImGui.BulletText("Innovation + Standard/Advanced Touch for quality efficiency.");
            ImGui.BulletText("Pliant: free Manipulation or Innovation (half CP cost).");
            ImGui.BulletText("Good: use Precise Touch for extra quality efficiency.");
            ImGui.BulletText("Finish with Byregot's Blessing before final Careful Synthesis.");
        }
        ImGui.Unindent();
        ImGui.Spacing();
        ImGui.TextDisabled("The synthesis helper (above) shows the adaptive next action in real-time.");
    }

    private static void DrawIngredientTree(RecipeIngredient[] ings, int depth)
    {
        foreach (var ing in ings)
        {
            var indent = new string(' ', depth * 2);
            var tag    = ing.IsGatherable  ? " [G]" :
                         ing.IsCraftable   ? " [C]" :
                         ing.IsShopBuyable ? " [$]" : " [MB]";
            var color  = ing.IsGatherable  ? new Vector4(0.4f, 1f, 0.4f, 1f) :
                         ing.IsCraftable   ? new Vector4(0.4f, 0.8f, 1f, 1f) :
                         ing.IsShopBuyable ? new Vector4(1f, 0.9f, 0.4f, 1f) :
                                            new Vector4(0.8f, 0.8f, 0.8f, 1f);

            ImGui.Text($"{indent}{ing.Quantity}×");
            ImGui.SameLine();
            ImGui.TextUnformatted(ing.Name);
            ImGui.SameLine();
            ImGui.TextColored(color, tag);

            if (ing.MarketPriceFetched && ing.MarketMinPrice > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({ing.MarketMinPrice:N0}g ea)");
            }

            if (ing.IsCraftable && ing.SubIngredients.Length > 0 && depth < 3)
                DrawIngredientTree(ing.SubIngredients, depth + 1);
        }
    }
}
