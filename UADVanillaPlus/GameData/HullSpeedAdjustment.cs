using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.GameData;

// Balance intent: reduce early small-craft hull speed targets that VP's
// new-hull defaults read, without touching later TBD/DD hulls or CSVs.
internal static class HullSpeedAdjustment
{
    private const float AdjustedTbSpeedKnots = 26f;
    private const float AdjustedDdSpeedKnots = 29f;
    private const string EarlyTbDualFunnelId = "torpedo_funnel_3";
    private const string EarlyTbDualFunnelUnlock = "smallearlyfunnels_level_3";
    private const string NeedUnlockKey = "needunlock";

    private static readonly HullSpeedTarget[] Targets =
    {
        new("tb_lowbow", "TB", AdjustedTbSpeedKnots),
        new("tb_highbow", "TB", AdjustedTbSpeedKnots),
        new("tb_standard", "TB", AdjustedTbSpeedKnots),
        new("dd_1", "DD", AdjustedDdSpeedKnots),
        new("dd_1_france", "DD", AdjustedDdSpeedKnots),
        new("dd_1_japan", "DD", AdjustedDdSpeedKnots),
        new("dd_1_russia", "DD", AdjustedDdSpeedKnots),
        new("dd_1_austria", "DD", AdjustedDdSpeedKnots),
        new("dd_1_german", "DD", AdjustedDdSpeedKnots),
        new("dd_1_german_large", "DD", AdjustedDdSpeedKnots),
        new("dd_1_austria_large", "DD", AdjustedDdSpeedKnots),
    };

    private static readonly Dictionary<string, float> OriginalSpeedLimiters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedMissingHulls = new(StringComparer.OrdinalIgnoreCase);

    private static FunnelUnlockState? originalFunnelUnlockState;
    private static bool? lastAppliedEnabled;
    private static string? lastAppliedSummary;
    private static bool loggedMissingGameData;
    private static bool loggedMissingFunnel;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.parts == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP hull speed adjustment: option stored; part data is not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        CaptureOriginalSpeedLimiters(gameData);
        CaptureOriginalFunnelUnlockState(gameData);

        bool enabled = ModSettings.HullSpeedAdjustmentEnabled;
        List<string> changes = new();
        bool anyChanged = false;
        int tbSeen = 0;
        int ddSeen = 0;
        foreach (HullSpeedTarget targetInfo in Targets)
        {
            if (!TryGetPart(gameData, targetInfo.HullId, out PartData? hull) || hull == null)
            {
                LogMissingHull(targetInfo.HullId);
                continue;
            }

            if (string.Equals(targetInfo.Group, "TB", StringComparison.Ordinal))
                tbSeen++;
            else if (string.Equals(targetInfo.Group, "DD", StringComparison.Ordinal))
                ddSeen++;

            if (!OriginalSpeedLimiters.TryGetValue(targetInfo.HullId, out float original))
                continue;

            float target = enabled ? targetInfo.AdjustedSpeed : original;
            float before = hull.speedLimiter;
            if (Math.Abs(before - target) > 0.0001f)
            {
                hull.speedLimiter = target;
                anyChanged = true;
            }

            changes.Add($"{targetInfo.HullId} {Fmt(before)}->{Fmt(target)}");
        }

        FunnelGateResult funnelGate = ApplyFunnelGate(gameData, enabled);
        anyChanged |= funnelGate.Changed;

        if (changes.Count == 0)
            return;

        if (!anyChanged && lastAppliedEnabled == enabled)
            return;

        string summary = $"{string.Join(", ", changes)}; {funnelGate.Detail}";
        if (lastAppliedEnabled == enabled && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedEnabled = enabled;
        lastAppliedSummary = summary;
        string action = enabled ? "adjusted" : "restored";
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP hull speed adjustment: applied {ModSettings.HullSpeedAdjustmentModeText(enabled)} during {context}; TB {tbSeen}/3 {action} to {(enabled ? Fmt(AdjustedTbSpeedKnots) + " kn" : "vanilla")}; DD {ddSeen}/8 {action} to {(enabled ? Fmt(AdjustedDdSpeedKnots) + " kn" : "vanilla")}; TB funnel gate {funnelGate.Detail}; {string.Join(", ", changes)}.");
    }

    private static void CaptureOriginalSpeedLimiters(UadGameData gameData)
    {
        foreach (HullSpeedTarget targetInfo in Targets)
        {
            if (OriginalSpeedLimiters.ContainsKey(targetInfo.HullId))
                continue;

            if (TryGetPart(gameData, targetInfo.HullId, out PartData? hull) && hull != null)
                OriginalSpeedLimiters[targetInfo.HullId] = hull.speedLimiter;
        }
    }

    private static void CaptureOriginalFunnelUnlockState(UadGameData gameData)
    {
        if (originalFunnelUnlockState != null)
            return;

        if (!TryGetPart(gameData, EarlyTbDualFunnelId, out PartData? part) || part == null)
            return;

        bool hadParamxNeedUnlock = false;
        List<string> paramxNeedUnlockValues = new();
        try
        {
            if (part.paramx != null &&
                part.paramx.TryGetValue(NeedUnlockKey, out Il2CppSystem.Collections.Generic.List<string> unlockValues) &&
                unlockValues != null)
            {
                hadParamxNeedUnlock = true;
                for (int i = 0; i < unlockValues.Count; i++)
                    paramxNeedUnlockValues.Add(unlockValues[i]);
            }
        }
        catch
        {
        }

        originalFunnelUnlockState = new FunnelUnlockState(
            part.param ?? string.Empty,
            part.NeedUnlock,
            hadParamxNeedUnlock,
            paramxNeedUnlockValues.ToArray());
    }

    private static FunnelGateResult ApplyFunnelGate(UadGameData gameData, bool enabled)
    {
        if (!TryGetPart(gameData, EarlyTbDualFunnelId, out PartData? part) || part == null)
        {
            LogMissingFunnel();
            return new FunnelGateResult(false, $"{EarlyTbDualFunnelId}=missing");
        }

        bool changed = enabled
            ? ApplyAdjustedFunnelGate(part)
            : RestoreFunnelGate(part);

        string detail = enabled
            ? $"{EarlyTbDualFunnelId}={OriginalFunnelUnlockLabel()}->{EarlyTbDualFunnelUnlock}"
            : $"{EarlyTbDualFunnelId}=vanilla";
        return new FunnelGateResult(changed, detail);
    }

    private static bool ApplyAdjustedFunnelGate(PartData part)
    {
        bool changed = false;
        changed |= EnsureNeedUnlockInParam(part);
        changed |= EnsureNeedUnlockInParamx(part);

        if (!string.Equals(part.NeedUnlock, EarlyTbDualFunnelUnlock, StringComparison.OrdinalIgnoreCase))
        {
            part.NeedUnlock = EarlyTbDualFunnelUnlock;
            changed = true;
        }

        return changed;
    }

    private static bool EnsureNeedUnlockInParam(PartData part)
    {
        string token = $"{NeedUnlockKey}({EarlyTbDualFunnelUnlock})";
        string param = part.param ?? string.Empty;
        if (param.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        part.param = string.IsNullOrWhiteSpace(param) ? token : $"{param}, {token}";
        return true;
    }

    private static bool EnsureNeedUnlockInParamx(PartData part)
    {
        if (part.paramx == null)
            part.paramx = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();

        if (!part.paramx.TryGetValue(NeedUnlockKey, out Il2CppSystem.Collections.Generic.List<string> unlockValues) || unlockValues == null)
        {
            unlockValues = new Il2CppSystem.Collections.Generic.List<string>();
            part.paramx[NeedUnlockKey] = unlockValues;
        }

        if (ContainsValue(unlockValues, EarlyTbDualFunnelUnlock))
            return false;

        unlockValues.Add(EarlyTbDualFunnelUnlock);
        return true;
    }

    private static bool RestoreFunnelGate(PartData part)
    {
        if (originalFunnelUnlockState == null)
            return false;

        FunnelUnlockState original = originalFunnelUnlockState.Value;
        bool changed = false;

        string currentParam = part.param ?? string.Empty;
        if (!string.Equals(currentParam, original.Param, StringComparison.Ordinal))
        {
            part.param = original.Param;
            changed = true;
        }

        if (!string.Equals(part.NeedUnlock, original.NeedUnlock, StringComparison.Ordinal))
        {
            part.NeedUnlock = original.NeedUnlock;
            changed = true;
        }

        changed |= RestoreNeedUnlockParamx(part, original);
        return changed;
    }

    private static bool RestoreNeedUnlockParamx(PartData part, FunnelUnlockState original)
    {
        if (part.paramx == null)
        {
            if (!original.HadParamxNeedUnlock)
                return false;

            part.paramx = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();
        }

        if (!original.HadParamxNeedUnlock)
        {
            if (part.paramx.ContainsKey(NeedUnlockKey))
            {
                part.paramx.Remove(NeedUnlockKey);
                return true;
            }

            return false;
        }

        part.paramx.TryGetValue(NeedUnlockKey, out Il2CppSystem.Collections.Generic.List<string> currentValues);
        if (ParamxValuesEqual(currentValues, original.ParamxNeedUnlockValues))
            return false;

        Il2CppSystem.Collections.Generic.List<string> restored = new();
        foreach (string value in original.ParamxNeedUnlockValues)
            restored.Add(value);

        part.paramx[NeedUnlockKey] = restored;
        return true;
    }

    private static bool ContainsValue(Il2CppSystem.Collections.Generic.List<string> values, string target)
    {
        if (values == null)
            return false;

        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ParamxValuesEqual(Il2CppSystem.Collections.Generic.List<string>? currentValues, string[] expectedValues)
    {
        if (currentValues == null || currentValues.Count != expectedValues.Length)
            return false;

        for (int i = 0; i < expectedValues.Length; i++)
        {
            if (!string.Equals(currentValues[i], expectedValues[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string OriginalFunnelUnlockLabel()
    {
        if (originalFunnelUnlockState == null || string.IsNullOrWhiteSpace(originalFunnelUnlockState.Value.NeedUnlock))
            return "start";

        return originalFunnelUnlockState.Value.NeedUnlock;
    }

    private static bool TryGetPart(UadGameData gameData, string partId, out PartData? part)
    {
        part = null;
        try
        {
            if (gameData.parts != null && gameData.parts.TryGetValue(partId, out PartData loaded) && loaded != null)
            {
                part = loaded;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static void LogMissingHull(string hullId)
    {
        if (LoggedMissingHulls.Add(hullId))
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP hull speed adjustment: missing loaded hull {hullId}; cannot adjust speed limiter.");
    }

    private static void LogMissingFunnel()
    {
        if (!loggedMissingFunnel)
        {
            loggedMissingFunnel = true;
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP hull speed adjustment: missing loaded part {EarlyTbDualFunnelId}; cannot gate early TB dual funnel.");
        }
    }

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct HullSpeedTarget(string HullId, string Group, float AdjustedSpeed);
    private readonly record struct FunnelUnlockState(string Param, string? NeedUnlock, bool HadParamxNeedUnlock, string[] ParamxNeedUnlockValues);
    private readonly record struct FunnelGateResult(bool Changed, string Detail);
}
