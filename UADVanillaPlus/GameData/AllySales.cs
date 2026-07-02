using System;
using System.Collections.Generic;
using Il2Cpp;

namespace UADVanillaPlus.GameData;

// Alliance test + war/break detection + ally buyable-design enumeration for "buy ships from an
// allied major". Reuses CampaignInvasionActions.IsAllied (the canonical Relation accessor) and
// ExtraGameData.MainPlayer. Runtime-only.
internal static class AllySales
{
    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    // Allied MAJOR sellers: a non-human, non-defeated major AI currently allied to the player.
    internal static List<Player> AlliedSellers()
    {
        var result = new List<Player>();
        var data = CampaignController.Instance?.CampaignData;
        Player? human = ExtraGameData.MainPlayer();
        if (data?.PlayersMajor == null || human == null) return result;

        foreach (Player major in data.PlayersMajor)
        {
            if (major == null || major == human) continue;
            if (!Safe(() => major.isMajor && !major.isMain && major.isAi, false)) continue; // real major AI
            if (Safe(() => major.IsDisabled(), false)) continue;                            // skip defeated
            if (!CampaignInvasionActions.IsAllied(human, major)) continue;                  // current alliance
            result.Add(major);
        }
        return result;
    }

    // Buyable BASE-class designs for one seller (exclude refits + erased).
    internal static List<Ship> BuyableDesigns(Player? seller)
    {
        var result = new List<Ship>();
        if (seller == null) return result;
        foreach (Ship d in SafeShipList(Safe<Il2CppSystem.Collections.Generic.IEnumerable<Ship>?>(() => seller.designs, null)))
        {
            bool baseClass = Safe(() => d.isDesign && !d.isRefitDesign, false);
            bool erased = Safe(() => d.isErased, true);
            if (baseClass && !erased) result.Add(d);
        }
        return result;
    }

    // Classify a broken alliance for an open-order seller who is no longer allied this turn.
    // true => broke into WAR => seize the hull; false => peaceful dissolution => honor the contract.
    internal static bool AllianceBrokeIntoWar(Player? seller)
    {
        Player? human = ExtraGameData.MainPlayer();
        var relations = CampaignController.Instance?.CampaignData?.Relations;
        if (human == null || seller == null || relations == null) return false;
        try
        {
            Relation? rel = RelationExt.Between(relations, human, seller); // null if no entry
            if (rel == null) return false;                                 // no relation => not war => honor
            return Safe(() => rel.isWar, false);
        }
        catch { return false; }
    }

    private static List<Ship> SafeShipList(Il2CppSystem.Collections.Generic.IEnumerable<Ship>? ships)
    {
        var result = new List<Ship>();
        if (ships == null) return result;
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Ship>(ships); // materialize before foreach
            foreach (Ship s in list) if (s != null) result.Add(s);
        }
        catch { }
        return result;
    }
}
