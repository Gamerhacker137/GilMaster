using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CAction = Craftimizer.Simulator.Actions.ActionType;

namespace GilMaster.Core;

/// <summary>
/// Rotations the solver has found for specific recipes, persisted to disk so they survive
/// restarts. When the Sim bot (or a live craft's Raphael opening) finds a good rotation it is
/// stored here; the live crafter seeds from it instead of solving cold — so what's discovered
/// improves real crafts run after run.
///
/// Keyed by recipe AND the crafter stats it was solved for: a rotation tuned for one stat set
/// (gear, food) may not complete or may be suboptimal at another, so <see cref="Get"/> only
/// returns an exact-stat match and otherwise misses (forcing a fresh solve).
/// </summary>
public static class RotationCache
{
    private sealed record Entry(int[] Actions, int Cms, int Ctrl, int Cp, int Level);

    private static readonly Dictionary<uint, Entry> Cache = new();

    private static string CachePath =>
        Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "rotations.json");

    public static int Count { get { lock (Cache) return Cache.Count; } }

    public static void Store(uint recipeId, IEnumerable<CAction> actions, int cms, int ctrl, int cp, int level)
    {
        if (recipeId == 0) return;
        var arr = actions.Select(a => (int)a).ToArray();
        if (arr.Length == 0) return;
        lock (Cache) Cache[recipeId] = new Entry(arr, cms, ctrl, cp, level);
    }

    public static CAction[]? Get(uint recipeId, int cms, int ctrl, int cp, int level)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(recipeId, out var e)) return null;
            // Only replay a rotation solved at the SAME stats — different gear/food changes the
            // math, so a stale-stat rotation could fail to complete or waste CP.
            if (e.Cms != cms || e.Ctrl != ctrl || e.Cp != cp || e.Level != level) return null;
            return e.Actions.Select(i => (CAction)i).ToArray();
        }
    }

    public static void Clear()
    {
        lock (Cache) Cache.Clear();
        Save();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<uint, Entry>>(File.ReadAllText(CachePath));
            if (data == null) return;
            lock (Cache)
            {
                Cache.Clear();
                foreach (var (k, v) in data) Cache[k] = v;
            }
            Service.Log.Information($"[GilMaster] Loaded {Count} learned rotations.");
        }
        // Old format (recipeId -> int[]) won't deserialize into Entry — discard and rebuild.
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to load rotation cache (rebuilding)"); }
    }

    public static void Save()
    {
        try
        {
            Dictionary<uint, Entry> data;
            lock (Cache) data = new Dictionary<uint, Entry>(Cache);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(data));
            Service.Log.Information($"[GilMaster] Saved {data.Count} learned rotations.");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to save rotation cache"); }
    }
}
