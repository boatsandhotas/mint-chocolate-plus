using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;
using UnityEngine.UI;

namespace MintChipPlus.Harmony;

// Patch intent: vanilla only spawns naval invasions through AI logic on certain
// turn ticks. MC adds a Launch Invasion button to the port popup so the player
// can target an enemy port directly, then funnels the click through vanilla's
// own CampaignController.CreateConquestEvent so save/load, AI awareness, and
// downstream diplomacy stay on the same path as AI-generated conquests.
[HarmonyPatch(typeof(PortPopupUI), nameof(PortPopupUI.Show))]
internal static class CampaignLaunchInvasionPatch
{
    private const string LaunchInvasionButtonName = "UADMC_LaunchInvasion";
    private static readonly Color LaunchInvasionColor = new(1f, 0.45f, 0.18f, 1f);
    private static readonly Color LaunchInvasionBlockedColor = new(0.7f, 0.5f, 0.4f, 1f);
    private static readonly Dictionary<IntPtr, PortElement> LastShownPort = new();
    private static readonly Dictionary<IntPtr, string> LastLoggedState = new();
    private static readonly HashSet<IntPtr> LoggedNoSourceForPopup = new();

    [HarmonyPostfix]
    private static void Postfix(PortPopupUI __instance, PortElement port)
    {
        try
        {
            if (__instance == null)
                return;

            LastShownPort[__instance.Pointer] = port;

            Button? source = ChooseSourceButton(__instance);
            if (source == null)
            {
                // PortPopup (small/hover variant) has no action buttons —
                // MoveShips/Repair/MoveSubmarines are null. Silently skip;
                // CampaignEnemyPortPopupPatch force-opens PortWindow (the
                // full popup) on click which DOES have buttons, so the
                // launch button attaches over there. Log only on first
                // sighting of this specific popup instance to surface
                // unexpected non-small no-button cases without spam.
                if (!LoggedNoSourceForPopup.Contains(__instance.Pointer))
                {
                    LoggedNoSourceForPopup.Add(__instance.Pointer);
                    string which =
                        (__instance == G.ui?.PortWindow) ? "PortWindow" :
                        (__instance == G.ui?.PortPopup)  ? "PortPopup" :
                                                           "unknown";
                    Melon<MintChipPlusMod>.Logger.Msg(
                        $"UADMC launch-invasion: no source button on PortPopupUI (instance={which}, " +
                        $"smallVersion={__instance.SmallVersion}); will not attach to this popup variant.");
                }
                return;
            }

            Button? launch = EnsureLaunchButton(__instance, source);
            if (launch == null)
                return;

            RefreshLaunchButton(__instance, launch, port);
            LayoutButtonRow(__instance, launch);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC launch-invasion: PortPopupUI.Show postfix failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Button? ChooseSourceButton(PortPopupUI popup)
    {
        // Prefer Repair as the clone template because it lives in the same row
        // as the action buttons and tends to be present on any port view; fall
        // back to MoveShips if Repair is hidden on the current popup variant.
        if (popup.Repair != null) return popup.Repair;
        if (popup.MoveShips != null) return popup.MoveShips;
        return popup.MoveSubmarines;
    }

    private static Button? EnsureLaunchButton(PortPopupUI popup, Button source)
    {
        Transform? parent = source.transform.parent;
        if (parent == null)
            return null;

        Transform? existing = parent.Find(LaunchInvasionButtonName);
        if (existing != null)
            return existing.GetComponent<Button>();

        GameObject buttonObject = UnityEngine.Object.Instantiate(source.gameObject, parent);
        buttonObject.name = LaunchInvasionButtonName;

        try { buttonObject.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1); }
        catch { }

        Button button = buttonObject.GetComponent<Button>() ?? buttonObject.AddComponent<Button>();
        button.onClick.RemoveAllListeners();

        // Cloned button inherits vanilla's localization and tooltip; strip them
        // so MC fully owns the label/tooltip surface.
        RemoveComponent<LocalizeText>(buttonObject);
        TMP_Text? label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            RemoveComponent<LocalizeText>(label.gameObject);
        RemoveComponent<OnEnter>(buttonObject);
        RemoveComponent<OnLeave>(buttonObject);

        button.onClick.AddListener(new System.Action(() => ConfirmLaunch(popup)));

        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC launch-invasion: added Launch Invasion button to PortPopupUI (cloned from {source.name}).");
        return button;
    }

    private static void RefreshLaunchButton(PortPopupUI popup, Button button, PortElement port)
    {
        Player? attacker = ExtraGameData.MainPlayer();
        var status = CampaignInvasionActions.CheckNavalInvasion(
            attacker, port,
            out Player? defender,
            out Player? _majorAlly,
            out Province? _province,
            out string reason);

        // Hard-blocked targets (own port, conquest already in progress) hide
        // the button entirely. Soft-blocked targets (insufficient tonnage)
        // show the button as DISABLED so the player sees the action is
        // available in principle and can read the reason. Allowed makes it
        // clickable.
        bool show = status != CampaignInvasionActions.InvasionTargetStatus.HardBlocked;
        bool clickable = status == CampaignInvasionActions.InvasionTargetStatus.Allowed;
        button.gameObject.SetActive(show);
        button.interactable = clickable;

        TMP_Text? text = button.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
        {
            text.text = "Launch Invasion";
            text.color = clickable ? LaunchInvasionColor : LaunchInvasionBlockedColor;
        }

        // Attach a tooltip with the blocked reason so the disabled state is
        // self-explanatory ("Not enough tonnage in alaska_gulf area…"). When
        // the button is allowed, clear the tooltip so it doesn't show stale
        // text on the actionable state.
        if (show && !clickable && !string.IsNullOrWhiteSpace(reason))
            AttachTooltip(button.gameObject, reason);
        else
            ClearTooltip(button.gameObject);

        LogState(popup, attacker, port, defender, clickable, reason);
    }

    private static void AttachTooltip(GameObject target, string text)
    {
        try
        {
            ClearTooltip(target);
            OnEnter onEnter = target.AddComponent<OnEnter>();
            onEnter.action = new System.Action(() =>
            {
                try { G.ui.ShowTooltip(text, target); }
                catch { }
            });
            OnLeave onLeave = target.AddComponent<OnLeave>();
            onLeave.action = new System.Action(() =>
            {
                try { G.ui.HideTooltip(); }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC launch-invasion: tooltip attach failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ClearTooltip(GameObject target)
    {
        try
        {
            RemoveComponent<OnEnter>(target);
            RemoveComponent<OnLeave>(target);
        }
        catch { }
    }

    private static void ConfirmLaunch(PortPopupUI popup)
    {
        if (!LastShownPort.TryGetValue(popup.Pointer, out PortElement? port) || port == null)
        {
            MessageBoxUI.Show("Launch Invasion", "Could not determine which port was selected.");
            return;
        }

        Player? attacker = ExtraGameData.MainPlayer();
        if (!CampaignInvasionActions.CanLaunchNavalInvasion(
            attacker, port,
            out Player? defender,
            out Player? _majorAlly,
            out Province? province,
            out string reason))
        {
            MessageBoxUI.Show("Launch Invasion", reason);
            return;
        }

        bool alreadyAtWar = IsAlreadyAtWar(attacker, defender);
        bool isAlly = CampaignInvasionActions.IsAllied(attacker, defender);
        string title = "Launch Invasion";
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
              BuildPenaltyPreview(attacker!, defender, province!);
        string message =
            $"Launch a naval invasion of {SafePortName(port)} ({SafeProvinceId(province)}) " +
            $"against {defender!.Name(false)}?\n\n" +
            "Your task forces in the area will contribute tonnage toward the required force." +
            allyClause + warClause;
        string yes = LocalizeManager.Localize("$Ui_Popup_Generic_Yes");
        string no = LocalizeManager.Localize("$Ui_Popup_Generic_No");

        MessageBoxUI.Show(
            title,
            message,
            null,
            true,
            yes,
            no,
            new System.Action(() =>
            {
                CampaignInvasionActions.LaunchNavalInvasion(attacker!, port);
                // Refresh the popup state so the button greys out after launch.
                Button? launch = FindLaunchButton(popup);
                if (launch != null)
                    RefreshLaunchButton(popup, launch, port);
            }),
            null);
    }

    // Width fraction applied to every active port-popup button when our extra
    // button is present. With vanilla's 4 native buttons (MoveShips,
    // MoveSubmarines, Repair, Close) plus our Launch Invasion, 4/5 = 0.8 keeps
    // the row at its original total width.
    private const float CrowdedButtonWidthFraction = 0.8f;
    private static readonly Dictionary<IntPtr, float> OriginalButtonWidths = new();

    private static void LayoutButtonRow(PortPopupUI popup, Button launchButton)
    {
        try
        {
            // popup.Close is the corner X-button, not a row peer — exclude it
            // from the resize set so we don't shrink the close button or
            // factor its width into the row math.
            Button?[] candidates =
            {
                popup.MoveShips,
                popup.MoveSubmarines,
                popup.Repair,
                launchButton,
            };

            List<Button> active = new();
            foreach (Button? b in candidates)
                if (b != null && b.gameObject.activeInHierarchy)
                    active.Add(b);

            if (active.Count < 2) return;

            // Snapshot vanilla widths the first time we touch each button so
            // we always shrink from the original, not from a previous shrink.
            foreach (Button b in active)
            {
                if (OriginalButtonWidths.ContainsKey(b.Pointer)) continue;

                RectTransform? rect = b.GetComponent<RectTransform>();
                LayoutElement? layout = b.GetComponent<LayoutElement>();
                float origWidth = layout != null && layout.preferredWidth > 0f
                    ? layout.preferredWidth
                    : rect != null ? rect.sizeDelta.x : 0f;
                if (origWidth > 0f)
                    OriginalButtonWidths[b.Pointer] = origWidth;
            }

            // Apply 80% width to every button via BOTH sizeDelta AND
            // LayoutElement. Some popup variants use a HorizontalLayoutGroup
            // that ignores LayoutElement.preferredWidth (childControlWidth
            // disabled or childForceExpandWidth enabled), so we coerce the
            // layout group's settings AND set sizeDelta directly.
            Transform? parent = active[0].transform.parent;
            HorizontalLayoutGroup? horizontalLayout = parent?.GetComponent<HorizontalLayoutGroup>();
            RectTransform? parentRect = parent?.GetComponent<RectTransform>();

            // Pre-layout diagnostic: dump the parent's geometry and every
            // active button's rect so we can see what's actually in play.
            // Logged once on first encounter only (after that the original
            // widths are cached, no need to re-log every popup open).
            bool firstPass = active.Any(b => !OriginalButtonWidths.ContainsKey(b.Pointer));
            if (firstPass)
            {
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC launch-invasion: PRE parent.rect.width={(parentRect != null ? parentRect.rect.width : -1f):0} " +
                    $"hlg={(horizontalLayout != null)} " +
                    $"hlg.spacing={(horizontalLayout != null ? horizontalLayout.spacing : -1f):0} " +
                    $"hlg.childCtrlW={(horizontalLayout != null ? horizontalLayout.childControlWidth : false)} " +
                    $"hlg.childForceExpW={(horizontalLayout != null ? horizontalLayout.childForceExpandWidth : false)}");
                foreach (Button b in active)
                {
                    RectTransform? rr = b.GetComponent<RectTransform>();
                    LayoutElement? le = b.GetComponent<LayoutElement>();
                    ContentSizeFitter? csf = b.GetComponent<ContentSizeFitter>();
                    if (rr == null) continue;
                    Melon<MintChipPlusMod>.Logger.Msg(
                        $"UADMC launch-invasion: PRE {b.name} rect.width={rr.rect.width:0} " +
                        $"anchorMin={rr.anchorMin} anchorMax={rr.anchorMax} pivot={rr.pivot} " +
                        $"sizeDelta={rr.sizeDelta} anchoredPos={rr.anchoredPosition} " +
                        $"le.prefW={(le != null ? le.preferredWidth : -1f):0} " +
                        $"le.flexW={(le != null ? le.flexibleWidth : -1f):0} " +
                        $"le.minW={(le != null ? le.minWidth : -1f):0} " +
                        $"csf.horiz={(csf != null ? csf.horizontalFit.ToString() : "none")}");
                }
            }

            if (horizontalLayout != null)
            {
                // Tell HLG to distribute parent width evenly among its active
                // children. With flexibleWidth=1 on each button (set below),
                // 4 buttons each get (parent - 3*spacing)/4; with 5 buttons
                // (our extra) each gets (parent - 4*spacing)/5. Hidden
                // buttons are excluded automatically.
                if (horizontalLayout.spacing > 12f) horizontalLayout.spacing = 12f;
                horizontalLayout.childForceExpandWidth = true;
                horizontalLayout.childControlWidth = true;
            }

            List<(Button button, RectTransform rect)> rects = new();
            foreach (Button b in active)
            {
                RectTransform? rect = b.GetComponent<RectTransform>();
                if (rect != null) rects.Add((b, rect));
            }
            if (rects.Count < 2) return;

            // Manual-anchor path: also redistribute positions so the row
            // doesn't develop gaps. Only kicks in when there's no layout group.
            if (horizontalLayout == null)
            {
                float left = float.MaxValue, right = float.MinValue;
                foreach ((Button _, RectTransform r) in rects)
                {
                    float l = r.anchoredPosition.x - r.sizeDelta.x * r.pivot.x;
                    float ri = r.anchoredPosition.x + r.sizeDelta.x * (1f - r.pivot.x);
                    if (l < left) left = l;
                    if (ri > right) right = ri;
                }
                float span = right - left;
                int count = rects.Count;
                float width = OriginalButtonWidthFor(rects[0].button) * CrowdedButtonWidthFraction;
                float gap = count > 1 ? (span - width * count) / (count - 1) : 0f;
                if (gap < 4f) gap = 4f;

                for (int i = 0; i < count; i++)
                {
                    RectTransform r = rects[i].rect;
                    r.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                    Vector2 pos = r.anchoredPosition;
                    pos.x = left + i * (width + gap) + width * r.pivot.x;
                    r.anchoredPosition = pos;
                    SetPreferredButtonWidth(rects[i].button, width);
                }
            }
            else
            {
                // HLG path with flex: let the layout group do the math. Set
                // flexibleWidth=1 so HLG distributes available space evenly,
                // and a minWidth floor so buttons stay readable. Clear any
                // stale preferredWidth from earlier code paths so flex takes
                // priority.
                foreach ((Button b, RectTransform _) in rects)
                {
                    LayoutElement layout = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
                    layout.flexibleWidth = 1f;
                    layout.preferredWidth = -1f;
                    if (layout.minWidth <= 0f) layout.minWidth = 60f;
                }
            }

            // Diagnostic: log actual measured widths AND parent width so we
            // can tell whether the buttons exceed the popup's row container.
            // If sum-of-widths-plus-spacing > parent.rect.width, overlap is
            // expected and we need a different fix (shrink further or widen
            // the parent).
            System.Text.StringBuilder sb = new("UADMC launch-invasion: post-layout widths");
            float total = 0f;
            foreach ((Button b, RectTransform r) in rects)
            {
                sb.Append($" {b.name}={r.rect.width:0}");
                total += r.rect.width;
            }
            float spacingTotal = (rects.Count > 1 && horizontalLayout != null)
                ? horizontalLayout.spacing * (rects.Count - 1)
                : 0f;
            float parentWidth = parentRect != null ? parentRect.rect.width : -1f;
            sb.Append($" | totalButtonWidth={total:0} totalSpacing={spacingTotal:0} parent.rect.width={parentWidth:0}");
            if (parentWidth > 0 && total + spacingTotal > parentWidth)
                sb.Append($" → OVERFLOW by {(total + spacingTotal - parentWidth):0}px");
            Melon<MintChipPlusMod>.Logger.Msg(sb.ToString());
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC launch-invasion: LayoutButtonRow failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static float OriginalButtonWidthFor(Button b)
    {
        if (OriginalButtonWidths.TryGetValue(b.Pointer, out float w)) return w;
        RectTransform? rect = b.GetComponent<RectTransform>();
        return rect != null ? rect.sizeDelta.x : 0f;
    }

    private static void SetPreferredButtonWidth(Button button, float width)
    {
        LayoutElement? layout = button.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = width;
            if (layout.minWidth > width) layout.minWidth = width;
            layout.flexibleWidth = 0f;
        }
    }

    private static string BuildPenaltyPreview(Player attacker, Player defender, Province province)
    {
        try
        {
            List<InvasionDiplomaticConsequences.Penalty> previews =
                InvasionDiplomaticConsequences.Preview(attacker, defender, province);
            if (previews.Count == 0)
                return "\n  • No notable diplomatic reaction from other majors.";

            // Group identical penalty amounts so e.g. five "-5" general-aggression
            // hits fold into one line. Reasons are deduped per group.
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
            if (CampaignController.Instance?.CampaignData?.Relations == null)
                return false;
            Relation? rel = RelationExt.Between(
                CampaignController.Instance.CampaignData.Relations, attacker, defender);
            return rel != null && rel.isWar;
        }
        catch { return false; }
    }

    private static Button? FindLaunchButton(PortPopupUI popup)
    {
        Button? source = ChooseSourceButton(popup);
        Transform? parent = source?.transform?.parent;
        if (parent == null)
            return null;

        Transform? existing = parent.Find(LaunchInvasionButtonName);
        return existing == null ? null : existing.GetComponent<Button>();
    }

    private static string SafePortName(PortElement? port)
    {
        if (port == null) return "<unknown>";
        try
        {
            string id = port.Id;
            return string.IsNullOrWhiteSpace(id) ? "<unknown>" : id;
        }
        catch { return "<unknown>"; }
    }

    private static string SafeProvinceId(Province? province)
    {
        if (province == null) return "<unknown>";
        try { return string.IsNullOrWhiteSpace(province.Id) ? "<unknown>" : province.Id; }
        catch { return "<unknown>"; }
    }

    private static void LogState(
        PortPopupUI popup,
        Player? attacker,
        PortElement? port,
        Player? defender,
        bool canLaunch,
        string reason)
    {
        if (popup == null || popup.Pointer == IntPtr.Zero)
            return;

        string state = canLaunch
            ? $"enabled attacker={attacker?.Name(false) ?? "<none>"} -> defender={defender?.Name(false) ?? "<none>"} port={SafePortName(port)}"
            : $"blocked attacker={attacker?.Name(false) ?? "<none>"} -> defender={defender?.Name(false) ?? "<none>"} port={SafePortName(port)}: {reason}";

        if (LastLoggedState.TryGetValue(popup.Pointer, out string? last) && last == state)
            return;

        LastLoggedState[popup.Pointer] = state;
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC launch-invasion button {state}");
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }
}
