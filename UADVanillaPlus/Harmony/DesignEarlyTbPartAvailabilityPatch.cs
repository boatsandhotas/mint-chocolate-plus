using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// The loaded-data needunlock rewrites are useful for data/tooltips, but the
// constructor carousel can still admit these early TB parts through its active
// availability layer. Enforce the same VP gates at Ship.IsPartAvailable.
[HarmonyPatch(typeof(Ship))]
internal static class DesignEarlyTbPartAvailabilityPatch
{
    private const int EarlyTbDualFunnelUnlockYear = 1894;
    private const int OversizedTbTowerUnlockYear = 1900;

    private static readonly HashSet<string> EarlyTbDualFunnels = new(StringComparer.OrdinalIgnoreCase)
    {
        "torpedo_funnel_3",
        "torpedo_funnel_3_big",
        "torpedo_funnel_3_bigger",
    };

    private static readonly HashSet<string> OversizedNormalTbTowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "torpedo_tower_main_2_small",
        "torpedo_tower_main_2",
        "torpedo_tower_main_3",
        "torpedo_tower_main_3_big",
    };

    private static readonly HashSet<string> LoggedBlocks = new(StringComparer.OrdinalIgnoreCase);
    private static bool loggedUnknownYear;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Ship.IsPartAvailable), typeof(PartData), typeof(Player), typeof(ShipType), typeof(Ship))]
    private static void IsPartAvailablePostfix(PartData part, Player player, ShipType shipType, Ship ship, ref bool __result)
    {
        if (!__result || part == null || !IsNormalTbContext(part, shipType, ship))
            return;

        string partId = PartId(part);
        if (EarlyTbDualFunnels.Contains(partId))
        {
            TryBlockEarlyPart(
                part,
                ship,
                ModSettings.HullSpeedAdjustmentEnabled,
                EarlyTbDualFunnelUnlockYear,
                "dual-funnel",
                "normal TB dual funnels",
                ref __result);
            return;
        }

        if (OversizedNormalTbTowers.Contains(partId))
        {
            TryBlockEarlyPart(
                part,
                ship,
                ModSettings.HullWeightAdjustmentEnabled,
                OversizedTbTowerUnlockYear,
                "oversized-tower",
                "oversized normal TB towers",
                ref __result);
        }
    }

    private static void TryBlockEarlyPart(
        PartData part,
        Ship ship,
        bool optionEnabled,
        int unlockYear,
        string ruleKey,
        string ruleLabel,
        ref bool available)
    {
        if (!optionEnabled)
            return;

        if (!TryResolveEffectiveYear(ship, out int year))
        {
            LogUnknownYear();
            return;
        }

        if (year >= unlockYear)
            return;

        available = false;
        LogFirstBlock(ruleKey, ruleLabel, part, year, unlockYear);
    }

    private static bool IsNormalTbContext(PartData part, ShipType? shipType, Ship? ship)
    {
        string type = AiDesignCompetitiveness.NormalizeShipType(shipType);
        if (!string.Equals(type, "TB", StringComparison.OrdinalIgnoreCase))
            type = AiDesignCompetitiveness.NormalizeShipType(Safe(() => ship?.shipType, null));

        if (!string.Equals(type, "TB", StringComparison.OrdinalIgnoreCase))
            return false;

        string partId = PartId(part);
        return EarlyTbDualFunnels.Contains(partId) || OversizedNormalTbTowers.Contains(partId);
    }

    private static bool TryResolveEffectiveYear(Ship? ship, out int year)
    {
        year = 0;

        if (GameManager.IsSharedDesignConstructor)
            return SharedDesignYearInputPatch.TryGetCurrentSharedDesignYear(out year);

        if (TryReadYear(() => ship == null ? 0 : ship.dateCreated.AsDate().Year, out year))
            return true;

        if (TryReadYear(() => G.ui?.sharedDesignYear ?? 0, out year))
            return true;

        return TryReadYear(() => CampaignController.Instance?.CurrentDate.AsDate().Year ?? 0, out year);
    }

    private static bool TryReadYear(Func<int> read, out int year)
    {
        year = 0;
        try
        {
            int value = read();
            if (value >= 1890 && value <= 1960)
            {
                year = value;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string PartId(PartData part)
    {
        string name = SafeString(() => part.name);
        return string.IsNullOrWhiteSpace(name) ? SafeString(() => part.nameUi) : name;
    }

    private static void LogFirstBlock(string ruleKey, string ruleLabel, PartData part, int year, int unlockYear)
    {
        string key = $"{ruleKey}|{year}";
        if (!LoggedBlocks.Add(key))
            return;

        string partName = SafeString(() => part.nameUi);
        if (string.IsNullOrWhiteSpace(partName))
            partName = PartId(part);

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP early TB availability: hiding {ruleLabel} before {unlockYear} in {ContextLabel()} constructor. year={year} firstBlocked={LogToken(partName)}.");
    }

    private static void LogUnknownYear()
    {
        if (loggedUnknownYear)
            return;

        loggedUnknownYear = true;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            "UADVP early TB availability: could not resolve effective design year; leaving early TB part gates to vanilla/data unlocks.");
    }

    private static string ContextLabel()
        => GameManager.IsSharedDesignConstructor ? "shared-design" : GameManager.IsConstructor ? "design" : "non-constructor";

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "?" : value.Trim().Replace(' ', '_');
}
