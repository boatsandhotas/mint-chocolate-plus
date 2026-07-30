using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.Harmony;

// Campaign abstract sea-zone power should come from active local forces, not
// task forces that are only transiting through the area.
internal static class CampaignActiveSeaPowerPatch
{
    private const string LogPrefix = "UADVP active sea power";
    private static bool loggedActive;
    private static string lastFailure = string.Empty;

    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.PowerProjectionInAreaForPlayer))]
    private static class PowerProjectionInAreaForPlayerPatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
            => LogPatchAvailability(nameof(CampaignController.PowerProjectionInAreaForPlayer));

        [HarmonyPrefix]
        private static bool Prefix(CampaignController __instance, Area area, Player player, ref float __result)
        {
            try
            {
                __result = CalculatePowerProjection(__instance, area, player);
                LogActiveOnce();
                return false;
            }
            catch (Exception ex)
            {
                LogFailureOnce(nameof(CampaignController.PowerProjectionInAreaForPlayer), ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.EscortPowerAvgInAreaForPlayer))]
    private static class EscortPowerAvgInAreaForPlayerPatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
            => LogPatchAvailability(nameof(CampaignController.EscortPowerAvgInAreaForPlayer));

        [HarmonyPrefix]
        private static bool Prefix(CampaignController __instance, Area area, Player player, ref float __result)
        {
            try
            {
                __result = AveragePower(__instance, area, player, static vessel => Safe(() => vessel.EscortPower(), 0f));
                LogActiveOnce();
                return false;
            }
            catch (Exception ex)
            {
                LogFailureOnce(nameof(CampaignController.EscortPowerAvgInAreaForPlayer), ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.RaidPowerAvgInAreaForPlayer))]
    private static class RaidPowerAvgInAreaForPlayerPatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
            => LogPatchAvailability(nameof(CampaignController.RaidPowerAvgInAreaForPlayer));

        [HarmonyPrefix]
        private static bool Prefix(CampaignController __instance, Area area, Player player, ref float __result)
        {
            try
            {
                __result = AveragePower(__instance, area, player, static vessel => Safe(() => vessel.RaidingPower(), 0f));
                LogActiveOnce();
                return false;
            }
            catch (Exception ex)
            {
                LogFailureOnce(nameof(CampaignController.RaidPowerAvgInAreaForPlayer), ex);
                return true;
            }
        }
    }

    private static float CalculatePowerProjection(CampaignController campaign, Area area, Player player)
    {
        float total = 0f;
        foreach (VesselEntity vessel in GetActiveAreaVessels(campaign, area, player))
        {
            if (!Safe(() => vessel.NormalVessel(), false))
                continue;

            total += Math.Max(0f, Safe(() => vessel.PowerProjection(false), 0f));
        }

        return total;
    }

    private static float AveragePower(CampaignController campaign, Area area, Player player, Func<VesselEntity, float> readPower)
    {
        float total = 0f;
        int count = 0;

        foreach (VesselEntity vessel in GetActiveAreaVessels(campaign, area, player))
        {
            if (!IsEligibleForAveragePower(vessel))
                continue;

            total += Math.Max(0f, readPower(vessel));
            count++;
        }

        return count <= 0 ? 0f : total / count;
    }

    private static List<VesselEntity> GetActiveAreaVessels(CampaignController campaign, Area area, Player player)
    {
        List<VesselEntity> active = new();
        if (campaign == null || area == null || player == null)
            return active;

        var areaVessels = Safe(() => campaign.VesselsInAreaOfPlayer(area, player, false), null);
        if (areaVessels == null)
            return active;

        HashSet<IntPtr> movingVesselIds = MovingTaskForceVesselIds(campaign, player);
        foreach (VesselEntity vessel in areaVessels)
        {
            if (vessel == null)
                continue;

            if (movingVesselIds.Contains(vessel.Pointer))
                continue;

            active.Add(vessel);
        }

        return active;
    }

    private static HashSet<IntPtr> MovingTaskForceVesselIds(CampaignController campaign, Player player)
    {
        HashSet<IntPtr> ids = new();
        var taskForces = Safe(() => campaign.CampaignData?.TaskForces, null);
        if (taskForces == null)
            return ids;

        foreach (CampaignController.TaskForce group in taskForces)
        {
            if (group == null || !SamePlayer(Safe(() => group.Controller, null), player))
                continue;

            if (!IsTaskForceMoving(group))
                continue;

            var vessels = Safe(() => group.Vessels, null);
            if (vessels == null)
                continue;

            foreach (VesselEntity vessel in vessels)
            {
                if (vessel != null)
                    ids.Add(vessel.Pointer);
            }
        }

        return ids;
    }

    private static bool IsEligibleForAveragePower(VesselEntity? vessel)
    {
        if (vessel == null)
            return false;

        if (!Safe(() => vessel.NormalVessel(), false))
            return false;

        if (Safe(() => vessel.isRepairing, false) ||
            Safe(() => vessel.isCommissioning, false) ||
            Safe(() => vessel.isBuilding, false) ||
            Safe(() => vessel.isRefit, false) ||
            Safe(() => vessel.isSunk, false) ||
            Safe(() => vessel.isScrapped, false) ||
            Safe(() => vessel.isMothballed, false) ||
            Safe(() => vessel.isLowCrew, false))
        {
            return false;
        }

        return true;
    }

    private static bool IsTaskForceMoving(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        if (moving)
            return true;

        return Safe(() => group.Path != null && group.CurrentPositionIndex < group.Path.Length - 1, false);
    }

    private static bool SamePlayer(Player? a, Player? b)
    {
        if (a == null || b == null)
            return false;

        return a == b || (a.data != null && b.data != null && a.data == b.data);
    }

    private static bool LogPatchAvailability(string methodName)
    {
        bool available = AccessTools.Method(typeof(CampaignController), methodName) != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: target missing {methodName}; vanilla area power remains unchanged.");

        return available;
    }

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: moving task forces excluded from PP/escort/raid area calculations.");
    }

    private static void LogFailureOnce(string methodName, Exception ex)
    {
        string key = $"{methodName}:{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, key, StringComparison.Ordinal))
            return;

        lastFailure = key;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: {methodName} failed; falling back to vanilla for this call. {ex.GetType().Name}: {ex.Message}");
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
}
