using HarmonyLib;
using Il2Cpp;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Patch intent: while a province popup is click-pinned, skip vanilla's
// MapTextMeshLabel.OnMouseOver so hover-triggered Show calls don't run
// (which would re-position the popup at the cursor each frame). The
// popup stays at its click-anchored position until the user dismisses it
// via the MC Close button or clicks a different province.
//
// When nothing is pinned, vanilla's hover-tooltip behaviour runs as
// normal — hover any province name and the info popup follows the cursor.
[HarmonyPatch(typeof(MapTextMeshLabel), nameof(MapTextMeshLabel.OnMouseOver))]
internal static class CampaignProvinceLabelMouseOverPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!ProvincePopupInvocationContext.IsPinned) return true;

        // Pin sanity-check. PinnedProvinceId is a static field, so it
        // survives save reloads, campaign restarts, and any path that
        // dismissed the popup without flowing through our Unpin() call.
        // If the popup isn't actually visible right now, the pin is
        // stale — clear it and let vanilla's hover behaviour run.
        try
        {
            CampaignProvincePopupUI? popup = G.ui?.ProvincePopupElement;
            if (popup?.Root == null || !popup.Root.activeSelf)
            {
                ProvincePopupInvocationContext.Unpin();
                return true;
            }
        }
        catch
        {
            // If access throws, fall back to vanilla rather than
            // silencing hover indefinitely.
            ProvincePopupInvocationContext.Unpin();
            return true;
        }

        // Popup is visible and pin is real — suppress hover-driven
        // re-Show so the popup stays put at its click-anchored position.
        return false;
    }
}
