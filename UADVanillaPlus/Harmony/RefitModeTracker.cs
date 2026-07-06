using HarmonyLib;
using Il2Cpp;

namespace UADVanillaPlus.Harmony;

// "Is the ship designer currently in refit mode" — read LIVE from the game's own Ui.isConstructorRefitMode
// field. The game sets it true when entering refit (Ui.RefitShip) and false on every exit; its OWN logic
// depends on that being correct, so unlike a mod-maintained flag it cannot get stuck true (an earlier
// attempt toggled our own bool on Ui.ExitFromRefitMode, which doesn't reliably fire — the flag stuck and
// wrongly un-blocked new builds).
//
// This is the complete discriminator for the foreign-purchase "refit-yes / build-no" gate:
//   refit COMMIT  -> isConstructorRefitMode == true  -> allow
//   new build (base OR a saved refit design, from the design list) -> false -> block
//
// Ui has no static singleton accessor here, so we capture the live instance from Ui.Update (refreshed
// every frame, so the reference is never stale) and read the field on demand.
internal static class RefitModeTracker
{
    private static Ui? ui;

    internal static void Capture(Ui instance)
    {
        if (instance != null) ui = instance;
    }

    // Fails to FALSE (block) if the instance is missing or the read throws — the safe direction.
    internal static bool IsInRefitEditor()
    {
        try { return ui != null && ui.isConstructorRefitMode; }
        catch { return false; }
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.Update))]
internal static class RefitModeCaptureOnUpdatePatch
{
    [HarmonyPostfix]
    private static void Postfix(Ui __instance) => RefitModeTracker.Capture(__instance);
}

[HarmonyPatch(typeof(Ui), nameof(Ui.RefitShip))]
internal static class RefitModeCaptureOnRefitPatch
{
    // Capture immediately on refit entry too, so the instance is available the very moment it matters.
    [HarmonyPostfix]
    private static void Postfix(Ui __instance) => RefitModeTracker.Capture(__instance);
}
