using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using GilMaster.Core;
using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows.Tabs;

/// <summary>
/// Best Kit — the strongest crafting gear (highest Craftsmanship / Control / CP) you can equip
/// for a crafter job at a given level, with which pieces you can craft or buy. Pulls the gear
/// set straight from the game's item data.
/// </summary>
public sealed class GearTab
{
    private static readonly string[] Jobs = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];
    private static readonly string[] Priorities = ["Balanced", "Craftsmanship", "Control", "CP"];
    private static readonly GearSlot[] SlotOrder =
        [GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands,
         GearSlot.Legs, GearSlot.Feet, GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.Ring];

    private int job = -1;       // -1 → initialise to the current job on first draw
    private int level;
    private int priority;
    private Dictionary<GearSlot, GearPiece>? kit;

    public void Draw()
    {
        if (job < 0) InitFromCurrentJob();

        ImGui.TextWrapped("Best Kit — the strongest crafting gear you can equip for a job at a given level " +
                          "(highest Craftsmanship / Control / CP), and which pieces you can craft or buy.");
        ImGui.Separator();

        // ── Controls ──────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(90);
        if (ImGui.BeginCombo("Job##gearjob", Jobs[job]))
        {
            for (var i = 0; i < Jobs.Length; i++)
                if (ImGui.Selectable(Jobs[i], i == job))
                {
                    job = i;
                    level = CraftQueue.GetCrafterLevel(job + 8);   // default to that job's level
                    if (level is <= 0 or >= 99) level = 100;
                }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Level##gearlvl", ref level);
        level = Math.Clamp(level, 1, 100);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("Priority##gearprio", Priorities[priority]))
        {
            for (var i = 0; i < Priorities.Length; i++)
                if (ImGui.Selectable(Priorities[i], i == priority)) priority = i;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How to break ties between pieces. Balanced ≈ the highest item level;\n" +
                             "the others lean the pick toward that stat.");

        ImGui.SameLine();
        if (ImGui.Button("Find best kit##gearfind"))
            kit = Plugin.GearEngine.BestKit(job, level, priority);

        if (kit == null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Pick a job + level and press \"Find best kit\".");
            return;
        }

        // ── Totals ────────────────────────────────────────────────────────
        var pieces = SlotOrder.Where(kit.ContainsKey).Select(s => kit[s]).ToList();
        int cms = pieces.Sum(p => p.Craftsmanship);
        int ctrl = pieces.Sum(p => p.Control);
        int cp = 180 + pieces.Sum(p => p.CP); // crafters start at 180 CP, gear adds on top
        // A ring is worn twice — count its stats again for the totals.
        if (kit.TryGetValue(GearSlot.Ring, out var ring))
        { cms += ring.Craftsmanship; ctrl += ring.Control; cp += ring.CP; }

        ImGui.Spacing();
        ImGui.TextUnformatted("HQ totals:");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"Craftsmanship {cms}");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), $"Control {ctrl}");
        ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.9f, 0.5f, 1f), $"CP {cp}");
        ImGui.SameLine();
        ImGui.TextDisabled("(rings counted ×2; before materia)");

        // ── Craft-the-set ─────────────────────────────────────────────────
        var craftable = pieces.Where(p => p.Craftable).ToList();
        if (craftable.Count > 0)
        {
            if (ImGui.Button($"Add {craftable.Count} craftable piece(s) to a list##gearlist"))
                AddKitToList(craftable);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a 'Best Kit' crafting list with every piece you can craft,\nthen build it on the Lists/Queue tab.");
            ImGui.SameLine();
        }
        ImGui.TextDisabled($"{pieces.Count} slots · {craftable.Count} craftable");

        // ── Table ─────────────────────────────────────────────────────────
        if (ImGui.BeginTable("##gearkit", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupColumn("Slot",  ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Item",  ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("iLvl",  ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Cms",   ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Ctrl",  ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("CP",    ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableHeadersRow();

            foreach (var slot in SlotOrder)
            {
                if (!kit.TryGetValue(slot, out var p)) continue;
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled(slot == GearSlot.Ring ? "Ring ×2" : SlotName(slot));

                ImGui.TableSetColumnIndex(1);
                DrawIcon(p.Icon);
                ImGui.SameLine();
                ImGui.TextUnformatted(p.Name);
                if (p.CanHq) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), "HQ"); }
                DrawContextMenu(p);

                ImGui.TableSetColumnIndex(2); ImGui.Text(p.ItemLevel.ToString());
                ImGui.TableSetColumnIndex(3); ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), p.Craftsmanship.ToString());
                ImGui.TableSetColumnIndex(4); ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), p.Control.ToString());
                ImGui.TableSetColumnIndex(5); ImGui.TextColored(new Vector4(1f, 0.9f, 0.5f, 1f), p.CP.ToString());

                ImGui.TableSetColumnIndex(6);
                if (p.Craftable)      ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), $"Craft ({p.CraftJobName})");
                else if (p.VendorSold) ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "Vendor");
                else                   ImGui.TextDisabled("Market / content");
            }
            ImGui.EndTable();
        }
    }

    private void InitFromCurrentJob()
    {
        var cj = Service.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        job   = cj is >= 8 and <= 15 ? (int)cj - 8 : 5; // default Weaver if not on a crafter
        level = CraftQueue.GetCrafterLevel(job + 8);
        if (level is <= 0 or >= 99) level = 100;
    }

    private void AddKitToList(List<GearPiece> craftable)
    {
        var lists = Plugin.Config.CraftLists;
        var name  = $"Best Kit {Jobs[job]} Lv{level}";
        lists.RemoveAll(l => l.Name == name);
        var list = new CraftList { Name = name };
        foreach (var p in craftable)
            list.Items.Add(new CraftListItem { ItemId = p.ItemId, Name = p.Name, Quantity = p.Slot == GearSlot.Ring ? 2 : 1 });
        lists.Add(list);
        Plugin.Config.Save();
        Service.ToastGui.ShowNormal($"Built '{name}' ({list.Items.Count} craftable piece(s)) — see the Lists tab.");
    }

    private static string SlotName(GearSlot s) => s switch
    {
        GearSlot.MainHand => "Main", GearSlot.OffHand => "Off", GearSlot.Head => "Head",
        GearSlot.Body => "Body", GearSlot.Hands => "Hands", GearSlot.Legs => "Legs",
        GearSlot.Feet => "Feet", GearSlot.Ears => "Ears", GearSlot.Neck => "Neck",
        GearSlot.Wrists => "Wrist", GearSlot.Ring => "Ring", _ => s.ToString(),
    };

    private void DrawContextMenu(GearPiece p)
        => ItemActions.ContextMenu($"##gearctx{p.ItemId}", p.ItemId, p.Name, p.Craftable, extra: () =>
        {
            if (p.Craftable && ImGui.MenuItem("Add to a new Best Kit list")) AddKitToList([p]);
        });

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
