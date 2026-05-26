using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UnityEngine.EventSystems;

namespace UADVanillaPlus.Harmony;

// Patch intent: vanilla MapTextMeshLabel.OnPointerClick (the click handler on
// the per-province name label on the campaign map) shows the province popup
// only for player-aligned territory — clicking a minor/enemy territory name
// does nothing. VP brackets OnPointerClick with a Prefix + Postfix that
// marks this as a click-driven invocation (so the action-button injection
// in CampaignLaunchLandInvasionPatch knows to engage) and force-calls
// ProvincePopupElement.Show(province.Id) when vanilla bailed.
//
// Hover-only invocations (vanilla MapTextMeshLabel.OnMouseOver) leave the
// click-invocation counter at zero, so the action button is NOT injected
// and the popup retains vanilla tooltip behaviour.
[HarmonyPatch(typeof(MapTextMeshLabel), nameof(MapTextMeshLabel.OnPointerClick))]
internal static class CampaignEnemyProvincePopupPatch
{
    private static string lastLoggedProvinceId = string.Empty;

    private static int clicksObserved;

    [HarmonyPrefix]
    private static void Prefix(MapTextMeshLabel __instance)
    {
        ProvincePopupInvocationContext.BeginClick();
        // One-time-per-session log + per-click compact line so we can verify
        // that label clicks are actually reaching us. If you click on the map
        // and see nothing here, you're hitting the colored-territory layer
        // (not the text label) and the click never reaches MapTextMeshLabel.
        try
        {
            clicksObserved++;
            string labelId = __instance?.Id ?? "<null>";
            string provId = __instance?.province?.Id ?? "<null>";
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP province-label click #{clicksObserved}: label='{labelId}' province='{provId}'.");
        }
        catch { }
    }

    [HarmonyPostfix]
    private static void Postfix(MapTextMeshLabel __instance, PointerEventData eventData)
    {
        try
        {
            if (__instance == null) return;

            Province? province = __instance.province;
            if (province == null)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP enemy-province-popup: click on label '{__instance.Id}' but province is null; skipping.");
                return;
            }

            CampaignProvincePopupUI? popup = G.ui?.ProvincePopupElement;
            if (popup == null)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    "UADVP enemy-province-popup: G.ui.ProvincePopupElement is null.");
                return;
            }

            string controller = "<none>";
            try { controller = province.ControllerPlayer?.Name(false) ?? "<none>"; }
            catch { }

            // Debounced log: only emit on province change to avoid log spam if
            // OnPointerClick fires multiple times for the same selection.
            if (lastLoggedProvinceId != province.Id)
            {
                lastLoggedProvinceId = province.Id;
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP enemy-province-popup: forcing popup for province={province.Id} controller={controller}.");
            }

            popup.Show(province.Id);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP enemy-province-popup: postfix failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ProvincePopupInvocationContext.EndClick();
        }
    }
}
