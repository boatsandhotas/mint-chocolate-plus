using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;
using UnityEngine.UI;

namespace MintChipPlus.Harmony;

// Patch intent: complement the naval-invasion port-popup button with a
// "Launch Land Invasion" button on the campaign province info popup. Visible
// only when the clicked province is adjacent to one of the player's own
// provinces. Drives the same diplomatic backbone (force war + third-party
// consequences) and hands off to vanilla via ProvinceBattleManager.AddBattle
// so save/load and AI awareness behave like vanilla-spawned province battles.
//
// CampaignProvincePopupUI has no native buttons of its own, so we clone the
// PortPopupUI.MoveShips button as a styled template the first time a port
// popup opens, then re-parent that clone into the province popup's LayoutRoot.
[HarmonyPatch(typeof(CampaignProvincePopupUI), nameof(CampaignProvincePopupUI.Show))]
internal static class CampaignLaunchLandInvasionPatch
{
    private const string LaunchLandInvasionButtonName = "UADMC_LaunchLandInvasion";
    private const string CloseProvincePopupButtonName = "UADMC_CloseProvincePopup";
    private static readonly Color LaunchLandColor = new(1f, 0.45f, 0.18f, 1f);
    private static readonly Color LaunchLandBlockedColor = new(0.7f, 0.5f, 0.4f, 1f);
    private static readonly Dictionary<IntPtr, Province> LastShownProvince = new();
    private static readonly Dictionary<IntPtr, string> LastLoggedState = new();
    private static string lastShownLoggedProvince = string.Empty;

    [HarmonyPostfix]
    private static void Postfix(CampaignProvincePopupUI __instance, string provinceId)
    {
        try
        {
            if (__instance == null) return;

            // Vanilla calls Show with non-province ids in several paths —
            // areas (e.g. "caribbean"), empty strings on hide/clear cycles,
            // and any future case we haven't enumerated. Silently bail in
            // those cases; the warning was spamming the log on every popup
            // refresh.
            if (string.IsNullOrEmpty(provinceId)) return;
            Province? province = FindProvinceById(provinceId);
            if (province == null) return;

            LastShownProvince[__instance.Pointer] = province;

            // Click detection: three layers.
            //  1. IsActiveClickInvocation — OnPointerClick Harmony hook (when
            //     it fires; doesn't in this game build).
            //  2. Input.GetMouseButton(0) → LATCH to PinnedProvinceId. The
            //     first frame the mouse is down on a label, we pin that
            //     province. The pin survives across mouse release so the
            //     button stays visible past the click frame.
            //  3. IsPinnedTo(province.Id) — for subsequent Show calls on the
            //     pinned province, treat as click context.
            // A click on a DIFFERENT label re-pins (step 2 latches the new
            // id). The Close button calls Unpin().
            bool mouseDown = UnityEngine.Input.GetMouseButton(0);
            if (mouseDown)
                ProvincePopupInvocationContext.PinTo(province.Id);

            bool clickContext = ProvincePopupInvocationContext.IsActiveClickInvocation
                                || ProvincePopupInvocationContext.IsPinnedTo(province.Id);

            if (lastShownLoggedProvince != province.Id)
            {
                lastShownLoggedProvince = province.Id;
                string controller = "<none>";
                try { controller = province.ControllerPlayer?.Name(false) ?? "<none>"; }
                catch { }
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC land-invasion: Show postfix for province={province.Id} controller={controller} " +
                    $"clickFlag={ProvincePopupInvocationContext.IsActiveClickInvocation} " +
                    $"mouseDown={mouseDown} pinned='{ProvincePopupInvocationContext.PinnedProvinceId}' → clickContext={clickContext}.");
            }

            if (!clickContext)
            {
                // Hide any previously-added MC button so a tooltip from hover
                // looks like vanilla and the pin/exit patches don't engage.
                HideVpButtonsIfPresent(__instance);
                return;
            }

            Button? launch = EnsureLaunchButton(__instance);
            if (launch == null)
                return;

            EnsureCloseButton(__instance);
            RefreshLaunchButton(__instance, launch, province);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC land-invasion: CampaignProvincePopupUI.Show postfix failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Button? EnsureLaunchButton(CampaignProvincePopupUI popup)
    {
        RectTransform? parent = popup.LayoutRoot;
        if (parent == null)
        {
            Melon<MintChipPlusMod>.Logger.Warning("UADMC land-invasion: CampaignProvincePopupUI.LayoutRoot is null.");
            return null;
        }

        Transform? existing = parent.Find(LaunchLandInvasionButtonName);
        if (existing != null)
            return existing.GetComponent<Button>();

        // Find a vanilla button to clone. PortPopupUI is always loaded with the
        // campaign UI and its MoveShips button has the styling we want.
        Button? template = LocateButtonTemplate();
        if (template == null)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                "UADMC land-invasion: no button template available yet; will retry on next popup open.");
            return null;
        }

        GameObject buttonObject = UnityEngine.Object.Instantiate(template.gameObject, parent);
        buttonObject.name = LaunchLandInvasionButtonName;
        buttonObject.transform.SetAsLastSibling();

        Button button = buttonObject.GetComponent<Button>() ?? buttonObject.AddComponent<Button>();
        button.onClick.RemoveAllListeners();

        // Strip inherited vanilla behaviour: MC owns the label, color, and tooltip.
        RemoveComponent<LocalizeText>(buttonObject);
        TMP_Text? label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            RemoveComponent<LocalizeText>(label.gameObject);
        RemoveComponent<OnEnter>(buttonObject);
        RemoveComponent<OnLeave>(buttonObject);

        button.onClick.AddListener(new System.Action(() => ConfirmLaunch(popup)));

        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC land-invasion: added Launch Land Invasion button to CampaignProvincePopupUI (cloned from {template.name}).");
        return button;
    }

    // When the popup is opened on hover (no click context), strip our buttons
    // so the popup behaves as a vanilla tooltip. The buttons are merely
    // hidden via SetActive(false), not destroyed, so a subsequent click can
    // re-enable them without re-cloning the template.
    private static void HideVpButtonsIfPresent(CampaignProvincePopupUI popup)
    {
        RectTransform? parent = popup.LayoutRoot;
        if (parent == null) return;
        foreach (string name in new[] { LaunchLandInvasionButtonName, CloseProvincePopupButtonName })
        {
            Transform? child = parent.Find(name);
            if (child != null && child.gameObject.activeSelf)
                child.gameObject.SetActive(false);
        }
    }

    // The province popup pins open while our Launch button is attached (see
    // CampaignProvincePopupPinPatch + CampaignProvinceLabelMouseExitPatch),
    // so the user needs an explicit dismiss affordance. Adds a small Close
    // button that calls popup.Hide().
    private static Button? EnsureCloseButton(CampaignProvincePopupUI popup)
    {
        RectTransform? parent = popup.LayoutRoot;
        if (parent == null) return null;

        Transform? existing = parent.Find(CloseProvincePopupButtonName);
        if (existing != null) return existing.GetComponent<Button>();

        Button? template = LocateButtonTemplate();
        if (template == null) return null;

        GameObject buttonObject = UnityEngine.Object.Instantiate(template.gameObject, parent);
        buttonObject.name = CloseProvincePopupButtonName;
        buttonObject.transform.SetAsLastSibling();

        Button button = buttonObject.GetComponent<Button>() ?? buttonObject.AddComponent<Button>();
        button.onClick.RemoveAllListeners();
        // Force interactable: the cloned PortPopupUI.MoveShips template may
        // have been non-interactable at the moment of the clone (e.g. no
        // ships to move in that port). Our Close button should always be
        // clickable.
        button.interactable = true;
        RemoveComponent<LocalizeText>(buttonObject);
        TMP_Text? label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            RemoveComponent<LocalizeText>(label.gameObject);
            label.text = "Close";
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
        }
        RemoveComponent<OnEnter>(buttonObject);
        RemoveComponent<OnLeave>(buttonObject);

        button.onClick.AddListener(new System.Action(() =>
        {
            try
            {
                ProvincePopupInvocationContext.Unpin();
                popup.Hide();
            }
            catch (Exception ex)
            {
                Melon<MintChipPlusMod>.Logger.Warning(
                    $"UADMC land-invasion: Close click failed. {ex.GetType().Name}: {ex.Message}");
            }
        }));

        Melon<MintChipPlusMod>.Logger.Msg(
            "UADMC land-invasion: added Close button to province popup.");
        return button;
    }

    private static Button? LocateButtonTemplate()
    {
        // Prefer PortWindow (full popup with action buttons). PortPopup is the
        // small/hover variant and typically has these fields null.
        try
        {
            Button? b = TryGetButtonsFrom(G.ui?.PortWindow);
            if (b != null) return b;
            b = TryGetButtonsFrom(G.ui?.PortPopup);
            if (b != null) return b;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC land-invasion: button template lookup failed. {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static Button? TryGetButtonsFrom(PortPopupUI? popup)
    {
        if (popup == null) return null;
        if (popup.MoveShips != null) return popup.MoveShips;
        if (popup.Repair != null) return popup.Repair;
        if (popup.MoveSubmarines != null) return popup.MoveSubmarines;
        return null;
    }

    private static void RefreshLaunchButton(CampaignProvincePopupUI popup, Button button, Province province)
    {
        Player? attacker = ExtraGameData.MainPlayer();
        var status = CampaignInvasionActions.CheckLandInvasion(
            attacker, province,
            out Player? defender,
            out Province? _attackerProvince,
            out string reason);

        // Hard-blocked targets (own territory, no land border, conquest in
        // progress) hide the buttons entirely so the popup reverts to vanilla
        // tooltip behaviour. Soft-blocked targets currently don't apply for
        // land invasions (no per-area tonnage gate), but the dispatch is
        // ready if we add any later.
        bool show = status != CampaignInvasionActions.InvasionTargetStatus.HardBlocked;
        bool clickable = status == CampaignInvasionActions.InvasionTargetStatus.Allowed;
        button.gameObject.SetActive(show);
        button.interactable = clickable;

        Transform? closeBtn = popup.LayoutRoot?.Find(CloseProvincePopupButtonName);
        if (closeBtn != null)
            closeBtn.gameObject.SetActive(show);

        TMP_Text? text = button.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
        {
            text.text = "Launch Land Invasion";
            text.color = clickable ? LaunchLandColor : LaunchLandBlockedColor;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.enableAutoSizing = true;
            text.fontSizeMin = 11f;
            text.fontSizeMax = Mathf.Min(text.fontSizeMax > 0f ? text.fontSizeMax : text.fontSize, 22f);
            text.overflowMode = TextOverflowModes.Ellipsis;
        }

        LogState(popup, attacker, province, defender, clickable, reason);
    }

    private static void ConfirmLaunch(CampaignProvincePopupUI popup)
    {
        if (!LastShownProvince.TryGetValue(popup.Pointer, out Province? province) || province == null)
        {
            MessageBoxUI.Show("Launch Land Invasion", "Could not determine which province was selected.");
            return;
        }

        Player? attacker = ExtraGameData.MainPlayer();
        if (!CampaignInvasionActions.CanLaunchLandInvasion(
            attacker, province,
            out Player? defender,
            out Province? attackerProvince,
            out string reason))
        {
            MessageBoxUI.Show("Launch Land Invasion", reason);
            return;
        }

        bool alreadyAtWar = IsAlreadyAtWar(attacker, defender);
        bool isAlly = CampaignInvasionActions.IsAllied(attacker, defender);
        string allyClause = isAlly
            ? $"\n\n⚠ {defender!.Name(false)} is your SWORN ALLY. Proceeding will " +
              "cancel the alliance and apply a severe reputation hit on top of the " +
              "normal war declaration — every other major will see this as a betrayal."
            : string.Empty;
        string warClause = alreadyAtWar
            ? string.Empty
            : $"\n\nYou are not at war with {defender!.Name(false)}. " +
              "Launching this invasion will declare war and damage your " +
              "standing with other major powers (final values ±10%):" +
              BuildPenaltyPreview(attacker!, defender, province);

        string message =
            $"Launch a land invasion of {SafeProvinceName(province)} from {SafeProvinceName(attackerProvince)} " +
            $"against {defender!.Name(false)}?" + allyClause + warClause;
        string yes = LocalizeManager.Localize("$Ui_Popup_Generic_Yes");
        string no = LocalizeManager.Localize("$Ui_Popup_Generic_No");

        MessageBoxUI.Show(
            "Launch Land Invasion",
            message,
            null,
            true,
            yes,
            no,
            new System.Action(() =>
            {
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC land-invasion: confirm-Yes received for province={province.Id}; calling LaunchLandInvasion…");
                bool launched = false;
                try
                {
                    launched = CampaignInvasionActions.LaunchLandInvasion(attacker!, province);
                }
                catch (Exception ex)
                {
                    Melon<MintChipPlusMod>.Logger.Warning(
                        $"UADMC land-invasion: confirm-Yes LaunchLandInvasion threw. {ex.GetType().Name}: {ex.Message}");
                }
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC land-invasion: LaunchLandInvasion returned launched={launched}.");
                if (launched)
                {
                    try
                    {
                        ProvincePopupInvocationContext.Unpin();
                        popup.Hide();
                        Melon<MintChipPlusMod>.Logger.Msg("UADMC land-invasion: popup unpinned + hidden.");
                    }
                    catch (Exception ex)
                    {
                        Melon<MintChipPlusMod>.Logger.Warning(
                            $"UADMC land-invasion: Unpin/Hide threw. {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    Button? launch = FindLaunchButton(popup);
                    if (launch != null)
                        RefreshLaunchButton(popup, launch, province);
                }
            }),
            null);
    }

    private static Province? FindProvinceById(string provinceId)
    {
        if (string.IsNullOrEmpty(provinceId)) return null;
        if (CampaignController.Instance?.CampaignData?.Players == null) return null;

        // Province lookup: iterate every player's provinces. Vanilla doesn't
        // expose an "all provinces" collection on CampaignData, so we union
        // per-player. Player.provinces is a small list per player.
        foreach (Player p in CampaignController.Instance.CampaignData.Players)
        {
            Il2CppSystem.Collections.Generic.List<Province>? provs = p?.provinces;
            if (provs == null) continue;
            foreach (Province prov in provs)
            {
                if (prov != null && prov.Id == provinceId)
                    return prov;
            }
        }
        return null;
    }

    private static string BuildPenaltyPreview(Player attacker, Player defender, Province province)
    {
        try
        {
            List<InvasionDiplomaticConsequences.Penalty> previews =
                InvasionDiplomaticConsequences.Preview(attacker, defender, province);
            if (previews.Count == 0)
                return "\n  • No notable diplomatic reaction from other majors.";

            var grouped = previews
                .GroupBy(p => Math.Round(p.Base, 1))
                .OrderBy(g => g.Key);
            System.Text.StringBuilder sb = new();
            foreach (var group in grouped)
            {
                string names = string.Join(", ", group.Select(p => p.Target.Name(false)));
                string reasons = string.Join(" / ",
                    group.Select(p => p.Reason).Distinct(StringComparer.OrdinalIgnoreCase));
                sb.AppendLine().Append($"  • {names}: {group.Key:+0.#;-0.#;0} ({reasons})");
            }
            return sb.ToString();
        }
        catch
        {
            return "\n  • (could not compute preview)";
        }
    }

    private static bool IsAlreadyAtWar(Player? attacker, Player? defender)
    {
        if (attacker == null || defender == null) return false;
        try
        {
            var relations = CampaignController.Instance?.CampaignData?.Relations;
            if (relations == null) return false;
            Relation? rel = RelationExt.Between(relations, attacker, defender);
            return rel != null && rel.isWar;
        }
        catch { return false; }
    }

    private static Button? FindLaunchButton(CampaignProvincePopupUI popup)
    {
        Transform? existing = popup.LayoutRoot?.Find(LaunchLandInvasionButtonName);
        return existing == null ? null : existing.GetComponent<Button>();
    }

    private static string SafeProvinceName(Province? province)
    {
        if (province == null) return "<unknown>";
        try { return string.IsNullOrWhiteSpace(province.Id) ? "<unknown>" : province.Id; }
        catch { return "<unknown>"; }
    }

    private static void LogState(
        CampaignProvincePopupUI popup,
        Player? attacker,
        Province province,
        Player? defender,
        bool canLaunch,
        string reason)
    {
        if (popup == null || popup.Pointer == IntPtr.Zero) return;

        string state = canLaunch
            ? $"enabled attacker={attacker?.Name(false) ?? "<none>"} -> defender={defender?.Name(false) ?? "<none>"} province={SafeProvinceName(province)}"
            : $"blocked attacker={attacker?.Name(false) ?? "<none>"} -> defender={defender?.Name(false) ?? "<none>"} province={SafeProvinceName(province)}: {reason}";

        if (LastLoggedState.TryGetValue(popup.Pointer, out string? last) && last == state)
            return;
        LastLoggedState[popup.Pointer] = state;
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC land-invasion button {state}");
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }
}
