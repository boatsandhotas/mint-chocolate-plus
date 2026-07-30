using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Phase 0 shared per-turn dispatcher: a single CampaignController.OnNewTurn postfix
// that drives MC's per-turn campaign systems in a guarded, deterministic order
// (multi-year shipyard rebuild today; the vanquished-asset transfer hooks
// DisablePlayer separately). Also reconciles persisted mod state on campaign load.
// OnNewTurn fires once per resolved turn, after ownership/battles commit and after
// vanilla's own shipyard growth, so per-turn Player.shipyard writes here persist.
[HarmonyPatch(typeof(CampaignController))]
internal static class CampaignPerTurnDispatchPatch
{
    [HarmonyPostfix]
    [HarmonyPatch("OnNewTurn")]
    private static void OnNewTurnPostfix(CampaignController __instance)
    {
        try { ModCampaignState.EnsureCampaignId(); } catch { }
        try { PortCapacityRebuild.ProcessTurn(__instance); }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC per-turn dispatch: {ex.GetType().Name}: {ex.Message}"); }
        // Snapshot each major's empire so vanquished spoils can credit a dying nation's last-held
        // territory (incl. colonies) once it controls nothing at DisablePlayer.
        try { if (ModSettings.VanquishedSpoilsEnabled) VanquishedTransfer.SnapshotEmpires(__instance?.CampaignData); } catch { }
        // Ally ship purchase: deliver completed contract hulls + resolve any broken-alliance orders.
        try { if (ModSettings.AllyShipPurchaseEnabled) { AlliedShipPurchase.EnsureOrderedBuildsRunning(); AlliedShipPurchase.DeliverCompleted(); AlliedShipPurchase.ProcessBreaks(); AlliedShipPurchase.AdoptOwnedPurchasedDesigns(); } }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC ally-purchase turn: {ex.GetType().Name}: {ex.Message}"); }
        // Naval reinforcement: drop commitments whose battle has resolved/vanished, then charge the per-turn
        // supply cost for the ones still active.
        try { LandInvasionSupport.PurgeStale(); } catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC reinforce purge: {ex.GetType().Name}: {ex.Message}"); }
        try { LandInvasionSupport.ChargeTurnCosts(); } catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC reinforce cost: {ex.GetType().Name}: {ex.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CampaignController.OnLoadingScreenHide))]
    private static void OnLoadingScreenHidePostfix(CampaignController __instance)
    {
        try { ModCampaignState.EnsureCampaignId(); } catch { }
        try { NameThemeDatabase.EnsureLoaded(); } catch { } // Phase 2 Stage 1: load + log the name DB (de-risk nation-key mapping)
        try { SurrenderedShipCapture.ReinstatePending(); } catch { } // fallback drain (one-shot) for post-battle capture
        try { AllyPurchaseState.Reconcile(); } catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC ally-purchase reconcile: {ex.GetType().Name}: {ex.Message}"); }
        try { LandInvasionSupport.Reconcile(); } catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC reinforce reconcile: {ex.GetType().Name}: {ex.Message}"); }
        try { PortCapacityRebuild.Reconcile(__instance); }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC load reconcile: {ex.GetType().Name}: {ex.Message}"); }
        try { if (ModSettings.VanquishedSpoilsEnabled) VanquishedTransfer.SnapshotEmpires(__instance?.CampaignData); } catch { }
    }

    // Phase 1 vanquished spoils: prefix so the dying nation's fleet is still present
    // (vanilla destroys it inside DisablePlayer). Distribute ships + cash to the victors
    // before vanilla cleans up.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CampaignController.DisablePlayer))]
    private static void DisablePlayerPrefix(CampaignController __instance, Player player)
    {
        try { VanquishedTransfer.OnDisablePlayer(__instance, player); }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC vanquished dispatch: {ex.GetType().Name}: {ex.Message}"); }
    }
}
