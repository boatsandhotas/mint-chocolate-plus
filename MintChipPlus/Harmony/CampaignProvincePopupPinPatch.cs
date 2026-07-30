using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Patch intent: CampaignProvincePopupUI behaves as a hover tooltip in vanilla
// — its Update() method repositions the popup to the mouse every frame so
// the panel follows the cursor while hovering a province name. That makes
// clicking MC's Launch Land Invasion button impossible because the popup
// (and its button) skitter away under the cursor.
//
// When our button is present on the popup, the popup is no longer a tooltip
// — it's a click-anchored panel — so skip vanilla's per-frame position
// update. Friendly/vanilla hover paths without our button stay untouched.
[HarmonyPatch(typeof(CampaignProvincePopupUI), nameof(CampaignProvincePopupUI.Update))]
internal static class CampaignProvincePopupPinPatch
{
    // Has to match CampaignLaunchLandInvasionPatch's button name. Duplicating
    // the literal keeps this patch standalone and dependency-free.
    private const string LaunchLandInvasionButtonName = "UADMC_LaunchLandInvasion";

    [HarmonyPrefix]
    private static bool Prefix(CampaignProvincePopupUI __instance)
    {
        try
        {
            RectTransform? layout = __instance?.LayoutRoot;
            if (layout == null) return true;  // run vanilla Update
            Transform? launch = layout.Find(LaunchLandInvasionButtonName);
            if (launch != null && launch.gameObject.activeSelf)
            {
                // MC-pinned: do nothing, leaving the popup at its last placed
                // screen position so the button stays clickable. Inactive
                // (hidden) launch button means non-invadable target — fall
                // back to vanilla hover behaviour.
                return false;
            }
        }
        catch
        {
            // If anything throws, fall back to vanilla behavior so we never
            // freeze the popup unexpectedly.
        }
        return true;
    }
}
