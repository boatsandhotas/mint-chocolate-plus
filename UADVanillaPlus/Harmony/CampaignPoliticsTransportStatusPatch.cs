using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.Harmony;

[HarmonyPatch]
internal static class CampaignPoliticsTransportStatusPatch
{
    private const string TransportGoodColor = "#A7D37A";
    private const string TransportWarnColor = "#D8C06A";
    private const string TransportBadColor = "#D37A7A";
    private static bool loggedActive;
    private static string lastFailure = string.Empty;

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignPolitics_ElementUI), "GetNavalInfo", new[] { typeof(Player) });

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP politics transport status unavailable: CampaignPolitics_ElementUI.GetNavalInfo not found.");

        return available;
    }

    [HarmonyPostfix]
    private static void GetNavalInfoPostfix(Player p, ref string __result)
    {
        try
        {
            if (p == null || string.IsNullOrEmpty(__result))
                return;

            __result = $"{__result}\n{BuildTransportLine(p)}";
            LogActiveOnce();
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
        }
    }

    private static string BuildTransportLine(Player player)
    {
        float capacity = player.transportCapacity;
        float percent = capacity <= 3f ? capacity * 100f : capacity;
        string color = percent >= 190f ? TransportGoodColor : percent >= 150f ? TransportWarnColor : TransportBadColor;

        return $"TR Capacity: <color={color}>{percent.ToString("0", CultureInfo.InvariantCulture)}%</color>";
    }

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP politics transport status active: appended transport capacity to naval info rows.");
    }

    private static void LogFailureOnce(Exception ex)
    {
        string failure = $"{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, failure, StringComparison.Ordinal))
            return;

        lastFailure = failure;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"UADVP politics transport status failed; leaving vanilla naval info intact. {ex.GetType().Name}: {ex.Message}");
    }
}
