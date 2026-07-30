using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Replace vanilla's budget/population/random army-logistics model with a
// deterministic coverage model: transports supply the army, and navy coverage
// is blended from effective projection and raw fleet tonnage.
[HarmonyPatch(typeof(Player), nameof(Player.LogisticsFactorFinal))]
internal static class CampaignArmyLogisticsBalancePatch
{
    private const float BaseRequiredNavyRating = 20f;
    private const float HomePortWeight = 0.5f;
    private const float ForeignPortWeight = 1.0f;
    private const float NonHomeProvinceWeight = 0.35f;
    private const float MinRequiredNavyRating = 20f;
    private const float MaxRequiredNavyRating = 100f;

    private const float BaseRequiredFleetTonnage = 100000f;
    private const float HomePortTonnageWeight = 3000f;
    private const float ForeignPortTonnageWeight = 6000f;
    private const float NonHomeProvinceTonnageWeight = 4000f;

    private const float PowerCoverageWeight = 0.5f;
    private const float TonnageCoverageWeight = 0.5f;
    private const float MediumCap = 30f;
    private const float MinorCap = 15f;

    private static readonly Dictionary<long, string> LastLoggedByPlayer = new();
    private static bool probingVanilla;
    private static string lastFailure = string.Empty;

    [HarmonyPrefix]
    private static bool Prefix(Player __instance, ref float __result)
    {
        if (probingVanilla || ModSettings.ArmyLogistics == ModSettings.ArmyLogisticsMode.Vanilla)
            return true;

        try
        {
            LogisticsBreakdown breakdown = Calculate(__instance);
            __result = breakdown.Result;
            LogSample(__instance, breakdown);
            return false;
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
            return true;
        }
    }

    private static LogisticsBreakdown Calculate(Player player)
    {
        Footprint footprint = BuildFootprint(player);
        FleetSummary fleet = BuildFleetSummary(player);
        TaskForceSummary taskForces = BuildTaskForceSummary(player);

        float transportCoverage = Math.Clamp(NormalizeTransportCapacity(Safe(() => player.transportCapacity, 0f)), 0f, 1f);
        float navyRating = Math.Max(0f, Safe(() => player.NavyPowerRating(), 0f));
        float totalProjection = Math.Max(0f, Safe(() => player.TotalPowerProjection(), 0f));
        float totalFleetTonnage = Math.Max(0f, Safe(() => player.TotalFleetTonnage(), 0f));
        float requiredNavyRating = Math.Clamp(
            BaseRequiredNavyRating +
            footprint.HomePorts * HomePortWeight +
            footprint.ForeignPorts * ForeignPortWeight +
            footprint.NonHomeProvinces * NonHomeProvinceWeight,
            MinRequiredNavyRating,
            MaxRequiredNavyRating);
        float powerCoverage = requiredNavyRating <= 0f
            ? 0f
            : Math.Clamp(navyRating / requiredNavyRating, 0f, 1f);

        float requiredFleetTonnage = Math.Max(
            1f,
            BaseRequiredFleetTonnage +
            footprint.HomePorts * HomePortTonnageWeight +
            footprint.ForeignPorts * ForeignPortTonnageWeight +
            footprint.NonHomeProvinces * NonHomeProvinceTonnageWeight);
        float tonnageCoverage = Math.Clamp(totalFleetTonnage / requiredFleetTonnage, 0f, 1f);
        float navyCoverage = Math.Clamp(
            powerCoverage * PowerCoverageWeight + tonnageCoverage * TonnageCoverageWeight,
            0f,
            1f);
        float cap = LogisticsCap(player);
        float result = Math.Clamp(100f * transportCoverage * navyCoverage, 1f, cap);

        return new LogisticsBreakdown(
            PlayerClass(player),
            transportCoverage,
            navyRating,
            totalProjection,
            totalFleetTonnage,
            requiredNavyRating,
            requiredFleetTonnage,
            powerCoverage,
            tonnageCoverage,
            navyCoverage,
            cap,
            result,
            footprint,
            fleet,
            taskForces);
    }

    private static Footprint BuildFootprint(Player player)
    {
        int provinces = CountProvinces(() => player.provinces);
        int homeProvinces = CountProvinces(() => player.homeProvinces);
        int ports = CountProvinces(() => player.provincesWithPort);
        int homePorts = CountProvinces(() => player.homeProvincesWithPort);

        return new Footprint(
            provinces,
            homeProvinces,
            Math.Max(0, provinces - homeProvinces),
            ports,
            homePorts,
            Math.Max(0, ports - homePorts));
    }

    private static FleetSummary BuildFleetSummary(Player player)
    {
        int ships = 0;
        int inPort = 0;
        int atSea = 0;
        int other = 0;

        try
        {
            var fleet = new Il2CppSystem.Collections.Generic.List<Ship>(player.fleet);
            foreach (Ship ship in fleet)
            {
                if (ship == null)
                    continue;

                ships++;
                if (Safe(() => ship.isInSea, false))
                    atSea++;
                else if (Safe(() => ship.isNormal, false))
                    inPort++;
                else
                    other++;
            }
        }
        catch
        {
            // Keep diagnostics best-effort. TotalFleetTonnage remains the
            // authoritative tonnage input for the formula.
        }

        return new FleetSummary(ships, inPort, atSea, other);
    }

    private static TaskForceSummary BuildTaskForceSummary(Player player)
    {
        int total = 0;
        int moving = 0;

        var taskForces = Safe(() => CampaignController.Instance?.CampaignData?.TaskForces, null);
        if (taskForces == null)
            return new TaskForceSummary(0, 0);

        try
        {
            foreach (CampaignController.TaskForce group in taskForces)
            {
                if (group == null || !SamePlayer(Safe(() => group.Controller, null), player))
                    continue;

                var vessels = Safe(() => group.Vessels, null);
                if (vessels == null || vessels.Count <= 0)
                    continue;

                total++;
                if (IsTaskForceMoving(group))
                    moving++;
            }
        }
        catch
        {
            return new TaskForceSummary(total, moving);
        }

        return new TaskForceSummary(total, moving);
    }

    private static bool IsTaskForceMoving(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        if (moving)
            return true;

        return Safe(() => group.Path != null && group.CurrentPositionIndex < group.Path.Length - 1, false);
    }

    private static float NormalizeTransportCapacity(float capacity)
    {
        if (float.IsNaN(capacity) || float.IsInfinity(capacity) || capacity <= 0f)
            return 0f;

        return capacity > 3f ? capacity / 100f : capacity;
    }

    private static float LogisticsCap(Player player)
    {
        if (Safe(() => player.isMajor, false))
            return 100f;

        if (Safe(() => player.isMedium, false))
            return MediumCap;

        return MinorCap;
    }

    private static string PlayerClass(Player player)
    {
        if (Safe(() => player.isMajor, false))
            return "major";

        if (Safe(() => player.isMedium, false))
            return "medium";

        return "minor";
    }

    private static int CountProvinces(Func<Il2CppSystem.Collections.Generic.List<Province>?> read)
    {
        try
        {
            return read()?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void LogSample(Player player, LogisticsBreakdown breakdown)
    {
        long key = Safe(() => player.Pointer.ToInt64(), 0L);
        string fingerprint =
            $"{breakdown.Class}:{Fmt(breakdown.TransportCoverage)}:{Fmt(breakdown.NavyRating)}:{Fmt(breakdown.TotalFleetTonnage)}:" +
            $"{Fmt(breakdown.RequiredNavyRating)}:{Fmt(breakdown.RequiredFleetTonnage)}:{breakdown.Footprint.Provinces}:" +
            $"{breakdown.Footprint.HomePorts}:{breakdown.Footprint.ForeignPorts}:{breakdown.Footprint.NonHomeProvinces}:{Fmt(breakdown.Result)}";
        if (LastLoggedByPlayer.TryGetValue(key, out string? last) && string.Equals(last, fingerprint, StringComparison.Ordinal))
            return;

        LastLoggedByPlayer[key] = fingerprint;
        float vanilla = ReadVanillaLogistics(player);
        float projectionPer100kTons = breakdown.TotalFleetTonnage <= 0f
            ? 0f
            : breakdown.TotalProjection / Math.Max(0.001f, breakdown.TotalFleetTonnage / 100000f);
        float projectionPerPort = breakdown.Footprint.Ports <= 0
            ? 0f
            : breakdown.TotalProjection / breakdown.Footprint.Ports;
        float projectionPerProvince = breakdown.Footprint.Provinces <= 0
            ? 0f
            : breakdown.TotalProjection / breakdown.Footprint.Provinces;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP army logistics: mode=Balanced player={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} class={breakdown.Class} " +
            $"vanilla={Fmt(vanilla)} result={Fmt(breakdown.Result)} cap={Fmt(breakdown.Cap)} transport={Fmt(breakdown.TransportCoverage)} " +
            $"navyRating={Fmt(breakdown.NavyRating)} requiredNavy={Fmt(breakdown.RequiredNavyRating)} powerCoverage={Fmt(breakdown.PowerCoverage)} " +
            $"fleetTons={Fmt(breakdown.TotalFleetTonnage)} requiredFleetTons={Fmt(breakdown.RequiredFleetTonnage)} tonnageCoverage={Fmt(breakdown.TonnageCoverage)} " +
            $"navyCoverage={Fmt(breakdown.NavyCoverage)} totalProjection={Fmt(breakdown.TotalProjection)} projPer100kTons={Fmt(projectionPer100kTons)} " +
            $"projPerPort={Fmt(projectionPerPort)} projPerProvince={Fmt(projectionPerProvince)} ships={breakdown.Fleet.Ships} " +
            $"inPort={breakdown.Fleet.InPort} atSea={breakdown.Fleet.AtSea} otherFleet={breakdown.Fleet.Other} " +
            $"taskForces={breakdown.TaskForces.Total} movingTF={breakdown.TaskForces.Moving} provinces={breakdown.Footprint.Provinces} " +
            $"homeProvinces={breakdown.Footprint.HomeProvinces} nonHomeProvinces={breakdown.Footprint.NonHomeProvinces} " +
            $"ports={breakdown.Footprint.Ports} homePorts={breakdown.Footprint.HomePorts} foreignPorts={breakdown.Footprint.ForeignPorts}.");
    }

    private static float ReadVanillaLogistics(Player player)
    {
        try
        {
            probingVanilla = true;
            return player.LogisticsFactorFinal();
        }
        catch
        {
            return -1f;
        }
        finally
        {
            probingVanilla = false;
        }
    }

    private static bool SamePlayer(Player? a, Player? b)
    {
        if (a == null || b == null)
            return false;

        return a == b || (a.data != null && b.data != null && a.data == b.data);
    }

    private static void LogFailureOnce(Exception ex)
    {
        string key = $"{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, key, StringComparison.Ordinal))
            return;

        lastFailure = key;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"UADVP army logistics: balanced calculation failed; falling back to vanilla for this call. {ex.GetType().Name}: {ex.Message}");
    }

    private static string Fmt(float value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().Replace(' ', '_');

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

    private readonly record struct Footprint(
        int Provinces,
        int HomeProvinces,
        int NonHomeProvinces,
        int Ports,
        int HomePorts,
        int ForeignPorts);

    private readonly record struct FleetSummary(int Ships, int InPort, int AtSea, int Other);

    private readonly record struct TaskForceSummary(int Total, int Moving);

    private readonly record struct LogisticsBreakdown(
        string Class,
        float TransportCoverage,
        float NavyRating,
        float TotalProjection,
        float TotalFleetTonnage,
        float RequiredNavyRating,
        float RequiredFleetTonnage,
        float PowerCoverage,
        float TonnageCoverage,
        float NavyCoverage,
        float Cap,
        float Result,
        Footprint Footprint,
        FleetSummary Fleet,
        TaskForceSummary TaskForces);
}
