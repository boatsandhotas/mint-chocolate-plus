using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.Services;
using UADVanillaPlus.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Player-only test surface for VP's conservative Smart Refit service. This is
// deliberately button-driven and refit-constructor scoped; AI campaign refits
// remain blocked by CampaignSmartRefitPatch while the replacement matures.
[HarmonyPatch]
internal static class DesignSmartRefitPatch
{
    private const string LogPrefix = "UADVP smart refit";
    private const string ButtonName = "UADVP_SmartRefit";
    private const string AutoLiteButtonName = "UADVP_AutoDesignLite";
    private const string GenerateArmorButtonName = "UADVP_GenerateArmor";
    private const string VanillaRandomShipButtonName = "RandomShip";
    private const string VanillaRandomShipSimpleRefitButtonName = "RandomShipSimpleRefit";
    private const float SmartRefitButtonWidth = 58f;

    private static readonly MethodInfo? RefreshHullStatsMethod =
        AccessTools.Method(typeof(Ship), "RefreshHullStats", Type.EmptyTypes);
    private static readonly MethodInfo? OnConShipChangedMethod =
        AccessTools.Method(typeof(Ui), "OnConShipChanged", new[] { typeof(bool) });

    private static bool loggedAttached;
    private static bool loggedMissingButton;
    private static bool loggedRefreshWarning;
    private static bool runningSmartRefit;
    private static readonly HashSet<IntPtr> HiddenVanillaRefitAutoButtons = new();

    internal static MethodBase? ConstructorUiTarget()
        => AccessTools.Method(typeof(Ui), nameof(Ui.ConstructorUI), Type.EmptyTypes);

    internal static MethodBase? RefreshConstructorInfoTarget()
        => AccessTools.Method(typeof(Ui), nameof(Ui.RefreshConstructorInfo), Type.EmptyTypes);

    internal static void EnsureButton(Ui? ui)
    {
        if (ui == null || !IsConstructor())
            return;

        try
        {
            GameObject? source =
                FindDeepChild(ui.transform, VanillaRandomShipSimpleRefitButtonName) ??
                FindDeepChild(ui.transform, VanillaRandomShipButtonName) ??
                FindDeepChild(ui.transform, AutoLiteButtonName) ??
                FindDeepChild(ui.transform, GenerateArmorButtonName);
            if (source == null)
            {
                SyncVanillaRefitAutoButtons(ui);
                if (!loggedMissingButton)
                {
                    loggedMissingButton = true;
                    Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: constructor button template not found; Smart Refit button disabled.");
                }

                return;
            }

            Transform? targetParent = source.transform.parent;
            if (targetParent == null)
                return;

            GameObject? buttonObject = FindDeepChild(ui.transform, ButtonName);
            bool created = false;
            if (buttonObject == null)
            {
                buttonObject = UnityEngine.Object.Instantiate(source, targetParent);
                buttonObject.name = ButtonName;
                created = true;
            }
            else if (buttonObject.transform.parent != targetParent)
            {
                buttonObject.transform.SetParent(targetParent, false);
            }

            try { buttonObject.transform.SetSiblingIndex(Math.Min(source.transform.GetSiblingIndex() + 1, targetParent.childCount - 1)); }
            catch { }

            ConfigureButton(buttonObject, source);
            SyncButtonState(buttonObject, source);
            SyncVanillaRefitAutoButtons(ui);
            DesignAutoArmorPatch.EnsureButton(ui);

            if (created && !loggedAttached)
            {
                loggedAttached = true;
                Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: attached player Smart Refit constructor button.");
            }
        }
        catch (Exception ex)
        {
            try { SyncVanillaRefitAutoButtons(ui); }
            catch { }
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to attach Smart Refit button. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ConfigureButton(GameObject buttonObject, GameObject source)
    {
        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        if (button == null)
            button = buttonObject.AddComponent<Button>();

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(RunSmartRefit));

        SetButtonText(buttonObject, "Smart\nRefit");
        SetTooltip(
            buttonObject,
            "Updates available default components for the current player refit design without rebuilding guns or armor.");
        DesignerActionButtonVisuals.Apply(buttonObject, DesignerActionButtonVisual.SmartRefit);

        LayoutElement? layout = buttonObject.GetComponent<LayoutElement>();
        LayoutElement? sourceLayout = source.GetComponent<LayoutElement>();
        if (layout != null && sourceLayout != null)
        {
            layout.minWidth = SmartRefitButtonWidth;
            layout.preferredWidth = SmartRefitButtonWidth;
            layout.flexibleWidth = 0f;
            layout.minHeight = sourceLayout.minHeight;
            layout.preferredHeight = sourceLayout.preferredHeight;
            layout.flexibleHeight = sourceLayout.flexibleHeight;
        }
    }

    private static void SyncButtonState(GameObject buttonObject, GameObject source)
    {
        Ship? ship = ResolveDesignerShip(Safe(() => G.ui, null));
        bool visible =
            ModSettings.SmartRefitsEnabled &&
            ship != null &&
            IsHumanMainDesignerShip(ship) &&
            IsRefitContext(Safe(() => G.ui, null), ship);
        if (buttonObject.activeSelf != visible)
            buttonObject.SetActive(visible);

        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        if (button != null)
            button.interactable = visible && !runningSmartRefit && Safe(() => G.ui?.allowEdit ?? false, false);
    }

    private static void SyncVanillaRefitAutoButtons(Ui? ui)
    {
        if (ui == null)
            return;

        Ship? ship = ResolveDesignerShip(ui);
        bool hide = ModSettings.SmartRefitsEnabled &&
                    ship != null &&
                    IsRefitContext(ui, ship);

        SyncVanillaRefitAutoButton(ui, VanillaRandomShipSimpleRefitButtonName, hide);
        SyncVanillaRefitAutoButton(ui, VanillaRandomShipButtonName, hide);
    }

    private static void SyncVanillaRefitAutoButton(Ui ui, string name, bool hide)
    {
        GameObject? buttonObject = FindDeepChild(ui.transform, name);
        if (buttonObject == null)
            return;

        IntPtr key = ObjectPointer(buttonObject);
        if (hide)
        {
            if (buttonObject.activeSelf)
            {
                buttonObject.SetActive(false);
                if (key != IntPtr.Zero)
                    HiddenVanillaRefitAutoButtons.Add(key);
            }

            return;
        }

        if (key != IntPtr.Zero && HiddenVanillaRefitAutoButtons.Remove(key))
            buttonObject.SetActive(true);
    }

    private static IntPtr ObjectPointer(GameObject? buttonObject)
        => buttonObject == null ? IntPtr.Zero : Safe(() => buttonObject.Pointer, IntPtr.Zero);

    private static void RunSmartRefit()
    {
        if (runningSmartRefit)
            return;

        Ui? ui = Safe(() => G.ui, null);
        Ship? ship = ResolveDesignerShip(ui);
        if (ui == null || ship == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; constructor ship was unavailable.");
            return;
        }

        if (!ModSettings.SmartRefitsEnabled)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; Smart Refits option is Vanilla.");
            return;
        }

        if (!IsConstructor() || !Safe(() => ui.allowEdit, false) || !IsHumanMainDesignerShip(ship) || !IsRefitContext(ui, ship))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; active constructor is not an editable player refit.");
            return;
        }

        try
        {
            runningSmartRefit = true;
            try { GameManager.ShowShipBuildingOverlay(); }
            catch { }
            try { ui.HideTooltip(); }
            catch { }

            SmartRefitResult result = SmartRefitResult.Rejected("not-run");
            try
            {
                result = SmartRefitService.Apply(ship);
            }
            finally
            {
                RefreshConstructorUi(ship);
            }

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: button result={(result.Success ? "accepted" : "rejected")} {result.Message}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: button failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            runningSmartRefit = false;
            try { GameManager.HideShipBuildingOverlay(); }
            catch { }
            try { EnsureButton(ui); }
            catch { }
        }
    }

    private static bool IsRefitContext(Ui? ui, Ship ship)
        => Safe(() => ui?.isConstructorRefitMode ?? false, false) ||
           Safe(() => ship.isRefitDesign, false) ||
           Safe(() => ship.designShipForRefit != null, false) ||
           !string.IsNullOrWhiteSpace(Safe(() => ship.refitDesignName, string.Empty));

    private static Ship? ResolveDesignerShip(Ui? ui)
        => Safe(() => ui?.mainShip, null) ?? Safe(() => PlayerController.Instance?.Ship, null);

    private static bool IsHumanMainDesignerShip(Ship ship)
    {
        Player? owner = Safe(() => ship.player, null) ?? PlayerController.Instance;
        return owner != null && Safe(() => owner.isMain && !owner.isAi, false);
    }

    private static bool IsConstructor()
        => Safe(() => GameManager.IsConstructor, false);

    private static void RefreshConstructorUi(Ship? ship)
    {
        Ui? ui = Safe(() => G.ui, null);
        if (ui == null)
            return;

        if (ship != null)
        {
            try { RefreshHullStatsMethod?.Invoke(ship, Array.Empty<object>()); }
            catch (Exception ex) { WarnRefreshOnce("RefreshHullStats", ex); }
        }

        try { OnConShipChangedMethod?.Invoke(ui, new object[] { false }); }
        catch (Exception ex) { WarnRefreshOnce("OnConShipChanged", ex); }

        try { ui.RefreshConstructorInfo(); }
        catch (Exception ex) { WarnRefreshOnce("RefreshConstructorInfo", ex); }
    }

    private static void WarnRefreshOnce(string phase, Exception ex)
    {
        if (loggedRefreshWarning)
            return;

        loggedRefreshWarning = true;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: designer UI refresh failed at {phase}. {ex.GetType().Name}: {ex.Message}");
    }

    private static GameObject? FindDeepChild(Transform root, string name)
    {
        if (root == null)
            return null;
        if (string.Equals(root.name, name, StringComparison.Ordinal))
            return root.gameObject;

        int count = Safe(() => root.childCount, 0);
        for (int i = 0; i < count; i++)
        {
            Transform? child = Safe(() => root.GetChild(i), null);
            if (child == null)
                continue;

            GameObject? result = FindDeepChild(child, name);
            if (result != null)
                return result;
        }

        return null;
    }

    private static void SetButtonText(GameObject buttonObject, string text)
    {
        TMP_Text? tmp = buttonObject.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            RemoveComponent<LocalizeText>(tmp.gameObject);
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 10f;
            tmp.fontSizeMax = Math.Min(tmp.fontSizeMax > 0f ? tmp.fontSizeMax : tmp.fontSize, 18f);
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return;
        }

        Text? uiText = buttonObject.GetComponentInChildren<Text>(true);
        if (uiText != null)
        {
            RemoveComponent<LocalizeText>(uiText.gameObject);
            uiText.text = text;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.resizeTextForBestFit = true;
            uiText.resizeTextMinSize = 10;
            uiText.resizeTextMaxSize = Math.Min(uiText.fontSize, 18);
        }
    }

    private static void SetTooltip(GameObject target, string text)
    {
        RemoveComponent<OnEnter>(target);
        RemoveComponent<OnLeave>(target);

        OnEnter onEnter = target.AddComponent<OnEnter>();
        onEnter.action = new System.Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(text))
                G.ui?.ShowTooltip(text, target);
        });

        OnLeave onLeave = target.AddComponent<OnLeave>();
        onLeave.action = new System.Action(() =>
        {
            try { G.ui?.HideTooltip(); }
            catch { }
        });
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }
}

[HarmonyPatch]
internal static class DesignSmartRefitConstructorUiPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool found = DesignSmartRefitPatch.ConstructorUiTarget() != null;
        if (!found)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refit: Ui.ConstructorUI target not found; button disabled.");
        return found;
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod()
        => DesignSmartRefitPatch.ConstructorUiTarget();

    [HarmonyPostfix]
    private static void Postfix(Ui __instance)
        => DesignSmartRefitPatch.EnsureButton(__instance);
}

[HarmonyPatch]
internal static class DesignSmartRefitRefreshConstructorInfoPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
        => DesignSmartRefitPatch.RefreshConstructorInfoTarget() != null;

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod()
        => DesignSmartRefitPatch.RefreshConstructorInfoTarget();

    [HarmonyPostfix]
    private static void Postfix()
        => DesignSmartRefitPatch.EnsureButton(Safe(() => G.ui, null));

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }
}
