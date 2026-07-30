using HarmonyLib;
using UADVanillaPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: apply VP's protection-weight cleanup after vanilla parses
// loaded technology data, keeping CSV files untouched.
[HarmonyPatch(typeof(UadGameData))]
internal static class DesignProtectionWeightBalancePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => ProtectionWeightBalance.ApplyCurrentSetting("game data postprocess", __instance);
}
