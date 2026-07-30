using HarmonyLib;
using UADVanillaPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: apply VP's crew and early TB tower weight cleanup after vanilla
// loads and post-processes game data, under the existing Hull Weight Adjustment
// toggle.
[HarmonyPatch(typeof(UadGameData))]
internal static class DesignShipWeightBalancePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => ShipWeightBalance.ApplyCurrentSetting("game data postprocess", __instance);
}
