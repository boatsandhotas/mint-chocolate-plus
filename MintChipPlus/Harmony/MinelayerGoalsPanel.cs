using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8603
#pragma warning disable CS8604
#pragma warning disable CS8618
#pragma warning disable CS8625

namespace MintChipPlus.Harmony;

// "Minelayer Port Goals" panel — set a DEFAULT per-port minelayer composition (count of each
// variant) plus PER-PORT OVERRIDES, then Rebalance to place idle (unassigned) minelayers into
// quota-deficit ports. Reads/writes MinelayerGoals (per-campaign) and uses MinelayerPlanner (the
// save-tool AssignSubmarines port) — assigned and in-transit subs are never touched.
//
// Trigger: campaign hotkey F8 (toggle). Self-contained UI (own Canvas + helpers), gated by
// CampaignController being present. Log prefix for Plan/Rebalance: "UADMC_MINEREBAL".
internal static class MinelayerGoalsPanel
{
    private const KeyCode ToggleKey = KeyCode.F8;

    private static readonly Color PanelBg = new(0.05f, 0.05f, 0.06f, 0.97f);
    private static readonly Color HeaderBg = new(0.16f, 0.30f, 0.50f, 0.96f);
    private static readonly Color PaneBg = new(0f, 0f, 0f, 0.12f);
    private static readonly Color ScrollBg = new(0f, 0f, 0f, 0.28f);
    private static readonly Color RowIdle = new(0.12f, 0.12f, 0.14f, 0.95f);
    private static readonly Color RowSelected = new(0.50f, 0.40f, 0.16f, 0.98f);
    private static readonly Color RowDeficit = new(0.34f, 0.18f, 0.12f, 0.96f);
    private static readonly Color RegionHdrBg = new(0.16f, 0.22f, 0.30f, 0.97f);
    private static readonly Color BtnBg = new(0.18f, 0.26f, 0.20f, 0.97f);
    private static readonly Color StepBg = new(0.16f, 0.18f, 0.22f, 0.97f);
    private static readonly Color CloseBtn = new(0.34f, 0.16f, 0.16f, 0.96f);
    private static readonly Color ApplyBtn = new(0.20f, 0.42f, 0.24f, 0.98f);

    private static Canvas? canvas;
    private static GameObject? panel;
    private static RectTransform? panelRect;
    private static Transform? leftPane;
    private static Transform? portListContent;

    private static MinelayerGoals.Goal goal = new();
    private static MinelayerPlanner.Plan plan = new();
    private static string? selectedPort;

    private static bool IsOpen => canvas != null && panel != null;

    internal static void TryHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(ToggleKey))
                return;
            if (CampaignController.Instance == null)
                return;
            if (Util.FocusIsInInputField())
                return;
            Toggle();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC minelayer goals: hotkey failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Toggle()
    {
        if (IsOpen) { Close(); return; }
        Open();
    }

    private static void Open()
    {
        goal = MinelayerGoals.Load();
        Recompute();
        Close();
        BuildPanel();
    }

    private static void Close()
    {
        try { if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject); }
        catch { }
        finally { canvas = null; panel = null; panelRect = null; leftPane = null; portListContent = null; }
    }

    private static void Recompute() => plan = MinelayerPlanner.Compute(goal);

    // ----- panel frame -----

    private static void BuildPanel()
    {
        GameObject canvasGo = new("UADMC_MinelayerGoalsCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32000;
        canvasGo.AddComponent<GraphicRaycaster>();
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1.4f; // enlarge the whole panel + text for readability

        panel = new GameObject("UADMC_MinelayerGoalsPanel");
        panel.transform.SetParent(canvas.transform, false);
        panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(900f, 600f);
        panelRect.anchoredPosition = Vector2.zero;
        Image bg = panel.AddComponent<Image>();
        bg.color = PanelBg;
        bg.raycastTarget = true;

        VerticalLayoutGroup vl = panel.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset { left = 10, right = 10, top = 8, bottom = 10 };
        vl.spacing = 8f;
        vl.childControlWidth = vl.childControlHeight = vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;

        BuildHeader(panel.transform);

        GameObject body = new("Body");
        body.transform.SetParent(panel.transform, false);
        HorizontalLayoutGroup bl = body.AddComponent<HorizontalLayoutGroup>();
        bl.spacing = 10f;
        bl.childControlWidth = bl.childControlHeight = bl.childForceExpandHeight = true;
        bl.childForceExpandWidth = false;
        AddLayoutElement(body, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 360f);

        // Left: default composition + selected-port override + actions + status.
        GameObject left = new("Left");
        left.transform.SetParent(body.transform, false);
        Image li = left.AddComponent<Image>();
        li.color = PaneBg;
        VerticalLayoutGroup lvl = left.AddComponent<VerticalLayoutGroup>();
        lvl.padding = new RectOffset { left = 8, right = 8, top = 6, bottom = 8 };
        lvl.spacing = 6f;
        lvl.childControlWidth = lvl.childControlHeight = lvl.childForceExpandWidth = true;
        lvl.childForceExpandHeight = false;
        AddLayoutElement(left, minWidth: 360f, preferredWidth: 380f, flexibleWidth: 0f, flexibleHeight: 1f);
        leftPane = left.transform;

        // Right: per-port list.
        GameObject rightWrap = new("RightWrap");
        rightWrap.transform.SetParent(body.transform, false);
        VerticalLayoutGroup rvl = rightWrap.AddComponent<VerticalLayoutGroup>();
        rvl.spacing = 4f;
        rvl.childControlWidth = rvl.childControlHeight = rvl.childForceExpandWidth = rvl.childForceExpandHeight = true;
        AddLayoutElement(rightWrap, flexibleWidth: 1f, flexibleHeight: 1f);
        MakeText(rightWrap.transform, "Ports (click to override — sorted by deficit):", 12, TextAnchor.MiddleLeft);
        GameObject scroll = BuildScroll(rightWrap.transform, "PortList", out Transform content);
        AddLayoutElement(scroll, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 320f);
        portListContent = content;

        RefreshLeft();
        RefreshPortList();
    }

    private static void BuildHeader(Transform parent)
    {
        GameObject header = new("Header");
        header.transform.SetParent(parent, false);
        Image hb = header.AddComponent<Image>();
        hb.color = HeaderBg;
        HorizontalLayoutGroup hl = header.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset { left = 10, right = 6, top = 2, bottom = 2 };
        hl.spacing = 6f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlWidth = hl.childControlHeight = hl.childForceExpandHeight = true;
        hl.childForceExpandWidth = false;
        AddLayoutElement(header, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        Text title = MakeText(header.transform, "Minelayer Port Goals   —   drag to move,  F8 to close", 15, TextAnchor.MiddleLeft);
        title.raycastTarget = false;
        AddLayoutElement(title.gameObject, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);
        MakeButton(header.transform, "Close", CloseBtn, Close, 96f);
        AddDragHandler(header);
    }

    // ----- left pane: default composition, override editor, actions, status -----

    private static void RefreshLeft()
    {
        if (leftPane == null) return;
        ClearChildren(leftPane.gameObject);

        MakeText(leftPane, "Default composition (every port, unless overridden):", 13, TextAnchor.MiddleLeft);
        if (plan.Variants.Count == 0)
            MakeText(leftPane, "(no minelayer variants found — build or unlock some)", 11, TextAnchor.MiddleLeft);
        foreach (string v in plan.Variants)
        {
            goal.Default.TryGetValue(v, out int cnt);
            string variant = v;
            Stepper(leftPane, v, cnt,
                () => { goal.SetDefault(variant, Math.Max(0, GetDefault(variant) - 1)); Persist(); },
                () => { goal.SetDefault(variant, GetDefault(variant) + 1); Persist(); });
        }

        // selected-port override editor
        if (selectedPort != null && plan.PortById.ContainsKey(selectedPort))
        {
            string port = selectedPort;
            bool overridden = goal.HasOverride(port);
            string pname = plan.PortById.TryGetValue(port, out var prow) ? prow.Name : port;
            MakeText(leftPane, $"\nOverride for: {pname}  {(overridden ? "(custom)" : "(using default)")}", 13, TextAnchor.MiddleLeft);
            foreach (string v in plan.Variants)
            {
                int eff = goal.EffectiveTarget(port, v);
                string variant = v;
                Stepper(leftPane, "  " + v, eff,
                    () => { EditOverride(port, variant, -1); },
                    () => { EditOverride(port, variant, +1); });
            }
            GameObject ovRow = Row(leftPane, 26f);
            MakeButton(ovRow.transform, "Use default", BtnBg, () => { goal.ClearOverride(port); Persist(); }, 110f);
            MakeButton(ovRow.transform, "Skip port", BtnBg, () => { var ov = goal.EnsureOverride(port); ov.Clear(); Persist(); }, 100f);
        }
        else
        {
            MakeText(leftPane, "\n(select a port on the right to override it)", 11, TextAnchor.MiddleLeft);
        }

        // complete-the-set: build missing + assign, with shipyard-capacity warning
        MakeText(leftPane, "\nComplete the set (build missing + assign to deficit ports):", 13, TextAnchor.MiddleLeft);
        float cap = plan.ShipyardCapacity;
        float cur = plan.CurrentBuildingTonnage;
        float totalAdd = 0f;
        bool anyMissing = false;
        foreach (string v in plan.Variants)
        {
            plan.Shortfall.TryGetValue(v, out int miss);
            if (miss <= 0) continue;
            anyMissing = true;
            plan.TonnageByVariant.TryGetValue(v, out float ton);
            totalAdd += miss * ton;
            string variant = v;
            GameObject brow = Row(leftPane, 26f);
            Text bt = MakeText(brow.transform, $"{Short(v)}: build {miss}  (+{miss * ton:0}t)", 12, TextAnchor.MiddleLeft);
            AddLayoutElement(bt.gameObject, minWidth: 200f, preferredWidth: 200f, flexibleWidth: 1f);
            if (plan.TypeByVariant.ContainsKey(v))
                MakeButton(brow.transform, $"Build {miss}+assign", ApplyBtn, () => BuildAndAssign(variant), 150f);
        }
        if (!anyMissing)
            MakeText(leftPane, "  (goal fully met by existing subs — nothing to build)", 11, TextAnchor.MiddleLeft);
        if (cap > 0f)
        {
            float projected = cur + totalAdd;
            bool overNow = cur > cap;
            bool overAfter = totalAdd > 0f && projected > cap;
            Color warn = new(1f, 0.66f, 0.4f, 1f);
            Text l1 = MakeText(leftPane, $"Shipyard: ~{cur:0}t building / {cap:0}t capacity.", 12, TextAnchor.MiddleLeft);
            if (overNow) l1.color = warn;
            if (totalAdd > 0f)
            {
                MakeText(leftPane, $"Build-all adds +{totalAdd:0}t  ->  ~{projected:0}t total.", 12, TextAnchor.MiddleLeft);
                Text l3 = MakeText(leftPane, overAfter ? $"OVER capacity by {projected - cap:0}t (builds slow down)." : "Within capacity.", 12, TextAnchor.MiddleLeft);
                if (overAfter) l3.color = warn;
            }
        }

        // actions
        GameObject actions = Row(leftPane, 30f);
        MakeButton(actions.transform, "Plan (log only)", BtnBg, () => { LogPlan(false); }, 150f);
        MakeButton(actions.transform, "Rebalance now", ApplyBtn, DoRebalance, 150f);

        // status
        int totalDeficit = 0;
        foreach (int s in plan.Shortfall.Values) totalDeficit += s;
        int placeable = plan.Moves.Count;
        MakeText(leftPane,
            $"\nidle (unassigned): {plan.IdleTotal}   assigned: {plan.AssignedTotal}   deployed: {plan.DeployedTotal}\n" +
            $"placements ready: {placeable}   still short (build more): {totalDeficit}",
            12, TextAnchor.UpperLeft);
    }

    private static int GetDefault(string variant) { goal.Default.TryGetValue(variant, out int c); return c; }

    private static void EditOverride(string port, string variant, int delta)
    {
        if (!goal.HasOverride(port))
        {
            // Seed a fresh override from the current default so tweaking one variant doesn't zero the rest.
            var ov = goal.EnsureOverride(port);
            foreach (var kv in goal.Default) ov[kv.Key] = kv.Value;
        }
        int cur = goal.EffectiveTarget(port, variant);
        goal.SetOverride(port, variant, Math.Max(0, cur + delta));
        Persist();
    }

    private static void Persist()
    {
        MinelayerGoals.Save(goal);
        Recompute();
        RefreshLeft();
        RefreshPortList();
    }

    // ----- right pane: port list -----

    private static void RefreshPortList()
    {
        if (portListContent == null) return;
        ClearChildren(portListContent.gameObject);
        if (plan.Ports.Count == 0)
        {
            MakeText(portListContent, "(no ports)", 12, TextAnchor.UpperLeft);
            return;
        }

        // group ports by region label
        var groups = new Dictionary<string, List<MinelayerPlanner.PortRow>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (MinelayerPlanner.PortRow row in plan.Ports)
        {
            string reg = string.IsNullOrEmpty(row.Region) ? "(no region)" : row.Region;
            if (!groups.TryGetValue(reg, out var list)) { groups[reg] = list = new List<MinelayerPlanner.PortRow>(); order.Add(reg); }
            list.Add(row);
        }

        int RegDef(List<MinelayerPlanner.PortRow> g) { int d = 0; foreach (var r in g) d += PortDeficit(r); return d; }
        order.Sort((a, b) => { int da = RegDef(groups[a]), db = RegDef(groups[b]); return da != db ? db.CompareTo(da) : string.Compare(a, b, StringComparison.Ordinal); });

        foreach (string reg in order)
        {
            var g = groups[reg];
            g.Sort((a, b) => { int da = PortDeficit(a), db = PortDeficit(b); return da != db ? db.CompareTo(da) : string.Compare(a.PortId, b.PortId, StringComparison.Ordinal); });

            GameObject hdr = new("RegionHdr");
            hdr.transform.SetParent(portListContent, false);
            Image hi = hdr.AddComponent<Image>();
            hi.color = RegionHdrBg;
            hi.raycastTarget = false;
            AddLayoutElement(hdr, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 1f);
            FillText(hdr.transform, $"{reg}    ({g.Count} ports, def {RegDef(g)})", 13, TextAnchor.MiddleLeft);

            foreach (MinelayerPlanner.PortRow row in g)
            {
                int deficit = PortDeficit(row);
                string portId = row.PortId;

                GameObject go = new("PortRow");
                go.transform.SetParent(portListContent, false);
                Image img = go.AddComponent<Image>();
                img.color = portId == selectedPort ? RowSelected : (deficit > 0 ? RowDeficit : RowIdle);
                img.raycastTarget = true;
                Button b = go.AddComponent<Button>();
                b.targetGraphic = img;
                string captured = portId;
                // Toggle: clicking the already-selected port deselects it (back to default-only editing).
                b.onClick.AddListener(new System.Action(() => { selectedPort = selectedPort == captured ? null : captured; RefreshLeft(); RefreshPortList(); }));
                AddLayoutElement(go, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);

                string tag = goal.HasOverride(portId) ? (IsSkipped(portId) ? " [skip]" : " [custom]") : "";
                FillText(go.transform,
                    $"  {row.Name}{tag}   cur {Compo(row, false)}  tgt {Compo(row, true)}  def {deficit}",
                    12, TextAnchor.MiddleLeft);
            }
        }
    }

    private static bool IsSkipped(string portId) => goal.HasOverride(portId) && goal.Overrides[portId].Count == 0;

    private static int PortDeficit(MinelayerPlanner.PortRow row)
    {
        int d = 0;
        foreach (string v in plan.Variants)
        {
            int t = goal.EffectiveTarget(row.PortId, v);
            row.Current.TryGetValue(v, out int have);
            if (t > have) d += t - have;
        }
        return d;
    }

    // Compact composition string: "II:1 III:0" (target=true uses goal targets, else current counts).
    private static string Compo(MinelayerPlanner.PortRow row, bool target)
    {
        var parts = new List<string>();
        foreach (string v in plan.Variants)
        {
            int n = target ? goal.EffectiveTarget(row.PortId, v) : (row.Current.TryGetValue(v, out int c) ? c : 0);
            if (n > 0 || target) parts.Add($"{Short(v)}:{n}");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "-";
    }

    private static string Short(string variant)
    {
        int sp = variant.LastIndexOf(' ');
        return sp >= 0 && sp < variant.Length - 1 ? variant.Substring(sp + 1) : variant;
    }

    // ----- Plan / Rebalance -----

    private static void LogPlan(bool applied)
    {
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL === {(applied ? "REBALANCED" : "PLAN")} idle={plan.IdleTotal} assigned={plan.AssignedTotal} deployed={plan.DeployedTotal} placements={plan.Moves.Count} ===");
        int shown = 0;
        foreach (MinelayerPlanner.Move m in plan.Moves)
        {
            if (shown++ >= 80) break;
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL   {(applied ? "PLACED" : "PLAN")} \"{m.Name}\" [{m.Variant}] -> {m.ToPort}");
        }
        foreach (var kv in plan.Shortfall)
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL   shortfall(build) [{kv.Key}] = {kv.Value}");
    }

    private static void DoRebalance()
    {
        try
        {
            var moves = plan.Moves;
            if (moves.Count == 0)
            {
                Melon<MintChipPlusMod>.Logger.Msg("UADMC_MINEREBAL nothing to place (no idle subs or no deficit).");
                return;
            }
            int applied = MinelayerPlanner.Apply(moves);
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL applied {applied}/{moves.Count} placement(s).");
            LogPlan(true);
            Recompute();
            RefreshLeft();
            RefreshPortList();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC minelayer goals: rebalance failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    // Build the shortfall of a variant and assign the new subs (plus any idle ones) to deficit ports.
    private static void BuildAndAssign(string variant)
    {
        try
        {
            if (!plan.TypeByVariant.TryGetValue(variant, out SubmarineType type) || type == null)
            {
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL no SubmarineType for {variant} — build one manually first.");
                return;
            }
            Player human = plan.Human;
            PlayerController pc = PlayerController.Instance;
            if (human == null || pc == null) return;

            // deficit slots for this variant, in port order
            var slots = new List<PortElement>();
            foreach (MinelayerPlanner.PortRow row in plan.Ports)
            {
                int target = goal.EffectiveTarget(row.PortId, variant);
                row.Current.TryGetValue(variant, out int have);
                for (int i = have; i < target; i++) slots.Add(row.Element);
            }
            if (slots.Count == 0)
            {
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL {variant}: no deficit to fill.");
                return;
            }

            plan.IdleSubs.TryGetValue(variant, out var idleSubs);
            int idleN = idleSubs?.Count ?? 0;
            int need = Math.Max(0, slots.Count - idleN);

            // shipyard-capacity warning (subs count against shipyard per the user)
            plan.TonnageByVariant.TryGetValue(variant, out float ton);
            float addTon = need * ton;
            float projected = plan.CurrentBuildingTonnage + addTon;
            if (plan.ShipyardCapacity > 0f && need > 0 && projected > plan.ShipyardCapacity)
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC_MINEREBAL WARNING: building {need} {variant} (+{addTon:0}t) -> shipyard ~{projected:0}t / {plan.ShipyardCapacity:0}t, OVER by {projected - plan.ShipyardCapacity:0}t (builds will be slower).");

            var pool = new List<Submarine>();
            if (idleSubs != null) pool.AddRange(idleSubs);
            int built = 0;
            if (need > 0)
            {
                bool can = false;
                string reason = "";
                try { can = pc.CanBuildSubmarineForType(type, human, out reason); } catch { }
                if (!can)
                {
                    Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL cannot build {variant}: {reason} — assigning idle only.");
                }
                else
                {
                    try
                    {
                        var res = pc.BuildSubmarines(type, need, human);
                        if (res != null)
                            foreach (Submarine s in res)
                                if (s != null) { pool.Add(s); built++; }
                    }
                    catch (Exception bex)
                    {
                        Melon<MintChipPlusMod>.Logger.Warning($"UADMC_MINEREBAL build call failed: {bex.GetType().Name}: {bex.Message}");
                    }
                }
            }

            int assigned = 0;
            for (int i = 0; i < slots.Count && i < pool.Count; i++)
                if (VesselAssignment.AssignToPort(pool[i], slots[i])) assigned++;

            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_MINEREBAL build+assign [{variant}]: deficit={slots.Count} idle={idleN} built={built} assigned={assigned}.");
            Recompute();
            RefreshLeft();
            RefreshPortList();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC minelayer goals: build+assign failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ----- UI helpers (self-contained, mirroring ShipRecordsViewer) -----

    private static GameObject Row(Transform parent, float height)
    {
        GameObject row = new("Row");
        row.transform.SetParent(parent, false);
        HorizontalLayoutGroup hl = row.AddComponent<HorizontalLayoutGroup>();
        hl.spacing = 6f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlWidth = hl.childControlHeight = true;
        hl.childForceExpandWidth = hl.childForceExpandHeight = false; // don't stretch buttons vertically
        AddLayoutElement(row, minHeight: height, preferredHeight: height, flexibleWidth: 1f);
        return row;
    }

    private static void Stepper(Transform parent, string label, int value, System.Action onMinus, System.Action onPlus)
    {
        GameObject row = Row(parent, 26f);
        Text t = MakeText(row.transform, label, 12, TextAnchor.MiddleLeft);
        AddLayoutElement(t.gameObject, minWidth: 210f, preferredWidth: 210f, flexibleWidth: 1f);
        MakeButton(row.transform, "-", StepBg, onMinus, 30f);
        Text val = MakeText(row.transform, value.ToString(), 13, TextAnchor.MiddleCenter);
        AddLayoutElement(val.gameObject, minWidth: 28f, preferredWidth: 28f, flexibleWidth: 0f);
        MakeButton(row.transform, "+", StepBg, onPlus, 30f);
    }

    private static Text MakeText(Transform parent, string text, int fontSize, TextAnchor anchor)
    {
        GameObject go = new("Text");
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.color = Color.white;
        t.alignment = anchor;
        t.text = text;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        AddLayoutElement(go, minHeight: fontSize + 8f, preferredHeight: fontSize + 8f, flexibleWidth: 1f);
        return t;
    }

    private static Text FillText(Transform parent, string text, int fontSize, TextAnchor anchor)
    {
        GameObject go = new("Label");
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.color = Color.white;
        t.alignment = anchor;
        t.text = text;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        RectTransform r = go.GetComponent<RectTransform>();
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(8f, 1f);
        r.offsetMax = new Vector2(-8f, -1f);
        return t;
    }

    private static Button MakeButton(Transform parent, string label, Color color, System.Action onClick, float width)
    {
        GameObject go = new("Btn_" + label);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        Button b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(new System.Action(onClick));
        AddLayoutElement(go, minWidth: width, preferredWidth: width, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 0f);
        FillText(go.transform, label, 12, TextAnchor.MiddleCenter);
        return b;
    }

    private static GameObject BuildScroll(Transform parent, string name, out Transform content)
    {
        GameObject scrollGo = new(name);
        scrollGo.transform.SetParent(parent, false);
        Image scrollBg = scrollGo.AddComponent<Image>();
        scrollBg.color = ScrollBg;
        scrollBg.raycastTarget = true;
        ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 26f;

        GameObject viewport = new("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        Image vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0.01f);
        viewport.AddComponent<RectMask2D>();
        RectTransform vpRect = viewport.GetComponent<RectTransform>();
        vpRect.anchorMin = Vector2.zero;
        vpRect.anchorMax = Vector2.one;
        vpRect.pivot = new Vector2(0f, 1f);
        vpRect.offsetMin = Vector2.zero;
        vpRect.offsetMax = Vector2.zero;

        GameObject contentGo = new("Content");
        contentGo.transform.SetParent(viewport.transform, false);
        VerticalLayoutGroup vlg = contentGo.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset { left = 4, right = 4, top = 4, bottom = 4 };
        vlg.spacing = 2f;
        vlg.childControlHeight = vlg.childControlWidth = vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        ContentSizeFitter csf = contentGo.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        RectTransform cRect = contentGo.GetComponent<RectTransform>();
        cRect.anchorMin = new Vector2(0f, 1f);
        cRect.anchorMax = new Vector2(1f, 1f);
        cRect.pivot = new Vector2(0.5f, 1f);
        cRect.offsetMin = cRect.offsetMax = Vector2.zero;

        scroll.viewport = vpRect;
        scroll.content = cRect;
        content = contentGo.transform;
        return scrollGo;
    }

    private static LayoutElement AddLayoutElement(GameObject target, float minWidth = -1f, float preferredWidth = -1f,
        float minHeight = -1f, float preferredHeight = -1f, float flexibleWidth = -1f, float flexibleHeight = -1f)
    {
        LayoutElement l = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
        if (minWidth >= 0f) l.minWidth = minWidth;
        if (preferredWidth >= 0f) l.preferredWidth = preferredWidth;
        if (minHeight >= 0f) l.minHeight = minHeight;
        if (preferredHeight >= 0f) l.preferredHeight = preferredHeight;
        if (flexibleWidth >= 0f) l.flexibleWidth = flexibleWidth;
        if (flexibleHeight >= 0f) l.flexibleHeight = flexibleHeight;
        return l;
    }

    private static void ClearChildren(GameObject target)
    {
        for (int i = target.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(target.transform.GetChild(i).gameObject);
    }

    private static void AddDragHandler(GameObject handle)
    {
        EventTrigger trig = handle.GetComponent<EventTrigger>() ?? handle.AddComponent<EventTrigger>();
        EventTrigger.Entry entry = new();
        entry.eventID = EventTriggerType.Drag;
        entry.callback.AddListener(new System.Action<BaseEventData>(OnPanelDrag));
        trig.triggers.Add(entry);
    }

    private static void OnPanelDrag(BaseEventData data)
    {
        try
        {
            if (panelRect == null) return;
            PointerEventData p = data.TryCast<PointerEventData>();
            if (p == null) return;
            float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            panelRect.anchoredPosition += p.delta / scale;
        }
        catch { }
    }
}

// Trigger: campaign hotkey via a Ui.Update postfix.
[HarmonyPatch(typeof(Ui), nameof(Ui.Update))]
internal static class MinelayerGoalsPanelHotkeyPatch
{
    [HarmonyPostfix]
    private static void Postfix() => MinelayerGoalsPanel.TryHotkey();
}
