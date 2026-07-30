using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.Harmony;

// CloneShipRaw copies the source ship through Ship.Store, including status.
// A refit design cloned from an erased design must become a live design row,
// otherwise campaign UI treats the new refit as deleted immediately.
[HarmonyPatch(typeof(PlayerController))]
internal static class PlayerDeletedDesignClonePatch
{
    private static bool loggedClearedDeletedFlag;

    [HarmonyPostfix]
    [HarmonyPatch(
        nameof(PlayerController.CloneShipRaw),
        typeof(Ship),
        typeof(bool),
        typeof(bool),
        typeof(bool),
        typeof(string),
        typeof(Player))]
    private static void CloneShipRawPostfix(Ship from, bool willBeDesign, Ship __result)
    {
        try
        {
            if (!willBeDesign || from == null || __result == null)
                return;

            if (!Safe(() => from.isErased, false) || !Safe(() => __result.isErased, false))
                return;

            Player? owner = Safe(() => __result.player, null) ?? Safe(() => PlayerController.Instance, null);
            if (owner == null || !Safe(() => owner.isMain && !owner.isAi, false))
                return;

            __result.status = VesselEntity.Status.Normal;
            LogClearedDeletedFlagOnce(from, __result);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP refit design clone: failed to clear inherited deleted state. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogClearedDeletedFlagOnce(Ship source, Ship clone)
    {
        if (loggedClearedDeletedFlag)
            return;

        loggedClearedDeletedFlag = true;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP refit design clone: cleared inherited deleted state for new player design '{Safe(() => clone.name, "?")}' cloned from '{Safe(() => source.name, "?")}'.");
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }
}
