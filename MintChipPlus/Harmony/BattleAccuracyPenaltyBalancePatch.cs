using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.Harmony;

// Patch intent: tone down extreme design-side accuracy penalties and crew
// training swings at data parse time. This keeps the actual battle accuracy
// path identical to vanilla: it reads already-parsed curves and pays no
// MC-specific per-shot cost.
[HarmonyPatch(typeof(StatData), nameof(StatData.PostProcess))]
internal static class BattleAccuracyPenaltyBalancePatch
{
    [HarmonyPrefix]
    private static void PrefixPostProcess(StatData __instance)
    {
        AccuracyPenaltyBalance.PrepareStatForVanillaPostProcess(__instance);
    }
}

[HarmonyPatch(typeof(UadGameData), "PostProcessAll")]
internal static class CrewTrainingAccuracyBalancePatch
{
    [HarmonyPostfix]
    private static void PostfixPostProcessAll()
    {
        AccuracyPenaltyBalance.ApplyLoadedCrewTrainingLevels(ModSettings.DesignAccuracyPenaltyMode, "game data load");
        AccuracyPenaltyBalance.ApplyLoadedDamageStateParams(ModSettings.DesignAccuracyPenaltyMode, "game data load");
    }
}
