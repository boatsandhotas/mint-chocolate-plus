using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// At campaign battle end, hand all surrendered ships to the MC-winner (capture the
// loser's, recover the winner's own). Postfix runs after vanilla's result/MC reconciliation
// (probe-confirmed: surrendered survivors still present + Victor populated). The
// CampaignBattle overload scopes this to campaign battles (excludes custom/mission and the
// separate CampaignBattleWithSubmarine overload).
[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.CompleteBattle),
    new[] { typeof(CampaignBattle), typeof(bool), typeof(bool) })]
internal static class CampaignSurrenderedShipCapturePatch
{
    // Snapshot only — the transfer is deferred to after reconciliation (see below).
    [HarmonyPostfix]
    private static void Postfix(CampaignBattle battle) => SurrenderedShipCapture.OnCompleteBattle(battle);
}

// Reinstate the snapshotted surrendered ships to the victor AFTER the campaign reconciles on
// World re-entry (this is where vanilla's loss pass has already run, so our transfer sticks).
[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnEnterState))]
internal static class CampaignSurrenderedShipReinstatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState state)
    {
        if (state == GameManager.GameState.World)
            SurrenderedShipCapture.ReinstatePending();
    }
}

// The post-battle loss pass that reaps surrendered ships. Prefix: scrub our kept ships from
// its input lists before it runs; pre/post logging pins which method drops the vessel count.
[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.CheckResultBattleOnShipLosses),
    new[] { typeof(CampaignBattle) })]
internal static class CampaignSurrenderLossPassPatch
{
    [HarmonyPrefix]
    private static void Prefix(CampaignBattle battle)
    {
        SurrenderedShipCapture.LogLossPass("CheckResultBattleOnShipLosses PRE");
        SurrenderedShipCapture.ProtectFromLossPass(battle);
    }

    [HarmonyPostfix]
    private static void Postfix() => SurrenderedShipCapture.LogLossPass("CheckResultBattleOnShipLosses POST");
}

// Diagnostic only: the other candidate reaper. Logging is gated to post-capture battles.
[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.CleanupShips))]
internal static class CampaignSurrenderCleanupProbePatch
{
    [HarmonyPrefix]
    private static void Prefix() => SurrenderedShipCapture.LogLossPass("CleanupShips PRE");

    [HarmonyPostfix]
    private static void Postfix() => SurrenderedShipCapture.LogLossPass("CleanupShips POST");
}
