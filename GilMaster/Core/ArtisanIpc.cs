using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

/// <summary>
/// Thin bridge to the Artisan crafting plugin over Dalamud IPC.
///
/// Artisan is the community-standard crafting automation plugin (battle-tested
/// MCTS solver + endurance auto-craft). Rather than reimplement a fragile in-game
/// craft loop, GilMaster hands the resolved queue to Artisan when it's installed
/// and lets Artisan execute it. If Artisan isn't present we fall back to the
/// built-in CraftQueueExecutor.
///
/// Verified IPC surface (PunishXIV/Artisan main):
///   Artisan.CraftItem            RegisterAction&lt;ushort recipeId, int quantity, object&gt;
///   Artisan.IsBusy               RegisterFunc&lt;bool&gt;
///   Artisan.GetEnduranceStatus   RegisterFunc&lt;bool&gt;
///   Artisan.SetEnduranceStatus   RegisterAction&lt;bool, object&gt;
///   Artisan.GetStopRequest       RegisterFunc&lt;bool&gt;
///   Artisan.SetStopRequest       RegisterAction&lt;bool, object&gt;
///   Artisan.IsListRunning        RegisterFunc&lt;bool&gt;
/// </summary>
public sealed class ArtisanIpc
{
    private const string InternalName = "Artisan";

    private readonly ICallGateSubscriber<ushort, int, object> _craftItem;
    private readonly ICallGateSubscriber<bool>                _isBusy;
    private readonly ICallGateSubscriber<bool>                _isListRunning;
    private readonly ICallGateSubscriber<bool, object>        _setStopRequest;
    private readonly ICallGateSubscriber<bool>                _getEndurance;
    private readonly ICallGateSubscriber<bool, object>        _setEndurance;

    public ArtisanIpc()
    {
        var pi = Service.PluginInterface;
        _craftItem      = pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
        _isBusy         = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        _isListRunning  = pi.GetIpcSubscriber<bool>("Artisan.IsListRunning");
        _setStopRequest = pi.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest");
        _getEndurance   = pi.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
        _setEndurance   = pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus");
    }

    /// <summary>True when Artisan is installed and currently loaded.</summary>
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

    /// <summary>Artisan is mid-craft / processing.</summary>
    public bool IsBusy => Try(() => _isBusy.InvokeFunc());

    /// <summary>Artisan is working through a crafting list.</summary>
    public bool IsListRunning => Try(() => _isListRunning.InvokeFunc());

    /// <summary>True if Artisan is doing anything we should wait on.</summary>
    public bool IsActive => IsBusy || IsListRunning;

    /// <summary>
    /// Queue every entry (leaves-first) into Artisan and let it craft them.
    /// Each CraftItem call appends to Artisan's run; intermediates are crafted
    /// before the items that consume them because Entries is already ordered
    /// sub-components-first.
    /// </summary>
    /// <returns>The number of entries handed off, or -1 on failure.</returns>
    public int CraftAll(IReadOnlyList<CraftQueueEntry> entries)
    {
        if (!IsAvailable) return -1;
        var sent = 0;
        try
        {
            foreach (var e in entries)
            {
                if (e.RecipeId == 0 || e.QuantityToCraft <= 0) continue;
                _craftItem.InvokeAction((ushort)e.RecipeId, e.QuantityToCraft);
                sent++;
            }
            return sent;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Artisan.CraftItem IPC failed");
            return sent > 0 ? sent : -1;
        }
    }

    /// <summary>Craft a single recipe N times via Artisan.</summary>
    public bool CraftItem(uint recipeId, int quantity)
    {
        if (!IsAvailable || recipeId == 0 || quantity <= 0) return false;
        try { _craftItem.InvokeAction((ushort)recipeId, quantity); return true; }
        catch (Exception ex) { Service.Log.Warning(ex, "Artisan.CraftItem IPC failed"); return false; }
    }

    /// <summary>Ask Artisan to stop the current craft/list, and clear endurance.</summary>
    public void Stop()
    {
        Try<object?>(() => { _setStopRequest.InvokeAction(true); return null; });
        if (Try(() => _getEndurance.InvokeFunc()))
            Try<object?>(() => { _setEndurance.InvokeAction(false); return null; });
    }

    private static T Try<T>(Func<T> fn)
    {
        try { return fn(); } catch { return default!; }
    }
}
