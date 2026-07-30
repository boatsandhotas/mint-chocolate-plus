using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.GameData;

// Balance intent: make battle damage punchier by rewriting the loaded global
// damage params once. This deliberately avoids Ship.TakeHitRaw and other
// per-hit battle paths; vanilla combat reads already-scaled param values.
internal static class BattleDamageBalance
{
    private static readonly string[] DamageParams =
    {
        "section_damage_gun",
        "section_damage_torpedo",
    };

    private static readonly Dictionary<string, float> OriginalParamValues = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedMissingParams = new(StringComparer.Ordinal);
    private static ModSettings.BattleDamageMode? lastAppliedMode;
    private static string? lastAppliedSummary;
    private static bool loggedMissingGameData;
    private static bool loggedDeferredInBattle;

    internal static bool IsBattleOrLoading()
        => GameManager.IsBattle || GameManager.IsLoadingBattle;

    internal static void ApplyCurrentSetting(
        string context = "manual",
        UadGameData? gameDataOverride = null,
        bool allowDuringBattleTransition = false)
    {
        if (!allowDuringBattleTransition && IsBattleOrLoading())
        {
            if (!loggedDeferredInBattle)
            {
                loggedDeferredInBattle = true;
                Melon<MintChipPlusMod>.Logger.Warning(
                    "UADMC battle damage: live reapply deferred because a battle is loading or active.");
            }

            return;
        }

        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.paramsRaw == null && gameData?.parms == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<MintChipPlusMod>.Logger.Msg("UADMC battle damage: option stored; game params are not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        loggedDeferredInBattle = false;

        CaptureOriginalParams(gameData);

        ModSettings.BattleDamageMode mode = ModSettings.BattleDamage;
        float multiplier = ModSettings.BattleDamageMultiplier(mode);
        List<string> summaryParts = new();

        foreach (string paramName in DamageParams)
        {
            if (!OriginalParamValues.TryGetValue(paramName, out float original))
            {
                LogMissingParam(paramName);
                continue;
            }

            float scaled = original * multiplier;
            bool wroteRaw = TryWriteRawParam(gameData, paramName, scaled);
            bool wroteParsed = TryWriteParsedParam(gameData, paramName, scaled);
            summaryParts.Add($"{paramName} {Fmt(original)}->{Fmt(scaled)} ({WriteTargetText(wroteRaw, wroteParsed)})");
        }

        if (summaryParts.Count == 0)
            return;

        string summary = string.Join("; ", summaryParts);
        if (lastAppliedMode == mode && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedMode = mode;
        lastAppliedSummary = summary;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC battle damage: applied {ModSettings.BattleDamageModeText(mode)} during {context}; {summary}.");
    }

    private static void CaptureOriginalParams(UadGameData gameData)
    {
        foreach (string paramName in DamageParams)
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
                $"UADMC battle damage: failed to write raw param {paramName}. {ex.GetType().Name}: {ex.Message}");
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
                $"UADMC battle damage: failed to write parsed param {paramName}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void LogMissingParam(string paramName)
    {
        if (LoggedMissingParams.Add(paramName))
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC battle damage: missing loaded param {paramName}; cannot scale it.");
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
}
