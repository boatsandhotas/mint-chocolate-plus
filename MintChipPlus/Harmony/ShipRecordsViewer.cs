using System;
using System.Collections.Generic;
using System.Globalization;
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

// Self-contained "Ship Service Records" viewer.
//
// Reads the per-campaign data layer (MintChipPlus.GameData.ShipServiceRecords) and presents it as a
// draggable, always-on-top panel:
//   - LEFT: a sortable, scrollable ship list (sort by name / class / battles / tonnage sunk / kills).
//   - RIGHT: the selected ship's career totals — including TONNAGE SUNK and DAMAGED split by enemy type
//     (BB/BC/CA...) — and a battle-by-battle history. Each battle row is click-to-expand to reveal the
//     specific enemy ships it sank / wrecked / damaged that battle (name, type, tonnage, damage).
//
// Refit continuity: records are keyed by the vessel's Ship.id (stable across a refit — the vessel
// persists, only its design changes), so a refit's battles accumulate on the same record; the displayed
// name/type update to the latest class each battle.
//
// Everything here is intentionally standalone (its own tiny UI helpers, its own root Canvas) so it does
// not depend on private helpers in other MC files. All engine calls are try/catch wrapped for IL2CPP
// transient-null safety, and the feature is gated behind ModSettings.ShipServiceRecordsEnabled.
//
// Trigger: a campaign hotkey (default F10) via ShipRecordsViewerHotkeyPatch below; public entry is Toggle().
internal static class ShipRecordsViewer
{
    private const KeyCode ToggleKey = KeyCode.F10;

    private static readonly Color PanelBg = new(0.05f, 0.05f, 0.06f, 0.97f);
    private static readonly Color HeaderBg = new(0.16f, 0.30f, 0.50f, 0.96f);
    private static readonly Color PaneBg = new(0f, 0f, 0f, 0.12f);
    private static readonly Color ScrollBg = new(0f, 0f, 0f, 0.28f);
    private static readonly Color RowIdle = new(0.12f, 0.12f, 0.14f, 0.95f);
    private static readonly Color RowSelected = new(0.50f, 0.40f, 0.16f, 0.98f);
    private static readonly Color CloseBtn = new(0.34f, 0.16f, 0.16f, 0.96f);
    private static readonly Color SortIdle = new(0.14f, 0.16f, 0.20f, 0.96f);
    private static readonly Color SortActive = new(0.20f, 0.40f, 0.62f, 0.98f);
    private static readonly Color BattleRowBg = new(0f, 0f, 0f, 0.20f);

    private enum SortMode { TonnageSunk, Kills, Battles, Name, Type }

    private static Canvas? canvas;
    private static GameObject? panel;
    private static RectTransform? panelRect;
    private static GameObject? detailPane;
    private static Transform? listContentTransform;

    private static readonly Dictionary<string, Image> rowBgById = new(StringComparer.Ordinal);
    private static readonly Dictionary<SortMode, Image> sortButtons = new();
    private static Dictionary<string, ShipServiceRecords.Record> records = new(StringComparer.Ordinal);
    private static readonly List<ShipServiceRecords.Record> ordered = new();
    private static readonly HashSet<int> expandedBattles = new();
    private static string? selectedId;
    private static SortMode sortMode = SortMode.TonnageSunk;

    private static bool IsOpen => canvas != null && panel != null;

    internal static void TryHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(ToggleKey))
                return;
            if (!ModSettings.ShipServiceRecordsEnabled)
                return;
            if (CampaignController.Instance == null)
                return; // campaign-only
            if (Util.FocusIsInInputField())
                return;

            Toggle();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: hotkey failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Toggle()
    {
        try
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            Open();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: toggle failed — {ex.GetType().Name}: {ex.Message}");
            Close();
        }
    }

    private static void Open()
    {
        if (!ModSettings.ShipServiceRecordsEnabled)
        {
            Melon<MintChipPlusMod>.Logger.Msg("UADMC ship records viewer: feature is Off; not opening.");
            return;
        }

        LoadRecords();
        Close(); // rebuild fresh so the data is always current
        BuildPanel();
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC ship records viewer: opened ({records.Count} ship(s) tracked).");
    }

    private static void Close()
    {
        try
        {
            if (canvas != null)
                UnityEngine.Object.Destroy(canvas.gameObject);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: close failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            canvas = null;
            panel = null;
            panelRect = null;
            detailPane = null;
            listContentTransform = null;
            rowBgById.Clear();
            sortButtons.Clear();
        }
    }

    private static void LoadRecords()
    {
        try
        {
            records = ShipServiceRecords.Load() ?? new Dictionary<string, ShipServiceRecords.Record>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            records = new Dictionary<string, ShipServiceRecords.Record>(StringComparer.Ordinal);
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: load failed — {ex.GetType().Name}: {ex.Message}");
        }

        ordered.Clear();
        foreach (ShipServiceRecords.Record rec in records.Values)
        {
            if (rec != null)
                ordered.Add(rec);
        }

        ApplySort();

        if (selectedId == null || !records.ContainsKey(selectedId))
            selectedId = ordered.Count > 0 ? ordered[0].Id : null;
    }

    private static void ApplySort()
    {
        Comparison<ShipServiceRecords.Record> cmp = sortMode switch
        {
            SortMode.Name => (a, b) => string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            SortMode.Type => (a, b) =>
            {
                int byType = string.Compare(a.Type ?? string.Empty, b.Type ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                return byType != 0 ? byType : string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            },
            SortMode.Battles => (a, b) => Sum(b).Battles.CompareTo(Sum(a).Battles),
            SortMode.Kills => (a, b) => Sum(b).Kills.CompareTo(Sum(a).Kills),
            _ => (a, b) => Sum(b).TonnageSunk.CompareTo(Sum(a).TonnageSunk),
        };

        try { ordered.Sort(cmp); } catch { }
    }

    private static void SetSort(SortMode mode)
    {
        try
        {
            sortMode = mode;
            ApplySort();
            foreach (KeyValuePair<SortMode, Image> kv in sortButtons)
                if (kv.Value != null)
                    kv.Value.color = kv.Key == sortMode ? SortActive : SortIdle;

            if (listContentTransform != null)
            {
                ClearChildren(listContentTransform.gameObject);
                PopulateList(listContentTransform);
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: sort failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ----- panel construction -----

    private static void BuildPanel()
    {
        GameObject canvasGo = new("UADMC_ShipRecordsCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32000;
        canvasGo.AddComponent<GraphicRaycaster>();
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1.4f; // match the Minelayer/Patrol panels for readability

        panel = new GameObject("UADMC_ShipRecordsPanel");
        panel.transform.SetParent(canvas.transform, false);
        panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(940f, 600f);
        panelRect.anchoredPosition = Vector2.zero;

        Image bg = panel.AddComponent<Image>();
        bg.color = PanelBg;
        bg.raycastTarget = true;

        VerticalLayoutGroup vl = panel.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset { left = 10, right = 10, top = 8, bottom = 10 };
        vl.spacing = 8f;
        vl.childAlignment = TextAnchor.UpperLeft;
        vl.childControlWidth = true;
        vl.childControlHeight = true;
        vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;

        BuildHeader(panel.transform);

        GameObject body = new("UADMC_ShipRecordsBody");
        body.transform.SetParent(panel.transform, false);
        HorizontalLayoutGroup bl = body.AddComponent<HorizontalLayoutGroup>();
        bl.spacing = 10f;
        bl.childAlignment = TextAnchor.UpperLeft;
        bl.childControlWidth = true;
        bl.childControlHeight = true;
        bl.childForceExpandWidth = false;
        bl.childForceExpandHeight = true;
        AddLayoutElement(body, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 340f);

        // Left column: sort row + scrollable ship list.
        GameObject left = new("UADMC_ShipListColumn");
        left.transform.SetParent(body.transform, false);
        VerticalLayoutGroup lvl = left.AddComponent<VerticalLayoutGroup>();
        lvl.spacing = 6f;
        lvl.childAlignment = TextAnchor.UpperLeft;
        lvl.childControlWidth = true;
        lvl.childControlHeight = true;
        lvl.childForceExpandWidth = true;
        lvl.childForceExpandHeight = false;
        AddLayoutElement(left, minWidth: 300f, preferredWidth: 330f, flexibleWidth: 0f, minHeight: 340f, flexibleHeight: 1f);

        BuildSortRow(left.transform);

        GameObject listScroll = BuildScroll(left.transform, "UADMC_ShipList", out Transform listContent);
        AddLayoutElement(listScroll, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 300f);
        listContentTransform = listContent;
        PopulateList(listContent);

        // Right: selected ship's career totals + battle history.
        GameObject right = new("UADMC_ShipRecordsDetail");
        right.transform.SetParent(body.transform, false);
        Image ri = right.AddComponent<Image>();
        ri.color = PaneBg;
        ri.raycastTarget = true;
        VerticalLayoutGroup rvl = right.AddComponent<VerticalLayoutGroup>();
        rvl.padding = new RectOffset { left = 10, right = 10, top = 8, bottom = 10 };
        rvl.spacing = 6f;
        rvl.childAlignment = TextAnchor.UpperLeft;
        rvl.childControlWidth = true;
        rvl.childControlHeight = true;
        rvl.childForceExpandWidth = true;
        rvl.childForceExpandHeight = false;
        AddLayoutElement(right, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 340f);
        detailPane = right;

        BuildDetail(right.transform);
    }

    private static void BuildHeader(Transform parent)
    {
        GameObject header = new("UADMC_ShipRecordsHeader");
        header.transform.SetParent(parent, false);
        Image hb = header.AddComponent<Image>();
        hb.color = HeaderBg;
        hb.raycastTarget = true;
        HorizontalLayoutGroup hl = header.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset { left = 10, right = 6, top = 2, bottom = 2 };
        hl.spacing = 6f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = true;
        AddLayoutElement(header, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        Text title = MakeText(header.transform, "Ship Service Records   —   drag to move,  F10 to close", 15, TextAnchor.MiddleLeft);
        title.raycastTarget = false;
        AddLayoutElement(title.gameObject, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);

        MakeButton(header.transform, "Close", Close, 96f);

        AddDragHandler(header);
    }

    private static void BuildSortRow(Transform parent)
    {
        sortButtons.Clear();

        GameObject row = new("UADMC_SortRow");
        row.transform.SetParent(parent, false);
        HorizontalLayoutGroup hl = row.AddComponent<HorizontalLayoutGroup>();
        hl.spacing = 4f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = true;
        AddLayoutElement(row, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);

        Text lbl = MakeText(row.transform, "Sort:", 12, TextAnchor.MiddleLeft);
        AddLayoutElement(lbl.gameObject, minWidth: 34f, preferredWidth: 34f, flexibleWidth: 0f);

        AddSortButton(row.transform, "Sunk t", SortMode.TonnageSunk, 58f);
        AddSortButton(row.transform, "Kills", SortMode.Kills, 48f);
        AddSortButton(row.transform, "Btl", SortMode.Battles, 42f);
        AddSortButton(row.transform, "Name", SortMode.Name, 52f);
        AddSortButton(row.transform, "Class", SortMode.Type, 52f);
    }

    private static void AddSortButton(Transform parent, string label, SortMode mode, float width)
    {
        GameObject go = new($"UADMC_Sort_{mode}");
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = mode == sortMode ? SortActive : SortIdle;
        img.raycastTarget = true;
        Button b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(new System.Action(() => SetSort(mode)));
        AddLayoutElement(go, minWidth: width, preferredWidth: width, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 0f);
        FillText(go.transform, label, 12, TextAnchor.MiddleCenter);
        sortButtons[mode] = img;
    }

    private static void PopulateList(Transform content)
    {
        rowBgById.Clear();

        if (ordered.Count == 0)
        {
            Text none = MakeText(content, "No ship service records yet.", 13, TextAnchor.UpperLeft);
            AddLayoutElement(none.gameObject, minHeight: 40f, preferredHeight: 40f, flexibleWidth: 1f);
            return;
        }

        foreach (ShipServiceRecords.Record rec in ordered)
        {
            string id = rec.Id ?? string.Empty;
            Totals t = Sum(rec);

            GameObject row = new("UADMC_ShipRow");
            row.transform.SetParent(content, false);
            Image rb = row.AddComponent<Image>();
            rb.color = id == selectedId ? RowSelected : RowIdle;
            rb.raycastTarget = true;
            Button btn = row.AddComponent<Button>();
            btn.targetGraphic = rb;
            string captured = id;
            btn.onClick.AddListener(new System.Action(() => OnSelect(captured)));
            AddLayoutElement(row, minHeight: 48f, preferredHeight: 48f, flexibleWidth: 1f);

            string label =
                $"{Display(rec.Name)}   [{Display(rec.Type)}]\n" +
                $"{t.Battles} btl · {t.Kills} sunk · {Num(t.TonnageSunk)}t sunk";
            FillText(row.transform, label, 13, TextAnchor.MiddleLeft);

            if (!string.IsNullOrEmpty(id))
                rowBgById[id] = rb;
        }
    }

    private static void OnSelect(string id)
    {
        try
        {
            selectedId = id;
            expandedBattles.Clear();

            foreach (KeyValuePair<string, Image> kv in rowBgById)
            {
                if (kv.Value != null)
                    kv.Value.color = kv.Key == selectedId ? RowSelected : RowIdle;
            }

            RebuildDetail();
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC ship records viewer: select failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ToggleBattle(int index)
    {
        if (!expandedBattles.Add(index))
            expandedBattles.Remove(index);
        RebuildDetail();
    }

    private static void RebuildDetail()
    {
        if (detailPane == null)
            return;
        ClearChildren(detailPane);
        BuildDetail(detailPane.transform);
    }

    private static void BuildDetail(Transform parent)
    {
        if (records.Count == 0)
        {
            MakeText(parent,
                "No ship service records yet.\n\nThey are captured automatically when your ships fight campaign battles\n(the Ship Service Records option must be On).",
                14, TextAnchor.UpperLeft);
            return;
        }

        if (selectedId == null || !records.TryGetValue(selectedId, out ShipServiceRecords.Record rec) || rec == null)
        {
            MakeText(parent, "Select a ship on the left to view its service record.", 14, TextAnchor.UpperLeft);
            return;
        }

        Totals t = Sum(rec);

        string typeLabel = string.IsNullOrWhiteSpace(rec.Type) ? string.Empty : $"   [{rec.Type}]";
        Text head = MakeText(parent, $"{Display(rec.Name)}{typeLabel}", 18, TextAnchor.MiddleLeft);
        AddLayoutElement(head.gameObject, minHeight: 28f, preferredHeight: 28f, flexibleWidth: 1f);

        string status = t.Battles == 0
            ? "No battles"
            : (t.LastLost ? "LOST IN ACTION" : (t.Losses > 0 ? $"In service (recorded sunk {t.Losses}x)" : "In service"));

        string totalsText =
            $"Battles fought:    {t.Battles}\n" +
            $"Damage dealt:      {Num(t.Dealt)}\n" +
            $"Damage received:   {Num(t.Received)}\n" +
            $"Ships sunk (finishing blows):  {t.Kills}\n" +
            $"Ships wrecked (most damage):   {t.Wrecks}\n" +
            $"Status:            {status}";
        Text totals = MakeText(parent, totalsText, 13, TextAnchor.UpperLeft);
        AddLayoutElement(totals.gameObject, minHeight: 132f, preferredHeight: 132f, flexibleWidth: 1f);

        string tonText =
            $"Tonnage SUNK ({Num(t.TonnageSunk)}t):   {FormatByType(t.SunkByType)}\n" +
            $"Tonnage DAMAGED ({Num(t.TonnageDamaged)}t):  {FormatByType(t.DmgByType)}";
        Text ton = MakeText(parent, tonText, 13, TextAnchor.UpperLeft);
        ton.color = new Color(0.85f, 0.92f, 1f, 1f);
        AddLayoutElement(ton.gameObject, minHeight: 52f, preferredHeight: 52f, flexibleWidth: 1f);

        Text histLabel = MakeText(parent, $"Battle history ({rec.Battles.Count}, newest first — click a battle to expand):", 13, TextAnchor.MiddleLeft);
        AddLayoutElement(histLabel.gameObject, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);

        GameObject battleScroll = BuildScroll(parent, "UADMC_BattleList", out Transform battleContent);
        AddLayoutElement(battleScroll, flexibleWidth: 1f, flexibleHeight: 1f, minHeight: 170f);

        if (rec.Battles.Count == 0)
        {
            Text empty = MakeText(battleContent, "No battles recorded for this ship.", 12, TextAnchor.UpperLeft);
            AddLayoutElement(empty.gameObject, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);
            return;
        }

        for (int i = rec.Battles.Count - 1; i >= 0; i--)
        {
            ShipServiceRecords.Entry e = rec.Battles[i];
            if (e == null)
                continue;

            int victimCount = e.Victims != null ? e.Victims.Count : 0;
            bool expanded = expandedBattles.Contains(i);
            string marker = victimCount > 0 ? (expanded ? "[-] " : "[+] ") : "    ";
            string date = string.IsNullOrWhiteSpace(e.Date) ? "—" : e.Date;
            string outcome = e.Sunk ? "LOST" : "survived";
            string line =
                $"{marker}{date}   dealt {Num(e.Dealt)}  recv {Num(e.Received)}  sunk {e.Kills}  wreck {e.Wrecks}  {outcome}";

            // Clickable battle row that toggles its victim breakdown.
            GameObject rowGo = new("UADMC_BattleRow");
            rowGo.transform.SetParent(battleContent, false);
            Image rimg = rowGo.AddComponent<Image>();
            rimg.color = BattleRowBg;
            rimg.raycastTarget = true;
            Button rbtn = rowGo.AddComponent<Button>();
            rbtn.targetGraphic = rimg;
            int captured = i;
            rbtn.onClick.AddListener(new System.Action(() => ToggleBattle(captured)));
            AddLayoutElement(rowGo, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);
            Text rowText = FillText(rowGo.transform, line, 12, TextAnchor.MiddleLeft);
            rowText.color = e.Sunk ? new Color(1f, 0.72f, 0.72f, 1f) : Color.white;

            if (!expanded || e.Victims == null)
                continue;

            for (int v = 0; v < e.Victims.Count; v++)
            {
                ShipServiceRecords.VictimHit vh = e.Victims[v];
                if (vh == null)
                    continue;
                string verb = vh.Sank ? "sank" : (vh.Wrecked ? "wrecked" : "hit");
                string ty = string.IsNullOrWhiteSpace(vh.Type) ? "?" : vh.Type;
                string tonStr = vh.Tonnage > 0f ? $", {Num(vh.Tonnage)}t" : string.Empty;
                Text vr = MakeText(battleContent, $"        - {verb} {Display(vh.Name)} [{ty}{tonStr}]  ({Num(vh.Damage)} dmg)", 11, TextAnchor.MiddleLeft);
                vr.color = vh.Sank ? new Color(0.7f, 1f, 0.7f, 1f)
                         : vh.Wrecked ? new Color(1f, 0.9f, 0.55f, 1f)
                         : new Color(0.78f, 0.80f, 0.85f, 1f);
                AddLayoutElement(vr.gameObject, minHeight: 17f, preferredHeight: 17f, flexibleWidth: 1f);
            }
        }
    }

    // ----- career totals -----

    private struct Totals
    {
        public int Battles;
        public double Dealt;
        public double Received;
        public int Kills;
        public int Wrecks;
        public int Losses;
        public bool LastLost;
        public double TonnageSunk;
        public double TonnageDamaged;
        public Dictionary<string, TypeAgg> SunkByType;
        public Dictionary<string, TypeAgg> DmgByType;
    }

    private struct TypeAgg
    {
        public int Count;
        public double Tonnage;
    }

    private static Totals Sum(ShipServiceRecords.Record rec)
    {
        Totals t = default;
        t.SunkByType = new Dictionary<string, TypeAgg>(StringComparer.OrdinalIgnoreCase);
        t.DmgByType = new Dictionary<string, TypeAgg>(StringComparer.OrdinalIgnoreCase);
        if (rec?.Battles == null)
            return t;

        t.Battles = rec.Battles.Count;
        for (int i = 0; i < rec.Battles.Count; i++)
        {
            ShipServiceRecords.Entry e = rec.Battles[i];
            if (e == null)
                continue;

            t.Dealt += e.Dealt;
            t.Received += e.Received;
            t.Kills += e.Kills;
            t.Wrecks += e.Wrecks;
            if (e.Sunk)
                t.Losses++;
            t.LastLost = e.Sunk; // last assignment = most recent battle

            if (e.Victims == null)
                continue;
            for (int v = 0; v < e.Victims.Count; v++)
            {
                ShipServiceRecords.VictimHit vh = e.Victims[v];
                if (vh == null)
                    continue;
                string ty = string.IsNullOrWhiteSpace(vh.Type) ? "?" : vh.Type;
                if (vh.Sank)
                {
                    t.TonnageSunk += vh.Tonnage;
                    AddType(t.SunkByType, ty, vh.Tonnage);
                }
                else
                {
                    t.TonnageDamaged += vh.Tonnage;
                    AddType(t.DmgByType, ty, vh.Tonnage);
                }
            }
        }

        return t;
    }

    private static void AddType(Dictionary<string, TypeAgg> d, string ty, double ton)
    {
        d.TryGetValue(ty, out TypeAgg cur);
        cur.Count += 1;
        cur.Tonnage += ton;
        d[ty] = cur;
    }

    private static string FormatByType(Dictionary<string, TypeAgg> d)
    {
        if (d == null || d.Count == 0)
            return "—";
        var parts = new List<string>();
        foreach (KeyValuePair<string, TypeAgg> kv in d)
            parts.Add($"{kv.Key} x{kv.Value.Count} ({Num(kv.Value.Tonnage)}t)");
        parts.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("   ", parts);
    }

    // ----- self-contained UI helpers (no dependencies on other MC files) -----

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
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlHeight = true;
        vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;
        ContentSizeFitter csf = contentGo.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        RectTransform cRect = contentGo.GetComponent<RectTransform>();
        cRect.anchorMin = new Vector2(0f, 1f);
        cRect.anchorMax = new Vector2(1f, 1f);
        cRect.pivot = new Vector2(0.5f, 1f);
        cRect.offsetMin = Vector2.zero;
        cRect.offsetMax = Vector2.zero;

        scroll.viewport = vpRect;
        scroll.content = cRect;
        content = contentGo.transform;
        return scrollGo;
    }

    private static Text MakeText(Transform parent, string text, int fontSize, TextAnchor anchor)
    {
        GameObject go = new("UADMC_Text");
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
        AddLayoutElement(go, minHeight: fontSize + 6f, preferredHeight: fontSize + 6f, flexibleWidth: 1f);
        return t;
    }

    private static Text FillText(Transform parent, string text, int fontSize, TextAnchor anchor)
    {
        GameObject go = new("UADMC_Label");
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

    private static Button MakeButton(Transform parent, string label, System.Action onClick, float width)
    {
        GameObject go = new($"UADMC_Btn_{label}");
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = CloseBtn;
        img.raycastTarget = true;
        Button b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(new System.Action(onClick));
        AddLayoutElement(go, minWidth: width, preferredWidth: width, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 0f);
        FillText(go.transform, label, 13, TextAnchor.MiddleCenter);
        return b;
    }

    private static LayoutElement AddLayoutElement(
        GameObject target,
        float minWidth = -1f,
        float preferredWidth = -1f,
        float minHeight = -1f,
        float preferredHeight = -1f,
        float flexibleWidth = -1f,
        float flexibleHeight = -1f)
    {
        LayoutElement layout = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
        if (minWidth >= 0f) layout.minWidth = minWidth;
        if (preferredWidth >= 0f) layout.preferredWidth = preferredWidth;
        if (minHeight >= 0f) layout.minHeight = minHeight;
        if (preferredHeight >= 0f) layout.preferredHeight = preferredHeight;
        if (flexibleWidth >= 0f) layout.flexibleWidth = flexibleWidth;
        if (flexibleHeight >= 0f) layout.flexibleHeight = flexibleHeight;
        return layout;
    }

    private static void ClearChildren(GameObject target)
    {
        for (int i = target.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(target.transform.GetChild(i).gameObject);
    }

    // ----- drag plumbing (header handle) -----

    private static void AddDragHandler(GameObject handle)
    {
        AddTrigger(handle, EventTriggerType.Drag, OnPanelDrag);
    }

    private static void AddTrigger(GameObject go, EventTriggerType type, System.Action<BaseEventData> cb)
    {
        EventTrigger trig = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
        EventTrigger.Entry entry = new();
        entry.eventID = type;
        entry.callback.AddListener(new System.Action<BaseEventData>(cb));
        trig.triggers.Add(entry);
    }

    private static void OnPanelDrag(BaseEventData data)
    {
        try
        {
            if (panelRect == null)
                return;
            PointerEventData p = data.TryCast<PointerEventData>();
            if (p == null)
                return;
            float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            panelRect.anchoredPosition += p.delta / scale;
        }
        catch
        {
        }
    }

    // ----- formatting -----

    private static string Display(string? s)
        => string.IsNullOrWhiteSpace(s) ? "(unnamed)" : s;

    private static string Num(double v)
        => v.ToString("N0", CultureInfo.InvariantCulture);
}

// Trigger: campaign hotkey. A second postfix on Ui.Update alongside other MC patches is fine —
// HarmonyX supports multiple patches on the same method.
[HarmonyPatch(typeof(Ui), nameof(Ui.Update))]
internal static class ShipRecordsViewerHotkeyPatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => ShipRecordsViewer.TryHotkey();
}
