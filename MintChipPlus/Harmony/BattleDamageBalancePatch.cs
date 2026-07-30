using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.Harmony;

// Patch intent: apply MC's blunt global battle damage multiplier at game-data
// load time, not while individual shells or torpedoes resolve damage.
[HarmonyPatch(typeof(UadGameData))]
internal static class BattleDamageBalanceDataPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => BattleDamageBalance.ApplyCurrentSetting("game data postprocess", __instance);
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnLeaveState))]
internal static class BattleDamageBalanceLeaveStatePatch
{
    [HarmonyPostfix]
    private static void PostfixLeaveState(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleDamageBalance.ApplyCurrentSetting("battle state leave", allowDuringBattleTransition: true);
    }
}
