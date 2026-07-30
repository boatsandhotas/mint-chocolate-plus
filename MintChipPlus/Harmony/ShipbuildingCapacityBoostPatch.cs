using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Balance: vanilla total shipbuilding capacity (derived from home port capacity) is
// restrictively low — a single ~40k-ton design can eat a third of a ~120k limit. Scale
// the limit for ALL players (human + AI) by a configurable multiplier. ShipbuildingCapacityLimit
// is the single accessor every caller uses (UI, build gating, overcapacity penalty, AI
// scrap targets), so a postfix here covers the whole game uniformly.
[HarmonyPatch(typeof(Player), nameof(Player.ShipbuildingCapacityLimit))]
internal static class ShipbuildingCapacityBoostPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref float __result)
    {
        try
        {
            float multiplier = ModSettings.ShipbuildingCapacityBoostMultiplier;
            if (multiplier > 1f && __result > 0f)
                __result *= multiplier;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC shipyard capacity boost failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
