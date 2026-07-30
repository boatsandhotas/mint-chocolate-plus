using HarmonyLib;
using UADVanillaPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.Harmony;

// Apply VP campaign naval mobility presets after vanilla parses params.csv.
[HarmonyPatch(typeof(UadGameData))]
internal static class CampaignNavalMobilityBalancePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => CampaignNavalMobilityBalance.ApplyCurrentSetting("game data postprocess", __instance);
}
