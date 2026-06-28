using System;

namespace GilMaster.Core;

/// <summary>
/// Bridge to GatherBuddy / GatherBuddyReborn. GBR's only "gather this specific item"
/// entry point is its <c>/gather &lt;item name&gt;</c> chat command (its IPC surface only
/// toggles AutoGather and identifies items), so we drive it through Dalamud's command
/// dispatcher. Works with either the original GatherBuddy or the Reborn fork since both
/// register the same command.
/// </summary>
public static class GatherBuddyBridge
{
    private const string GatherCommand = "/gather";

    /// <summary>True if a plugin registered the /gather command (GatherBuddy[Reborn]).</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return Service.CommandManager.Commands.ContainsKey(GatherCommand); }
            catch { return false; }
        }
    }

    /// <summary>Ask GatherBuddy to gather a named item. Returns false if it isn't available.</summary>
    public static bool Gather(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName) || !IsAvailable) return false;
        try
        {
            Service.CommandManager.ProcessCommand($"{GatherCommand} {itemName.Trim()}");
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "GatherBuddy /gather command failed");
            return false;
        }
    }
}
