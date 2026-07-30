using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.GameData;

// Balance intent: keep Hull Weight Adjustment as the single switch for
// loaded-data ship-weight cleanup. This flattens crew weight and lightens
// early torpedo-boat towers without touching CSV files.
internal static class ShipWeightBalance
{
    private const float AdjustedCrewWeight = 0.2f;
    private const string NeedUnlockKey = "needunlock";
    private const string EarlyTbTowerUnlock = "earlyDDtowers_level_4";

    private static readonly Dictionary<string, float> CrewParamTargets = new(StringComparer.Ordinal)
    {
        ["crew_weight_min"] = AdjustedCrewWeight,
        ["crew_weight_max"] = AdjustedCrewWeight,
    };

    private static readonly Dictionary<string, float> TorpedoBoatTowerTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["torpedo_tower_main_1_small"] = 5f,
        ["torpedo_tower_main_1"] = 6f,
        ["torpedo_tower_main_2_small"] = 7.5f,
        ["torpedo_tower_main_2"] = 9f,
        ["torpedo_tower_main_3"] = 11f,
        ["torpedo_tower_main_3_big"] = 12.5f,
    };

    private static readonly string[] OversizedEarlyTbTowerGateTargets =
    {
        "torpedo_tower_main_2_small",
        "torpedo_tower_main_2",
        "torpedo_tower_main_3",
        "torpedo_tower_main_3_big",
    };

    private static readonly Dictionary<string, float> OriginalParamValues = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> OriginalTowerWeights = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PartUnlockState> OriginalTowerUnlockStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedMissingParams = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedMissingTowers = new(StringComparer.OrdinalIgnoreCase);
    private static bool loggedMissingGameData;
    private static bool? lastAppliedEnabled;
    private static string? lastAppliedSummary;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if ((gameData?.paramsRaw == null && gameData?.parms == null) || gameData?.parts == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP ship weight balance: option stored; game params or part data are not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        bool enabled = ModSettings.HullWeightAdjustmentEnabled;
        CaptureOriginalParams(gameData);
        Dictionary<string, PartData> towerParts = FindTargetTowerParts(gameData);
        CaptureOriginalTowerUnlockStates(towerParts);

        List<string> paramSummaries = new();
        int changedParams = 0;
        foreach (var pair in CrewParamTargets)
        {
            string paramName = pair.Key;
            if (!OriginalParamValues.TryGetValue(paramName, out float original))
            {
                LogMissingParam(paramName);
                continue;
            }

            float target = enabled ? pair.Value : original;
            float before = CurrentParamValue(gameData, paramName, original);
            bool wroteRaw = TryWriteRawParam(gameData, paramName, target);
            bool wroteParsed = TryWriteParsedParam(gameData, paramName, target);
            if (Math.Abs(before - target) > 0.0001f)
                changedParams++;

            paramSummaries.Add($"{paramName} {Fmt(before)}->{Fmt(target)} ({WriteTargetText(wroteRaw, wroteParsed)})");
        }

        TowerApplySummary towerSummary = ApplyTowerWeights(towerParts, enabled);
        TowerGateSummary towerGateSummary = ApplyTowerGates(towerParts, enabled);
        string summary =
            $"crewParams={changedParams}/{CrewParamTargets.Count} {string.Join("; ", paramSummaries)}; " +
            $"tbTowers={towerSummary.Changed}/{TorpedoBoatTowerTargets.Count} changed {towerSummary.Examples}; " +
            $"tbTowerGate={towerGateSummary.Changed}/{OversizedEarlyTbTowerGateTargets.Length} {towerGateSummary.Detail}";

        List<string> missing = towerSummary.Missing.Concat(towerGateSummary.Missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            summary += $"; missing={string.Join(",", missing)}";

        bool anyChanged = changedParams > 0 || towerSummary.Changed > 0 || towerGateSummary.Changed > 0;
        if (!anyChanged && lastAppliedEnabled == enabled && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedEnabled = enabled;
        lastAppliedSummary = summary;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP ship weight balance: applied {ModSettings.HullWeightAdjustmentModeText(enabled)} during {context}; {summary}.");
    }

    private static void CaptureOriginalParams(UadGameData gameData)
    {
        foreach (string paramName in CrewParamTargets.Keys)
        {
            if (OriginalParamValues.ContainsKey(paramName))
                continue;

            if (TryReadRawParam(gameData, paramName, out float rawValue) ||
                TryReadParsedParam(gameData, paramName, out rawValue))
            {
                OriginalParamValues[paramName] = rawValue;
            }
        }
    }

    private static void CaptureOriginalTowerUnlockStates(IReadOnlyDictionary<string, PartData> towerParts)
    {
        foreach (string id in OversizedEarlyTbTowerGateTargets)
        {
            if (OriginalTowerUnlockStates.ContainsKey(id) ||
                !towerParts.TryGetValue(id, out PartData? part) ||
                part == null)
            {
                continue;
            }

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

            OriginalTowerUnlockStates[id] = new PartUnlockState(
                part.param ?? string.Empty,
                part.NeedUnlock,
                hadParamxNeedUnlock,
                paramxNeedUnlockValues.ToArray());
        }
    }

    private static TowerApplySummary ApplyTowerWeights(IReadOnlyDictionary<string, PartData> targets, bool enabled)
    {
        List<string> examples = new();
        List<string> missing = new();
        int changed = 0;

        foreach (var pair in TorpedoBoatTowerTargets)
        {
            string id = pair.Key;
            if (!targets.TryGetValue(id, out PartData? part) || part == null)
            {
                missing.Add(id);
                LogMissingTower(id);
                continue;
            }

            if (!OriginalTowerWeights.ContainsKey(id))
                OriginalTowerWeights[id] = Safe(() => part.weight, 0f);

            float original = OriginalTowerWeights[id];
            float target = enabled ? pair.Value : original;
            float before = Safe(() => part.weight, 0f);
            if (Math.Abs(before - target) <= 0.0001f)
                continue;

            part.weight = target;
            changed++;
            if (examples.Count < 8)
                examples.Add($"{id} {Fmt(before)}->{Fmt(target)}");
        }

        return new TowerApplySummary(changed, examples.Count == 0 ? "none" : string.Join(", ", examples), missing);
    }

    private static TowerGateSummary ApplyTowerGates(IReadOnlyDictionary<string, PartData> targets, bool enabled)
    {
        List<string> details = new();
        List<string> missing = new();
        int changed = 0;

        foreach (string id in OversizedEarlyTbTowerGateTargets)
        {
            if (!targets.TryGetValue(id, out PartData? part) || part == null)
            {
                missing.Add(id);
                LogMissingTower(id);
                continue;
            }

            bool partChanged = enabled
                ? ApplyAdjustedTowerGate(part)
                : RestoreTowerGate(id, part);

            if (partChanged)
                changed++;

            details.Add(enabled
                ? $"{id}={OriginalTowerUnlockLabel(id)}->{EarlyTbTowerUnlock}"
                : $"{id}=vanilla");
        }

        return new TowerGateSummary(changed, details.Count == 0 ? "none" : string.Join(",", details), missing);
    }

    private static bool ApplyAdjustedTowerGate(PartData part)
    {
        bool changed = false;
        changed |= SetSingleNeedUnlockInParam(part, EarlyTbTowerUnlock);
        changed |= SetSingleNeedUnlockInParamx(part, EarlyTbTowerUnlock);

        if (!string.Equals(part.NeedUnlock, EarlyTbTowerUnlock, StringComparison.OrdinalIgnoreCase))
        {
            part.NeedUnlock = EarlyTbTowerUnlock;
            changed = true;
        }

        return changed;
    }

    private static bool SetSingleNeedUnlockInParam(PartData part, string unlock)
    {
        string token = $"{NeedUnlockKey}({unlock})";
        List<string> tokens = SplitTopLevelParamTokens(part.param);
        List<string> rewritten = tokens
            .Where(existing => !IsNeedUnlockToken(existing))
            .ToList();
        rewritten.Add(token);

        string target = string.Join(", ", rewritten);
        if (string.Equals(part.param ?? string.Empty, target, StringComparison.Ordinal))
            return false;

        part.param = target;
        return true;
    }

    private static bool SetSingleNeedUnlockInParamx(PartData part, string unlock)
    {
        if (part.paramx == null)
            part.paramx = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();

        Il2CppSystem.Collections.Generic.List<string> target = new();
        target.Add(unlock);

        if (part.paramx.TryGetValue(NeedUnlockKey, out Il2CppSystem.Collections.Generic.List<string> currentValues) &&
            ParamxValuesEqual(currentValues, new[] { unlock }))
        {
            return false;
        }

        part.paramx[NeedUnlockKey] = target;
        return true;
    }

    private static bool RestoreTowerGate(string id, PartData part)
    {
        if (!OriginalTowerUnlockStates.TryGetValue(id, out PartUnlockState original))
            return false;

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

    private static bool RestoreNeedUnlockParamx(PartData part, PartUnlockState original)
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

    private static Dictionary<string, PartData> FindTargetTowerParts(UadGameData gameData)
    {
        Dictionary<string, PartData> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in gameData.parts)
        {
            PartData? part = pair.Value;
            if (part == null)
                continue;

            string entryKey = SafeString(() => pair.Key);
            if (TorpedoBoatTowerTargets.ContainsKey(entryKey) && !targets.ContainsKey(entryKey))
            {
                targets[entryKey] = part;
                continue;
            }

            string partName = SafeString(() => part.name);
            if (TorpedoBoatTowerTargets.ContainsKey(partName) && !targets.ContainsKey(partName))
                targets[partName] = part;
        }

        return targets;
    }

    private static string OriginalTowerUnlockLabel(string id)
    {
        if (!OriginalTowerUnlockStates.TryGetValue(id, out PartUnlockState original) ||
            string.IsNullOrWhiteSpace(original.NeedUnlock))
        {
            return "start";
        }

        return original.NeedUnlock;
    }

    private static bool IsNeedUnlockToken(string token)
    {
        string trimmed = token.Trim();
        if (!trimmed.StartsWith(NeedUnlockKey, StringComparison.OrdinalIgnoreCase))
            return false;

        int index = NeedUnlockKey.Length;
        while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
            index++;

        return index < trimmed.Length && trimmed[index] == '(';
    }

    private static List<string> SplitTopLevelParamTokens(string? param)
    {
        List<string> tokens = new();
        if (string.IsNullOrWhiteSpace(param))
            return tokens;

        int depth = 0;
        int start = 0;
        for (int i = 0; i < param.Length; i++)
        {
            char c = param[i];
            if (c == '(' || c == '[' || c == '{')
                depth++;
            else if ((c == ')' || c == ']' || c == '}') && depth > 0)
                depth--;
            else if (c == ',' && depth == 0)
            {
                AddToken(param[start..i]);
                start = i + 1;
            }
        }

        AddToken(param[start..]);
        return tokens;

        void AddToken(string token)
        {
            token = token.Trim();
            if (!string.IsNullOrEmpty(token))
                tokens.Add(token);
        }
    }

    private static float CurrentParamValue(UadGameData gameData, string paramName, float fallback)
        => TryReadParsedParam(gameData, paramName, out float parsed)
            ? parsed
            : TryReadRawParam(gameData, paramName, out float raw)
                ? raw
                : fallback;

    private static bool TryReadRawParam(UadGameData gameData, string paramName, out float value)
    {
        value = 0f;
        try
        {
            if (gameData.paramsRaw != null &&
                gameData.paramsRaw.TryGetValue(paramName, out ParamData raw) &&
                raw != null)
            {
                value = raw.value;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryReadParsedParam(UadGameData gameData, string paramName, out float value)
    {
        value = 0f;
        try
        {
            return gameData.parms != null && gameData.parms.TryGetValue(paramName, out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteRawParam(UadGameData gameData, string paramName, float value)
    {
        try
        {
            if (gameData.paramsRaw == null ||
                !gameData.paramsRaw.TryGetValue(paramName, out ParamData raw) ||
                raw == null)
            {
                return false;
            }

            raw.value = value;
            return true;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP ship weight balance: failed to write raw param {paramName}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryWriteParsedParam(UadGameData gameData, string paramName, float value)
    {
        try
        {
            if (gameData.parms == null || !gameData.parms.ContainsKey(paramName))
                return false;

            gameData.parms[paramName] = value;
            return true;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP ship weight balance: failed to write parsed param {paramName}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void LogMissingParam(string paramName)
    {
        if (LoggedMissingParams.Add(paramName))
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP ship weight balance: missing loaded param {paramName}; cannot adjust it.");
    }

    private static void LogMissingTower(string id)
    {
        if (LoggedMissingTowers.Add(id))
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP ship weight balance: missing TB tower part {id}; cannot adjust it.");
    }

    private static string WriteTargetText(bool wroteRaw, bool wroteParsed)
        => (wroteRaw, wroteParsed) switch
        {
            (true, true) => "raw+parsed",
            (true, false) => "raw",
            (false, true) => "parsed",
            _ => "not written",
        };

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private readonly record struct TowerApplySummary(int Changed, string Examples, List<string> Missing);
    private readonly record struct TowerGateSummary(int Changed, string Detail, List<string> Missing);
    private readonly record struct PartUnlockState(string Param, string? NeedUnlock, bool HadParamxNeedUnlock, string[] ParamxNeedUnlockValues);
}
