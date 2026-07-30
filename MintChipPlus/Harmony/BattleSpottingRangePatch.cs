using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Battle detection adds the target's visibility range to the spotter's
// spotting range. MC only buffs the spotter side, leaving target visibility,
// weather, smoke, and reveal behavior on the vanilla path.
[HarmonyPatch(typeof(Ship), nameof(Ship.GetSpottingRange))]
internal static class BattleSpottingRangePatch
{
    private static int lastLoggedMode = int.MinValue;

    [HarmonyPostfix]
    private static void Postfix(ref float __result)
    {
        if (!GameManager.IsBattle)
            return;

        ModSettings.BattleSpottingRangeMode mode = ModSettings.BattleSpottingRange;
        float multiplier = ModSettings.BattleSpottingRangeMultiplier(mode);
        if (Mathf.Abs(multiplier - 1f) <= 0.001f)
            return;

        __result *= multiplier;
        LogAppliedMode(mode, multiplier);
    }

    private static void LogAppliedMode(ModSettings.BattleSpottingRangeMode mode, float multiplier)
    {
        int modeKey = (int)mode;
        if (lastLoggedMode == modeKey)
            return;

        lastLoggedMode = modeKey;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC battle spotting: applying {ModSettings.BattleSpottingRangeModeText(mode)} spotter range multiplier ({multiplier:0.##}x).");
    }
}
