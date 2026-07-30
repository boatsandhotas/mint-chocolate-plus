using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Replaces vanilla's battle Space handling with a predictable pause toggle that
// restores the player's previous non-zero battle speed.
[HarmonyPatch(typeof(Ui), "UpdateBattle")]
internal static class BattleSpacePauseTogglePatch
{
    private const float DefaultBattleTimeScale = 1f;
    private const float MinPausedTimeScale = 0.01f;

    private static float previousBattleTimeScale = DefaultBattleTimeScale;

    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    private static bool PrefixUpdateBattle(Ui __instance)
        => !TryHandleSpaceToggle(__instance);

    internal static void Reset(string context)
    {
        previousBattleTimeScale = DefaultBattleTimeScale;
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC battle pause: reset Space pause speed during {context}.");
    }

    private static bool TryHandleSpaceToggle(Ui? ui)
    {
        if (ui == null || !GameManager.IsBattle || !Input.GetKeyDown(KeyCode.Space))
            return false;

        if (!CanHandleBattleKeyboard())
            return false;

        try
        {
            if (TimeControl.IsPaused())
            {
                float restoreScale = RestorableTimeScale(previousBattleTimeScale);
                TimeControl.TimeScale(restoreScale);
                RefreshBattleUi(ui);
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC battle pause: resumed battle via Space to {FormatScale(restoreScale)}x.");
                return true;
            }

            previousBattleTimeScale = RestorableTimeScale(Time.timeScale);
            TimeControl.Pause(true);
            RefreshBattleUi(ui);
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC battle pause: paused battle via Space; cached {FormatScale(previousBattleTimeScale)}x.");
            return true;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC battle pause: Space toggle failed; leaving vanilla input path active. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool CanHandleBattleKeyboard()
    {
        try
        {
            return GameManager.CanHandleKeyboardInput() && !Util.FocusIsInInputField();
        }
        catch
        {
            return false;
        }
    }

    private static float RestorableTimeScale(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= MinPausedTimeScale)
            return DefaultBattleTimeScale;

        return Mathf.Max(DefaultBattleTimeScale, scale);
    }

    private static void RefreshBattleUi(Ui ui)
    {
        try
        {
            ui.Refresh(false);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC battle pause: could not refresh battle UI after Space toggle. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatScale(float scale)
        => scale.ToString("0.##");
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.LeaveBattle))]
internal static class BattleSpacePauseToggleLeaveBattlePatch
{
    [HarmonyPostfix]
    private static void PostfixLeaveBattle()
        => BattleSpacePauseTogglePatch.Reset("LeaveBattle");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnLeaveState))]
internal static class BattleSpacePauseToggleLeaveStatePatch
{
    [HarmonyPostfix]
    private static void PostfixLeaveState(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleSpacePauseTogglePatch.Reset("battle state exit");
    }
}
