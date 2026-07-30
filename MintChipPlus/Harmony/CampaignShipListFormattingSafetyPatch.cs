using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using ShipTypeCountPair = Il2CppSystem.Collections.Generic.KeyValuePair<Il2Cpp.ShipType, int>;

namespace MintChipPlus.Harmony;

// Patch intent: protect vanilla campaign info formatting from malformed
// ship-count rows that contain null ShipType keys while leaving valid
// Ui.ShipsListToStr callers on the original vanilla formatter.
internal static class CampaignShipListFormattingSafety
{
    private static readonly HashSet<string> LoggedContexts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedNonSuppressedContexts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedResolvedTargets = new(StringComparer.Ordinal);

    internal static MethodBase? ShipsListToStrKeyValueOverload()
    {
        return typeof(Ui)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == nameof(Ui.ShipsListToStr))
            .Select(method => new
            {
                Method = method,
                Parameters = method.GetParameters()
            })
            .Where(candidate => candidate.Parameters.Length == 6)
            .OrderByDescending(candidate => candidate.Parameters[0].ParameterType.FullName?.Contains("KeyValuePair", StringComparison.Ordinal) == true)
            .Select(candidate => candidate.Method)
            .FirstOrDefault();
    }

    internal static MethodBase? ShipsListSorterLambda()
    {
        return ShipsListLambdaCandidates()
            .FirstOrDefault(method => method.ReturnType == typeof(float) &&
                                      IsShipTypeCountPairParameter(method));
    }

    internal static MethodBase? ShipsListFormatterLambda()
    {
        return ShipsListLambdaCandidates()
            .FirstOrDefault(method => method.ReturnType == typeof(string) &&
                                      IsShipTypeCountPairParameter(method));
    }

    private static IEnumerable<MethodInfo> ShipsListLambdaCandidates()
    {
        return typeof(Ui)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.Name.IndexOf("ShipsListToStr", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(method => method.Name, StringComparer.Ordinal);
    }

    private static bool IsShipTypeCountPairParameter(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 1)
            return false;

        string parameterType = parameters[0].ParameterType.FullName ?? string.Empty;
        return parameterType.IndexOf("KeyValuePair", StringComparison.OrdinalIgnoreCase) >= 0 &&
               parameterType.IndexOf("ShipType", StringComparison.OrdinalIgnoreCase) >= 0 &&
               parameterType.IndexOf("Int32", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static void LogMissingTarget(string target)
    {
        LogCandidateMethods(target);
        Melon<MintChipPlusMod>.Logger.Warning(
            $"UADMC campaign ship-list safety: {target} not found; formatter safety disabled for this runtime.");
    }

    internal static void LogResolvedTarget(string target, MethodBase method)
    {
        string key = $"{target}:{method.DeclaringType?.FullName}:{method.Name}";
        if (!LoggedResolvedTargets.Add(key))
            return;

        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC campaign ship-list safety: {target} resolved to {method.DeclaringType?.FullName}.{method.Name}.");
    }

    internal static bool TryGetRowKey(ShipTypeCountPair row, out ShipType? key)
    {
        try
        {
            key = row.Key;
            return true;
        }
        catch
        {
            key = null;
            return false;
        }
    }

    internal static bool TryGetRowValue(ShipTypeCountPair row, out int value)
    {
        try
        {
            value = row.Value;
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static void LogCandidateMethods(string target)
    {
        string key = $"candidate-log:{target}";
        if (!LoggedResolvedTargets.Add(key))
            return;

        List<string> candidates = ShipsListLambdaCandidates()
            .Take(12)
            .Select(method =>
            {
                string parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName));
                return $"{method.DeclaringType?.Name}.{method.Name}:{method.ReturnType.Name}({parameters})";
            })
            .ToList();

        string text = candidates.Count == 0 ? "none" : string.Join(" | ", candidates);
        Melon<MintChipPlusMod>.Logger.Warning(
            $"UADMC campaign ship-list safety: {target} candidate methods: {text}");
    }

    internal static bool IsSuppressibleNullShipTypeFormatterException(Exception exception)
    {
        if (exception is NullReferenceException)
            return true;

        string typeName = exception.GetType().FullName ?? string.Empty;
        if (!string.Equals(typeName, "Il2CppInterop.Runtime.Il2CppException", StringComparison.Ordinal))
            return false;

        string text = exception.ToString();
        return text.Contains("System.NullReferenceException", StringComparison.Ordinal) &&
               text.Contains("<ShipsListToStr>b__705_0", StringComparison.Ordinal);
    }

    internal static void LogSortedNullRow()
        => LogSafety("sorted-null-row", "sorted malformed null ship-type row last.");

    internal static void LogFormattedNullRow(int count)
        => LogSafety($"formatted-null-row:{count}", "formatted malformed null ship-type row as unknown.");

    internal static void LogFormattedInvalidRow()
        => LogSafety("formatted-invalid-row", "formatted malformed ship-count row as empty.");

    internal static void LogSuppressedOuterFormatterException()
        => LogSafety("outer-suppressed", "suppressed null ship-type formatter NRE; using fallback.");

    internal static void LogNonSuppressed(Exception exception)
    {
        string typeName = exception.GetType().FullName ?? exception.GetType().Name;
        string key = $"{CampaignTurnKey()}:non-suppressed:{typeName}";
        if (!LoggedNonSuppressedContexts.Add(key))
            return;

        if (LoggedNonSuppressedContexts.Count > 64)
            LoggedNonSuppressedContexts.Clear();

        Melon<MintChipPlusMod>.Logger.Warning(
            $"UADMC campaign ship-list safety: non-suppressed formatter exception type={typeName} message={ShortMessage(exception.Message)}");
    }

    private static void LogSafety(string eventKey, string message)
    {
        string key = $"{CampaignTurnKey()}:{eventKey}";
        if (!LoggedContexts.Add(key))
            return;

        if (LoggedContexts.Count > 64)
            LoggedContexts.Clear();

        Melon<MintChipPlusMod>.Logger.Warning($"UADMC campaign ship-list safety: {message}");
    }

    private static string CampaignTurnKey()
    {
        try
        {
            CampaignController? campaign = CampaignController.Instance;
            return campaign == null ? "no-campaign" : campaign.CurrentDate.turn.ToString();
        }
        catch
        {
            return "unknown-turn";
        }
    }

    private static string ShortMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "<empty>";

        const int maxLength = 180;
        string clean = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return clean.Length <= maxLength ? clean : clean[..maxLength] + "...";
    }
}

[HarmonyPatch]
internal static class CampaignShipListFormattingSafetySorterPatch
{
    private static bool Prepare()
    {
        MethodBase? target = TargetMethod();
        bool available = target != null;
        if (!available)
            CampaignShipListFormattingSafety.LogMissingTarget("Ui.ShipsListToStr sorter lambda");
        else
            CampaignShipListFormattingSafety.LogResolvedTarget("sorter lambda", target!);

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignShipListFormattingSafety.ShipsListSorterLambda();

    private static bool Prefix(
        ShipTypeCountPair p,
        ref float __result)
    {
        if (CampaignShipListFormattingSafety.TryGetRowKey(p, out ShipType? key) && key != null)
            return true;

        __result = float.MaxValue;
        CampaignShipListFormattingSafety.LogSortedNullRow();
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignShipListFormattingSafetyFormatterPatch
{
    private static bool Prepare()
    {
        MethodBase? target = TargetMethod();
        bool available = target != null;
        if (!available)
            CampaignShipListFormattingSafety.LogMissingTarget("Ui.ShipsListToStr formatter lambda");
        else
            CampaignShipListFormattingSafety.LogResolvedTarget("formatter lambda", target!);

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignShipListFormattingSafety.ShipsListFormatterLambda();

    private static bool Prefix(
        ShipTypeCountPair p,
        ref string __result)
    {
        bool hasKey = CampaignShipListFormattingSafety.TryGetRowKey(p, out ShipType? key);
        bool hasValue = CampaignShipListFormattingSafety.TryGetRowValue(p, out int value);

        if (hasKey && key != null && hasValue && value > 0)
            return true;

        if (hasValue && value > 0)
        {
            __result = $"{value} unknown";
            CampaignShipListFormattingSafety.LogFormattedNullRow(value);
            return false;
        }

        __result = string.Empty;
        CampaignShipListFormattingSafety.LogFormattedInvalidRow();
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignShipListFormattingSafetyOuterBackstopPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            CampaignShipListFormattingSafety.LogMissingTarget("Ui.ShipsListToStr key/value overload");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignShipListFormattingSafety.ShipsListToStrKeyValueOverload();

    private static Exception? Finalizer(
        Exception? __exception,
        ref string __result,
        string forEmpty)
    {
        if (__exception == null)
            return null;

        if (!CampaignShipListFormattingSafety.IsSuppressibleNullShipTypeFormatterException(__exception))
        {
            CampaignShipListFormattingSafety.LogNonSuppressed(__exception);
            return __exception;
        }

        __result = string.IsNullOrWhiteSpace(forEmpty) ? "-" : forEmpty;
        CampaignShipListFormattingSafety.LogSuppressedOuterFormatterException();
        return null;
    }
}
