using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CAction = Craftimizer.Simulator.Actions.ActionType;

namespace GilMaster.Core;

/// <summary>
/// Rotations the simulator has learned for specific recipes, persisted to disk so they
/// survive restarts. When the Sim bot finds a good (ideally full-HQ) rotation for a recipe,
/// it stores it here; the live crafter seeds from it instead of solving cold — so what the
/// sim discovers improves real crafts, run after run.
/// </summary>
public static class RotationCache
{
    private static readonly Dictionary<uint, CAction[]> Cache = new();

    private static string CachePath =>
        Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "rotations.json");

    public static int Count { get { lock (Cache) return Cache.Count; } }

    public static void Store(uint recipeId, IEnumerable<CAction> actions)
    {
        if (recipeId == 0) return;
        var arr = actions.ToArray();
        if (arr.Length == 0) return;
        lock (Cache) Cache[recipeId] = arr;
    }

    public static CAction[]? Get(uint recipeId)
    {
        lock (Cache) return Cache.TryGetValue(recipeId, out var a) ? a : null;
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
            var data = JsonSerializer.Deserialize<Dictionary<uint, int[]>>(File.ReadAllText(CachePath));
            if (data == null) return;
            lock (Cache)
            {
                Cache.Clear();
                foreach (var (k, v) in data)
                    Cache[k] = v.Select(i => (CAction)i).ToArray();
            }
            Service.Log.Information($"[GilMaster] Loaded {Count} learned rotations.");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to load rotation cache"); }
    }

    public static void Save()
    {
        try
        {
            Dictionary<uint, int[]> data;
            lock (Cache)
                data = Cache.ToDictionary(kv => kv.Key, kv => kv.Value.Select(a => (int)a).ToArray());
            File.WriteAllText(CachePath, JsonSerializer.Serialize(data));
            Service.Log.Information($"[GilMaster] Saved {data.Count} learned rotations.");
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to save rotation cache"); }
    }
}
