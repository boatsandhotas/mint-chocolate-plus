using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.Harmony;

// Keeps the player-facing designer Auto Design button from burning through
// multiple attempts and wiping the partially generated layout when it fails.
[HarmonyPatch]
internal static class DesignRandomShipSingleAttemptPatch
{
    private const string LogPrefix = "UADMC designer auto-design";

    private static readonly Dictionary<Type, CallbackFields> CallbackFieldsByType = new();
    private static readonly Dictionary<Type, StateMachineFields> StateMachineFieldsByType = new();
    private static readonly HashSet<string> LoggedStateMachines = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedMoveNextStates = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ActiveDesignerStateMachines = new(StringComparer.Ordinal);
    private static readonly MethodInfo? OnConShipChangedMethod =
        AccessTools.Method(typeof(Ui), "OnConShipChanged", new[] { typeof(bool) });

    private static int pendingDesignerRandomShipCount;
    private static bool loggedMoveNextTarget;
    private static bool loggedRandomShipTarget;
    private static bool loggedCallbackTarget;
    private static bool loggedEndAutodesignTarget;

    internal static MethodBase? UiRandomShipTarget()
    {
        MethodInfo? exact = AccessTools.Method(
            typeof(Ui),
            nameof(Ui.RandomShip),
            new[] { typeof(bool), typeof(Action<bool, int, float>), typeof(bool), typeof(bool) });
        if (exact != null)
            return exact;

        return typeof(Ui)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(static method =>
            {
                if (method.Name != nameof(Ui.RandomShip) || method.ReturnType != typeof(void))
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(bool) &&
                    parameters[2].ParameterType == typeof(bool) &&
                    parameters[3].ParameterType == typeof(bool);
            });
    }

    internal static MethodBase? GenerateRandomShipMoveNextTarget()
    {
        return typeof(Ship)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => new
            {
                Type = type,
                MoveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            })
            .Where(candidate =>
                candidate.MoveNext != null &&
                candidate.Type.Name.Contains("GenerateRandomShip", StringComparison.Ordinal) &&
                candidate.MoveNext.ReturnType == typeof(bool) &&
                candidate.MoveNext.GetParameters().Length == 0)
            .Select(candidate => candidate.MoveNext)
            .FirstOrDefault();
    }

    internal static MethodBase? EndAutodesignTarget()
        => AccessTools.Method(typeof(GameManager), nameof(GameManager.EndAutodesign));

    internal static MethodBase? RandomShipCallbackTarget()
        => typeof(Ui)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .FirstOrDefault(static method =>
            {
                if (!method.Name.Contains("RandomShip", StringComparison.Ordinal) ||
                    !method.Name.EndsWith("b__0", StringComparison.Ordinal) ||
                    method.ReturnType != typeof(void))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(bool) &&
                    parameters[1].ParameterType == typeof(int) &&
                    parameters[2].ParameterType == typeof(float);
            });

    internal static bool IsUiRandomShipCallback(object? callback)
    {
        object? target = DelegateTarget(callback);
        if (target == null)
            return false;

        return IsUiRandomShipCallbackObject(target);
    }

    internal static bool IsUiRandomShipCallbackObject(object? target)
    {
        Type? type = target?.GetType();
        return type != null &&
            type.DeclaringType == typeof(Ui) &&
            type.Name.Contains("DisplayClass", StringComparison.Ordinal) &&
            type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(static method => method.Name.Contains("RandomShip", StringComparison.Ordinal));
    }

    internal static void MarkDesignerRandomShipStart(bool fromUi, bool isRandomActive)
    {
        if (!fromUi || !isRandomActive)
            return;

        pendingDesignerRandomShipCount++;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"{LogPrefix}: Auto Design start marked pending={pendingDesignerRandomShipCount}.");
    }

    internal static void ForceSingleAttemptForUiRandomShipState(object? stateMachine, string phase)
    {
        if (stateMachine == null)
            return;

        try
        {
            StateMachineFields fields = ResolveStateMachineFields(stateMachine.GetType());
            object? onDone = fields.OnDone!.GetValue(stateMachine);
            bool recognizedCallback = IsUiRandomShipCallback(onDone);
            string key = ObjectKey(stateMachine);
            object? state = fields.State?.GetValue(stateMachine);
            object? tryN = fields.TryN?.GetValue(stateMachine);
            object? beforeUseSmall = fields.UseSmallAmountTries!.GetValue(stateMachine);
            object? beforeTries = fields.TriesTotal!.GetValue(stateMachine);
            bool alreadyActive = ActiveDesignerStateMachines.Contains(key);
            bool claimedPending = false;
            bool activeDesigner = recognizedCallback || alreadyActive;

            if (!activeDesigner && pendingDesignerRandomShipCount > 0)
            {
                pendingDesignerRandomShipCount = Math.Max(0, pendingDesignerRandomShipCount - 1);
                ActiveDesignerStateMachines.Add(key);
                claimedPending = true;
                activeDesigner = true;
            }
            else if (recognizedCallback)
            {
                ActiveDesignerStateMachines.Add(key);
            }

            if ((activeDesigner || recognizedCallback || pendingDesignerRandomShipCount > 0) &&
                LoggedMoveNextStates.Add($"{phase}:{key}"))
            {
                string onDoneType = onDone?.GetType().FullName ?? "null";
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"{LogPrefix}: MoveNext seen phase={phase} state={state ?? "?"} try={tryN ?? "?"} triesTotal={beforeTries ?? "?"} useSmall={beforeUseSmall ?? "?"} onDoneType={onDoneType} recognized={recognizedCallback} activeDesigner={activeDesigner} pending={pendingDesignerRandomShipCount}.");
            }

            if (!activeDesigner)
                return;

            if (claimedPending)
            {
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"{LogPrefix}: claimed pending Auto Design generator state={key} phase={phase} stateValue={state ?? "?"}.");
            }

            bool changed = false;
            if (beforeUseSmall is bool useSmall && !useSmall)
            {
                fields.UseSmallAmountTries.SetValue(stateMachine, true);
                changed = true;
            }

            if (beforeTries is int triesTotal && triesTotal > 1)
            {
                fields.TriesTotal.SetValue(stateMachine, 1);
                changed = true;
            }

            if (changed || LoggedStateMachines.Add(key))
            {
                object? afterUseSmall = fields.UseSmallAmountTries.GetValue(stateMachine);
                object? afterTries = fields.TriesTotal.GetValue(stateMachine);
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"{LogPrefix}: capped Auto Design generator phase={phase} state={state ?? "?"} try={tryN ?? "?"} useSmallAmountTries={beforeUseSmall}->{afterUseSmall} triesTotal={beforeTries}->{afterTries}.");
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to cap Auto Design generator. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void CompleteSingleAttemptState(object? stateMachine, bool moveNextResult)
    {
        if (moveNextResult || stateMachine == null)
            return;

        string key = ObjectKey(stateMachine);
        if (ActiveDesignerStateMachines.Remove(key))
        {
            Melon<MintChipPlusMod>.Logger.Msg(
                $"{LogPrefix}: Auto Design generator completed state={key}.");
        }
    }

    internal static bool TryHandleFailedUiCallback(object callbackInstance, bool result, int triesCount, float buildTime)
    {
        if (!IsUiRandomShipCallbackObject(callbackInstance))
            return false;

        ClearDesignerRandomShipContext("callback");
        Melon<MintChipPlusMod>.Logger.Msg(
            $"{LogPrefix}: callback result={result} tries={Math.Max(1, triesCount)} buildTime={buildTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} skipVanillaClear={!result}.");

        if (result)
            return false;

        try
        {
            CallbackFields fields = ResolveCallbackFields(callbackInstance.GetType());
            Ui? ui = fields.UiField?.GetValue(callbackInstance) as Ui;
            Delegate? externalOnDone = fields.OnDoneField?.GetValue(callbackInstance) as Delegate;

            try { GameManager.HideShipBuildingOverlay(); }
            catch { }

            try { G.sound?.PlayUi("con_generate_ship"); }
            catch { }

            if (ui != null)
            {
                try { OnConShipChangedMethod?.Invoke(ui, new object[] { false }); }
                catch (Exception ex)
                {
                    Melon<MintChipPlusMod>.Logger.Warning(
                        $"{LogPrefix}: failed to refresh constructor ship state after failed attempt. {ex.GetType().Name}: {ex.Message}");
                }

                try { ui.Refresh(false); }
                catch (Exception ex)
                {
                    Melon<MintChipPlusMod>.Logger.Warning(
                        $"{LogPrefix}: failed to refresh designer after failed attempt. {ex.GetType().Name}: {ex.Message}");
                }
            }

            try { externalOnDone?.DynamicInvoke(false, triesCount, buildTime); }
            catch (Exception ex)
            {
                Melon<MintChipPlusMod>.Logger.Warning(
                    $"{LogPrefix}: external failure callback failed. {ex.GetType().Name}: {ex.Message}");
            }

            Melon<MintChipPlusMod>.Logger.Msg(
                $"{LogPrefix}: kept failed Auto Design result after {Math.Max(1, triesCount)} attempt(s).");
            return true;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed callback override failed; falling back to vanilla clear behavior. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static void ClearDesignerRandomShipContext(string reason)
    {
        int pending = pendingDesignerRandomShipCount;
        int active = ActiveDesignerStateMachines.Count;
        pendingDesignerRandomShipCount = 0;
        ActiveDesignerStateMachines.Clear();

        if (pending > 0 || active > 0)
        {
            Melon<MintChipPlusMod>.Logger.Msg(
                $"{LogPrefix}: cleared Auto Design context reason={reason} pending={pending} activeStates={active}.");
        }
    }

    internal static void LogRandomShipTargetAttached()
    {
        if (loggedRandomShipTarget)
            return;

        loggedRandomShipTarget = true;
        Melon<MintChipPlusMod>.Logger.Msg($"{LogPrefix}: attached Ui.RandomShip designer marker.");
    }

    internal static void LogMoveNextTargetAttached()
    {
        if (loggedMoveNextTarget)
            return;

        loggedMoveNextTarget = true;
        Melon<MintChipPlusMod>.Logger.Msg($"{LogPrefix}: attached single-attempt MoveNext cap.");
    }

    internal static void LogCallbackTargetAttached()
    {
        if (loggedCallbackTarget)
            return;

        loggedCallbackTarget = true;
        Melon<MintChipPlusMod>.Logger.Msg($"{LogPrefix}: attached failure-retention callback.");
    }

    internal static void LogEndAutodesignTargetAttached()
    {
        if (loggedEndAutodesignTarget)
            return;

        loggedEndAutodesignTarget = true;
        Melon<MintChipPlusMod>.Logger.Msg($"{LogPrefix}: attached EndAutodesign cleanup.");
    }

    internal static void LogMissingTarget(string target)
        => Melon<MintChipPlusMod>.Logger.Warning($"{LogPrefix}: {target} target not found; designer Auto Design behavior remains vanilla.");

    private static StateMachineFields ResolveStateMachineFields(Type type)
    {
        if (StateMachineFieldsByType.TryGetValue(type, out StateMachineFields cached))
            return cached;

        StateMachineFields fields = new(
            ResolveMember(type, "<>1__state", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "__1__state", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "1__state", memberType => memberType == typeof(int)),
            ResolveMember(type, "onDone", memberType => IsDelegateLike(memberType)),
            ResolveMember(type, "useSmallAmountTries", memberType => memberType == typeof(bool)),
            ResolveMember(type, "<triesTotal>5__4", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "triesTotal", memberType => memberType == typeof(int)),
            ResolveMember(type, "<tryN>5__5", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "tryN", memberType => memberType == typeof(int)));

        if (!fields.IsValid)
            throw new MissingFieldException(type.FullName, "onDone/useSmallAmountTries/triesTotal");

        StateMachineFieldsByType[type] = fields;
        return fields;
    }

    private static CallbackFields ResolveCallbackFields(Type type)
    {
        if (CallbackFieldsByType.TryGetValue(type, out CallbackFields cached))
            return cached;

        FieldInfo? ui = AccessTools.Field(type, "<>4__this") ??
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(static field => field.FieldType == typeof(Ui));

        FieldInfo? onDone = AccessTools.Field(type, "onDone") ??
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(static field => typeof(Delegate).IsAssignableFrom(field.FieldType));

        CallbackFields fields = new(ui, onDone);
        CallbackFieldsByType[type] = fields;
        return fields;
    }

    private static MemberAccessor? ResolveMember(Type type, string name, Func<Type, bool> typePredicate)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && typePredicate(field.FieldType))
            return new MemberAccessor(field, null);

        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetMethod != null && typePredicate(property.PropertyType))
            return new MemberAccessor(null, property);

        field = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && typePredicate(candidate.FieldType));
        if (field != null)
            return new MemberAccessor(field, null);

        property = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.GetMethod != null && candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && typePredicate(candidate.PropertyType));
        return property == null ? null : new MemberAccessor(null, property);
    }

    private static bool IsDelegateLike(Type type)
        => typeof(Delegate).IsAssignableFrom(type) ||
           string.Equals(type.FullName, "Il2CppSystem.Action`3", StringComparison.Ordinal) ||
           type.FullName?.Contains("Il2CppSystem.Action", StringComparison.Ordinal) == true ||
           type.FullName?.Contains("Il2CppSystem.Delegate", StringComparison.Ordinal) == true;

    private static object? DelegateTarget(object? callback)
    {
        if (callback == null)
            return null;

        if (callback is Delegate systemDelegate)
            return systemDelegate.Target;

        Type type = callback.GetType();
        PropertyInfo? targetProperty = type.GetProperty("Target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (targetProperty?.GetMethod != null)
            return targetProperty.GetValue(callback);

        FieldInfo? targetField = type.GetField("m_target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(field => field.Name.Contains("target", StringComparison.OrdinalIgnoreCase));
        return targetField?.GetValue(callback);
    }

    private static string ObjectKey(object value)
    {
        try
        {
            PropertyInfo? pointer = value.GetType().GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? pointerValue = pointer?.GetValue(value);
            if (pointerValue != null)
                return pointerValue.ToString() ?? RuntimeHelpers.GetHashCode(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        return RuntimeHelpers.GetHashCode(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private readonly record struct StateMachineFields(
        MemberAccessor? State,
        MemberAccessor? OnDone,
        MemberAccessor? UseSmallAmountTries,
        MemberAccessor? TriesTotal,
        MemberAccessor? TryN)
    {
        internal bool IsValid => OnDone != null && UseSmallAmountTries != null && TriesTotal != null;
    }

    private sealed class MemberAccessor
    {
        private readonly FieldInfo? field;
        private readonly PropertyInfo? property;

        internal MemberAccessor(FieldInfo? field, PropertyInfo? property)
        {
            this.field = field;
            this.property = property;
        }

        internal object? GetValue(object instance)
            => field != null ? field.GetValue(instance) : property?.GetValue(instance);

        internal void SetValue(object instance, object? value)
        {
            if (field != null)
                field.SetValue(instance, value);
            else if (property?.SetMethod != null)
                property.SetValue(instance, value);
        }
    }

    private readonly record struct CallbackFields(FieldInfo? UiField, FieldInfo? OnDoneField);
}

[HarmonyPatch]
internal static class DesignRandomShipUiRandomShipPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (available)
            DesignRandomShipSingleAttemptPatch.LogRandomShipTargetAttached();
        else
            DesignRandomShipSingleAttemptPatch.LogMissingTarget("Ui.RandomShip");

        return available;
    }

    private static MethodBase? TargetMethod()
        => DesignRandomShipSingleAttemptPatch.UiRandomShipTarget();

    [HarmonyPrefix]
    private static void Prefix(bool fromUI, bool isRandomActive)
        => DesignRandomShipSingleAttemptPatch.MarkDesignerRandomShipStart(fromUI, isRandomActive);
}

[HarmonyPatch]
internal static class DesignRandomShipMoveNextPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (available)
            DesignRandomShipSingleAttemptPatch.LogMoveNextTargetAttached();
        else
            DesignRandomShipSingleAttemptPatch.LogMissingTarget("GenerateRandomShip MoveNext");

        return available;
    }

    private static MethodBase? TargetMethod()
        => DesignRandomShipSingleAttemptPatch.GenerateRandomShipMoveNextTarget();

    [HarmonyPrefix]
    private static void Prefix(object __instance)
        => DesignRandomShipSingleAttemptPatch.ForceSingleAttemptForUiRandomShipState(__instance, "prefix");

    [HarmonyPostfix]
    private static void Postfix(object __instance, bool __result)
    {
        DesignRandomShipSingleAttemptPatch.ForceSingleAttemptForUiRandomShipState(__instance, "postfix");
        DesignRandomShipSingleAttemptPatch.CompleteSingleAttemptState(__instance, __result);
    }
}

[HarmonyPatch]
internal static class DesignRandomShipCallbackPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (available)
            DesignRandomShipSingleAttemptPatch.LogCallbackTargetAttached();
        else
            DesignRandomShipSingleAttemptPatch.LogMissingTarget("RandomShip callback");

        return available;
    }

    private static MethodBase? TargetMethod()
        => DesignRandomShipSingleAttemptPatch.RandomShipCallbackTarget();

    [HarmonyPrefix]
    private static bool Prefix(object __instance, bool result, int triesCount, float buildTime)
        => !DesignRandomShipSingleAttemptPatch.TryHandleFailedUiCallback(__instance, result, triesCount, buildTime);
}

[HarmonyPatch]
internal static class DesignRandomShipEndAutodesignPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (available)
            DesignRandomShipSingleAttemptPatch.LogEndAutodesignTargetAttached();
        else
            DesignRandomShipSingleAttemptPatch.LogMissingTarget("EndAutodesign");

        return available;
    }

    private static MethodBase? TargetMethod()
        => DesignRandomShipSingleAttemptPatch.EndAutodesignTarget();

    [HarmonyPostfix]
    private static void Postfix()
        => DesignRandomShipSingleAttemptPatch.ClearDesignerRandomShipContext("EndAutodesign");
}
