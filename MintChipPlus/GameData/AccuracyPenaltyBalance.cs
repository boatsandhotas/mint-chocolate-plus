using System.Globalization;
using System.Text.RegularExpressions;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.GameData;

// Balance intent: vanilla design-side accuracy penalties and crew training
// bonuses/penalties can stack into extreme combat swings. MC changes loaded
// data curves up front, so battle code reads normal vanilla fields and pays no
// MC-specific per-shot cost.
internal static class AccuracyPenaltyBalance
{
    private sealed record AccuracyCurve(string EffectName, float First, float Second);
    private sealed record CrewTrainingCurve(float Accuracy, float Aiming, float Reload, float DamageControl);
    private sealed record DamageStateParam(string Name, bool IsMultiplierTowardOne);

    private static readonly Dictionary<string, AccuracyCurve> VanillaAccuracyCurves = new(StringComparer.Ordinal)
    {
        ["smoke"] = new("accuracy", 0f, -15f),
        ["stability"] = new("accuracy", -50f, 25f),
        ["instability_z"] = new("accuracy", 0f, -30f),
        ["instability_x"] = new("accuracy", 0f, -40f),
        ["instability_zz"] = new("accuracy", 0f, -25f),
        ["instability_xx"] = new("accuracy", 0f, -25f),
    };

    private static readonly Regex EffectTermRegex = new(
        @"(?<effect>[A-Za-z_]+)\s*\(\s*(?<first>[+-]?\d+(?:\.\d+)?)\s*;\s*(?<second>[+-]?\d+(?:\.\d+)?)\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> LoggedStats = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OriginalEffectText = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CrewTrainingCurve> OriginalCrewTrainingCurves = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> OriginalDamageStateParams = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedMissingDamageStateParams = new(StringComparer.Ordinal);
    private static readonly DamageStateParam[] DamageStateAccuracyParams =
    {
        new("conning_tower_damage_acc_loss", true),
        new("fire_control_damage_acc_loss", true),
        new("acc_instab_flooding", false),
        new("acc_instab_damage", false),
    };

    private static readonly string[] DamageStateUnchangedParams =
    {
        "instability_damage_ratio",
        "instability_damage_decrease",
    };

    private static ModSettings.AccuracyPenaltyMode? lastAppliedCrewMode;
    private static ModSettings.AccuracyPenaltyMode? lastAppliedDamageStateMode;
    private static string? lastAppliedCrewSummary;
    private static string? lastAppliedDamageStateSummary;
    private static bool loggedMissingCrewTrainingLevels;
    private static bool loggedMissingDamageStateGameData;
    private static bool loggedDeferredDamageStateInBattle;

    internal static void PrepareStatForVanillaPostProcess(StatData stat)
    {
        if (stat == null || string.IsNullOrEmpty(stat.effect))
            return;

        string statName = stat.name ?? string.Empty;
        if (!VanillaAccuracyCurves.TryGetValue(statName, out AccuracyCurve? vanillaCurve))
            return;

        RememberOriginalEffectText(statName, stat.effect, vanillaCurve);

        ModSettings.AccuracyPenaltyMode mode = ModSettings.DesignAccuracyPenaltyMode;
        if (mode == ModSettings.AccuracyPenaltyMode.Vanilla)
            return;

        string balancedCurve = BalancedCurve(vanillaCurve, ModSettings.AccuracyPenaltyDivisor(mode));
        string originalEffect = stat.effect;
        string updatedEffect = EffectTermRegex.Replace(stat.effect, match => ReplaceAccuracyCurve(match, vanillaCurve, balancedCurve));
        if (updatedEffect == originalEffect)
        {
            LogOnce(statName, $"UADMC crew & accuracy balance: expected {statName}.{CurveText(vanillaCurve)} was not found in '{stat.effect}'; leaving vanilla effect text.");
            return;
        }

        stat.effect = updatedEffect;
        LogOnce(statName, $"UADMC crew & accuracy balance: {statName} {CurveText(vanillaCurve)} -> {balancedCurve}.");
    }

    internal static bool IsBattleOrLoading()
        => GameManager.IsBattle || GameManager.IsLoadingBattle;

    internal static void ApplyLoadedCrewTrainingLevels(ModSettings.AccuracyPenaltyMode mode, string reason)
    {
        var levels = G.GameData?.crewTrainingLevels;
        if (levels == null)
        {
            if (!loggedMissingCrewTrainingLevels)
            {
                loggedMissingCrewTrainingLevels = true;
                Melon<MintChipPlusMod>.Logger.Warning("UADMC crew & accuracy balance: crew training data is not loaded yet.");
            }

            return;
        }

        loggedMissingCrewTrainingLevels = false;
        CaptureOriginalCrewTrainingCurves(levels);

        float divisor = ModSettings.AccuracyPenaltyDivisor(mode);
        int appliedLevels = 0;
        List<string> sampleParts = new();

        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, CrewTrainingLevels> entry in levels)
        {
            CrewTrainingLevels level = entry.Value;
            if (level == null)
                continue;

            string key = CrewTrainingKey(entry.Key, level);
            if (!OriginalCrewTrainingCurves.TryGetValue(key, out CrewTrainingCurve? original))
                continue;

            CrewTrainingCurve balanced = new(
                FlattenMultiplier(original.Accuracy, divisor),
                FlattenMultiplier(original.Aiming, divisor),
                FlattenMultiplier(original.Reload, divisor),
                FlattenMultiplier(original.DamageControl, divisor));

            level.Accuracy = balanced.Accuracy;
            level.Aiming = balanced.Aiming;
            level.Reload = balanced.Reload;
            level.DamageControl = balanced.DamageControl;

            if (!IsNeutralCrewCurve(original))
                appliedLevels++;

            if (IsSampleCrewLevel(key))
                sampleParts.Add(FormatCrewSample(key, original, balanced));
        }

        string summary = $"{ModSettings.AccuracyPenaltyModeText(mode)} crew training curves to {appliedLevels} levels";
        if (sampleParts.Count > 0)
            summary += $" ({string.Join("; ", sampleParts)})";

        if (lastAppliedCrewMode == mode && string.Equals(lastAppliedCrewSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedCrewMode = mode;
        lastAppliedCrewSummary = summary;
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC crew & accuracy balance: applied {summary} during {reason}.");
    }

    internal static void ApplyLoadedDamageStateParams(
        ModSettings.AccuracyPenaltyMode mode,
        string reason,
        UadGameData? gameDataOverride = null,
        bool allowDuringBattleTransition = false)
    {
        if (!allowDuringBattleTransition && IsBattleOrLoading())
        {
            if (!loggedDeferredDamageStateInBattle)
            {
                loggedDeferredDamageStateInBattle = true;
                Melon<MintChipPlusMod>.Logger.Warning(
                    "UADMC crew & accuracy balance: damage-state param reapply deferred because a battle is loading or active.");
            }

            return;
        }

        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.paramsRaw == null && gameData?.parms == null)
        {
            if (!loggedMissingDamageStateGameData)
            {
                loggedMissingDamageStateGameData = true;
                Melon<MintChipPlusMod>.Logger.Warning(
                    "UADMC crew & accuracy balance: damage-state params are not loaded yet.");
            }

            return;
        }

        loggedMissingDamageStateGameData = false;
        loggedDeferredDamageStateInBattle = false;

        CaptureOriginalDamageStateParams(gameData);
        float divisor = DamageStateAccuracyDivisor(mode);
        List<string> summaryParts = new();

        foreach (DamageStateParam param in DamageStateAccuracyParams)
        {
            if (!OriginalDamageStateParams.TryGetValue(param.Name, out float original))
            {
                LogMissingDamageStateParam(param.Name);
                continue;
            }

            float balanced = param.IsMultiplierTowardOne
                ? FlattenMultiplier(original, divisor)
                : original / divisor;

            bool wroteRaw = TryWriteRawParam(gameData, param.Name, balanced);
            bool wroteParsed = TryWriteParsedParam(gameData, param.Name, balanced);
            summaryParts.Add($"{param.Name} {Format(original)}->{Format(balanced)} ({WriteTargetText(wroteRaw, wroteParsed)})");
        }

        foreach (string paramName in DamageStateUnchangedParams)
        {
            if (TryReadRawParam(gameData, paramName, out float unchanged) ||
                TryReadParsedParam(gameData, paramName, out unchanged))
            {
                summaryParts.Add($"{paramName} unchanged={Format(unchanged)}");
            }
        }

        if (summaryParts.Count == 0)
            return;

        string summary = string.Join("; ", summaryParts);
        if (lastAppliedDamageStateMode == mode && string.Equals(lastAppliedDamageStateSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedDamageStateMode = mode;
        lastAppliedDamageStateSummary = summary;
        string modeText = mode == ModSettings.AccuracyPenaltyMode.Vanilla
            ? "Vanilla"
            : $"{ModSettings.AccuracyPenaltyModeText(mode)}-softened";
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC crew & accuracy balance: damage-state params {modeText} during {reason}; {summary}.");
    }

    internal static void TryReapplyLoadedStats(ModSettings.AccuracyPenaltyMode mode)
    {
        if (IsBattleOrLoading())
        {
            Melon<MintChipPlusMod>.Logger.Warning("UADMC crew & accuracy balance: live reapply skipped because a battle is loading or active.");
            return;
        }

        if (G.GameData?.stats == null)
        {
            Melon<MintChipPlusMod>.Logger.Warning("UADMC crew & accuracy balance: live reapply deferred because game data is not loaded yet.");
            return;
        }

        int rebuilt = 0;
        int missing = 0;
        foreach (string statName in VanillaAccuracyCurves.Keys)
        {
            if (!G.GameData.stats.TryGetValue(statName, out StatData stat) || stat == null)
            {
                missing++;
                continue;
            }

            if (!OriginalEffectText.TryGetValue(statName, out string? originalEffect))
            {
                originalEffect = RestoreVanillaEffectText(stat.effect, VanillaAccuracyCurves[statName]);
                OriginalEffectText[statName] = originalEffect;
                Melon<MintChipPlusMod>.Logger.Warning($"UADMC crew & accuracy balance: rebuilt original effect text for {statName} from currently loaded data.");
            }

            // Live changes rebuild the vanilla parsed effect dictionary from
            // original text. The Harmony prefix then applies the selected MC
            // curve before vanilla PostProcess parses it, avoiding cumulative
            // divide-by-divide rewrites and avoiding battle hot-path patches.
            stat.effect = originalEffect;
            stat.PostProcess();
            rebuilt++;
        }

        ApplyLoadedCrewTrainingLevels(mode, "live option reapply");
        ApplyLoadedDamageStateParams(mode, "live option reapply");

        Melon<MintChipPlusMod>.Logger.Msg($"UADMC crew & accuracy balance: reapplied {ModSettings.AccuracyPenaltyModeText(mode)} mode to {rebuilt} loaded design stat curves{(missing > 0 ? $" ({missing} missing)" : string.Empty)}.");
    }

    private static void RememberOriginalEffectText(string statName, string effectText, AccuracyCurve vanillaCurve)
    {
        if (OriginalEffectText.ContainsKey(statName))
            return;

        OriginalEffectText[statName] = RestoreVanillaEffectText(effectText, vanillaCurve);
    }

    private static string ReplaceAccuracyCurve(Match match, AccuracyCurve expected, string balancedCurve)
    {
        if (!string.Equals(match.Groups["effect"].Value, expected.EffectName, StringComparison.Ordinal))
            return match.Value;

        float first = float.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture);
        float second = float.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture);
        if (Math.Abs(first - expected.First) > 0.001f || Math.Abs(second - expected.Second) > 0.001f)
            return match.Value;

        return balancedCurve;
    }

    private static string RestoreVanillaEffectText(string effectText, AccuracyCurve vanillaCurve)
        => EffectTermRegex.Replace(effectText, match =>
            string.Equals(match.Groups["effect"].Value, vanillaCurve.EffectName, StringComparison.Ordinal)
                ? CurveText(vanillaCurve)
                : match.Value);

    private static string BalancedCurve(AccuracyCurve vanillaCurve, float divisor)
        => $"{vanillaCurve.EffectName}({Format(DampenNegative(vanillaCurve.First, divisor))};{Format(DampenNegative(vanillaCurve.Second, divisor))})";

    private static string CurveText(AccuracyCurve curve)
        => $"{curve.EffectName}({Format(curve.First)};{Format(curve.Second)})";

    private static float DampenNegative(float value, float divisor)
        => value < 0f ? value / divisor : value;

    private static float FlattenMultiplier(float value, float divisor)
        => 1f + ((value - 1f) / divisor);

    private static float DamageStateAccuracyDivisor(ModSettings.AccuracyPenaltyMode mode)
        => mode switch
        {
            ModSettings.AccuracyPenaltyMode.Div2 => MathF.Sqrt(2f),
            ModSettings.AccuracyPenaltyMode.Div5 => 2f,
            ModSettings.AccuracyPenaltyMode.Div10 => MathF.Sqrt(10f),
            _ => 1f,
        };

    private static string Format(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void CaptureOriginalCrewTrainingCurves(Il2CppSystem.Collections.Generic.Dictionary<string, CrewTrainingLevels> levels)
    {
        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, CrewTrainingLevels> entry in levels)
        {
            CrewTrainingLevels level = entry.Value;
            if (level == null)
                continue;

            string key = CrewTrainingKey(entry.Key, level);
            if (OriginalCrewTrainingCurves.ContainsKey(key))
                continue;

            OriginalCrewTrainingCurves[key] = new CrewTrainingCurve(level.Accuracy, level.Aiming, level.Reload, level.DamageControl);
        }
    }

    private static void CaptureOriginalDamageStateParams(UadGameData gameData)
    {
        foreach (DamageStateParam param in DamageStateAccuracyParams)
        {
            if (OriginalDamageStateParams.ContainsKey(param.Name))
                continue;

            if (TryReadRawParam(gameData, param.Name, out float rawValue) ||
                TryReadParsedParam(gameData, param.Name, out rawValue))
            {
                OriginalDamageStateParams[param.Name] = rawValue;
            }
        }
    }

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
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC crew & accuracy balance: failed to write raw damage-state param {paramName}. {ex.GetType().Name}: {ex.Message}");
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
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC crew & accuracy balance: failed to write parsed damage-state param {paramName}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string CrewTrainingKey(string dictionaryKey, CrewTrainingLevels level)
    {
        if (!string.IsNullOrWhiteSpace(dictionaryKey))
            return dictionaryKey;

        if (!string.IsNullOrWhiteSpace(level.name))
            return level.name;

        return $"level-{OriginalCrewTrainingCurves.Count + 1}";
    }

    private static bool IsNeutralCrewCurve(CrewTrainingCurve curve)
        => Math.Abs(curve.Accuracy - 1f) <= 0.001f &&
           Math.Abs(curve.Aiming - 1f) <= 0.001f &&
           Math.Abs(curve.Reload - 1f) <= 0.001f &&
           Math.Abs(curve.DamageControl - 1f) <= 0.001f;

    private static bool IsSampleCrewLevel(string key)
        => string.Equals(key, "Cadets", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, "Veterans", StringComparison.OrdinalIgnoreCase);

    private static string FormatCrewSample(string key, CrewTrainingCurve original, CrewTrainingCurve balanced)
        => $"{key} acc {Format(original.Accuracy)}->{Format(balanced.Accuracy)} " +
           $"aim {Format(original.Aiming)}->{Format(balanced.Aiming)} " +
           $"reload {Format(original.Reload)}->{Format(balanced.Reload)} " +
           $"dc {Format(original.DamageControl)}->{Format(balanced.DamageControl)}";

    private static void LogMissingDamageStateParam(string paramName)
    {
        if (LoggedMissingDamageStateParams.Add(paramName))
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC crew & accuracy balance: missing loaded damage-state param {paramName}; cannot scale it.");
    }

    private static string WriteTargetText(bool wroteRaw, bool wroteParsed)
        => (wroteRaw, wroteParsed) switch
        {
            (true, true) => "raw+parsed",
            (true, false) => "raw",
            (false, true) => "parsed",
            _ => "not written",
        };

    private static void LogOnce(string statName, string message)
    {
        if (LoggedStats.Add(statName))
            Melon<MintChipPlusMod>.Logger.Msg(message);
    }
}
