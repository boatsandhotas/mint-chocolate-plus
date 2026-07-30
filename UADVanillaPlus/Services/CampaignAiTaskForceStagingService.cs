using System.Diagnostics;
using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UnityEngine;

namespace UADVanillaPlus.Services;

internal static class CampaignAiTaskForceStagingService
{
    private const string LogPrefix = "UADVP ai taskforce staging";
    private const int MaxDetailEntries = 8;
    private const int MaxSkipReasons = 8;
    private const int MaxSkipSamplesPerScan = 5;
    private const int MaxTurnSkipSamples = 8;
    private const int MaxClusterGroupIds = 10;
    private const int MinSurfaceClusterVessels = 3;
    private const int MinSubmarineClusterVessels = 3;
    private const float MinSurfaceClusterTonnage = 5000f;
    private const float MinMovementDistance = 1f;
    private const float RendezvousRadius = 1f;
    private const float TinyStopOffset = 0.15f;

    private static readonly Dictionary<Guid, string> StagedGroupRoles = new();
    private static readonly Dictionary<Guid, string> StagedGroupLabels = new();
    private static readonly TurnSummary PendingSummary = new();
    private static string lastFailure = string.Empty;

    internal static void AfterMoveVesselsCompleted(CampaignController? campaign, Player? player)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ScanStats stats = new();
        string playerLabel = PlayerLabel(player);

        try
        {
            stats.Scans = 1;
            PendingSummary.Scans++;

            if (!ModSettings.AiTaskForceStagingEnabled)
            {
                LogScan(player, stats, "disabled_option", stopwatch.ElapsedMilliseconds);
                return;
            }

            if (campaign == null)
            {
                PendingSummary.UnsafeSkips++;
                LogScan(player, stats, "no_campaign_data", stopwatch.ElapsedMilliseconds);
                return;
            }

            if (!IsEligiblePlayer(player, out string playerSkip))
            {
                if (player != null)
                    LogScan(player, stats, playerSkip, stopwatch.ElapsedMilliseconds);
                return;
            }

            var taskForces = Safe(() => campaign.CampaignData?.TaskForces, null);
            if (taskForces == null)
            {
                PendingSummary.UnsafeSkips++;
                LogScan(player, stats, "no_taskforces", stopwatch.ElapsedMilliseconds);
                return;
            }

            List<GroupCandidate> eligible = new();
            List<GroupCandidate> safeAnchors = new();
            foreach (CampaignController.TaskForce group in taskForces)
            {
                stats.GroupsTotal++;
                if (TryBuildCandidate(player!, group, out GroupCandidate candidate, out string skipReason))
                {
                    eligible.Add(candidate);
                    stats.Eligible++;
                    PendingSummary.EligibleGroups++;
                }
                else
                {
                    RecordSkip(skipReason, stats, player!, group);
                }

                if (TryBuildSafeAnchorCandidate(player!, group, out GroupCandidate anchorCandidate))
                    safeAnchors.Add(anchorCandidate);
            }

            HashSet<Guid> consumedGroups = new();
            ProcessInferredRendezvousClusters(player!, eligible, safeAnchors, stats, consumedGroups);

            List<IGrouping<ClusterKey, GroupCandidate>> clusters = eligible
                .Where(candidate => !consumedGroups.Contains(candidate.Id))
                .GroupBy(static candidate => candidate.Key)
                .ToList();
            stats.Clusters = clusters.Count;
            PendingSummary.Clusters += clusters.Count;

            foreach (IGrouping<ClusterKey, GroupCandidate> cluster in clusters)
                ProcessCluster(player!, cluster, stats);

            LogScan(player, stats, "ok", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            PendingSummary.UnsafeSkips++;
            LogFailureOnce("scan", ex);
            LogScan(player, stats, "exception", stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            stopwatch.Stop();
            PendingSummary.ElapsedMs += stopwatch.ElapsedMilliseconds;
            PendingSummary.MaxMs = Math.Max(PendingSummary.MaxMs, stopwatch.ElapsedMilliseconds);
            PendingSummary.PlayersTouched.Add(LogToken(playerLabel));
        }
    }

    internal static MergeSnapshot BeforeMerge(CampaignController.TaskForce? mergeTo, CampaignController.TaskForce? group)
    {
        bool staged = IsTracked(mergeTo) || IsTracked(group);
        return new MergeSnapshot(
            staged,
            GroupId(mergeTo),
            GroupId(group),
            VesselCount(mergeTo),
            VesselCount(group),
            Safe(() => mergeTo?.BattleTonnage() ?? 0f, 0f),
            Safe(() => group?.BattleTonnage() ?? 0f, 0f));
    }

    internal static void AfterMerge(CampaignController.TaskForce? mergeTo, CampaignController.TaskForce? group, MergeSnapshot snapshot)
    {
        if (!snapshot.Staged)
            return;

        PendingSummary.MergesObserved++;
        string mergeToId = ShortId(snapshot.MergeToId);
        string groupId = ShortId(snapshot.GroupId);
        string roles = $"{RoleFor(snapshot.MergeToId)}+{RoleFor(snapshot.GroupId)}";

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: merge observed mergeTo={mergeToId} group={groupId} roles={LogToken(roles)} " +
            $"countsBefore={snapshot.MergeToCount}+{snapshot.GroupCount} countsAfter={VesselCount(mergeTo)} " +
            $"tonsBefore={Fmt(snapshot.MergeToTonnage)}+{Fmt(snapshot.GroupTonnage)} tonsAfter={Fmt(Safe(() => mergeTo?.BattleTonnage() ?? 0f, 0f))}.");

        if (mergeTo != null)
            MarkTracked(mergeTo, "merged-anchor");
    }

    internal static void BeforeRemove(CampaignController.TaskForce? group, bool returnFromSea)
    {
        if (!IsTracked(group))
            return;

        PendingSummary.RemovalsObserved++;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: remove observed group={ShortId(GroupId(group))} role={LogToken(RoleFor(GroupId(group)))} " +
            $"returnFromSea={returnFromSea} vessels={VesselCount(group)} tons={Fmt(Safe(() => group?.BattleTonnage() ?? 0f, 0f))}.");
    }

    internal static void FlushPendingTurnSummary()
    {
        string turn = LogToken(AiDesignCompetitiveness.CurrentTurnLabel());
        if (PendingSummary.Scans <= 0 &&
            PendingSummary.Clusters <= 0 &&
            PendingSummary.AnchorsStopped <= 0 &&
            PendingSummary.RedirectsOk <= 0 &&
            PendingSummary.MergesObserved <= 0 &&
            PendingSummary.RemovalsObserved <= 0)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} summary turn={turn} entries=0.");
            ClearTurnState();
            return;
        }

        string players = PendingSummary.PlayersTouched.Count == 0
            ? "none"
            : string.Join(",", PendingSummary.PlayersTouched.Take(MaxDetailEntries));
        string details = PendingSummary.Details.Count == 0
            ? "none"
            : string.Join(" | ", PendingSummary.Details.Take(MaxDetailEntries));
        if (PendingSummary.Details.Count > MaxDetailEntries)
            details += $" +{PendingSummary.Details.Count - MaxDetailEntries} more";
        string skipReasons = SkipReasonSummary(PendingSummary.SkipReasons);
        string skipSamples = PendingSummary.SkipSamples.Count == 0
            ? "none"
            : string.Join(" | ", PendingSummary.SkipSamples.Take(MaxTurnSkipSamples));

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} summary turn={turn} scans={PendingSummary.Scans} players={players} eligibleGroups={PendingSummary.EligibleGroups} " +
            $"clusters={PendingSummary.Clusters} rendezvousAnchors={PendingSummary.RendezvousAnchors} rendezvousRestops={PendingSummary.RendezvousRestops} " +
            $"rendezvousStragglers={PendingSummary.RendezvousStragglers} anchorsStopped={PendingSummary.AnchorsStopped} stopFallbacks={PendingSummary.StopFallbacks} " +
            $"redirectsOk={PendingSummary.RedirectsOk} redirectsFail={PendingSummary.RedirectsFail} capSkips={PendingSummary.CapSkips} " +
            $"rendezvousSkippedCap={PendingSummary.RendezvousSkippedCap} rendezvousSkippedUnsafe={PendingSummary.RendezvousSkippedUnsafe} " +
            $"minStrengthSkips={PendingSummary.MinStrengthSkips} unsafeSkips={PendingSummary.UnsafeSkips} mergesObserved={PendingSummary.MergesObserved} removalsObserved={PendingSummary.RemovalsObserved} " +
            $"skipReasons={LogToken(skipReasons)} skipSamples={LogToken(skipSamples)} elapsedMs={PendingSummary.ElapsedMs} maxMs={PendingSummary.MaxMs} details={LogToken(details)}.");

        ClearTurnState();
    }

    private static void ProcessInferredRendezvousClusters(
        Player player,
        List<GroupCandidate> movingCandidates,
        List<GroupCandidate> safeAnchors,
        ScanStats stats,
        HashSet<Guid> consumedGroups)
    {
        if (movingCandidates.Count == 0 || safeAnchors.Count == 0)
            return;

        Dictionary<Guid, GroupCandidate> anchorsById = new();
        Dictionary<Guid, List<RendezvousMatch>> matchesByAnchor = new();

        foreach (GroupCandidate straggler in movingCandidates)
        {
            if (!TryFindRendezvousAnchor(straggler, safeAnchors, out GroupCandidate anchor, out float distance))
                continue;

            anchorsById[anchor.Id] = anchor;
            if (!matchesByAnchor.TryGetValue(anchor.Id, out List<RendezvousMatch>? matches))
            {
                matches = new List<RendezvousMatch>();
                matchesByAnchor[anchor.Id] = matches;
            }

            matches.Add(new RendezvousMatch(straggler, distance));
        }

        foreach (KeyValuePair<Guid, List<RendezvousMatch>> pair in matchesByAnchor
                     .OrderByDescending(pair => anchorsById[pair.Key].Tonnage)
                     .ThenByDescending(pair => anchorsById[pair.Key].VesselCount))
        {
            GroupCandidate anchor = anchorsById[pair.Key];
            if (consumedGroups.Contains(anchor.Id))
                continue;

            List<RendezvousMatch> matches = pair.Value
                .Where(match => !consumedGroups.Contains(match.Straggler.Id))
                .OrderBy(static match => match.Distance)
                .ToList();
            if (matches.Count == 0)
                continue;

            ProcessInferredRendezvousCluster(player, anchor, matches, stats, consumedGroups);
        }
    }

    private static bool TryFindRendezvousAnchor(
        GroupCandidate straggler,
        List<GroupCandidate> safeAnchors,
        out GroupCandidate anchor,
        out float distance)
    {
        anchor = default;
        distance = float.MaxValue;
        Vector3 destination = Safe(() => straggler.Group.DestinationPosition(), Safe(() => straggler.Group.ToPosition, Vector3.zero));
        if (destination == Vector3.zero)
            return false;

        bool found = false;
        foreach (GroupCandidate candidate in safeAnchors)
        {
            if (candidate.Id == straggler.Id || candidate.Type != straggler.Type)
                continue;

            Vector3 candidatePosition = Safe(() => candidate.Group.CurrentPosition(), Vector3.zero);
            float candidateDistance = Vector3.Distance(destination, candidatePosition);
            if (candidateDistance > RendezvousRadius)
                continue;

            if (!found ||
                candidateDistance < distance - 0.001f ||
                (Math.Abs(candidateDistance - distance) <= 0.001f && IsBetterRendezvousAnchor(candidate, anchor)))
            {
                anchor = candidate;
                distance = candidateDistance;
                found = true;
            }
        }

        return found;
    }

    private static bool IsBetterRendezvousAnchor(GroupCandidate candidate, GroupCandidate current)
    {
        if (candidate.Tonnage > current.Tonnage + 0.001f)
            return true;
        if (candidate.Tonnage < current.Tonnage - 0.001f)
            return false;

        return candidate.VesselCount > current.VesselCount;
    }

    private static void ProcessInferredRendezvousCluster(
        Player player,
        GroupCandidate anchor,
        List<RendezvousMatch> matches,
        ScanStats stats,
        HashSet<Guid> consumedGroups)
    {
        List<GroupCandidate> groups = new(capacity: matches.Count + 1) { anchor };
        groups.AddRange(matches.Select(static match => match.Straggler));
        ClusterKey key = anchor.Key;

        stats.RendezvousAnchors++;
        PendingSummary.RendezvousAnchors++;

        string matchSummary = string.Join(",", matches.Take(MaxClusterGroupIds).Select(static match => $"{ShortId(match.Straggler.Id)}:{Fmt(match.Distance)}"));
        if (matches.Count > MaxClusterGroupIds)
            matchSummary += $"+{matches.Count - MaxClusterGroupIds}";

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: rendezvous detected player={LogToken(PlayerLabel(player))} type={key.TypeLabel} anchor={ShortId(anchor.Id)} " +
            $"anchorPos={FormatVector(Safe(() => anchor.Group.CurrentPosition(), Vector3.zero))} " +
            $"anchorDestBefore={FormatVector(Safe(() => anchor.Group.DestinationPosition(), Vector3.zero))} " +
            $"stragglers={LogToken(matchSummary)} radius={Fmt(RendezvousRadius)}.");

        if (!TryPlanCompatibleStragglers(player, key, groups, anchor, out List<PlannedStraggler> plannedStragglers, out string planReason))
        {
            if (IsCapSkip(planReason))
            {
                stats.CapSkips++;
                stats.RendezvousSkippedCap++;
                PendingSummary.CapSkips++;
                PendingSummary.RendezvousSkippedCap++;
            }
            else
            {
                stats.UnsafeSkips++;
                stats.RendezvousSkippedUnsafe++;
                PendingSummary.UnsafeSkips++;
                PendingSummary.RendezvousSkippedUnsafe++;
            }

            AddDetail($"{PlayerLabel(player)}:{key.TypeLabel}:rendezvous:{ShortId(anchor.Id)}:skip={planReason}");
            LogRendezvousSkip(player, anchor, planReason, matches.Count);
            return;
        }

        List<GroupCandidate> compatibleGroups = new(capacity: plannedStragglers.Count + 1) { anchor };
        compatibleGroups.AddRange(plannedStragglers.Select(static planned => planned.Candidate));
        if (!MeetsMinimumClusterStrength(key, compatibleGroups, out string strengthReason))
        {
            stats.MinStrengthSkips++;
            PendingSummary.MinStrengthSkips++;
            AddDetail($"{PlayerLabel(player)}:{key.TypeLabel}:rendezvous:{ShortId(anchor.Id)}:skip={strengthReason}");
            LogRendezvousSkip(player, anchor, strengthReason, matches.Count);
            return;
        }

        if (!TryStopAnchor(anchor.Group, out string stopMode))
        {
            stats.AnchorStopFailed++;
            stats.RendezvousSkippedUnsafe++;
            PendingSummary.UnsafeSkips++;
            PendingSummary.RendezvousSkippedUnsafe++;
            AddDetail($"{PlayerLabel(player)}:{key.TypeLabel}:rendezvous:{ShortId(anchor.Id)}:skip=anchor_stop_failed");
            LogRendezvousSkip(player, anchor, "anchor_stop_failed", matches.Count);
            return;
        }

        stats.AnchorsStopped++;
        stats.RendezvousRestops++;
        PendingSummary.AnchorsStopped++;
        PendingSummary.RendezvousRestops++;
        if (string.Equals(stopMode, "fallback", StringComparison.Ordinal))
        {
            stats.StopFallbacks++;
            PendingSummary.StopFallbacks++;
        }

        MarkTracked(anchor.Group, "rendezvous-anchor");
        consumedGroups.Add(anchor.Id);
        Vector3 anchorPosition = Safe(() => anchor.Group.CurrentPosition(), Vector3.zero);
        int skipped = Math.Max(0, matches.Count - plannedStragglers.Count);
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: rendezvous restop player={LogToken(PlayerLabel(player))} anchor={ShortId(anchor.Id)} mode={LogToken(stopMode)} " +
            $"compatible={plannedStragglers.Count} skipped={skipped} pos={FormatVector(anchorPosition)} pathAfter={PathSummary(anchor.Group)}.");

        foreach (PlannedStraggler plannedStraggler in plannedStragglers)
        {
            GroupCandidate straggler = plannedStraggler.Candidate;
            if (TryRedirectStraggler(player, anchor, straggler, anchorPosition, stats))
            {
                stats.RendezvousStragglers++;
                PendingSummary.RendezvousStragglers++;
                consumedGroups.Add(straggler.Id);
            }
        }
    }

    private static void LogRendezvousSkip(Player player, GroupCandidate anchor, string reason, int stragglerCount)
    {
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: rendezvous skip player={LogToken(PlayerLabel(player))} anchor={ShortId(anchor.Id)} " +
            $"reason={LogToken(reason)} stragglers={stragglerCount}.");
    }

    private static void ProcessCluster(Player player, IGrouping<ClusterKey, GroupCandidate> cluster, ScanStats stats)
    {
        List<GroupCandidate> groups = cluster
            .OrderByDescending(static candidate => candidate.Tonnage)
            .ThenBy(static candidate => candidate.DistancePerTurn)
            .ToList();

        if (groups.Count < 2)
        {
            stats.SingleGroupClusters++;
            AddDetail($"{PlayerLabel(player)}:{cluster.Key.TypeLabel}:{cluster.Key.Theater}:skip=single_group_cluster");
            LogCluster(player, cluster.Key, groups, "single_group_cluster", null);
            return;
        }

        if (!MeetsMinimumClusterStrength(cluster.Key, groups, out string strengthReason))
        {
            stats.MinStrengthSkips++;
            PendingSummary.MinStrengthSkips++;
            AddDetail($"{PlayerLabel(player)}:{cluster.Key.TypeLabel}:{cluster.Key.Theater}:skip={strengthReason}");
            LogCluster(player, cluster.Key, groups, strengthReason, null);
            return;
        }

        GroupCandidate anchor = PickAnchor(groups);
        LogCluster(player, cluster.Key, groups, "candidate", anchor);
        if (!TryPlanCompatibleStragglers(player, cluster.Key, groups, anchor, out List<PlannedStraggler> plannedStragglers, out string planReason))
        {
            if (IsCapSkip(planReason))
            {
                stats.CapSkips++;
                PendingSummary.CapSkips++;
            }
            else
            {
                stats.UnsafeSkips++;
                PendingSummary.UnsafeSkips++;
            }

            AddDetail($"{PlayerLabel(player)}:{cluster.Key.TypeLabel}:{cluster.Key.Theater}:skip={planReason}");
            LogCluster(player, cluster.Key, groups, planReason, anchor);
            return;
        }

        if (!TryStopAnchor(anchor.Group, out string stopMode))
        {
            stats.AnchorStopFailed++;
            PendingSummary.UnsafeSkips++;
            AddDetail($"{PlayerLabel(player)}:{cluster.Key.TypeLabel}:{cluster.Key.Theater}:skip=anchor_stop_failed");
            LogCluster(player, cluster.Key, groups, "anchor_stop_failed", anchor);
            return;
        }

        stats.AnchorsStopped++;
        PendingSummary.AnchorsStopped++;
        if (string.Equals(stopMode, "fallback", StringComparison.Ordinal))
        {
            stats.StopFallbacks++;
            PendingSummary.StopFallbacks++;
        }

        MarkTracked(anchor.Group, "anchor");
        Vector3 anchorPosition = Safe(() => anchor.Group.CurrentPosition(), Vector3.zero);
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: anchor-stop result player={LogToken(PlayerLabel(player))} id={ShortId(anchor.Id)} mode={LogToken(stopMode)} " +
            $"pos={FormatVector(anchorPosition)} destAfter={FormatVector(Safe(() => anchor.Group.DestinationPosition(), Vector3.zero))} " +
            $"pathAfter={PathSummary(anchor.Group)}.");

        int plannedAmount = cluster.Key.TypeLabel == "Submarine"
            ? anchor.VesselCount
            : Mathf.CeilToInt(anchor.Crew);
        foreach (PlannedStraggler plannedStraggler in plannedStragglers)
        {
            GroupCandidate straggler = plannedStraggler.Candidate;

            if (!CompatibleWithAnchor(player, cluster.Key, plannedAmount, straggler, out string reason, out int combinedAmount, out int cap))
            {
                if (IsCapSkip(reason))
                {
                    stats.CapSkips++;
                    PendingSummary.CapSkips++;
                }
                else
                {
                    stats.UnsafeSkips++;
                    PendingSummary.UnsafeSkips++;
                }

                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{LogPrefix}: cap/compat skip player={LogToken(PlayerLabel(player))} anchor={ShortId(anchor.Id)} " +
                    $"group={ShortId(straggler.Id)} type={cluster.Key.TypeLabel} reason={LogToken(reason)} amount={combinedAmount} cap={cap}.");
                continue;
            }

            if (TryRedirectStraggler(player, anchor, straggler, anchorPosition, stats))
                plannedAmount = combinedAmount;
        }
    }

    private static GroupCandidate PickAnchor(List<GroupCandidate> groups)
        => groups
            .OrderByDescending(static candidate => candidate.Tonnage)
            .ThenBy(static candidate => candidate.DistancePerTurn <= 0f ? float.MaxValue : candidate.DistancePerTurn)
            .First();

    private static bool MeetsMinimumClusterStrength(ClusterKey key, List<GroupCandidate> groups, out string reason)
    {
        int combinedVessels = groups.Sum(static group => group.VesselCount);
        if (key.TypeLabel == "Submarine")
        {
            if (combinedVessels < MinSubmarineClusterVessels)
            {
                reason = "below_min_strength";
                return false;
            }

            reason = "ok";
            return true;
        }

        float combinedTonnage = groups.Sum(static group => group.Tonnage);
        if (combinedVessels < MinSurfaceClusterVessels && combinedTonnage < MinSurfaceClusterTonnage)
        {
            reason = "below_min_strength";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool TryPlanCompatibleStragglers(
        Player player,
        ClusterKey key,
        List<GroupCandidate> groups,
        GroupCandidate anchor,
        out List<PlannedStraggler> plannedStragglers,
        out string reason)
    {
        plannedStragglers = new List<PlannedStraggler>();
        int plannedAmount = key.TypeLabel == "Submarine"
            ? anchor.VesselCount
            : Mathf.CeilToInt(anchor.Crew);
        if (plannedAmount <= 0)
        {
            reason = "anchor_amount_unknown";
            return false;
        }

        bool sawCapExceeded = false;
        bool sawCapUnknown = false;
        bool sawUnsafe = false;
        foreach (GroupCandidate straggler in groups)
        {
            if (straggler.Group == anchor.Group)
                continue;

            if (!CompatibleWithAnchor(player, key, plannedAmount, straggler, out string stragglerReason, out int combinedAmount, out int cap))
            {
                if (string.Equals(stragglerReason, "cap_exceeded", StringComparison.Ordinal))
                    sawCapExceeded = true;
                else if (string.Equals(stragglerReason, "cap_unknown", StringComparison.Ordinal))
                    sawCapUnknown = true;
                else
                    sawUnsafe = true;

                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{LogPrefix}: pre-stop compat skip player={LogToken(PlayerLabel(player))} anchor={ShortId(anchor.Id)} " +
                    $"group={ShortId(straggler.Id)} type={key.TypeLabel} reason={LogToken(stragglerReason)} amount={combinedAmount} cap={cap}.");
                continue;
            }

            plannedStragglers.Add(new PlannedStraggler(straggler, combinedAmount));
            plannedAmount = combinedAmount;
        }

        if (plannedStragglers.Count > 0)
        {
            reason = "ok";
            return true;
        }

        reason = sawCapUnknown ? "cap_unknown" : sawCapExceeded ? "cap_exceeded" : sawUnsafe ? "no_safe_straggler" : "no_straggler";
        return false;
    }

    private static bool IsCapSkip(string reason)
        => string.Equals(reason, "cap_exceeded", StringComparison.Ordinal) ||
           string.Equals(reason, "cap_unknown", StringComparison.Ordinal);

    private static bool TryStopAnchor(CampaignController.TaskForce anchor, out string mode)
    {
        Vector3 position = Safe(() => anchor.CurrentPosition(), Vector3.zero);
        string before = PathSummary(anchor);
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: anchor-stop attempt id={ShortId(GroupId(anchor))} pos={FormatVector(position)} " +
            $"destBefore={FormatVector(Safe(() => anchor.DestinationPosition(), Vector3.zero))} pathBefore={before}.");

        if (Safe(() => CampaignShipsMovementManager.MoveGroup(anchor, Move.Position(position), MoveSettings.Empty, true), false))
        {
            mode = "ok";
            return true;
        }

        foreach (Vector3 offset in TinyOffsets())
        {
            Vector3 fallback = position + offset;
            if (Safe(() => CampaignShipsMovementManager.MoveGroup(anchor, Move.Position(fallback), MoveSettings.Empty, true), false))
            {
                mode = "fallback";
                return true;
            }
        }

        mode = "fail";
        return false;
    }

    private static bool TryRedirectStraggler(Player player, GroupCandidate anchor, GroupCandidate straggler, Vector3 anchorPosition, ScanStats stats)
    {
        Vector3 beforeDest = Safe(() => straggler.Group.DestinationPosition(), Vector3.zero);
        float distance = Vector3.Distance(Safe(() => straggler.Group.CurrentPosition(), Vector3.zero), anchorPosition);
        bool ok = Safe(() => CampaignShipsMovementManager.MoveGroup(straggler.Group, Move.Position(anchorPosition), MoveSettings.Empty, true), false);
        Vector3 afterDest = Safe(() => straggler.Group.DestinationPosition(), Vector3.zero);

        if (ok)
        {
            stats.RedirectsOk++;
            PendingSummary.RedirectsOk++;
            MarkTracked(straggler.Group, "straggler");
        }
        else
        {
            stats.RedirectsFail++;
            PendingSummary.RedirectsFail++;
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: straggler redirect player={LogToken(PlayerLabel(player))} anchor={ShortId(anchor.Id)} id={ShortId(straggler.Id)} " +
            $"distance={Fmt(distance)} ok={ok} destBefore={FormatVector(beforeDest)} destAfter={FormatVector(afterDest)} pathAfter={PathSummary(straggler.Group)}.");

        return ok;
    }

    private static bool CompatibleWithAnchor(Player player, ClusterKey key, int plannedAmount, GroupCandidate straggler, out string reason, out int combinedAmount, out int cap)
    {
        if (key.TypeLabel == "Submarine")
        {
            combinedAmount = plannedAmount + straggler.VesselCount;
            cap = Safe(() => player.GetSubsMaxGroupSize(), 0);
            if (cap <= 0)
            {
                reason = "cap_unknown";
                return false;
            }

            if (combinedAmount > cap)
            {
                reason = "cap_exceeded";
                return false;
            }

            reason = "ok";
            return true;
        }

        int stragglerCrew = Mathf.CeilToInt(straggler.Crew);
        combinedAmount = plannedAmount + stragglerCrew;
        cap = Safe(() => player.GetShipsMaxCrewForTaskForce(), 0);
        if (cap <= 0)
        {
            reason = "cap_unknown";
            return false;
        }

        if (plannedAmount <= 0 || stragglerCrew <= 0)
        {
            reason = "crew_unknown";
            return false;
        }

        if (combinedAmount > cap)
        {
            reason = "cap_exceeded";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool HasActiveMovementDestination(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        int pathLength = Safe(() => group.Path?.Length ?? 0, 0);
        int index = Safe(() => group.CurrentPositionIndex, -1);
        if (moving && pathLength > 1 && index >= 0 && index < pathLength - 1)
            return true;

        bool wasMove = Safe(() => group.WasMove, false);
        if (!wasMove)
            return false;

        Vector3 current = Safe(() => group.CurrentPosition(), Vector3.zero);
        Vector3 destination = Safe(() => group.DestinationPosition(), Safe(() => group.ToPosition, Vector3.zero));
        return destination != Vector3.zero && Vector3.Distance(current, destination) >= MinMovementDistance;
    }

    private static bool TryBuildCandidate(Player player, CampaignController.TaskForce? group, out GroupCandidate candidate, out string skipReason)
    {
        candidate = default;
        if (group == null)
        {
            skipReason = "empty_group";
            return false;
        }

        if (!SamePlayer(Safe(() => group.Controller, null), player))
        {
            skipReason = "wrong_controller";
            return false;
        }

        int vesselCount = VesselCount(group);
        if (vesselCount <= 0)
        {
            skipReason = "empty_group";
            return false;
        }

        VesselEntity.VesselType type = Safe(() => group.GetVesselType(), (VesselEntity.VesselType)(-1));
        if (type != VesselEntity.VesselType.Ship && type != VesselEntity.VesselType.Submarine)
        {
            skipReason = "wrong_vessel_type";
            return false;
        }

        if (Safe(() => group.GroupMovingToBattle, false))
        {
            skipReason = "moving_to_battle";
            return false;
        }

        if (Safe(() => group.GroupMovingFromBattle, false))
        {
            skipReason = "moving_from_battle";
            return false;
        }

        if (Safe(() => group.PendingBattle, 0) > 0)
        {
            skipReason = "pending_battle";
            return false;
        }

        if (HasBattleId(group))
        {
            skipReason = "pending_battle";
            return false;
        }

        if (!Safe(() => group.HaveSupply, true))
        {
            skipReason = "no_supply";
            return false;
        }

        if (Safe(() => group.PathInvalid, false))
        {
            skipReason = "path_invalid";
            return false;
        }

        if (!HasActiveMovementDestination(group))
        {
            skipReason = "not_moving";
            return false;
        }

        string theater = TheaterKey(group);
        if (string.IsNullOrWhiteSpace(theater))
        {
            skipReason = "no_theater_key";
            return false;
        }

        float crew = SurfaceCrew(group);
        float tonnage = Safe(() => group.BattleTonnage(), 0f);
        float distancePerTurn = Safe(() => CampaignShipsMovementManager.DistancePerTurn(group.Vessels), 0f);
        ClusterKey key = new(PlayerIdentity(player), type == VesselEntity.VesselType.Ship ? "Ship" : "Submarine", theater);
        candidate = new GroupCandidate(group, GroupId(group), key, type, vesselCount, crew, tonnage, distancePerTurn);
        skipReason = "eligible";
        return true;
    }

    private static bool TryBuildSafeAnchorCandidate(Player player, CampaignController.TaskForce? group, out GroupCandidate candidate)
    {
        candidate = default;
        if (group == null)
            return false;

        if (!SamePlayer(Safe(() => group.Controller, null), player))
            return false;

        int vesselCount = VesselCount(group);
        if (vesselCount <= 0)
            return false;

        VesselEntity.VesselType type = Safe(() => group.GetVesselType(), (VesselEntity.VesselType)(-1));
        if (type != VesselEntity.VesselType.Ship && type != VesselEntity.VesselType.Submarine)
            return false;

        if (Safe(() => group.GroupMovingToBattle, false) ||
            Safe(() => group.GroupMovingFromBattle, false) ||
            Safe(() => group.PendingBattle, 0) > 0 ||
            HasBattleId(group) ||
            !Safe(() => group.HaveSupply, true) ||
            Safe(() => group.PathInvalid, false))
        {
            return false;
        }

        float crew = SurfaceCrew(group);
        float tonnage = Safe(() => group.BattleTonnage(), 0f);
        float distancePerTurn = Safe(() => CampaignShipsMovementManager.DistancePerTurn(group.Vessels), 0f);
        ClusterKey key = new(PlayerIdentity(player), type == VesselEntity.VesselType.Ship ? "Ship" : "Submarine", "rendezvous:" + ShortId(GroupId(group)));
        candidate = new GroupCandidate(group, GroupId(group), key, type, vesselCount, crew, tonnage, distancePerTurn);
        return candidate.Id != Guid.Empty;
    }

    private static bool IsEligiblePlayer(Player? player, out string reason)
    {
        if (player == null)
        {
            reason = "no_player";
            return false;
        }

        if (!Safe(() => player.isAi, false))
        {
            reason = "not_ai";
            return false;
        }

        if (Safe(() => player.isMain, false))
        {
            reason = "main_player";
            return false;
        }

        if (Safe(() => player.IsDisabled(), false))
        {
            reason = "disabled_player";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static string TheaterKey(CampaignController.TaskForce group)
    {
        Area? area = Safe(() => group.To?.CurrentProvince?.CurrentArea, null) ??
                     Safe(() => group.DestinationNearestPort?.CurrentProvince?.CurrentArea, null);
        if (area != null)
            return "area:" + AreaLabel(area);

        Vector3 destination = Safe(() => group.DestinationPosition(), Safe(() => group.ToPosition, Vector3.zero));
        if (destination != Vector3.zero)
            return $"pos:{Mathf.RoundToInt(destination.x / 10f)}:{Mathf.RoundToInt(destination.z / 10f)}";

        return string.Empty;
    }

    private static void LogCluster(Player player, ClusterKey key, List<GroupCandidate> groups, string status, GroupCandidate? anchor)
    {
        int combinedVessels = groups.Sum(static group => group.VesselCount);
        float combinedCrew = groups.Sum(static group => group.Crew);
        float combinedTonnage = groups.Sum(static group => group.Tonnage);
        int cap = key.TypeLabel == "Submarine"
            ? Safe(() => player.GetSubsMaxGroupSize(), 0)
            : Safe(() => player.GetShipsMaxCrewForTaskForce(), 0);

        string ids = string.Join(",", groups.Take(MaxClusterGroupIds).Select(static group => ShortId(group.Id)));
        if (groups.Count > MaxClusterGroupIds)
            ids += $"+{groups.Count - MaxClusterGroupIds}";
        string positions = string.Join("|", groups.Take(4).Select(static group =>
            $"{ShortId(group.Id)}:{FormatVector(Safe(() => group.Group.CurrentPosition(), Vector3.zero))}->{FormatVector(Safe(() => group.Group.DestinationPosition(), Vector3.zero))}:{Fmt(group.Tonnage)}t:{group.VesselCount}v"));

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: cluster player={LogToken(PlayerLabel(player))} type={key.TypeLabel} theater={LogToken(key.Theater)} " +
            $"status={LogToken(status)} groups={groups.Count} ids={ids} combinedVessels={combinedVessels} combinedCrew={Fmt(combinedCrew)} " +
            $"combinedTonnage={Fmt(combinedTonnage)} cap={cap} anchor={ShortId(anchor?.Id ?? Guid.Empty)} positions={LogToken(positions)}.");
    }

    private static void LogScan(Player? player, ScanStats stats, string status, long elapsedMs)
    {
        if (player == null && stats.GroupsTotal == 0 && status is "not_ai" or "main_player")
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: scan turn={LogToken(AiDesignCompetitiveness.CurrentTurnLabel())} player={LogToken(PlayerLabel(player))} " +
            $"status={LogToken(status)} groups={stats.GroupsTotal} eligible={stats.Eligible} clusters={stats.Clusters} " +
            $"rendezvousAnchors={stats.RendezvousAnchors} rendezvousRestops={stats.RendezvousRestops} rendezvousStragglers={stats.RendezvousStragglers} " +
            $"anchors={stats.AnchorsStopped} stopFallbacks={stats.StopFallbacks} redirectsOk={stats.RedirectsOk} redirectsFail={stats.RedirectsFail} " +
            $"capSkips={stats.CapSkips} rendezvousSkippedCap={stats.RendezvousSkippedCap} rendezvousSkippedUnsafe={stats.RendezvousSkippedUnsafe} " +
            $"minStrengthSkips={stats.MinStrengthSkips} unsafeSkips={stats.UnsafeSkips} " +
            $"skips={LogToken(SkipReasonSummary(stats.SkipReasons))} samples={LogToken(SkipSampleSummary(stats.SkipSamples, MaxSkipSamplesPerScan))} elapsedMs={elapsedMs}.");
    }

    private static void RecordSkip(string reason, ScanStats stats, Player player, CampaignController.TaskForce? group)
    {
        IncrementReason(stats.SkipReasons, reason);
        IncrementReason(PendingSummary.SkipReasons, reason);
        AddSkipSample(stats, player, group, reason);

        switch (reason)
        {
            case "pending_battle":
            case "moving_to_battle":
            case "moving_from_battle":
            case "no_supply":
            case "path_invalid":
            case "no_theater_key":
            case "wrong_vessel_type":
                stats.UnsafeSkips++;
                PendingSummary.UnsafeSkips++;
                break;
        }
    }

    private static void IncrementReason(Dictionary<string, int> reasons, string reason)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        reasons[key] = reasons.TryGetValue(key, out int current) ? current + 1 : 1;
    }

    private static void AddSkipSample(ScanStats stats, Player player, CampaignController.TaskForce? group, string reason)
    {
        if (!ShouldSampleSkipReason(reason))
            return;

        string sample = FormatSkipSample(player, group, reason);
        if (stats.SkipSamples.Count < MaxSkipSamplesPerScan)
            stats.SkipSamples.Add(sample);
        if (PendingSummary.SkipSamples.Count < MaxTurnSkipSamples)
            PendingSummary.SkipSamples.Add(sample);
    }

    private static bool ShouldSampleSkipReason(string reason)
        => reason is
            "not_moving" or
            "no_theater_key" or
            "pending_battle" or
            "no_supply" or
            "path_invalid" or
            "wrong_controller";

    private static string SkipReasonSummary(Dictionary<string, int> reasons)
        => reasons.Count == 0
            ? "none"
            : string.Join(
                ",",
                reasons
                    .OrderByDescending(static pair => pair.Value)
                    .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Take(MaxSkipReasons)
                    .Select(static pair => $"{pair.Key}:{pair.Value}"));

    private static string SkipSampleSummary(List<string> samples, int maxSamples)
        => samples.Count == 0
            ? "none"
            : string.Join(" | ", samples.Take(maxSamples));

    private static void AddDetail(string detail)
    {
        if (PendingSummary.Details.Count < MaxDetailEntries + 8)
            PendingSummary.Details.Add(LogToken(detail));
    }

    private static void MarkTracked(CampaignController.TaskForce group, string role)
    {
        Guid id = GroupId(group);
        if (id == Guid.Empty)
            return;

        StagedGroupRoles[id] = role;
        StagedGroupLabels[id] = $"{role}:{ShortId(id)}:{VesselCount(group)}v:{Fmt(Safe(() => group.BattleTonnage(), 0f))}t";
    }

    private static bool IsTracked(CampaignController.TaskForce? group)
        => group != null && StagedGroupRoles.ContainsKey(GroupId(group));

    private static string RoleFor(Guid id)
        => StagedGroupRoles.TryGetValue(id, out string? role) ? role : "unstaged";

    private static void ClearTurnState()
    {
        PendingSummary.Reset();
        StagedGroupRoles.Clear();
        StagedGroupLabels.Clear();
    }

    private static IEnumerable<Vector3> TinyOffsets()
    {
        yield return new Vector3(TinyStopOffset, 0f, 0f);
        yield return new Vector3(-TinyStopOffset, 0f, 0f);
        yield return new Vector3(0f, 0f, TinyStopOffset);
        yield return new Vector3(0f, 0f, -TinyStopOffset);
    }

    private static int VesselCount(CampaignController.TaskForce? group)
        => Safe(() => group?.Vessels?.Count ?? 0, 0);

    private static float SurfaceCrew(CampaignController.TaskForce group)
    {
        float crew = 0f;
        var vessels = Safe(() => group.Vessels, null);
        if (vessels == null)
            return crew;

        foreach (VesselEntity vessel in vessels)
        {
            Ship? ship = Safe(() => vessel?.TryCast<Ship>(), null);
            if (ship != null)
                crew += Safe(() => ship.GetTotalCrew(), 0f);
        }

        return crew;
    }

    private static bool HasBattleId(CampaignController.TaskForce group)
    {
        try
        {
            return ToSystemGuid(group.BattleId) != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    private static Guid GroupId(CampaignController.TaskForce? group)
    {
        if (group == null)
            return Guid.Empty;

        try
        {
            return ToSystemGuid(group.Id);
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static Guid ToSystemGuid(Il2CppSystem.Guid value)
        => Guid.TryParse(value.ToString(), out Guid parsed) ? parsed : Guid.Empty;

    private static string PathSummary(CampaignController.TaskForce? group)
    {
        if (group == null)
            return "none";

        int pathLength = Safe(() => group.Path?.Length ?? 0, 0);
        int fullPathLength = Safe(() => group.FullPath?.Length ?? 0, 0);
        int index = Safe(() => group.CurrentPositionIndex, -1);
        bool moving = Safe(() => group.IsMoving(), false);
        return $"idx={index}:path={pathLength}:full={fullPathLength}:moving={moving}";
    }

    private static string FormatSkipSample(Player player, CampaignController.TaskForce? group, string reason)
    {
        if (group == null)
            return $"{reason}:group=null";

        string type = Safe(() => group.GetVesselType().ToString(), "unknown");
        string repair = Safe(() => group.Repair.ToString(), "unknown");
        bool moving = Safe(() => group.IsMoving(), false);
        bool wasMove = Safe(() => group.WasMove, false);
        bool toBattle = Safe(() => group.GroupMovingToBattle, false);
        bool fromBattle = Safe(() => group.GroupMovingFromBattle, false);
        int pendingBattle = Safe(() => group.PendingBattle, 0);
        string controller = PlayerLabel(Safe(() => group.Controller, null));
        string current = FormatVector(Safe(() => group.CurrentPosition(), Vector3.zero));
        string destination = FormatVector(Safe(() => group.DestinationPosition(), Safe(() => group.ToPosition, Vector3.zero)));
        string to = PortLabel(Safe(() => group.To, null));
        string destinationPort = PortLabel(Safe(() => group.DestinationNearestPort, null));

        return string.Join(
            ":",
            reason,
            ShortId(GroupId(group)),
            $"player={LogToken(PlayerLabel(player))}",
            $"controller={LogToken(controller)}",
            $"type={LogToken(type)}",
            $"{VesselCount(group)}v",
            $"repair={LogToken(repair)}",
            $"moving={moving}",
            $"wasMove={wasMove}",
            $"toBattle={toBattle}",
            $"fromBattle={fromBattle}",
            $"pending={pendingBattle}",
            $"path={PathSummary(group)}",
            $"cur={current}",
            $"dest={destination}",
            $"to={LogToken(to)}",
            $"destPort={LogToken(destinationPort)}");
    }

    private static string PortLabel(PortElement? port)
    {
        if (port == null)
            return "none";

        string name = SafeString(() => port.Name);
        string id = SafeString(() => port.Id);
        string area = AreaLabel(Safe(() => port.CurrentProvince?.CurrentArea, null));
        string label = !string.IsNullOrWhiteSpace(name)
            ? name
            : !string.IsNullOrWhiteSpace(id)
                ? id
                : "unknown";
        return area == "unknown" ? label : $"{label}/{area}";
    }

    private static string AreaLabel(Area? area)
    {
        if (area == null)
            return "unknown";

        string id = SafeString(() => area.Id);
        string name = SafeString(() => area.Name);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }

    private static string PlayerIdentity(Player? player)
    {
        string dataName = SafeString(() => player?.data?.name);
        if (!string.IsNullOrWhiteSpace(dataName))
            return dataName;

        return PlayerLabel(player);
    }

    private static string PlayerLabel(Player? player)
    {
        string label = AiDesignCompetitiveness.PlayerLabel(player);
        return string.IsNullOrWhiteSpace(label) ? "unknown" : label;
    }

    private static bool SamePlayer(Player? a, Player? b)
    {
        if (a == null || b == null)
            return false;

        return a == b || (a.data != null && b.data != null && a.data == b.data);
    }

    private static string ShortId(Guid id)
        => id == Guid.Empty ? "none" : id.ToString("N")[..8];

    private static string FormatVector(Vector3 value)
        => $"{Fmt(value.x)},{Fmt(value.y)},{Fmt(value.z)}";

    private static string Fmt(float value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim()
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace(" ", "_");

    private static string SafeString(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
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

    private static void LogFailureOnce(string action, Exception ex)
    {
        string key = $"{action}:{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, key, StringComparison.Ordinal))
            return;

        lastFailure = key;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: {action} failed; leaving vanilla task-force behavior for this callback. {ex.GetType().Name}: {ex.Message}");
    }

    internal readonly record struct MergeSnapshot(
        bool Staged,
        Guid MergeToId,
        Guid GroupId,
        int MergeToCount,
        int GroupCount,
        float MergeToTonnage,
        float GroupTonnage);

    private readonly record struct ClusterKey(string PlayerKey, string TypeLabel, string Theater);

    private readonly record struct GroupCandidate(
        CampaignController.TaskForce Group,
        Guid Id,
        ClusterKey Key,
        VesselEntity.VesselType Type,
        int VesselCount,
        float Crew,
        float Tonnage,
        float DistancePerTurn);

    private readonly record struct PlannedStraggler(GroupCandidate Candidate, int CombinedAmount);

    private readonly record struct RendezvousMatch(GroupCandidate Straggler, float Distance);

    private sealed class ScanStats
    {
        internal readonly Dictionary<string, int> SkipReasons = new(StringComparer.Ordinal);
        internal readonly List<string> SkipSamples = new();
        internal int Scans;
        internal int GroupsTotal;
        internal int Eligible;
        internal int Clusters;
        internal int SingleGroupClusters;
        internal int RendezvousAnchors;
        internal int RendezvousRestops;
        internal int RendezvousStragglers;
        internal int RendezvousSkippedCap;
        internal int RendezvousSkippedUnsafe;
        internal int AnchorsStopped;
        internal int AnchorStopFailed;
        internal int StopFallbacks;
        internal int RedirectsOk;
        internal int RedirectsFail;
        internal int CapSkips;
        internal int MinStrengthSkips;
        internal int UnsafeSkips;
    }

    private sealed class TurnSummary
    {
        internal readonly HashSet<string> PlayersTouched = new(StringComparer.Ordinal);
        internal readonly List<string> Details = new();
        internal readonly Dictionary<string, int> SkipReasons = new(StringComparer.Ordinal);
        internal readonly List<string> SkipSamples = new();
        internal int Scans;
        internal int EligibleGroups;
        internal int Clusters;
        internal int RendezvousAnchors;
        internal int RendezvousRestops;
        internal int RendezvousStragglers;
        internal int RendezvousSkippedCap;
        internal int RendezvousSkippedUnsafe;
        internal int AnchorsStopped;
        internal int StopFallbacks;
        internal int RedirectsOk;
        internal int RedirectsFail;
        internal int CapSkips;
        internal int MinStrengthSkips;
        internal int UnsafeSkips;
        internal int MergesObserved;
        internal int RemovalsObserved;
        internal long ElapsedMs;
        internal long MaxMs;

        internal void Reset()
        {
            PlayersTouched.Clear();
            Details.Clear();
            SkipReasons.Clear();
            SkipSamples.Clear();
            Scans = 0;
            EligibleGroups = 0;
            Clusters = 0;
            RendezvousAnchors = 0;
            RendezvousRestops = 0;
            RendezvousStragglers = 0;
            RendezvousSkippedCap = 0;
            RendezvousSkippedUnsafe = 0;
            AnchorsStopped = 0;
            StopFallbacks = 0;
            RedirectsOk = 0;
            RedirectsFail = 0;
            CapSkips = 0;
            MinStrengthSkips = 0;
            UnsafeSkips = 0;
            MergesObserved = 0;
            RemovalsObserved = 0;
            ElapsedMs = 0;
            MaxMs = 0;
        }
    }
}
