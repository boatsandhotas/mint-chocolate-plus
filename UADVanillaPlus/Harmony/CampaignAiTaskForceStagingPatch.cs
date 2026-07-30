using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.Services;

namespace UADVanillaPlus.Harmony;

// Runs a narrow AI-only staging pass after vanilla finishes moving one
// nation's task forces. The service does not persist state to saves; it only
// redirects compatible moving groups so vanilla can merge them naturally.
[HarmonyPatch]
internal static class CampaignAiTaskForceStagingMoveVesselsPatch
{
    private const string LogPrefix = "UADVP ai taskforce staging";

    private static Type? stateMachineType;
    private static MethodInfo? moveNextMethod;
    private static MemberAccessor? campaignAccessor;
    private static MemberAccessor? playerAccessor;
    private static bool resolved;
    private static string lastFailure = string.Empty;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        ResolveTarget();
        string typeName = stateMachineType == null ? "missing" : stateMachineType.Name;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} proof: MoveVessels target={(moveNextMethod != null ? "yes" : "no")} " +
            $"stateMachine={typeName} campaignAccessor={campaignAccessor?.Description ?? "missing"} playerAccessor={playerAccessor?.Description ?? "missing"}.");

        return moveNextMethod != null && campaignAccessor != null && playerAccessor != null;
    }

    private static MethodBase? TargetMethod()
    {
        ResolveTarget();
        return moveNextMethod;
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, bool __result)
    {
        if (__result)
            return;

        try
        {
            CampaignController? campaign = campaignAccessor?.Get(__instance) as CampaignController;
            Player? player = playerAccessor?.Get(__instance) as Player;
            CampaignAiTaskForceStagingService.AfterMoveVesselsCompleted(campaign, player);
        }
        catch (Exception ex)
        {
            LogFailureOnce("MoveVessels postfix", ex);
        }
    }

    private static void ResolveTarget()
    {
        if (resolved)
            return;

        resolved = true;
        stateMachineType = typeof(CampaignController)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(static type =>
            {
                if (!type.Name.Contains("MoveVessels", StringComparison.Ordinal))
                    return false;

                MethodInfo? moveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return moveNext != null &&
                       moveNext.ReturnType == typeof(bool) &&
                       moveNext.GetParameters().Length == 0;
            });

        if (stateMachineType == null)
            return;

        moveNextMethod = stateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        campaignAccessor = MemberAccessor.ResolveAnyAssignable(stateMachineType, typeof(CampaignController), "<>4__this", "__4__this");
        playerAccessor = MemberAccessor.ResolveAnyAssignable(stateMachineType, typeof(Player), "player");
    }

    private static void LogFailureOnce(string action, Exception ex)
    {
        string key = $"{action}:{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, key, StringComparison.Ordinal))
            return;

        lastFailure = key;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: {action} failed; leaving vanilla task-force movement intact. {ex.GetType().Name}: {ex.Message}");
    }

    private sealed class MemberAccessor
    {
        private readonly FieldInfo? field;
        private readonly PropertyInfo? property;
        private readonly MethodInfo? getter;

        private MemberAccessor(string kind, string name, FieldInfo? field = null, PropertyInfo? property = null, MethodInfo? getter = null)
        {
            Kind = kind;
            Name = name;
            this.field = field;
            this.property = property;
            this.getter = getter;
        }

        internal string Kind { get; }
        internal string Name { get; }
        internal string Description => $"{Kind}:{Name}";

        internal static MemberAccessor? Resolve(Type owner, string name, Type expectedType)
        {
            FieldInfo? field = AccessTools.Field(owner, name);
            if (field != null && expectedType.IsAssignableFrom(field.FieldType))
                return new MemberAccessor("field", field.Name, field: field);

            PropertyInfo? property = owner.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetGetMethod(true) != null && expectedType.IsAssignableFrom(property.PropertyType))
                return new MemberAccessor("property", property.Name, property: property);

            MethodInfo? getter = AccessTools.Method(owner, "get_" + name, Type.EmptyTypes);
            if (getter != null && expectedType.IsAssignableFrom(getter.ReturnType))
                return new MemberAccessor("getter", getter.Name, getter: getter);

            return null;
        }

        internal static MemberAccessor? ResolveAnyAssignable(Type owner, Type expectedType, params string[] preferredNames)
        {
            foreach (string preferredName in preferredNames)
            {
                MemberAccessor? preferred = Resolve(owner, preferredName, expectedType);
                if (preferred != null)
                    return preferred;
            }

            List<FieldInfo> fields = owner
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => expectedType.IsAssignableFrom(field.FieldType))
                .ToList();
            if (fields.Count == 1)
                return new MemberAccessor("field", fields[0].Name, field: fields[0]);

            List<PropertyInfo> properties = owner
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property => property.GetGetMethod(true) != null && expectedType.IsAssignableFrom(property.PropertyType))
                .ToList();
            if (properties.Count == 1)
                return new MemberAccessor("property", properties[0].Name, property: properties[0]);

            List<MethodInfo> getters = owner
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method =>
                    method.Name.StartsWith("get_", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0 &&
                    expectedType.IsAssignableFrom(method.ReturnType))
                .ToList();
            if (getters.Count == 1)
                return new MemberAccessor("getter", getters[0].Name, getter: getters[0]);

            return null;
        }

        internal object? Get(object instance)
        {
            if (field != null)
                return field.GetValue(instance);
            if (property != null)
                return property.GetValue(instance);
            return getter?.Invoke(instance, Array.Empty<object>());
        }
    }
}

[HarmonyPatch]
internal static class CampaignAiTaskForceStagingMergePatch
{
    private const string LogPrefix = "UADVP ai taskforce staging";

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: MergeShipGroups target not found; merge observation disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(
            typeof(CampaignController.Data),
            nameof(CampaignController.Data.MergeShipGroups),
            new[] { typeof(CampaignController.TaskForce), typeof(CampaignController.TaskForce) });

    [HarmonyPrefix]
    private static void Prefix(
        CampaignController.TaskForce mergeTo,
        CampaignController.TaskForce group,
        ref CampaignAiTaskForceStagingService.MergeSnapshot __state)
    {
        __state = CampaignAiTaskForceStagingService.BeforeMerge(mergeTo, group);
    }

    [HarmonyPostfix]
    private static void Postfix(
        CampaignController.TaskForce mergeTo,
        CampaignController.TaskForce group,
        CampaignAiTaskForceStagingService.MergeSnapshot __state)
    {
        CampaignAiTaskForceStagingService.AfterMerge(mergeTo, group, __state);
    }
}

[HarmonyPatch]
internal static class CampaignAiTaskForceStagingRemovePatch
{
    private const string LogPrefix = "UADVP ai taskforce staging";

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: RemoveTaskForce target not found; remove observation disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(
            typeof(CampaignController.Data),
            nameof(CampaignController.Data.RemoveTaskForce),
            new[] { typeof(CampaignController.TaskForce), typeof(bool) });

    [HarmonyPrefix]
    private static void Prefix(CampaignController.TaskForce group, bool returnFromSea)
        => CampaignAiTaskForceStagingService.BeforeRemove(group, returnFromSea);
}

[HarmonyPatch]
internal static class CampaignAiTaskForceStagingSummaryNextTurnPatch
{
    private const string LogPrefix = "UADVP ai taskforce staging";

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: NextTurn MoveNext target not found; turn summary flush will be skipped.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignSharedDesignDiagnosticsPatch.NextTurnMoveNextTarget();

    [HarmonyPostfix]
    private static void Postfix(bool __result)
    {
        if (!__result)
            CampaignAiTaskForceStagingService.FlushPendingTurnSummary();
    }
}
