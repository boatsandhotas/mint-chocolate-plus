using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Sea-zone transport losses should use ships that are actually active in the
// area, not task forces that are merely passing through on strategic movement.
internal static class CampaignTransportLossAreaVesselScope
{
    private const string LogPrefix = "UADVP TR active area vessels";
    private const string SnapshotStage = "SubmarineBattles";
    private const string LossStage = "TransportLoses";
    private const int MaxTimingTokenLogs = 12;

    private static readonly HashSet<IntPtr> MovingVesselIds = new();
    private static readonly HashSet<string> SeenTimingTokens = new(StringComparer.Ordinal);
    private static bool isActive;
    private static IntPtr activeCampaign;
    private static int totalTaskForces;
    private static int movingTaskForces;
    private static int calls;
    private static int filteredCalls;
    private static int removedVessels;
    private static int returnedVessels;
    private static int emptyResults;
    private static bool loggedNoCache;
    private static bool loggedDuplicateBegin;
    private static string lastFailure = string.Empty;

    internal static bool IsActive => isActive;

    internal static void OnTimingToken(CampaignController? campaign, string? msg, string source)
    {
        if (!ModSettings.SeaTransportLossesActiveForcesEnabled)
            return;

        LogTimingTokenOnce(msg, source);

        if (campaign == null || string.IsNullOrWhiteSpace(msg))
            return;

        if (string.Equals(msg, SnapshotStage, StringComparison.Ordinal))
            Begin(campaign, "MeasureTime");
        else if (string.Equals(msg, LossStage, StringComparison.Ordinal))
            End("MeasureTime");
    }

    internal static void BeginForTransportLossState(CampaignController? campaign, string source)
    {
        if (ModSettings.SeaTransportLossesActiveForcesEnabled)
            Begin(campaign, source);
    }

    internal static void EndForTransportLossState(string source)
    {
        if (ModSettings.SeaTransportLossesActiveForcesEnabled)
            End(source);
        else
            Reset();
    }

    internal static void Begin(CampaignController? campaign, string source = "TransportLoses window")
    {
        if (!ModSettings.SeaTransportLossesActiveForcesEnabled)
        {
            Reset();
            return;
        }

        if (isActive && !loggedDuplicateBegin)
        {
            loggedDuplicateBegin = true;
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: begin requested while already active; replacing previous context source={source}.");
        }

        Reset();
        if (campaign == null)
            return;

        try
        {
            activeCampaign = campaign.Pointer;
            var taskForces = Safe(() => campaign.CampaignData?.TaskForces, null);
            if (taskForces != null)
            {
                foreach (CampaignController.TaskForce group in taskForces)
                {
                    if (group == null)
                        continue;

                    totalTaskForces++;
                    if (!IsTaskForceMoving(group))
                        continue;

                    movingTaskForces++;
                    var vessels = Safe(() => group.Vessels, null);
                    if (vessels == null)
                        continue;

                    foreach (VesselEntity vessel in vessels)
                    {
                        if (vessel != null)
                            MovingVesselIds.Add(vessel.Pointer);
                    }
                }
            }

            isActive = true;
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: active for {source} movingVessels={MovingVesselIds.Count} movingTF={movingTaskForces}/{totalTaskForces}.");
        }
        catch (Exception ex)
        {
            Reset();
            LogFailureOnce("begin", ex);
        }
    }

    internal static void End(string source = "TransportLoses window")
    {
        if (!isActive && activeCampaign == IntPtr.Zero && MovingVesselIds.Count == 0)
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: summary source={source} calls={calls} filteredCalls={filteredCalls} movingVessels={MovingVesselIds.Count} " +
            $"movingTF={movingTaskForces}/{totalTaskForces} removed={removedVessels} returned={returnedVessels} empty={emptyResults}.");
        Reset();
    }

    internal static bool TryFilter(
        CampaignController? campaign,
        Area? area,
        Player? player,
        ref Il2CppSystem.Collections.Generic.List<VesselEntity>? result)
    {
        if (!ModSettings.SeaTransportLossesActiveForcesEnabled)
            return false;

        if (!isActive || campaign == null || area == null || player == null)
            return false;

        if (activeCampaign != IntPtr.Zero && campaign.Pointer != activeCampaign)
            return false;

        try
        {
            calls++;
            var byArea = Safe(() => campaign.CampaignData?.VesselsInAreaOfPlayer, null);
            if (byArea == null)
            {
                LogNoCacheOnce();
                return false;
            }

            if (!byArea.ContainsKey(area))
            {
                result = new Il2CppSystem.Collections.Generic.List<VesselEntity>();
                emptyResults++;
                return true;
            }

            var byPlayer = byArea[area];
            if (byPlayer == null || !byPlayer.ContainsKey(player))
            {
                result = new Il2CppSystem.Collections.Generic.List<VesselEntity>();
                emptyResults++;
                return true;
            }

            var original = byPlayer[player];
            if (original == null)
            {
                result = new Il2CppSystem.Collections.Generic.List<VesselEntity>();
                emptyResults++;
                return true;
            }

            Il2CppSystem.Collections.Generic.List<VesselEntity> filtered = new();
            int removed = 0;
            foreach (VesselEntity vessel in original)
            {
                if (vessel == null)
                    continue;

                if (MovingVesselIds.Contains(vessel.Pointer))
                {
                    removed++;
                    continue;
                }

                filtered.Add(vessel);
            }

            result = filtered;
            returnedVessels += filtered.Count;
            if (filtered.Count == 0)
                emptyResults++;

            if (removed > 0)
            {
                filteredCalls++;
                removedVessels += removed;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogFailureOnce("filter", ex);
            return false;
        }
    }

    private static void Reset()
    {
        isActive = false;
        activeCampaign = IntPtr.Zero;
        MovingVesselIds.Clear();
        totalTaskForces = 0;
        movingTaskForces = 0;
        calls = 0;
        filteredCalls = 0;
        removedVessels = 0;
        returnedVessels = 0;
        emptyResults = 0;
        loggedNoCache = false;
    }

    private static bool IsTaskForceMoving(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        if (moving)
            return true;

        return Safe(() => group.Path != null && group.CurrentPositionIndex < group.Path.Length - 1, false);
    }

    private static void LogNoCacheOnce()
    {
        if (loggedNoCache)
            return;

        loggedNoCache = true;
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: campaign area-vessel cache unavailable; leaving vanilla input for this call.");
    }

    private static void LogTimingTokenOnce(string? msg, string source)
    {
        if (SeenTimingTokens.Count >= MaxTimingTokenLogs)
            return;

        string token = string.IsNullOrWhiteSpace(msg) ? "<blank>" : msg.Trim();
        string key = source + ":" + token;
        if (!SeenTimingTokens.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} timing: source={source} token={token}.");
    }

    private static void LogFailureOnce(string action, Exception ex)
    {
        string key = $"{action}:{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, key, StringComparison.Ordinal))
            return;

        lastFailure = key;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: {action} failed; leaving vanilla area vessels for this call. {ex.GetType().Name}: {ex.Message}");
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

[HarmonyPatch(typeof(CampaignController), "MeasureTime", new[] { typeof(string) })]
internal static class CampaignTransportLossActiveVesselMeasureTimePatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        MethodBase? target = AccessTools.Method(typeof(CampaignController), "MeasureTime", new[] { typeof(string) });
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP TR active area vessels proof: MeasureTime target={(target != null ? "yes" : "no")}.");
        return target != null;
    }

    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance, string __0)
        => CampaignTransportLossAreaVesselScope.OnTimingToken(__instance, __0, "MeasureTime");
}

[HarmonyPatch]
internal static class CampaignTransportLossActiveVesselMeasureMethodPatch
{
    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "MeasureMethod", new[] { typeof(string) });

    [HarmonyPrepare]
    private static bool Prepare()
    {
        MethodBase? target = TargetMethod();
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP TR active area vessels proof: MeasureMethod target={(target != null ? "yes" : "no")}.");
        return target != null;
    }

    [HarmonyPostfix]
    private static void Postfix(string __0)
    {
        try
        {
            CampaignController? campaign = CampaignController.Instance;
            CampaignTransportLossAreaVesselScope.OnTimingToken(campaign, __0, "MeasureMethod");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP TR active area vessels: MeasureMethod hook failed; transport loss input remains vanilla for this callback. {ex.GetType().Name}: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.VesselsInAreaOfPlayer), new[] { typeof(Area), typeof(Player), typeof(bool) })]
internal static class CampaignTransportLossAreaVesselsInAreaPatch
{
    private static bool loggedActive;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        MethodBase? target = AccessTools.Method(
            typeof(CampaignController),
            nameof(CampaignController.VesselsInAreaOfPlayer),
            new[] { typeof(Area), typeof(Player), typeof(bool) });
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP TR active area vessels proof: VesselsInAreaOfPlayer target={(target != null ? "yes" : "no")}.");
        return target != null;
    }

    [HarmonyPrefix]
    private static bool Prefix(
        CampaignController __instance,
        Area area,
        Player player,
        bool force,
        ref Il2CppSystem.Collections.Generic.List<VesselEntity>? __result)
    {
        CampaignTransportLossNextTurnStateDiagnosticsPatch.RecordVesselsInAreaCall(__instance, area, player);

        if (!CampaignTransportLossAreaVesselScope.IsActive)
            return true;

        if (!CampaignTransportLossAreaVesselScope.TryFilter(__instance, area, player, ref __result))
            return true;

        LogActiveOnce();
        return false;
    }

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP TR active area vessels: VesselsInAreaOfPlayer filtered during TransportLoses window.");
    }
}
