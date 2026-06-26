using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Collections.Generic;

namespace GilMaster.Core;

/// <summary>
/// Aetheryte helpers: GatherBuddy's forced-aetheryte overrides plus teleporting.
/// </summary>
public static class AetheryteData
{
    // Ported from GatherBuddy's ForcedAetherytes.ZonesWithoutAetherytes — territories
    // with no usable in-zone aetheryte, so gathering there teleports to a neighbouring
    // one (housing wards, capital sub-zones, the Diadem, Cosmic Exploration zones, etc.).
    public static readonly Dictionary<uint, uint> ZoneToAetheryte = new()
    {
        [128]  = 8,   // Limsa Upper Decks  -> Limsa Lominsa
        [900]  = 8,   // The Endeavor       -> Limsa Lominsa
        [901]  = 70,  // The Diadem         -> Foundation
        [929]  = 70,  // The Diadem         -> Foundation
        [939]  = 70,  // The Diadem         -> Foundation
        [399]  = 75,  // Dravanian Hinterlands -> Idyllshire
        [133]  = 2,   // Old Gridania       -> New Gridania
        [339]  = 8,   // Mist               -> Limsa Lominsa
        [340]  = 2,   // Lavender Beds       -> New Gridania
        [341]  = 9,   // The Goblet          -> Ul'dah
        [641]  = 111, // Shirogane            -> Kugane
        [1073] = 181, // Elysion              -> Base Omicron
        [1237] = 175, // Sinus Ardorum        -> Bestway Burrows
        [1291] = 175, // Phaenna              -> Bestway Burrows
        [1310] = 175, // Oizys                -> Bestway Burrows
        [1319] = 175, // Auxesia              -> Bestway Burrows
    };

    /// <summary>Teleport to an aetheryte by its Aetheryte-sheet RowId. Safe no-op on failure.</summary>
    public static unsafe bool Teleport(uint aetheryteId)
    {
        if (aetheryteId == 0) return false;
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return false;
            return telepo->Teleport(aetheryteId, 0);
        }
        catch
        {
            return false;
        }
    }
}
