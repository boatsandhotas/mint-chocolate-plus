using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Disc World is a visual/raycast wrap illusion: battle state remains on the
// canonical map, but the player may be looking at an equivalent side-lap
// position. Vanilla battle popups anchor to the canonical marker rect, so this
// swaps in a hidden side-lap anchor when that is the visible copy.
[HarmonyPatch]
internal static class CampaignMapWrapBattlePopupPatch
{
    private const float PositionEpsilon = 0.01f;
    private const float ScreenMargin = 8f;
    private const string ProxyAnchorName = "UADMC_WrapBattlePopupAnchor";

    private static readonly FieldInfo? BattleEventPositionField = AccessTools.Field(typeof(BattlePopup), "battleEventPosition");
    private static readonly Vector3[] RectCorners = new Vector3[4];
    private static readonly HashSet<string> LoggedBattleIds = new();

    private static GameObject? proxyAnchor;
    private static RectTransform? proxyAnchorRect;
    private static string proxyBattleKey = string.Empty;
    private static bool warnedMissingField;
    private static bool warnedSetFieldFailed;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BattlePopup), nameof(BattlePopup.Init), typeof(CampaignBattle))]
    private static void BattlePopupInitPostfix(BattlePopup __instance, CampaignBattle battle)
    {
        TryPrepareVisibleAnchor(__instance, battle, "init");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BattlePopup), nameof(BattlePopup.Update))]
    private static void BattlePopupUpdatePrefix(BattlePopup __instance)
    {
        TryPrepareVisibleAnchor(__instance, __instance?.CurrentBattle, "update");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BattlePopup), nameof(BattlePopup.Update))]
    private static void BattlePopupUpdatePostfix(BattlePopup __instance)
    {
        TryFallbackVisiblePopup(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BattlePopup), nameof(BattlePopup.OnBeforeHide))]
    private static void BattlePopupHidePostfix()
    {
        ClearProxyAnchor();
    }

    private static void TryPrepareVisibleAnchor(BattlePopup? popup, CampaignBattle? battle, string context)
    {
        if (!ModSettings.CampaignMapWraparoundEnabled || popup == null || battle == null)
        {
            ClearProxyAnchor();
            return;
        }

        try
        {
            if (!TryChooseWrappedBattlePosition(battle.BattleWorldPos, out Vector3 chosenWorld, out float mapWidth) ||
                Mathf.Abs(chosenWorld.x - battle.BattleWorldPos.x) <= PositionEpsilon)
            {
                ClearProxyAnchor();
                return;
            }

            RectTransform? sourceRect = battle.BattleUI?.CachedRectTransform;
            if (sourceRect == null)
            {
                LogOnce(
                    battle,
                    $"UADMC map wrap battle popup: battle {BattleLabel(battle)} needed side-lap popup anchor but had no battle marker rect; fallback may center the popup.");
                return;
            }

            RectTransform? proxyRect = EnsureProxyAnchor(sourceRect, battle);
            if (proxyRect == null)
                return;

            Vector3 uiPosition = WrappedBattleUiPosition(chosenWorld);
            proxyRect.position = uiPosition;
            popup.BattleWorldPosition = chosenWorld;

            if (!SetBattleEventPosition(popup, proxyRect))
                return;

            LogOnce(
                battle,
                $"UADMC map wrap battle popup: anchored {BattleLabel(battle)} to visible Disc World lap during {context}; " +
                $"originalX={battle.BattleWorldPos.x:0.###}, chosenX={chosenWorld.x:0.###}, " +
                $"mapWidth={mapWidth:0.###}, marker={FormatVector(sourceRect.anchoredPosition)}, " +
                $"proxy={FormatVector(proxyRect.anchoredPosition)}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC map wrap battle popup anchor failed; vanilla popup placement remains active. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryFallbackVisiblePopup(BattlePopup? popup)
    {
        if (!ModSettings.CampaignMapWraparoundEnabled || popup?.Rect == null || popup.CurrentBattle == null)
            return;

        try
        {
            if (PopupRectOverlapsScreen(popup.Rect))
                return;

            popup.Rect.anchoredPosition3D = Vector3.zero;
            LogOnce(
                popup.CurrentBattle,
                $"UADMC map wrap battle popup: centered offscreen popup for {BattleLabel(popup.CurrentBattle)} as a Disc World softlock fallback; " +
                $"world={FormatVector(popup.BattleWorldPosition)}, final={FormatVector(popup.Rect.anchoredPosition)}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC map wrap battle popup fallback failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static RectTransform? EnsureProxyAnchor(RectTransform sourceRect, CampaignBattle battle)
    {
        string battleKey = BattleKey(battle);
        if (proxyAnchorRect != null && proxyAnchor != null && string.Equals(proxyBattleKey, battleKey, StringComparison.Ordinal))
        {
            MatchProxyRect(sourceRect, proxyAnchorRect);
            return proxyAnchorRect;
        }

        ClearProxyAnchor();
        Transform? parent = sourceRect.parent;
        if (parent == null)
            return null;

        proxyAnchor = new GameObject(ProxyAnchorName);
        proxyAnchor.transform.SetParent(parent, false);
        RectTransform rect = proxyAnchor.AddComponent<RectTransform>();
        proxyAnchorRect = rect;
        proxyBattleKey = battleKey;
        MatchProxyRect(sourceRect, rect);
        proxyAnchor.SetActive(false);
        return rect;
    }

    private static void MatchProxyRect(RectTransform sourceRect, RectTransform proxyRect)
    {
        proxyRect.anchorMin = sourceRect.anchorMin;
        proxyRect.anchorMax = sourceRect.anchorMax;
        proxyRect.pivot = sourceRect.pivot;
        proxyRect.sizeDelta = sourceRect.sizeDelta;
        proxyRect.offsetMin = sourceRect.offsetMin;
        proxyRect.offsetMax = sourceRect.offsetMax;
        proxyRect.localRotation = sourceRect.localRotation;
        proxyRect.localScale = sourceRect.localScale;
    }

    private static bool SetBattleEventPosition(BattlePopup popup, RectTransform rect)
    {
        if (BattleEventPositionField == null)
        {
            if (!warnedMissingField)
            {
                warnedMissingField = true;
                Melon<MintChipPlusMod>.Logger.Warning("UADMC map wrap battle popup: private battleEventPosition field was not found.");
            }

            return false;
        }

        try
        {
            BattleEventPositionField.SetValue(popup, rect);
            return true;
        }
        catch (Exception ex)
        {
            if (!warnedSetFieldFailed)
            {
                warnedSetFieldFailed = true;
                Melon<MintChipPlusMod>.Logger.Warning(
                    $"UADMC map wrap battle popup: could not replace battleEventPosition; fallback will center if needed. {ex.GetType().Name}: {ex.Message}");
            }

            return false;
        }
    }

    private static bool TryChooseWrappedBattlePosition(Vector3 original, out Vector3 chosen, out float mapWidth)
    {
        chosen = original;
        mapWidth = CampaignMapWrapVisualPatch.CurrentMapWidth();
        if (mapWidth <= 0f)
            return false;

        float cameraX = CurrentCameraCenterX(original.x);
        Vector3 negative = original;
        negative.x -= mapWidth;
        Vector3 positive = original;
        positive.x += mapWidth;

        chosen = ClosestByX(cameraX, original, negative, positive);
        return true;
    }

    private static Vector3 ClosestByX(float x, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 best = a;
        float bestDistance = Mathf.Abs(a.x - x);

        float bDistance = Mathf.Abs(b.x - x);
        if (bDistance < bestDistance)
        {
            best = b;
            bestDistance = bDistance;
        }

        float cDistance = Mathf.Abs(c.x - x);
        if (cDistance < bestDistance)
            best = c;

        return best;
    }

    private static float CurrentCameraCenterX(float fallback)
    {
        try
        {
            Cam? cam = Cam.Instance;
            if (cam == null)
                return fallback;

            Vector3 lookingAt = cam.lookingAtPosition;
            if (IsFinite(lookingAt.x))
                return lookingAt.x;

            Camera? camera = cam.cameraComp;
            if (camera != null)
                return camera.transform.position.x;
        }
        catch
        {
            // Fall through to the canonical battle x.
        }

        return fallback;
    }

    private static Vector3 WrappedBattleUiPosition(Vector3 worldPosition)
    {
        CampaignMap? map = CampaignMap.Instance;
        MapUI? mapUi = map?.UIMap;
        if (mapUi?.UICanvas == null)
            return worldPosition;

        return mapUi.WorldToUISpace(mapUi.UICanvas, worldPosition);
    }

    private static bool PopupRectOverlapsScreen(RectTransform rect)
    {
        if (!rect.gameObject.activeInHierarchy)
            return true;

        rect.GetWorldCorners(RectCorners);
        Camera? camera = rect.GetComponentInParent<Canvas>()?.worldCamera;

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < RectCorners.Length; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, RectCorners[i]);
            minX = Mathf.Min(minX, screen.x);
            maxX = Mathf.Max(maxX, screen.x);
            minY = Mathf.Min(minY, screen.y);
            maxY = Mathf.Max(maxY, screen.y);
        }

        return maxX >= ScreenMargin &&
               minX <= Screen.width - ScreenMargin &&
               maxY >= ScreenMargin &&
               minY <= Screen.height - ScreenMargin;
    }

    private static void ClearProxyAnchor()
    {
        if (proxyAnchor != null)
            UnityEngine.Object.Destroy(proxyAnchor);

        proxyAnchor = null;
        proxyAnchorRect = null;
        proxyBattleKey = string.Empty;
    }

    private static void LogOnce(CampaignBattle battle, string message)
    {
        if (!LoggedBattleIds.Add(BattleKey(battle)))
            return;

        Melon<MintChipPlusMod>.Logger.Msg(message);
    }

    private static string BattleKey(CampaignBattle battle)
        => battle?.Id.ToString() ?? string.Empty;

    private static string BattleLabel(CampaignBattle battle)
    {
        string type = "unknown";
        try { type = battle.Type?.name ?? type; }
        catch { }

        return $"{battle.Id}/{type}";
    }

    private static string FormatVector(Vector2 vector)
        => $"({vector.x:0.###},{vector.y:0.###})";

    private static string FormatVector(Vector3 vector)
        => $"({vector.x:0.###},{vector.y:0.###},{vector.z:0.###})";

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);
}
