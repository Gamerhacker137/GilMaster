using System.Collections.Generic;
using System.Linq;
using CAction = Craftimizer.Simulator.Actions.ActionType;

namespace GilMaster.Core;

/// <summary>
/// Rotations the simulator has learned for specific recipes. When the Sim bot finds a
/// good (ideally full-HQ) rotation for a recipe, it stores it here; the live crafter then
/// seeds from it instead of solving cold — so what the sim discovers improves real crafts.
/// In-memory for the session (the sim repopulates it each run).
/// </summary>
public static class RotationCache
{
    private static readonly Dictionary<uint, CAction[]> Cache = new();

    public static int Count
    {
        get { lock (Cache) return Cache.Count; }
    }

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

    public static void Clear() { lock (Cache) Cache.Clear(); }
}
