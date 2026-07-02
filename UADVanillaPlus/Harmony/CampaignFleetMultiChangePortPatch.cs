using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.Harmony;

// QoL: the campaign Fleet tab lets the player multi-select ships, but vanilla caps
// how many can be sent through the "Change Port" flow at once via the settable int
// field CampaignFleetWindow.maxShipCanChangePortAtOneTime. Native code reads that
// backing field directly, so raising it (rather than patching a property getter)
// lets a whole selected group be ordered to one port in a single Change Port click.
//
// This is a pure UI cap raise — it touches no save/move pipeline of its own, it just
// removes vanilla's group-size limit on its own Change Port order — so it is always on.
[HarmonyPatch(typeof(CampaignFleetWindow))]
internal static class CampaignFleetMultiChangePortPatch
{
    // Vanilla's cap is small (a few ships); 999 is effectively "no limit" for any
    // realistic fleet while staying a sane finite int the native code can compare against.
    private const int RaisedChangePortCap = 999;

    private static bool loggedCapRaise;

    [HarmonyPatch(nameof(CampaignFleetWindow.OnEnable))]
    [HarmonyPostfix]
    private static void PostfixOnEnable(CampaignFleetWindow __instance)
        => RaiseChangePortCap(__instance);

    [HarmonyPatch(nameof(CampaignFleetWindow.Show))]
    [HarmonyPostfix]
    private static void PostfixShow(CampaignFleetWindow __instance)
        => RaiseChangePortCap(__instance);

    [HarmonyPatch(nameof(CampaignFleetWindow.Refresh))]
    [HarmonyPostfix]
    private static void PostfixRefresh(CampaignFleetWindow __instance)
        => RaiseChangePortCap(__instance);

    private static void RaiseChangePortCap(CampaignFleetWindow window)
    {
        // Never throw into the game loop: the Fleet tab refreshes constantly, so a
        // failure here must be a silent no-op that leaves vanilla behavior intact.
        try
        {
            if (window == null)
                return;

            if (window.maxShipCanChangePortAtOneTime >= RaisedChangePortCap)
                return;

            window.maxShipCanChangePortAtOneTime = RaisedChangePortCap;
            LogCapRaiseOnce();
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP multi change-port cap raise failed; leaving vanilla limit intact. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogCapRaiseOnce()
    {
        if (loggedCapRaise)
            return;

        loggedCapRaise = true;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP Fleet tab: raised Change Port group cap to {RaisedChangePortCap} so multi-selected ships can be sent to one port at once.");
    }
}
