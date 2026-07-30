using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.Harmony;

// Patch intent: rewrite loaded GunData.damageMod values at data-load time so
// vanilla shell damage resolution naturally uses MC's selected shell curve.
[HarmonyPatch(typeof(UadGameData))]
internal static class RealisticShellDamageBalanceDataPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => RealisticShellDamageBalance.ApplyCurrentSetting("game data postprocess", __instance);
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnLeaveState))]
internal static class RealisticShellDamageBalanceLeaveStatePatch
{
    [HarmonyPostfix]
    private static void PostfixLeaveState(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            RealisticShellDamageBalance.ApplyCurrentSetting("battle state leave", allowDuringBattleTransition: true);
    }
}
