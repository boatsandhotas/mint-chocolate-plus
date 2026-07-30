using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Patch intent: vanilla MapTextMeshLabel.OnMouseExit hides the province popup
// when the cursor leaves the label. That kills MC's Launch Land Invasion
// button before the user can reach it. When the popup is in "MC pinned"
// mode (our button is attached), skip the hover-exit hide so the popup
// stays put until the user clicks elsewhere.
[HarmonyPatch(typeof(MapTextMeshLabel), nameof(MapTextMeshLabel.OnMouseExit))]
internal static class CampaignProvinceLabelMouseExitPatch
{
    private const string LaunchLandInvasionButtonName = "UADMC_LaunchLandInvasion";

    [HarmonyPrefix]
    private static bool Prefix(MapTextMeshLabel __instance)
    {
        try
        {
            CampaignProvincePopupUI? popup = G.ui?.ProvincePopupElement;
            if (popup?.LayoutRoot == null) return true;
            Transform? launch = popup.LayoutRoot.Find(LaunchLandInvasionButtonName);
            if (launch != null && launch.gameObject.activeSelf)
            {
                // MC-pinned: don't run vanilla's hover-exit handler so the popup
                // (and our button) stay visible. Inactive launch button means
                // non-invadable target — fall back to vanilla hover behaviour
                // (popup disappears on cursor exit, no dismiss-button needed).
                return false;
            }
        }
        catch
        {
            // Fall back to vanilla behaviour on any unexpected access error.
        }
        return true;
    }
}
