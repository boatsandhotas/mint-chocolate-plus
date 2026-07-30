using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Debug trigger for ally-ship-purchase (until the design-viewer "Purchase" button lands):
//   Shift+F6 -> toggle the AllyShipPurchase feature on/off
//   F6       -> place a TEST order: 1 ship of the first buyable design from the first allied major
// Campaign-only. Logs under UADMC_ALLYBUY (and the PROBE line settles the capacity-attribution question).
internal static class AllyPurchaseDebug
{
    private const KeyCode Key = KeyCode.F6;
    private static void Log(string m) => Melon<MintChipPlusMod>.Logger.Msg("UADMC_ALLYBUY " + m);

    internal static void TryHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(Key)) return;
            if (CampaignController.Instance == null) return;
            if (Util.FocusIsInInputField()) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift)
            {
                ModSettings.AllyShipPurchaseEnabled = !ModSettings.AllyShipPurchaseEnabled;
                Log($"DEBUG toggle -> AllyShipPurchaseEnabled={ModSettings.AllyShipPurchaseEnabled}");
                return;
            }

            if (!ModSettings.AllyShipPurchaseEnabled) { Log("DEBUG order: feature is OFF (Shift+F6 to enable)."); return; }

            var sellers = AllySales.AlliedSellers();
            if (sellers.Count == 0) { Log("DEBUG order: no allied majors found."); return; }
            Player seller = sellers[0];
            var designs = AllySales.BuyableDesigns(seller);
            if (designs.Count == 0) { Log($"DEBUG order: {Name(seller)} has no buyable designs."); return; }

            Log($"DEBUG order: buying 1x from {Name(seller)} (allied majors={sellers.Count}, buyable designs={designs.Count}).");
            AlliedShipPurchase.PlaceOrder(seller, designs[0], 1);
        }
        catch (Exception ex) { Log("DEBUG hotkey error: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static string Name(Player p) { try { return p.data?.name ?? "?"; } catch { return "?"; } }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.Update))]
internal static class AllyPurchaseDebugHotkeyPatch
{
    [HarmonyPostfix]
    private static void Postfix() => AllyPurchaseDebug.TryHotkey();
}
