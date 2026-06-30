using Dalamud.Bindings.ImGui;
using GilMaster.Core;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Tradecraft leves you've accepted → a craft list. Scans the leve journal, shows what each
/// accepted craft leve wants you to make, and builds a "Leve crafts" list you run on the Craft tab.
/// </summary>
public sealed class LevesTab
{
    public void Draw()
    {
        ImGui.TextWrapped("Tradecraft leves you've accepted — and the items they want you to craft. " +
                          "Turn them into a craft list in one click.");
        ImGui.Separator();

        var (leves, allowances, accepted) = LeveScanner.ReadAcceptedCraftLeves();

        ImGui.TextDisabled($"Accepted leves: {accepted}");
        ImGui.SameLine();
        ImGui.TextColored(allowances > 0 ? new Vector4(0.6f, 0.9f, 1f, 1f) : new Vector4(1f, 0.6f, 0.4f, 1f),
            $"   Allowances: {allowances}/100");

        if (leves.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No tradecraft leves accepted. Accept some at a Levemete, then come back here.");
            return;
        }

        var craftableCount = leves.Sum(l => l.Items.Count(i => i.Craftable));
        if (craftableCount > 0)
        {
            if (ImGui.Button($"Build craft list ({craftableCount} craftable item{(craftableCount == 1 ? "" : "s")})"))
                BuildAndOpen();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Make a 'Leve crafts' list with every craftable item your accepted leves need\n" +
                                 "(summed across leves), then open it on the Craft tab.");
        }
        else ImGui.TextDisabled("None of your accepted leves need craftable items.");

        ImGui.Separator();

        foreach (var leve in leves.OrderBy(l => l.Level))
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), leve.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"(Lv {leve.Level})");
            ImGui.Indent();
            foreach (var (_, qty, name, craftable) in leve.Items)
            {
                ImGui.TextUnformatted($"{qty}x  {name}");
                ImGui.SameLine();
                if (craftable) ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "[craft]");
                else           ImGui.TextDisabled("[gather/buy]");
            }
            ImGui.Unindent();
        }
    }

    private static void BuildAndOpen()
    {
        var list = LeveScanner.BuildList();
        if (list == null) { Service.ToastGui.ShowError("No craftable items in your accepted leves."); return; }
        Plugin.CraftQueue.BuildMulti(list.Items.Select(i => (i.ItemId, i.Quantity)).ToList());
        Plugin.CraftQueue.SourceList = list;
        MainWindow.OpenToQueue();
        Service.ToastGui.ShowNormal($"Built '{list.Name}' ({list.Items.Count} item(s)) — opening Craft.");
    }
}
