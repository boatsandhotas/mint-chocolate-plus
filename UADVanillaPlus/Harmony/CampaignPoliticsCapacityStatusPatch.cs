using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace UADVanillaPlus.Harmony;

[HarmonyPatch]
internal static class CampaignPoliticsCapacityStatusPatch
{
    private const string GoodColor = "#A7D37A";
    private const string WarnColor = "#D8C06A";
    private const string BadColor = "#D37A7A";
    private static bool loggedActive;
    private static string lastFailure = string.Empty;

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignPolitics_ElementUI), "GetFinancialInfoOther", new[] { typeof(Player) });

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP politics capacity status unavailable: CampaignPolitics_ElementUI.GetFinancialInfoOther not found.");

        return available;
    }

    [HarmonyPostfix]
    private static void GetFinancialInfoOtherPostfix(Player p, ref string __result)
    {
        try
        {
            if (p == null || string.IsNullOrEmpty(__result))
                return;

            __result = $"{__result}\n{BuildCapacityLines(p)}";
            LogActiveOnce();
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
        }
    }

    private static string BuildCapacityLines(Player player)
        => $"{BuildDockyardLine(player)}\n{BuildBuildCapacityLine(player)}";

    private static string BuildDockyardLine(Player player)
    {
        float dockyard = Safe(() => player.MaxShipyard(), 0f);
        return $"Dockyard: <color={GoodColor}>{FormatWeightSafe(dockyard)}</color>";
    }

    private static string BuildBuildCapacityLine(Player player)
    {
        float used = Safe(() => player.ShipTonnageUnderConstruction(), 0f);
        float limit = Safe(() => player.ShipbuildingCapacityLimit(), 0f);
        float percent = limit > 0f ? used / limit * 100f : 0f;
        string color = used > limit + 0.5f
            ? BadColor
            : percent >= 90f
                ? WarnColor
                : GoodColor;

        return $"Build Cap: <color={color}>{FormatWeightSafe(used)} / {FormatWeightSafe(limit)}</color>";
    }

    private static string FormatWeightSafe(float tons)
    {
        try
        {
            return Ui.FormatWeight(tons, true, false);
        }
        catch
        {
            return $"{Mathf.RoundToInt(tons):N0} t";
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP politics capacity status active: appended dockyard and build capacity to financial info rows.");
    }

    private static void LogFailureOnce(Exception ex)
    {
        string failure = $"{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, failure, StringComparison.Ordinal))
            return;

        lastFailure = failure;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"UADVP politics capacity status failed; leaving vanilla financial info intact. {ex.GetType().Name}: {ex.Message}");
    }
}
