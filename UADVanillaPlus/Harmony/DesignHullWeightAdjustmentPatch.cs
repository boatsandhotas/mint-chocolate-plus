using HarmonyLib;
using UADVanillaPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: apply VP's hull mass ratio caps after vanilla loads and
// post-processes part data, so CSV files stay untouched.
[HarmonyPatch(typeof(UadGameData))]
internal static class DesignHullWeightAdjustmentPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => HullWeightAdjustment.ApplyCurrentSetting("game data postprocess", __instance);
}
