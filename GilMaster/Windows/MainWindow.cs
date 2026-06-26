using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GilMaster.Models;
using GilMaster.Windows.Tabs;
using System;
using System.Numerics;

namespace GilMaster.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly FindTab findTab = new();
    private readonly GatherTab gatherTab = new();
    private readonly CraftTab craftTab = new();
    private readonly QueueTab queueTab = new();
    private readonly LevelTab levelTab = new();

    // Signals to the tab bar that we want to switch to Gather next frame
    private bool pendingGatherSwitch = false;
    private bool gatherTabOpen = true;

    public MainWindow() : base("GilMaster##main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(820, 540);
        SizeCondition = ImGuiCond.FirstUseEver;

        Plugin.ProfitEngine.OnResultsUpdated += Refresh;
    }

    private void Refresh() { /* triggers redraw naturally on next frame */ }

    // Called from FindTab when user clicks a result row
    public static void SwitchToGather(ProfitableItem item)
    {
        ActiveInstance?.HandleItemSelected(item);
    }

    private static MainWindow? ActiveInstance;

    private void HandleItemSelected(ProfitableItem item)
    {
        gatherTab.SetTarget(item);
        craftTab.SetTarget(item);
        pendingGatherSwitch = true;
        Service.ToastGui.ShowNormal($"Plan ready for {item.Name} — check Gather / Craft tabs.");
    }

    public override void OnOpen() => ActiveInstance = this;
    public override void OnClose() => ActiveInstance = null;

    public override void Draw()
    {
        ActiveInstance = this;

        // ── Character header ──────────────────────────────────────────
        var player = Service.Objects.LocalPlayer;
        if (player != null && Service.PlayerState.ContentId != 0)
        {
            var world = Service.PlayerState.CurrentWorld.Value.Name.ExtractText();
            ImGui.TextDisabled($"{player.Name}   @{world}   lv{player.Level} {player.ClassJob.Value.Abbreviation.ExtractText()}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Not logged in — enter the game to use scanning.");
        }

        ImGui.Separator();

        // ── Tab bar ────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("##gm-tabs"))
        {
            if (ImGui.BeginTabItem("Find"))
            {
                findTab.Draw();
                ImGui.EndTabItem();
            }

            // Use SetSelected flag on the Gather tab when user picks an item in Find
            var gatherFlags = pendingGatherSwitch
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;

            if (ImGui.BeginTabItem("Gather", ref gatherTabOpen, gatherFlags))
            {
                pendingGatherSwitch = false;
                gatherTabOpen = true; // keep it open (ignore any accidental close)
                gatherTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Craft"))
            {
                craftTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Queue"))
            {
                queueTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Level"))
            {
                levelTab.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    public void Dispose()
    {
        if (ActiveInstance == this) ActiveInstance = null;
        Plugin.ProfitEngine.OnResultsUpdated -= Refresh;
    }
}
