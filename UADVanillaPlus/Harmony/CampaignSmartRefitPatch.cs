using System.Reflection;
using System.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.Services;

namespace UADVanillaPlus.Harmony;

// Smart Refits replaces vanilla's random AI refit generator with a bounded
// conservative pass that clones an existing live class, refreshes it through
// SmartRefitService, and then starts a normal campaign refit only if accepted.
internal static class CampaignSmartRefitPatch
{
    private const string LogPrefix = "UADVP smart refits";
    private const string AiLogPrefix = "UADVP smart refits ai";
    private const string AiDebugLogPrefix = "UADVP smart refits ai-debug";
    private static bool VerboseAiRefitDiagnostics => false;
    private const int MinAiRefitAgeYears = 4;
    private const int MaxAiRefitAttemptsPerNation = 2;
    private const int MaxShipsPerAiRefitStart = 8;
    private const int MaxAiRefitSummaryDetails = 8;
    private static readonly bool AllowAiSmartRefitStarts = true;
    private static readonly bool RunPlacementProbeBeforeRealAiRefit = false;
    private static readonly AiPlacementProbeMode[] AiPlacementProbeModes =
    {
        new("constructor-and-model-load", true, true),
    };

    private static readonly HashSet<string> LoggedBlocks = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedCleanup = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedAiAttempts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AiSmartRefitTurnSummary> PendingAiRefitTurnSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedEmptyAiRefitSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<long> InProgressSmartAiRefitDesigns = new();
    private static readonly HashSet<long> AcceptedSmartAiRefitDesigns = new();
    private static readonly Dictionary<Type, AiRefitStateMachineFields> AiRefitStateMachineFieldsByType = new();

    internal static bool ShouldBlockAiRefits(Player? player)
        => ModSettings.SmartRefitsEnabled &&
           player != null &&
           Safe(() => player.isAi && !player.isMain, false);

    internal static bool IsBlockedAiRefitDesign(Ship? design)
    {
        if (!ModSettings.SmartRefitsEnabled || design == null || Safe(() => design.isErased, true))
            return false;

        Player? player = Safe(() => design.player, null);
        if (!ShouldBlockAiRefits(player))
            return false;

        if (IsLiveFleetShip(player, design, out _))
            return false;

        if (IsApprovedSmartAiRefitDesign(design))
            return false;

        if (IsReferencedByLiveRefitShip(player, design, out _, out _))
            return false;

        return IsAiRefitDesignLike(design);
    }

    internal static bool IsApprovedSmartAiRefitDesign(Ship? design)
    {
        long pointer = ShipPointer(design);
        if (pointer != 0L &&
            (InProgressSmartAiRefitDesigns.Contains(pointer) || AcceptedSmartAiRefitDesigns.Contains(pointer)))
            return true;

        Player? player = Safe(() => design?.player, null);
        return ShouldBlockAiRefits(player) &&
               design != null &&
               IsReferencedByLiveRefitShip(player, design, out _, out _);
    }

    internal static bool ShouldProtectAiRefitDesignFromDeletion(
        Player? player,
        Ship? design,
        out string reason,
        out int liveRefs,
        out string liveRefExamples)
    {
        reason = "none";
        liveRefs = 0;
        liveRefExamples = "none";

        if (!ModSettings.SmartRefitsEnabled || design == null || Safe(() => design.isErased, true))
            return false;

        Player? owner = player ?? Safe(() => design.player, null);
        if (!ShouldBlockAiRefits(owner))
            return false;

        if (IsLiveFleetShip(owner, design, out string liveShipExamples))
        {
            reason = "live-fleet-ship";
            liveRefs = 1;
            liveRefExamples = liveShipExamples;
            return true;
        }

        if (IsReferencedByLiveRefitShip(owner, design, out liveRefs, out liveRefExamples))
        {
            reason = "referenced-live-refit";
            return true;
        }

        long pointer = ShipPointer(design);
        if (pointer != 0L && InProgressSmartAiRefitDesigns.Contains(pointer))
        {
            reason = "in-progress-smart-refit";
            return true;
        }

        if (pointer != 0L && AcceptedSmartAiRefitDesigns.Contains(pointer))
        {
            reason = "accepted-smart-refit";
            return true;
        }

        return false;
    }

    internal static bool DeleteDesignGuardPrefix(Ship? design, string source)
    {
        if (!ModSettings.SmartRefitsEnabled || design == null)
            return true;

        Player? owner = Safe(() => design.player, null);
        if (!ShouldProtectAiRefitDesignFromDeletion(owner, design, out string reason, out int liveRefs, out string liveRefExamples))
            return true;

        string nation = AiDesignCompetitiveness.PlayerLabel(owner);
        RecordAiRefitSummary(
            AiSmartRefitSummaryKind.DeleteGuard,
            nation,
            $"{nation} {ReadableDesignLabel(design)}: delete guard {reason} liveRefs={liveRefs}");
        if (VerboseAiRefitDiagnostics)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{AiDebugLogPrefix}: delete-guard blocked source={LogToken(source)} nation={LogToken(nation)} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason={LogToken(reason)} liveRefs={liveRefs} liveRefExamples={LogToken(liveRefExamples)}.");
        }
        return false;
    }

    internal static void LogBlockedAiRefit(Player? player, bool force, bool forceSimple, string source)
    {
        string turn = AiDesignCompetitiveness.CurrentTurnLabel();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        string key = $"{turn}|{nation}|{force}|{forceSimple}|{source}";
        if (!LoggedBlocks.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: blocked AI campaign refit nation={LogToken(nation)} force={force} forceSimple={forceSimple} source={LogToken(source)}.");
    }

    internal static Il2CppSystem.Collections.IEnumerator EmptyIl2CppCoroutine()
        => new Il2CppSystem.Collections.ArrayList().GetEnumerator();

    internal static Player? FirstShipPlayer(Il2CppSystem.Collections.Generic.List<Ship>? ships)
        => Safe(() => ships != null && ships.Count > 0 ? ships[0]?.player : null, null);

    internal static void RunAiSmartRefits(Player player, bool force, bool forceSimple)
    {
        string turn = AiDesignCompetitiveness.CurrentTurnLabel();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        CleanupAiRefitDesigns(CampaignController.Instance, "before smart AI refit");

        try
        {
            LogAi(
                $"attempt turn={LogToken(turn)} nation={LogToken(nation)} force={force} forceSimple={forceSimple} cash={Fmt(Safe(() => player.cash, 0f))} revenue={Fmt(Safe(() => player.Revenue(), 0f))}.");

            AiRefitContinuationResult continuation = ContinueExistingAiSmartRefits(player, force);
            AddExistingAiRefitCoverageKeys(player, continuation);
            int continued = continuation.Started;

            if (!force && !PassAiRefitChanceGate(player, out float chance, out float roll))
            {
                RecordAiRefitSummary(AiSmartRefitSummaryKind.Skipped, nation, $"{nation}: skipped chance");
                LogAi(
                    $"skip turn={LogToken(turn)} nation={LogToken(nation)} reason=chance chance={Fmt(chance)} roll={Fmt(roll)}.");
                LogAi(
                    $"done turn={LogToken(turn)} nation={LogToken(nation)} groups=0 attempts=0 started={continued} continued={continued} newStarted=0.");
                return;
            }

            if (!force && !PassAiRefitBudgetGate(player, out string budgetReason))
            {
                RecordAiRefitSummary(AiSmartRefitSummaryKind.Skipped, nation, $"{nation}: skipped {ReadableLogText(budgetReason)}");
                LogAi(
                    $"skip turn={LogToken(turn)} nation={LogToken(nation)} reason={LogToken(budgetReason)} cash={Fmt(Safe(() => player.cash, 0f))} revenue={Fmt(Safe(() => player.Revenue(), 0f))}.");
                LogAi(
                    $"done turn={LogToken(turn)} nation={LogToken(nation)} groups=0 attempts=0 started={continued} continued={continued} newStarted=0.");
                return;
            }

            List<AiRefitGroup> groups = BuildAiRefitGroups(player, force);
            if (continuation.CoveredGroupKeys.Count > 0)
                groups = FilterContinuationCoveredGroups(player, groups, continuation.CoveredGroupKeys);

            if (groups.Count == 0)
            {
                RecordAiRefitSummary(AiSmartRefitSummaryKind.Skipped, nation, $"{nation}: skipped no-candidates");
                LogAi($"skip turn={LogToken(turn)} nation={LogToken(nation)} reason=no-candidates.");
                LogAi(
                    $"done turn={LogToken(turn)} nation={LogToken(nation)} groups=0 attempts=0 started={continued} continued={continued} newStarted=0.");
                return;
            }

            int attempts = 0;
            int newStarted = 0;
            foreach (AiRefitGroup group in groups)
            {
                if (attempts >= MaxAiRefitAttemptsPerNation)
                    break;

                attempts++;
                if (TryRunAiSmartRefitGroup(player, group, force, forceSimple))
                    newStarted++;
            }

            LogAi(
                $"done turn={LogToken(turn)} nation={LogToken(nation)} groups={groups.Count} attempts={attempts} started={continued + newStarted} continued={continued} newStarted={newStarted}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{AiLogPrefix}: failed turn={LogToken(turn)} nation={LogToken(nation)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static MethodBase? AiRefitShipsMoveNextTarget()
        => typeof(CampaignController)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => new
            {
                Type = type,
                MoveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            })
            .Where(candidate =>
                candidate.MoveNext != null &&
                candidate.Type.Name.Contains("AiRefitShips", StringComparison.Ordinal) &&
                candidate.MoveNext.ReturnType == typeof(bool) &&
                candidate.MoveNext.GetParameters().Length == 0)
            .Select(candidate => candidate.MoveNext)
            .FirstOrDefault();

    private static bool TryRunAiSmartRefitGroup(Player player, AiRefitGroup group, bool force, bool forceSimple)
    {
        Stopwatch candidateTimer = Stopwatch.StartNew();
        Ship? source = group.Ships
            .OrderByDescending(AgeYears)
            .ThenByDescending(ship => Safe(() => ship.Tonnage(), 0f))
            .FirstOrDefault();
        if (source == null)
            return false;

        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        LogAi(
            $"candidate nation={LogToken(nation)} type={group.Type} class={LogToken(group.ClassName)} ships={group.Ships.Count} oldestAge={Fmt(group.OldestAge)} forceSimple={forceSimple}.");

        if (RunPlacementProbeBeforeRealAiRefit)
            RunAiPlacementProbeIfNeeded(player, source, group);

        Ship baseline = Safe(() => source.design, null) ?? source;
        Ship? refitDesign = CreateAiSmartRefitDesign(player, source, out bool enteredConstructor, forceConstructor: true);
        if (refitDesign == null)
        {
            RecordAiRefitSummary(
                AiSmartRefitSummaryKind.Rejected,
                nation,
                $"{nation} {group.Type} {ReadableLogText(group.ClassName)}: rejected create-design-failed",
                candidateTimer.ElapsedMilliseconds);
            LogAi(
                $"cleanup nation={LogToken(nation)} type={group.Type} class={LogToken(group.ClassName)} reason=create-design-failed.");
            return false;
        }

        RegisterInProgressSmartAiRefit(refitDesign);
        bool constructorVisualsCleaned = false;
        try
        {
            LogAi(
                $"created-design nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} source={LogLabel(AiDesignCompetitiveness.ShipLabel(source))} refitYear={CurrentCampaignYear()}.");
            LogAiDebugPreService(player, refitDesign, source, enteredConstructor, "ai-real", true, true);

            SmartRefitResult result = SmartRefitService.ApplyForAi(refitDesign, player, baseline);
            LogAi(
                $"service-result nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} result={(result.Success ? "accepted" : "rejected")} message={LogToken(result.Message)}.");

            if (!result.Success)
            {
                CleanupAiSmartRefitDesign(player, refitDesign, "service-rejected");
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Rejected,
                    nation,
                    $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: rejected {ReadableLogText(result.Message)}",
                    candidateTimer.ElapsedMilliseconds);
                LogAi(
                    $"candidate-finished nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} result=rejected elapsedMs={candidateTimer.ElapsedMilliseconds}.");
                return false;
            }

            if (!AllowAiSmartRefitStarts)
            {
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Skipped,
                    nation,
                    $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: skipped start-deferred",
                    candidateTimer.ElapsedMilliseconds);
                LogAi(
                    $"start-deferred nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} reason=save-reference-guard elapsedMs={candidateTimer.ElapsedMilliseconds}.");
                CleanupAiSmartRefitDesign(player, refitDesign, "start-deferred-save-reference-guard");
                return false;
            }

            SmartRefitService.CleanupConstructorVisualsBeforeLeave(
                refitDesign,
                "ai-success-before-buildability",
                logDetails: VerboseAiRefitDiagnostics);
            constructorVisualsCleaned = true;
            if (!CanBuildSmartRefitDesign(refitDesign, out string buildReason))
            {
                CleanupAiSmartRefitDesign(player, refitDesign, "buildability-" + buildReason, constructorVisualsCleaned);
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Rejected,
                    nation,
                    $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: rejected buildability-{ReadableLogText(buildReason)}",
                    candidateTimer.ElapsedMilliseconds);
                LogAi(
                    $"candidate-finished nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} result=buildability-rejected reason={LogToken(buildReason)} elapsedMs={candidateTimer.ElapsedMilliseconds}.");
                return false;
            }

            Il2CppSystem.Collections.Generic.List<Ship> selection = SelectShipsForAiRefit(group.Ships, refitDesign, player, force);
            if (selection.Count == 0)
            {
                CleanupAiSmartRefitDesign(player, refitDesign, "no-affordable-ships", constructorVisualsCleaned);
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Skipped,
                    nation,
                    $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: skipped no-affordable-ships",
                    candidateTimer.ElapsedMilliseconds);
                LogAi(
                    $"candidate-finished nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} result=no-affordable-ships elapsedMs={candidateTimer.ElapsedMilliseconds}.");
                return false;
            }

            RegisterAcceptedSmartAiRefit(refitDesign);
            float costPerMonth = Safe(() => refitDesign.RefitCostPerMonth(), 0f);
            LogAiDebugRefitStartState(player, refitDesign, selection, "before-refit-start");
            PlayerController.Instance.RefitShipsStart(selection, refitDesign, false);
            LogAiDebugRefitStartState(player, refitDesign, selection, "after-refit-start");
            if (costPerMonth > 0f)
                SafeDo(() => player.cash -= costPerMonth * selection.Count);

            LogAi(
                $"inspect=AI_SMART_REFIT nation={LogToken(nation)} type={group.Type} source={LogLabel(AiDesignCompetitiveness.ShipLabel(source))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} ships={selection.Count}.");
            LogAi(
                $"start-refit nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} ships={selection.Count} costPerMonth={Fmt(costPerMonth)} cashAfter={Fmt(Safe(() => player.cash, 0f))} elapsedMs={candidateTimer.ElapsedMilliseconds}.");
            RecordAiRefitSummary(
                AiSmartRefitSummaryKind.Started,
                nation,
                $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: started {selection.Count} ships, ${Fmt(costPerMonth)}/mo",
                candidateTimer.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            CleanupAiSmartRefitDesign(player, refitDesign, "exception-" + ex.GetType().Name, constructorVisualsCleaned);
            RecordAiRefitSummary(
                AiSmartRefitSummaryKind.Rejected,
                nation,
                $"{nation} {group.Type} {ReadableDesignLabel(refitDesign)}: rejected exception-{ex.GetType().Name}",
                candidateTimer.ElapsedMilliseconds);
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{AiLogPrefix}: failed candidate nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            UnregisterInProgressSmartAiRefit(refitDesign);
        }
    }

    private static AiRefitContinuationResult ContinueExistingAiSmartRefits(Player player, bool force)
    {
        AiRefitContinuationResult result = new();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);

        foreach (Ship refitDesign in ExistingAiSmartRefitTargets(player))
        {
            AddContinuationCoverageKeys(result, player, refitDesign);
            if (result.Started >= MaxAiRefitAttemptsPerNation)
                continue;

            List<Ship> candidates = ContinuationCandidates(player, refitDesign);
            foreach (Ship candidate in candidates)
                result.CoveredGroupKeys.Add(GroupKey(candidate));

            if (candidates.Count == 0)
                continue;

            if (!CanBuildSmartRefitDesign(refitDesign, out string buildReason))
            {
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Rejected,
                    nation,
                    $"{nation} {ReadableDesignLabel(refitDesign)}: rejected buildability-{ReadableLogText(buildReason)}");
                LogAi(
                    $"continue-skip nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} reason=buildability-{LogToken(buildReason)} candidates={candidates.Count} source=vp-smart-refit-existing.");
                continue;
            }

            Il2CppSystem.Collections.Generic.List<Ship> selection = SelectShipsForAiRefit(candidates, refitDesign, player, force);
            if (selection.Count == 0)
            {
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Skipped,
                    nation,
                    $"{nation} {ReadableDesignLabel(refitDesign)}: skipped no-affordable-ships");
                LogAi(
                    $"continue-skip nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} reason=no-affordable-ships candidates={candidates.Count} costPerMonth={Fmt(Safe(() => refitDesign.RefitCostPerMonth(), 0f))} cash={Fmt(Safe(() => player.cash, 0f))} source=vp-smart-refit-existing.");
                continue;
            }

            if (PlayerController.Instance == null)
            {
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Skipped,
                    nation,
                    $"{nation} {ReadableDesignLabel(refitDesign)}: skipped no-player-controller");
                LogAi(
                    $"continue-skip nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} reason=no-player-controller candidates={candidates.Count} source=vp-smart-refit-existing.");
                continue;
            }

            try
            {
                SafeDo(() => refitDesign.LeaveConstructor());
                RegisterAcceptedSmartAiRefit(refitDesign);
                float costPerMonth = Safe(() => refitDesign.RefitCostPerMonth(), 0f);
                LogAiDebugRefitStartState(player, refitDesign, selection, "before-continue-refit-start");
                PlayerController.Instance.RefitShipsStart(selection, refitDesign, false);
                LogAiDebugRefitStartState(player, refitDesign, selection, "after-continue-refit-start");
                if (costPerMonth > 0f)
                    SafeDo(() => player.cash -= costPerMonth * selection.Count);

                result.Started++;
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Continued,
                    nation,
                    $"{nation} {ReadableDesignLabel(refitDesign)}: continued {selection.Count} ships, ${Fmt(costPerMonth)}/mo");
                LogAi(
                    $"continue-refit nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} ships={selection.Count} candidates={candidates.Count} costPerMonth={Fmt(costPerMonth)} cashAfter={Fmt(Safe(() => player.cash, 0f))} source=vp-smart-refit-existing.");
            }
            catch (Exception ex)
            {
                RecordAiRefitSummary(
                    AiSmartRefitSummaryKind.Rejected,
                    nation,
                    $"{nation} {ReadableDesignLabel(refitDesign)}: rejected exception-{ex.GetType().Name}");
                LogAi(
                    $"continue-skip nation={LogToken(nation)} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} reason=exception-{LogToken(ex.GetType().Name)} candidates={candidates.Count} source=vp-smart-refit-existing.");
            }
        }

        return result;
    }

    private static void AddExistingAiRefitCoverageKeys(Player player, AiRefitContinuationResult result)
    {
        foreach (Ship design in PlayerDesigns(player))
        {
            if (!IsAiRefitCoverageTarget(player, design, out string reason, out int liveRefs, out string liveRefExamples))
                continue;

            int before = result.CoveredGroupKeys.Count;
            AddContinuationCoverageKeys(result, player, design);
            if (result.CoveredGroupKeys.Count <= before)
                continue;

            LogAi(
                $"coverage-existing nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(design))} reason={LogToken(reason)} liveRefs={liveRefs} liveRefExamples={LogToken(liveRefExamples)}.");
        }
    }

    private static List<AiRefitGroup> FilterContinuationCoveredGroups(
        Player player,
        List<AiRefitGroup> groups,
        HashSet<string> coveredKeys)
    {
        if (coveredKeys.Count == 0 || groups.Count == 0)
            return groups;

        List<AiRefitGroup> result = new();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        foreach (AiRefitGroup group in groups)
        {
            if (!coveredKeys.Contains(group.Key))
            {
                result.Add(group);
                continue;
            }

            RecordAiRefitSummary(
                AiSmartRefitSummaryKind.Skipped,
                nation,
                $"{nation} {group.Type} {ReadableLogText(group.ClassName)}: skipped continuation-covered");
            LogAi(
                $"candidate-skip reason=continuation-covered nation={LogToken(nation)} type={group.Type} class={LogToken(group.ClassName)} key={LogToken(group.Key)} ships={group.Ships.Count}.");
        }

        return result;
    }

    private static void AddContinuationCoverageKeys(AiRefitContinuationResult result, Player player, Ship refitDesign)
    {
        HashSet<string> added = new(StringComparer.Ordinal);
        void AddKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (result.CoveredGroupKeys.Add(key))
                added.Add(key);
        }

        AddKey(DesignGroupKey(refitDesign));
        AddKey(ClassGroupKey(refitDesign));

        Ship? baseline = Safe(() => refitDesign.designShipForRefit, null);
        AddKey(DesignGroupKey(baseline));
        AddKey(ClassGroupKey(baseline));

        List<Ship> liveRefs = LiveShipsReferencingRefitDesign(player, refitDesign);
        foreach (Ship ship in liveRefs)
        {
            AddKey(GroupKey(ship));
            AddKey(DesignGroupKey(Safe(() => ship.design, null)));
            AddKey(ClassGroupKey(ship));
        }

        if (added.Count == 0)
            return;

        string keys = string.Join(",", added.Take(8).Select(LogToken));
        int hidden = Math.Max(0, added.Count - 8);
        if (hidden > 0)
            keys += $",+{hidden}more";

        LogAi(
            $"continue-cover nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(refitDesign))} keys={keys} liveRefs={liveRefs.Count} liveRefExamples={LogToken(ReferenceExamples(liveRefs))}.");
    }

    private static List<Ship> ExistingAiSmartRefitTargets(Player player)
    {
        List<Ship> result = new();

        void AddTarget(Ship? design)
        {
            if (!IsVpSmartRefitTarget(player, design, out string skipReason, out int liveRefs, out string liveRefExamples))
            {
                if (ShouldLogContinuationTargetSkip(design, liveRefs))
                    LogContinueSkipTarget(player, design!, skipReason, liveRefs, liveRefExamples);
                return;
            }

            if (result.Any(existing => SameShipIdentity(existing, design!)))
                return;

            result.Add(design!);
        }

        foreach (Ship design in PlayerDesigns(player))
            AddTarget(design);

        foreach (Ship ship in PlayerFleetShips(player))
            AddTarget(Safe(() => ship.design, null));

        return result
            .OrderByDescending(design => Safe(() => design.dateCreatedRefit.turn, -1))
            .ThenBy(design => BaseClassName(design), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsVpSmartRefitTarget(Player player, Ship? design, out string reason, out int liveRefs, out string liveRefExamples)
    {
        reason = "unknown";
        liveRefs = 0;
        liveRefExamples = "none";

        if (design == null)
        {
            reason = "null";
            return false;
        }

        if (!ModSettings.SmartRefitsEnabled)
        {
            reason = "settings-off";
            return false;
        }

        if (!ShouldBlockAiRefits(player))
        {
            reason = "not-ai-smart-refit-player";
            return false;
        }

        if (Safe(() => design.shipType == null, true))
        {
            reason = "no-ship-type";
            return false;
        }

        bool referenced = IsReferencedByLiveRefitShip(player, design, out liveRefs, out liveRefExamples);
        if (Safe(() => design.isErased, false) && !referenced)
        {
            reason = "erased-unreferenced";
            return false;
        }

        if (IsLiveFleetShip(player, design, out string liveShipExamples))
        {
            reason = "live-fleet-ship";
            liveRefs = Math.Max(liveRefs, 1);
            liveRefExamples = liveShipExamples;
            return false;
        }

        Player? owner = Safe(() => design.player, null);
        if (owner != null && Safe(() => owner.Pointer != player.Pointer, true))
        {
            reason = "owner-mismatch";
            return false;
        }

        int refitTurn = Safe(() => design.dateCreatedRefit.turn, 0);
        int refitYear = Safe(() => design.dateCreatedRefit.AsDate().Year, 0);
        if (refitTurn <= 0)
        {
            reason = "no-refit-turn";
            return false;
        }

        if (refitYear <= 1890)
        {
            reason = "campaign-start-refit-year";
            return false;
        }

        bool isRefitDesign = Safe(() => design.isRefitDesign, false);
        bool hasBaseline = Safe(() => design.designShipForRefit != null, false);
        bool hasGeneratedYearName = HasGeneratedRefitYearName(design, refitYear);
        if (!isRefitDesign && !(hasGeneratedYearName && (hasBaseline || referenced)))
        {
            reason = "not-refit-design";
            return false;
        }

        if (!hasGeneratedYearName && !hasBaseline)
        {
            reason = "no-smart-refit-marker";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool IsAiRefitCoverageTarget(Player player, Ship? design, out string reason, out int liveRefs, out string liveRefExamples)
    {
        reason = "unknown";
        liveRefs = 0;
        liveRefExamples = "none";

        if (design == null)
        {
            reason = "null";
            return false;
        }

        if (!ModSettings.SmartRefitsEnabled)
        {
            reason = "settings-off";
            return false;
        }

        if (!ShouldBlockAiRefits(player))
        {
            reason = "not-ai-smart-refit-player";
            return false;
        }

        if (Safe(() => design.shipType == null, true))
        {
            reason = "no-ship-type";
            return false;
        }

        if (Safe(() => design.isErased, false))
        {
            reason = "erased";
            return false;
        }

        Player? owner = Safe(() => design.player, null);
        if (owner != null && Safe(() => owner.Pointer != player.Pointer, true))
        {
            reason = "owner-mismatch";
            return false;
        }

        bool referenced = IsReferencedByLiveRefitShip(player, design, out liveRefs, out liveRefExamples);
        bool isRefitDesign = Safe(() => design.isRefitDesign, false);
        int refitTurn = Safe(() => design.dateCreatedRefit.turn, 0);
        int refitYear = Safe(() => design.dateCreatedRefit.AsDate().Year, 0);
        bool hasBaseline = Safe(() => design.designShipForRefit != null, false);
        bool hasGeneratedYearName = refitYear > 0 && HasGeneratedRefitYearName(design, refitYear);

        if (!referenced && !isRefitDesign && refitTurn <= 0 && !hasBaseline && !hasGeneratedYearName)
        {
            reason = "not-refit-coverage";
            return false;
        }

        reason = refitYear <= 1890 ? "campaign-start-coverage" : "active-refit-coverage";
        return true;
    }

    private static bool ShouldLogContinuationTargetSkip(Ship? design, int liveRefs)
        => design != null &&
           (liveRefs > 0 ||
            Safe(() => design.isErased, false) ||
            Safe(() => design.isRefitDesign, false) ||
            Safe(() => design.dateCreatedRefit.turn != 0, false) ||
            Safe(() => design.designShipForRefit != null, false));

    private static bool IsPotentialSmartRefitTarget(Ship? design)
        => design != null &&
           (Safe(() => design.isRefitDesign, false) ||
            Safe(() => design.dateCreatedRefit.turn > 0, false) ||
            Safe(() => design.designShipForRefit != null, false));

    private static void LogContinueSkipTarget(Player player, Ship design, string reason, int liveRefs, string liveRefExamples)
    {
        LogAi(
            $"continue-skip-target reason=not-smart-refit detail={LogToken(reason)} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} isDesign={Safe(() => design.isDesign, false)} isRefitDesign={Safe(() => design.isRefitDesign, false)} dateCreatedRefit={Safe(() => design.dateCreatedRefit.turn, -1)}/{Safe(() => design.dateCreatedRefit.AsDate().Year, -1)} status={LogToken(Safe(() => design.status.ToString(), string.Empty))} inDesigns={ContainsShipByIdentity(PlayerDesigns(player), design)} liveRefs={liveRefs} liveRefExamples={LogToken(liveRefExamples)}.");
    }

    private static bool HasGeneratedRefitYearName(Ship design, int expectedYear)
    {
        foreach (string value in new[]
                 {
                     Safe(() => design.name, string.Empty),
                     Safe(() => design.vesselName, string.Empty),
                     Safe(() => design.refitDesignName, string.Empty),
                     AiDesignCompetitiveness.ShipLabel(design)
                 })
        {
            if (!TryReadGeneratedRefitYear(value, out int suffixYear))
                continue;

            if (suffixYear > 1890 && (expectedYear <= 1890 || suffixYear == expectedYear))
                return true;
        }

        return false;
    }

    private static bool TryReadGeneratedRefitYear(string? value, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int open = value.LastIndexOf('(');
        if (open < 0 || open + 5 >= value.Length)
            return false;

        int close = value.IndexOf(')', open + 1);
        if (close < 0)
            return false;

        string token = value.Substring(open + 1, close - open - 1).Trim();
        if (token.Length < 4)
            return false;

        for (int i = 0; i < 4; i++)
        {
            if (!char.IsDigit(token[i]))
                return false;
        }

        return int.TryParse(token.Substring(0, 4), out year);
    }

    private static List<Ship> ContinuationCandidates(Player player, Ship refitDesign)
        => PlayerFleetShips(player)
            .Where(ship => IsAiRefitContinuationCandidateShip(ship, player, refitDesign))
            .OrderByDescending(AgeYears)
            .ThenByDescending(ship => Safe(() => ship.Tonnage(), 0f))
            .ToList();

    private static bool IsAiRefitContinuationCandidateShip(Ship ship, Player owner, Ship refitDesign)
    {
        if (ship == null || refitDesign == null || Safe(() => ship.isErased, true))
            return false;

        if (Safe(() => ship.player?.Pointer != owner.Pointer, true))
            return false;

        if (Safe(() => ship.isDesign, true))
            return false;

        string shipRefitName = Safe(() => ship.refitDesignName, string.Empty);
        if (IsRefittingStatus(ship) ||
            Safe(() => ship.isRefit, false) ||
            Safe(() => ship.isRepairing, false) ||
            !string.IsNullOrWhiteSpace(shipRefitName))
        {
            LogContinueSkipShip(owner, ship, "already-refitting");
            return false;
        }

        if (!IsRefittableStatus(ship))
            return false;

        if (!string.Equals(
                AiDesignCompetitiveness.NormalizeShipType(ship.shipType),
                AiDesignCompetitiveness.NormalizeShipType(refitDesign.shipType),
                StringComparison.OrdinalIgnoreCase))
            return false;

        Ship? currentDesign = Safe(() => ship.design, null);
        if (SameShipIdentity(currentDesign, refitDesign))
            return false;

        if (IsVpSmartRefitTarget(owner, currentDesign, out _, out _, out _))
        {
            LogContinueSkipShip(owner, ship, "already-on-smart-refit");
            return false;
        }

        if (MainGunCount(ship) <= 0)
            return false;

        return MatchesContinuationLineage(ship, refitDesign);
    }

    private static void LogContinueSkipShip(Player player, Ship ship, string reason)
    {
        Ship? design = Safe(() => ship.design, null);
        LogAi(
            $"continue-skip-ship reason={LogToken(reason)} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} ship={LogLabel(AiDesignCompetitiveness.ShipLabel(ship))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(design))} status={LogToken(Safe(() => ship.status.ToString(), string.Empty))} refitDesignName={LogToken(Safe(() => ship.refitDesignName, string.Empty))}.");
    }

    private static bool MatchesContinuationLineage(Ship ship, Ship refitDesign)
    {
        Ship? baseline = Safe(() => refitDesign.designShipForRefit, null);
        Ship? currentDesign = Safe(() => ship.design, null);
        if (baseline != null && currentDesign != null && SameShipIdentity(currentDesign, baseline))
            return true;

        string shipBase = BaseClassName(ship);
        string refitBase = BaseClassName(refitDesign);
        if (!string.IsNullOrWhiteSpace(shipBase) &&
            !string.IsNullOrWhiteSpace(refitBase) &&
            string.Equals(shipBase, refitBase, StringComparison.OrdinalIgnoreCase))
            return true;

        if (baseline == null)
            return false;

        string baselineBase = BaseClassName(baseline);
        return !string.IsNullOrWhiteSpace(shipBase) &&
               !string.IsNullOrWhiteSpace(baselineBase) &&
               string.Equals(shipBase, baselineBase, StringComparison.OrdinalIgnoreCase);
    }

    private static void RunAiPlacementProbeIfNeeded(Player player, Ship source, AiRefitGroup group)
    {
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        LogAi(
            $"probe-start turn={LogToken(AiDesignCompetitiveness.CurrentTurnLabel())} nation={LogToken(nation)} type={group.Type} class={LogToken(group.ClassName)} source={LogToken(AiDesignCompetitiveness.ShipLabel(source))} modes={AiPlacementProbeModes.Length} startGuard={!AllowAiSmartRefitStarts}.");

        foreach (AiPlacementProbeMode mode in AiPlacementProbeModes)
            RunAiPlacementProbeMode(player, source, mode);
    }

    private static void RunAiPlacementProbeMode(Player player, Ship source, AiPlacementProbeMode mode)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        Ship? probeDesign = CreateAiSmartRefitDesign(player, source, out bool enteredConstructor, mode.ForceConstructor);
        if (probeDesign == null)
        {
            LogAi(
                $"probe-result nation={LogToken(nation)} mode={mode.Name} result=create-design-failed forceConstructor={mode.ForceConstructor} modelLoad={mode.LoadVisualModels} elapsedMs={timer.ElapsedMilliseconds}.");
            return;
        }

        RegisterInProgressSmartAiRefit(probeDesign);
        try
        {
            LogAiDebugPreService(
                player,
                probeDesign,
                source,
                enteredConstructor,
                "ai-probe-" + mode.Name,
                mode.LoadVisualModels,
                mode.ForceConstructor);

            SmartRefitResult result = SmartRefitService.ApplyForAiProbe(
                probeDesign,
                player,
                "ai-probe-" + mode.Name,
                mode.LoadVisualModels,
                Safe(() => source.design, null) ?? source);
            LogAi(
                $"probe-result nation={LogToken(nation)} mode={mode.Name} forceConstructor={mode.ForceConstructor} modelLoad={mode.LoadVisualModels} enteredConstructor={enteredConstructor} design={LogToken(AiDesignCompetitiveness.ShipLabel(probeDesign))} result={(result.Success ? "accepted" : "rejected")} message={LogToken(result.Message)} elapsedMs={timer.ElapsedMilliseconds} startGuard={!AllowAiSmartRefitStarts}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{AiLogPrefix}: placement probe failed nation={LogToken(nation)} mode={mode.Name}. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CleanupAiSmartRefitDesign(player, probeDesign, "placement-probe-" + mode.Name);
            UnregisterInProgressSmartAiRefit(probeDesign);
        }
    }

    private static Ship? CreateAiSmartRefitDesign(Player player, Ship source, out bool enteredConstructor, bool forceConstructor = false)
    {
        enteredConstructor = false;
        PlayerController? controller = PlayerController.Instance;
        if (controller == null)
            return null;

        Ship cloneSource = Safe(() => source.design, null) ?? source;
        Ship? refitDesign = Safe(() => controller.CloneShipRaw(cloneSource, true, false, false, string.Empty, player), null);
        if (refitDesign == null)
            return null;

        SafeDo(() => refitDesign.status = VesselEntity.Status.Normal);

        GameDate currentDate = Safe(() => CampaignController.Instance!.CurrentDate, Safe(() => source.dateCreated, default));
        string baseName = BaseClassName(cloneSource);
        string suffix = Safe(() => refitDesign.GetRefitYearNameEnd(player, false, false), string.Empty);
        string finalName = BuildAiSmartRefitName(baseName, suffix);

        SafeDo(() => refitDesign.player = player);
        SafeDo(() => refitDesign.designShipForRefit = cloneSource);
        SafeDo(() => refitDesign.dateCreated = currentDate);
        SafeDo(() => refitDesign.dateCreatedRefit = currentDate);
        SafeDo(() => refitDesign.name = finalName);
        SafeDo(() => refitDesign.vesselName = finalName);
        SafeDo(() => refitDesign.refitDesignName = finalName);
        SafeDo(() => refitDesign.GrabTechs(false, false, false));
        SafeDo(() => refitDesign.CalculateRefitZones());
        bool needsConstructor = forceConstructor || NeedsConstructorSetup(refitDesign);
        enteredConstructor = needsConstructor && Safe(() =>
        {
            refitDesign.EnterConstructor();
            return true;
        }, false);
        SafeDo(() => refitDesign.CalcWeightAndCost(true, true));
        return refitDesign;
    }

    private static bool NeedsConstructorSetup(Ship ship)
        => PartCount(ship) <= 0 ||
           GunPartCount(ship) <= 0 ||
           MainGunCount(ship) <= 0 ||
           !Safe(() => ship.partsCont != null, false);

    private static void LogAiDebugPreService(
        Player player,
        Ship refitDesign,
        Ship source,
        bool enteredConstructor,
        string serviceContext,
        bool loadVisualModels,
        bool forceConstructor)
    {
        if (!VerboseAiRefitDiagnostics)
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{AiDebugLogPrefix}: pre-service context={LogToken(serviceContext)} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(refitDesign))} source={LogToken(AiDesignCompetitiveness.ShipLabel(source))} isDesign={Safe(() => refitDesign.isDesign, false)} isRefitDesign={Safe(() => refitDesign.isRefitDesign, false)} isErased={Safe(() => refitDesign.isErased, false)} dateCreatedRefit={Safe(() => refitDesign.dateCreatedRefit.turn, -1)}/{Safe(() => refitDesign.dateCreatedRefit.AsDate().Year, -1)} parts={PartCount(refitDesign)} hullAndParts={HullAndPartsCount(refitDesign)} gunParts={GunPartCount(refitDesign)} mainGuns={MainGunCount(refitDesign)} partsCont={(Safe(() => refitDesign.partsCont != null, false) ? "yes" : "no")} baseline={(Safe(() => refitDesign.designShipForRefit != null, false) ? "yes" : "no")} constructor={GameManager.IsConstructor} forceConstructor={forceConstructor} enteredConstructor={enteredConstructor} modelLoad={loadVisualModels} startGuard={!AllowAiSmartRefitStarts}.");
    }

    private static void LogAiDebugRefitStartState(
        Player player,
        Ship refitDesign,
        Il2CppSystem.Collections.Generic.List<Ship> selection,
        string stage)
    {
        if (!VerboseAiRefitDiagnostics)
            return;

        IsReferencedByLiveRefitShip(player, refitDesign, out int references, out string referenceExamples);
        string selected = string.Join(
            "|",
            SafeShipList(selection)
                .Take(4)
                .Select(ShipReferenceSummary));
        if (string.IsNullOrWhiteSpace(selected))
            selected = "none";

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{AiDebugLogPrefix}: {stage} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(refitDesign))} id={LogToken(AiDesignCompetitiveness.ShipId(refitDesign))} linkDesign={LogToken(AiDesignCompetitiveness.ShipId(Safe(() => refitDesign.design, null)))} baseline={LogToken(AiDesignCompetitiveness.ShipId(Safe(() => refitDesign.designShipForRefit, null)))} isDesign={Safe(() => refitDesign.isDesign, false)} isRefitDesign={Safe(() => refitDesign.isRefitDesign, false)} dateCreatedRefit={Safe(() => refitDesign.dateCreatedRefit.turn, -1)}/{Safe(() => refitDesign.dateCreatedRefit.AsDate().Year, -1)} status={LogToken(Safe(() => refitDesign.status.ToString(), string.Empty))} inDesigns={ContainsShipByIdentity(PlayerDesigns(player), refitDesign)} inFleet={ContainsShipByIdentity(SafeShipList(player.fleetAll), refitDesign)} liveRefs={references} liveRefExamples={LogToken(referenceExamples)} selected={selected}.");
    }

    private static List<AiRefitGroup> BuildAiRefitGroups(Player player, bool force)
    {
        int currentYear = CurrentCampaignYear();
        return SafeShipList(player.fleetAll)
            .Where(ship => IsAiRefitCandidateShip(ship, player, currentYear, force))
            .GroupBy(ship => GroupKey(ship), StringComparer.Ordinal)
            .Select(group => new AiRefitGroup(group.Key, group.ToList()))
            .Where(group => group.Ships.Count > 0)
            .OrderByDescending(group => group.OldestAge)
            .ThenByDescending(group => group.Ships.Count)
            .ThenByDescending(group => group.TotalTonnage)
            .ToList();
    }

    private static bool IsAiRefitCandidateShip(Ship ship, Player owner, int currentYear, bool force)
    {
        if (ship == null || Safe(() => ship.isErased, true))
            return false;

        if (Safe(() => ship.player?.Pointer != owner.Pointer, true))
            return false;

        if (Safe(() => ship.isDesign, true))
            return false;

        string type = AiDesignCompetitiveness.NormalizeShipType(ship.shipType);
        if (!CampaignAiDesignRosterPrunePatch.SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return false;

        string shipRefitName = Safe(() => ship.refitDesignName, string.Empty);
        if (IsRefittingStatus(ship) ||
            Safe(() => ship.isRefit, false) ||
            Safe(() => ship.isRepairing, false) ||
            !string.IsNullOrWhiteSpace(shipRefitName))
        {
            LogCandidateSkipShip(owner, ship, "already-assigned-refit");
            return false;
        }

        Ship? currentDesign = Safe(() => ship.design, null);
        if (IsPotentialSmartRefitTarget(currentDesign) &&
            IsVpSmartRefitTarget(owner, currentDesign, out _, out _, out _))
        {
            LogCandidateSkipShip(owner, ship, "already-on-smart-refit");
            return false;
        }

        if (!IsRefittableStatus(ship))
            return false;

        if (!force && currentYear - Safe(() => ship.dateCreated.AsDate().Year, currentYear) < MinAiRefitAgeYears)
            return false;

        return MainGunCount(ship) > 0;
    }

    private static void LogCandidateSkipShip(Player player, Ship ship, string reason)
    {
        Ship? design = Safe(() => ship.design, null);
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        RecordAiRefitSummary(
            AiSmartRefitSummaryKind.Skipped,
            nation,
            $"{ReadableLogText(nation)} {ReadableDesignLabel(design)}: skipped {ReadableLogText(reason)}");
        LogAi(
            $"candidate-skip reason={LogToken(reason)} nation={LogToken(nation)} ship={LogLabel(AiDesignCompetitiveness.ShipLabel(ship))} design={LogLabel(AiDesignCompetitiveness.ShipLabel(design))} status={LogToken(Safe(() => ship.status.ToString(), string.Empty))} refitDesignName={LogToken(Safe(() => ship.refitDesignName, string.Empty))}.");
    }

    private static bool IsRefittableStatus(Ship ship)
    {
        string status = Safe(() => ship.status.ToString(), string.Empty);
        return status is "Normal" or "InSea" or "Mothballed" or "LowCrew";
    }

    private static Il2CppSystem.Collections.Generic.List<Ship> SelectShipsForAiRefit(
        IReadOnlyList<Ship> candidates,
        Ship refitDesign,
        Player player,
        bool force)
    {
        Il2CppSystem.Collections.Generic.List<Ship> selection = new();
        float costPerMonth = Safe(() => refitDesign.RefitCostPerMonth(), 0f);
        float maxBudget = force ? float.MaxValue : Math.Max(0f, Safe(() => player.cash, 0f));
        float reserved = 0f;

        foreach (Ship ship in candidates.OrderByDescending(AgeYears).Take(MaxShipsPerAiRefitStart))
        {
            if (ship == null)
                continue;

            float nextCost = Math.Max(0f, costPerMonth);
            if (!force && nextCost > 0f && reserved + nextCost > maxBudget)
                continue;

            selection.Add(ship);
            reserved += nextCost;
        }

        return selection;
    }

    private static bool PassAiRefitChanceGate(Player player, out float chance, out float roll)
    {
        chance = Math.Clamp(Safe(() => CampaignController.Param("ai_refit_base", 0.25f), 0.25f), 0f, 1f);
        int turn = AiDesignCompetitiveness.CurrentTurnIndex();
        uint hash = unchecked((uint)HashCode.Combine(turn, PlayerPointer(player), "uadvp-smart-refit"));
        roll = hash / (float)uint.MaxValue;
        return roll <= chance;
    }

    private static bool PassAiRefitBudgetGate(Player player, out string reason)
    {
        float cash = Safe(() => player.cash, 0f);
        float revenue = Math.Max(1f, Safe(() => player.Revenue(), 0f));
        float minimumCash = revenue * 0.5f;
        if (cash < minimumCash)
        {
            reason = "budget";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool CanBuildSmartRefitDesign(Ship design, out string reason)
    {
        reason = "unknown";
        try
        {
            bool result = AiDesignBuildability.CanBuildDesign(
                Safe(() => design.player, null),
                design,
                1,
                "SmartRefitStart",
                out reason);
            reason = NormalizeReason(reason);
            if (result)
                return true;

            if (string.Equals(reason, "obsolete", StringComparison.OrdinalIgnoreCase) &&
                IsRefitDesignForBuildability(design))
            {
                LogAi($"buildability-obsolete-allowed design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} reason=obsolete.");
                reason = "obsolete-allowed";
                return true;
            }

            if (SmartRefitService.CanUseWaiverAwareBuildabilityForRefit(
                    design,
                    reason,
                    out int waivedBadParts,
                    out string waiverDetails))
            {
                LogAi(
                    $"start-buildability-waiver-allowed design={LogLabel(AiDesignCompetitiveness.ShipLabel(design))} reason={LogToken(reason)} waivedBadParts={waivedBadParts} details={LogToken(waiverDetails)}.");
                reason = "waiver-allowed";
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            reason = "checkFailed:" + ex.GetType().Name;
            return false;
        }
    }

    private static bool IsRefitDesignForBuildability(Ship design)
        => Safe(() => design.isRefitDesign, false) ||
           Safe(() => design.designShipForRefit != null, false);

    private static void CleanupAiSmartRefitDesign(Player player, Ship? design, string reason, bool visualsAlreadyCleaned = false)
    {
        if (design == null)
            return;

        if (!visualsAlreadyCleaned)
        {
            SmartRefitService.CleanupConstructorVisualsBeforeLeave(
                design,
                "ai-cleanup-" + reason,
                logDetails: VerboseAiRefitDiagnostics);
            visualsAlreadyCleaned = true;
        }

        UnregisterInProgressSmartAiRefit(design);
        UnregisterAcceptedSmartAiRefit(design);
        TryRemoveAiRefitDesign(player, design, visualsAlreadyCleaned);
        LogAi(
            $"cleanup nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} reason={LogToken(reason)}.");
    }

    private static void RegisterInProgressSmartAiRefit(Ship design)
    {
        long pointer = ShipPointer(design);
        if (pointer != 0L)
            InProgressSmartAiRefitDesigns.Add(pointer);
    }

    private static void UnregisterInProgressSmartAiRefit(Ship? design)
    {
        long pointer = ShipPointer(design);
        if (pointer != 0L)
            InProgressSmartAiRefitDesigns.Remove(pointer);
    }

    private static void RegisterAcceptedSmartAiRefit(Ship design)
    {
        long pointer = ShipPointer(design);
        if (pointer != 0L)
            AcceptedSmartAiRefitDesigns.Add(pointer);
    }

    private static void UnregisterAcceptedSmartAiRefit(Ship? design)
    {
        long pointer = ShipPointer(design);
        if (pointer != 0L)
            AcceptedSmartAiRefitDesigns.Remove(pointer);
    }

    private static string GroupKey(Ship ship)
    {
        string type = AiDesignCompetitiveness.NormalizeShipType(ship.shipType);
        Ship? design = Safe(() => ship.design, null);
        string designKey = DesignGroupKey(type, design);
        if (!string.IsNullOrWhiteSpace(designKey))
            return designKey;

        return ClassGroupKey(type, ship);
    }

    private static string DesignGroupKey(Ship? ship)
    {
        if (ship == null)
            return string.Empty;

        return DesignGroupKey(AiDesignCompetitiveness.NormalizeShipType(ship.shipType), ship);
    }

    private static string DesignGroupKey(string type, Ship? ship)
    {
        string designId = AiDesignCompetitiveness.ShipId(ship);
        if (string.IsNullOrWhiteSpace(type) ||
            string.IsNullOrWhiteSpace(designId) ||
            string.Equals(designId, "<null>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"{type}|design:{designId}";
    }

    private static string ClassGroupKey(Ship? ship)
    {
        if (ship == null)
            return string.Empty;

        return ClassGroupKey(AiDesignCompetitiveness.NormalizeShipType(ship.shipType), ship);
    }

    private static string ClassGroupKey(string type, Ship ship)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        return $"{type}|class:{BaseClassName(ship).ToUpperInvariant()}";
    }

    private static string BaseClassName(Ship ship)
    {
        Ship? design = Safe(() => ship.design, null);
        string name = DesignRefitNamePatch.CleanRefitBaseNameForVp(design ?? ship);
        if (string.IsNullOrWhiteSpace(name))
            name = Safe(() => design?.name ?? string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            name = Safe(() => ship.name, string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            name = AiDesignCompetitiveness.ShipLabel(ship);

        name = TrimGeneratedNameEdge(name);
        return string.IsNullOrWhiteSpace(name) ? "Refit" : name;
    }

    private static string BuildAiSmartRefitName(string baseName, string suffix)
    {
        string cleanBase = TrimGeneratedNameEdge(baseName);
        if (string.IsNullOrWhiteSpace(cleanBase))
            cleanBase = "Refit";

        string cleanSuffix = suffix?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanSuffix))
            return $"{cleanBase} ({CurrentCampaignYear()})";

        if (cleanSuffix.StartsWith("(", StringComparison.Ordinal))
            return $"{cleanBase} {cleanSuffix}";

        return TrimGeneratedNameEdge(cleanSuffix);
    }

    private static string TrimGeneratedNameEdge(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('_', '-', ':', ' ');

    private static int MainGunCount(Ship ship)
    {
        int count = 0;
        foreach (Part part in ShipParts(ship))
        {
            if (!Safe(() => part.data?.isGun ?? false, false))
                continue;

            if (Safe(() => ship.IsMainCal(part), Safe(() => ship.IsMainCal(part.data), false)))
                count++;
        }

        return count;
    }

    private static int PartCount(Ship ship)
        => Safe(() => ship.parts?.Count ?? 0, 0);

    private static int HullAndPartsCount(Ship ship)
        => Safe(() => ship.hullAndPartsInited?.Count ?? 0, 0);

    private static int GunPartCount(Ship ship)
        => ShipParts(ship).Count(part => Safe(() => part.data?.isGun ?? false, false));

    private static List<Part> ShipParts(Ship ship)
    {
        List<Part> parts = new();
        try
        {
            if (ship.parts == null)
                return parts;

            foreach (Part part in ship.parts)
            {
                if (part != null)
                    parts.Add(part);
            }
        }
        catch
        {
        }

        return parts;
    }

    private static List<Ship> PlayerFleetShips(Player player)
    {
        List<Ship> result = new();
        try
        {
            foreach (Ship ship in player.GetFleetAll())
            {
                if (ship != null)
                    result.Add(ship);
            }
        }
        catch
        {
            result = SafeShipList(player.fleetAll);
        }

        return result;
    }

    private static List<Ship> SafeShipList(Il2CppSystem.Collections.Generic.IEnumerable<Ship>? ships)
    {
        List<Ship> result = new();
        if (ships == null)
            return result;

        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Ship>(ships);
            foreach (Ship ship in list)
            {
                if (ship != null)
                    result.Add(ship);
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<Ship> SafeShipList(Il2CppSystem.Collections.Generic.List<Ship>? ships)
    {
        List<Ship> result = new();
        if (ships == null)
            return result;

        try
        {
            foreach (Ship ship in ships)
            {
                if (ship != null)
                    result.Add(ship);
            }
        }
        catch
        {
        }

        return result;
    }

    private static float AgeYears(Ship ship)
    {
        int currentYear = CurrentCampaignYear();
        int createdYear = Safe(() => ship.dateCreated.AsDate().Year, currentYear);
        return Math.Max(0, currentYear - createdYear);
    }

    private static int CurrentCampaignYear()
        => Safe(() => CampaignController.Instance!.CurrentDate.AsDate().Year, 0);

    internal static void CleanupAiRefitDesigns(CampaignController? controller, string context)
    {
        if (!ModSettings.SmartRefitsEnabled)
            return;

        try
        {
            var players = (controller ?? CampaignController.Instance)?.CampaignData?.PlayersMajor;
            if (players == null)
                return;

            foreach (Player player in players)
            {
                if (!ShouldBlockAiRefits(player))
                    continue;

                List<Ship> refitDesigns = PlayerDesigns(player)
                    .Where(IsAiRefitDesignLike)
                    .ToList();
                if (refitDesigns.Count == 0)
                    continue;

                RepairDuplicateAiRefitNames(player, refitDesigns, context);

                int removed = 0;
                List<string> examples = new();
                foreach (Ship design in refitDesigns)
                {
                    if (ShouldProtectAiRefitDesignFromDeletion(player, design, out string protectReason, out int protectedRefs, out string protectedExamples))
                    {
                        if (VerboseAiRefitDiagnostics)
                        {
                            Melon<UADVanillaPlusMod>.Logger.Msg(
                                $"{AiDebugLogPrefix}: cleanup-skip nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} context={LogToken(context)} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason={LogToken(protectReason)} liveRefs={protectedRefs} liveRefExamples={LogToken(protectedExamples)}.");
                        }
                        CleanupProtectedAiRefitVisuals(player, design, context, protectReason);
                        continue;
                    }

                    if (!IsBlockedAiRefitDesign(design))
                    {
                        continue;
                    }

                    IsReferencedByLiveRefitShip(player, design, out int liveRefs, out string liveRefExamples);
                    if (VerboseAiRefitDiagnostics)
                    {
                        Melon<UADVanillaPlusMod>.Logger.Msg(
                            $"{AiDebugLogPrefix}: cleanup-candidate nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} context={LogToken(context)} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} status={LogToken(Safe(() => design.status.ToString(), string.Empty))} isErased={Safe(() => design.isErased, false)} isRefitDesign={Safe(() => design.isRefitDesign, false)} dateCreatedRefit={Safe(() => design.dateCreatedRefit.turn, -1)}/{Safe(() => design.dateCreatedRefit.AsDate().Year, -1)} liveRefs={liveRefs} liveRefExamples={LogToken(liveRefExamples)}.");
                    }

                    if (TryRemoveAiRefitDesign(player, design))
                    {
                        removed++;
                        if (examples.Count < 4)
                            examples.Add(LogToken(AiDesignCompetitiveness.ShipLabel(design)));
                    }
                }

                if (removed <= 0)
                    continue;

                string turn = AiDesignCompetitiveness.CurrentTurnLabel();
                string key = $"{turn}|{PlayerPointer(player)}|{context}|{removed}";
                if (LoggedCleanup.Add(key))
                {
                    Melon<UADVanillaPlusMod>.Logger.Msg(
                        $"{LogPrefix}: removed leaked AI refit designs turn={LogToken(turn)} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} context={LogToken(context)} removed={removed} examples={string.Join(",", examples)}.");
                }
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: leaked AI refit design cleanup failed during {LogToken(context)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RepairDuplicateAiRefitNames(Player player, List<Ship> refitDesigns, string context)
    {
        List<AiRefitNameRepairEntry> entries = new();
        foreach (Ship design in refitDesigns)
        {
            if (TryBuildAiRefitNameRepairEntry(player, design, out AiRefitNameRepairEntry? entry) && entry != null)
                entries.Add(entry);
        }

        if (entries.Count < 2)
            return;

        foreach (IGrouping<string, AiRefitNameRepairEntry> group in entries.GroupBy(entry => entry.GroupKey, StringComparer.Ordinal))
        {
            List<AiRefitNameRepairEntry> groupEntries = group.ToList();
            if (groupEntries.Count < 2)
                continue;

            HashSet<AiRefitNameRepairEntry> toRename = new();
            foreach (IGrouping<int, AiRefitNameRepairEntry> ordinalGroup in groupEntries.GroupBy(entry => Math.Max(1, entry.Ordinal)))
            {
                List<AiRefitNameRepairEntry> duplicates = SortRefitNameRepairEntries(ordinalGroup).ToList();
                foreach (AiRefitNameRepairEntry duplicate in duplicates.Skip(1))
                    toRename.Add(duplicate);
            }

            foreach (IGrouping<string, AiRefitNameRepairEntry> nameGroup in groupEntries.GroupBy(entry => entry.VisibleNameKey, StringComparer.Ordinal))
            {
                List<AiRefitNameRepairEntry> duplicates = SortRefitNameRepairEntries(nameGroup).ToList();
                foreach (AiRefitNameRepairEntry duplicate in duplicates.Skip(1))
                    toRename.Add(duplicate);
            }

            if (toRename.Count == 0)
                continue;

            HashSet<int> usedOrdinals = groupEntries
                .Where(entry => !toRename.Contains(entry))
                .Select(entry => Math.Max(1, entry.Ordinal))
                .ToHashSet();

            int renamed = 0;
            List<string> examples = new();
            foreach (AiRefitNameRepairEntry entry in SortRefitNameRepairEntries(toRename))
            {
                int newOrdinal = NextFreeRefitOrdinal(usedOrdinals);
                usedOrdinals.Add(newOrdinal);
                string newName = DesignRefitNamePatch.BuildRefitYearNameForVp(entry.BaseName, entry.Year, newOrdinal);
                string oldName = AiDesignCompetitiveness.ShipLabel(entry.Design);

                SafeDo(() => entry.Design.name = newName);
                SafeDo(() => entry.Design.vesselName = newName);
                SafeDo(() => entry.Design.refitDesignName = newName);
                int liveUpdated = UpdateLiveShipsForRenamedRefit(player, entry.Design, newName);

                renamed++;
                if (examples.Count < 4)
                    examples.Add($"{LogToken(entry.Id)}->{LogToken(newName)}:ships={liveUpdated}:old={LogToken(oldName)}");
            }

            if (renamed <= 0)
                continue;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: repaired duplicate AI refit names nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} context={LogToken(context)} type={LogToken(groupEntries[0].Type)} base={LogToken(groupEntries[0].BaseName)} year={groupEntries[0].Year} renamed={renamed} examples={string.Join(",", examples)}.");
        }
    }

    private static bool TryBuildAiRefitNameRepairEntry(Player player, Ship design, out AiRefitNameRepairEntry? entry)
    {
        entry = null;
        if (design == null || Safe(() => design.isErased, true))
            return false;

        bool isRefitDesign = Safe(() => design.isRefitDesign, false);
        int refitTurn = Safe(() => design.dateCreatedRefit.turn, 0);
        if (!isRefitDesign && refitTurn <= 0)
            return false;

        string baseName = string.Empty;
        int year = 0;
        int ordinal = 1;
        foreach (string candidateName in DesignRefitNamePatch.RefitNameCandidatesForVp(design))
        {
            if (!DesignRefitNamePatch.TryReadRefitYearNameForVp(candidateName, design, out string candidateBase, out int candidateYear, out int candidateOrdinal))
                continue;

            baseName = candidateBase;
            year = candidateYear;
            ordinal = Math.Max(1, candidateOrdinal);
            break;
        }

        if (year <= 0)
            year = Safe(() => design.dateCreatedRefit.AsDate().Year, 0);

        if (year <= 0)
            return false;

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = DesignRefitNamePatch.CleanRefitBaseNameForVp(design);

        baseName = TrimGeneratedNameEdge(baseName);
        if (string.IsNullOrWhiteSpace(baseName))
            return false;

        string type = AiDesignCompetitiveness.NormalizeShipType(design.shipType);
        if (string.IsNullOrWhiteSpace(type))
            return false;

        IsReferencedByLiveRefitShip(player, design, out int liveRefs, out string liveRefExamples);
        string visibleName = FirstRefitNameCandidate(design);
        string id = AiDesignCompetitiveness.ShipId(design);
        entry = new AiRefitNameRepairEntry(
            design,
            type,
            baseName,
            year,
            ordinal,
            RefitNameRepairGroupKey(type, baseName, year),
            RefitNameVisibleKey(visibleName),
            id,
            liveRefs,
            liveRefExamples,
            refitTurn <= 0 ? int.MaxValue : refitTurn);
        return true;
    }

    private static IEnumerable<AiRefitNameRepairEntry> SortRefitNameRepairEntries(IEnumerable<AiRefitNameRepairEntry> entries)
        => entries
            .OrderByDescending(entry => entry.LiveRefs > 0)
            .ThenBy(entry => entry.RefitTurn)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

    private static int NextFreeRefitOrdinal(HashSet<int> usedOrdinals)
    {
        int ordinal = 1;
        while (usedOrdinals.Contains(ordinal))
            ordinal++;

        return ordinal;
    }

    private static int UpdateLiveShipsForRenamedRefit(Player player, Ship design, string newName)
    {
        int updated = 0;
        foreach (Ship ship in PlayerFleetShips(player))
        {
            if (ship == null || Safe(() => ship.isErased, true) || Safe(() => ship.isDesign, false))
                continue;

            if (!SameShipIdentity(Safe(() => ship.design, null), design))
                continue;

            SafeDo(() => ship.refitDesignName = newName);
            updated++;
        }

        return updated;
    }

    private static string FirstRefitNameCandidate(Ship design)
    {
        foreach (string candidate in DesignRefitNamePatch.RefitNameCandidatesForVp(design))
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return AiDesignCompetitiveness.ShipLabel(design);
    }

    private static string RefitNameRepairGroupKey(string type, string baseName, int year)
        => $"{type.ToUpperInvariant()}|{baseName.ToUpperInvariant()}|{year}";

    private static string RefitNameVisibleKey(string value)
        => TrimGeneratedNameEdge(value).ToUpperInvariant();

    internal static bool BlockAiRefitMoveNext(object? stateMachine, ref bool result)
    {
        if (stateMachine == null || !ModSettings.SmartRefitsEnabled)
            return true;

        try
        {
            AiRefitStateMachineFields fields = ResolveAiRefitStateMachineFields(stateMachine.GetType());
            Player? player = fields.Player?.GetValue(stateMachine) as Player;
            if (!ShouldBlockAiRefits(player))
                return true;

            bool force = ReadBoolField(fields.Force, stateMachine);
            bool forceSimple = ReadBoolField(fields.ForceSimple, stateMachine);
            SetIntField(fields.State, stateMachine, -2);
            result = false;
            CleanupAiRefitDesigns(CampaignController.Instance, "AiRefitShips MoveNext");
            LogBlockedAiRefit(player, force, forceSimple, "AiRefitShips.MoveNext");
            return false;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: AI refit MoveNext guard failed; allowing vanilla refit. {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static IEnumerable<Ship> PlayerDesigns(Player player)
    {
        Il2CppSystem.Collections.Generic.List<Ship>? designs = Safe(
            () => player.designs == null ? null : new Il2CppSystem.Collections.Generic.List<Ship>(player.designs),
            null);
        if (designs == null)
            yield break;

        foreach (Ship design in designs)
        {
            if (design != null)
                yield return design;
        }
    }

    private static bool TryRemoveAiRefitDesign(Player player, Ship design, bool visualsAlreadyCleaned = false)
    {
        if (!Safe(() => design.isDesign, false))
        {
            if (VerboseAiRefitDiagnostics)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{AiDebugLogPrefix}: cleanup-skip nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason=not-design-row status={LogToken(Safe(() => design.status.ToString(), string.Empty))} isRefitDesign={Safe(() => design.isRefitDesign, false)}.");
            }
            return false;
        }

        if (ShouldProtectAiRefitDesignFromDeletion(player, design, out string protectReason, out int protectedRefs, out string protectedExamples))
        {
            if (VerboseAiRefitDiagnostics)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{AiDebugLogPrefix}: cleanup-skip nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason={LogToken(protectReason)} liveRefs={protectedRefs} liveRefExamples={LogToken(protectedExamples)}.");
            }
            CleanupProtectedAiRefitVisuals(player, design, "try-remove", protectReason);
            return false;
        }

        if (IsReferencedByLiveRefitShip(player, design, out int references, out string referenceExamples))
        {
            if (VerboseAiRefitDiagnostics)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{AiDebugLogPrefix}: cleanup-skip nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason=referenced-before-delete liveRefs={references} liveRefExamples={LogToken(referenceExamples)}.");
            }
            return false;
        }

        UnregisterInProgressSmartAiRefit(design);
        UnregisterAcceptedSmartAiRefit(design);
        string visualSummary = visualsAlreadyCleaned
            ? "constructorVisualCleanup=skipped_already-cleaned"
            : SmartRefitService.CleanupConstructorVisualsBeforeLeave(
                design,
                "ai-cleanup-before-delete",
                logDetails: VerboseAiRefitDiagnostics);
        LogAi(
            $"cleanup-unload nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} visuals={LogToken(visualSummary)}.");

        try
        {
            CampaignController.Instance?.DeleteDesign(design);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: DeleteDesign failed for leaked AI refit {LogToken(AiDesignCompetitiveness.PlayerLabel(player))}/{LogToken(AiDesignCompetitiveness.ShipLabel(design))}. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (!Safe(() => design.isErased, false))
                design.Erase();
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: Erase failed for leaked AI refit {LogToken(AiDesignCompetitiveness.PlayerLabel(player))}/{LogToken(AiDesignCompetitiveness.ShipLabel(design))}. {ex.GetType().Name}: {ex.Message}");
        }

        return Safe(() => design.isErased, false) ||
               !PlayerDesigns(player).Any(existing => Safe(() => existing.Pointer == design.Pointer, false));
    }

    private static void CleanupProtectedAiRefitVisuals(Player player, Ship design, string context, string protectReason)
    {
        if (!ModSettings.SmartRefitsEnabled || !ShouldBlockAiRefits(player) || !IsAiRefitDesignLike(design))
            return;

        string reason = "ai-protected-" + context + "-" + protectReason;
        string visualSummary = SmartRefitService.CleanupConstructorVisualsBeforeLeave(
            design,
            reason,
            logDetails: VerboseAiRefitDiagnostics);
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        IsReferencedByLiveRefitShip(player, design, out int liveRefs, out _);
        RecordAiRefitSummary(
            AiSmartRefitSummaryKind.Protected,
            nation,
            $"{nation} {ReadableDesignLabel(design)}: protected liveRefs={liveRefs}");
        if (VerboseAiRefitDiagnostics)
        {
            LogAi(
                $"protected-cleanup nation={LogToken(nation)} context={LogToken(context)} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} reason={LogToken(protectReason)} visuals={LogToken(visualSummary)}.");
        }
    }

    private static bool IsAiRefitDesignLike(Ship? design)
        => design != null &&
           !Safe(() => design.isErased, true) &&
           (Safe(() => design.isRefitDesign, false) ||
            Safe(() => design.dateCreatedRefit.turn > 0, false));

    private static bool IsReferencedByLiveRefitShip(Player? player, Ship? design, out int references, out string examples)
    {
        references = 0;
        examples = "none";
        if (player == null || design == null)
            return false;

        List<string> sample = new();

        foreach (Ship ship in SafeShipList(player.fleetAll))
        {
            if (ship == null || Safe(() => ship.isErased, true) || Safe(() => ship.isDesign, false))
                continue;

            if (!TryMatchRefitDesignReference(ship, design, out string reason))
                continue;

            references++;
            if (sample.Count < 3)
            {
                Ship? linkedDesign = Safe(() => ship.design, null);
                string shipRefitName = Safe(() => ship.refitDesignName, string.Empty);
                sample.Add(
                    $"{LogToken(AiDesignCompetitiveness.ShipLabel(ship))}:{reason}:{LogToken(AiDesignCompetitiveness.ShipId(linkedDesign))}:shipIsDesign={Safe(() => ship.isDesign, false)}:shipIsRefitDesign={Safe(() => ship.isRefitDesign, false)}:status={LogToken(Safe(() => ship.status.ToString(), string.Empty))}:refitName={LogToken(shipRefitName)}");
            }
        }

        if (sample.Count > 0)
            examples = string.Join("|", sample);

        return references > 0;
    }

    private static List<Ship> LiveShipsReferencingRefitDesign(Player player, Ship design)
        => PlayerFleetShips(player)
            .Where(ship => ship != null &&
                           !Safe(() => ship.isErased, true) &&
                           !Safe(() => ship.isDesign, false) &&
                           TryMatchRefitDesignReference(ship, design, out _))
            .ToList();

    private static bool IsLiveFleetShip(Player? player, Ship? candidate, out string examples)
    {
        examples = "none";
        if (player == null || candidate == null)
            return false;

        long candidatePointer = ShipPointer(candidate);
        string candidateId = AiDesignCompetitiveness.ShipId(candidate);
        List<string> sample = new();
        foreach (Ship ship in PlayerFleetShips(player))
        {
            if (ship == null || Safe(() => ship.isErased, true) || Safe(() => ship.isDesign, false))
                continue;

            long shipPointer = ShipPointer(ship);
            bool sameObject = candidatePointer != 0L && shipPointer == candidatePointer;
            bool sameFallbackId = candidatePointer == 0L &&
                                  !string.IsNullOrWhiteSpace(candidateId) &&
                                  !string.Equals(candidateId, "<null>", StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(AiDesignCompetitiveness.ShipId(ship), candidateId, StringComparison.Ordinal);
            if (!sameObject && !sameFallbackId)
                continue;

            if (sample.Count < 3)
                sample.Add(ShipReferenceSummary(ship));
        }

        if (sample.Count == 0)
            return false;

        examples = string.Join("|", sample);
        return true;
    }

    private static bool TryMatchRefitDesignReference(Ship ship, Ship design, out string reason)
    {
        reason = "none";
        Ship? linkedDesign = Safe(() => ship.design, null);
        if (SameShipIdentity(linkedDesign, design))
        {
            reason = "design";
            return true;
        }

        string shipRefitName = Safe(() => ship.refitDesignName, string.Empty);
        if (string.IsNullOrWhiteSpace(shipRefitName))
            return false;

        string designName = Safe(() => design.name, string.Empty);
        string refitName = Safe(() => design.refitDesignName, string.Empty);
        if (string.Equals(shipRefitName, designName, StringComparison.Ordinal) ||
            string.Equals(shipRefitName, refitName, StringComparison.Ordinal))
        {
            reason = "refitName";
            return true;
        }

        return false;
    }

    private static string ReferenceExamples(List<Ship> ships)
    {
        if (ships.Count == 0)
            return "none";

        return string.Join("|", ships.Take(3).Select(ShipReferenceSummary));
    }

    private static bool IsRefittingStatus(Ship ship)
    {
        string status = Safe(() => ship.status.ToString(), string.Empty);
        return status.Equals("Refit", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("Repair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsShipByIdentity(IEnumerable<Ship> ships, Ship target)
    {
        string targetId = AiDesignCompetitiveness.ShipId(target);
        long targetPointer = ShipPointer(target);
        foreach (Ship ship in ships)
        {
            if (ship == null)
                continue;

            if (targetPointer != 0L && Safe(() => ship.Pointer == target.Pointer, false))
                return true;

            if (string.Equals(AiDesignCompetitiveness.ShipId(ship), targetId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool SameShipIdentity(Ship? left, Ship? right)
    {
        if (left == null || right == null)
            return false;

        long leftPointer = ShipPointer(left);
        long rightPointer = ShipPointer(right);
        if (leftPointer != 0L && leftPointer == rightPointer)
            return true;

        string leftId = AiDesignCompetitiveness.ShipId(left);
        string rightId = AiDesignCompetitiveness.ShipId(right);
        return !string.IsNullOrWhiteSpace(leftId) &&
               !string.IsNullOrWhiteSpace(rightId) &&
               !string.Equals(leftId, "<null>", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftId, rightId, StringComparison.Ordinal);
    }

    private static string ShipReferenceSummary(Ship ship)
    {
        Ship? design = Safe(() => ship.design, null);
        return $"{LogToken(AiDesignCompetitiveness.ShipLabel(ship))}:id={LogToken(AiDesignCompetitiveness.ShipId(ship))}:design={LogToken(AiDesignCompetitiveness.ShipId(design))}:status={LogToken(Safe(() => ship.status.ToString(), string.Empty))}:refit={LogToken(Safe(() => ship.refitDesignName, string.Empty))}";
    }

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "?" : value.Trim().Replace(' ', '_');

    private static string LogLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"?\"";

        string label = value.Trim().Replace("\"", "'", StringComparison.Ordinal);
        return $"\"{label}\"";
    }

    private static string ReadableDesignLabel(Ship? ship)
    {
        if (ship == null)
            return "?";

        string label = AiDesignCompetitiveness.ShipLabel(ship);
        int year = Safe(() => ship.dateCreatedRefit.AsDate().Year, 0);
        if (year <= 0)
            year = Safe(() => ship.dateCreated.AsDate().Year, 0);
        return year > 0 ? $"{ReadableLogText(label)} ({year})" : ReadableLogText(label);
    }

    private static string ReadableLogText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        return value
            .Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace(";", ",")
            .Replace("|", "/")
            .Replace("[", "(")
            .Replace("]", ")");
    }

    private static string ExtractSkipReason(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "unknown";

        int index = detail.IndexOf("skipped ", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return "other";

        string reason = detail[(index + "skipped ".Length)..].Trim();
        int stop = reason.IndexOfAny(new[] { ' ', ',', ';', '|', '.' });
        if (stop >= 0)
            reason = reason[..stop];

        return string.IsNullOrWhiteSpace(reason) ? "unknown" : ReadableLogText(reason);
    }

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "none";

        string normalized = reason.Trim().Trim('$');
        normalized = normalized.Replace("Ui_Constr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
    }

    private static string Fmt(float value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    internal static void FlushPendingAiRefitTurnSummariesAfterNextTurn()
    {
        if (!ModSettings.SmartRefitsEnabled && PendingAiRefitTurnSummaries.Count == 0)
            return;

        List<AiSmartRefitTurnSummary> summaries = PendingAiRefitTurnSummaries.Values
            .OrderBy(summary => summary.TurnIndex)
            .ThenBy(summary => summary.TurnLabel, StringComparer.Ordinal)
            .ToList();
        PendingAiRefitTurnSummaries.Clear();

        if (summaries.Count == 0)
        {
            string turn = AiDesignCompetitiveness.CurrentTurnLabel();
            string key = $"{AiDesignCompetitiveness.CurrentTurnIndex()}:{turn}:empty";
            if (LoggedEmptyAiRefitSummaries.Add(key))
                Melon<UADVanillaPlusMod>.Logger.Msg($"{AiLogPrefix} summary turn={LogToken(turn)} entries=0");
            return;
        }

        foreach (AiSmartRefitTurnSummary summary in summaries)
        {
            try
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(summary.ToLogLine());
                LoggedEmptyAiRefitSummaries.Add($"{summary.TurnIndex}:{summary.TurnLabel}:empty");
            }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"{AiLogPrefix}: summary flush failed for {LogToken(summary.TurnLabel)}. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void RecordAiRefitSummary(
        AiSmartRefitSummaryKind kind,
        string nation,
        string detail,
        long elapsedMs = 0L)
    {
        if (!ModSettings.SmartRefitsEnabled)
            return;

        int turnIndex = AiDesignCompetitiveness.CurrentTurnIndex();
        string turn = AiDesignCompetitiveness.CurrentTurnLabel();
        string key = $"{turnIndex}:{LogToken(turn)}";
        if (!PendingAiRefitTurnSummaries.TryGetValue(key, out AiSmartRefitTurnSummary? summary))
        {
            summary = new AiSmartRefitTurnSummary(turnIndex, turn);
            PendingAiRefitTurnSummaries[key] = summary;
        }

        summary.Add(kind, nation, detail, elapsedMs);
    }

    private static void LogAi(string message)
    {
        if (!VerboseAiRefitDiagnostics)
            return;

        string key = $"{AiDesignCompetitiveness.CurrentTurnIndex()}|{message}";
        if (!LoggedAiAttempts.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg($"{AiLogPrefix}: {message}");
    }

    private static long PlayerPointer(Player? player)
        => Safe(() => player?.Pointer.ToInt64() ?? 0L, 0L);

    private static long ShipPointer(Ship? ship)
        => Safe(() => ship?.Pointer.ToInt64() ?? 0L, 0L);

    private static void SafeDo(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private static AiRefitStateMachineFields ResolveAiRefitStateMachineFields(Type type)
    {
        if (AiRefitStateMachineFieldsByType.TryGetValue(type, out AiRefitStateMachineFields? cached))
            return cached;

        FieldInfo? player = FindField(type, field => field.FieldType == typeof(Player) && field.Name.Equals("player", StringComparison.Ordinal));
        player ??= FindField(type, field => field.FieldType == typeof(Player));
        FieldInfo? force = FindField(type, field => field.FieldType == typeof(bool) && field.Name.Equals("force", StringComparison.Ordinal));
        FieldInfo? forceSimple = FindField(type, field => field.FieldType == typeof(bool) && field.Name.Equals("forceSimple", StringComparison.Ordinal));
        FieldInfo? state = FindField(type, field => field.FieldType == typeof(int) && field.Name.Contains("__state", StringComparison.Ordinal));

        AiRefitStateMachineFields fields = new(player, force, forceSimple, state);
        AiRefitStateMachineFieldsByType[type] = fields;
        return fields;
    }

    private static FieldInfo? FindField(Type type, Func<FieldInfo, bool> predicate)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(predicate);

    private static bool ReadBoolField(FieldInfo? field, object target)
        => Safe(() => field?.GetValue(target) is bool value && value, false);

    private static void SetIntField(FieldInfo? field, object target, int value)
    {
        try
        {
            field?.SetValue(target, value);
        }
        catch
        {
            // The state write is defensive; returning false from MoveNext is
            // enough to end the current iterator call.
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

    private enum AiSmartRefitSummaryKind
    {
        Started,
        Continued,
        Rejected,
        Skipped,
        Protected,
        DeleteGuard
    }

    private sealed class AiSmartRefitTurnSummary
    {
        private readonly List<string> details = new();
        private readonly Dictionary<string, int> skipReasons = new(StringComparer.OrdinalIgnoreCase);
        private int hiddenDetails;

        internal AiSmartRefitTurnSummary(int turnIndex, string turnLabel)
        {
            TurnIndex = turnIndex;
            TurnLabel = turnLabel;
        }

        internal int TurnIndex { get; }
        internal string TurnLabel { get; }
        internal int Started { get; private set; }
        internal int Continued { get; private set; }
        internal int Rejected { get; private set; }
        internal int Skipped { get; private set; }
        internal int Protected { get; private set; }
        internal int DeleteGuards { get; private set; }
        internal long TotalMs { get; private set; }
        internal long MaxMs { get; private set; }
        internal int Entries => Started + Continued + Rejected + Skipped + Protected + DeleteGuards;

        internal void Add(AiSmartRefitSummaryKind kind, string nation, string detail, long elapsedMs)
        {
            switch (kind)
            {
                case AiSmartRefitSummaryKind.Started:
                    Started++;
                    break;
                case AiSmartRefitSummaryKind.Continued:
                    Continued++;
                    break;
                case AiSmartRefitSummaryKind.Rejected:
                    Rejected++;
                    break;
                case AiSmartRefitSummaryKind.Skipped:
                    Skipped++;
                    break;
                case AiSmartRefitSummaryKind.Protected:
                    Protected++;
                    break;
                case AiSmartRefitSummaryKind.DeleteGuard:
                    DeleteGuards++;
                    break;
            }

            if (elapsedMs > 0)
            {
                TotalMs += elapsedMs;
                MaxMs = Math.Max(MaxMs, elapsedMs);
            }

            if (kind == AiSmartRefitSummaryKind.Skipped)
            {
                string skipReason = ExtractSkipReason(detail);
                skipReasons.TryGetValue(skipReason, out int count);
                skipReasons[skipReason] = count + 1;
            }

            if (details.Count < MaxAiRefitSummaryDetails)
                details.Add(ReadableLogText(detail));
            else
                hiddenDetails++;
        }

        internal string ToLogLine()
        {
            if (Entries <= 0)
                return $"{AiLogPrefix} summary turn={LogToken(TurnLabel)} entries=0";

            string detailText = details.Count == 0 ? "none" : string.Join(" | ", details);
            if (hiddenDetails > 0)
                detailText += $" | +{hiddenDetails} more";

            string skipReasonText = skipReasons.Count == 0
                ? "none"
                : string.Join(",", skipReasons.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}:{pair.Value}"));

            return
                $"{AiLogPrefix} summary turn={LogToken(TurnLabel)} entries={Entries} started={Started} continued={Continued} rejected={Rejected} skipped={Skipped} protected={Protected} deleteGuards={DeleteGuards} skipReasons={skipReasonText} totalMs={TotalMs} maxMs={MaxMs} details={detailText}";
        }
    }

    private sealed class AiRefitStateMachineFields
    {
        internal AiRefitStateMachineFields(FieldInfo? player, FieldInfo? force, FieldInfo? forceSimple, FieldInfo? state)
        {
            Player = player;
            Force = force;
            ForceSimple = forceSimple;
            State = state;
        }

        internal FieldInfo? Player { get; }
        internal FieldInfo? Force { get; }
        internal FieldInfo? ForceSimple { get; }
        internal FieldInfo? State { get; }
    }

    private readonly record struct AiPlacementProbeMode(string Name, bool ForceConstructor, bool LoadVisualModels);

    private sealed class AiRefitContinuationResult
    {
        internal int Started { get; set; }
        internal HashSet<string> CoveredGroupKeys { get; } = new(StringComparer.Ordinal);
    }

    private sealed class AiRefitNameRepairEntry
    {
        internal AiRefitNameRepairEntry(
            Ship design,
            string type,
            string baseName,
            int year,
            int ordinal,
            string groupKey,
            string visibleNameKey,
            string id,
            int liveRefs,
            string liveRefExamples,
            int refitTurn)
        {
            Design = design;
            Type = type;
            BaseName = baseName;
            Year = year;
            Ordinal = ordinal;
            GroupKey = groupKey;
            VisibleNameKey = visibleNameKey;
            Id = id;
            LiveRefs = liveRefs;
            LiveRefExamples = liveRefExamples;
            RefitTurn = refitTurn;
        }

        internal Ship Design { get; }
        internal string Type { get; }
        internal string BaseName { get; }
        internal int Year { get; }
        internal int Ordinal { get; }
        internal string GroupKey { get; }
        internal string VisibleNameKey { get; }
        internal string Id { get; }
        internal int LiveRefs { get; }
        internal string LiveRefExamples { get; }
        internal int RefitTurn { get; }
    }

    private sealed class AiRefitGroup
    {
        internal AiRefitGroup(string key, List<Ship> ships)
        {
            Key = key;
            Ships = ships;
            Ship first = ships.First();
            Type = AiDesignCompetitiveness.NormalizeShipType(first.shipType);
            ClassName = BaseClassName(first);
            OldestAge = ships.Count == 0 ? 0f : ships.Max(AgeYears);
            TotalTonnage = ships.Sum(ship => Safe(() => ship.Tonnage(), 0f));
        }

        internal string Key { get; }
        internal string Type { get; }
        internal string ClassName { get; }
        internal List<Ship> Ships { get; }
        internal float OldestAge { get; }
        internal float TotalTonnage { get; }
    }
}

[HarmonyPatch]
internal static class CampaignSmartRefitAiRefitShipsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: CampaignController.AiRefitShips target not found; AI smart refit replacement disabled.");
        else
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP smart refits: attached CampaignController.AiRefitShips smart replacement.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "AiRefitShips", new[] { typeof(Player), typeof(bool), typeof(bool) });

    [HarmonyPrefix]
    private static bool Prefix(Player player, bool force, bool forceSimple, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!CampaignSmartRefitPatch.ShouldBlockAiRefits(player))
            return true;

        CampaignSmartRefitPatch.RunAiSmartRefits(player, force, forceSimple);
        __result = CampaignSmartRefitPatch.EmptyIl2CppCoroutine();
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignSmartRefitAiRefitShipsMoveNextPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: CampaignController.AiRefitShips MoveNext target not found; AI refit block disabled.");
        else
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP smart refits: attached CampaignController.AiRefitShips MoveNext fallback guard.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignSmartRefitPatch.AiRefitShipsMoveNextTarget();

    [HarmonyPrefix]
    private static bool Prefix(object __instance, ref bool __result)
        => CampaignSmartRefitPatch.BlockAiRefitMoveNext(__instance, ref __result);
}

[HarmonyPatch]
internal static class CampaignSmartRefitStartPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: PlayerController.RefitShipsStart target not found; defensive non-player refit block disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(
            typeof(PlayerController),
            nameof(PlayerController.RefitShipsStart),
            new[] { typeof(Il2CppSystem.Collections.Generic.List<Ship>), typeof(Ship), typeof(bool) });

    [HarmonyPrefix]
    private static bool Prefix(Il2CppSystem.Collections.Generic.List<Ship> shipList, Ship refitDesign, bool isPlayer)
    {
        if (!ModSettings.SmartRefitsEnabled || isPlayer)
            return true;

        if (CampaignSmartRefitPatch.IsApprovedSmartAiRefitDesign(refitDesign))
            return true;

        CampaignSmartRefitPatch.LogBlockedAiRefit(CampaignSmartRefitPatch.FirstShipPlayer(shipList), force: false, forceSimple: false, "RefitShipsStart");
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignSmartRefitCampaignDeleteDesignGuardPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: CampaignController.DeleteDesign target not found; live-referenced AI refit delete guard disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), nameof(CampaignController.DeleteDesign), new[] { typeof(Ship) });

    [HarmonyPrefix]
    private static bool Prefix(Ship ship)
        => CampaignSmartRefitPatch.DeleteDesignGuardPrefix(ship, "CampaignController.DeleteDesign");
}

[HarmonyPatch]
internal static class CampaignSmartRefitPlayerDeleteDesignGuardPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: PlayerController.DeleteDesign target not found; live-referenced AI refit delete guard disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.DeleteDesign), new[] { typeof(Ship) });

    [HarmonyPrefix]
    private static bool Prefix(Ship ship)
        => CampaignSmartRefitPatch.DeleteDesignGuardPrefix(ship, "PlayerController.DeleteDesign");
}

[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.OnLoadingScreenHide))]
internal static class CampaignSmartRefitLoadCleanupPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance)
        => CampaignSmartRefitPatch.CleanupAiRefitDesigns(__instance, "campaign load");
}

[HarmonyPatch(typeof(CampaignController), "OnNewTurn")]
internal static class CampaignSmartRefitNewTurnCleanupPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance)
        => CampaignSmartRefitPatch.CleanupAiRefitDesigns(__instance, "new turn");
}

[HarmonyPatch]
internal static class CampaignSmartRefitSummaryNextTurnCompletionPatch
{
    private static MethodBase? TargetMethod()
        => CampaignSharedDesignDiagnosticsPatch.NextTurnMoveNextTarget();

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits ai: NextTurn MoveNext target not found; summary flush will be skipped.");

        return available;
    }

    [HarmonyPostfix]
    private static void Postfix(bool __result)
    {
        if (!__result)
            CampaignSmartRefitPatch.FlushPendingAiRefitTurnSummariesAfterNextTurn();
    }
}

[HarmonyPatch]
internal static class CampaignSmartRefitCanBuildDesignAmountPatch
{
    private const string BlockReason = "uadvpSmartRefitsAiRefitDesign";

    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: PlayerController.CanBuildShipsFromDesign amount target not found; AI refit design buildability gate disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign), new[] { typeof(Ship), typeof(int), typeof(string).MakeByRefType() });

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(Ship design, ref string reason, ref bool __result)
    {
        if (!__result || !CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            return;

        __result = false;
        reason = BlockReason;
    }
}

[HarmonyPatch]
internal static class CampaignSmartRefitCanBuildDesignPatch
{
    private const string BlockReason = "uadvpSmartRefitsAiRefitDesign";

    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP smart refits: PlayerController.CanBuildShipsFromDesign target not found; AI refit design buildability gate disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign), new[] { typeof(Ship), typeof(string).MakeByRefType() });

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(Ship design, ref string reason, ref bool __result)
    {
        if (!__result || !CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            return;

        __result = false;
        reason = BlockReason;
    }
}
