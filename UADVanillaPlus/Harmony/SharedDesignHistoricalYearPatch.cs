using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Historical Research makes campaign tech availability deterministic by year.
// Keep the main-menu Shared Designs constructor on that same basis so a design
// saved for 1891 cannot quietly include a vanilla offset-unlocked 1892 tech.
internal static class SharedDesignHistoricalYearPatch
{
    private static int sharedDesignYearScopeDepth;
    private static bool resolvingHistoricalTechYear;
    private static bool loggedActive;
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);

    internal static void BeginSharedDesignYearScope()
    {
        if (ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical)
            return;

        sharedDesignYearScopeDepth++;
    }

    internal static void EndSharedDesignYearScope()
    {
        if (sharedDesignYearScopeDepth > 0)
            sharedDesignYearScopeDepth--;
    }

    internal static bool TryGetHistoricalSharedDesignTechYear(
        TechnologyData? tech,
        bool sort,
        bool doNotUseRandomTechOffset,
        ref int result)
    {
        if (doNotUseRandomTechOffset ||
            resolvingHistoricalTechYear ||
            tech == null ||
            ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical ||
            !IsSharedDesignYearScope())
        {
            return true;
        }

        try
        {
            resolvingHistoricalTechYear = true;
            result = GameManager.GetTechYear(tech, sort, doNotUseRandomTechOffset: true);
            LogActiveOnce();
            return false;
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"tech-year:{SafeTechKey(tech)}:{ex.GetType().Name}",
                $"UADVP shared-design historical years: failed to read deterministic tech year for {SafeTechKey(tech)}. {ex.GetType().Name}: {ex.Message}");
            return true;
        }
        finally
        {
            resolvingHistoricalTechYear = false;
        }
    }

    private static bool IsSharedDesignYearScope()
        => sharedDesignYearScopeDepth > 0 || Safe(() => GameManager.IsSharedDesignConstructor, false);

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            "UADVP shared-design historical years: using deterministic tech years in Shared Designs screen.");
    }

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning(message);
    }

    private static string SafeTechKey(TechnologyData? tech)
        => Safe(() => tech?.name ?? "<null>", "<unavailable>");

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch
        {
            return fallback;
        }
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.RefreshSharedDesign), new[] { typeof(int), typeof(PlayerData), typeof(bool) })]
internal static class SharedDesignHistoricalYearRefreshScopePatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => SharedDesignHistoricalYearPatch.BeginSharedDesignYearScope();

    [HarmonyFinalizer]
    private static void Finalizer()
        => SharedDesignHistoricalYearPatch.EndSharedDesignYearScope();
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ToSharedDesignsConstructor), new[] { typeof(int), typeof(PlayerData), typeof(bool) })]
internal static class SharedDesignHistoricalYearConstructorScopePatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => SharedDesignHistoricalYearPatch.BeginSharedDesignYearScope();

    [HarmonyFinalizer]
    private static void Finalizer()
        => SharedDesignHistoricalYearPatch.EndSharedDesignYearScope();
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.GetTechYear), new[] { typeof(TechnologyData), typeof(bool), typeof(bool) })]
internal static class SharedDesignHistoricalYearGetTechYearPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        TechnologyData t,
        bool sort,
        bool doNotUseRandomTechOffset,
        ref int __result)
        => SharedDesignHistoricalYearPatch.TryGetHistoricalSharedDesignTechYear(
            t,
            sort,
            doNotUseRandomTechOffset,
            ref __result);
}
