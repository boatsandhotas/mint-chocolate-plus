using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Apply the player's preferred battle-start ship settings once combat begins. BattleStarted
// fires after deploy with ships + divisions populated, so the settings stick.
[HarmonyPatch(typeof(GameManager), nameof(GameManager.BattleStarted))]
internal static class BattleStartDefaultsPatch
{
    [HarmonyPostfix]
    private static void Postfix() => BattleStartDefaults.Apply();
}
