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
    private readonly ListsTab listsTab = new();
    private readonly SellTab sellTab = new();
    private readonly FlipTab flipTab = new();
    private readonly SimTab simTab = new();
    private readonly LevelTab levelTab = new();

    // Signals to the tab bar that we want to switch to Gather / Queue next frame
    private bool pendingGatherSwitch = false;
    private bool pendingQueueSwitch = false;

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

    // Called from ListsTab after building a multi-item queue
    public static void SwitchToQueue()
    {
        if (ActiveInstance != null) ActiveInstance.pendingQueueSwitch = true;
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
        // No tab is closeable (no ref-bool overload → no X button) and each tab's
        // body lives in its own scrolling child so long content never gets cut off.
        if (ImGui.BeginTabBar("##gm-tabs"))
        {
            if (ImGui.BeginTabItem("Find"))
            {
                DrawTabBody("Find", findTab.Draw);
                ImGui.EndTabItem();
            }

            // Use SetSelected flag on the Gather tab when user picks an item in Find
            var gatherFlags = pendingGatherSwitch
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;

            if (ImGui.BeginTabItem("Gather", gatherFlags))
            {
                pendingGatherSwitch = false;
                DrawTabBody("Gather", gatherTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Craft"))
            {
                DrawTabBody("Craft", craftTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Lists"))
            {
                DrawTabBody("Lists", listsTab.Draw);
                ImGui.EndTabItem();
            }

            var queueFlags = pendingQueueSwitch
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Queue", queueFlags))
            {
                pendingQueueSwitch = false;
                DrawTabBody("Queue", queueTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Sell"))
            {
                DrawTabBody("Sell", sellTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Flip"))
            {
                DrawTabBody("Flip", flipTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Sim"))
            {
                DrawTabBody("Sim", simTab.Draw);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Level"))
            {
                DrawTabBody("Level", levelTab.Draw);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // Wrap a tab's content in a scrolling child region so it can scroll
    // independently when the content is taller than the window.
    private static void DrawTabBody(string id, Action draw)
    {
        if (ImGui.BeginChild(id + "##body", new Vector2(0, 0), false))
            draw();
        ImGui.EndChild();
    }

    public void Dispose()
    {
        if (ActiveInstance == this) ActiveInstance = null;
        Plugin.ProfitEngine.OnResultsUpdated -= Refresh;
    }
}
