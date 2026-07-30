using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.GameData;

// Balance intent: expose vanilla's campaign movement/supply-distance multiplier
// as a VP preset. CampaignShipsMovementManager.DistancePerTurn reads this param,
// so rewriting it at data-load time keeps movement and supply reach aligned.
internal static class CampaignNavalMobilityBalance
{
    private const string MonthlyShipSpeedModParam = "monthly_ship_speed_mod";

    private static float? originalValue;
    private static bool loggedMissingGameData;
    private static bool loggedMissingParam;
    private static ModSettings.CampaignNavalMobilityMode? lastAppliedMode;
    private static string? lastAppliedSummary;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.paramsRaw == null && gameData?.parms == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP campaign naval mobility: option stored; game params are not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        CaptureOriginalValue(gameData);
        if (originalValue == null)
        {
            LogMissingParam();
            return;
        }

        ModSettings.CampaignNavalMobilityMode mode = ModSettings.CampaignNavalMobility;
        float target = ModSettings.CampaignNavalMobilitySpeedMod(mode);
        float before = CurrentParamValue(gameData, originalValue.Value);
        bool wroteRaw = TryWriteRawParam(gameData, target);
        bool wroteParsed = TryWriteParsedParam(gameData, target);
        string summary = $"{MonthlyShipSpeedModParam} {Fmt(before)}->{Fmt(target)} ({WriteTargetText(wroteRaw, wroteParsed)})";

        if (lastAppliedMode == mode && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedMode = mode;
        lastAppliedSummary = summary;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP campaign naval mobility: applied {ModSettings.CampaignNavalMobilityModeText(mode)} during {context}; {summary}.");
    }

    private static void CaptureOriginalValue(UadGameData gameData)
    {
        if (originalValue != null)
            return;

        if (TryReadRawParam(gameData, out float rawValue) ||
            TryReadParsedParam(gameData, out rawValue))
        {
            originalValue = rawValue;
        }
    }

    private static float CurrentParamValue(UadGameData gameData, float fallback)
        => TryReadRawParam(gameData, out float rawValue)
            ? rawValue
            : TryReadParsedParam(gameData, out float parsedValue)
                ? parsedValue
                : fallback;

    private static bool TryReadRawParam(UadGameData gameData, out float value)
    {
        value = 0f;
        try
        {
            if (gameData.paramsRaw != null &&
                gameData.paramsRaw.TryGetValue(MonthlyShipSpeedModParam, out ParamData raw) &&
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

    private static bool TryReadParsedParam(UadGameData gameData, out float value)
    {
        value = 0f;
        try
        {
            return gameData.parms != null && gameData.parms.TryGetValue(MonthlyShipSpeedModParam, out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteRawParam(UadGameData gameData, float value)
    {
        try
        {
            if (gameData.paramsRaw == null ||
                !gameData.paramsRaw.TryGetValue(MonthlyShipSpeedModParam, out ParamData raw) ||
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
                $"UADVP campaign naval mobility: failed to write raw param {MonthlyShipSpeedModParam}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryWriteParsedParam(UadGameData gameData, float value)
    {
        try
        {
            if (gameData.parms == null || !gameData.parms.ContainsKey(MonthlyShipSpeedModParam))
                return false;

            gameData.parms[MonthlyShipSpeedModParam] = value;
            return true;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP campaign naval mobility: failed to write parsed param {MonthlyShipSpeedModParam}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void LogMissingParam()
    {
        if (loggedMissingParam)
            return;

        loggedMissingParam = true;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"UADVP campaign naval mobility: missing loaded param {MonthlyShipSpeedModParam}; cannot apply campaign mobility preset.");
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
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}
