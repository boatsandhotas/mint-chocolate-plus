using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;

namespace MintChipPlus.Harmony;

// "Reinforce with Navy" — THE real lever (confirmed by the driver dump 0.5.292): the battle is driven by
// Player.ArmyForceForProvince (the DISTANCE-ATTENUATED projected force at the target), NOT PlayerArmyForce.
// Postfix it to add the committed reinforcement, which boosts BOTH the loss/advance math AND the native popup
// display (which reads the same value) in one shot. Fires on every call, so ProjBonusFor bails fast.
[HarmonyPatch(typeof(Player), nameof(Player.ArmyForceForProvince))]
internal static class LandInvasionSupportProjPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance, Province province, ref float __result)
    {
        try { __result += LandInvasionSupport.ProjBonusFor(__instance, province); }
        catch { }
    }
}


// TEMPORARY debug hotkeys until the "Reinforce with Navy" button + circle UI land, so we can verify the
// tonnage -> force -> outcome pipeline end to end:
//   Ctrl+Shift+N  toggle a commitment on the player's current attacking battle (logs tonnage + bonus)
//   Ctrl+Shift+M  clear all commitments
[HarmonyPatch(typeof(Cam), "Update")]
internal static class LandInvasionSupportHotkeys
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            if (!(Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift))) return;
            if (Input.GetKeyDown(KeyCode.N)) CommitCurrent();
            else if (Input.GetKeyDown(KeyCode.M)) LandInvasionSupport.ClearAll();
            else if (Input.GetKeyDown(KeyCode.I)) LandInvasionSupport.DumpBattleDrivers(); // dump all advance-driver candidates
        }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC_REINFORCE hotkey err {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void CommitCurrent()
    {
        try
        {
            Player? main = PlayerController.Instance;
            if (main == null) { LandInvasionSupport.Log("commit: no main player"); return; }

            // Prefer the battle UNDER THE CURSOR (so you can pick the invasion / each defense); fall back to
            // the first battle you're in if you're not hovering one.
            ProvinceBattle? found = CampaignGlobeVisualPatch.CurrentHoverBattle;
            if (found == null || LandInvasionSupport.PlayerSideInBattle(found) == null)
            {
                found = null;
                var battles = ProvinceBattleManager.Battles;
                if (battles != null)
                    foreach (var kv in battles)
                        if (kv.Value != null && LandInvasionSupport.PlayerSideInBattle(kv.Value) != null) { found = kv.Value; break; }
            }

            if (found == null) { LandInvasionSupport.Log("commit: hover a land battle you're attacking or defending, then press Ctrl+Shift+N"); return; }

            bool added = LandInvasionSupport.ToggleCommitment(found);
            CampaignGlobeVisualPatch.InvalidateBattleArrows(); // force the circle to (re)draw now
            float t = LandInvasionSupport.MeasureTonnage(found);
            LandInvasionSupport.Log(
                $"commit toggle -> {(added ? "COMMITTED" : "removed")}; coast tonnage={t:0} troops=+{LandInvasionSupport.SoldiersFor(found):0}");
        }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC_REINFORCE commit err {ex.GetType().Name}: {ex.Message}"); }
    }
}
