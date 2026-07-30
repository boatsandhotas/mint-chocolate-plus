using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace MintChipPlus.Harmony;

// Patch intent: vanilla's per-port click handler shows the port popup only
// for player-controlled (or possibly friendly) ports — enemy and minor-power
// ports just do nothing on click. That blocks the Launch Invasion button
// because PortPopupUI.Show never fires. MC appends a second onClick listener
// to every PortUI.PortButton that force-opens G.ui.PortPopup for any port,
// letting the existing CampaignLaunchInvasionPatch attach its button.
//
// We attach an extra listener instead of replacing vanilla's: vanilla's
// listener is harmless for enemy ports (does nothing observable) and remains
// useful for friendly ports, so we just add a no-op-or-show-popup pass.
[HarmonyPatch(typeof(MapUI))]
internal static class CampaignEnemyPortPopupPatch
{
    private static readonly HashSet<IntPtr> AttachedButtons = new();

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MapUI.InitPortsUI))]
    internal static void InitPortsUIPostfix(MapUI __instance) => AttachAll(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MapUI.UpdatePortsOwnerUI))]
    internal static void UpdatePortsOwnerUIPostfix(MapUI __instance) => AttachAll(__instance);

    private static void AttachAll(MapUI mapUi)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<CampaignMapElement>? portElements = mapUi?.portElements;
            if (portElements == null) return;

            int attached = 0;
            int forced = 0;
            int totalPortUis = 0;
            int totalSeen = portElements.Count;
            foreach (CampaignMapElement element in portElements)
            {
                if (element == null) continue;
                PortUI? portUi = element.TryCast<PortUI>();
                if (portUi == null) continue;
                totalPortUis++;

                Button? button = portUi.PortButton;
                if (button == null) continue;

                // Vanilla disables PortButton on enemy/non-owned ports, which is
                // what blocks both vanilla's own click handler AND any listener
                // we add. Force interactable=true so clicks reach our listener.
                if (!button.interactable)
                {
                    button.interactable = true;
                    forced++;
                }

                // Also defeat any CanvasGroup that intercepts raycasts on enemy
                // ports' parent containers.
                CanvasGroup? group = button.GetComponentInParent<CanvasGroup>();
                if (group != null && (!group.interactable || !group.blocksRaycasts))
                {
                    group.interactable = true;
                    group.blocksRaycasts = true;
                }

                if (AttachedButtons.Contains(button.Pointer)) continue;
                button.onClick.AddListener(new System.Action(() => OnPortClicked(portUi, button)));
                AttachedButtons.Add(button.Pointer);
                attached++;
            }

            if (attached > 0 || forced > 0)
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC enemy-port-popup: attached={attached} forced-interactable={forced} " +
                    $"portUiCount={totalPortUis} elementCount={totalSeen}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC enemy-port-popup: AttachAll failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnPortClicked(PortUI portUi, Button button)
    {
        try
        {
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC enemy-port-popup: click portUi.Id='{portUi.Id}' provinceId='{portUi.ProvinceId}' " +
                $"interactable={button.interactable}.");

            PortElement? port = LookupPortElement(portUi);
            if (port == null)
            {
                Melon<MintChipPlusMod>.Logger.Warning(
                    $"UADMC enemy-port-popup: could not resolve PortElement for PortUI id='{portUi.Id}' provinceId='{portUi.ProvinceId}'.");
                return;
            }

            // Vanilla has two PortPopupUI instances:
            //   G.ui.PortWindow = full popup with MoveShips/Repair/etc action buttons
            //   G.ui.PortPopup  = small/hover info-only popup (action buttons null)
            // We want PortWindow so the Launch Invasion button has somewhere to
            // attach. Fall back to PortPopup if PortWindow is unavailable, but
            // log the fallback so we can tell from the log when this happens.
            PortPopupUI? popup = G.ui?.PortWindow;
            string popupName = "PortWindow";
            if (popup == null)
            {
                popup = G.ui?.PortPopup;
                popupName = "PortPopup (fallback)";
            }
            if (popup == null)
            {
                Melon<MintChipPlusMod>.Logger.Warning("UADMC enemy-port-popup: both PortWindow and PortPopup are null.");
                return;
            }

            // Diagnostic: which buttons does this popup actually have wired up?
            string buttonState = $"moveShips={(popup.MoveShips != null)} " +
                                 $"moveSubs={(popup.MoveSubmarines != null)} " +
                                 $"repair={(popup.Repair != null)} " +
                                 $"close={(popup.Close != null)} " +
                                 $"smallVersion={popup.SmallVersion}";

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC enemy-port-popup: calling Show on {popupName} for port {port.Id} " +
                $"(controller={port.CurrentProvince?.ControllerPlayer?.Name(false) ?? "<none>"}); " +
                $"pre-Show state: {buttonState}.");

            popup.Show(port);

            // Confirm the popup actually opened. If Root is inactive, vanilla
            // (or our own postfix) hid it again and we need a different attack.
            GameObject? root = popup.Root;
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC enemy-port-popup: after Show, popup.Root.activeSelf={(root != null ? root.activeSelf.ToString() : "<null>")}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC enemy-port-popup: click handler failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PortElement? LookupPortElement(PortUI portUi)
    {
        string portId = portUi.Id;
        string provinceId = portUi.ProvinceId;
        if (CampaignController.Instance?.CampaignData?.Players == null) return null;

        // First pass: exact port id match (preferred when multi-port provinces exist).
        if (!string.IsNullOrEmpty(portId))
        {
            foreach (Player p in CampaignController.Instance.CampaignData.Players)
            {
                Il2CppSystem.Collections.Generic.List<Province>? provs = p?.provinces;
                if (provs == null) continue;
                foreach (Province prov in provs)
                {
                    Il2CppSystem.Collections.Generic.List<PortElement>? ports = prov?.Ports;
                    if (ports == null) continue;
                    foreach (PortElement port in ports)
                    {
                        if (port != null && port.Id == portId)
                            return port;
                    }
                }
            }
        }

        // Fallback: first port in the matching province.
        if (!string.IsNullOrEmpty(provinceId))
        {
            foreach (Player p in CampaignController.Instance.CampaignData.Players)
            {
                Il2CppSystem.Collections.Generic.List<Province>? provs = p?.provinces;
                if (provs == null) continue;
                foreach (Province prov in provs)
                {
                    if (prov == null || prov.Id != provinceId) continue;
                    Il2CppSystem.Collections.Generic.List<PortElement>? ports = prov.Ports;
                    if (ports == null || ports.Count == 0) continue;
                    return ports[0];
                }
            }
        }

        return null;
    }
}
