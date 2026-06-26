using System.Collections.Generic;

namespace GilMaster.Core;

public sealed record LevelingTip(
    string Category,
    string Activity,
    string Details,
    int MinLevel,
    int MaxLevel
);

// Provides leveling guidance for DoH and DoL jobs.
// Data here is general knowledge — not dynamically computed.
public sealed class LevelingAdvisor
{
    private static readonly List<LevelingTip> Tips =
    [
        // DoH leveling tiers
        new("DoH", "Levequests", "Pick up leves from the nearest leve counter. HQ items give 2× XP. Prioritize delivery leves over fieldcraft.", 1, 100),
        new("DoH", "Grand Company Turn-ins", "Complete your daily GC supply & provisioning mission. Turn in HQ for max XP. Check your GC officer daily.", 1, 100),
        new("DoH", "Tribal Quests", "Ixal quests (1-50), Moogle quests (50-60), Dwarf quests (70-80), Loporrits (80-90), Pelupelu (90-100) give huge XP chunks.", 1, 100),
        new("DoH", "Crafters' Scrip Exchange", "Collectables grind for white/purple scrips gives excellent XP from ~50+ and funds gear upgrades.", 50, 100),
        new("DoH", "First-time Recipe Bonus", "Craft each recipe for the first time for a big bonus. Open your crafting log and work through unchecked items.", 1, 100),

        // Low-level DoH
        new("DoH (1-15)", "Craft copper ingots / bronze rivets", "Miner mats, fast recipe, very cheap. Use as filler between quests.", 1, 15),
        new("DoH (15-30)", "Craft Hammond (Iron/Electrum)", "Level up synthesis until GC leves open. Use GC leves with HQ items from lv 20.", 15, 30),
        new("DoH (30-50)", "Housing/furnishing items", "High demand, good XP. Blacksmith/Carpenter excel here. Pair with GC leves.", 30, 50),
        new("DoH (50-60)", "Ishgard Restoration", "Available from Ishgard (Firmament) once unlocked. Excellent XP, good scrips.", 50, 60),
        new("DoH (60-80)", "Custom Deliveries", "Unlock clients in Idyllshire, Rhalgr's Reach, etc. Turn in weekly for purple scrips + XP.", 60, 80),
        new("DoH (80-90)", "Endwalker Collectables", "Farm collectables in Radz-at-Han and Old Sharlayan for white scrips and XP.", 80, 90),
        new("DoH (90-100)", "Dawntrail Collectables + Pelupelu Tribe", "Pelupelu quests give the best daily XP. Pair with Dawntrail collectables.", 90, 100),

        // DoL leveling
        new("DoL", "Levequests", "Miner/Botanist leves give XP. HQ items give 2×. Field/Woodland leves are easiest.", 1, 100),
        new("DoL", "Grand Company Turn-ins", "Gathering supply missions refill daily. HQ items critical for max XP at higher levels.", 1, 100),
        new("DoL", "Tribal Quests", "Ixal (1-50), Vath (50-60), Namazu (60-70), Qitari (70-80), Arkasodara (80-90), Pelupelu (90-100).", 1, 100),
        new("DoL", "Unspoiled/Legendary Nodes", "Spawn at specific Eorzea Times. Time these to collect rare mats — sell on MB for big gil.", 50, 100),
        new("DoL (1-50)", "Mine copper, iron in Central Thanalan / Central Shroud", "Easy gathering rotation. Level Mining and Botany together to share leve bonuses.", 1, 50),
        new("DoL (50-60)", "Coerthas / Dravanian Forelands nodes", "Cluster farming gives gathering scrips + fast XP.", 50, 60),
        new("DoL (60-80)", "Stormblood/Shadowbringers timed nodes", "Farm high-tier skybuilders materials once Ishgard Restoration unlocks.", 60, 80),
        new("DoL (80-90)", "Endwalker zones — Labyrinthos/Thavnair", "Daily turn-ins + tribal quests hit hard here.", 80, 90),
        new("DoL (90-100)", "Dawntrail nodes + Pelupelu", "Dawntrail gathering nodes cap quickly. Pelupelu gives the most daily XP.", 90, 100),
    ];

    // Returns tips relevant to the given level for the specified role (DoH or DoL).
    public IReadOnlyList<LevelingTip> GetTips(int level, bool isDoh)
    {
        var role = isDoh ? "DoH" : "DoL";
        var result = new List<LevelingTip>();

        foreach (var tip in Tips)
        {
            if (level < tip.MinLevel || level > tip.MaxLevel) continue;
            if (!tip.Category.StartsWith(role) && tip.Category != role) continue;
            result.Add(tip);
        }

        return result;
    }

    // Returns the top 3 priority activities for the given level.
    public IReadOnlyList<LevelingTip> GetTopPriorities(int level, bool isDoh)
    {
        var all = GetTips(level, isDoh);
        // Prioritize tribal quests and GC turn-ins first, then everything else
        var ordered = new List<LevelingTip>();
        foreach (var tip in all)
        {
            if (tip.Activity.Contains("Tribal") || tip.Activity.Contains("Grand Company"))
                ordered.Insert(0, tip);
            else
                ordered.Add(tip);
        }
        return ordered.Count > 3 ? ordered[..3] : ordered;
    }
}
