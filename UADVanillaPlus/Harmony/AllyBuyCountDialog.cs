using System;
using MelonLoader;
using UADVanillaPlus.GameData;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// "How many to buy" modal for ally ship purchases. We use our own +/- panel rather than the native
// BuildShipAmountPopupUI because that popup gates on the BUYER's dock + base Cost() (wrong: the hull
// is built in the SELLER's dock at our premium price) and shows no price. Shows per-ship price/
// premium/build-time and a live total as the count changes; on confirm calls onBuy(count).
// Mirrors MinelayerGoalsPanel's self-contained UI helpers.
internal static class AllyBuyCountDialog
{
    private static Canvas? canvas;
    private static int count = 1;
    private static int maxCount = 1;
    private static AlliedShipPurchase.Quote quote;
    private static Action<int>? onBuy;
    private static Text? totalText;
    private static Text? countText;

    internal static void Open(string cls, string nation, int max, AlliedShipPurchase.Quote q, Action<int> onBuyCb)
    {
        Close();
        count = 1;
        maxCount = Math.Max(1, max);
        quote = q;
        onBuy = onBuyCb;

        var go = new GameObject("UADVP_AllyBuyCountCanvas");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32050;
        go.AddComponent<GraphicRaycaster>();
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1.4f;

        // full-screen backdrop: blocks clicks behind, click to cancel
        var backdrop = new GameObject("Backdrop");
        backdrop.transform.SetParent(canvas.transform, false);
        var br = backdrop.AddComponent<RectTransform>();
        br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one; br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero;
        var bi = backdrop.AddComponent<Image>();
        bi.color = new Color(0f, 0f, 0f, 0.55f);
        bi.raycastTarget = true;
        backdrop.AddComponent<Button>().onClick.AddListener(new System.Action(Close));

        var panel = new GameObject("Panel");
        panel.transform.SetParent(canvas.transform, false);
        var pr = panel.AddComponent<RectTransform>();
        pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0.5f, 0.5f);
        pr.sizeDelta = new Vector2(600f, 300f);
        panel.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.09f, 0.98f);
        var vl = panel.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset { left = 18, right = 18, top = 14, bottom = 14 };
        vl.spacing = 10f;
        vl.childControlWidth = vl.childControlHeight = vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;

        MakeText(panel.transform, $"Commission from {nation}", 17, TextAnchor.MiddleLeft);
        MakeText(panel.transform, $"How many <b>{cls}</b>?   (max {maxCount})", 13, TextAnchor.MiddleLeft);

        var step = Row(panel.transform, 40f);
        Color sb = new Color(0.16f, 0.18f, 0.22f, 1f);
        MakeButton(step.transform, "-5", sb, () => SetCount(count - 5), 52f);
        MakeButton(step.transform, "-1", sb, () => SetCount(count - 1), 52f);
        countText = MakeText(step.transform, count.ToString(), 22, TextAnchor.MiddleCenter);
        AddLayoutElement(countText.gameObject, minWidth: 64f, preferredWidth: 64f, flexibleWidth: 0f);
        MakeButton(step.transform, "+1", sb, () => SetCount(count + 1), 52f);
        MakeButton(step.transform, "+5", sb, () => SetCount(count + 5), 52f);

        totalText = MakeText(panel.transform, "", 13, TextAnchor.MiddleLeft);

        var actions = Row(panel.transform, 38f);
        MakeButton(actions.transform, "Buy", new Color(0.20f, 0.42f, 0.24f, 1f), Confirm, 150f);
        MakeButton(actions.transform, "Cancel", new Color(0.34f, 0.16f, 0.16f, 1f), Close, 150f);

        RefreshTotals();
    }

    private static void SetCount(int v)
    {
        count = Math.Max(1, Math.Min(maxCount, v));
        RefreshTotals();
    }

    private static void RefreshTotals()
    {
        if (countText != null) countText.text = count.ToString();
        if (totalText != null)
            totalText.text =
                $"Each: {Money(quote.Price)}  (base {Money(quote.Cost)} + {quote.PremiumFraction * 100f:0}% premium)  ·  ~{quote.BuildMonths} mo/ea{(quote.Pressure > 1f ? " (yard overbooked)" : "")}\n" +
                $"Total for {count}: <b>{Money(quote.Price * count)}</b>\n" +
                $"Deposit now: {Money(quote.Deposit * count)}    |    Balance on delivery: {Money((quote.Price - quote.Deposit) * count)}";
    }

    private static void Confirm()
    {
        Action<int>? cb = onBuy;
        int n = count;
        Close();
        try { cb?.Invoke(n); } catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_ALLYBUY count-dialog confirm failed. {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Close()
    {
        try { if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject); } catch { }
        finally { canvas = null; totalText = null; countText = null; onBuy = null; }
    }

    private static string Money(float v)
    {
        float a = v < 0f ? -v : v;
        if (a >= 1e9f) return $"${v / 1e9f:0.0}B";
        if (a >= 1e6f) return $"${v / 1e6f:0.0}M";
        return $"${v:0}";
    }

    // ---- self-contained UI helpers (mirror MinelayerGoalsPanel) ----
    private static GameObject Row(Transform parent, float height)
    {
        var row = new GameObject("Row");
        row.transform.SetParent(parent, false);
        var hl = row.AddComponent<HorizontalLayoutGroup>();
        hl.spacing = 8f;
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = hl.childControlHeight = true;
        hl.childForceExpandWidth = hl.childForceExpandHeight = false;
        AddLayoutElement(row, minHeight: height, preferredHeight: height, flexibleWidth: 1f);
        return row;
    }

    private static Text MakeText(Transform parent, string text, int fontSize, TextAnchor anchor)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.color = Color.white;
        t.alignment = anchor;
        t.text = text;
        t.supportRichText = true;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        AddLayoutElement(go, minHeight: fontSize + 10f, preferredHeight: fontSize + 10f, flexibleWidth: 1f);
        return t;
    }

    private static Button MakeButton(Transform parent, string label, Color color, Action onClick, float width)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        var b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(new System.Action(onClick));
        AddLayoutElement(go, minWidth: width, preferredWidth: width, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 0f);
        Text t = MakeText(go.transform, label, 14, TextAnchor.MiddleCenter);
        t.raycastTarget = false;
        return b;
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
}
