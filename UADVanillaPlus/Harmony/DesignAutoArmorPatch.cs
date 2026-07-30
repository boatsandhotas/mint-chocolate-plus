using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UADVanillaPlus.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Explicit designer action for generating a coherent armor profile. It never
// reacts to hull, gun, component, tonnage, or speed changes; the player must
// click the button for armor to change.
[HarmonyPatch]
internal static class DesignAutoArmorPatch
{
    private const string LogPrefix = "UADVP designer auto armor";
    private const string ButtonName = "UADVP_GenerateArmor";
    private const string AutoLiteButtonName = "UADVP_AutoDesignLite";
    private const string SmartRefitButtonName = "UADVP_SmartRefit";
    private const string VanillaRandomShipButtonName = "RandomShip";
    private const string VanillaRandomShipSimpleRefitButtonName = "RandomShipSimpleRefit";
    private const float DefaultBeltRangeMeters = 10000f;
    private const int ProfileSearchIterations = 32;
    private const int MaxFineFillIterations = 700;
    private const int MaxOverflowFillIterations = 5000;
    private const int MaxAiSmartRefitOverflowFillIterations = 200;
    private const int MaxAiSmartRefitBinaryProbesPerTarget = 10;
    private const int MaxOverflowFillMilliseconds = 2000;
    private const int OverflowTimeCheckInterval = 16;
    private const float ArmorTargetSafetyMargin = 1f;
    private const float ArmorTolerance = 0.0001f;
    private const float AiLeftoverArmorDumpThresholdTons = 100f;
    private const float AiLeftoverArmorDumpStepInches = 0.5f;
    private const float SecondaryGunTopArmorCap = 1f;
    private const float SecondaryGunComfortableSpareRatio = 0.04f;
    private const float SecondaryGunRoomySpareRatio = 0.08f;

    private static readonly MethodInfo? RefreshHullStatsMethod =
        AccessTools.Method(typeof(Ship), "RefreshHullStats", Type.EmptyTypes);
    private static readonly MethodInfo? RefreshGunsStatsMethod =
        AccessTools.Method(typeof(Ship), "RefreshGunsStats", Type.EmptyTypes);
    private static readonly MethodInfo? OnConShipChangedMethod =
        AccessTools.Method(typeof(Ui), "OnConShipChanged", new[] { typeof(bool) });
    private static readonly MethodInfo? RefreshPartsMethod =
        AccessTools.Method(typeof(Ui), "RefreshParts", Type.EmptyTypes);
    private static readonly MethodInfo? GetGunArmorMethod =
        AccessTools.Method(typeof(Ship), "GetGunArmor", new[] { typeof(Part), typeof(PartData), typeof(Ship), typeof(bool) });

    private static string lastPlacementLog = string.Empty;
    private static bool loggedMissingButton;
    private static bool loggedRefreshWarning;
    private static bool loggedMissingGunArmorRows;
    private static bool applyingArmor;
    private static ArmorPerfStats? activeArmorPerfStats;

    private enum ArmorFillMode
    {
        Ui,
        AiSmartRefit
    }

    internal static MethodBase? ConstructorUiTarget()
        => AccessTools.Method(typeof(Ui), nameof(Ui.ConstructorUI), Type.EmptyTypes);

    internal static MethodBase? RefreshConstructorInfoTarget()
        => AccessTools.Method(typeof(Ui), nameof(Ui.RefreshConstructorInfo), Type.EmptyTypes);

    internal static bool TryApplySmartRefitArmor(Ship ship, out string resultText)
    {
        resultText = "not-run";
        if (ship == null)
        {
            resultText = "ship-unavailable";
            return false;
        }

        if (applyingArmor)
        {
            resultText = "already-running";
            return false;
        }

        try
        {
            applyingArmor = true;
            ArmorPerfStats perf = new();
            activeArmorPerfStats = perf;
            perf.Start();

            MainGunArmorSelection mainGun = SelectMainGunArmor(ship);
            ArmorSnapshot originalArmor = CaptureArmor(ship);
            ArmorFitResult result = ApplyArmorProfileFit(ship, mainGun, originalArmor, ArmorFillMode.AiSmartRefit);
            perf.Complete();
            perf.LogIfSlow(result, mainGun);

            resultText = FormatSmartRefitArmorResult(ship, result, mainGun, perf);
            return true;
        }
        catch (Exception ex)
        {
            resultText = $"failed:{ex.GetType().Name}";
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: smart refit armor rebalance failed. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            activeArmorPerfStats = null;
            applyingArmor = false;
        }
    }

    private static string FormatSmartRefitArmorResult(
        Ship ship,
        ArmorFitResult result,
        MainGunArmorSelection mainGun,
        ArmorPerfStats perf)
        => string.Join(
            " ",
            $"result={result.Result}",
            $"gunRows={mainGun.EntryCount}",
            $"casemateRows={mainGun.CasemateCount}",
            $"largestNonCasemateCaliber={Fmt(mainGun.Caliber)}",
            $"profileB={Fmt(result.ProfileB)}",
            $"profileHighBound={Fmt(result.ProfileHighBound)}",
            $"profileBoundSource={result.ProfileBoundSource}",
            $"cappedProfileZones={result.CappedProfileZones}",
            $"capacity={Fmt(result.Capacity)}",
            $"baselineWeight={Fmt(result.BaselineWeight)}",
            $"targetWeight={Fmt(result.TargetWeight)}",
            $"finalWeight={Fmt(result.FinalWeight)}",
            $"budget={Fmt(result.Budget)}",
            $"spent={Fmt(result.Spent)}",
            $"unusedWeight={Fmt(result.UnusedWeight)}",
            $"fineFill={result.FineFillAccepted}/{result.FineFillIterations}",
            $"finalFill={result.FinalFillAccepted}/{result.FinalFillIterations}",
            $"stop={result.FinalFillStopReason}",
            $"smallestRejected={Fmt(result.SmallestRejectedIncrement)}",
            $"targets={result.Targets}",
            $"overflowFill={result.OverflowFillAccepted}/{result.OverflowFillIterations}",
            $"overflowStop={result.OverflowFillStopReason}",
            $"overflowDetails={result.OverflowDetails}",
            $"overflowMinStep={Fmt(OverflowMinStepFor(ArmorFillMode.AiSmartRefit))}",
            $"rejectedOvershoot={result.OverflowRejectedOvershoot}",
            $"rejectedByCap={result.OverflowRejectedByCap}",
            $"leftoverFill={result.LeftoverFillAccepted}/{result.LeftoverFillIterations}",
            $"leftoverStop={result.LeftoverFillStopReason}",
            $"leftoverUnusedBefore={Fmt(result.LeftoverUnusedBefore)}",
            $"leftoverUnusedAfter={Fmt(result.LeftoverUnusedAfter)}",
            $"leftoverTargets={result.LeftoverTargets}",
            $"leftoverDetails={result.LeftoverDetails}",
            $"bandGunArmorClamped={result.SecondaryGunArmorClamped}/{result.SecondaryGunArmorChecked}",
            $"maxBandSideCap={Fmt(result.SecondaryGunSideCap)}",
            $"maxBandTopCap={Fmt(result.SecondaryGunTopCap)}",
            $"elapsedMs={perf.ElapsedMsText}",
            $"recalcCalls={perf.RecalcCalls}",
            $"armorTable={ArmorTableLog(ship, mainGun)}");

    internal static void EnsureButton(Ui? ui)
    {
        if (ui == null || !IsConstructor())
            return;

        try
        {
            Ship? ship = ResolveDesignerShip(ui);
            bool refitContext = ship != null && IsRefitContext(ui, ship);
            GameObject? source = SourceButton(ui, refitContext);
            if (source == null)
            {
                if (!loggedMissingButton)
                {
                    loggedMissingButton = true;
                    Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: vanilla button template not found; Generate Armor button disabled.");
                }

                return;
            }

            Transform? targetParent = source.transform.parent;
            if (targetParent == null)
                return;

            GameObject? buttonObject = FindDeepChild(ui.transform, ButtonName);
            bool created = false;
            bool moved = false;
            if (buttonObject == null)
            {
                buttonObject = UnityEngine.Object.Instantiate(source, targetParent);
                buttonObject.name = ButtonName;
                created = true;
            }
            else if (buttonObject.transform.parent != targetParent)
            {
                buttonObject.transform.SetParent(targetParent, false);
                moved = true;
            }

            int sibling = -1;
            try
            {
                sibling = Math.Min(source.transform.GetSiblingIndex() + 1, targetParent.childCount - 1);
                moved |= buttonObject.transform.GetSiblingIndex() != sibling;
                buttonObject.transform.SetSiblingIndex(Math.Max(0, sibling));
            }
            catch { }

            ConfigureButton(buttonObject, source);
            SyncButtonState(buttonObject, source);

            string parentName = Safe(() => buttonObject.transform.parent?.name ?? "<none>", "<none>");
            string sourceName = Safe(() => source.name ?? "<none>", "<none>");
            int actualSibling = Safe(() => buttonObject.transform.GetSiblingIndex(), sibling);
            string placementLog = $"{parentName}|{sourceName}|{actualSibling}";
            if ((created || moved || sourceName == AutoLiteButtonName) && placementLog != lastPlacementLog)
            {
                lastPlacementLog = placementLog;
                string action = created ? "attached" : "repositioned";
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{LogPrefix}: {action} Generate Armor button parent={LogToken(parentName)} source={LogToken(sourceName)} sibling={actualSibling}.");
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to attach button. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ConfigureButton(GameObject buttonObject, GameObject source)
    {
        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        if (button == null)
            button = buttonObject.AddComponent<Button>();

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(GenerateArmorForActiveShip));

        SetButtonText(buttonObject, "Generate\nArmor");
        SetTooltip(
            buttonObject,
            "Generates a coherent armor profile from the current ship and spends remaining displacement into armor.");
        DesignerActionButtonVisuals.Apply(buttonObject, DesignerActionButtonVisual.ArmorFill);

        LayoutElement? layout = buttonObject.GetComponent<LayoutElement>();
        LayoutElement? sourceLayout = source.GetComponent<LayoutElement>();
        if (layout != null && sourceLayout != null)
        {
            layout.minWidth = sourceLayout.minWidth;
            layout.preferredWidth = sourceLayout.preferredWidth;
            layout.flexibleWidth = sourceLayout.flexibleWidth;
            layout.minHeight = sourceLayout.minHeight;
            layout.preferredHeight = sourceLayout.preferredHeight;
            layout.flexibleHeight = sourceLayout.flexibleHeight;
        }
    }

    private static void SyncButtonState(GameObject buttonObject, GameObject source)
    {
        Ui? ui = Safe(() => G.ui, null);
        Ship? ship = ResolveDesignerShip(ui);
        bool refitVisible = IsEditableHumanRefitContext(ui, ship);
        bool visible = refitVisible || source.activeSelf;
        if (buttonObject.activeSelf != visible)
            buttonObject.SetActive(visible);

        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        Button? sourceButton = source.GetComponent<Button>() ?? source.GetComponentInChildren<Button>(true);
        if (button != null)
            button.interactable = !applyingArmor && (refitVisible
                ? Safe(() => ui?.allowEdit ?? false, false)
                : (sourceButton?.interactable ?? true));
    }

    private static GameObject? SourceButton(Ui ui, bool refitContext)
    {
        if (refitContext)
        {
            GameObject? smartRefit = FindDeepChild(ui.transform, SmartRefitButtonName);
            if (smartRefit != null && smartRefit.activeSelf)
                return smartRefit;

            return FindDeepChild(ui.transform, VanillaRandomShipSimpleRefitButtonName) ??
                   FindDeepChild(ui.transform, AutoLiteButtonName) ??
                   FindDeepChild(ui.transform, VanillaRandomShipButtonName);
        }

        return FindDeepChild(ui.transform, AutoLiteButtonName) ??
               FindDeepChild(ui.transform, VanillaRandomShipButtonName);
    }

    private static bool IsEditableHumanRefitContext(Ui? ui, Ship? ship)
        => ui != null &&
           ship != null &&
           Safe(() => ui.allowEdit, false) &&
           IsHumanMainDesignerShip(ship) &&
           IsRefitContext(ui, ship);

    private static bool IsRefitContext(Ui? ui, Ship ship)
        => Safe(() => ui?.isConstructorRefitMode ?? false, false) ||
           Safe(() => ship.isRefitDesign, false) ||
           Safe(() => ship.designShipForRefit != null, false) ||
           !string.IsNullOrWhiteSpace(Safe(() => ship.refitDesignName, string.Empty));

    private static Ship? ResolveDesignerShip(Ui? ui)
        => Safe(() => ui?.mainShip, null) ?? Safe(() => PlayerController.Instance?.Ship, null);

    private static void GenerateArmorForActiveShip()
    {
        if (applyingArmor)
            return;

        Ui? ui = Safe(() => G.ui, null);
        Ship? ship = Safe(() => ui?.mainShip, null) ?? Safe(() => PlayerController.Instance?.Ship, null);
        if (ui == null || ship == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; constructor ship was unavailable.");
            return;
        }

        if (!IsConstructor() || !Safe(() => ui.allowEdit, false) || !IsHumanMainDesignerShip(ship))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; constructor is not editable for the main player.");
            return;
        }

        try
        {
            applyingArmor = true;
            ArmorPerfStats perf = new();
            activeArmorPerfStats = perf;
            perf.Start();

            MainGunArmorSelection mainGun = SelectMainGunArmor(ship);
            ArmorSnapshot originalArmor = CaptureArmor(ship);

            ArmorFitResult result = ApplyArmorProfileFit(ship, mainGun, originalArmor, ArmorFillMode.Ui);
            RefreshConstructorUi(ship);

            long finalLogStart = Stopwatch.GetTimestamp();
            string armorLog = ArmorTableLog(ship, mainGun);
            perf.AddFinalLog(finalLogStart);
            perf.Complete();

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: applied mode=ProfileFit result={result.Result} gunRows={mainGun.EntryCount} casemateRows={mainGun.CasemateCount} largestNonCasemateCaliber={Fmt(mainGun.Caliber)} " +
                $"profileB={Fmt(result.ProfileB)} capacity={Fmt(result.Capacity)} baselineWeight={Fmt(result.BaselineWeight)} targetWeight={Fmt(result.TargetWeight)} finalWeight={Fmt(result.FinalWeight)} " +
                $"budget={Fmt(result.Budget)} spent={Fmt(result.Spent)} unusedWeight={Fmt(result.UnusedWeight)} fineFill={result.FineFillAccepted}/{result.FineFillIterations} " +
                $"finalFill={result.FinalFillAccepted}/{result.FinalFillIterations} stop={result.FinalFillStopReason} smallestRejected={Fmt(result.SmallestRejectedIncrement)} targets={result.Targets} " +
                $"overflowFill={result.OverflowFillAccepted}/{result.OverflowFillIterations} overflowStop={result.OverflowFillStopReason} overflowMinStep={Fmt(OverflowMinStepFor(ArmorFillMode.Ui))} rejectedOvershoot={result.OverflowRejectedOvershoot} rejectedByCap={result.OverflowRejectedByCap} " +
                $"bandGunArmorClamped={result.SecondaryGunArmorClamped}/{result.SecondaryGunArmorChecked} maxBandSideCap={Fmt(result.SecondaryGunSideCap)} maxBandTopCap={Fmt(result.SecondaryGunTopCap)} " +
                $"elapsedMs={perf.ElapsedMsText} recalcCalls={perf.RecalcCalls} armor={armorLog}.");

            perf.LogIfSlow(result, mainGun);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to generate armor. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            activeArmorPerfStats = null;
            applyingArmor = false;
        }
    }

    private static ArmorFitResult ApplyArmorProfileFit(Ship ship, MainGunArmorSelection mainGun, ArmorSnapshot originalArmor, ArmorFillMode mode)
    {
        List<ArmorTarget> targets = BuildArmorTargets(ship, mainGun);
        if (targets.Count <= 0)
            return new ArmorFitResult("no-valid-targets", 0f, 0f, "none", "none", 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0, 0, 0, 0, "not-run", 0f, 0, 0, "not-run", 0, 0, "none", 0, 0, "not-run", 0f, 0f, 0, "none", 0, 0, 0f, 0f);

        try
        {
            long resetStart = Stopwatch.GetTimestamp();
            ResetAllArmor(ship, targets);
            RecalculateArmorWeight(ship);

            float capacity = Safe(() => ship.Tonnage(), 0f);
            float baselineWeight = ShipWeight(ship);
            float targetWeight = Math.Max(0f, capacity - ArmorTargetSafetyMargin);
            float budget = targetWeight - baselineWeight;
            activeArmorPerfStats?.AddReset(resetStart);

            if (capacity <= 0f || budget <= 0f)
            {
                string status = capacity > 0f && baselineWeight > capacity + 0.5f
                    ? "min-only-still-overweight"
                    : "min-only-no-budget";
                long clampStart = Stopwatch.GetTimestamp();
                SecondaryGunArmorStats secondary = ClampSecondaryGunArmor(ship, mainGun, 0f, capacity, targetWeight);
                activeArmorPerfStats?.AddClamp(clampStart);
                RecalculateArmorWeight(ship);
                float noBudgetFinalWeight = ShipWeight(ship);
                return new ArmorFitResult(
                    status,
                    0f,
                    0f,
                    "no-budget",
                    "none",
                    capacity,
                    baselineWeight,
                    targetWeight,
                    noBudgetFinalWeight,
                    budget,
                    Math.Max(0f, noBudgetFinalWeight - baselineWeight),
                    Math.Max(0f, targetWeight - noBudgetFinalWeight),
                    targets.Count,
                    0,
                    0,
                    0,
                    0,
                    "no-budget",
                    0f,
                    0,
                    0,
                    "no-budget",
                    0,
                    0,
                    "none",
                    0,
                    0,
                    "not-run",
                    0f,
                    0f,
                    0,
                    "none",
                    secondary.Checked,
                    secondary.Clamped,
                    secondary.SideCap,
                    secondary.TopCap);
            }

            ProfileBound profileBound = ProfileSearchBound(targets, mode);
            if (profileBound.High <= ArmorTolerance)
            {
                SecondaryGunArmorStats secondary = ClampSecondaryGunArmor(ship, mainGun, 0f, capacity, targetWeight);
                RecalculateArmorWeight(ship);
                float noProfileFinalWeight = ShipWeight(ship);
                return new ArmorFitResult(
                    "no-profile-room",
                    0f,
                    profileBound.High,
                    profileBound.Source,
                    "none",
                    capacity,
                    baselineWeight,
                    targetWeight,
                    noProfileFinalWeight,
                    budget,
                    Math.Max(0f, noProfileFinalWeight - baselineWeight),
                    Math.Max(0f, targetWeight - noProfileFinalWeight),
                    targets.Count,
                    0,
                    0,
                    0,
                    0,
                    "no-profile-room",
                    0f,
                    0,
                    0,
                    "no-profile-room",
                    0,
                    0,
                    "none",
                    0,
                    0,
                    "not-run",
                    0f,
                    0f,
                    0,
                    "none",
                    secondary.Checked,
                    secondary.Clamped,
                    secondary.SideCap,
                    secondary.TopCap);
            }

            float low = 0f;
            float high = profileBound.High;
            float bestB = 0f;
            float bestWeight = baselineWeight;

            long profileStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < ProfileSearchIterations; i++)
            {
                activeArmorPerfStats?.RecordProfileSearchIteration();
                float candidate = (low + high) * 0.5f;
                ApplyProfile(ship, targets, candidate);
                float weight = ShipWeight(ship);
                if (weight <= targetWeight + 0.001f)
                {
                    bestB = candidate;
                    bestWeight = weight;
                    low = candidate;
                }
                else
                {
                    high = candidate;
                }
            }
            activeArmorPerfStats?.AddProfileSearch(profileStart);

            long applyBestStart = Stopwatch.GetTimestamp();
            ApplyProfile(ship, targets, bestB);
            activeArmorPerfStats?.AddApplyBestProfile(applyBestStart);
            long clampBeforeStart = Stopwatch.GetTimestamp();
            SecondaryGunArmorStats secondaryBeforeFill = ClampSecondaryGunArmor(ship, mainGun, bestB, capacity, targetWeight);
            activeArmorPerfStats?.AddClamp(clampBeforeStart);
            bestWeight = ShipWeight(ship);
            long fineStart = Stopwatch.GetTimestamp();
            FineFillStats fineFill = FineFillProfile(ship, targets, bestB, targetWeight);
            activeArmorPerfStats?.AddFineFill(fineStart, fineFill);
            long clampAfterFillStart = Stopwatch.GetTimestamp();
            SecondaryGunArmorStats secondaryAfterFill = ClampSecondaryGunArmor(ship, mainGun, bestB, capacity, targetWeight);
            activeArmorPerfStats?.AddClamp(clampAfterFillStart);
            long finalFillStart = Stopwatch.GetTimestamp();
            FinalFillStats finalFill = FinalFineFill(ship, targets, mainGun, bestB, capacity, targetWeight);
            activeArmorPerfStats?.AddFinalFill(finalFillStart, finalFill);
            long clampAfterFinalStart = Stopwatch.GetTimestamp();
            SecondaryGunArmorStats secondaryAfterFinalFill = ClampSecondaryGunArmor(ship, mainGun, bestB, capacity, targetWeight);
            activeArmorPerfStats?.AddClamp(clampAfterFinalStart);
            long overflowStart = Stopwatch.GetTimestamp();
            OverflowFillStats overflowFill = OverflowFineFill(ship, targets, mainGun, bestB, capacity, targetWeight, mode);
            activeArmorPerfStats?.AddOverflowFill(overflowStart, overflowFill);
            long clampAfterOverflowStart = Stopwatch.GetTimestamp();
            SecondaryGunArmorStats secondaryAfterOverflowFill = ClampSecondaryGunArmor(ship, mainGun, bestB, capacity, targetWeight);
            activeArmorPerfStats?.AddClamp(clampAfterOverflowStart);
            OverflowFillStats leftoverFill = new(0, 0, "not-run", 0, 0, "none");
            float leftoverUnusedBefore = 0f;
            float leftoverUnusedAfter = 0f;
            int leftoverTargets = 0;
            if (mode == ArmorFillMode.AiSmartRefit)
            {
                AiLeftoverArmorDumpResult leftoverDump = AiLeftoverArmorDumpFill(
                    ship,
                    mainGun,
                    capacity,
                    targetWeight);
                leftoverFill = leftoverDump.Stats;
                leftoverUnusedBefore = leftoverDump.UnusedBefore;
                leftoverUnusedAfter = leftoverDump.UnusedAfter;
                leftoverTargets = leftoverDump.Targets;
            }

            long finalWeightStart = Stopwatch.GetTimestamp();
            float finalWeight = ShipWeight(ship);
            activeArmorPerfStats?.AddFinalWeight(finalWeightStart);
            float unused = Math.Max(0f, targetWeight - finalWeight);
            int secondaryChecked = Math.Max(Math.Max(Math.Max(secondaryBeforeFill.Checked, secondaryAfterFill.Checked), secondaryAfterFinalFill.Checked), secondaryAfterOverflowFill.Checked);
            int secondaryClamped = secondaryBeforeFill.Clamped + secondaryAfterFill.Clamped + secondaryAfterFinalFill.Clamped + secondaryAfterOverflowFill.Clamped;
            float secondarySideCap = Math.Max(Math.Max(Math.Max(secondaryBeforeFill.SideCap, secondaryAfterFill.SideCap), secondaryAfterFinalFill.SideCap), secondaryAfterOverflowFill.SideCap);
            float secondaryTopCap = Math.Max(Math.Max(Math.Max(secondaryBeforeFill.TopCap, secondaryAfterFill.TopCap), secondaryAfterFinalFill.TopCap), secondaryAfterOverflowFill.TopCap);

            string result = bestB <= ArmorTolerance
                ? "min-only-profile-too-heavy"
                : unused <= ArmorTargetSafetyMargin
                    ? "fit"
                    : fineFill.Accepted + finalFill.Accepted + overflowFill.Accepted > 0
                        ? "fit-fine-filled"
                        : "fit-with-unused";

            return new ArmorFitResult(
                result,
                bestB,
                profileBound.High,
                profileBound.Source,
                CappedProfileZones(targets, bestB),
                capacity,
                baselineWeight,
                targetWeight,
                finalWeight,
                budget,
                Math.Max(0f, finalWeight - baselineWeight),
                unused,
                targets.Count,
                fineFill.Accepted,
                fineFill.Iterations,
                finalFill.Accepted,
                finalFill.Iterations,
                finalFill.StopReason,
                finalFill.SmallestRejectedIncrement,
                overflowFill.Accepted,
                overflowFill.Iterations,
                overflowFill.StopReason,
                overflowFill.RejectedOvershoot,
                overflowFill.RejectedByCap,
                overflowFill.Details,
                leftoverFill.Accepted,
                leftoverFill.Iterations,
                leftoverFill.StopReason,
                leftoverUnusedBefore,
                leftoverUnusedAfter,
                leftoverTargets,
                leftoverFill.Details,
                secondaryChecked,
                secondaryClamped,
                secondarySideCap,
                secondaryTopCap);
        }
        catch
        {
            RestoreArmor(ship, originalArmor);
            throw;
        }
    }

    private static void ResetAllArmor(Ship ship, List<ArmorTarget> targets)
    {
        ResetAllGunArmor(ship);

        HashSet<Ship.A> zones = new();
        try
        {
            if (ship.armor != null)
            {
                foreach (var pair in ship.armor)
                    zones.Add(pair.Key);
            }
        }
        catch
        {
        }

        foreach (ArmorTarget target in targets)
        {
            if (!target.IsGunArmor)
                zones.Add(target.Zone);
        }

        zones.Add(Ship.A.TurretSide);
        zones.Add(Ship.A.TurretTop);
        zones.Add(Ship.A.Barbette);

        foreach (Ship.A zone in zones)
        {
            float min = Safe(() => ship.MinArmorForZone(zone), 0f);
            float max = Safe(() => ship.MaxArmorForZone(zone, null), min);
            if (max < min)
                (min, max) = (max, min);
            if (min < 0f || max < 0f)
                continue;

            float value = ClampRawArmor(min, min, max);
            try { ship.SetArmor(zone, value, false); }
            catch { }
        }
    }

    private static void ResetAllGunArmor(Ship ship)
    {
        try
        {
            foreach (GunArmorEntry entry in GunArmorEntries(ship))
            {
                if (entry.Row == null)
                    continue;

                TrySetGunArmorRow(entry.Row, Ship.A.TurretSide, 0f);
                TrySetGunArmorRow(entry.Row, Ship.A.TurretTop, 0f);
                TrySetGunArmorRow(entry.Row, Ship.A.Barbette, 0f);
            }
        }
        catch
        {
        }
    }

    private static List<ArmorTarget> BuildArmorTargets(Ship ship, MainGunArmorSelection mainGun)
    {
        List<ArmorTarget> targets = new();
        AddHullTarget(ship, targets, Ship.A.Belt, 1.0f, 1.05f, 3);
        AddHullTarget(ship, targets, Ship.A.ConningTower, 1.0f, 1.05f, 2);
        AddHullTarget(ship, targets, Ship.A.BeltBow, 0.38f, 0.42f, 4);
        AddHullTarget(ship, targets, Ship.A.BeltStern, 0.38f, 0.42f, 4);
        AddHullTarget(ship, targets, Ship.A.Deck, 0.125f, 0.18f, 5);
        AddHullTarget(ship, targets, Ship.A.DeckBow, 0.125f, 0.15f, 7);
        AddHullTarget(ship, targets, Ship.A.DeckStern, 0.125f, 0.15f, 7);
        AddHullTarget(ship, targets, Ship.A.Superstructure, 0.125f, 0.15f, 9);
        AddHullTarget(ship, targets, Ship.A.InnerDeck_1st, 0.065f, 0.08f, 8);
        AddHullTarget(ship, targets, Ship.A.InnerBelt_1st, 0.22f, 0.3f, 8);

        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            AddGunBandTargets(ship, targets, entry);
        }

        return targets
            .OrderBy(static target => target.Priority)
            .ThenBy(static target => target.Zone)
            .ToList();
    }

    private static void AddHullTarget(Ship ship, List<ArmorTarget> targets, Ship.A zone, float ratio, float capRatio, int priority)
    {
        float min = Safe(() => ship.MinArmorForZone(zone), float.NaN);
        float max = Safe(() => ship.MaxArmorForZone(zone, null), float.NaN);
        AddTarget(targets, zone, false, null, null, ratio, capRatio, priority, min, max);
    }

    private static void AddGunTarget(Ship ship, List<ArmorTarget> targets, PartData gun, Ship.A zone, float ratio, float capRatio, int priority)
    {
        float max = Safe(() => ship.MaxArmorForZone(zone, gun), float.NaN);
        AddTarget(targets, zone, true, gun, null, ratio, capRatio, priority, 0f, max);
    }

    private static void AddGunBandTargets(Ship ship, List<ArmorTarget> targets, GunArmorEntry entry)
    {
        GunArmorBand band = GunArmorBandFor(ship, entry);
        AddGunRowTarget(ship, targets, entry, Ship.A.TurretSide, band.SideFloor, band.SideCap, band.SidePriority);
        AddGunRowTarget(ship, targets, entry, Ship.A.TurretTop, band.TopFloor, band.TopCap, band.TopPriority);
        AddGunRowTarget(ship, targets, entry, Ship.A.Barbette, band.BarbetteFloor, band.BarbetteCap, band.BarbettePriority);
    }

    private static void AddGunRowTarget(Ship ship, List<ArmorTarget> targets, GunArmorEntry entry, Ship.A zone, float floor, float cap, int priority)
    {
        if (entry.PartData == null || entry.Row == null)
            return;

        float legalMax = Safe(() => ship.MaxArmorForZone(zone, entry.PartData), cap);
        if (float.IsNaN(legalMax) || legalMax < 0f)
            legalMax = cap;

        float min = Math.Clamp(Math.Min(floor, cap), 0f, Math.Max(0f, legalMax));
        float max = Math.Clamp(cap, 0f, Math.Max(0f, legalMax));
        if (max < min)
            max = min;

        AddTarget(targets, zone, true, entry.PartData, entry.Row, 0f, 0f, priority, min, max);
    }

    private static GunArmorBand GunArmorBandFor(Ship ship, GunArmorEntry entry)
    {
        float caliber = Math.Max(0f, entry.Caliber);
        bool turretMount = !entry.IsCasemate;

        if (caliber <= 4.05f)
        {
            return new GunArmorBand(
                InchesToArmor(1f),
                InchesToArmor(1f),
                turretMount ? InchesToArmor(1f) : 0f,
                InchesToArmor(1f),
                InchesToArmor(1f),
                turretMount ? InchesToArmor(1f) : 0f,
                30,
                31,
                32);
        }

        if (caliber <= 6.05f)
        {
            return new GunArmorBand(
                InchesToArmor(2f),
                InchesToArmor(1f),
                turretMount ? InchesToArmor(1f) : 0f,
                InchesToArmor(2f),
                InchesToArmor(1f),
                turretMount ? InchesToArmor(1f) : 0f,
                25,
                26,
                27);
        }

        if (caliber < 10f)
        {
            return new GunArmorBand(
                InchesToArmor(4f),
                InchesToArmor(2f),
                turretMount ? InchesToArmor(2f) : 0f,
                InchesToArmor(4f),
                InchesToArmor(2f),
                turretMount ? InchesToArmor(2f) : 0f,
                18,
                19,
                20);
        }

        float roof = LargeGunRoofArmor(ship, caliber);
        return new GunArmorBand(
            InchesToArmor(caliber),
            InchesToArmor(roof),
            InchesToArmor(caliber * 0.5f),
            float.PositiveInfinity,
            InchesToArmor(Math.Max(roof, caliber * 0.35f)),
            float.PositiveInfinity,
            1,
            8,
            7);
    }

    private static float LargeGunRoofArmor(Ship ship, float caliber)
    {
        int year = Safe(
            () => (ship.isRefitDesign ? ship.dateCreatedRefit : ship.dateCreated).AsDate().Year,
            Safe(() => CampaignController.Instance!.CurrentDate.AsDate().Year, 0));
        if (year >= 1930)
            return Math.Max(4f, caliber * 0.25f);
        if (year >= 1910)
            return Math.Max(3f, caliber * 0.22f);

        return Math.Max(2f, caliber * 0.18f);
    }

    private static float InchesToArmor(float inches)
        => inches * 25.4f;

    private static void AddTarget(List<ArmorTarget> targets, Ship.A zone, bool isGunArmor, PartData? gunPart, Ship.TurretArmor? gunArmorRow, float ratio, float capRatio, int priority, float min, float max)
    {
        if (float.IsNaN(min) || float.IsNaN(max))
            return;
        if (max < min)
            (min, max) = (max, min);
        if (max < min - ArmorTolerance || max < 0f || min < 0f)
            return;

        min = ClampRawArmor(min, min, max);
        max = ClampRawArmor(max, min, max);
        if (max < min - ArmorTolerance || (max <= ArmorTolerance && min <= ArmorTolerance))
            return;

        targets.Add(new ArmorTarget(zone, isGunArmor, gunPart, gunArmorRow, ratio, Math.Max(ratio, capRatio), priority, min, max));
    }

    private static ProfileBound ProfileSearchBound(List<ArmorTarget> targets, ArmorFillMode mode)
    {
        if (mode == ArmorFillMode.AiSmartRefit)
            return AiSmartRefitProfileSearchBound(targets);

        return ConservativeProfileSearchBound(targets);
    }

    private static ProfileBound ConservativeProfileSearchBound(List<ArmorTarget> targets)
    {
        float max = float.PositiveInfinity;
        string source = "none";
        foreach (ArmorTarget target in targets)
        {
            if (target.Ratio <= ArmorTolerance)
                continue;

            float candidate = target.Max / target.Ratio;
            if (candidate < max)
            {
                max = candidate;
                source = ArmorTargetToken(target);
            }
        }

        if (float.IsInfinity(max) || float.IsNaN(max))
            return new ProfileBound(0f, "none");

        return new ProfileBound(Math.Max(0f, max), $"conservative:{source}");
    }

    private static ProfileBound AiSmartRefitProfileSearchBound(List<ArmorTarget> targets)
    {
        ArmorTarget belt = default;
        bool hasBelt = false;
        foreach (ArmorTarget target in targets.OrderBy(static target => target.Priority))
        {
            if (target.IsGunArmor || target.Zone != Ship.A.Belt || target.Ratio <= ArmorTolerance)
                continue;

            belt = target;
            hasBelt = true;
            break;
        }

        if (hasBelt)
            return new ProfileBound(Math.Max(0f, belt.Max / belt.Ratio), $"ai-belt:{ArmorTargetToken(belt)}");

        ArmorTarget fallback = default;
        bool hasFallback = false;
        float best = 0f;
        foreach (ArmorTarget target in targets)
        {
            if (target.Ratio <= ArmorTolerance)
                continue;

            float candidate = target.Max / target.Ratio;
            float anchorBonus = IsProfileAnchorZone(target.Zone) ? 1000000f : 0f;
            if (hasFallback && candidate + anchorBonus <= best)
                continue;

            fallback = target;
            hasFallback = true;
            best = candidate + anchorBonus;
        }

        if (!hasFallback)
            return new ProfileBound(0f, "none");

        return new ProfileBound(Math.Max(0f, fallback.Max / fallback.Ratio), $"ai-fallback:{ArmorTargetToken(fallback)}");
    }

    private static bool IsProfileAnchorZone(Ship.A zone)
        => zone == Ship.A.Belt ||
           zone == Ship.A.BeltBow ||
           zone == Ship.A.BeltStern ||
           zone == Ship.A.InnerBelt_1st ||
           zone == Ship.A.InnerBelt_2nd ||
           zone == Ship.A.InnerBelt_3rd;

    private static string CappedProfileZones(List<ArmorTarget> targets, float profileB)
    {
        if (profileB <= ArmorTolerance)
            return "none";

        List<string> capped = new();
        int more = 0;
        foreach (ArmorTarget target in targets.OrderBy(static target => target.Priority))
        {
            if (target.Ratio <= ArmorTolerance)
                continue;

            float wanted = profileB * target.Ratio;
            if (target.Max > wanted - ArmorTolerance)
                continue;

            if (capped.Count < 6)
                capped.Add($"{ArmorTargetToken(target)}:{Fmt(target.Max)}/{Fmt(wanted)}");
            else
                more++;
        }

        if (more > 0)
            capped.Add($"+{more}more");

        return capped.Count == 0 ? "none" : string.Join("|", capped);
    }

    private static void ApplyProfile(Ship ship, List<ArmorTarget> targets, float profileB)
    {
        foreach (ArmorTarget target in targets)
        {
            float value = ClampTargetArmor(target, profileB * target.Ratio);
            SetTargetArmor(ship, target, value);
        }

        RecalculateArmorWeight(ship);
    }

    private static FineFillStats FineFillProfile(Ship ship, List<ArmorTarget> targets, float profileB, float targetWeight)
    {
        float step = ArmorStep();
        float[] increments =
        {
            Math.Max(step, 0.05f),
            Math.Max(step * 0.5f, 0.025f),
            Math.Max(step * 0.25f, 0.01f),
            Math.Max(step * 0.125f, 0.005f),
            0.001f
        };

        int accepted = 0;
        int iterations = 0;
        bool anyAccepted;
        List<ArmorTarget> ordered = targets.OrderBy(static target => target.Priority).ToList();

        do
        {
            anyAccepted = false;
            foreach (ArmorTarget target in ordered)
            {
                if (iterations >= MaxFineFillIterations || ShipWeight(ship) >= targetWeight - 0.05f)
                    return new FineFillStats(accepted, iterations);

                float cap = ClampTargetArmor(target, profileB * target.CapRatio);
                float current = CurrentArmor(ship, target);
                if (current >= cap - ArmorTolerance)
                    continue;

                foreach (float increment in increments)
                {
                    iterations++;
                    float next = ClampTargetArmor(target, Math.Min(current + increment, cap));
                    if (next <= current + ArmorTolerance)
                        continue;

                    SetTargetArmor(ship, target, next);
                    RecalculateArmorWeight(ship);
                    if (ShipWeight(ship) > targetWeight + 0.001f)
                    {
                        SetTargetArmor(ship, target, current);
                        RecalculateArmorWeight(ship);
                        continue;
                    }

                    accepted++;
                    anyAccepted = true;
                    break;
                }
            }
        }
        while (anyAccepted && iterations < MaxFineFillIterations);

        return new FineFillStats(accepted, iterations);
    }

    private static SecondaryGunArmorStats ClampSecondaryGunArmor(Ship ship, MainGunArmorSelection mainGun, float profileB, float capacity, float targetWeight)
    {
        int checkedCount = 0;
        int clampedCount = 0;
        float maxSideCap = 0f;
        float maxTopCap = 0f;

        List<GunArmorEntry> entries = GunArmorEntries(ship);
        foreach (GunArmorEntry entry in entries)
        {
            if (entry.PartData == null || entry.Row == null)
                continue;

            checkedCount++;
            bool changed = false;
            GunArmorBand band = GunArmorBandFor(ship, entry);
            float sideLimit = LegalGunArmorCap(ship, entry.PartData, Ship.A.TurretSide, band.SideCap);
            float topLimit = LegalGunArmorCap(ship, entry.PartData, Ship.A.TurretTop, band.TopCap);
            float barbetteLimit = LegalGunArmorCap(ship, entry.PartData, Ship.A.Barbette, band.BarbetteCap);
            maxSideCap = Math.Max(maxSideCap, sideLimit);
            maxTopCap = Math.Max(maxTopCap, topLimit);

            if (entry.Side > sideLimit + ArmorTolerance)
            {
                TrySetGunArmorRow(entry.Row, Ship.A.TurretSide, sideLimit);
                changed = true;
            }

            if (entry.Top > topLimit + ArmorTolerance)
            {
                TrySetGunArmorRow(entry.Row, Ship.A.TurretTop, topLimit);
                changed = true;
            }

            if (entry.Barbette > barbetteLimit + ArmorTolerance)
            {
                TrySetGunArmorRow(entry.Row, Ship.A.Barbette, barbetteLimit);
                changed = true;
            }

            if (changed)
                clampedCount++;
        }

        if (clampedCount > 0)
            RecalculateArmorWeight(ship);

        return new SecondaryGunArmorStats(checkedCount, clampedCount, maxSideCap, maxTopCap);
    }

    private static FinalFillStats FinalFineFill(Ship ship, List<ArmorTarget> targets, MainGunArmorSelection mainGun, float profileB, float capacity, float targetWeight)
    {
        float step = ArmorStep();
        float[] increments =
        {
            Math.Max(step, 0.05f),
            Math.Max(step * 0.5f, 0.025f),
            Math.Max(step * 0.25f, 0.01f),
            Math.Max(step * 0.125f, 0.005f),
            0.001f
        };

        List<ArmorTarget> ordered = BuildFinalFillTargets(ship, targets, mainGun, profileB, capacity, targetWeight);
        int accepted = 0;
        int iterations = 0;
        float smallestRejected = 0f;

        while (iterations < MaxFineFillIterations)
        {
            float currentWeight = ShipWeight(ship);
            if (currentWeight >= targetWeight - ArmorTargetSafetyMargin)
                return new FinalFillStats(accepted, iterations, "near-target", smallestRejected);

            bool anyAccepted = false;
            foreach (ArmorTarget target in ordered)
            {
                if (iterations >= MaxFineFillIterations)
                    return new FinalFillStats(accepted, iterations, "iteration-cap", smallestRejected);

                float current = CurrentArmor(ship, target);
                if (current >= target.Max - ArmorTolerance)
                    continue;

                bool acceptedForTarget = false;
                foreach (float increment in increments)
                {
                    iterations++;
                    float next = ClampTargetArmor(target, Math.Min(current + increment, target.Max));
                    if (next <= current + ArmorTolerance)
                        continue;

                    SetTargetArmor(ship, target, next);
                    RecalculateArmorWeight(ship);
                    float nextWeight = ShipWeight(ship);
                    if (nextWeight > targetWeight + 0.001f)
                    {
                        SetTargetArmor(ship, target, current);
                        RecalculateArmorWeight(ship);
                        smallestRejected = smallestRejected <= ArmorTolerance
                            ? increment
                            : Math.Min(smallestRejected, increment);
                        continue;
                    }

                    accepted++;
                    anyAccepted = true;
                    acceptedForTarget = true;
                    break;
                }

                if (acceptedForTarget && ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                    return new FinalFillStats(accepted, iterations, "near-target", smallestRejected);
            }

            if (!anyAccepted)
            {
                bool allMaxed = ordered.All(target => CurrentArmor(ship, target) >= target.Max - ArmorTolerance);
                return new FinalFillStats(accepted, iterations, allMaxed ? "all-maxed" : "no-affordable-increment", smallestRejected);
            }
        }

        return new FinalFillStats(accepted, iterations, "iteration-cap", smallestRejected);
    }

    private static OverflowFillStats OverflowFineFill(Ship ship, List<ArmorTarget> profileTargets, MainGunArmorSelection mainGun, float profileB, float capacity, float targetWeight, ArmorFillMode mode)
    {
        Stopwatch overflowTimer = Stopwatch.StartNew();
        int maxIterations = MaxOverflowFillIterationsFor(mode);

        List<ArmorTarget> ordered = BuildOverflowFillTargets(ship, profileTargets, mainGun, profileB, capacity, targetWeight, mode);
        if (mode == ArmorFillMode.AiSmartRefit)
            return OverflowDoctrineJumpFill(ship, ordered, maxIterations, targetWeight);

        float[] increments = OverflowIncrementsFor(mode);

        int accepted = 0;
        int iterations = 0;
        int rejectedOvershoot = 0;
        int rejectedByCap = 0;

        while (iterations < maxIterations)
        {
            if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, "none");

            bool anyAccepted = false;
            foreach (ArmorTarget target in ordered)
            {
                if (iterations >= maxIterations)
                    return new OverflowFillStats(accepted, iterations, "iteration-cap", rejectedOvershoot, rejectedByCap, "none");
                if (ShouldStopOverflowForTime(mode, overflowTimer, iterations))
                    return new OverflowFillStats(accepted, iterations, "time-cap", rejectedOvershoot, rejectedByCap, "none");

                float current = CurrentArmor(ship, target);
                if (current >= target.Max - ArmorTolerance)
                {
                    rejectedByCap++;
                    continue;
                }

                foreach (float increment in increments)
                {
                    iterations++;
                    if (ShouldStopOverflowForTime(mode, overflowTimer, iterations))
                        return new OverflowFillStats(accepted, iterations, "time-cap", rejectedOvershoot, rejectedByCap, "none");

                    float next = ClampTargetArmor(target, Math.Min(current + increment, target.Max));
                    if (next <= current + ArmorTolerance)
                    {
                        rejectedByCap++;
                        continue;
                    }

                    SetTargetArmor(ship, target, next);
                    RecalculateArmorWeight(ship);
                    if (ShipWeight(ship) > targetWeight + 0.001f)
                    {
                        SetTargetArmor(ship, target, current);
                        RecalculateArmorWeight(ship);
                        rejectedOvershoot++;
                        continue;
                    }

                    accepted++;
                    anyAccepted = true;
                    break;
                }

                if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                    return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, "none");
            }

            if (!anyAccepted)
            {
                bool allMaxed = ordered.Count > 0 && ordered.All(target => CurrentArmor(ship, target) >= target.Max - ArmorTolerance);
                return new OverflowFillStats(accepted, iterations, allMaxed ? "all-maxed" : "no-affordable-increment", rejectedOvershoot, rejectedByCap, "none");
            }
        }

        return new OverflowFillStats(accepted, iterations, "iteration-cap", rejectedOvershoot, rejectedByCap, "none");
    }

    private static OverflowFillStats OverflowDoctrineJumpFill(
        Ship ship,
        List<ArmorTarget> ordered,
        int maxIterations,
        float targetWeight)
    {
        int accepted = 0;
        int iterations = 0;
        int rejectedOvershoot = 0;
        int rejectedByCap = 0;
        int maxAccepted = 0;
        int binaryAccepted = 0;
        int touched = 0;
        string firstTouched = "none";
        string lastTouched = "none";
        string largestJump = "none";
        float largestDelta = 0f;

        foreach (ArmorTarget target in ordered)
        {
            if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, AiOverflowDetails());

            if (iterations >= maxIterations)
                return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());

            float current = CurrentArmor(ship, target);
            float max = ClampTargetArmor(target, target.Max);
            if (current >= max - ArmorTolerance)
            {
                rejectedByCap++;
                continue;
            }

            string targetToken = ArmorTargetToken(target);
            float original = current;
            float best = current;
            float low = current;
            float high = max;
            float stateValue = current;

            if (!ProbeArmorValue(max, out float maxWeight))
                return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());

            if (maxWeight <= targetWeight + 0.001f)
            {
                AcceptTarget(max, true);
                if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                    return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, AiOverflowDetails());
                continue;
            }

            rejectedOvershoot++;
            HashSet<string> probed = new(StringComparer.Ordinal);
            probed.Add(ArmorProbeKey(max));
            for (int probe = 0; probe < MaxAiSmartRefitBinaryProbesPerTarget && iterations < maxIterations; probe++)
            {
                float candidate = ClampTargetArmor(target, (low + high) * 0.5f);
                string key = ArmorProbeKey(candidate);
                if (candidate <= low + ArmorTolerance || candidate >= high - ArmorTolerance || !probed.Add(key))
                    break;

                if (!ProbeArmorValue(candidate, out float candidateWeight))
                    return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());

                if (candidateWeight <= targetWeight + 0.001f)
                {
                    best = candidate;
                    low = candidate;
                }
                else
                {
                    rejectedOvershoot++;
                    high = candidate;
                }
            }

            if (best > original + ArmorTolerance)
            {
                if (Math.Abs(stateValue - best) > ArmorTolerance)
                {
                    if (!ProbeArmorValue(best, out _))
                        return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());
                }

                AcceptTarget(best, false);
                if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                    return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, AiOverflowDetails());
            }
            else if (Math.Abs(stateValue - original) > ArmorTolerance)
            {
                if (!ProbeArmorValue(original, out _))
                    return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());
            }

            if (iterations >= maxIterations)
                return new OverflowFillStats(accepted, iterations, "probe-cap", rejectedOvershoot, rejectedByCap, AiOverflowDetails());

            bool ProbeArmorValue(float value, out float weight)
            {
                if (iterations >= maxIterations)
                {
                    weight = ShipWeight(ship);
                    return false;
                }

                SetTargetArmor(ship, target, value);
                RecalculateArmorWeight(ship);
                stateValue = value;
                iterations++;
                weight = ShipWeight(ship);
                return true;
            }

            void AcceptTarget(float value, bool acceptedMax)
            {
                accepted++;
                touched++;
                if (firstTouched == "none")
                    firstTouched = targetToken;
                lastTouched = targetToken;

                if (acceptedMax)
                    maxAccepted++;
                else
                    binaryAccepted++;

                float delta = value - original;
                if (delta > largestDelta)
                {
                    largestDelta = delta;
                    largestJump = $"{targetToken}:{Fmt(original)}->{Fmt(value)}/{Fmt(max)}";
                }
            }
        }

        bool allMaxed = ordered.Count > 0 && ordered.All(target => CurrentArmor(ship, target) >= target.Max - ArmorTolerance);
        return new OverflowFillStats(
            accepted,
            iterations,
            allMaxed ? "all-maxed" : "no-affordable-increment",
            rejectedOvershoot,
            rejectedByCap,
            AiOverflowDetails());

        string AiOverflowDetails()
            => string.Join(
                "|",
                $"targets={ordered.Count}",
                $"touched={touched}",
                $"first={firstTouched}",
                $"last={lastTouched}",
                $"largest={largestJump}",
                $"maxAccept={maxAccepted}",
                $"binaryAccept={binaryAccepted}");
    }

    private static AiLeftoverArmorDumpResult AiLeftoverArmorDumpFill(
        Ship ship,
        MainGunArmorSelection mainGun,
        float capacity,
        float targetWeight)
    {
        int accepted = 0;
        int iterations = 0;
        int rejectedOvershoot = 0;
        int rejectedByCap = 0;
        int maxAccepted = 0;
        int binaryAccepted = 0;
        int touched = 0;
        string firstTouched = "none";
        string lastTouched = "none";
        string largestJump = "none";
        float largestDelta = 0f;
        float threshold = AiLeftoverArmorDumpThreshold(capacity);
        float step = LeftoverArmorStep();

        float unusedBefore = RemainingUnused();
        float unusedAfter = unusedBefore;
        int targetCount = 0;

        if (unusedBefore <= ArmorTargetSafetyMargin)
            return Finish("near-target");

        if (unusedBefore <= threshold)
            return Finish("threshold");

        List<ArmorTarget> ordered = BuildAiLeftoverArmorDumpTargets(ship, mainGun);
        targetCount = ordered.Count;
        if (ordered.Count <= 0)
            return Finish("no-targets");

        foreach (ArmorTarget target in ordered)
        {
            unusedAfter = RemainingUnused();
            if (unusedAfter <= ArmorTargetSafetyMargin)
                return Finish("near-target");

            if (unusedAfter <= threshold)
                return Finish("threshold");

            if (iterations >= MaxAiSmartRefitOverflowFillIterations)
                return Finish("probe-cap");

            float current = CurrentArmor(ship, target);
            float max = SnapLeftoverTargetMax(target, step);
            if (current >= max - ArmorTolerance)
            {
                rejectedByCap++;
                continue;
            }

            string targetToken = ArmorTargetToken(target);
            float original = current;
            float best = current;
            float low = current;
            float high = max;
            float stateValue = current;

            bool ProbeArmorValue(float value, out float weight)
            {
                if (iterations >= MaxAiSmartRefitOverflowFillIterations)
                {
                    weight = ShipWeight(ship);
                    return false;
                }

                SetTargetArmor(ship, target, value);
                RecalculateArmorWeight(ship);
                stateValue = value;
                iterations++;
                weight = ShipWeight(ship);
                unusedAfter = Math.Max(0f, targetWeight - weight);
                return true;
            }

            void RestoreArmorValue(float value)
            {
                SetTargetArmor(ship, target, value);
                RecalculateArmorWeight(ship);
                stateValue = value;
                unusedAfter = RemainingUnused();
            }

            void AcceptTarget(float value, bool acceptedMax)
            {
                accepted++;
                touched++;
                if (firstTouched == "none")
                    firstTouched = targetToken;
                lastTouched = targetToken;

                if (acceptedMax)
                    maxAccepted++;
                else
                    binaryAccepted++;

                float delta = value - original;
                if (delta > largestDelta)
                {
                    largestDelta = delta;
                    largestJump = $"{targetToken}:{Fmt(original)}->{Fmt(value)}/{Fmt(max)}";
                }
            }

            if (!ProbeArmorValue(max, out float maxWeight))
                return Finish("probe-cap");

            if (maxWeight <= targetWeight + 0.001f)
            {
                AcceptTarget(max, true);
                continue;
            }

            rejectedOvershoot++;
            HashSet<string> probed = new(StringComparer.Ordinal);
            probed.Add(ArmorProbeKey(max));
            for (int probe = 0; probe < MaxAiSmartRefitBinaryProbesPerTarget && iterations < MaxAiSmartRefitOverflowFillIterations; probe++)
            {
                float candidate = ClampTargetArmor(target, SnapArmorDownToStep((low + high) * 0.5f, step));
                string key = ArmorProbeKey(candidate);
                if (candidate <= low + ArmorTolerance || candidate >= high - ArmorTolerance || !probed.Add(key))
                    break;

                if (!ProbeArmorValue(candidate, out float candidateWeight))
                {
                    RestoreArmorValue(best > original + ArmorTolerance ? best : original);
                    return Finish("probe-cap");
                }

                if (candidateWeight <= targetWeight + 0.001f)
                {
                    best = candidate;
                    low = candidate;
                }
                else
                {
                    rejectedOvershoot++;
                    high = candidate;
                }
            }

            if (best > original + ArmorTolerance)
            {
                if (Math.Abs(stateValue - best) > ArmorTolerance)
                    RestoreArmorValue(best);

                AcceptTarget(best, false);
            }
            else if (Math.Abs(stateValue - original) > ArmorTolerance)
            {
                RestoreArmorValue(original);
            }
        }

        unusedAfter = RemainingUnused();
        bool allMaxed = ordered.Count > 0 && ordered.All(target => CurrentArmor(ship, target) >= SnapLeftoverTargetMax(target, step) - ArmorTolerance);
        return Finish(allMaxed ? "all-maxed" : "no-affordable-increment");

        float RemainingUnused()
            => Math.Max(0f, targetWeight - ShipWeight(ship));

        string LeftoverDetails()
            => string.Join(
                "|",
                $"targets={targetCount}",
                $"touched={touched}",
                $"first={firstTouched}",
                $"last={lastTouched}",
                $"largest={largestJump}",
                $"maxAccept={maxAccepted}",
                $"binaryAccept={binaryAccepted}",
                $"threshold={Fmt(threshold)}");

        AiLeftoverArmorDumpResult Finish(string stopReason)
            => new(
                new OverflowFillStats(
                    accepted,
                    iterations,
                    stopReason,
                    rejectedOvershoot,
                    rejectedByCap,
                    LeftoverDetails()),
                unusedBefore,
                unusedAfter,
                targetCount);
    }

    private static float AiLeftoverArmorDumpThreshold(float capacity)
        => Math.Max(AiLeftoverArmorDumpThresholdTons, capacity * 0.01f);

    private static float LeftoverArmorStep()
        => InchesToArmor(AiLeftoverArmorDumpStepInches);

    private static float SnapLeftoverTargetMax(ArmorTarget target, float step)
        => ClampTargetArmor(target, SnapArmorDownToStep(ClampTargetArmor(target, target.Max), step));

    private static float SnapArmorDownToStep(float value, float step)
    {
        if (step <= ArmorTolerance || value <= 0f)
            return Math.Max(0f, value);

        return Math.Max(0f, (float)Math.Floor((value + ArmorTolerance) / step) * step);
    }

    private static List<ArmorTarget> BuildAiLeftoverArmorDumpTargets(Ship ship, MainGunArmorSelection mainGun)
    {
        List<ArmorTarget> targets = new();

        AddAiLeftoverHullTarget(ship, targets, Ship.A.Belt, 1);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.ConningTower, 2);

        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            if (entry.PartData == null || entry.Row == null || !IsPrimaryGunArmor(entry, mainGun))
                continue;

            AddAiLeftoverGunRowTarget(ship, targets, entry, Ship.A.TurretSide, 3);
            AddAiLeftoverGunRowTarget(ship, targets, entry, Ship.A.Barbette, 4);
            AddAiLeftoverGunRowTarget(ship, targets, entry, Ship.A.TurretTop, 10);
        }

        AddAiLeftoverHullTarget(ship, targets, Ship.A.BeltBow, 5);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.BeltStern, 5);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.InnerBelt_1st, 6);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.Superstructure, 7);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.InnerBelt_2nd, 8);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.InnerBelt_3rd, 9);
        AddAiLeftoverHullTarget(ship, targets, Ship.A.Deck, 11);

        return targets
            .Where(target => target.Max > CurrentArmor(ship, target) + ArmorTolerance)
            .OrderBy(static target => target.Priority)
            .ThenBy(static target => target.IsGunArmor ? 1 : 0)
            .ThenBy(static target => target.Zone)
            .ToList();
    }

    private static void AddAiLeftoverHullTarget(Ship ship, List<ArmorTarget> targets, Ship.A zone, int priority)
    {
        float min = Safe(() => ship.MinArmorForZone(zone), float.NaN);
        float max = Safe(() => ship.MaxArmorForZone(zone, null), float.NaN);
        AddTarget(targets, zone, false, null, null, 0f, 0f, priority, min, max);
    }

    private static void AddAiLeftoverGunRowTarget(Ship ship, List<ArmorTarget> targets, GunArmorEntry entry, Ship.A zone, int priority)
    {
        if (entry.PartData == null || entry.Row == null)
            return;

        float max = Safe(() => ship.MaxArmorForZone(zone, entry.PartData), float.NaN);
        AddTarget(targets, zone, true, entry.PartData, entry.Row, 0f, 0f, priority, 0f, max);
    }

    private static OverflowFillStats OverflowDoctrineFill(
        Ship ship,
        List<ArmorTarget> ordered,
        float[] increments,
        int maxIterations,
        Stopwatch overflowTimer,
        float targetWeight,
        ArmorFillMode mode)
    {
        int accepted = 0;
        int iterations = 0;
        int rejectedOvershoot = 0;
        int rejectedByCap = 0;

        foreach (ArmorTarget target in ordered)
        {
            while (iterations < maxIterations)
            {
                if (ShipWeight(ship) >= targetWeight - ArmorTargetSafetyMargin)
                    return new OverflowFillStats(accepted, iterations, "near-target", rejectedOvershoot, rejectedByCap, "none");

                if (ShouldStopOverflowForTime(mode, overflowTimer, iterations))
                    return new OverflowFillStats(accepted, iterations, "time-cap", rejectedOvershoot, rejectedByCap, "none");

                float current = CurrentArmor(ship, target);
                if (current >= target.Max - ArmorTolerance)
                {
                    rejectedByCap++;
                    break;
                }

                bool acceptedForTarget = false;
                foreach (float increment in increments)
                {
                    iterations++;
                    if (ShouldStopOverflowForTime(mode, overflowTimer, iterations))
                        return new OverflowFillStats(accepted, iterations, "time-cap", rejectedOvershoot, rejectedByCap, "none");

                    float next = ClampTargetArmor(target, Math.Min(current + increment, target.Max));
                    if (next <= current + ArmorTolerance)
                    {
                        rejectedByCap++;
                        continue;
                    }

                    SetTargetArmor(ship, target, next);
                    RecalculateArmorWeight(ship);
                    if (ShipWeight(ship) > targetWeight + 0.001f)
                    {
                        SetTargetArmor(ship, target, current);
                        RecalculateArmorWeight(ship);
                        rejectedOvershoot++;
                        continue;
                    }

                    accepted++;
                    acceptedForTarget = true;
                    break;
                }

                if (!acceptedForTarget)
                    break;
            }

            if (iterations >= maxIterations)
                return new OverflowFillStats(accepted, iterations, "iteration-cap", rejectedOvershoot, rejectedByCap, "none");
        }

        bool allMaxed = ordered.Count > 0 && ordered.All(target => CurrentArmor(ship, target) >= target.Max - ArmorTolerance);
        return new OverflowFillStats(accepted, iterations, allMaxed ? "all-maxed" : "no-affordable-increment", rejectedOvershoot, rejectedByCap, "none");
    }

    private static int MaxOverflowFillIterationsFor(ArmorFillMode mode)
        => mode == ArmorFillMode.AiSmartRefit
            ? MaxAiSmartRefitOverflowFillIterations
            : MaxOverflowFillIterations;

    private static float[] OverflowIncrementsFor(ArmorFillMode mode)
    {
        if (mode == ArmorFillMode.AiSmartRefit)
        {
            return new[]
            {
                25.4f,
                12.7f,
                2.54f
            };
        }

        float step = ArmorStep();
        return new[]
        {
            Math.Max(step * 6f, 1f),
            Math.Max(step * 3f, 0.5f),
            Math.Max(step, 0.1f),
            Math.Max(step * 0.5f, 0.05f),
            Math.Max(step * 0.25f, 0.01f),
            0.001f
        };
    }

    private static float OverflowMinStepFor(ArmorFillMode mode)
        => OverflowIncrementsFor(mode).Min();

    private static string ArmorProbeKey(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ArmorTargetToken(ArmorTarget target)
    {
        string scope = target.IsGunArmor
            ? Safe(() => target.GunPart?.name ?? "gun", "gun")
            : "hull";
        return LogToken($"{target.Zone}:{scope}:p{target.Priority}");
    }

    private static bool ShouldStopOverflowForTime(ArmorFillMode mode, Stopwatch timer, int iterations)
        => mode == ArmorFillMode.Ui &&
           iterations > 0 &&
           (iterations % OverflowTimeCheckInterval) == 0 &&
           timer.ElapsedMilliseconds >= MaxOverflowFillMilliseconds;

    private static List<ArmorTarget> BuildFinalFillTargets(Ship ship, List<ArmorTarget> profileTargets, MainGunArmorSelection mainGun, float profileB, float capacity, float targetWeight)
    {
        List<ArmorTarget> fillTargets = new();
        foreach (ArmorTarget target in profileTargets)
        {
            float max = target.Max;
            if (target.IsGunArmor && (target.Zone == Ship.A.Barbette || target.Zone == Ship.A.TurretTop))
                max = ClampTargetArmor(target, profileB * target.CapRatio);

            if (max > CurrentArmor(ship, target) + ArmorTolerance)
                fillTargets.Add(target with { Max = max });
        }

        return fillTargets
            .OrderBy(static target => target.Priority)
            .ThenBy(static target => target.IsGunArmor ? 1 : 0)
            .ThenBy(static target => target.Zone)
            .ToList();
    }

    private static List<ArmorTarget> BuildOverflowFillTargets(Ship ship, List<ArmorTarget> profileTargets, MainGunArmorSelection mainGun, float profileB, float capacity, float targetWeight, ArmorFillMode mode)
    {
        if (mode == ArmorFillMode.AiSmartRefit)
            return BuildAiSmartRefitOverflowFillTargets(ship, mainGun, profileB);

        List<ArmorTarget> fillTargets = new();
        HashSet<Ship.A> seenHullZones = new();

        foreach (ArmorTarget target in profileTargets)
        {
            if (target.IsGunArmor)
                continue;

            AddOverflowHullTarget(ship, fillTargets, target.Zone);
            seenHullZones.Add(target.Zone);
        }

        foreach (Ship.A zone in Enum.GetValues(typeof(Ship.A)))
        {
            if (seenHullZones.Contains(zone)
                || zone is Ship.A.Invalid or Ship.A.None or Ship.A.TurretSide or Ship.A.TurretTop or Ship.A.Barbette)
                continue;

            AddOverflowHullTarget(ship, fillTargets, zone);
        }

        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            if (entry.PartData == null || entry.Row == null)
                continue;

            GunArmorBand band = GunArmorBandFor(ship, entry);
            AddOverflowGunRowTarget(ship, fillTargets, entry, Ship.A.TurretSide, band.SideCap, band.SidePriority);
            AddOverflowGunRowTarget(ship, fillTargets, entry, Ship.A.Barbette, band.BarbetteCap, band.BarbettePriority);
            AddOverflowGunRowTarget(ship, fillTargets, entry, Ship.A.TurretTop, band.TopCap, band.TopPriority);
        }

        return fillTargets
            .Where(target => target.Max > CurrentArmor(ship, target) + ArmorTolerance)
            .OrderBy(target => OverflowPriority(target))
            .ThenBy(static target => target.IsGunArmor ? 1 : 0)
            .ThenBy(static target => target.Zone)
            .ToList();
    }

    private static List<ArmorTarget> BuildAiSmartRefitOverflowFillTargets(Ship ship, MainGunArmorSelection mainGun, float profileB)
    {
        List<ArmorTarget> targets = new();

        AddAiOverflowHullTarget(ship, targets, Ship.A.Belt, profileB * 1.35f, 1);
        AddAiOverflowHullTarget(ship, targets, Ship.A.ConningTower, profileB * 1.5f, 2);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerBelt_1st, profileB * 0.9f, 4);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerBelt_2nd, profileB * 0.75f, 5);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerBelt_3rd, profileB * 0.6f, 6);

        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            if (entry.PartData == null || entry.Row == null)
                continue;

            AddAiOverflowGunRowTarget(ship, targets, entry, Ship.A.TurretSide);
            AddAiOverflowGunRowTarget(ship, targets, entry, Ship.A.TurretTop);
            AddAiOverflowGunRowTarget(ship, targets, entry, Ship.A.Barbette);
        }

        AddAiOverflowHullTarget(ship, targets, Ship.A.BeltBow, profileB * 0.65f, 20);
        AddAiOverflowHullTarget(ship, targets, Ship.A.BeltStern, profileB * 0.65f, 20);
        AddAiOverflowHullTarget(ship, targets, Ship.A.Deck, profileB * 0.22f, 40);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerDeck_1st, profileB * 0.16f, 45);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerDeck_2nd, profileB * 0.14f, 46);
        AddAiOverflowHullTarget(ship, targets, Ship.A.InnerDeck_3rd, profileB * 0.12f, 47);
        AddAiOverflowHullTarget(ship, targets, Ship.A.DeckBow, profileB * 0.1f, 55);
        AddAiOverflowHullTarget(ship, targets, Ship.A.DeckStern, profileB * 0.1f, 55);
        AddAiOverflowHullTarget(
            ship,
            targets,
            Ship.A.Superstructure,
            Math.Min(InchesToArmor(2f), Math.Max(InchesToArmor(1f), profileB * 0.12f)),
            60);

        return targets
            .Where(target => target.Max > CurrentArmor(ship, target) + ArmorTolerance)
            .OrderBy(static target => target.Priority)
            .ThenBy(static target => target.IsGunArmor ? 1 : 0)
            .ThenBy(static target => target.Zone)
            .ToList();
    }

    private static void AddAiOverflowHullTarget(Ship ship, List<ArmorTarget> targets, Ship.A zone, float doctrineCap, int priority)
    {
        float min = Safe(() => ship.MinArmorForZone(zone), float.NaN);
        float legalMax = Safe(() => ship.MaxArmorForZone(zone, null), float.NaN);
        if (float.IsNaN(min) || float.IsNaN(legalMax))
            return;

        if (legalMax < min)
            (min, legalMax) = (legalMax, min);

        float max = Math.Clamp(Math.Max(min, doctrineCap), min, legalMax);
        AddTarget(targets, zone, false, null, null, 0f, 0f, priority, min, max);
    }

    private static void AddAiOverflowGunRowTarget(Ship ship, List<ArmorTarget> targets, GunArmorEntry entry, Ship.A zone)
    {
        if (entry.PartData == null || entry.Row == null)
            return;

        float cap = AiSmartRefitGunArmorCap(entry, zone);
        if (cap <= ArmorTolerance)
            return;

        float legalMax = Safe(() => ship.MaxArmorForZone(zone, entry.PartData), cap);
        if (float.IsNaN(legalMax) || legalMax < 0f)
            legalMax = cap;

        int priority = AiSmartRefitGunArmorPriority(entry, zone);
        AddTarget(targets, zone, true, entry.PartData, entry.Row, 0f, 0f, priority, 0f, Math.Min(cap, legalMax));
    }

    private static float AiSmartRefitGunArmorCap(GunArmorEntry entry, Ship.A zone)
    {
        float caliber = Math.Max(0f, entry.Caliber);
        bool turret = !entry.IsCasemate;
        if (caliber >= 9f && turret)
        {
            return zone switch
            {
                Ship.A.TurretSide => InchesToArmor(Math.Min(caliber * 1.2f, caliber + 4f)),
                Ship.A.TurretTop => InchesToArmor(Math.Max(3f, caliber * 0.45f)),
                Ship.A.Barbette => InchesToArmor(Math.Max(4f, caliber * 0.6f)),
                _ => 0f
            };
        }

        if (caliber >= 6f)
        {
            return zone switch
            {
                Ship.A.TurretSide => InchesToArmor(turret ? 5f : 3f),
                Ship.A.TurretTop => InchesToArmor(turret ? 2.5f : 1.5f),
                Ship.A.Barbette => turret ? InchesToArmor(2.5f) : 0f,
                _ => 0f
            };
        }

        return zone switch
        {
            Ship.A.TurretSide => InchesToArmor(1.5f),
            Ship.A.TurretTop => InchesToArmor(1f),
            Ship.A.Barbette => turret ? InchesToArmor(1f) : 0f,
            _ => 0f
        };
    }

    private static int AiSmartRefitGunArmorPriority(GunArmorEntry entry, Ship.A zone)
    {
        bool largeTurret = !entry.IsCasemate && entry.Caliber >= 9f;
        if (largeTurret)
        {
            return zone switch
            {
                Ship.A.TurretSide => 3,
                Ship.A.TurretTop => 12,
                Ship.A.Barbette => 13,
                _ => 50
            };
        }

        if (entry.Caliber >= 6f)
        {
            return zone switch
            {
                Ship.A.TurretSide => 18,
                Ship.A.TurretTop => 24,
                Ship.A.Barbette => 25,
                _ => 50
            };
        }

        return zone switch
        {
            Ship.A.TurretSide => 50,
            Ship.A.TurretTop => 52,
            Ship.A.Barbette => 54,
            _ => 70
        };
    }

    private static void AddOverflowHullTarget(Ship ship, List<ArmorTarget> targets, Ship.A zone)
    {
        float min = Safe(() => ship.MinArmorForZone(zone), float.NaN);
        float max = Safe(() => ship.MaxArmorForZone(zone, null), float.NaN);
        AddTarget(targets, zone, false, null, null, 0f, 0f, OverflowPriority(zone, false), min, max);
    }

    private static void AddOverflowGunTarget(Ship ship, List<ArmorTarget> targets, PartData gun, Ship.A zone)
    {
        float max = Safe(() => ship.MaxArmorForZone(zone, gun), float.NaN);
        AddTarget(targets, zone, true, gun, null, 0f, 0f, OverflowPriority(zone, true), 0f, max);
    }

    private static void AddOverflowGunRowTarget(Ship ship, List<ArmorTarget> targets, GunArmorEntry entry, Ship.A zone, float cap, int priority)
    {
        if (entry.PartData == null || entry.Row == null || cap <= ArmorTolerance)
            return;

        float max = Safe(() => ship.MaxArmorForZone(zone, entry.PartData), cap);
        if (float.IsNaN(max) || max < 0f)
            max = cap;

        AddTarget(targets, zone, true, entry.PartData, entry.Row, 0f, 0f, priority, 0f, Math.Min(cap, max));
    }

    private static int OverflowPriority(ArmorTarget target)
        => OverflowPriority(target.Zone, target.IsGunArmor);

    private static int OverflowPriority(Ship.A zone, bool isGunArmor)
    {
        if (isGunArmor)
        {
            return zone switch
            {
                Ship.A.TurretSide => 1,
                Ship.A.TurretTop => 5,
                Ship.A.Barbette => 6,
                _ => 25
            };
        }

        return zone switch
        {
            Ship.A.Belt => 2,
            Ship.A.ConningTower => 3,
            Ship.A.BeltBow or Ship.A.BeltStern => 4,
            Ship.A.Deck => 8,
            Ship.A.DeckBow or Ship.A.DeckStern => 11,
            Ship.A.InnerDeck_1st or Ship.A.InnerBelt_1st or Ship.A.InnerDeck_2nd or Ship.A.InnerBelt_2nd or Ship.A.InnerDeck_3rd or Ship.A.InnerBelt_3rd => 9,
            Ship.A.Superstructure => 15,
            _ => 20
        };
    }

    private static void AddFinalSecondaryTarget(List<ArmorTarget> targets, PartData gunPart, Ship.A zone, float max, int priority)
    {
        if (max <= ArmorTolerance)
            return;

        targets.Add(new ArmorTarget(zone, true, gunPart, null, 0f, 0f, priority, 0f, max));
    }

    private static bool IsSecondaryGunArmor(GunArmorEntry entry, MainGunArmorSelection mainGun)
        => !IsPrimaryGunArmor(entry, mainGun);

    private static bool IsPrimaryGunArmor(GunArmorEntry entry, MainGunArmorSelection mainGun)
    {
        return mainGun.HasPrimary
            && !entry.IsCasemate
            && entry.Caliber >= mainGun.Caliber - 0.05f;
    }

    private static float SecondaryGunSideArmorCap(float profileB, float capacity, float remainingWeight)
    {
        float comfortableSpare = Math.Max(350f, capacity * SecondaryGunComfortableSpareRatio);
        float roomySpare = Math.Max(750f, capacity * SecondaryGunRoomySpareRatio);

        if (profileB >= 10f && remainingWeight >= roomySpare)
            return 3f;

        if (profileB >= 8f && remainingWeight >= comfortableSpare)
            return 2f;

        return 1f;
    }

    private static float LegalGunArmorCap(Ship ship, PartData partData, Ship.A zone, float cap)
    {
        float max = Safe(() => ship.MaxArmorForZone(zone, partData), cap);
        if (float.IsNaN(max) || max < 0f)
            max = cap;

        return ClampRawArmor(Math.Clamp(cap, 0f, Math.Max(0f, max)), 0f, Math.Max(0f, max));
    }

    private static List<GunArmorEntry> GunArmorEntries(Ship ship)
    {
        List<GunArmorEntry> entries = new();
        try
        {
            if (ship.shipTurretArmor == null)
            {
                WarnMissingGunArmorRows(ship, "null");
                return entries;
            }

            foreach (Ship.TurretArmor row in ship.shipTurretArmor)
            {
                if (row == null)
                    continue;

                PartData? partData = Safe(() => row.turretPartData, null);
                if (partData == null)
                    continue;

                entries.Add(new GunArmorEntry(
                    row,
                    partData,
                    Safe(() => row.isCasemateGun, false),
                    GunCaliber(ship, partData),
                    Safe(() => row.sideTurretArmor, 0f),
                    Safe(() => row.topTurretArmor, 0f),
                    Safe(() => row.barbetteArmor, 0f)));
            }

            if (entries.Count <= 0)
                WarnMissingGunArmorRows(ship, "empty");
        }
        catch
        {
        }

        return entries;
    }

    private static void WarnMissingGunArmorRows(Ship ship, string reason)
    {
        if (loggedMissingGunArmorRows || !ShipHasGunParts(ship))
            return;

        loggedMissingGunArmorRows = true;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: ship has gun parts but no readable shipTurretArmor rows ({reason}); gun armor bands skipped.");
    }

    private static bool ShipHasGunParts(Ship ship)
    {
        try
        {
            if (ship.parts == null)
                return false;

            foreach (Part part in ship.parts)
            {
                if (Safe(() => part?.data?.isGun ?? false, false))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static float GunCaliber(Ship ship, PartData partData)
        => Safe(() => partData.GetCaliberInch(ship), Safe(() => partData.caliber, 0f));

    private static void TrySetGunArmor(Ship ship, PartData partData, Ship.A zone, float value)
    {
        try { ship.SetGunArmor(partData, zone, value); }
        catch { }
    }

    private static void TrySetGunArmorRow(Ship.TurretArmor row, Ship.A zone, float value)
    {
        try
        {
            if (zone == Ship.A.TurretSide)
                row.sideTurretArmor = value;
            else if (zone == Ship.A.TurretTop)
                row.topTurretArmor = value;
            else if (zone == Ship.A.Barbette)
                row.barbetteArmor = value;
        }
        catch
        {
        }
    }

    private static float CurrentGunArmorRow(Ship.TurretArmor row, Ship.A zone)
    {
        try
        {
            if (zone == Ship.A.TurretSide)
                return row.sideTurretArmor;
            if (zone == Ship.A.TurretTop)
                return row.topTurretArmor;
            if (zone == Ship.A.Barbette)
                return row.barbetteArmor;
        }
        catch
        {
        }

        return 0f;
    }

    private static float ClampTargetArmor(ArmorTarget target, float value)
        => ClampRawArmor(value, target.Min, target.Max);

    private static float ClampRawArmor(float value, float min, float max)
    {
        if (max < min)
            (min, max) = (max, min);

        float clamped = max > min ? Math.Clamp(value, min, max) : Math.Max(value, min);
        float rounded = Safe(() => G.settings.RoundToArmorStep(clamped), clamped);
        return max > min ? Math.Clamp(rounded, min, max) : Math.Max(rounded, min);
    }

    private static void SetTargetArmor(Ship ship, ArmorTarget target, float value)
    {
        try
        {
            if (target.IsGunArmor && target.GunArmorRow != null)
                TrySetGunArmorRow(target.GunArmorRow, target.Zone, value);
            else if (target.IsGunArmor && target.GunPart != null)
                ship.SetGunArmor(target.GunPart, target.Zone, value);
            else
                ship.SetArmor(target.Zone, value, false);
        }
        catch
        {
        }
    }

    private static float CurrentArmor(Ship ship, ArmorTarget target)
    {
        if (target.IsGunArmor && target.GunArmorRow != null)
            return CurrentGunArmorRow(target.GunArmorRow, target.Zone);
        if (target.IsGunArmor && target.GunPart != null)
            return CurrentGunArmor(ship, target.GunPart, target.Zone);

        return CurrentHullArmor(ship, target.Zone);
    }

    private static float CurrentHullArmor(Ship ship, Ship.A zone)
    {
        try
        {
            if (ship.armor != null && ship.armor.TryGetValue(zone, out float value))
                return value;
        }
        catch
        {
        }

        return 0f;
    }

    private static float CurrentGunArmor(Ship ship, PartData gun, Ship.A zone)
    {
        try
        {
            object? armor = GetGunArmorMethod?.Invoke(ship, new object?[] { null, gun, null, false });
            if (armor == null)
                return 0f;

            string field = zone == Ship.A.TurretSide
                ? "sideTurretArmor"
                : zone == Ship.A.TurretTop
                    ? "topTurretArmor"
                    : "barbetteArmor";
            object? value = armor.GetType().GetField(field)?.GetValue(armor);
            return value is float f ? f : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private static ArmorSnapshot CaptureArmor(Ship ship)
    {
        Dictionary<Ship.A, float> hull = new();
        try
        {
            if (ship.armor != null)
            {
                foreach (var pair in ship.armor)
                    hull[pair.Key] = pair.Value;
            }
        }
        catch
        {
        }

        List<GunArmorSnapshot> guns = new();
        try
        {
            if (ship.shipTurretArmor != null)
            {
                foreach (Ship.TurretArmor row in ship.shipTurretArmor)
                {
                    PartData? part = Safe(() => row.turretPartData, null);
                    if (part == null)
                        continue;

                    guns.Add(new GunArmorSnapshot(
                        row,
                        part,
                        Safe(() => row.sideTurretArmor, 0f),
                        Safe(() => row.topTurretArmor, 0f),
                        Safe(() => row.barbetteArmor, 0f)));
                }
            }
        }
        catch
        {
        }

        return new ArmorSnapshot(hull, guns);
    }

    private static void RestoreArmor(Ship ship, ArmorSnapshot snapshot)
    {
        foreach (var pair in snapshot.HullArmor)
        {
            try { ship.SetArmor(pair.Key, pair.Value, false); }
            catch { }
        }

        foreach (GunArmorSnapshot gun in snapshot.GunArmor)
        {
            if (gun.Row != null)
            {
                TrySetGunArmorRow(gun.Row, Ship.A.TurretSide, gun.Side);
                TrySetGunArmorRow(gun.Row, Ship.A.TurretTop, gun.Top);
                TrySetGunArmorRow(gun.Row, Ship.A.Barbette, gun.Barbette);
                continue;
            }

            TrySetGunArmor(ship, gun.PartData, Ship.A.TurretSide, gun.Side);
            TrySetGunArmor(ship, gun.PartData, Ship.A.TurretTop, gun.Top);
            TrySetGunArmor(ship, gun.PartData, Ship.A.Barbette, gun.Barbette);
        }

        RecalculateArmorWeight(ship);
    }

    private static string ArmorTableLog(Ship ship, MainGunArmorSelection mainGun)
    {
        GunArmorExtremes largeGun = GunArmorExtremesFor(ship, static entry => entry.Caliber >= 10f);
        SecondaryGunArmorLog secondary = SecondaryGunArmorTableLog(ship, mainGun);
        return string.Join(",",
            $"belt:{Fmt(CurrentHullArmor(ship, Ship.A.Belt))}",
            $"foreBelt:{Fmt(CurrentHullArmor(ship, Ship.A.BeltBow))}",
            $"aftBelt:{Fmt(CurrentHullArmor(ship, Ship.A.BeltStern))}",
            $"deck:{Fmt(CurrentHullArmor(ship, Ship.A.Deck))}",
            $"foreDeck:{Fmt(CurrentHullArmor(ship, Ship.A.DeckBow))}",
            $"aftDeck:{Fmt(CurrentHullArmor(ship, Ship.A.DeckStern))}",
            $"conning:{Fmt(CurrentHullArmor(ship, Ship.A.ConningTower))}",
            $"super:{Fmt(CurrentHullArmor(ship, Ship.A.Superstructure))}",
            $"innerBelt1:{Fmt(CurrentHullArmor(ship, Ship.A.InnerBelt_1st))}",
            $"innerDeck1:{Fmt(CurrentHullArmor(ship, Ship.A.InnerDeck_1st))}",
            $"innerBelt2:{Fmt(CurrentHullArmor(ship, Ship.A.InnerBelt_2nd))}",
            $"innerDeck2:{Fmt(CurrentHullArmor(ship, Ship.A.InnerDeck_2nd))}",
            $"innerBelt3:{Fmt(CurrentHullArmor(ship, Ship.A.InnerBelt_3rd))}",
            $"innerDeck3:{Fmt(CurrentHullArmor(ship, Ship.A.InnerDeck_3rd))}",
            $"largeGunRows:{largeGun.Count}",
            $"largeGunMaxSide:{Fmt(largeGun.MaxSide)}",
            $"largeGunMaxTop:{Fmt(largeGun.MaxTop)}",
            $"largeGunMaxBarbette:{Fmt(largeGun.MaxBarbette)}",
            $"smallMediumGunRows:{secondary.Count}",
            $"smallMediumMaxSide:{Fmt(secondary.MaxSide)}",
            $"smallMediumMaxTop:{Fmt(secondary.MaxTop)}",
            $"smallMediumMaxBarbette:{Fmt(secondary.MaxBarbette)}",
            $"gunRows:{GunClassificationLog(ship, mainGun)}");
    }

    private static GunArmorExtremes GunArmorExtremesFor(Ship ship, Func<GunArmorEntry, bool> predicate)
    {
        int count = 0;
        float maxSide = 0f;
        float maxTop = 0f;
        float maxBarbette = 0f;
        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            if (!predicate(entry))
                continue;

            count++;
            maxSide = Math.Max(maxSide, entry.Side);
            maxTop = Math.Max(maxTop, entry.Top);
            maxBarbette = Math.Max(maxBarbette, entry.Barbette);
        }

        return new GunArmorExtremes(count, maxSide, maxTop, maxBarbette);
    }

    private static SecondaryGunArmorLog SecondaryGunArmorTableLog(Ship ship, MainGunArmorSelection mainGun)
    {
        int count = 0;
        float maxSide = 0f;
        float maxTop = 0f;
        float maxBarbette = 0f;

        foreach (GunArmorEntry entry in GunArmorEntries(ship))
        {
            if (entry.Caliber >= 10f)
                continue;

            count++;
            maxSide = Math.Max(maxSide, entry.Side);
            maxTop = Math.Max(maxTop, entry.Top);
            maxBarbette = Math.Max(maxBarbette, entry.Barbette);
        }

        return new SecondaryGunArmorLog(count, maxSide, maxTop, maxBarbette);
    }

    private static string GunClassificationLog(Ship ship, MainGunArmorSelection mainGun)
    {
        List<string> rows = new();
        foreach (GunArmorEntry entry in GunArmorEntries(ship).OrderByDescending(static entry => entry.Caliber).Take(10))
        {
            string classification = GunArmorBandLabel(entry);
            rows.Add(
                $"{Fmt(entry.Caliber)}in:{classification}:{LogToken(entry.PartData?.name ?? "unknown")}:{Fmt(entry.Side)}/{Fmt(entry.Top)}/{Fmt(entry.Barbette)}");
        }

        return rows.Count > 0 ? string.Join("|", rows) : "none";
    }

    private static string GunArmorBandLabel(GunArmorEntry entry)
    {
        string mount = entry.IsCasemate ? "casemate" : "turret";
        if (entry.Caliber <= 4.05f)
            return $"{mount}-small";
        if (entry.Caliber <= 6.05f)
            return $"{mount}-medium";
        if (entry.Caliber < 10f)
            return $"{mount}-large";

        return $"{mount}-capital";
    }

    private static void RecalculateArmorWeight(Ship ship)
    {
        activeArmorPerfStats?.RecordRecalculate();

        try { RefreshHullStatsMethod?.Invoke(ship, Array.Empty<object>()); }
        catch (Exception ex) { WarnRefreshOnce("RefreshHullStats", ex); }

        try { RefreshGunsStatsMethod?.Invoke(ship, Array.Empty<object>()); }
        catch (Exception ex) { WarnRefreshOnce("RefreshGunsStats", ex); }

        try { ship.CalcWeightAndCost(true, true); }
        catch { }
    }

    private static float ShipWeight(Ship ship)
    {
        activeArmorPerfStats?.RecordShipWeight();
        return Safe(() => ship.Weight(true, true), Safe(() => ship.Weight(), 0f));
    }

    private static float ArmorStep()
    {
        try
        {
            float previous = 0f;
            for (float probe = 0.01f; probe <= 2f; probe += 0.01f)
            {
                float rounded = G.settings.RoundToArmorStep(probe);
                if (rounded > previous + ArmorTolerance)
                    return Math.Clamp(rounded - previous, 0.05f, 1f);

                previous = rounded;
            }
        }
        catch
        {
        }

        return 0.1f;
    }

    private static MainGunArmorSelection SelectMainGunArmor(Ship ship)
    {
        List<GunArmorEntry> entries = GunArmorEntries(ship);
        if (entries.Count <= 0)
            return new MainGunArmorSelection(0f, new List<PartData>(), 0, 0);

        float mainCaliber = 0f;
        foreach (GunArmorEntry entry in entries)
        {
            if (!entry.IsCasemate)
                mainCaliber = Math.Max(mainCaliber, entry.Caliber);
        }

        if (mainCaliber <= ArmorTolerance)
        {
            foreach (GunArmorEntry entry in entries)
                mainCaliber = Math.Max(mainCaliber, entry.Caliber);

            return new MainGunArmorSelection(mainCaliber, new List<PartData>(), entries.Count, entries.Count(static entry => entry.IsCasemate));
        }

        Dictionary<string, PartData> primaryParts = new(StringComparer.Ordinal);
        foreach (GunArmorEntry entry in entries)
        {
            if (entry.PartData == null
                || entry.IsCasemate
                || entry.Caliber < mainCaliber - 0.05f)
                continue;

            string key = $"{entry.PartData.name}|{entry.Caliber.ToString("0.###", CultureInfo.InvariantCulture)}";
            primaryParts.TryAdd(key, entry.PartData);
        }

        return new MainGunArmorSelection(mainCaliber, primaryParts.Values.ToList(), entries.Count, entries.Count(static entry => entry.IsCasemate));
    }

    private static bool IsConstructor()
        => Safe(() => GameManager.IsConstructor, false);

    private static bool IsHumanMainDesignerShip(Ship ship)
    {
        Player? owner = Safe(() => ship.player, null) ?? PlayerController.Instance;
        return owner != null && Safe(() => owner.isMain && !owner.isAi, false);
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

    private static void RefreshConstructorUi(Ship ship)
    {
        Ui? ui = Safe(() => G.ui, null);
        if (ui == null)
            return;

        try { RefreshHullStatsMethod?.Invoke(ship, Array.Empty<object>()); }
        catch (Exception ex) { WarnRefreshOnce("RefreshHullStats", ex); }

        try { OnConShipChangedMethod?.Invoke(ui, new object[] { false }); }
        catch (Exception ex) { WarnRefreshOnce("OnConShipChanged", ex); }

        try { ui.RefreshConstructorInfo(); }
        catch (Exception ex) { WarnRefreshOnce("RefreshConstructorInfo", ex); }

        try { RefreshPartsMethod?.Invoke(ui, Array.Empty<object>()); }
        catch (Exception ex) { WarnRefreshOnce("RefreshParts", ex); }
    }

    private static void WarnRefreshOnce(string phase, Exception ex)
    {
        if (loggedRefreshWarning)
            return;

        loggedRefreshWarning = true;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: designer UI refresh failed at {phase}. {ex.GetType().Name}: {ex.Message}");
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
        RemoveTooltipHandlers(target);

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

    private static void RemoveTooltipHandlers(GameObject target)
    {
        RemoveComponent<OnEnter>(target);
        RemoveComponent<OnLeave>(target);
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<none>"
            : value.Replace(' ', '_').Replace('"', '\'');

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private sealed class ArmorPerfStats
    {
        private const double SlowLogThresholdMs = 250d;

        private long startTimestamp;
        private long endTimestamp;
        private long resetTicks;
        private long profileSearchTicks;
        private long applyBestProfileTicks;
        private long clampTicks;
        private long fineFillTicks;
        private long finalFillTicks;
        private long overflowFillTicks;
        private long finalWeightTicks;
        private long finalLogTicks;

        internal int RecalcCalls { get; private set; }
        internal int WeightCalls { get; private set; }
        internal int ProfileSearchIterations { get; private set; }
        private int fineFillIterations;
        private int fineFillAccepted;
        private int finalFillIterations;
        private int finalFillAccepted;
        private string finalFillStop = "not-run";
        private int overflowFillIterations;
        private int overflowFillAccepted;
        private string overflowFillStop = "not-run";
        private int overflowRejectedOvershoot;
        private int overflowRejectedByCap;

        internal string ElapsedMsText => FormatMs(ElapsedTicks);

        private long ElapsedTicks
        {
            get
            {
                long end = endTimestamp > 0 ? endTimestamp : Stopwatch.GetTimestamp();
                return startTimestamp > 0 ? Math.Max(0L, end - startTimestamp) : 0L;
            }
        }

        internal void Start()
        {
            startTimestamp = Stopwatch.GetTimestamp();
        }

        internal void Complete()
        {
            endTimestamp = Stopwatch.GetTimestamp();
        }

        internal void RecordRecalculate()
        {
            RecalcCalls++;
        }

        internal void RecordShipWeight()
        {
            WeightCalls++;
        }

        internal void RecordProfileSearchIteration()
        {
            ProfileSearchIterations++;
        }

        internal void AddReset(long start)
        {
            resetTicks += ElapsedSince(start);
        }

        internal void AddProfileSearch(long start)
        {
            profileSearchTicks += ElapsedSince(start);
        }

        internal void AddApplyBestProfile(long start)
        {
            applyBestProfileTicks += ElapsedSince(start);
        }

        internal void AddClamp(long start)
        {
            clampTicks += ElapsedSince(start);
        }

        internal void AddFineFill(long start, FineFillStats stats)
        {
            fineFillTicks += ElapsedSince(start);
            fineFillIterations = stats.Iterations;
            fineFillAccepted = stats.Accepted;
        }

        internal void AddFinalFill(long start, FinalFillStats stats)
        {
            finalFillTicks += ElapsedSince(start);
            finalFillIterations = stats.Iterations;
            finalFillAccepted = stats.Accepted;
            finalFillStop = stats.StopReason;
        }

        internal void AddOverflowFill(long start, OverflowFillStats stats)
        {
            overflowFillTicks += ElapsedSince(start);
            overflowFillIterations = stats.Iterations;
            overflowFillAccepted = stats.Accepted;
            overflowFillStop = stats.StopReason;
            overflowRejectedOvershoot = stats.RejectedOvershoot;
            overflowRejectedByCap = stats.RejectedByCap;
        }

        internal void AddFinalWeight(long start)
        {
            finalWeightTicks += ElapsedSince(start);
        }

        internal void AddFinalLog(long start)
        {
            finalLogTicks += ElapsedSince(start);
        }

        internal void LogIfSlow(ArmorFitResult result, MainGunArmorSelection mainGun)
        {
            if (ElapsedTicks * 1000d / Stopwatch.Frequency < SlowLogThresholdMs)
                return;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix} perf: elapsedMs={ElapsedMsText} recalcCalls={RecalcCalls} weightCalls={WeightCalls} " +
                $"resetMs={FormatMs(resetTicks)} profileMs={FormatMs(profileSearchTicks)} applyProfileMs={FormatMs(applyBestProfileTicks)} clampMs={FormatMs(clampTicks)} " +
                $"fineMs={FormatMs(fineFillTicks)} finalMs={FormatMs(finalFillTicks)} overflowMs={FormatMs(overflowFillTicks)} finalWeightMs={FormatMs(finalWeightTicks)} logMs={FormatMs(finalLogTicks)} " +
                $"profileIterations={ProfileSearchIterations} fineFill={fineFillAccepted}/{fineFillIterations} finalFill={finalFillAccepted}/{finalFillIterations} finalStop={LogToken(finalFillStop)} " +
                $"overflow={overflowFillAccepted}/{overflowFillIterations} overflowStop={LogToken(overflowFillStop)} overflowRejectedOvershoot={overflowRejectedOvershoot} overflowRejectedByCap={overflowRejectedByCap} " +
                $"unusedWeight={Fmt(result.UnusedWeight)} targets={result.Targets} gunRows={mainGun.EntryCount}.");
        }

        private static long ElapsedSince(long start)
            => Math.Max(0L, Stopwatch.GetTimestamp() - start);

        private static string FormatMs(long ticks)
            => (ticks * 1000d / Stopwatch.Frequency).ToString("0.#", CultureInfo.InvariantCulture);
    }

    private readonly record struct ProfileBound(float High, string Source);

    private readonly record struct ArmorFitResult(
        string Result,
        float ProfileB,
        float ProfileHighBound,
        string ProfileBoundSource,
        string CappedProfileZones,
        float Capacity,
        float BaselineWeight,
        float TargetWeight,
        float FinalWeight,
        float Budget,
        float Spent,
        float UnusedWeight,
        int Targets,
        int FineFillAccepted,
        int FineFillIterations,
        int FinalFillAccepted,
        int FinalFillIterations,
        string FinalFillStopReason,
        float SmallestRejectedIncrement,
        int OverflowFillAccepted,
        int OverflowFillIterations,
        string OverflowFillStopReason,
        int OverflowRejectedOvershoot,
        int OverflowRejectedByCap,
        string OverflowDetails,
        int LeftoverFillAccepted,
        int LeftoverFillIterations,
        string LeftoverFillStopReason,
        float LeftoverUnusedBefore,
        float LeftoverUnusedAfter,
        int LeftoverTargets,
        string LeftoverDetails,
        int SecondaryGunArmorChecked,
        int SecondaryGunArmorClamped,
        float SecondaryGunSideCap,
        float SecondaryGunTopCap);

    private readonly record struct FineFillStats(
        int Accepted,
        int Iterations);

    private readonly record struct FinalFillStats(
        int Accepted,
        int Iterations,
        string StopReason,
        float SmallestRejectedIncrement);

    private readonly record struct OverflowFillStats(
        int Accepted,
        int Iterations,
        string StopReason,
        int RejectedOvershoot,
        int RejectedByCap,
        string Details);

    private readonly record struct AiLeftoverArmorDumpResult(
        OverflowFillStats Stats,
        float UnusedBefore,
        float UnusedAfter,
        int Targets);

    private readonly record struct MainGunArmorSelection(
        float Caliber,
        List<PartData> PrimaryParts,
        int EntryCount,
        int CasemateCount)
    {
        public bool HasPrimary => PrimaryParts.Count > 0;
        public string LogName => HasPrimary
            ? string.Join("+", PrimaryParts.Select(static part => part.name))
            : "none";
    }

    private readonly record struct SecondaryGunArmorStats(
        int Checked,
        int Clamped,
        float SideCap,
        float TopCap);

    private readonly record struct GunArmorEntry(
        Ship.TurretArmor Row,
        PartData PartData,
        bool IsCasemate,
        float Caliber,
        float Side,
        float Top,
        float Barbette);

    private readonly record struct GunArmorBand(
        float SideFloor,
        float TopFloor,
        float BarbetteFloor,
        float SideCap,
        float TopCap,
        float BarbetteCap,
        int SidePriority,
        int TopPriority,
        int BarbettePriority);

    private readonly record struct GunArmorExtremes(
        int Count,
        float MaxSide,
        float MaxTop,
        float MaxBarbette);

    private readonly record struct SecondaryGunArmorLog(
        int Count,
        float MaxSide,
        float MaxTop,
        float MaxBarbette);

    private readonly record struct ArmorTarget(
        Ship.A Zone,
        bool IsGunArmor,
        PartData? GunPart,
        Ship.TurretArmor? GunArmorRow,
        float Ratio,
        float CapRatio,
        int Priority,
        float Min,
        float Max);

    private readonly record struct ArmorSnapshot(
        Dictionary<Ship.A, float> HullArmor,
        List<GunArmorSnapshot> GunArmor);

    private readonly record struct GunArmorSnapshot(
        Ship.TurretArmor? Row,
        PartData PartData,
        float Side,
        float Top,
        float Barbette);
}

[HarmonyPatch]
internal static class DesignAutoArmorConstructorUiPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool found = DesignAutoArmorPatch.ConstructorUiTarget() != null;
        if (!found)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP designer auto armor: Ui.ConstructorUI target not found; button disabled.");
        return found;
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod()
        => DesignAutoArmorPatch.ConstructorUiTarget();

    [HarmonyPostfix]
    private static void Postfix(Ui __instance)
        => DesignAutoArmorPatch.EnsureButton(__instance);
}

[HarmonyPatch]
internal static class DesignAutoArmorRefreshConstructorInfoPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
        => DesignAutoArmorPatch.RefreshConstructorInfoTarget() != null;

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod()
        => DesignAutoArmorPatch.RefreshConstructorInfoTarget();

    [HarmonyPostfix]
    private static void Postfix(Ui __instance)
        => DesignAutoArmorPatch.EnsureButton(__instance);
}
