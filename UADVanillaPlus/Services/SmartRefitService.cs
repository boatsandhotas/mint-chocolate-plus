using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.Harmony;

namespace UADVanillaPlus.Services;

// Conservative manual refit helper. This mutates only the active player
// constructor ship and restores the original design snapshot on any rejected
// outcome, so the button can be tested without exposing the AI to partial
// refit behavior.
internal static class SmartRefitService
{
    private const string LogPrefix = "UADVP smart refit";
    private static bool VerboseAiRefitDiagnostics => false;
    private static readonly MethodInfo? LoadUnloadModelMethod =
        AccessTools.Method(typeof(Ship), "LoadUnloadModel", new[] { typeof(bool) });
    private static readonly PropertyInfo? ShipDecorProperty =
        AccessTools.Property(typeof(Ship), "decor");
    private static readonly MethodInfo? ShipDecorGetMethod =
        AccessTools.Method(typeof(Ship), "get_decor", Type.EmptyTypes);
    private static readonly MethodInfo? ShipDecorSetMethod =
        AccessTools.Method(typeof(Ship), "set_decor", new[] { typeof(Il2CppSystem.Collections.Generic.List<Decor>) })
        ?? AccessTools.Method(typeof(Ship), "set_decor");
    private static readonly FieldInfo? ShipDecorField =
        AccessTools.Field(typeof(Ship), "decor");
    private static bool loggedDecorAccessorResolution;
    private static bool loggedMissingDecorAccessor;

    internal static SmartRefitResult Apply(Ship ship)
        => ApplyCore(ship, null, SmartRefitOptions.Manual);

    internal static SmartRefitResult ApplyForAi(Ship ship, Player owner, Ship? baselineDesign)
        => ApplyCore(ship, owner, SmartRefitOptions.Ai, baselineDesign);

    internal static SmartRefitResult ApplyForAiProbe(Ship ship, Player owner, string context, bool loadVisualModels, Ship? baselineDesign = null)
        => ApplyCore(ship, owner, new SmartRefitOptions(context, loadVisualModels, true, true, true, true, true, true), baselineDesign);

    private static SmartRefitResult ApplyCore(Ship ship, Player? ownerOverride, SmartRefitOptions options, Ship? powerBaseline = null)
    {
        if (ship == null)
            return SmartRefitResult.Rejected("ship-unavailable");

        Stopwatch timer = Stopwatch.StartNew();
        Ship.Store? snapshot = null;
        Player? owner = ownerOverride ?? Safe(() => ship.player, null) ?? PlayerController.Instance;
        try
        {
            if (options.UseStoreRollback)
                snapshot = ship.ToStore(Safe(() => ship.isRefitDesign, false));

            Ship beforeShip = powerBaseline ?? ship;
            string beforeSource = powerBaseline == null ? "clone" : "baseline";
            string beforeSourceDetail = $"{beforeSource}:{LogToken(ShipLabel(beforeShip))}:{LogToken(AiDesignCompetitiveness.ShipId(beforeShip))}";
            InheritedPartSet inheritedParts = CaptureInheritedNonGunParts(ship);
            InheritedPartSet beforeInheritedParts = powerBaseline == null ? inheritedParts : CaptureInheritedNonGunParts(beforeShip);
            float beforeWeight = CurrentWeight(beforeShip);
            PowerSnapshot beforePower = CalculatePower(beforeShip);
            int beforeParts = ShipParts(beforeShip).Count;
            int beforeMainGuns = MainGunCount(beforeShip);
            string beforeGunSummary = GunPartSummary(beforeShip);
            PowerRejectDiagnosticSnapshot beforePowerRejectDetails = CapturePowerRejectDiagnostics(beforeShip);
            LogValidationStageIfNeeded(ship, options, "initial", inheritedParts);
            ValidationStatus initialValidation = options.ValidateDesign
                ? ValidateDesign(beforeShip, beforeInheritedParts)
                : new ValidationStatus(true, "skipped:component-only");
            if (options.RefreshGunParts && beforeMainGuns <= 0)
                return RejectAndRollback(ship, snapshot, owner, options, "no-main-guns-before-refit", timer);

            GunRefreshSummary guns = options.RefreshGunParts
                ? RefreshGunPartsForCurrentMarks(ship, options)
                : GunRefreshSummary.CreateSkipped(options, ship);
            LogValidationStageIfNeeded(ship, options, "post-guns", inheritedParts);
            if (options.RefreshGunParts && guns.Recreated <= 0)
                return RejectAndRollback(ship, snapshot, owner, options, $"no-guns-recreated {guns.Summary}", timer);

            int afterMainGuns = MainGunCount(ship);
            if (options.RefreshGunParts && afterMainGuns <= 0)
                return RejectAndRollback(ship, snapshot, owner, options, $"no-valid-main-guns {guns.Summary}", timer);

            string componentSummary = DesignNewHullDefaultsPatch.ApplySmartRefitComponentDefaults(ship);
            Recalculate(ship);
            LogValidationStageIfNeeded(ship, options, "post-components", inheritedParts);
            if (options.ComponentOnlyManual && !HasInstalledComponentChanges(componentSummary))
            {
                string noOpMessage =
                    $"no-op componentDeltas=none parts {beforeParts}->{ShipParts(ship).Count} " +
                    $"weight {Fmt(beforeWeight)}->{Fmt(CurrentWeight(ship))} gunRefresh={guns.Summary} {componentSummary} armor=skipped=component-only elapsedMs={timer.ElapsedMilliseconds}";
                Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} {options.Context}: rejected {noOpMessage}.");
                return SmartRefitResult.Rejected(noOpMessage);
            }

            ValidationStatus preArmorValidation = options.ValidateDesign
                ? ValidateBeforeArmor(ship, inheritedParts)
                : new ValidationStatus(true, "skipped:component-only");
            if (options.ValidateDesign && !preArmorValidation.Valid)
            {
                LogValidationStageIfNeeded(ship, options, "pre-armor-invalid", inheritedParts);
                return RejectAndRollback(
                    ship,
                    snapshot,
                    owner,
                    options,
                    $"design-invalid-before-armor reason={preArmorValidation.Reason} {guns.Summary} components={componentSummary} armor=skipped",
                    timer);
            }

            bool armorApplied;
            string armorSummary;
            if (options.RebalanceArmor)
            {
                armorApplied = DesignAutoArmorPatch.TryApplySmartRefitArmor(ship, out armorSummary);
            }
            else
            {
                armorApplied = true;
                armorSummary = "skipped=component-only";
            }
            Recalculate(ship);
            LogValidationStageIfNeeded(ship, options, "post-armor", inheritedParts);
            if (!armorApplied && IsFatalArmorResult(armorSummary))
                return RejectAndRollback(ship, snapshot, owner, options, $"armor-rebalance-failed {armorSummary}", timer);

            ValidationStatus validation = options.ValidateDesign
                ? ValidateDesign(ship, inheritedParts)
                : new ValidationStatus(true, "skipped:component-only");
            if (options.ValidateDesign && !validation.Valid)
            {
                LogValidationStageIfNeeded(ship, options, "final-invalid", inheritedParts);
                return RejectAndRollback(
                    ship,
                    snapshot,
                    owner,
                    options,
                    $"design-invalid reason={validation.Reason} {guns.Summary} components={componentSummary} armor={armorSummary}",
                    timer);
            }

            float afterWeight = CurrentWeight(ship);
            ShipEffectivePowerCalculator.InvalidateDesignMetrics(ship);
            PowerSnapshot afterPower = CalculatePower(ship);
            string afterGunSummary = GunPartSummary(ship);
            PowerRejectDiagnosticSnapshot afterPowerRejectDetails = CapturePowerRejectDiagnostics(ship);
            PowerGateResult powerGate = options.RequirePowerImprovement
                ? EvaluatePowerGate(beforePower, afterPower, initialValidation, options)
                : new PowerGateResult(true, "skipped:component-only");
            if (options.RequirePowerImprovement && !powerGate.Accept)
                return RejectAndRollback(
                    ship,
                    snapshot,
                    owner,
                    options,
                    $"power-not-improved power {beforePower.Text}->{afterPower.Text} powerGate={powerGate.Text} beforeSource={beforeSourceDetail} beforePower={beforePower.Summary} afterPower={afterPower.Summary} beforeGuns={beforeGunSummary} afterGuns={afterGunSummary} beforeMainGuns={beforeMainGuns} afterMainGuns={afterMainGuns} gunRefresh={guns.Summary} armor={armorSummary} details={FormatPowerRejectDetails(beforePowerRejectDetails, afterPowerRejectDetails)}",
                    timer);

            int afterParts = ShipParts(ship).Count;
            string message =
                $"accepted guns={guns.Recreated} invalidGuns={guns.Invalid} mainGuns {beforeMainGuns}->{afterMainGuns} " +
                $"parts {beforeParts}->{afterParts} weight {Fmt(beforeWeight)}->{Fmt(afterWeight)} " +
                $"power {beforePower.Text}->{afterPower.Text} powerGate={powerGate.Text} beforeSource={beforeSourceDetail} modelLoad={options.LoadVisualModels} " +
                $"elapsedMs={timer.ElapsedMilliseconds} gunRefresh={guns.Summary} {componentSummary} armor={armorSummary}";
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} {options.Context}: {message}.");
            return SmartRefitResult.Accepted(message);
        }
        catch (Exception ex)
        {
            string rollback = snapshot == null ? "no-rollback:component-only" : Rollback(ship, snapshot, owner, options);
            string message = $"unexpected-exception {ex.GetType().Name} rollback={rollback} elapsedMs={timer.ElapsedMilliseconds}";
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} {options.Context}: {message}: {ex.Message}");
            return SmartRefitResult.Rejected(message);
        }
    }

    private static SmartRefitResult RejectAndRollback(
        Ship ship,
        Ship.Store? snapshot,
        Player? owner,
        SmartRefitOptions options,
        string reason,
        Stopwatch timer)
    {
        string rollback = snapshot == null ? "no-rollback:component-only" : Rollback(ship, snapshot, owner, options);
        string message = $"{reason} rollback={rollback} elapsedMs={timer.ElapsedMilliseconds}";
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} {options.Context}: rejected {message}.");
        return SmartRefitResult.Rejected(message);
    }

    private static string Rollback(Ship ship, Ship.Store snapshot, Player? owner, SmartRefitOptions options)
    {
        try
        {
            string unloadSummary = options.UnloadModelsDuringRollback
                ? TryUnloadShipPartModels(ship, "rollback-before-from-store")
                : "unloadModels=0_unloadFailures=0_skipped=component-only";
            var emptyGuid = new Il2CppSystem.Nullable<Il2CppSystem.Guid>();
            bool restored = ship.FromStore(snapshot, emptyGuid, null, owner, false);
            Recalculate(ship);
            ShipEffectivePowerCalculator.InvalidateDesignMetrics(ship);
            return $"{(restored ? "restored" : "fromStore-false")}_{unloadSummary}";
        }
        catch (Exception ex)
        {
            return $"failed:{ex.GetType().Name}";
        }
    }

    internal static string TryUnloadShipPartModels(Ship? ship, string reason, bool logDetails = true)
    {
        if (ship == null)
            return "unloadModels=0_unloadFailures=0_ship=null";

        int unloaded = 0;
        int failures = 0;
        foreach (Part part in ShipParts(ship).ToList())
            TryUnloadPartModel(part, reason, ref unloaded, ref failures);

        string summary = $"unloadModels={unloaded}_unloadFailures={failures}";
        if (logDetails)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: unload-models reason={LogToken(reason)} ship={LogToken(ShipLabel(ship))} {summary}.");
        }

        return summary;
    }

    internal static string TryCleanupConstructorDecor(Ship? ship, string reason, bool logDetails = true)
    {
        if (ship == null)
            return "decorCleanup=false_ship=null";

        if (logDetails)
            LogDecorAccessorResolutionOnce();
        if (!HasDecorReadAccessor())
        {
            if (logDetails && !loggedMissingDecorAccessor)
            {
                loggedMissingDecorAccessor = true;
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"{LogPrefix}: constructor-decor-cleanup unavailable reason={LogToken(reason)} ship={LogToken(ShipLabel(ship))} accessor=missing.");
            }

            return "decorCleanup=false_accessor=missing";
        }

        int decorCount = 0;
        int childrenCount = 0;
        int hiddenDecor = 0;
        int hiddenChildren = 0;
        int destroyedChildren = 0;
        int failures = 0;
        bool childrenCleared = false;
        bool decorListCleared = false;
        string accessor = "none";
        string setter = "none";

        try
        {
            object? rawDecorList = TryGetDecorList(ship, ref failures, out accessor);
            List<Decor> decorEntries = DecorEntries(rawDecorList, ref failures);
            decorCount = decorEntries.Count;

            foreach (Decor decor in decorEntries)
            {
                if (decor == null)
                    continue;

                try
                {
                    decor.Show(false);
                    hiddenDecor++;
                }
                catch
                {
                    failures++;
                }

                object? rawChildren = Safe(() => (object?)decor.children, null);
                List<UnityEngine.GameObject> children = DecorChildren(rawChildren, ref failures);
                childrenCount += children.Count;
                foreach (UnityEngine.GameObject child in children)
                {
                    if (child == null)
                        continue;

                    try
                    {
                        child.SetActive(false);
                        hiddenChildren++;
                    }
                    catch
                    {
                        failures++;
                    }

                    try
                    {
                        UnityEngine.Object.Destroy(child);
                        destroyedChildren++;
                    }
                    catch
                    {
                        failures++;
                    }
                }

                childrenCleared |= TryClearCollection(rawChildren, ref failures);
            }

            decorListCleared = TryClearCollection(rawDecorList, ref failures);
            TrySetDecorListNull(ship, ref failures, out setter);
        }
        catch
        {
            failures++;
        }

        string summary =
            $"decorCleanup=true_decor={decorCount}_children={childrenCount}_hidden={hiddenDecor}_childHidden={hiddenChildren}_" +
            $"destroyed={destroyedChildren}_childrenCleared={childrenCleared}_decorCleared={decorListCleared}_accessor={accessor}_setter={setter}_failures={failures}";
        if (logDetails && (decorCount > 0 || childrenCount > 0 || failures > 0))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: constructor-decor-cleanup reason={LogToken(reason)} ship={LogToken(ShipLabel(ship))} {summary}.");
        }

        return summary;
    }

    internal static string CleanupConstructorVisualsBeforeLeave(Ship? ship, string reason, bool leaveConstructor = true, bool logDetails = true)
    {
        if (ship == null)
            return "constructorVisualCleanup=false_ship=null";

        string preDecor = TryCleanupConstructorDecor(ship, reason + "-pre-leave", logDetails);
        string preUnload = TryUnloadShipPartModels(ship, reason + "-pre-leave", logDetails);
        bool leave = false;
        int leaveFailures = 0;
        if (leaveConstructor)
        {
            try
            {
                ship.LeaveConstructor();
                leave = true;
            }
            catch
            {
                leaveFailures++;
            }
        }

        string postDecor = TryCleanupConstructorDecor(ship, reason + "-post-leave", logDetails);
        string summary =
            $"constructorVisualCleanup=true_preDecor={preDecor}_preUnload={preUnload}_leave={leave}_" +
            $"leaveFailures={leaveFailures}_postDecor={postDecor}";
        if (logDetails)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: constructor-visual-cleanup reason={LogToken(reason)} ship={LogToken(ShipLabel(ship))} {summary}.");
        }

        return summary;
    }

    private static bool HasDecorReadAccessor()
        => ShipDecorProperty?.CanRead == true || ShipDecorGetMethod != null || ShipDecorField != null;

    private static void LogDecorAccessorResolutionOnce()
    {
        if (loggedDecorAccessorResolution)
            return;

        loggedDecorAccessorResolution = true;
        string get = ShipDecorProperty?.CanRead == true
            ? "property"
            : ShipDecorGetMethod != null
                ? "getter"
                : ShipDecorField != null
                    ? "field"
                    : "missing";
        string set = ShipDecorProperty?.CanWrite == true
            ? "property"
            : ShipDecorSetMethod != null
                ? "method"
                : ShipDecorField != null
                    ? "field"
                    : "missing";
        string field = ShipDecorField != null ? "present" : "missing";
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: decor cleanup accessor resolved get={get} set={set} field={field}.");
    }

    private static object? TryGetDecorList(Ship ship, ref int failures, out string accessor)
    {
        if (ShipDecorProperty?.CanRead == true)
        {
            try
            {
                accessor = "property";
                return ShipDecorProperty.GetValue(ship);
            }
            catch
            {
                failures++;
            }
        }

        if (ShipDecorGetMethod != null)
        {
            try
            {
                accessor = "getter";
                return ShipDecorGetMethod.Invoke(ship, Array.Empty<object>());
            }
            catch
            {
                failures++;
            }
        }

        if (ShipDecorField != null)
        {
            try
            {
                accessor = "field";
                return ShipDecorField.GetValue(ship);
            }
            catch
            {
                failures++;
            }
        }

        accessor = "missing";
        return null;
    }

    private static bool TrySetDecorListNull(Ship ship, ref int failures, out string setter)
    {
        if (ShipDecorProperty?.CanWrite == true)
        {
            try
            {
                ShipDecorProperty.SetValue(ship, null);
                setter = "property";
                return true;
            }
            catch
            {
                failures++;
            }
        }

        if (ShipDecorSetMethod != null)
        {
            try
            {
                ShipDecorSetMethod.Invoke(ship, new object?[] { null });
                setter = "method";
                return true;
            }
            catch
            {
                failures++;
            }
        }

        if (ShipDecorField != null)
        {
            try
            {
                ShipDecorField.SetValue(ship, null);
                setter = "field";
                return true;
            }
            catch
            {
                failures++;
            }
        }

        setter = "missing";
        return false;
    }

    internal static string TryReloadShipVisuals(Ship? ship, string reason)
    {
        if (ship == null)
            return "visualReload=false_ship=null";

        string unloadSummary = TryUnloadShipPartModels(ship, reason + "-before-visual-reload");
        bool unloadShip = false;
        bool loadShip = false;
        bool fallbackLeave = false;
        bool fallbackEnter = false;
        int failures = 0;

        if (LoadUnloadModelMethod != null)
        {
            try
            {
                LoadUnloadModelMethod.Invoke(ship, new object[] { false });
                unloadShip = true;
            }
            catch
            {
                failures++;
            }

            try
            {
                LoadUnloadModelMethod.Invoke(ship, new object[] { true });
                loadShip = true;
            }
            catch
            {
                failures++;
            }
        }
        else
        {
            try
            {
                ship.LeaveConstructor();
                fallbackLeave = true;
            }
            catch
            {
                failures++;
            }

            try
            {
                ship.EnterConstructor();
                fallbackEnter = true;
            }
            catch
            {
                failures++;
            }
        }

        string summary =
            $"visualReload={(loadShip || fallbackEnter ? "true" : "false")}_loadUnloadFalse={unloadShip}_loadUnloadTrue={loadShip}_" +
            $"fallbackLeave={fallbackLeave}_fallbackEnter={fallbackEnter}_failures={failures}_{unloadSummary}";
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: visual-reload reason={LogToken(reason)} ship={LogToken(ShipLabel(ship))} constructor={GameManager.IsConstructor} {summary}.");
        return summary;
    }

    private static bool TryUnloadPartModel(Part? part, string reason, ref int unloaded, ref int failures)
    {
        if (part == null)
            return false;

        try
        {
            part.UnloadModel();
            unloaded++;
            return true;
        }
        catch
        {
            failures++;
            return false;
        }
    }

    private static List<Decor> DecorEntries(object? rawDecorList, ref int failures)
    {
        List<Decor> result = new();
        if (rawDecorList == null)
            return result;

        try
        {
            if (rawDecorList is Il2CppSystem.Collections.Generic.List<Decor> il2CppList)
            {
                foreach (Decor decor in il2CppList)
                {
                    if (decor != null)
                        result.Add(decor);
                }

                return result;
            }

            if (rawDecorList is IEnumerable<Decor> managedList)
            {
                foreach (Decor decor in managedList)
                {
                    if (decor != null)
                        result.Add(decor);
                }

                return result;
            }

            if (rawDecorList is System.Collections.IEnumerable enumerable)
            {
                foreach (object entry in enumerable)
                {
                    if (entry is Decor decor && decor != null)
                        result.Add(decor);
                }
            }
        }
        catch
        {
            failures++;
        }

        return result;
    }

    private static List<UnityEngine.GameObject> DecorChildren(object? rawChildren, ref int failures)
    {
        List<UnityEngine.GameObject> result = new();
        if (rawChildren == null)
            return result;

        try
        {
            if (rawChildren is Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject> il2CppList)
            {
                foreach (UnityEngine.GameObject child in il2CppList)
                {
                    if (child != null)
                        result.Add(child);
                }

                return result;
            }

            if (rawChildren is IEnumerable<UnityEngine.GameObject> managedList)
            {
                foreach (UnityEngine.GameObject child in managedList)
                {
                    if (child != null)
                        result.Add(child);
                }

                return result;
            }

            if (rawChildren is System.Collections.IEnumerable enumerable)
            {
                foreach (object entry in enumerable)
                {
                    if (entry is UnityEngine.GameObject child && child != null)
                        result.Add(child);
                }
            }
        }
        catch
        {
            failures++;
        }

        return result;
    }

    private static bool TryClearCollection(object? collection, ref int failures)
    {
        if (collection == null)
            return false;

        try
        {
            switch (collection)
            {
                case Il2CppSystem.Collections.Generic.List<Decor> decorList:
                    decorList.Clear();
                    return true;
                case Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject> childList:
                    childList.Clear();
                    return true;
                case System.Collections.IList list:
                    list.Clear();
                    return true;
            }

            MethodInfo? clear = AccessTools.Method(collection.GetType(), "Clear", Type.EmptyTypes);
            if (clear == null)
                return false;

            clear.Invoke(collection, Array.Empty<object>());
            return true;
        }
        catch
        {
            failures++;
            return false;
        }
    }

    private static bool TryRemovePartCleanly(
        Ship ship,
        Part part,
        string reason,
        ref int unloaded,
        ref int unloadFailures,
        ref int removeFailures)
    {
        TryUnloadPartModel(part, reason, ref unloaded, ref unloadFailures);

        try
        {
            ship.RemovePart(part, true, true);
            return true;
        }
        catch
        {
            removeFailures++;
            return false;
        }
    }

    private static GunRefreshSummary RefreshGunPartsForCurrentMarks(Ship ship, SmartRefitOptions options)
    {
        List<Part> originalGuns = ShipParts(ship)
            .Where(part => IsGunPart(part))
            .ToList();
        if (originalGuns.Count == 0)
            return GunRefreshSummary.Empty(options, ship);

        List<TurretCaliberSnapshot> caliberSnapshots = CaptureTurretCalibers(ship);
        List<TurretArmorSnapshot> armorSnapshots = CaptureTurretArmor(ship);
        List<StoredGunRefreshPart> stores = new();
        int storeNull = 0;
        int unloadedOriginalGunModels = 0;
        int unloadedInvalidRecreatedModels = 0;
        int unloadFailures = 0;
        int removeFailures = 0;
        foreach (Part part in originalGuns)
        {
            Part.Store? store = Safe(() => part.ToStore(), null);
            if (store != null)
                stores.Add(new StoredGunRefreshPart(store, CaptureGunBarrelSnapshot(ship, part, store, stores.Count)));
            else
                storeNull++;

            TryRemovePartCleanly(
                ship,
                part,
                "original-gun-refresh",
                ref unloadedOriginalGunModels,
                ref unloadFailures,
                ref removeFailures);
        }

        int recreated = 0;
        int createNull = 0;
        int addFailed = 0;
        int loadModelFailed = 0;
        int canPlaceFailed = 0;
        List<string> canPlaceReasons = new();
        List<string> barrelRestores = new();
        List<GunBarrelRestoreTarget> finalBarrelRestores = new();
        foreach (StoredGunRefreshPart stored in stores)
        {
            Part? part = Safe(() => Part.CreateFromStore(stored.Store, ship, ship.partsCont), null);
            if (part == null)
            {
                createNull++;
                continue;
            }

            try { part.SetActiveX(true); }
            catch { }

            try { ship.AddPart(part); }
            catch
            {
                addFailed++;
                TryUnloadPartModel(
                    part,
                    "recreated-gun-add-failed",
                    ref unloadedInvalidRecreatedModels,
                    ref unloadFailures);
                continue;
            }

            if (options.LoadVisualModels)
            {
                try { part.LoadModel(ship, true); }
                catch { loadModelFailed++; }
            }

            GunBarrelRestoreState barrelState = TryRestoreGunBarrelState(
                ship,
                part,
                stored.Barrel,
                "initial",
                barrelRestores);

            if (!CanPlace(part, out string canPlaceReason))
            {
                canPlaceFailed++;
                AddCanPlaceReason(canPlaceReasons, part, canPlaceReason);
                TryRemovePartCleanly(
                    ship,
                    part,
                    "recreated-gun-invalid",
                    ref unloadedInvalidRecreatedModels,
                    ref unloadFailures,
                    ref removeFailures);
                continue;
            }

            if (barrelState.Restored)
                finalBarrelRestores.Add(new GunBarrelRestoreTarget(part, stored.Barrel));
            recreated++;
        }

        try { ship.CheckCaliberOnShip(ship); }
        catch { }
        RestoreTurretCalibers(ship, caliberSnapshots);
        RestoreTurretArmor(ship, armorSnapshots);
        try { ship.Init(); }
        catch { }
        RestoreFinalGunBarrelStates(ship, finalBarrelRestores, barrelRestores);
        try { ship.CalcInstability(true); }
        catch { }
        Recalculate(ship);

        return new GunRefreshSummary(
            originalGuns.Count,
            stores.Count,
            storeNull,
            createNull,
            addFailed,
            loadModelFailed,
            canPlaceFailed,
            recreated,
            options.Context,
            options.LoadVisualModels,
            Safe(() => ship.partsCont != null, false),
            GameManager.IsConstructor,
            canPlaceReasons.Count == 0 ? "none" : string.Join("|", canPlaceReasons),
            "none",
            FormatBarrelRestores(barrelRestores),
            unloadedOriginalGunModels,
            unloadedInvalidRecreatedModels,
            unloadFailures,
            removeFailures,
            false);
    }

    private static GunBarrelSnapshot CaptureGunBarrelSnapshot(Ship ship, Part part, Part.Store store, int index)
    {
        PartData? data = Safe(() => part.data, null);
        return new GunBarrelSnapshot(
            SafeString(() => store.Id.ToString()),
            SafeString(() => store.name),
            index,
            PartDataKey(data),
            SafeString(() => data?.name),
            SafeString(() => data?.GetGunDataId(ship)),
            data == null ? 0f : GunCaliberInches(ship, data),
            GunBarrelCount(data),
            Safe(() => part.barrelLength, 0f),
            Safe(() => part.caliberLength, 0),
            Safe(() => data != null ? ship.TechGunGrade(data, true) : 0, 0),
            data == null ? "<empty>" : SafeString(() => Part.GunBarrelLength(data, ship, false)));
    }

    private static GunBarrelRestoreState TryRestoreGunBarrelState(
        Ship ship,
        Part part,
        GunBarrelSnapshot snapshot,
        string stage,
        List<string> logs)
    {
        float postCreateBarrelLength = Safe(() => part.barrelLength, 0f);
        int postCreateCaliberLength = Safe(() => part.caliberLength, 0);
        string partName = NormalizeReason(SafeString(() => part.data?.name));
        GunBarrelRestoreState state = new(postCreateBarrelLength, postCreateCaliberLength, false);

        if (!snapshot.HasUsableLength)
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:invalid-value beforeBL={Fmt(snapshot.BarrelLength)} beforeCL={snapshot.CaliberLength}");
            return state;
        }

        PartData? data = Safe(() => part.data, null);
        if (data == null)
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:no-data");
            return state;
        }

        if (!snapshot.MatchesData(data))
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:data-changed from={LogToken(snapshot.PartDataName)} to={LogToken(SafeString(() => data.name))}");
            return state;
        }

        float caliber = GunCaliberInches(ship, data);
        if (snapshot.Caliber > 0.05f && caliber > 0.05f && Math.Abs(caliber - snapshot.Caliber) > 0.05f)
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:caliber-changed {Fmt(snapshot.Caliber)}->{Fmt(caliber)}");
            return state;
        }

        int grade = Safe(() => ship.TechGunGrade(data, true), 0);
        if (snapshot.TechGrade > 0 && grade > 0 && grade != snapshot.TechGrade)
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:grade-changed {snapshot.TechGrade}->{grade}");
            return state;
        }

        try
        {
            part.barrelLength = snapshot.BarrelLength;
            part.caliberLength = snapshot.CaliberLength;
            AddBarrelRestore(
                logs,
                $"{partName}:{stage}:restored BL={Fmt(postCreateBarrelLength)}->{Fmt(snapshot.BarrelLength)} CL={postCreateCaliberLength}->{snapshot.CaliberLength} grade={grade} barrel={LogToken(snapshot.BarrelText)}");
            return state with { Restored = true };
        }
        catch (Exception ex)
        {
            AddBarrelRestore(logs, $"{partName}:{stage}:skip:restore-failed:{ex.GetType().Name}");
            return state;
        }
    }

    private static void RestoreFinalGunBarrelStates(
        Ship ship,
        List<GunBarrelRestoreTarget> targets,
        List<string> logs)
    {
        foreach (GunBarrelRestoreTarget target in targets)
            TryRestoreGunBarrelState(ship, target.Part, target.Snapshot, "final", logs);
    }

    private static int GunBarrelCount(PartData? data)
    {
        int count = TryParseGunBarrelCount(SafeString(() => data?.name));
        if (count > 0)
            return count;

        count = TryParseGunBarrelCount(SafeString(() => data?.nameUi));
        if (count > 0)
            return count;

        return TryParseGunBarrelCount(SafeString(() => data?.type));
    }

    private static int TryParseGunBarrelCount(string value)
    {
        int marker = value.LastIndexOf("_x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || marker + 2 >= value.Length)
            return 0;

        int start = marker + 2;
        int end = start;
        while (end < value.Length && char.IsDigit(value[end]))
            end++;

        return end > start && int.TryParse(value[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;
    }

    private static void AddBarrelRestore(List<string> entries, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || entries.Count >= 16)
            return;

        entries.Add(LogToken(entry));
    }

    private static string FormatBarrelRestores(List<string> entries)
    {
        if (entries.Count == 0)
            return "none";

        List<string> visible = entries.Take(8).ToList();
        int hidden = Math.Max(0, entries.Count - visible.Count);
        if (hidden > 0)
            visible.Add($"+{hidden}more");

        return string.Join("|", visible);
    }

    private static void AddCanPlaceReason(List<string> reasons, Part part, string reason)
    {
        if (reasons.Count >= 4)
            return;

        string partName = NormalizeReason(Safe(() => part.data?.name ?? string.Empty, string.Empty));
        string normalizedReason = NormalizeReason(reason);
        reasons.Add($"{partName}:{normalizedReason}");
    }

    private static bool CanPlace(Part part, out string reason)
    {
        reason = string.Empty;
        try
        {
            return part.CanPlace(out reason);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    private static void LogValidationStageIfNeeded(
        Ship ship,
        SmartRefitOptions options,
        string stage,
        InheritedPartSet? inheritedParts = null)
    {
        if (!ShouldLogValidationStages(options))
            return;

        try
        {
            List<Part> parts = ShipParts(ship);
            int gunParts = parts.Count(IsGunPart);
            string message =
                $"validation-stage context={options.Context} stage={stage} ship={LogToken(ShipLabel(ship))} " +
                $"parts={parts.Count} gunParts={gunParts} mainGuns={MainGunCount(ship)} " +
                $"weight={Fmt(CurrentWeight(ship))}/{Fmt(Safe(() => ship.Tonnage(), 0f))} " +
                $"reqParts={CostReqPartsStageDetails(ship, inheritedParts)} " +
                $"costWeightBarbette={CostWeightBarbetteStageDetails(ship)} " +
                $"shipValid={ShipValidStageDetails(ship)} " +
                $"towerCanPlace={TowerCanPlaceDetails(ship)}";
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} {options.Context}: {message}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix} {options.Context}: validation-stage failed stage={stage}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldLogValidationStages(SmartRefitOptions options)
        => options.Context.Contains("ai-probe-constructor-and-model-load", StringComparison.OrdinalIgnoreCase) ||
           options.Context.Contains("ai-real", StringComparison.OrdinalIgnoreCase);

    private static string CostReqPartsStageDetails(Ship ship, InheritedPartSet? inheritedParts = null)
    {
        try
        {
            return EvaluateReqPartsValidation(ship, inheritedParts ?? InheritedPartSet.Empty).Details;
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string CostWeightBarbetteStageDetails(Ship ship)
    {
        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);

            return string.Join(
                ";",
                valid.ToString().ToLowerInvariant(),
                "reason=" + NormalizeReason(reason),
                "barbetteParts=" + FormatPartList(errorBarbettePart));
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string ShipValidStageDetails(Ship ship)
    {
        try
        {
            return ship.IsValid(false).ToString().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string TowerCanPlaceDetails(Ship ship)
    {
        try
        {
            List<Part> towers = ShipParts(ship)
                .Where(IsTowerPart)
                .Take(4)
                .ToList();
            if (towers.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (Part tower in towers)
            {
                bool canPlace = CanPlace(tower, out string reason);
                labels.Add($"{PartLabel(tower)}:{(canPlace ? "pass" : NormalizeReason(reason))}");
            }

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static bool IsTowerPart(Part part)
    {
        string name = SafeString(() => part.data?.name);
        string nameUi = SafeString(() => part.data?.nameUi);
        return name.Contains("tower", StringComparison.OrdinalIgnoreCase) ||
               nameUi.Contains("tower", StringComparison.OrdinalIgnoreCase);
    }

    private static List<TurretCaliberSnapshot> CaptureTurretCalibers(Ship ship)
    {
        List<TurretCaliberSnapshot> snapshots = new();
        try
        {
            if (ship.shipGunCaliber == null)
                return snapshots;

            foreach (Ship.TurretCaliber row in ship.shipGunCaliber)
            {
                if (row?.turretPartData == null)
                    continue;

                snapshots.Add(new TurretCaliberSnapshot(
                    PartDataKey(row.turretPartData),
                    Safe(() => row.turretPartData.name, string.Empty),
                    row.isCasemateGun,
                    row.diameter,
                    row.length));
            }
        }
        catch
        {
        }

        return snapshots;
    }

    private static List<TurretArmorSnapshot> CaptureTurretArmor(Ship ship)
    {
        List<TurretArmorSnapshot> snapshots = new();
        try
        {
            if (ship.shipTurretArmor == null)
                return snapshots;

            foreach (Ship.TurretArmor row in ship.shipTurretArmor)
            {
                if (row?.turretPartData == null)
                    continue;

                snapshots.Add(new TurretArmorSnapshot(
                    PartDataKey(row.turretPartData),
                    Safe(() => row.turretPartData.name, string.Empty),
                    row.isCasemateGun,
                    row.sideTurretArmor,
                    row.topTurretArmor,
                    row.barbetteArmor));
            }
        }
        catch
        {
        }

        return snapshots;
    }

    private static void RestoreTurretCalibers(Ship ship, List<TurretCaliberSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        try
        {
            if (ship.shipGunCaliber == null)
                return;

            foreach (Ship.TurretCaliber row in ship.shipGunCaliber)
            {
                if (row == null ||
                    !TryFindSnapshot(snapshots, row.turretPartData, Safe(() => row.isCasemateGun, false), out TurretCaliberSnapshot snapshot))
                    continue;

                row.diameter = snapshot.Diameter;
                row.length = snapshot.Length;
            }
        }
        catch
        {
        }
    }

    private static void RestoreTurretArmor(Ship ship, List<TurretArmorSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        try
        {
            if (ship.shipTurretArmor == null)
                return;

            foreach (Ship.TurretArmor row in ship.shipTurretArmor)
            {
                if (row == null ||
                    !TryFindSnapshot(snapshots, row.turretPartData, Safe(() => row.isCasemateGun, false), out TurretArmorSnapshot snapshot))
                    continue;

                row.sideTurretArmor = snapshot.Side;
                row.topTurretArmor = snapshot.Top;
                row.barbetteArmor = snapshot.Barbette;
            }
        }
        catch
        {
        }
    }

    private static ValidationStatus ValidateBeforeArmor(Ship ship, InheritedPartSet inheritedParts)
    {
        try
        {
            ReqPartsValidation reqValidation = EvaluateReqPartsValidation(ship, inheritedParts);
            if (!reqValidation.Valid)
                return new ValidationStatus(false, "reqParts:false " + reqValidation.Details);
        }
        catch (Exception ex)
        {
            return new ValidationStatus(false, "reqParts:" + ex.GetType().Name);
        }

        return new ValidationStatus(true, "valid");
    }

    private static ValidationStatus ValidateDesign(Ship ship, InheritedPartSet? inheritedParts = null)
    {
        ReqPartsValidation reqValidation;
        try
        {
            reqValidation = EvaluateReqPartsValidation(ship, inheritedParts ?? InheritedPartSet.Empty);
            if (!reqValidation.Valid)
                return new ValidationStatus(false, "reqParts:false " + reqValidation.Details);
        }
        catch (Exception ex)
        {
            return new ValidationStatus(false, "reqParts:" + ex.GetType().Name);
        }

        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);
            if (!valid)
            {
                return new ValidationStatus(
                    false,
                    $"costWeightBarbette:false reason={NormalizeReason(reason)} barbetteParts={FormatPartList(errorBarbettePart)}");
            }
        }
        catch (Exception ex)
        {
            return new ValidationStatus(false, "costWeightBarbette:" + ex.GetType().Name);
        }

        try
        {
            if (reqValidation.WaivedBadParts == 0 && !ship.IsValid(false))
                return new ValidationStatus(false, "ship-valid-false-after-explicit-checks");
        }
        catch (Exception ex)
        {
            return new ValidationStatus(false, "ship-valid-check:" + ex.GetType().Name);
        }

        try
        {
            PlayerController? player = PlayerController.Instance;
            Player? owner = Safe(() => ship.player, null);
            bool canBuild;
            string buildReason = "unknown";
            if (AiDesignBuildability.IsAiPlayer(owner))
            {
                canBuild = AiDesignBuildability.CanBuildDesign(
                    owner,
                    ship,
                    1,
                    "SmartRefitService",
                    out buildReason);
            }
            else
            {
                canBuild = player == null || player.CanBuildShipsFromDesign(ship, out buildReason);
                buildReason = NormalizeReason(buildReason);
            }

            if (!canBuild)
            {
                string normalizedReason = NormalizeReason(buildReason);
                if (string.Equals(normalizedReason, "obsolete", StringComparison.OrdinalIgnoreCase) &&
                    IsRefitDesignForBuildability(ship))
                {
                    if (VerboseAiRefitDiagnostics)
                    {
                        Melon<UADVanillaPlusMod>.Logger.Msg(
                            $"UADVP smart refits ai: buildability-obsolete-allowed design={LogToken(ShipLabel(ship))} reason={LogToken(normalizedReason)} waivedBadParts={reqValidation.WaivedBadParts} source=service-validation.");
                    }

                    return new ValidationStatus(true, "valid");
                }

                if (CanUseWaiverAwareBuildabilityForRefit(
                        ship,
                        normalizedReason,
                        reqValidation,
                        out int waivedBadParts,
                        out string waiverDetails))
                {
                    if (VerboseAiRefitDiagnostics)
                    {
                        Melon<UADVanillaPlusMod>.Logger.Msg(
                            $"UADVP smart refits ai: buildability-waiver-allowed design={LogToken(ShipLabel(ship))} reason={LogToken(normalizedReason)} waivedBadParts={waivedBadParts} source=service-validation details={LogToken(waiverDetails)}.");
                    }

                    return new ValidationStatus(true, "valid");
                }

                return new ValidationStatus(false, "canBuild:false reason=" + normalizedReason);
            }
        }
        catch (Exception ex)
        {
            return new ValidationStatus(false, "canBuild:" + ex.GetType().Name);
        }

        return new ValidationStatus(true, "valid");
    }

    private static bool IsRefitDesignForBuildability(Ship ship)
        => Safe(() => ship.isRefitDesign, false) ||
           Safe(() => ship.designShipForRefit != null, false);

    internal static bool CanUseWaiverAwareBuildabilityForRefit(
        Ship ship,
        string normalizedReason,
        out int waivedBadParts,
        out string details)
    {
        waivedBadParts = 0;
        details = "none";
        try
        {
            ReqPartsValidation reqValidation = EvaluateReqPartsValidation(ship, CaptureInheritedNonGunParts(ship));
            return CanUseWaiverAwareBuildabilityForRefit(
                ship,
                normalizedReason,
                reqValidation,
                out waivedBadParts,
                out details);
        }
        catch (Exception ex)
        {
            details = "error:" + ex.GetType().Name;
            return false;
        }
    }

    private static bool CanUseWaiverAwareBuildabilityForRefit(
        Ship ship,
        string normalizedReason,
        ReqPartsValidation reqValidation,
        out int waivedBadParts,
        out string details)
    {
        waivedBadParts = reqValidation.WaivedBadParts;
        details = reqValidation.Details;
        if (!IsRefitDesignForBuildability(ship))
        {
            details = "not-refit-design";
            return false;
        }

        if (!IsWaiverAwareBuildabilityReason(normalizedReason))
        {
            details = "reason-not-waivable";
            return false;
        }

        if (!reqValidation.Valid || reqValidation.WaivedBadParts <= 0)
            return false;

        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);
            if (!valid)
            {
                details += $";costWeightBarbette:false reason={NormalizeReason(reason)} barbetteParts={FormatPartList(errorBarbettePart)}";
                return false;
            }
        }
        catch (Exception ex)
        {
            details += ";costWeightBarbette:" + ex.GetType().Name;
            return false;
        }

        return true;
    }

    private static ReqPartsValidation EvaluateReqPartsValidation(Ship ship, InheritedPartSet inheritedParts)
    {
        bool rawValid = ship.IsValidCostReqParts(
            out string reason,
            out Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo> notPassed,
            out Il2CppSystem.Collections.Generic.Dictionary<Part, string> badParts);

        List<BadPartValidation> badPartDetails = AnalyzeBadParts(ship, badParts, inheritedParts);
        int blockingBadParts = badPartDetails.Count(detail => !detail.Waived);
        int waivedBadParts = badPartDetails.Count(detail => detail.Waived);
        bool hasNotPassed = HasItems(notPassed);
        bool rawFailureWithoutDetails = !rawValid && !hasNotPassed && !HasItems(badParts);
        bool effectiveValid = !rawFailureWithoutDetails && !hasNotPassed && blockingBadParts == 0;
        string details = string.Join(
            ";",
            effectiveValid.ToString().ToLowerInvariant(),
            "raw=" + rawValid.ToString().ToLowerInvariant(),
            "reason=" + NormalizeReason(reason),
            "notPassed=" + FormatReqInfoFailures(ship, notPassed),
            "badParts=" + FormatBadPartFailures(badPartDetails.Where(detail => !detail.Waived)),
            "waivedBadParts=" + FormatBadPartFailures(badPartDetails.Where(detail => detail.Waived)));

        return new ReqPartsValidation(effectiveValid, details, waivedBadParts);
    }

    private static List<BadPartValidation> AnalyzeBadParts(
        Ship ship,
        Il2CppSystem.Collections.Generic.Dictionary<Part, string>? badParts,
        InheritedPartSet inheritedParts)
    {
        List<BadPartValidation> result = new();
        try
        {
            if (badParts == null || badParts.Count == 0)
                return result;

            foreach (var pair in badParts)
            {
                Part? part = pair.Key;
                string reason = NormalizeReason(pair.Value);
                bool inherited = inheritedParts.Contains(part);
                bool isGun = part != null && IsGunPart(part);
                bool availability = IsAvailabilityOrObsoleteReason(reason);
                bool inheritedPartPlacement = IsInheritedPartPlacementWaiver(ship, part, reason, inherited, isGun);
                bool waived = part != null && inherited && !isGun && (availability || inheritedPartPlacement);
                result.Add(new BadPartValidation(
                    part,
                    reason,
                    inherited,
                    part != null && !inherited,
                    waived,
                    PartAvailabilityDiagnostic(part)));
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool IsInheritedPartPlacementWaiver(Ship ship, Part? part, string reason, bool inherited, bool isGun)
    {
        if (part == null || !inherited || isGun || !IsRefitDesignForBuildability(ship))
            return false;

        if (!reason.Equals("part", StringComparison.OrdinalIgnoreCase))
            return false;

        string type = SafeString(() => part.data?.type);
        return type.Equals("tower_main", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("tower_sec", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("funnel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAvailabilityOrObsoleteReason(string? reason)
    {
        string normalized = NormalizeReason(reason);
        return normalized.Equals("available", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("obsolete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWaiverAwareBuildabilityReason(string? reason)
    {
        string normalized = NormalizeReason(reason);
        return IsAvailabilityOrObsoleteReason(normalized) ||
               normalized.Equals("parts", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("valid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasItems<T>(Il2CppSystem.Collections.Generic.List<T>? items)
        => Safe(() => items != null && items.Count > 0, false);

    private static bool HasItems<TKey, TValue>(Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>? items)
        where TKey : notnull
        => Safe(() => items != null && items.Count > 0, false);

    private static string FormatReqInfoFailures(
        Ship ship,
        Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo>? notPassed)
    {
        try
        {
            if (notPassed == null || notPassed.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (ShipType.ReqInfo req in notPassed)
            {
                if (labels.Count >= 6)
                    break;

                string stat = LogToken(SafeString(() => req.stat?.name));
                float value = Safe(() => ship.stats[req.stat].total, float.NaN);
                string valueText = float.IsNaN(value) ? "?" : value.ToString("0.##", CultureInfo.InvariantCulture);
                labels.Add($"{stat}={valueText}({req.min}-{req.max})");
            }

            int hidden = Math.Max(0, notPassed.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string FormatBadPartFailures(Il2CppSystem.Collections.Generic.Dictionary<Part, string>? badParts)
    {
        try
        {
            if (badParts == null || badParts.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (var pair in badParts)
            {
                if (labels.Count >= 6)
                    break;

                labels.Add($"{PartLabel(pair.Key)}:{LogToken(pair.Value)}");
            }

            int hidden = Math.Max(0, badParts.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string FormatBadPartFailures(IEnumerable<BadPartValidation> badParts)
    {
        try
        {
            List<BadPartValidation> parts = badParts.ToList();
            if (parts.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (BadPartValidation part in parts)
            {
                if (labels.Count >= 6)
                    break;

                labels.Add(
                    $"{PartLabel(part.Part)}:reason={LogToken(part.Reason)}:inheritedBaseline={part.InheritedBaseline}:touchedBySmartRefit={part.TouchedBySmartRefit}:waived={part.Waived}:{part.Availability}");
            }

            int hidden = Math.Max(0, parts.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string PartAvailabilityDiagnostic(Part? part)
    {
        PartData? data = Safe(() => part?.data, null);
        if (data == null)
            return "partData=none";

        return string.Join(
            ":",
            "type=" + LogToken(SafeString(() => data.type)),
            "needUnlock=" + LogToken(SafeString(() => data.NeedUnlock)),
            "needTags=" + FormatNeedTags(data));
    }

    private static string FormatNeedTags(PartData data)
    {
        try
        {
            if (data.needTags == null || data.needTags.Count == 0)
                return "none";

            List<string> groups = new();
            foreach (var orGroup in data.needTags)
            {
                if (groups.Count >= 4)
                    break;

                if (orGroup == null || orGroup.Count == 0)
                    continue;

                List<string> tags = new();
                foreach (string tag in orGroup)
                {
                    if (tags.Count >= 4)
                        break;

                    tags.Add(LogToken(tag));
                }

                groups.Add(string.Join("+", tags));
            }

            int hidden = Math.Max(0, data.needTags.Count - groups.Count);
            if (hidden > 0)
                groups.Add($"+{hidden}more");

            return groups.Count == 0 ? "none" : string.Join(",", groups);
        }
        catch (Exception ex)
        {
            return "unavailable_" + ex.GetType().Name;
        }
    }

    private static string FormatPartList(Il2CppSystem.Collections.Generic.List<Part>? parts)
    {
        try
        {
            if (parts == null || parts.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (Part part in parts)
            {
                if (labels.Count >= 6)
                    break;

                labels.Add(PartLabel(part));
            }

            int hidden = Math.Max(0, parts.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string PartLabel(Part? part)
    {
        PartData? data = Safe(() => part?.data, null);
        if (data == null)
            return "unknown";

        string name = SafeString(() => data.name);
        if (string.Equals(name, "<empty>", StringComparison.Ordinal))
            name = SafeString(() => data.nameUi);

        return LogToken(name);
    }

    private static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "<null>";

        string name = SafeString(() => ship.Name(false, false, false, false, true));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        name = SafeString(() => ship.vesselName);
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => ship.name);
    }

    private static bool IsFatalArmorResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;

        string normalized = result.Trim();
        return normalized.Equals("ship-unavailable", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("already-running", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("failed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInstalledComponentChanges(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        foreach (string token in summary.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            int slash = token.IndexOf('/', equals + 1);
            if (equals < 0 || slash <= equals + 1)
                continue;

            string installedText = token.Substring(equals + 1, slash - equals - 1);
            if (int.TryParse(installedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int installed) && installed > 0)
                return true;
        }

        return false;
    }

    private static PowerSnapshot CalculatePower(Ship ship)
    {
        try
        {
            EffectivePowerResult result = ShipEffectivePowerCalculator.Calculate(ship);
            string reason = NormalizeReason(result.Reason);
            if (!result.Success)
                return PowerSnapshot.Failed(reason);

            if (float.IsNaN(result.AdjustedPower) || float.IsInfinity(result.AdjustedPower) || result.AdjustedPower <= 0f)
                return PowerSnapshot.Failed("non-positive");

            return PowerSnapshot.Successful(result);
        }
        catch (Exception ex)
        {
            return PowerSnapshot.Failed("checkFailed:" + ex.GetType().Name);
        }
    }

    private static PowerGateResult EvaluatePowerGate(PowerSnapshot before, PowerSnapshot after, ValidationStatus beforeValidation, SmartRefitOptions options)
    {
        if (options.StrictPowerGate)
        {
            if (!before.Success)
                return new PowerGateResult(false, "before-power-unavailable:" + before.Reason);

            if (before.AdjustedPower <= 0f)
                return new PowerGateResult(false, "before-power-unavailable:non-positive");

            if (!after.Success)
                return new PowerGateResult(false, "after-power-unavailable:" + after.Reason);

            if (after.AdjustedPower <= 0f)
                return new PowerGateResult(false, "after-power-unavailable:non-positive");

            if (after.AdjustedPower > before.AdjustedPower + 0.001f)
                return new PowerGateResult(true, beforeValidation.Valid ? "improved" : "improved-baseline-invalid");

            return new PowerGateResult(false, beforeValidation.Valid ? "not-improved" : "not-improved-baseline-invalid");
        }

        if (!beforeValidation.Valid)
            return new PowerGateResult(true, "skipped:before-invalid");

        if (!before.Success)
            return new PowerGateResult(true, "skipped:before-" + before.Reason);

        if (!after.Success)
            return new PowerGateResult(true, "skipped:after-" + after.Reason);

        if (before.AdjustedPower <= 0f)
            return new PowerGateResult(true, "skipped:before-non-positive");

        if (after.AdjustedPower > before.AdjustedPower + 0.001f)
            return new PowerGateResult(true, "improved");

        return new PowerGateResult(false, "not-improved");
    }

    private static string PowerResultBreakdown(EffectivePowerResult result)
        => string.Join(
            ",",
            $"type={result.Type}",
            $"base={ShipEffectivePowerCalculator.FormatCompactPower(result.BasePower)}",
            $"vanillaBase={ShipEffectivePowerCalculator.FormatCompactPower(result.VanillaBasePower)}",
            $"adj={ShipEffectivePowerCalculator.FormatCompactPower(result.AdjustedPower)}",
            $"bench={ShipEffectivePowerCalculator.FormatCompactPower(result.BenchmarkAdjustedPower)}",
            $"ratio={ShipEffectivePowerCalculator.FormatRatio(result.CompetitivenessRatio)}",
            $"cost={Fmt(result.Cost)}",
            $"speed={Fmt(result.Speed)}",
            $"vanillaFirepower={Fmt(result.VanillaFirepower)}",
            $"vpWeapon={Fmt(result.VpWeaponPower)}",
            $"vpGun={Fmt(result.AdjustedGunPower)}",
            $"vpTorp={Fmt(result.AdjustedTorpedoPower)}",
            $"armorAvg={Fmt(result.ArmorAverageMm)}",
            $"vpArmor={Fmt(result.VpArmorScore)}",
            $"typeArmor={Fmt(result.TypeArmorFactor)}",
            $"costFactor={Fmt(result.CostFactor)}",
            $"speedFactor={Fmt(result.SpeedFactor)}",
            $"armorFactor={Fmt(result.ArmorFactor)}",
            $"weaponFactor={Fmt(result.WeaponFactor)}",
            $"q={Fmt(result.QualityMultiplier)}",
            $"gunQ={Fmt(result.GunQualityMultiplier)}",
            $"reload={Fmt(result.GunReloadFactor)}",
            $"gunAcc={Fmt(result.GunIntrinsicAccuracyFactor)}",
            $"shipAcc={Fmt(result.ShipAccuracyFactor)}",
            $"range={Fmt(result.GunRangeFactor)}",
            $"torp={Fmt(result.TorpedoThreatFactor)}",
            $"torpRange={Fmt(result.TorpedoRangeFactor)}",
            $"torpSpeed={Fmt(result.TorpedoSpeedFactor)}",
            $"torpReload={Fmt(result.TorpedoReloadFactor)}",
            $"torpAcc={Fmt(result.TorpedoAccuracyFactor)}",
            $"rawGunReload={Fmt(result.WeightedGunReloadScore)}",
            $"rawGunRange={Fmt(result.WeightedGunRangeScore)}",
            $"rawGunAcc={Fmt(result.WeightedGunAccuracyScore)}",
            $"rawShipAcc={Fmt(result.ShipAccuracyScore)}",
            $"rawTorpRange={Fmt(result.WeightedTorpedoRange)}",
            $"rawTorpSpeed={Fmt(result.WeightedTorpedoSpeed)}",
            $"rawTorpReload={Fmt(result.WeightedTorpedoReloadScore)}",
            $"rawTorpAcc={Fmt(result.WeightedTorpedoAccuracyScore)}",
            $"gunGroups={result.GunGroupCount}",
            $"torpTubes={result.TorpedoTubeCount}",
            $"reason={NormalizeReason(result.Reason)}");

    private static PowerRejectDiagnosticSnapshot CapturePowerRejectDiagnostics(Ship ship)
        => new(
            ShipLevelDiagnostic(ship),
            DetailedGunSummary(ship),
            DetailedArmorSummary(ship),
            ComponentSnapshot(ship));

    private static string FormatPowerRejectDetails(
        PowerRejectDiagnosticSnapshot before,
        PowerRejectDiagnosticSnapshot after)
        => string.Join(
            ";",
            $"shipBefore={before.Ship}",
            $"shipAfter={after.Ship}",
            $"gunsBefore={before.Guns}",
            $"gunsAfter={after.Guns}",
            $"armorBefore={before.Armor}",
            $"armorAfter={after.Armor}",
            $"componentDeltas={ComponentDeltas(before.Components, after.Components)}");

    private static string ShipLevelDiagnostic(Ship ship)
        => string.Join(
            ",",
            $"firepower={Fmt(Safe(() => ship.GetFirepower(), 0f))}",
            $"projection={Fmt(Safe(() => ship.PowerProjection(true), 0f))}",
            $"weight={Fmt(CurrentWeight(ship))}",
            $"tons={Fmt(Safe(() => ship.Tonnage(), 0f))}");

    private static string DetailedGunSummary(Ship ship)
    {
        try
        {
            List<GunGroupDiagnostic> groups = DetailedGunGroups(ship)
                .OrderByDescending(static group => group.IsMain)
                .ThenByDescending(group => group.Caliber)
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groups.Count == 0)
                return "none";

            List<string> entries = groups
                .Take(10)
                .Select(FormatGunGroupDiagnostic)
                .ToList();

            int hidden = Math.Max(0, groups.Count - entries.Count);
            if (hidden > 0)
                entries.Add($"+{hidden}groups");

            return string.Join("|", entries);
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static List<GunGroupDiagnostic> DetailedGunGroups(Ship ship)
    {
        Dictionary<long, (PartData Data, int Count, bool IsMain)> groups = new();
        foreach (Part part in ShipParts(ship))
        {
            if (!IsGunPart(part))
                continue;

            PartData? data = Safe(() => part.data, null);
            if (data == null)
                continue;

            long key = PartDataKey(data);
            bool isMain = Safe(() => ship.IsMainCal(part), Safe(() => ship.IsMainCal(data), false));
            groups.TryGetValue(key, out (PartData Data, int Count, bool IsMain) existing);
            groups[key] = (data, existing.Count + 1, existing.IsMain || isMain);
        }

        List<GunGroupDiagnostic> result = new();
        foreach ((PartData data, int count, bool isMain) in groups.Values)
        {
            int barrels = Math.Max(1, Safe(() => data.barrels, 1));
            float caliber = GunCaliberInches(ship, data);
            int grade = Math.Max(1, Safe(() => ship.TechGunGrade(data, true), 1));
            float reload = Math.Max(1f, Safe(() => Part.WeaponReloadTime(data, ship), 1f));
            float range = Math.Max(1f, Safe(() => Part.WeaponRange(data, ship, null), 1f));
            string gunId = SafeString(() => data.GetGunDataId(ship));
            float damageMod = GunDamageMod(ship, data, gunId);
            float vpWeight = Math.Max(0.01f, count * barrels * caliber * caliber * damageMod);
            result.Add(new GunGroupDiagnostic(
                LogToken(SafeString(() => data.name)),
                LogToken(SafeString(() => data.nameUi)),
                LogToken(gunId),
                count,
                barrels,
                caliber,
                grade,
                reload,
                range,
                damageMod,
                vpWeight,
                isMain));
        }

        return result;
    }

    private static string FormatGunGroupDiagnostic(GunGroupDiagnostic group)
        => string.Join(
            ",",
            group.Name,
            $"ui={group.UiName}",
            $"n={group.Count}",
            $"barrels={group.Barrels}",
            $"cal={Fmt(group.Caliber)}",
            $"grade={group.Grade}",
            $"reload={Fmt(group.Reload)}",
            $"range={Fmt(group.Range)}",
            $"dmg={Fmt(group.DamageMod)}",
            $"vpW={Fmt(group.VpWeight)}",
            $"main={(group.IsMain ? "Y" : "N")}",
            $"id={group.GunDataId}");

    private static float GunCaliberInches(Ship ship, PartData data)
    {
        float caliber = Safe(() => data.GetCaliberInch(ship), 0f);
        if (caliber > 0f)
            return caliber > 25f ? caliber / 25.4f : caliber;

        caliber = Safe(() => data.caliber, 1f);
        return caliber > 25f ? caliber / 25.4f : Math.Max(1f, caliber);
    }

    private static float GunDamageMod(Ship ship, PartData data, string gunId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(gunId) &&
                !string.Equals(gunId, "<empty>", StringComparison.Ordinal) &&
                G.GameData?.guns != null &&
                G.GameData.guns.TryGetValue(gunId, out GunData gunData))
            {
                return Math.Max(0.25f, Safe(() => gunData.DamageMod(ship, data), Safe(() => gunData.damageMod, 1f)));
            }
        }
        catch
        {
        }

        return 1f;
    }

    private static string DetailedArmorSummary(Ship ship)
    {
        try
        {
            string hull = string.Join(
                ",",
                $"belt={Fmt(ArmorInches(ship, Ship.A.Belt))}",
                $"foreBelt={Fmt(ArmorInches(ship, Ship.A.BeltBow))}",
                $"aftBelt={Fmt(ArmorInches(ship, Ship.A.BeltStern))}",
                $"deck={Fmt(ArmorInches(ship, Ship.A.Deck))}",
                $"foreDeck={Fmt(ArmorInches(ship, Ship.A.DeckBow))}",
                $"aftDeck={Fmt(ArmorInches(ship, Ship.A.DeckStern))}",
                $"conning={Fmt(ArmorInches(ship, Ship.A.ConningTower))}",
                $"super={Fmt(ArmorInches(ship, Ship.A.Superstructure))}");
            return $"hull({hull}) turrets({DetailedTurretArmorSummary(ship)})";
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static float ArmorInches(Ship ship, Ship.A zone)
    {
        try
        {
            var armor = ship.armor;
            if (armor != null && armor.TryGetValue(zone, out float raw))
                return raw / 25.4f;
        }
        catch
        {
        }

        return 0f;
    }

    private static string DetailedTurretArmorSummary(Ship ship)
    {
        try
        {
            List<string> rows = new();
            int total = 0;
            if (ship.shipTurretArmor != null)
            {
                foreach (Ship.TurretArmor row in ship.shipTurretArmor)
                {
                    if (row?.turretPartData == null)
                        continue;

                    total++;
                    if (rows.Count >= 8)
                        continue;

                    PartData data = row.turretPartData;
                    string mount = Safe(() => row.isCasemateGun, false) ? "casemate" : "turret";
                    rows.Add(string.Join(
                        ",",
                        LogToken(SafeString(() => data.name)),
                        mount,
                        $"cal={Fmt(GunCaliberInches(ship, data))}",
                        $"side={Fmt(Safe(() => row.sideTurretArmor, 0f) / 25.4f)}",
                        $"top={Fmt(Safe(() => row.topTurretArmor, 0f) / 25.4f)}",
                        $"barb={Fmt(Safe(() => row.barbetteArmor, 0f) / 25.4f)}"));
                }
            }

            if (rows.Count == 0)
                return "none";

            int hidden = Math.Max(0, total - rows.Count);
            if (hidden > 0)
                rows.Add($"+{hidden}rows");

            return string.Join("|", rows);
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static Dictionary<string, string> ComponentSnapshot(Ship ship)
    {
        Dictionary<string, string> snapshot = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var components = ship.components;
            if (components == null)
                return snapshot;

            foreach (var pair in components)
            {
                string slot = ComponentSlotLabel(pair.Key, pair.Value);
                string component = ComponentLabel(pair.Value);
                if (!string.IsNullOrWhiteSpace(slot))
                    snapshot[slot] = component;
            }
        }
        catch
        {
        }

        return snapshot;
    }

    private static string ComponentDeltas(
        Dictionary<string, string> before,
        Dictionary<string, string> after)
    {
        try
        {
            List<string> keys = before.Keys
                .Concat(after.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> deltas = new();
            int changed = 0;
            foreach (string key in keys)
            {
                before.TryGetValue(key, out string? beforeValue);
                after.TryGetValue(key, out string? afterValue);
                beforeValue ??= "none";
                afterValue ??= "none";
                if (string.Equals(beforeValue, afterValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                changed++;
                if (deltas.Count < 12)
                    deltas.Add($"{LogToken(key)}:{LogToken(beforeValue)}->{LogToken(afterValue)}");
            }

            if (changed == 0)
                return "none";

            int hidden = Math.Max(0, changed - deltas.Count);
            if (hidden > 0)
                deltas.Add($"+{hidden}slots");

            return string.Join("|", deltas);
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static string ComponentSlotLabel(CompType? slot, ComponentData? component)
    {
        string label = SafeString(() => slot?.name);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        label = SafeString(() => slot?.nameUi);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        return SafeString(() => component?.type);
    }

    private static string ComponentLabel(ComponentData? component)
    {
        string label = SafeString(() => component?.name);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        label = SafeString(() => component?.nameShort);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        return SafeString(() => component?.nameUi);
    }

    private static string GunPartSummary(Ship ship)
    {
        try
        {
            List<string> groups = ShipParts(ship)
                .Where(IsGunPart)
                .GroupBy(part => SafeString(() => part.data?.name))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(group => $"{LogToken(group.Key)}:{group.Count()}")
                .ToList();

            if (groups.Count == 0)
                return "none";

            int totalGroups = ShipParts(ship)
                .Where(IsGunPart)
                .Select(part => SafeString(() => part.data?.name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int hidden = Math.Max(0, totalGroups - groups.Count);
            if (hidden > 0)
                groups.Add($"+{hidden}groups");

            return string.Join("|", groups);
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        string normalized = reason.Trim().Trim('$');
        normalized = normalized.Replace("Ui_Constr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static int MainGunCount(Ship ship)
        => ShipParts(ship).Count(part => IsGunPart(part) && Safe(() => ship.IsMainCal(part), Safe(() => ship.IsMainCal(part.data), false)));

    private static bool IsGunPart(Part part)
        => Safe(() => part.data?.isGun ?? false, false);

    private static InheritedPartSet CaptureInheritedNonGunParts(Ship ship)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (Part part in ShipParts(ship))
        {
            if (part == null || IsGunPart(part))
                continue;

            string identity = InheritedPartIdentity(part);
            if (!string.IsNullOrWhiteSpace(identity))
                identities.Add(identity);
        }

        return new InheritedPartSet(identities);
    }

    private static string InheritedPartIdentity(Part? part)
    {
        if (part == null)
            return string.Empty;

        PartData? data = Safe(() => part.data, null);
        Part.Store? store = Safe(() => part.ToStore(), null);
        if (data == null || store == null)
            return string.Empty;

        return string.Join(
            "|",
            PartDataKey(data).ToString(CultureInfo.InvariantCulture),
            SafeString(() => data.name),
            SafeString(() => store.name),
            SafeString(() => store.Id.ToString()),
            StorePositionKey(store),
            StoreRotationKey(store));
    }

    private static string StorePositionKey(Part.Store store)
        => string.Join(
            ",",
            Fmt(Safe(() => store.position.x, 0f)),
            Fmt(Safe(() => store.position.y, 0f)),
            Fmt(Safe(() => store.position.z, 0f)));

    private static string StoreRotationKey(Part.Store store)
        => string.Join(
            ",",
            Fmt(Safe(() => store.rotation.x, 0f)),
            Fmt(Safe(() => store.rotation.y, 0f)),
            Fmt(Safe(() => store.rotation.z, 0f)),
            Fmt(Safe(() => store.rotation.w, 0f)));

    private static bool TryFindSnapshot(List<TurretCaliberSnapshot> snapshots, PartData? data, bool isCasemate, out TurretCaliberSnapshot result)
    {
        foreach (TurretCaliberSnapshot snapshot in snapshots)
        {
            if (!snapshot.Matches(data, isCasemate))
                continue;

            result = snapshot;
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryFindSnapshot(List<TurretArmorSnapshot> snapshots, PartData? data, bool isCasemate, out TurretArmorSnapshot result)
    {
        foreach (TurretArmorSnapshot snapshot in snapshots)
        {
            if (!snapshot.Matches(data, isCasemate))
                continue;

            result = snapshot;
            return true;
        }

        result = default;
        return false;
    }

    private static List<Part> ShipParts(Ship ship)
    {
        List<Part> parts = new();
        try
        {
            if (ship.parts == null)
                return parts;

            foreach (Part part in ship.parts)
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

    private static float CurrentWeight(Ship ship)
        => Safe(() => ship.Weight(true, true), 0f);

    private static void Recalculate(Ship ship)
    {
        try { ship.CalcWeightAndCost(true, true); }
        catch { }
    }

    private static long PartDataKey(PartData? partData)
        => Safe(() => partData?.Pointer.ToInt64() ?? 0L, 0L);

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private static string SafeString(Func<string?> read)
        => Safe(() => read() ?? "<empty>", "<empty>");

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "?" : value.Trim().Replace(' ', '_');

    private readonly record struct StoredGunRefreshPart(Part.Store Store, GunBarrelSnapshot Barrel);

    private readonly record struct GunBarrelSnapshot(
        string StoreId,
        string StoreName,
        int Index,
        long PartDataKey,
        string PartDataName,
        string GunDataId,
        float Caliber,
        int Barrels,
        float BarrelLength,
        int CaliberLength,
        int TechGrade,
        string BarrelText)
    {
        internal bool HasUsableLength => CaliberLength > 0 || Math.Abs(BarrelLength) > 0.0001f;

        internal bool MatchesData(PartData? data)
            => data != null &&
               ((PartDataKey != 0L && PartDataKey == SmartRefitService.PartDataKey(data)) ||
                string.Equals(PartDataName, Safe(() => data.name, string.Empty), StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct GunBarrelRestoreState(
        float PostCreateBarrelLength,
        int PostCreateCaliberLength,
        bool Restored);

    private readonly record struct GunBarrelRestoreTarget(Part Part, GunBarrelSnapshot Snapshot);

    private readonly record struct GunRefreshSummary(
        int OriginalGuns,
        int Stores,
        int StoreNull,
        int CreateNull,
        int AddFailed,
        int LoadModelFailed,
        int CanPlaceFailed,
        int Recreated,
        string Context,
        bool LoadVisualModels,
        bool PartsContAvailable,
        bool IsConstructor,
        string CanPlaceReasons,
        string CaliberFallbacks,
        string BarrelRestores,
        int UnloadedOriginalGunModels,
        int UnloadedInvalidRecreatedModels,
        int UnloadFailures,
        int RemoveFailures,
        bool Skipped)
    {
        internal int Invalid => AddFailed + CanPlaceFailed;

        internal string Summary =>
            Skipped
                ? $"skipped=true guns={OriginalGuns} context={Context} modelLoad={LoadVisualModels} " +
                  $"partsCont={(PartsContAvailable ? "yes" : "no")} constructor={IsConstructor}"
                : $"guns={OriginalGuns} stores={Stores} storeNull={StoreNull} createNull={CreateNull} " +
                  $"addFail={AddFailed} loadModelFail={LoadModelFailed} canPlaceFail={CanPlaceFailed} " +
                  $"invalid={Invalid} recreated={Recreated} context={Context} modelLoad={LoadVisualModels} " +
                  $"partsCont={(PartsContAvailable ? "yes" : "no")} constructor={IsConstructor} canPlaceReasons={CanPlaceReasons} " +
                  $"caliberFallback={CaliberFallbacks} barrelRestore={BarrelRestores} " +
                  $"unloadedOriginalGunModels={UnloadedOriginalGunModels} unloadedInvalidRecreatedModels={UnloadedInvalidRecreatedModels} " +
                  $"unloadFailures={UnloadFailures} removeFailures={RemoveFailures}";

        internal static GunRefreshSummary Empty(SmartRefitOptions options, Ship ship)
            => new(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                options.Context,
                options.LoadVisualModels,
                Safe(() => ship.partsCont != null, false),
                GameManager.IsConstructor,
                "none",
                "none",
                "none",
                0,
                0,
                0,
                0,
                false);

        internal static GunRefreshSummary CreateSkipped(SmartRefitOptions options, Ship ship)
            => new(
                ShipParts(ship).Count(IsGunPart),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                options.Context,
                options.LoadVisualModels,
                Safe(() => ship.partsCont != null, false),
                GameManager.IsConstructor,
                "none",
                "none",
                "none",
                0,
                0,
                0,
                0,
                true);
    }

    private readonly record struct ValidationStatus(bool Valid, string Reason);

    private sealed class InheritedPartSet
    {
        internal static InheritedPartSet Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

        private readonly HashSet<string> identities;

        internal InheritedPartSet(HashSet<string> identities)
        {
            this.identities = identities;
        }

        internal bool Contains(Part? part)
        {
            string identity = InheritedPartIdentity(part);
            return !string.IsNullOrWhiteSpace(identity) && identities.Contains(identity);
        }
    }

    private readonly record struct ReqPartsValidation(bool Valid, string Details, int WaivedBadParts);

    private readonly record struct BadPartValidation(
        Part? Part,
        string Reason,
        bool InheritedBaseline,
        bool TouchedBySmartRefit,
        bool Waived,
        string Availability);

    private readonly record struct SmartRefitOptions(
        string Context,
        bool LoadVisualModels,
        bool StrictPowerGate,
        bool RefreshGunParts,
        bool RebalanceArmor,
        bool ValidateDesign,
        bool UseStoreRollback,
        bool RequirePowerImprovement)
    {
        internal bool ComponentOnlyManual =>
            !RefreshGunParts &&
            !RebalanceArmor &&
            !ValidateDesign &&
            !UseStoreRollback &&
            !RequirePowerImprovement;

        internal bool UnloadModelsDuringRollback => RefreshGunParts || LoadVisualModels;

        internal static SmartRefitOptions Manual { get; } = new("manual", false, false, false, false, false, false, false);

        internal static SmartRefitOptions Ai { get; } = new("ai-real", true, true, true, true, true, true, true);
    }

    private readonly record struct PowerRejectDiagnosticSnapshot(
        string Ship,
        string Guns,
        string Armor,
        Dictionary<string, string> Components);

    private readonly record struct GunGroupDiagnostic(
        string Name,
        string UiName,
        string GunDataId,
        int Count,
        int Barrels,
        float Caliber,
        int Grade,
        float Reload,
        float Range,
        float DamageMod,
        float VpWeight,
        bool IsMain);

    private readonly record struct PowerSnapshot(bool Success, EffectivePowerResult Result, string Reason)
    {
        internal float AdjustedPower => Success ? Result.AdjustedPower : 0f;

        internal string Text => Success
            ? ShipEffectivePowerCalculator.FormatCompactPower(Result.AdjustedPower)
            : "unknown(" + Reason + ")";

        internal string Summary => Success
            ? PowerResultBreakdown(Result)
            : "unavailable:reason=" + Reason;

        internal static PowerSnapshot Successful(EffectivePowerResult result)
            => new(true, result, NormalizeReason(result.Reason));

        internal static PowerSnapshot Failed(string reason)
            => new(false, default, reason);
    }

    private readonly record struct PowerGateResult(bool Accept, string Text);

    private readonly record struct TurretCaliberSnapshot(
        long PartDataKey,
        string PartDataName,
        bool IsCasemate,
        float Diameter,
        int Length)
    {
        internal bool Matches(PartData? data, bool isCasemate)
            => IsCasemate == isCasemate &&
               ((PartDataKey != 0L && PartDataKey == SmartRefitService.PartDataKey(data)) ||
                string.Equals(PartDataName, Safe(() => data?.name ?? string.Empty, string.Empty), StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct TurretArmorSnapshot(
        long PartDataKey,
        string PartDataName,
        bool IsCasemate,
        float Side,
        float Top,
        float Barbette)
    {
        internal bool Matches(PartData? data, bool isCasemate)
            => IsCasemate == isCasemate &&
               ((PartDataKey != 0L && PartDataKey == SmartRefitService.PartDataKey(data)) ||
                string.Equals(PartDataName, Safe(() => data?.name ?? string.Empty, string.Empty), StringComparison.OrdinalIgnoreCase));
    }
}

internal readonly record struct SmartRefitResult(bool Success, string Message)
{
    internal static SmartRefitResult Accepted(string message)
        => new(true, message);

    internal static SmartRefitResult Rejected(string message)
        => new(false, message);
}
