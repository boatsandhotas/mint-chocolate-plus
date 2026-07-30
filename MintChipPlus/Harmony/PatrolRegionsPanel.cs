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

// "Patrol / Foreign Stations" panel — see where you're weakest by region, then send ships there.
//   LEFT  : regions ranked by tonnage deficit (required - current), click to pick a destination region.
//   RIGHT : your in-port ships (click to multi-select).
//   FOOTER: "Send selected -> <region>" (manual) and "Auto-distribute selected" (spread the selection
//           across the neediest regions). Both dispatch via the shared VesselAssignment core
//           (built -> MoveVessels, building -> field write), which logs every move (UADMC_VASSIGN).
//
// Trigger: campaign hotkey F7 (toggle). Self-contained UI, gated by CampaignController being present.
internal static class PatrolRegionsPanel
{
    private const KeyCode ToggleKey = KeyCode.F7;

    private static readonly Color PanelBg = new(0.05f, 0.05f, 0.06f, 0.97f);
    private static readonly Color HeaderBg = new(0.18f, 0.34f, 0.30f, 0.96f);
    private static readonly Color PaneBg = new(0f, 0f, 0f, 0.12f);
    private static readonly Color ScrollBg = new(0f, 0f, 0f, 0.28f);
    private static readonly Color RowIdle = new(0.12f, 0.12f, 0.14f, 0.95f);
    private static readonly Color RowSelected = new(0.50f, 0.40f, 0.16f, 0.98f);
    private static readonly Color RowDeficit = new(0.30f, 0.16f, 0.12f, 0.96f);
    private static readonly Color ClassHdrBg = new(0.16f, 0.26f, 0.24f, 0.97f);
    private static readonly Color BtnBg = new(0.20f, 0.42f, 0.24f, 0.98f);
    private static readonly Color CloseBtn = new(0.34f, 0.16f, 0.16f, 0.96f);

    private static Canvas? canvas;
    private static GameObject? panel;
    private static RectTransform? panelRect;
    private static Transform? regionContent;
    private static Transform? shipContent;
    private static Transform? footer;

    private static PatrolPlanner.PatrolPlan plan = new();
    private static string? selectedRegionKey;                       // target port id identifying the chosen region
    private static string? selectedPortId;                          // chosen destination port within that region (null = largest)
    private static readonly HashSet<IntPtr> selectedShips = new();

    private static bool IsOpen => canvas != null && panel != null;

    internal static void TryHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(ToggleKey)) return;
            if (CampaignController.Instance == null) return;
            if (Util.FocusIsInInputField()) return;
            Toggle();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC patrol panel: hotkey failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Toggle()
    {
        if (IsOpen) { Close(); return; }
        Open();
    }

    private static void Open()
    {
        Recompute();
        Close();
        BuildPanel();
    }

    private static void Close()
    {
        try { if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject); }
        catch { }
        finally { canvas = null; panel = null; panelRect = null; regionContent = null; shipContent = null; footer = null; }
    }

    private static void Recompute()
    {
        plan = PatrolPlanner.Compute();
        // prune selections that no longer exist
        if (selectedRegionKey != null && !RegionByKey(selectedRegionKey, out _))
            selectedRegionKey = null;
        var live = new HashSet<IntPtr>();
        foreach (var sr in plan.Pool) live.Add(Ptr(sr.Ship));
        selectedShips.RemoveWhere(p => !live.Contains(p));
    }

    private static IntPtr Ptr(VesselEntity v)
    {
        try { return v.Pointer; } catch { return IntPtr.Zero; }
    }

    private static bool RegionByKey(string key, out PatrolPlanner.RegionRow row)
    {
        foreach (var r in plan.Regions)
            if (r.TargetPort != null && SafeStr(() => r.TargetPort.Id) == key) { row = r; return true; }
        row = null;
        return false;
    }

    private static string SafeStr(Func<string?> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }

    // ----- panel frame -----

    private static void BuildPanel()
    {
        GameObject canvasGo = new("UADMC_PatrolCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32000;
        canvasGo.AddComponent<GraphicRaycaster>();
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1.4f;

        panel = new GameObject("UADMC_PatrolPanel");
        panel.transform.SetParent(canvas.transform, false);
        panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(940f, 600f);
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
        bl.childForceExpandWidth = true;
        AddLayoutElement(body, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 340f);

        // left: regions
        GameObject left = new("Regions");
        left.transform.SetParent(body.transform, false);
        VerticalLayoutGroup lvl = left.AddComponent<VerticalLayoutGroup>();
        lvl.spacing = 4f;
        lvl.childControlWidth = lvl.childControlHeight = lvl.childForceExpandWidth = lvl.childForceExpandHeight = true;
        AddLayoutElement(left, flexibleWidth: 1f, flexibleHeight: 1f);
        MakeText(left.transform, "Regions — weakest first (click to target):", 12, TextAnchor.MiddleLeft);
        GameObject rscroll = BuildScroll(left.transform, "RegionList", out Transform rc);
        AddLayoutElement(rscroll, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 300f);
        regionContent = rc;

        // right: ships
        GameObject right = new("Ships");
        right.transform.SetParent(body.transform, false);
        VerticalLayoutGroup rvl = right.AddComponent<VerticalLayoutGroup>();
        rvl.spacing = 4f;
        rvl.childControlWidth = rvl.childControlHeight = rvl.childForceExpandWidth = rvl.childForceExpandHeight = true;
        AddLayoutElement(right, flexibleWidth: 1f, flexibleHeight: 1f);
        MakeText(right.transform, "Your in-port ships (click = select one, shift+click = add):", 12, TextAnchor.MiddleLeft);
        GameObject sscroll = BuildScroll(right.transform, "ShipList", out Transform sc);
        AddLayoutElement(sscroll, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 300f);
        shipContent = sc;

        // footer: actions + status
        GameObject foot = new("Footer");
        foot.transform.SetParent(panel.transform, false);
        Image fi = foot.AddComponent<Image>();
        fi.color = PaneBg;
        VerticalLayoutGroup fvl = foot.AddComponent<VerticalLayoutGroup>();
        fvl.padding = new RectOffset { left = 8, right = 8, top = 4, bottom = 6 };
        fvl.spacing = 4f;
        fvl.childControlWidth = fvl.childControlHeight = fvl.childForceExpandWidth = true;
        fvl.childForceExpandHeight = false;
        AddLayoutElement(foot, minHeight: 64f, flexibleWidth: 1f);
        footer = foot.transform;

        RefreshAll();
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
        Text title = MakeText(header.transform, "Patrol / Foreign Stations   —   drag to move,  F7 to close", 15, TextAnchor.MiddleLeft);
        title.raycastTarget = false;
        AddLayoutElement(title.gameObject, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);
        MakeButton(header.transform, "Close", CloseBtn, Close, 96f);
        AddDragHandler(header);
    }

    private static void RefreshAll()
    {
        RefreshRegions();
        RefreshShips();
        RefreshFooter();
    }

    private static void RefreshRegions()
    {
        if (regionContent == null) return;
        ClearChildren(regionContent.gameObject);
        if (plan.Regions.Count == 0)
        {
            MakeText(regionContent, "(no regions with controlled ports found)", 12, TextAnchor.UpperLeft);
            return;
        }
        foreach (PatrolPlanner.RegionRow r in plan.Regions)
        {
            string key = SafeStr(() => r.TargetPort.Id);
            GameObject go = new("RegionRow");
            go.transform.SetParent(regionContent, false);
            Image img = go.AddComponent<Image>();
            img.color = key == selectedRegionKey ? RowSelected : (r.Deficit > 0f ? RowDeficit : RowIdle);
            img.raycastTarget = true;
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            string captured = key;
            b.onClick.AddListener(new System.Action(() => { selectedRegionKey = captured; selectedPortId = null; RefreshAll(); }));
            AddLayoutElement(go, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);
            FillText(go.transform,
                $"{r.Label}   def {r.Deficit:0}   (cur {r.Current:0} / req {r.Required:0})   {r.Ports} ports",
                12, TextAnchor.MiddleLeft);

            // when this region is selected, list its ports as selectable destinations (default = largest)
            if (key == selectedRegionKey && r.AllPorts.Count > 0)
            {
                string effPort = selectedPortId ?? SafeStr(() => r.TargetPort.Id);
                for (int i = 0; i < r.AllPorts.Count; i++)
                {
                    string pid = SafeStr(() => r.AllPorts[i].Id);
                    bool psel = pid == effPort;
                    GameObject pgo = new("PortPick");
                    pgo.transform.SetParent(regionContent, false);
                    Image pimg = pgo.AddComponent<Image>();
                    pimg.color = psel ? RowSelected : ClassHdrBg;
                    pimg.raycastTarget = true;
                    Button pb = pgo.AddComponent<Button>();
                    pb.targetGraphic = pimg;
                    string capturedPid = pid;
                    pb.onClick.AddListener(new System.Action(() => { selectedPortId = capturedPid; RefreshAll(); }));
                    AddLayoutElement(pgo, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 1f);
                    FillText(pgo.transform, $"        {(psel ? "> " : "  ")}{r.PortNames[i]}   cap {r.PortCaps[i]:n0}   in port: {r.PortShipCounts[i]} ({r.PortTons[i]:n0}t)", 12, TextAnchor.MiddleLeft);
                }
            }
        }
    }

    // Resolve the chosen destination port for the selected region (falls back to the largest).
    private static PortElement ResolveDest(PatrolPlanner.RegionRow region)
    {
        if (selectedPortId != null)
            for (int i = 0; i < region.AllPorts.Count; i++)
                if (SafeStr(() => region.AllPorts[i].Id) == selectedPortId)
                    return region.AllPorts[i];
        return region.TargetPort;
    }

    private static void RefreshShips()
    {
        if (shipContent == null) return;
        ClearChildren(shipContent.gameObject);
        if (plan.Pool.Count == 0)
        {
            MakeText(shipContent, "(no building or in-port ships available)", 12, TextAnchor.UpperLeft);
            return;
        }

        // group ships by class (className)
        var groups = new Dictionary<string, List<PatrolPlanner.ShipRow>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (PatrolPlanner.ShipRow sr in plan.Pool)
        {
            string cls = string.IsNullOrEmpty(sr.Class) ? "?" : sr.Class;
            if (!groups.TryGetValue(cls, out var list)) { groups[cls] = list = new List<PatrolPlanner.ShipRow>(); order.Add(cls); }
            list.Add(sr);
        }
        order.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string cls in order)
        {
            var g = groups[cls];
            // within a class, cluster ships by port (then biggest first) so same-port ships sit together
            g.Sort((a, b) =>
            {
                int c = string.Compare(a.Loc, b.Loc, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : b.Tonnage.CompareTo(a.Tonnage);
            });
            int building = 0;
            foreach (var sr in g) if (sr.IsBuilding) building++;
            bool allSel = true;
            foreach (var sr in g) if (!selectedShips.Contains(Ptr(sr.Ship))) { allSel = false; break; }

            // clickable class header: select / deselect the whole class
            GameObject hdr = new("ClassHdr");
            hdr.transform.SetParent(shipContent, false);
            Image hi = hdr.AddComponent<Image>();
            hi.color = allSel ? RowSelected : ClassHdrBg;
            hi.raycastTarget = true;
            Button hb = hdr.AddComponent<Button>();
            hb.targetGraphic = hi;
            var capturedGroup = g;
            hb.onClick.AddListener(new System.Action(() =>
            {
                bool all = true;
                foreach (var sr in capturedGroup) if (!selectedShips.Contains(Ptr(sr.Ship))) { all = false; break; }
                foreach (var sr in capturedGroup) { IntPtr p = Ptr(sr.Ship); if (all) selectedShips.Remove(p); else selectedShips.Add(p); }
                RefreshShips();
                RefreshFooter();
            }));
            AddLayoutElement(hdr, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);
            string typeTag = g.Count > 0 ? g[0].Type : "";
            FillText(hdr.transform, $"{cls}  [{typeTag}]   ({g.Count} ship{(g.Count == 1 ? "" : "s")}{(building > 0 ? ", " + building + " building" : "")})   — click to select class", 12, TextAnchor.MiddleLeft);

            foreach (PatrolPlanner.ShipRow sr in g)
            {
                IntPtr ptr = Ptr(sr.Ship);
                bool sel = selectedShips.Contains(ptr);
                GameObject go = new("ShipRow");
                go.transform.SetParent(shipContent, false);
                Image img = go.AddComponent<Image>();
                img.color = sel ? RowSelected : RowIdle;
                img.raycastTarget = true;
                Button b = go.AddComponent<Button>();
                b.targetGraphic = img;
                IntPtr captured = ptr;
                // plain click = select just this ship; shift+click = add/remove (multi-select)
                b.onClick.AddListener(new System.Action(() =>
                {
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (shift) { if (!selectedShips.Add(captured)) selectedShips.Remove(captured); }
                    else { selectedShips.Clear(); selectedShips.Add(captured); }
                    RefreshShips();
                    RefreshFooter();
                }));
                AddLayoutElement(go, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);
                FillText(go.transform, $"   {sr.Name}   {sr.Tonnage:0}t   {sr.Loc}", 12, TextAnchor.MiddleLeft, rich: true);
            }
        }
    }

    private static void RefreshFooter()
    {
        if (footer == null) return;
        ClearChildren(footer.gameObject);

        string destName = "(pick a region)";
        if (selectedRegionKey != null && RegionByKey(selectedRegionKey, out var rr))
        {
            PortElement d = ResolveDest(rr);
            string pn = SafeStr(() => d.Name);
            destName = rr.Label + " / " + (string.IsNullOrWhiteSpace(pn) ? SafeStr(() => d.Id) : pn);
        }
        GameObject actions = Row(footer, 30f);
        MakeButton(actions.transform, $"Send {selectedShips.Count} -> {destName}", BtnBg, SendSelected, 340f);
        MakeButton(actions.transform, $"Auto-distribute {selectedShips.Count}", BtnBg, AutoDistribute, 220f);

        MakeText(footer, $"selected ships: {selectedShips.Count}   regions: {plan.Regions.Count}   pool: {plan.Pool.Count}", 12, TextAnchor.MiddleLeft);
    }

    private static List<VesselEntity> SelectedVessels()
    {
        var list = new List<VesselEntity>();
        foreach (PatrolPlanner.ShipRow sr in plan.Pool)
            if (selectedShips.Contains(Ptr(sr.Ship)))
                list.Add(sr.Ship);
        return list;
    }

    private static void SendSelected()
    {
        try
        {
            if (selectedRegionKey == null || !RegionByKey(selectedRegionKey, out var region))
            {
                Melon<MintChipPlusMod>.Logger.Msg("UADMC_VASSIGN patrol: no region selected.");
                return;
            }
            var vessels = SelectedVessels();
            if (vessels.Count == 0)
            {
                Melon<MintChipPlusMod>.Logger.Msg("UADMC_VASSIGN patrol: no ships selected.");
                return;
            }
            PortElement dest = ResolveDest(region);
            int done = VesselAssignment.AssignManyToPort(vessels, dest);
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_VASSIGN patrol send: {done}/{vessels.Count} ship(s) -> {region.Label} / {SafeStr(() => dest.Id)}.");
            selectedShips.Clear();
            Recompute();
            RefreshAll();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC patrol send failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AutoDistribute()
    {
        try
        {
            var vessels = SelectedVessels();
            if (vessels.Count == 0)
            {
                Melon<MintChipPlusMod>.Logger.Msg("UADMC_VASSIGN patrol auto: no ships selected.");
                return;
            }
            if (plan.Regions.Count == 0)
            {
                Melon<MintChipPlusMod>.Logger.Msg("UADMC_VASSIGN patrol auto: no regions.");
                return;
            }

            // working deficit per region; each ship goes to the currently-neediest region, then that
            // region's running tonnage rises so the selection spreads out.
            var work = new List<float>();
            foreach (var r in plan.Regions) work.Add(r.Current);

            int done = 0;
            foreach (VesselEntity v in vessels)
            {
                int bestIdx = -1;
                float bestNeed = float.NegativeInfinity;
                for (int i = 0; i < plan.Regions.Count; i++)
                {
                    float need = plan.Regions[i].Required - work[i];
                    if (need > bestNeed) { bestNeed = need; bestIdx = i; }
                }
                if (bestIdx < 0) break;
                PatrolPlanner.RegionRow region = plan.Regions[bestIdx];
                bool ok = VesselAssignment.AssignToPort(v, region.TargetPort);
                if (ok) done++;
                float ton = 0f; try { ton = v.Tonnage(); } catch { }
                work[bestIdx] += ton;
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_VASSIGN patrol auto: ship -> {region.Label} (need was {bestNeed:0}) ok={ok}");
            }

            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_VASSIGN patrol auto-distribute: {done}/{vessels.Count} dispatched.");
            selectedShips.Clear();
            Recompute();
            RefreshAll();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC patrol auto-distribute failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ----- UI helpers (self-contained, mirroring MinelayerGoalsPanel) -----

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

    private static Text FillText(Transform parent, string text, int fontSize, TextAnchor anchor, bool rich = false)
    {
        GameObject go = new("Label");
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.color = Color.white;
        t.alignment = anchor;
        t.text = text;
        t.supportRichText = rich;
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
        AddLayoutElement(go, minWidth: width, preferredWidth: width, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 0f);
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
internal static class PatrolRegionsPanelHotkeyPatch
{
    [HarmonyPostfix]
    private static void Postfix() => PatrolRegionsPanel.TryHotkey();
}
