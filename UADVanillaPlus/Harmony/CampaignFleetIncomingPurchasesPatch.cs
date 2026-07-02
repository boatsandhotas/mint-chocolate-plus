using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UADVanillaPlus.GameData;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Surfaces ships an ALLY is currently building for you (outstanding ally-purchase orders) at the top
// of the campaign Fleet tab list, styled to match vanilla's own "building for another nation" rows
// ("Building 96%, 2m" in yellow + a Select Port button + the counterparty in the Sold column, greyed
// out). The hulls are owned by the seller (Ship.ForSaleTo = you), so they never appear in your fleet
// natively. We CLONE a real fleet row (FleetWindow_ShipElementUI) so it inherits the exact native
// height + columns, then populate it. The clones are NEVER registered in fleetUiByShip/
// selectedElements, so native sort/select/scrap/suspend can't touch a foreign hull (and it can't
// count against your shipbuilding capacity).
[HarmonyPatch(typeof(CampaignFleetWindow))]
internal static class CampaignFleetIncomingPurchasesPatch
{
    private static readonly Color Dim = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Color BuildYellow = new(0.95f, 0.78f, 0.30f, 1f);
    private static readonly List<GameObject> injected = new();

    [HarmonyPatch(nameof(CampaignFleetWindow.Refresh), new Type[] { typeof(bool) })]
    [HarmonyPostfix]
    private static void PostfixRefresh(CampaignFleetWindow __instance, bool isDesign)
    {
        foreach (GameObject go in injected)
            if (go != null) UnityEngine.Object.Destroy(go);
        injected.Clear();

        if (isDesign || __instance == null || !ModSettings.AllyShipPurchaseEnabled) return;

        try
        {
            Player buyer = ExtraGameData.MainPlayer();
            if (buyer == null) return;

            FleetWindow_ShipElementUI? source = FirstNativeRow(__instance);
            if (source == null || source.gameObject == null) return; // need a real row to clone
            Transform parent = source.gameObject.transform.parent;

            foreach (AllyPurchaseState.Order o in AllyPurchaseState.Current.Orders)
            {
                Ship? hull = AlliedShipPurchase.TryResolveOrderHull(o);
                if (hull == null) continue;
                bool mine = Safe(() => hull.ForSaleTo != null && hull.ForSaleTo.Pointer == buyer.data.Pointer, false);
                bool building = Safe(() => hull.isBuilding || hull.isCommissioning, false);
                if (!mine || !building) continue;
                GameObject? row = BuildRow(__instance, source, parent, o, hull);
                if (row != null) injected.Add(row);
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY fleet-row inject failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static FleetWindow_ShipElementUI? FirstNativeRow(CampaignFleetWindow window)
    {
        try
        {
            foreach (var kv in window.fleetUiByShip)
                if (kv.Value != null && kv.Value.gameObject != null && kv.Value.gameObject.activeInHierarchy)
                    return kv.Value;
        }
        catch { }
        return null;
    }

    private static GameObject? BuildRow(CampaignFleetWindow window, FleetWindow_ShipElementUI source, Transform parent, AllyPurchaseState.Order o, Ship hull)
    {
        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
        clone.name = "UADVP_IncomingPurchase";
        clone.SetActive(true);
        clone.transform.SetAsFirstSibling();

        var ui = clone.GetComponent<FleetWindow_ShipElementUI>();
        if (ui == null) return clone;

        // Strip hover handlers carried over from the source row: on a clone they invoke a Ui closure
        // that NREs every time the pointer enters/exits the row (spams the log).
        StripHover(clone);

        var refresh = new System.Action(() => { try { window.Refresh(false); } catch { } });

        // Columns that mirror vanilla's "building for another nation" rows.
        SetText(ui.Type, TypeLabel(hull), Dim);
        SetText(ui.Name, ShipName(hull), Dim);
        SetText(ui.Class, string.IsNullOrEmpty(o.DesignName) ? "ship" : o.DesignName, Dim);
        SetText(ui.Status, StatusText(o, hull), BuildYellow);     // "Building 45%, 9m"
        SetText(ui.Sold, o.Seller, Dim);                          // counterparty (the ally)
        SetText(ui.Cost, Money(o.Deposit + o.Balance), Dim);
        SetText(ui.Tonnes, Tonnage(hull), Dim);
        SetText(ui.Year, Year(hull), Dim);
        SetText(ui.CrewAmount, "0", Dim);

        // Columns that don't apply to an unbuilt foreign hull.
        SetText(ui.Damage, ""); SetText(ui.Ammo, ""); SetText(ui.Fuel, ""); SetText(ui.Area, "");
        SetText(ui.Speed, ""); SetText(ui.Weapons, ""); SetText(ui.CurrentRole, ""); SetText(ui.ShipCount, "");
        SetText(ui.Attack, ""); SetText(ui.Stealth, ""); SetText(ui.HullStrength, ""); SetText(ui.Range, "");

        // Blank (don't hide!) the non-functional cloned controls. The row is a HorizontalLayoutGroup,
        // so SetActive(false) collapses the gap and shifts every later column left. Instead keep the
        // cells present (preserves alignment) but make them invisible: disable the box image + blank text.
        BlankControl(ui.CrewAction);
        BlankControl(ui.RoleSelectionButton);
        BlankControl(ui.AreaButton);
        try { if (ui.Highlighted != null) ui.Highlighted.gameObject.SetActive(false); } catch { }
        try { ui.CurrentShip = hull; } catch { }

        // Port column: a "Select Port" button until a destination is chosen, then the port name —
        // exactly like vanilla's building rows. Both the button and the whole row open our panel.
        // Use the game's STANDARD "Select Port" control (PortSelection) until a destination is chosen,
        // then show the port name. Clicking either opens the order panel to pick your delivery port.
        bool hasDest = !string.IsNullOrEmpty(o.DestPort);
        string destName = hasDest ? (Safe(() => AlliedShipPurchase.FindBuyerPort(o.DestPort)?.Name, null) ?? o.DestPort) : "";
        try { if (ui.PortSelection != null) ui.PortSelection.SetActive(!hasDest); } catch { }
        SetText(ui.Port, destName, Dim);
        try
        {
            if (ui.PortSelectionButton != null)
            {
                ui.PortSelectionButton.onClick.RemoveAllListeners();
                ui.PortSelectionButton.onClick.AddListener(new System.Action(() => OpenPortPicker(window, o, refresh)));
            }
            if (ui.PortButton != null)
            {
                ui.PortButton.onClick.RemoveAllListeners();
                ui.PortButton.onClick.AddListener(new System.Action(() => OpenPortPicker(window, o, refresh)));
            }
        }
        catch { }

        try
        {
            if (ui.Btn != null)
            {
                ui.Btn.onClick.RemoveAllListeners();
                ui.Btn.onClick.AddListener(new System.Action(() => ConfirmCancel(o, refresh)));
            }
        }
        catch { }

        return clone;
    }

    private static void OpenPortPicker(CampaignFleetWindow window, AllyPurchaseState.Order o, System.Action refresh)
        => OpenPortPickerForOrders(window, new List<AllyPurchaseState.Order> { o }, refresh);

    // Open the game's STANDARD port-selection popup (ListSelectionPopupUI) with the buyer's ports and
    // apply the chosen port to EVERY order in the batch (so a whole order of N ships gets the port at
    // once). Used right after a purchase ("pick the delivery port when ordering") and per-row.
    internal static void OpenPortPickerForOrders(CampaignFleetWindow window, List<AllyPurchaseState.Order> orders, System.Action? refresh = null)
    {
        try
        {
            if (window == null || orders == null || orders.Count == 0) return;
            ListSelectionPopupUI? sel = Safe(() => window.SelectionWindow, null);
            if (sel == null) { Melon<UADVanillaPlusMod>.Logger.Warning("UADVP_ALLYBUY: no SelectionWindow on fleet window"); return; }

            var options = new Il2CppSystem.Collections.Generic.List<string>();
            var dict = new Il2CppSystem.Collections.Generic.Dictionary<string, PortElement>();
            var indexed = new List<PortElement>();
            foreach (PortElement pe in AlliedShipPurchase.BuyerPorts())
            {
                string name = Safe(() => pe.Name, "");
                if (string.IsNullOrWhiteSpace(name)) name = Safe(() => pe.Id, "port") ?? "port";
                int cap = Safe(() => pe.GetPortCapacityWithoutDamage(), 0);
                string label = $"{name}   (cap {cap:N0} t)";
                int n = 2;
                while (dict.ContainsKey(label)) label = $"{name}   (cap {cap:N0} t) #{n++}";
                options.Add(label);
                dict.Add(label, pe);
                indexed.Add(pe);
            }
            if (indexed.Count == 0) return;

            var ports = indexed;
            var ords = orders;
            var cb = refresh;
            string title = orders.Count == 1
                ? $"Delivery port for {orders[0].DesignName}"
                : $"Delivery port for {orders.Count}x {orders[0].DesignName}";
            sel.Show(title, options,
                new System.Action<int>(i =>
                {
                    try
                    {
                        if (i < 0 || i >= ports.Count) return;
                        string pid = Safe(() => ports[i].Id, "") ?? "";
                        foreach (AllyPurchaseState.Order o in ords) AllyPurchaseState.SetOrderPort(o, pid);
                        if (cb != null) cb(); else { try { window.Refresh(false); } catch { } }
                    }
                    catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY port-pick: {ex.GetType().Name}: {ex.Message}"); }
                }),
                null, dict);
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY OpenPortPickerForOrders: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Native confirm to cancel an order (forfeit the deposit).
    private static void ConfirmCancel(AllyPurchaseState.Order o, System.Action refresh)
    {
        try
        {
            string text = $"Cancel the order for <b>{o.DesignName}</b> from <b>{o.Seller}</b>?\n\n" +
                          $"The deposit of {Money(o.Deposit)} is forfeit and {o.Seller} keeps the hull.";
            MessageBoxUI.Show($"Cancel order — {o.DesignName}", text, null, false, "Cancel order", "Keep",
                new System.Action(() => { try { AlliedShipPurchase.CancelOrder(o); refresh(); } catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY cancel: {ex.GetType().Name}: {ex.Message}"); } }),
                null);
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY ConfirmCancel: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void StripHover(GameObject root)
    {
        try { foreach (var c in root.GetComponentsInChildren<OnEnter>(true)) UnityEngine.Object.Destroy(c); } catch { }
        try { foreach (var c in root.GetComponentsInChildren<OnLeave>(true)) UnityEngine.Object.Destroy(c); } catch { }
        try { foreach (var c in root.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true)) UnityEngine.Object.Destroy(c); } catch { }
    }

    private static void SetText(TMP_Text? t, string s) { try { if (t != null) t.text = s; } catch { } }
    private static void SetText(TMP_Text? t, string s, Color c) { try { if (t != null) { t.text = s; t.color = c; } } catch { } }
    private static void Hide(Component? c) { try { if (c != null) c.gameObject.SetActive(false); } catch { } }

    // Make a cloned cell invisible WITHOUT removing it from the layout (SetActive(false) would shift
    // every later column left). Keep the GameObject active; disable its box image, blank its text,
    // and make it non-interactable.
    private static void BlankControl(Component? c)
    {
        try
        {
            if (c == null) return;
            GameObject go = c.gameObject;
            var img = go.GetComponent<Image>();
            if (img != null) img.enabled = false;
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.interactable = false;
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) { try { t.text = ""; } catch { } }
        }
        catch { }
    }

    private static string TypeLabel(Ship hull) => Safe(() => hull.shipType?.nameUi, null) ?? Safe(() => hull.shipType?.name, "") ?? "";

    private static string ShipName(Ship hull)
    {
        string n = Safe(() => hull.vesselName, null) ?? "";          // the stored ship name
        if (string.IsNullOrWhiteSpace(n)) n = Safe(() => hull.Name(false, false), null) ?? "";
        return string.IsNullOrWhiteSpace(n) ? "Incoming" : n;
    }

    private static string StatusText(AllyPurchaseState.Order o, Ship hull)
    {
        bool commissioning = Safe(() => hull.isCommissioning && !hull.isBuilding, false);
        float progress = Safe(() => hull.buildingProgress, 0f); // 0..100 percent
        int monthsLeft = Safe(() =>
        {
            int rem = hull.isBuilding ? (int)Math.Ceiling((100f - hull.buildingProgress) / 100f * hull.BuildingTime(true)) : 0;
            return Math.Max(0, rem) + Safe(() => hull.CommissioningTime(), 0);
        }, 0);
        return commissioning ? $"Fitting out, {monthsLeft}m" : $"Building {progress:0}%, {monthsLeft}m";
    }

    private static string Tonnage(Ship hull) => Safe(() => { float t = hull.Tonnage(); return t > 0f ? t.ToString("0") : ""; }, "");
    private static string Year(Ship hull) => Safe(() => hull.dateCreated.AsDate().Year.ToString(), "");

    private static string Money(float v)
    {
        float a = v < 0f ? -v : v;
        if (a >= 1e9f) return $"${v / 1e9f:0.0}B";
        if (a >= 1e6f) return $"${v / 1e6f:0.0}M";
        return $"${v:0}";
    }

    private static T Safe<T>(Func<T> f, T fb) { try { return f(); } catch { return fb; } }
}
