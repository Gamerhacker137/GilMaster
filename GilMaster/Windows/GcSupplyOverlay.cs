using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GilMaster.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GilMaster.Windows;

/// <summary>
/// Draws a button anchored to the bottom of the native "Grand Company Delivery Missions"
/// window (addon "GrandCompanySupplyList"). One click copies the day's craftable turn-ins
/// into the "GC mission" craft list and opens GilMaster's Queue — the same flow as the Lists
/// tab's "Add GC mission list", but right where you see the missions. It's a pure ImGui
/// overlay (the technique Artisan uses); the native window itself is never modified.
///
/// This host Window is permanently open but invisible (zero-size, no background); it exists
/// only as a per-frame Draw() callback that polls for the GC window and emits the real button
/// as its own borderless sub-window positioned over the game UI.
/// </summary>
public sealed class GcSupplyOverlay : Window
{
    private const string AddonName = "GrandCompanySupplyList";

    public GcSupplyOverlay()
        : base("###GilMasterGcSupplyHost",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize)
    {
        Size = Vector2.Zero;
        Position = Vector2.Zero;
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        SizeConstraints = new WindowSizeConstraints { MaximumSize = Vector2.Zero };
    }

    public override void Draw()
    {
        // The addon can be mid-construction (null nodes) on the frame it opens — guard heavily.
        try { DrawAnchoredButton(); } catch { /* retry next frame */ }
    }

    private unsafe void DrawAnchoredButton()
    {
        var ptr = Service.GameGui.GetAddonByName(AddonName, 1).Address;
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (addon == null || !addon->IsVisible || addon->RootNode == null) return;

        // Don't draw over an open right-click menu — it would steal the click.
        if (IsAddonVisible("ContextMenu") || IsAddonVisible("AddonContextSub")) return;

        var root  = addon->RootNode;
        var pos   = AtkNodeHelper.GetNodePosition(root);
        var scale = AtkNodeHelper.GetNodeScale(root);
        var size  = new Vector2(root->Width, root->Height) * scale;
        if (size.X < 50f || size.Y < 50f) return; // window not laid out yet

        var items = GrandCompanyMission.ReadSupplyItems();
        var hasItems = items.Count > 0;

        // Anchor a borderless button window just under the bottom edge of the GC window,
        // tracking the game's UI scale so it follows HUD-scale / DPI changes.
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(pos.X, pos.Y + size.Y + 2f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin("###GilMasterGcButton",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoSavedSettings);

        var btnWidth = Math.Max(size.X - 4f, 220f);
        var label = hasItems
            ? $"★ Send {items.Count} craftable GC mission{(items.Count == 1 ? "" : "s")} to GilMaster queue"
            : "GilMaster — no craftable GC missions on this tab";

        if (!hasItems) ImGui.BeginDisabled();
        if (ImGui.Button(label, new Vector2(btnWidth, 0f)))
            SendToQueue(items);
        if (!hasItems) ImGui.EndDisabled();

        if (hasItems && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Builds the 'GC mission' list and opens it in GilMaster's Queue.");
            ImGui.Separator();
            foreach (var (_, qty, name) in items)
                ImGui.TextUnformatted($"{qty}x  {name}");
            ImGui.EndTooltip();
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static unsafe bool IsAddonVisible(string name)
    {
        try
        {
            var p = Service.GameGui.GetAddonByName(name, 1).Address;
            if (p == nint.Zero) return false;
            var a = (AtkUnitBase*)p;
            return a != null && a->IsVisible;
        }
        catch { return false; }
    }

    private static void SendToQueue(List<(uint ItemId, int Qty, string Name)> items)
    {
        if (items.Count == 0)
        {
            Service.ToastGui.ShowError("No craftable GC mission items on this window.");
            return;
        }

        var gc = GrandCompanyMission.BuildGcList();
        if (gc == null) return;

        var targets = gc.Items.Select(i => (i.ItemId, i.Quantity)).ToList();
        Plugin.CraftQueue.BuildMulti(targets);
        Plugin.CraftQueue.SourceList = gc; // MUST be after BuildMulti, which resets it to null

        // If Artisan is installed and the queue is fully stocked, hand it straight over;
        // otherwise just open GilMaster's Queue tab so the user can review/craft.
        var q = Plugin.CraftQueue;
        if (Plugin.Artisan.IsAvailable && q.Entries.Count > 0 && q.Missing.Count == 0)
        {
            var n = Plugin.Artisan.CraftAll(q.Entries);
            if (n > 0)
            {
                MainWindow.OpenToQueue();
                Service.ToastGui.ShowNormal($"Sent '{gc.Name}' ({n} step{(n == 1 ? "" : "s")}) to Artisan.");
                return;
            }
        }

        MainWindow.OpenToQueue();
        Service.ToastGui.ShowNormal($"Sent '{gc.Name}' ({gc.Items.Count} item(s)) to the Queue.");
    }
}
