using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

/// <summary>
/// Bridge to Allagan Tools (InventoryTools) — the community inventory tracker that
/// indexes items across your active character AND all their retainers. GilMaster uses
/// it to answer "do I actually own these materials?" including mats parked on retainers,
/// so the Have / "What can I make?" / missing-material checks reflect your whole stash.
///
/// Verified IPC surface (Critical-Impact/InventoryTools):
///   AllaganTools.GetCharactersOwnedByActive  Func&lt;bool includeOwner, HashSet&lt;ulong&gt;&gt;
///   AllaganTools.ItemCount                   Func&lt;uint itemId, ulong characterId, int inventoryType, uint&gt;
///   (inventoryType == -1 sums every container for that character/retainer)
/// </summary>
public sealed class AllaganToolsBridge
{
    private const string InternalName = "InventoryTools";

    private readonly ICallGateSubscriber<bool, HashSet<ulong>>   _charsOwned;
    private readonly ICallGateSubscriber<uint, ulong, int, uint> _itemCount;

    // The owned-character set rarely changes; cache it briefly so a tight
    // "what can I make?" scan doesn't re-query it for every single item.
    private HashSet<ulong>? _cachedChars;
    private DateTime _charsCachedAt;

    public AllaganToolsBridge()
    {
        var pi = Service.PluginInterface;
        _charsOwned = pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");
        _itemCount  = pi.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
    }

    /// <summary>True if Allagan Tools is installed and loaded.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return Service.PluginInterface.InstalledPlugins
                    .Any(p => string.Equals(p.InternalName, InternalName, StringComparison.OrdinalIgnoreCase) && p.IsLoaded);
            }
            catch { return false; }
        }
    }

    private HashSet<ulong>? OwnedChars()
    {
        if (_cachedChars != null && (DateTime.UtcNow - _charsCachedAt).TotalSeconds < 5)
            return _cachedChars;
        try
        {
            _cachedChars = _charsOwned.InvokeFunc(true);
            _charsCachedAt = DateTime.UtcNow;
            return _cachedChars;
        }
        catch { return null; } // IPC not registered → Allagan Tools unavailable
    }

    /// <summary>
    /// Total quantity of <paramref name="itemId"/> across the active character and all
    /// their retainers. Returns -1 if Allagan Tools is unavailable or the call fails, so
    /// callers can fall back to the game's own inventory count.
    /// </summary>
    public long CountOwned(uint itemId)
    {
        var chars = OwnedChars();
        if (chars == null || chars.Count == 0) return -1;
        try
        {
            long total = 0;
            foreach (var c in chars)
                total += _itemCount.InvokeFunc(itemId, c, -1);
            return total;
        }
        catch { return -1; }
    }
}
