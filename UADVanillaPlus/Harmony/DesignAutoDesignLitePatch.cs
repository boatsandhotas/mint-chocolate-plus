using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Adds a parts-only Auto Design action for the constructor. Unlike vanilla
// Auto Design, this deliberately avoids Ui.RandomShip/Ship.GenerateRandomShip
// so the player's current hull specs remain the source of truth.
[HarmonyPatch]
internal static class DesignAutoDesignLitePatch
{
    private const string LogPrefix = "UADVP auto design lite";
    private const string ButtonName = "UADVP_AutoDesignLite";
    private const float KnotsToMetersPerSecond = 0.514444444f;
    private const int MaxSecondaryGroupsRemoved = 4;

    private static readonly MethodInfo? AddRandomPartsNewMethod = ResolveAddRandomPartsNew();
    private static readonly MethodInfo? RefreshHullStatsMethod =
        AccessTools.Method(typeof(Ship), "RefreshHullStats", Type.EmptyTypes);
    private static readonly MethodInfo? OnConShipChangedMethod =
        AccessTools.Method(typeof(Ui), "OnConShipChanged", new[] { typeof(bool) });
    private static readonly MethodInfo? RefreshPartsMethod =
        AccessTools.Method(typeof(Ui), "RefreshParts", Type.EmptyTypes);

    private static bool loggedAttached;
    private static bool loggedMissingButton;
    private static bool loggedMissingPartsMethod;
    private static bool loggedRefreshWarning;
    private static bool runningAutoLite;

    internal static MethodBase? ConstructorUiTarget()
        => AccessTools.Method(typeof(Ui), nameof(Ui.ConstructorUI), Type.EmptyTypes);

    internal static void EnsureButton(Ui? ui)
    {
        if (ui == null || !IsConstructor())
            return;

        try
        {
            GameObject? source = FindConstructorButton(ui, "RandomShip");
            if (source == null)
            {
                if (!loggedMissingButton)
                {
                    loggedMissingButton = true;
                    Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: vanilla RandomShip constructor button not found; Lite button disabled.");
                }

                return;
            }

            GameObject? buttonObject = FindSibling(source, ButtonName);
            bool created = false;
            if (buttonObject == null)
            {
                buttonObject = UnityEngine.Object.Instantiate(source, source.transform.parent);
                buttonObject.name = ButtonName;
                created = true;
            }

            try { buttonObject.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1); }
            catch { }

            ConfigureButton(buttonObject, source);
            SyncButtonState(buttonObject, source);

            if (created && !loggedAttached)
            {
                loggedAttached = true;
                Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: attached constructor button.");
            }

            DesignAutoArmorPatch.EnsureButton(ui);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to attach constructor button. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ConfigureButton(GameObject buttonObject, GameObject source)
    {
        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        if (button == null)
            button = buttonObject.AddComponent<Button>();

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(RunAutoDesignLite));

        SetButtonText(buttonObject, "Auto\nLite");
        SetTooltip(
            buttonObject,
            "Auto-place parts on the current hull without changing tonnage, speed, beam, draught, range, armor, or components.");
        DesignerActionButtonVisuals.Apply(buttonObject, DesignerActionButtonVisual.PartsOnly);

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
        if (buttonObject.activeSelf != source.activeSelf)
            buttonObject.SetActive(source.activeSelf);

        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        Button? sourceButton = source.GetComponent<Button>() ?? source.GetComponentInChildren<Button>(true);
        if (button != null)
            button.interactable = !runningAutoLite && AddRandomPartsNewMethod != null && (sourceButton?.interactable ?? true);
    }

    private static void RunAutoDesignLite()
    {
        if (runningAutoLite)
            return;

        Ui? ui = Safe(() => G.ui, null);
        Ship? ship = ResolveDesignerShip(ui);
        if (ui == null || ship == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; constructor ship was unavailable.");
            return;
        }

        if (AddRandomPartsNewMethod == null)
        {
            if (!loggedMissingPartsMethod)
            {
                loggedMissingPartsMethod = true;
                Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: Ship.AddRandomPartsNew target not found; Lite button disabled.");
            }

            return;
        }

        if (!IsConstructor() || !Safe(() => ui.allowEdit, false) || !IsHumanMainDesignerShip(ship))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped; constructor is not editable for the main player.");
            return;
        }

        LiteSpecs before = CaptureSpecs(ship);
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: requested tons={Fmt(before.Tonnage)} speed={Fmt(Knots(before.SpeedMetersPerSecond))}kn " +
            $"beam={Fmt(before.Beam)} draught={Fmt(before.Draught)} range={before.Range} parts={before.Parts} components={before.Components}.");
        LogDiagnosticSnapshot(ship, "ui-parts-only", "requested");

        runningAutoLite = true;
        try
        {
            try { GameManager.ShowShipBuildingOverlay(); }
            catch { }

            try { ui.HideTooltip(); }
            catch { }

            MelonCoroutines.Start(RunPartsOnlyCoroutine(ui, ship, before));
        }
        catch (Exception ex)
        {
            runningAutoLite = false;
            try { GameManager.EndAutodesign(); }
            catch { }
            try { GameManager.HideShipBuildingOverlay(); }
            catch { }

            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to start parts-only generation. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerator RunPartsOnlyCoroutine(Ui ui, Ship ship, LiteSpecs before)
    {
        bool started = false;
        TrimSummary trimSummary = TrimSummary.NotRun;
        try
        {
            LogDiagnosticSnapshot(ship, "ui-parts-only", "before-removeAllParts");
            try { ship.RemoveAllParts(true); }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"{LogPrefix}: RemoveAllParts failed before parts-only generation. {ex.GetType().Name}: {ex.Message}");
            }
            LogDiagnosticSnapshot(ship, "ui-parts-only", "after-removeAllParts");

            object? routineObject = AddRandomPartsNewMethod!.Invoke(
                ship,
                new object[]
                {
                    true,
                    null!,
                    false,
                    new Il2CppSystem.Nullable<float>(),
                    false,
                    true,
                    true
                });

            started = true;
            if (routineObject is IEnumerator routine)
            {
                yield return routine;
            }
            else if (routineObject is Il2CppSystem.Collections.IEnumerator il2CppRoutine)
            {
                while (il2CppRoutine.MoveNext())
                    yield return il2CppRoutine.Current;
            }
            else
            {
                throw new InvalidOperationException($"AddRandomPartsNew returned {routineObject?.GetType().FullName ?? "null"}.");
            }

            LogDiagnosticSnapshot(ship, "ui-parts-only", "after-addRandomPartsNew");
            trimSummary = TryTrimAutoLiteOverweight(ship);
            LogDiagnosticSnapshot(ship, "ui-parts-only", "after-trim");
        }
        finally
        {
            CompleteAutoLite(ui, ship, before, started, trimSummary);
        }
    }

    internal static bool TryRunPartsOnlyForAi(Ship ship, out string summary, bool traceDiagnostics = false)
    {
        summary = "not-run";
        if (ship == null)
        {
            summary = "ship-unavailable";
            return false;
        }

        if (AddRandomPartsNewMethod == null)
        {
            summary = "addRandomPartsNew-unavailable";
            return false;
        }

        int beforeParts = Safe(() => ship.parts?.Count ?? 0, 0);
        bool started = false;
        int steps = 0;
        string routineKind = "unknown";
        TrimSummary trimSummary = TrimSummary.NotRun;

        if (traceDiagnostics)
            LogDiagnosticSnapshot(ship, "ai-parts-only", "before-removeAllParts");
        try { ship.RemoveAllParts(true); }
        catch (Exception ex)
        {
            return FailAiPartsOnly(ship, "removeAllParts", ex, beforeParts, routineKind, steps, started, out summary);
        }
        if (traceDiagnostics)
            LogDiagnosticSnapshot(ship, "ai-parts-only", "after-removeAllParts");

        object? routineObject;
        try
        {
            routineObject = AddRandomPartsNewMethod.Invoke(
                ship,
                new object[]
                {
                    true,
                    null!,
                    false,
                    new Il2CppSystem.Nullable<float>(),
                    false,
                    true,
                    true
                });
        }
        catch (Exception ex)
        {
            return FailAiPartsOnly(ship, "invoke", ex, beforeParts, routineKind, steps, started, out summary);
        }

        started = true;
        const int maxSteps = 20000;
        if (routineObject is IEnumerator routine)
        {
            routineKind = "managed";
            while (true)
            {
                bool next;
                try { next = routine.MoveNext(); }
                catch (Exception ex)
                {
                    return FailAiPartsOnly(ship, "managedMoveNext", ex, beforeParts, routineKind, steps, started, out summary);
                }

                if (!next)
                    break;

                steps++;
                if (steps >= maxSteps)
                {
                    summary = $"step-cap:{routineKind}:{steps}";
                    WarnAiPartsOnlyFailure(ship, "step-cap", null, beforeParts, routineKind, steps, started, summary);
                    return false;
                }
            }
        }
        else if (routineObject is Il2CppSystem.Collections.IEnumerator il2CppRoutine)
        {
            routineKind = "il2cpp";
            while (true)
            {
                bool next;
                try { next = il2CppRoutine.MoveNext(); }
                catch (Exception ex)
                {
                    return FailAiPartsOnly(ship, "il2cppMoveNext", ex, beforeParts, routineKind, steps, started, out summary);
                }

                if (!next)
                    break;

                steps++;
                if (steps >= maxSteps)
                {
                    summary = $"step-cap:{routineKind}:{steps}";
                    WarnAiPartsOnlyFailure(ship, "step-cap", null, beforeParts, routineKind, steps, started, summary);
                    return false;
                }
            }
        }
        else
        {
            summary = $"unexpected-routine:{routineObject?.GetType().FullName ?? "null"}";
            WarnAiPartsOnlyFailure(ship, "unexpected-routine", null, beforeParts, routineKind, steps, started, summary);
            return false;
        }

        if (traceDiagnostics)
            LogDiagnosticSnapshot(ship, "ai-parts-only", "after-addRandomPartsNew");
        try { trimSummary = TryTrimAutoLiteOverweight(ship); }
        catch (Exception ex)
        {
            return FailAiPartsOnly(ship, "trim", ex, beforeParts, routineKind, steps, started, out summary);
        }
        if (traceDiagnostics)
            LogDiagnosticSnapshot(ship, "ai-parts-only", "after-trim");

        try { ship.CalcWeightAndCost(true, true); }
        catch (Exception ex)
        {
            return FailAiPartsOnly(ship, "recalculate", ex, beforeParts, routineKind, steps, started, out summary);
        }

        int afterParts = Safe(() => ship.parts?.Count ?? 0, 0);
        summary = $"started={started} routine={routineKind} steps={steps} parts={beforeParts}->{afterParts} trim={LogToken(trimSummary.LogText())}";
        if (traceDiagnostics)
            LogDiagnosticSnapshot(ship, "ai-parts-only", "after-recalculate");
        return afterParts > 0;
    }

    private static bool FailAiPartsOnly(
        Ship ship,
        string stage,
        Exception ex,
        int beforeParts,
        string routineKind,
        int steps,
        bool started,
        out string summary)
    {
        string exception = ExceptionChainToken(ex);
        summary = $"{stage}-failed:{exception}";
        WarnAiPartsOnlyFailure(ship, stage, exception, beforeParts, routineKind, steps, started, summary);
        return false;
    }

    private static void WarnAiPartsOnlyFailure(
        Ship ship,
        string stage,
        string? exception,
        int beforeParts,
        string routineKind,
        int steps,
        bool started,
        string summary)
    {
        int currentParts = Safe(() => ship.parts?.Count ?? 0, 0);
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: ai parts-only failed stage={LogToken(stage)} ship={LogToken(ShipLabel(ship))} type={LogToken(ShipTypeLabel(ship))} hull={LogToken(HullLabel(ship))} " +
            $"parts={beforeParts}->{currentParts} started={started} routine={LogToken(routineKind)} steps={steps} summary={LogToken(summary)} exception={LogToken(exception)}.");
    }

    private static void CompleteAutoLite(Ui ui, Ship ship, LiteSpecs before, bool started, TrimSummary trimSummary)
    {
        try
        {
            try { ship.CalcWeightAndCost(true, true); }
            catch { }

            RefreshConstructorUi(ship);

            try { G.sound?.PlayUi("con_generate_ship"); }
            catch { }

            LiteSpecs after = CaptureSpecs(ship);
            bool specsChanged = !NearlyEqual(before.Tonnage, after.Tonnage) ||
                !NearlyEqual(before.SpeedMetersPerSecond, after.SpeedMetersPerSecond) ||
                !NearlyEqual(before.Beam, after.Beam) ||
                !NearlyEqual(before.Draught, after.Draught) ||
                before.Range != after.Range ||
                before.ArmorHash != after.ArmorHash ||
                before.ComponentHash != after.ComponentHash;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: result started={started} specsChanged={specsChanged} " +
                $"tons {Fmt(before.Tonnage)}->{Fmt(after.Tonnage)} speed {Fmt(Knots(before.SpeedMetersPerSecond))}->{Fmt(Knots(after.SpeedMetersPerSecond))}kn " +
                $"beam {Fmt(before.Beam)}->{Fmt(after.Beam)} draught {Fmt(before.Draught)}->{Fmt(after.Draught)} range {before.Range}->{after.Range} " +
                $"parts {before.Parts}->{after.Parts} components {before.Components}->{after.Components} armorChanged={before.ArmorHash != after.ArmorHash} componentsChanged={before.ComponentHash != after.ComponentHash} " +
                $"trim {trimSummary.LogText()}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: completion handling failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            runningAutoLite = false;
            try { GameManager.EndAutodesign(); }
            catch { }
            try { GameManager.HideShipBuildingOverlay(); }
            catch { }
            try { EnsureButton(ui); }
            catch { }
        }
    }

    internal static void LogDiagnosticSnapshot(Ship? ship, string context, string phase)
    {
        try
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: diagnostic context={LogToken(context)} phase={LogToken(phase)} {DiagnosticSnapshot(ship)}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: diagnostic snapshot failed context={LogToken(context)} phase={LogToken(phase)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static string DiagnosticSnapshot(Ship? ship)
    {
        if (ship == null)
            return "ship=none";

        try { ship.CalcWeightAndCost(true, true); }
        catch { }

        WeightValidity weight = ConstructorWeightStatus(ship);
        string shipName = ShipLabel(ship);
        string nation = AiDesignCompetitiveness.PlayerLabel(Safe(() => ship.player, null));
        string type = ShipTypeLabel(ship);
        string hull = HullLabel(ship);
        string hullUi = Safe(() => ship.hull?.data?.nameUi ?? string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(hullUi) || string.Equals(hullUi, "<empty>", StringComparison.Ordinal))
            hullUi = hull;
        Ship.Store? store = Safe(() => ship.ToStore(false), null);
        float storeRealTons = Safe(() => store?.RealTonnage() ?? -1f, -1f);

        return
            $"ship={LogToken(shipName)} nation={LogToken(nation)} type={LogToken(type)} hull={LogToken(hull)} hullUi={LogToken(hullUi)} " +
            $"tons={Fmt(Safe(() => ship.Tonnage(), 0f))} weight={Fmt(CurrentWeight(ship))} valid={weight.Valid} reason={LogToken(weight.Reason)} " +
            $"beam={Fmt(Safe(() => ship.Beam(), 0f))} draught={Fmt(Safe(() => ship.Draught(), 0f))} bonus={Fmt(Safe(() => ship.BeamDraughtBonus(), 1f))} " +
            $"storeRealTons={Fmt(storeRealTons)} storeBeam={Fmt(Safe(() => store?.beam ?? 0f, 0f))} storeDraught={Fmt(Safe(() => store?.draught ?? 0f, 0f))} " +
            $"speed={Fmt(Knots(Safe(() => ship.speedMax, 0f)))}kn range={Safe(() => ship.opRange, VesselEntity.OpRange.Medium)} quarters={Safe(() => ship.CurrentCrewQuarters, Ship.CrewQuarters.Standard)} " +
            $"parts={Safe(() => ship.parts?.Count ?? 0, 0)} components={Safe(() => ship.components?.Count ?? 0, 0)} " +
            $"componentManifest={ShortLogToken(ComponentManifest(ship), 900)} " +
            $"torpedoComponents={ShortLogToken(TorpedoComponentManifest(ship), 360)} " +
            $"partManifest={ShortLogToken(PartManifest(ship), 900)} " +
            $"gunRows={ShortLogToken(GunCaliberManifest(ship), 540)} " +
            $"gunArmorRows={ShortLogToken(GunArmorManifest(ship), 640)}";
    }

    private static string ComponentManifest(Ship ship)
    {
        List<string> labels = new();
        try
        {
            var components = ship.components;
            if (components == null)
                return "none";

            foreach (var pair in components)
            {
                string slot = LogToken(Safe(() => pair.Key?.name ?? pair.Key?.ToString() ?? "?", "?"));
                string component = ComponentLabel(pair.Value);
                labels.Add($"{slot}={component}");
            }
        }
        catch (Exception ex)
        {
            labels.Add("error:" + ex.GetType().Name);
        }

        return JoinLimited(labels.OrderBy(static label => label, StringComparer.OrdinalIgnoreCase), 40);
    }

    private static string TorpedoComponentManifest(Ship ship)
    {
        List<string> labels = new();
        try
        {
            var components = ship.components;
            if (components == null)
                return "none";

            foreach (var pair in components)
            {
                ComponentData? component = pair.Value;
                string text = $"{Safe(() => component?.name ?? string.Empty, string.Empty)} {Safe(() => component?.type ?? string.Empty, string.Empty)} {Safe(() => component?.typex?.name ?? string.Empty, string.Empty)}";
                if (!text.Contains("torp", StringComparison.OrdinalIgnoreCase))
                    continue;

                string slot = LogToken(Safe(() => pair.Key?.name ?? pair.Key?.ToString() ?? "?", "?"));
                labels.Add($"{slot}={ComponentLabel(component)}");
            }
        }
        catch (Exception ex)
        {
            labels.Add("error:" + ex.GetType().Name);
        }

        return JoinLimited(labels.OrderBy(static label => label, StringComparer.OrdinalIgnoreCase), 16);
    }

    private static string PartManifest(Ship ship)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (Part part in ShipParts(ship))
        {
            PartData? data = Safe(() => part.data, null);
            string label = PartDataDiagnosticLabel(ship, data);
            counts.TryGetValue(label, out int count);
            counts[label] = count + 1;
        }

        return JoinLimited(
            counts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value > 1 ? pair.Value.ToString(CultureInfo.InvariantCulture) + "x" + pair.Key : pair.Key),
            32);
    }

    private static string GunCaliberManifest(Ship ship)
    {
        List<string> labels = new();
        foreach (Ship.TurretCaliber row in GunCaliberRows(ship))
        {
            PartData? data = Safe(() => row.turretPartData, null);
            labels.Add(
                $"{PartDataToken(data)}:dia={Fmt(Safe(() => row.diameter, 0f))}:len={Safe(() => row.length, 0).ToString(CultureInfo.InvariantCulture)}");
        }

        return JoinLimited(labels, 16);
    }

    private static string GunArmorManifest(Ship ship)
    {
        List<string> labels = new();
        try
        {
            var rows = ship.shipTurretArmor;
            if (rows == null)
                return "none";

            foreach (Ship.TurretArmor row in rows)
            {
                PartData? data = Safe(() => row.turretPartData, null);
                labels.Add(
                    $"{PartDataToken(data)}:side={Fmt(Safe(() => row.sideTurretArmor, 0f))}:top={Fmt(Safe(() => row.topTurretArmor, 0f))}:barbette={Fmt(Safe(() => row.barbetteArmor, 0f))}");
            }
        }
        catch (Exception ex)
        {
            labels.Add("error:" + ex.GetType().Name);
        }

        return JoinLimited(labels, 18);
    }

    private static string PartDataDiagnosticLabel(Ship ship, PartData? data)
    {
        if (data == null)
            return "part:none";

        string label = $"{PartDataToken(data)}:{LogToken(Safe(() => data.type, string.Empty))}";
        if (Safe(() => data.isGun, false))
        {
            float caliber = EffectiveCaliberInches(ship, data);
            int barrels = Math.Max(1, Safe(() => data.barrels, 1));
            label += $":{Fmt(caliber)}in:{barrels}b";
        }

        return label;
    }

    private static string PartDataToken(PartData? data)
        => LogToken(Safe(() => data?.name ?? data?.nameUi ?? "none", "none"));

    private static string ComponentLabel(ComponentData? component)
        => LogToken(Safe(() => component?.name ?? component?.nameUi ?? "none", "none"));

    private static string JoinLimited(IEnumerable<string> values, int maxItems)
    {
        List<string> items = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(maxItems + 1)
            .ToList();
        if (items.Count == 0)
            return "none";

        bool hidden = items.Count > maxItems;
        if (hidden)
            items.RemoveAt(items.Count - 1);

        if (hidden)
            items.Add("+more");

        return string.Join(",", items);
    }

    private static string ShortLogToken(string? value, int maxLength)
    {
        string token = LogToken(value);
        if (token.Length <= maxLength)
            return token;

        return token[..Math.Max(0, maxLength)] + "...";
    }

    private static TrimSummary TryTrimAutoLiteOverweight(Ship ship)
    {
        float weightBefore = 0f;
        bool validBefore = false;
        string reasonBefore = "unknown";
        WeightValidity currentStatus = new(false, "unknown");
        int diameterRows = 0;
        int lengthRows = 0;
        int secondaryGroupsRemoved = 0;
        string result = "not-needed";

        try
        {
            RecalculateWeight(ship);
            weightBefore = CurrentWeight(ship);
            currentStatus = ConstructorWeightStatus(ship);
            validBefore = currentStatus.Valid;
            reasonBefore = currentStatus.Reason;
            if (validBefore)
            {
                return new TrimSummary(
                    Needed: false,
                    ValidBefore: true,
                    ValidAfter: true,
                    WeightBefore: weightBefore,
                    WeightAfter: weightBefore,
                    DiameterRows: 0,
                    LengthRows: 0,
                    SecondaryGroupsRemoved: 0,
                    Result: result,
                    ReasonBefore: reasonBefore,
                    ReasonAfter: currentStatus.Reason);
            }

            if (!IsWeightReason(reasonBefore))
            {
                return new TrimSummary(
                    Needed: false,
                    ValidBefore: false,
                    ValidAfter: false,
                    WeightBefore: weightBefore,
                    WeightAfter: weightBefore,
                    DiameterRows: 0,
                    LengthRows: 0,
                    SecondaryGroupsRemoved: 0,
                    Result: "not-weight-invalid",
                    ReasonBefore: reasonBefore,
                    ReasonAfter: reasonBefore);
            }

            result = "still-invalid";

            diameterRows = NormalizeCaliberDiameters(ship);
            if (diameterRows > 0)
            {
                RecalculateWeight(ship);
                currentStatus = ConstructorWeightStatus(ship);
                if (currentStatus.Valid)
                    result = "diameter";
            }

            if (!currentStatus.Valid && IsWeightReason(currentStatus.Reason))
            {
                lengthRows = NormalizeCaliberLengths(ship);
                if (lengthRows > 0)
                {
                    RecalculateWeight(ship);
                    currentStatus = ConstructorWeightStatus(ship);
                    if (currentStatus.Valid)
                        result = "length";
                }
            }

            if (!currentStatus.Valid && IsWeightReason(currentStatus.Reason))
            {
                secondaryGroupsRemoved = RemoveSmallestSecondaryGroups(ship);
                RecalculateWeight(ship);
                currentStatus = ConstructorWeightStatus(ship);
                if (currentStatus.Valid)
                    result = "secondary-groups";
                else if (secondaryGroupsRemoved >= MaxSecondaryGroupsRemoved)
                    result = "capped-still-invalid";
            }

            if (!currentStatus.Valid && !IsWeightReason(currentStatus.Reason))
                result = "not-weight-invalid";

            bool validAfter = currentStatus.Valid;
            float weightAfter = CurrentWeight(ship);
            if (validAfter && string.Equals(result, "still-invalid", StringComparison.Ordinal))
                result = "valid";

            return new TrimSummary(
                Needed: true,
                ValidBefore: validBefore,
                ValidAfter: validAfter,
                WeightBefore: weightBefore,
                WeightAfter: weightAfter,
                DiameterRows: diameterRows,
                LengthRows: lengthRows,
                SecondaryGroupsRemoved: secondaryGroupsRemoved,
                Result: result,
                ReasonBefore: reasonBefore,
                ReasonAfter: currentStatus.Reason);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: conservative trim failed; keeping parts-only result. {ex.GetType().Name}: {ex.Message}");
            return new TrimSummary(
                Needed: !validBefore,
                ValidBefore: validBefore,
                ValidAfter: ConstructorWeightStatus(ship).Valid,
                WeightBefore: weightBefore,
                WeightAfter: CurrentWeight(ship),
                DiameterRows: diameterRows,
                LengthRows: lengthRows,
                SecondaryGroupsRemoved: secondaryGroupsRemoved,
                Result: "failed",
                ReasonBefore: reasonBefore,
                ReasonAfter: ConstructorWeightStatus(ship).Reason);
        }
    }

    private static int NormalizeCaliberDiameters(Ship ship)
    {
        int changed = 0;
        foreach (Ship.TurretCaliber row in GunCaliberRows(ship))
        {
            float current = Safe(() => row.diameter, 0f);
            if (current <= 0.001f)
                continue;

            try
            {
                ship.SetCaliberDiameter(row, 0f);
                changed++;
            }
            catch
            {
            }
        }

        return changed;
    }

    private static int NormalizeCaliberLengths(Ship ship)
    {
        int changed = 0;
        foreach (Ship.TurretCaliber row in GunCaliberRows(ship))
        {
            int current = Safe(() => row.length, 0);
            if (current <= 0)
                continue;

            try
            {
                ship.SetCaliberLength(row, 0f);
                changed++;
            }
            catch
            {
            }
        }

        return changed;
    }

    private static int RemoveSmallestSecondaryGroups(Ship ship)
    {
        int removedGroups = 0;
        while (NeedsWeightTrim(ship) && removedGroups < MaxSecondaryGroupsRemoved)
        {
            List<SecondaryGunGroup> groups = SecondaryGunGroups(ship)
                .OrderBy(group => group.CaliberInches)
                .ToList();
            if (groups.Count <= 1)
                break;

            SecondaryGunGroup group = groups[0];
            if (group.Parts.Count <= 0)
                break;

            foreach (Part part in group.Parts.ToList())
            {
                try { ship.RemovePart(part, true, true); }
                catch { }
            }

            foreach (PartData partData in group.PartDatas)
            {
                if (RemainingPartsUseData(ship, partData))
                    continue;

                try { ship.RemoveShipTurretCaliber(partData); }
                catch { }
                try { ship.RemoveShipTurretArmor(partData); }
                catch { }
            }

            removedGroups++;
            RecalculateWeight(ship);
        }

        return removedGroups;
    }

    private static List<Ship.TurretCaliber> GunCaliberRows(Ship ship)
    {
        List<Ship.TurretCaliber> rows = new();
        try
        {
            var gunCalibers = ship.shipGunCaliber;
            if (gunCalibers == null)
                return rows;

            foreach (Ship.TurretCaliber row in gunCalibers)
            {
                if (row != null)
                    rows.Add(row);
            }
        }
        catch
        {
        }

        return rows;
    }

    private static List<Part> ShipParts(Ship ship)
    {
        List<Part> parts = new();
        try
        {
            var source = ship.parts;
            if (source == null)
                return parts;

            foreach (Part part in source)
            {
                if (part != null)
                    parts.Add(part);
            }
        }
        catch
        {
        }

        return parts;
    }

    private static List<SecondaryGunGroup> SecondaryGunGroups(Ship ship)
    {
        Dictionary<float, SecondaryGunGroupBuilder> groups = new();
        foreach (Part part in ShipParts(ship))
        {
            PartData? data = Safe(() => part.data, null);
            if (data == null || !Safe(() => data.isGun, false))
                continue;

            bool isMain = Safe(() => ship.IsMainCal(part), Safe(() => ship.IsMainCal(data), false));
            if (isMain)
                continue;

            bool isSecondary = Safe(() => ship.IsSecondaryCal(part), Safe(() => ship.IsSecondaryCal(data), false));
            if (!isSecondary)
                isSecondary = true;
            if (!isSecondary)
                continue;

            float caliber = EffectiveCaliberInches(ship, data);
            if (caliber <= 0f)
                continue;

            float key = MathF.Round(caliber * 100f) / 100f;
            if (!groups.TryGetValue(key, out SecondaryGunGroupBuilder? builder) || builder == null)
            {
                builder = new SecondaryGunGroupBuilder(key);
                groups[key] = builder;
            }

            builder.Parts.Add(part);
            builder.AddPartData(data);
        }

        return groups.Values
            .Select(builder => builder.ToGroup())
            .Where(group => group.Parts.Count > 0)
            .ToList();
    }

    private static bool RemainingPartsUseData(Ship ship, PartData partData)
    {
        long targetKey = PartDataKey(partData);
        foreach (Part part in ShipParts(ship))
        {
            PartData? data = Safe(() => part.data, null);
            if (data == null)
                continue;

            if (targetKey != 0L && PartDataKey(data) == targetKey)
                return true;
            if (string.Equals(Safe(() => data.name, string.Empty), Safe(() => partData.name, string.Empty), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static float BaseCaliberInches(PartData partData)
    {
        float value = Safe(() => partData.GetCaliberInch(null), 0f);
        if (value > 0f)
            return value;

        value = Safe(() => partData.caliber, 0f);
        return value > 40f ? value / 25.4f : value;
    }

    private static float EffectiveCaliberInches(Ship ship, PartData partData)
    {
        float value = Safe(() => partData.GetCaliberInch(ship), 0f);
        if (value > 0f)
            return value;

        return BaseCaliberInches(partData);
    }

    private static long PartDataKey(PartData partData)
        => Safe(() => partData.Pointer.ToInt64(), 0L);

    private static void RecalculateWeight(Ship ship)
    {
        try { ship.CalcWeightAndCost(true, true); }
        catch { }
    }

    private static bool NeedsWeightTrim(Ship ship)
    {
        WeightValidity status = ConstructorWeightStatus(ship);
        return !status.Valid && IsWeightReason(status.Reason);
    }

    private static WeightValidity ConstructorWeightStatus(Ship ship)
    {
        bool weightOffsetValid = true;
        try
        {
            weightOffsetValid = ship.IsValidWeightOffset();
        }
        catch
        {
        }

        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string rawReason,
                out Il2CppSystem.Collections.Generic.List<Part> _);
            if (!valid)
                return new WeightValidity(false, NormalizeReason(rawReason));

            return weightOffsetValid
                ? new WeightValidity(true, "valid")
                : new WeightValidity(false, "weightOffset");
        }
        catch (Exception ex)
        {
            return new WeightValidity(true, "check-failed:" + ex.GetType().Name);
        }
    }

    private static bool IsWeightReason(string? reason)
        => NormalizeReason(reason).Contains("weight", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        string normalized = reason.Trim().Trim('$');
        normalized = normalized.Replace("Ui_Constr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static float CurrentWeight(Ship ship)
        => Safe(() => ship.Weight(true, true), 0f);

    private static MethodInfo? ResolveAddRandomPartsNew()
    {
        return typeof(Ship)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(static method =>
            {
                if (method.Name != "AddRandomPartsNew")
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 7 &&
                    parameters[0].ParameterType == typeof(bool) &&
                    parameters[2].ParameterType == typeof(bool) &&
                    parameters[4].ParameterType == typeof(bool) &&
                    parameters[5].ParameterType == typeof(bool) &&
                    parameters[6].ParameterType == typeof(bool);
            });
    }

    private static LiteSpecs CaptureSpecs(Ship ship)
        => new(
            Safe(() => ship.Tonnage(), 0f),
            Safe(() => ship.speedMax, 0f),
            Safe(() => ship.Beam(), 0f),
            Safe(() => ship.Draught(), 0f),
            Safe(() => ship.opRange, VesselEntity.OpRange.Medium),
            Safe(() => ship.parts?.Count ?? 0, 0),
            Safe(() => ship.components?.Count ?? 0, 0),
            ArmorHash(ship),
            ComponentHash(ship));

    private static int ArmorHash(Ship ship)
    {
        unchecked
        {
            int hash = 17;
            foreach (Ship.A zone in ArmorZones())
            {
                float armor = Safe(() =>
                {
                    if (ship.armor != null && ship.armor.TryGetValue(zone, out float value))
                        return value;

                    return 0f;
                }, 0f);
                hash = (hash * 31) + zone.GetHashCode();
                hash = (hash * 31) + MathF.Round(armor * 100f).GetHashCode();
            }

            return hash;
        }
    }

    private static int ComponentHash(Ship ship)
    {
        unchecked
        {
            int hash = 17;
            try
            {
                if (ship.components == null)
                    return hash;

                foreach (var pair in ship.components)
                {
                    string key = Safe(() => pair.Key.ToString() ?? string.Empty, string.Empty);
                    string value = Safe(() => pair.Value?.name ?? string.Empty, string.Empty);
                    hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(key);
                    hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(value);
                }
            }
            catch
            {
            }

            return hash;
        }
    }

    private static IEnumerable<Ship.A> ArmorZones()
    {
        yield return Ship.A.Belt;
        yield return Ship.A.BeltBow;
        yield return Ship.A.BeltStern;
        yield return Ship.A.Deck;
        yield return Ship.A.DeckBow;
        yield return Ship.A.DeckStern;
        yield return Ship.A.ConningTower;
        yield return Ship.A.Superstructure;
        yield return Ship.A.TurretSide;
        yield return Ship.A.TurretTop;
        yield return Ship.A.Barbette;
        yield return Ship.A.InnerBelt_1st;
        yield return Ship.A.InnerBelt_2nd;
        yield return Ship.A.InnerBelt_3rd;
        yield return Ship.A.InnerDeck_1st;
        yield return Ship.A.InnerDeck_2nd;
        yield return Ship.A.InnerDeck_3rd;
    }

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

    private static GameObject? FindConstructorButton(Ui ui, string name)
    {
        Transform? root = Safe(() => ui.transform, null);
        return root == null ? null : FindDeepChild(root, name);
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

    private static GameObject? FindSibling(GameObject source, string name)
    {
        Transform? parent = source.transform?.parent;
        Transform? existing = parent == null ? null : parent.Find(name);
        return existing == null ? null : existing.gameObject;
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

    private static bool NearlyEqual(float a, float b)
        => Math.Abs(a - b) <= 0.001f;

    private static float Knots(float metersPerSecond)
        => metersPerSecond / KnotsToMetersPerSecond;

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "none";

        string name = Safe(() => ship.Name(false, false, false, false, true), string.Empty);
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        name = Safe(() => ship.vesselName, string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "unnamed" : name;
    }

    private static string ShipTypeLabel(Ship? ship)
    {
        ShipType? type = Safe(() => ship?.shipType, null);
        if (type == null)
            return "none";

        string name = Safe(() => type.name, string.Empty);
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        name = Safe(() => type.nameUi, string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static string HullLabel(Ship? ship)
    {
        string hull = Safe(() => ship?.hull?.data?.name ?? string.Empty, string.Empty);
        if (!string.IsNullOrWhiteSpace(hull) && !string.Equals(hull, "<empty>", StringComparison.Ordinal))
            return hull;

        hull = Safe(() => ship?.hull?.data?.nameUi ?? string.Empty, string.Empty);
        return string.IsNullOrWhiteSpace(hull) || string.Equals(hull, "<empty>", StringComparison.Ordinal)
            ? "none"
            : hull;
    }

    private static string ExceptionChainToken(Exception ex)
    {
        List<string> parts = new();
        Exception? current = ex;
        for (int depth = 0; current != null && depth < 3; depth++)
        {
            string token = current.GetType().Name;
            string message = ExceptionMessageToken(current);
            if (!string.IsNullOrWhiteSpace(message))
                token += ":" + message;
            parts.Add(token);

            current = current is TargetInvocationException targetInvocation && targetInvocation.InnerException != null
                ? targetInvocation.InnerException
                : current.InnerException;
        }

        return parts.Count == 0 ? "unknown" : string.Join(">", parts);
    }

    private static string ExceptionMessageToken(Exception ex)
    {
        string message = ex.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return "no-message";

        int lineBreak = message.IndexOfAny(new[] { '\r', '\n' });
        if (lineBreak >= 0)
            message = message[..lineBreak];

        message = message.Trim();
        if (message.Length > 120)
            message = message[..120] + "...";

        return LogToken(message);
    }

    private static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        return value
            .Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace(";", ",")
            .Replace("[", "(")
            .Replace("]", ")")
            .Replace(" ", "_");
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private readonly record struct TrimSummary(
        bool Needed,
        bool ValidBefore,
        bool ValidAfter,
        float WeightBefore,
        float WeightAfter,
        int DiameterRows,
        int LengthRows,
        int SecondaryGroupsRemoved,
        string Result,
        string ReasonBefore,
        string ReasonAfter)
    {
        internal static TrimSummary NotRun { get; } = new(
            Needed: false,
            ValidBefore: true,
            ValidAfter: true,
            WeightBefore: 0f,
            WeightAfter: 0f,
            DiameterRows: 0,
            LengthRows: 0,
            SecondaryGroupsRemoved: 0,
            Result: "not-run",
            ReasonBefore: "not-run",
            ReasonAfter: "not-run");

        internal string LogText()
            => $"needed={Needed} reason {ReasonBefore}->{ReasonAfter} diameterOffsetRows={DiameterRows} lengthRows={LengthRows} secondaryGroupsRemoved={SecondaryGroupsRemoved} valid {ValidBefore}->{ValidAfter} weight {Fmt(WeightBefore)}->{Fmt(WeightAfter)} result={Result}";
    }

    private readonly record struct WeightValidity(bool Valid, string Reason);

    private readonly record struct SecondaryGunGroup(
        float CaliberInches,
        List<Part> Parts,
        List<PartData> PartDatas);

    private sealed class SecondaryGunGroupBuilder
    {
        private readonly List<PartData> partDatas = new();

        internal SecondaryGunGroupBuilder(float caliberInches)
        {
            CaliberInches = caliberInches;
        }

        internal float CaliberInches { get; }
        internal List<Part> Parts { get; } = new();

        internal void AddPartData(PartData partData)
        {
            long key = PartDataKey(partData);
            foreach (PartData existing in partDatas)
            {
                long existingKey = PartDataKey(existing);
                if (key != 0L && existingKey == key)
                    return;

                if (string.Equals(Safe(() => existing.name, string.Empty), Safe(() => partData.name, string.Empty), StringComparison.OrdinalIgnoreCase))
                    return;
            }

            partDatas.Add(partData);
        }

        internal SecondaryGunGroup ToGroup()
            => new(CaliberInches, Parts, partDatas.ToList());
    }

    private readonly record struct LiteSpecs(
        float Tonnage,
        float SpeedMetersPerSecond,
        float Beam,
        float Draught,
        VesselEntity.OpRange Range,
        int Parts,
        int Components,
        int ArmorHash,
        int ComponentHash);
}

[HarmonyPatch]
internal static class DesignAutoDesignLiteConstructorUiPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool found = DesignAutoDesignLitePatch.ConstructorUiTarget() != null;
        if (!found)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP auto design lite: Ui.ConstructorUI target not found; button disabled.");
        return found;
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod()
        => DesignAutoDesignLitePatch.ConstructorUiTarget();

    [HarmonyPostfix]
    private static void Postfix(Ui __instance)
        => DesignAutoDesignLitePatch.EnsureButton(__instance);
}
