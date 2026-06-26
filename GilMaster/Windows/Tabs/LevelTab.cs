using Dalamud.Bindings.ImGui;
using GilMaster.Core;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

public sealed class LevelTab
{
    private bool showDoh = true;
    private int manualLevel = 0;

    public void Draw()
    {
        // ── Role selector ─────────────────────────────────────────────
        if (ImGui.RadioButton("Crafting (DoH)", showDoh)) showDoh = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Gathering (DoL)", !showDoh)) showDoh = false;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("Level##lvl-advisor", ref manualLevel, 1, 5);
        manualLevel = System.Math.Clamp(manualLevel, 0, 100);

        ImGui.SameLine();
        if (manualLevel == 0)
        {
            ImGui.TextDisabled("(using current job level)");
        }

        ImGui.Separator();

        var level = manualLevel > 0 ? manualLevel : (Service.Objects.LocalPlayer?.Level ?? 1);

        ImGui.TextUnformatted($"Level {level} {(showDoh ? "DoH" : "DoL")} Leveling Guide");
        ImGui.Separator();

        var tips = Plugin.LevelingAdvisor.GetTips(level, showDoh);

        if (tips.Count == 0)
        {
            ImGui.TextDisabled("No tips available for this level/role combination.");
            return;
        }

        // ── Priority block ────────────────────────────────────────────
        var priorities = Plugin.LevelingAdvisor.GetTopPriorities(level, showDoh);
        if (priorities.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), "Top priorities:");
            ImGui.Indent();
            foreach (var tip in priorities)
            {
                ImGui.BulletText(tip.Activity);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(tip.Details);
            }
            ImGui.Unindent();
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("All tips for this level:");
        ImGui.Spacing();

        // ── Full tips list ─────────────────────────────────────────────
        foreach (var tip in tips)
        {
            if (ImGui.CollapsingHeader($"{tip.Activity}##tip-{tip.Activity.GetHashCode()}"))
            {
                ImGui.Indent();
                ImGui.TextWrapped(tip.Details);

                if (tip.MinLevel > 1 || tip.MaxLevel < 100)
                    ImGui.TextDisabled($"Applies: lv{tip.MinLevel}–{tip.MaxLevel}");

                ImGui.Unindent();
                ImGui.Spacing();
            }
        }

        ImGui.Separator();
        ImGui.Spacing();

        // ── General XP tips ──────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Always-on bonuses:");
        ImGui.Indent();
        ImGui.BulletText("Eat food for the 3% EXP bonus (any cheap food works).");
        ImGui.BulletText("Log out in a sanctuary (inn/housing) for Rested EXP.");
        ImGui.BulletText("FC EXP buff (if available) stacks with everything.");
        ImGui.BulletText("Armory bonus: sub-job below highest job gets +100% XP.");
        ImGui.Unindent();
    }
}
