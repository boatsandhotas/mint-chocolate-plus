using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.Harmony;

namespace UADVanillaPlus.Services;

// Replaces the expensive vanilla random AI new-design fallback with one
// deterministic VP attempt. This is deliberately strict: generated new designs
// must pass vanilla buildability without Smart Refit grandfathering waivers.
internal static class SmartAiDesignService
{
    private const string LogPrefix = "UADVP smart AI designs";
    private static bool VerboseDiagnostics => false;
    private const float Top3MinimumRatio = 0.9f;
    private const int MaxSmartAiDesignAttempts = 3;
    private const int MaxAcceptedSmartAiDesignKeys = 512;

    private static readonly FieldInfo? CurrentDesignsField =
        typeof(CampaignController).GetField("_currentDesigns", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly Dictionary<string, SmartAiDesignTurnSummary> PendingTurnSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedEmptyTurnSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, RandomShipStateFields> RandomShipStateFieldsByType = new();
    private static readonly HashSet<string> AcceptedSmartAiDesignKeys = new(StringComparer.Ordinal);
    private static readonly Queue<string> AcceptedSmartAiDesignKeyOrder = new();

    private static SmartAiRandomGenerationContext? activeRandomGeneration;
    private static bool loggedSmartRandomShipHookAttached;

    internal static SmartAiDesignAttemptResult TryReplaceRandomFallback(
        CampaignController controller,
        Player player,
        ShipType shipType,
        bool prewarming)
    {
        if (!ModSettings.SmartAiDesignsEnabled)
        {
            return SmartAiDesignAttemptResult.NotAttempted;
        }

        if (controller == null || player == null || shipType == null)
            return SmartAiDesignAttemptResult.Suppress("invalid-context");

        if (!Safe(() => player.isAi && !player.isMain, false))
            return SmartAiDesignAttemptResult.Suppress("not-ai-player");

        Stopwatch timer = Stopwatch.StartNew();
        int turnIndex = AiDesignCompetitiveness.CurrentTurnIndex();
        string turn = AiDesignCompetitiveness.CurrentTurnLabel();
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        string type = AiDesignCompetitiveness.NormalizeShipType(shipType);
        int year = RequestedYear(controller, prewarming);
        Ship? candidate = null;
        Ship.Store? sourceStore = null;
        string defaultsPre = "not-run";
        string defaultsPost = "not-run";
        string partsSummary = "not-run";
        string armorSummary = "not-run";
        string powerSummary = "not-run";
        string generatorSummary = "not-run";
        string rescueSummary = "not-run";
        string designBookSummary = "not-run";
        string hull = "?";
        int attemptsUsed = 0;
        int acceptedAttempt = 0;
        List<SmartAiInnerAttemptResult> attemptResults = new();

        try
        {
            DesignBookLookup designBook = CurrentDesignBook(controller, prewarming);
            designBookSummary = designBook.Detail;
            CampaignDesigns? campaignDesigns = designBook.Book;
            if (campaignDesigns == null)
                return Finish(false, "source", "design-book-unavailable:" + designBook.Detail);

            sourceStore = Safe(() => campaignDesigns?.RandomShip(player, shipType, year), null);
            hull = StoreHull(sourceStore);
            if (sourceStore == null)
                return Finish(false, "source", "no-campaign-design-store:" + designBook.Detail);

            candidate = Ship.Create(null, null, false, false, false);
            if (candidate == null)
                return Finish(false, "create", "ship-create-null");

            Il2CppSystem.Nullable<Il2CppSystem.Guid> manualId = NewManualShipId();
            if (!Safe(() => candidate.FromStore(sourceStore, manualId, null, null, false), false))
                return Finish(false, "fromStore", "failed");

            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(candidate, "smart-ai", "after-fromStore");
            EnsureSmartAiDesignIdentity(candidate, sourceStore, "after-from-store");
            ClearSmartAiSharedMarkers(candidate, "after-from-store");
            InitializeLikeVanillaRandomDesign(controller, candidate);
            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(candidate, "smart-ai", "after-initialize");

            for (int attempt = 1; attempt <= MaxSmartAiDesignAttempts; attempt++)
            {
                SmartAiInnerAttemptResult result = RunInnerDesignAttempt(attempt);
                attemptResults.Add(result);
                attemptsUsed = attempt;
                defaultsPre = result.DefaultsPre;
                defaultsPost = result.DefaultsPost;
                partsSummary = result.PartsSummary;
                generatorSummary = result.GeneratorSummary;
                rescueSummary = result.RescueSummary;
                armorSummary = result.ArmorSummary;
                powerSummary = result.PowerSummary;

                if (result.Accepted)
                {
                    acceptedAttempt = attempt;
                    break;
                }
            }

            if (acceptedAttempt <= 0)
            {
                SmartAiInnerAttemptResult lastAttempt = attemptResults.Count == 0
                    ? SmartAiInnerAttemptResult.Empty
                    : attemptResults[^1];
                return Finish(false, lastAttempt.Stage, lastAttempt.Reason);
            }

            EnsureSmartAiDesignIdentity(candidate, sourceStore, "before-accept");
            ClearSmartAiSharedMarkers(candidate, "before-accept");
            RegisterAcceptedSmartAiDesign(candidate);
            FinalizeAcceptedDesign(candidate, controller, prewarming);
            return Finish(true, "accepted", "strict-valid");
        }
        catch (Exception ex)
        {
            return Finish(false, "exception", ex.GetType().Name);
        }

        SmartAiDesignAttemptResult Finish(bool accepted, string stage, string reason)
        {
            const bool suppressVanillaFallback = true;
            timer.Stop();
            string candidateId = candidate == null ? "none" : Safe(() => candidate.id.ToString(), "?");
            string sourceStoreId = Safe(() => sourceStore?.id.ToString() ?? "none", "?");
            string design = candidate == null ? StoreDesign(sourceStore) : AiDesignCompetitiveness.ShipLabel(candidate);
            string acceptedAttemptText = acceptedAttempt > 0 ? acceptedAttempt.ToString(CultureInfo.InvariantCulture) : "none";
            string attemptDetails = FormatInnerAttemptDetails(attemptResults, MaxSmartAiDesignAttempts);
            string weightCapacity = WeightCapacitySummary(candidate);
            HullSummary hullSummary = ResolveHullSummary(candidate, sourceStore, hull);

            if (VerboseDiagnostics)
            {
                string status = accepted ? "accepted" : "rejected";
                string serializedTonnage = SerializedTonnageSummary(candidate);
                string message =
                    $"attempt turn={LogToken(turn)} nation={LogToken(nation)} type={type} year={year} " +
                    $"prewarming={BoolText(prewarming)} hull={LogToken(hullSummary.Id)} hullUi={LogToken(hullSummary.Display)} design={LogToken(design)} id={LogToken(candidateId)} sourceStoreId={LogToken(sourceStoreId)} result={status} " +
                    $"stage={LogToken(stage)} reason={LogToken(reason)} attemptsUsed={attemptsUsed} maxAttempts={MaxSmartAiDesignAttempts} acceptedAttempt={LogToken(acceptedAttemptText)} " +
                    $"priorFailures={LogToken(FormatPriorFailures(attemptResults))} weight={LogToken(weightCapacity)} elapsedMs={timer.ElapsedMilliseconds} suppressVanillaFallback={BoolText(suppressVanillaFallback)} " +
                    $"designBook={LogToken(designBookSummary)} serialized={LogToken(serializedTonnage)} defaultsPre={LogToken(defaultsPre)} parts={LogToken(partsSummary)} defaultsPost={LogToken(defaultsPost)} " +
                    $"generator={LogToken(generatorSummary)} rescue={LogToken(rescueSummary)} armor={LogToken(armorSummary)} power={LogToken(powerSummary)} attemptDetails={LogToken(attemptDetails)}";
                Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: {message}.");
            }

            RecordTurnSummary(
                turnIndex,
                turn,
                nation,
                prewarming,
                type,
                accepted,
                stage,
                reason,
                design,
                hullSummary,
                candidate,
                attemptsUsed,
                acceptedAttempt,
                attemptResults,
                timer.ElapsedMilliseconds);

            if (!accepted)
                CleanupRejectedCandidate(player, candidate);

            return new SmartAiDesignAttemptResult(true, accepted, suppressVanillaFallback, stage, reason);
        }

        SmartAiInnerAttemptResult RunInnerDesignAttempt(int attempt)
        {
            Stopwatch attemptTimer = Stopwatch.StartNew();
            string attemptDefaultsPre = "not-run";
            string attemptDefaultsPost = "not-run";
            string attemptPartsSummary = "not-run";
            string attemptGeneratorSummary = "not-run";
            string attemptRescueSummary = "not-run";
            string attemptArmorSummary = "not-run";
            string attemptPowerSummary = "not-run";

            SmartAiInnerAttemptResult Done(bool accepted, string stage, string reason)
            {
                attemptTimer.Stop();
                return new SmartAiInnerAttemptResult(
                    attempt,
                    accepted,
                    stage,
                    reason,
                    attemptDefaultsPre,
                    attemptDefaultsPost,
                    attemptPartsSummary,
                    attemptGeneratorSummary,
                    attemptRescueSummary,
                    attemptArmorSummary,
                    attemptPowerSummary,
                    attemptTimer.ElapsedMilliseconds);
            }

            if (candidate == null)
                return Done(false, "candidate", "missing");

            Ship current = candidate;
            RandomGenerationResult generation = RunVanillaRandomGeneratorForSmartAi(current);
            attemptGeneratorSummary = generation.Summary;
            attemptPartsSummary = generation.Summary;
            attemptDefaultsPre = generation.DefaultsPre;
            attemptDefaultsPost = generation.DefaultsPost;
            if (!generation.Success)
                return Done(false, generation.Stage, generation.Reason);

            StrictValidationResult postParts = ValidateRequiredPartsOnly(current, "post-parts");
            if (!postParts.Valid)
                return Done(false, postParts.Stage, postParts.Reason);

            SmallShipRescueResult rescue = TryRunSmallShipProtectionRescue(current, type);
            attemptRescueSummary = rescue.Summary;
            if (!rescue.Valid)
                return Done(false, rescue.Stage, rescue.Reason);

            if (!DesignAutoArmorPatch.TryApplySmartRefitArmor(current, out attemptArmorSummary))
                return Done(false, "armor", attemptArmorSummary);
            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(current, "smart-ai", "after-armor-attempt-" + attempt);

            StrictValidationResult postArmor = ValidateStrictNewDesign(current, "post-armor");
            if (!postArmor.Valid)
                return Done(false, postArmor.Stage, postArmor.Reason);

            EnsureSmartAiDesignIdentity(current, sourceStore, "before-top3");
            ClearSmartAiSharedMarkers(current, "before-top3");
            Top3GateResult top3 = EvaluateTop3Gate(player, current);
            attemptPowerSummary = top3.Detail;
            if (!top3.Accept)
                return Done(false, "top3", top3.Detail);

            return Done(true, "accepted", "strict-valid");
        }
    }

    internal static bool IsAcceptedSmartAiDesign(Ship? ship)
    {
        if (ship == null || AcceptedSmartAiDesignKeys.Count == 0)
            return false;

        foreach (string key in SmartAiDesignKeys(ship))
        {
            if (AcceptedSmartAiDesignKeys.Contains(key))
                return true;
        }

        return false;
    }

    internal static void FlushPendingTurnSummariesAfterNextTurn()
    {
        if (!ModSettings.SmartAiDesignsEnabled && PendingTurnSummaries.Count == 0)
            return;

        List<SmartAiDesignTurnSummary> summaries = PendingTurnSummaries.Values
            .OrderBy(summary => summary.TurnIndex)
            .ThenBy(summary => summary.TurnLabel, StringComparer.Ordinal)
            .ThenBy(summary => summary.Prewarming)
            .ToList();
        PendingTurnSummaries.Clear();

        if (summaries.Count == 0)
        {
            string turn = AiDesignCompetitiveness.CurrentTurnLabel();
            string key = $"{AiDesignCompetitiveness.CurrentTurnIndex()}:{turn}:empty";
            if (LoggedEmptyTurnSummaries.Add(key))
                Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} summary turn={LogToken(turn)} entries=0");
            return;
        }

        foreach (SmartAiDesignTurnSummary summary in summaries)
        {
            try
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(summary.ToLogLine(LogPrefix));
                LoggedEmptyTurnSummaries.Add($"{summary.TurnIndex}:{summary.TurnLabel}:empty");
            }
            catch (Exception ex)
            {
                WarnOnce(
                    "turn-summary:" + ex.GetType().Name,
                    $"{LogPrefix}: turn summary flush failed for {LogToken(summary.TurnLabel)}. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void InitializeLikeVanillaRandomDesign(CampaignController controller, Ship candidate)
    {
        try { candidate.dateCreated = controller.CurrentDate; }
        catch { }
        try { candidate.dateFinished = controller.CurrentDate; }
        catch { }
        try { candidate.status = VesselEntity.Status.Normal; }
        catch { }
        try { candidate.SetRandomName(); }
        catch { }
        try { candidate.CalcCitadelValues(); }
        catch { }
        try { candidate.CalcWeightAndCost(true, true); }
        catch { }
    }

    private static RandomGenerationResult RunVanillaRandomGeneratorForSmartAi(Ship candidate)
    {
        bool enteredConstructorEver = false;
        bool constructorOpen = false;
        bool leftConstructor = false;
        bool callbackInvoked = false;
        bool callbackResult = false;
        int callbackTries = 0;
        float callbackBuildTime = 0f;
        int steps = 0;
        string changeHull = "not-run";
        string routineKind = "not-run";
        var context = new SmartAiRandomGenerationContext(candidate);

        RandomGenerationResult Fail(
            string stage,
            string reason,
            bool allowVanillaFallback = true)
        {
            string summary = BuildRandomGenerationSummary(
                context,
                changeHull,
                enteredConstructorEver,
                constructorOpen,
                leftConstructor,
                routineKind,
                steps,
                callbackInvoked,
                callbackResult,
                callbackTries,
                callbackBuildTime,
                reason);
            WarnOnce(
                "smart-ai-generator:" + stage + ":" + reason,
                $"{LogPrefix}: generator failed stage={LogToken(stage)} reason={LogToken(reason)} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))} summary={LogToken(summary)} allowVanillaFallback={BoolText(allowVanillaFallback)}.");
            return new RandomGenerationResult(
                false,
                stage,
                reason,
                summary,
                context.DefaultsPre,
                context.DefaultsPost,
                allowVanillaFallback);
        }

        try
        {
            PartData? hullData = Safe(() => candidate.hull?.data, null);
            if (hullData == null)
            {
                changeHull = "no-hull-data";
            }
            else
            {
                candidate.ChangeHull(hullData, null, candidate.shipType, true);
                changeHull = "ok:" + LogToken(SafeString(() => hullData.name));
                if (VerboseDiagnostics)
                    DesignAutoDesignLitePatch.LogDiagnosticSnapshot(candidate, "smart-ai", "after-changeHull");
            }
        }
        catch (Exception ex)
        {
            changeHull = "failed:" + ExceptionChainToken(ex);
            return Fail("generator-setup", "changeHull:" + ExceptionChainToken(ex));
        }

        try
        {
            candidate.EnterConstructor();
            enteredConstructorEver = true;
            constructorOpen = true;
        }
        catch (Exception ex)
        {
            return Fail("generator-setup", "enterConstructor:" + ExceptionChainToken(ex));
        }

        try
        {
            activeRandomGeneration = context;
            System.Action<bool, int, float> onDone = (result, tries, buildTime) =>
            {
                callbackInvoked = true;
                callbackResult = result;
                callbackTries = tries;
                callbackBuildTime = buildTime;
                context.CallbackInvoked = true;
                context.CallbackResult = result;
                context.CallbackTries = tries;
                context.CallbackBuildTime = buildTime;
            };

            var info = new Il2CppSystem.Text.StringBuilder();
            Il2CppSystem.Collections.IEnumerator routine;
            try
            {
                routine = candidate.GenerateRandomShip(
                    onDone,
                    true,
                    new Il2CppSystem.Nullable<bool>(),
                    true,
                    true,
                    true,
                    true,
                    true,
                    new(),
                    new(),
                    new(),
                    new(),
                    new(),
                    new(),
                    new(),
                    new(),
                    false,
                    false,
                    true,
                    true,
                    info);
            }
            catch (Exception ex)
            {
                return Fail("generator-start", "invoke:" + ExceptionChainToken(ex));
            }

            context.Started = true;
            routineKind = "il2cpp";
            const int maxSteps = 20000;
            while (true)
            {
                bool next;
                try { next = routine.MoveNext(); }
                catch (Exception ex)
                {
                    context.MoveNextFailure = ExceptionChainToken(ex);
                    return Fail("generator", "moveNext:" + ExceptionChainToken(ex));
                }

                if (!next)
                    break;

                steps++;
                if (steps >= maxSteps)
                    return Fail("generator", "step-cap:" + steps);

                if (context.SmartPartsSucceeded)
                {
                    context.StoppedAfterSmartParts = true;
                    break;
                }
            }

            context.Completed = true;
            if (context.StoppedAfterSmartParts)
            {
                context.Completed = false;
                try
                {
                    GameManager.EndAutodesign();
                    context.EndAutodesignSummary = "ok";
                }
                catch (Exception ex)
                {
                    context.EndAutodesignSummary = "failed:" + ExceptionChainToken(ex);
                    return Fail("generator-cleanup", "endAutodesign:" + ExceptionChainToken(ex), allowVanillaFallback: true);
                }
            }

            if (string.Equals(context.DefaultsPre, "not-run", StringComparison.Ordinal))
                context.DefaultsPre = "not-applied:no-parts-state";

            try
            {
                context.DefaultsPost = DesignNewHullDefaultsPatch.ApplySmartAiPartDependentDefaults(candidate, traceWeight: VerboseDiagnostics);
            }
            catch (Exception ex)
            {
                context.DefaultsPost = "failed:" + ExceptionChainToken(ex);
                return Fail("defaultsPost", context.DefaultsPost, allowVanillaFallback: false);
            }

            try { candidate.CalcWeightAndCost(true, true); }
            catch (Exception ex)
            {
                return Fail("generator", "recalculate:" + ExceptionChainToken(ex), allowVanillaFallback: false);
            }
            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(candidate, "smart-ai", "after-defaultsPost-before-armor");

            try
            {
                candidate.LeaveConstructor();
                leftConstructor = true;
                constructorOpen = false;
            }
            catch (Exception ex)
            {
                context.LeaveConstructorFailure = ExceptionChainToken(ex);
                return Fail("generator-cleanup", "leaveConstructor:" + ExceptionChainToken(ex), allowVanillaFallback: true);
            }

            int parts = Safe(() => candidate.parts?.Count ?? 0, 0);
            string infoText = LogToken(SafeString(() => info.ToString()));
            string summary = BuildRandomGenerationSummary(
                context,
                changeHull,
                enteredConstructorEver,
                constructorOpen,
                leftConstructor,
                routineKind,
                steps,
                callbackInvoked,
                callbackResult,
                callbackTries,
                callbackBuildTime,
                $"parts={parts}_info={infoText}");

            if (parts <= 0)
                return new RandomGenerationResult(false, "generator", "no-parts:" + summary, summary, context.DefaultsPre, context.DefaultsPost, true);

            if (callbackInvoked && !callbackResult && !context.SmartPartsSucceeded)
                return new RandomGenerationResult(false, "generator", "callback-false:" + summary, summary, context.DefaultsPre, context.DefaultsPost, true);

            return new RandomGenerationResult(true, "generator", "ok", summary, context.DefaultsPre, context.DefaultsPost, false);
        }
        finally
        {
            if (ReferenceEquals(activeRandomGeneration, context))
                activeRandomGeneration = null;

            if (constructorOpen)
            {
                try
                {
                    candidate.LeaveConstructor();
                    leftConstructor = true;
                }
                catch (Exception ex)
                {
                    context.LeaveConstructorFailure = ExceptionChainToken(ex);
                }
            }
        }
    }

    internal static void LogSmartRandomShipHookAttached()
    {
        if (loggedSmartRandomShipHookAttached)
            return;

        loggedSmartRandomShipHookAttached = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: attached GenerateRandomShip smart generator hook.");
    }

    internal static void SmartAiRandomShipMoveNextPrefix(object? stateMachine)
    {
        SmartAiRandomGenerationContext? context = TryGetActiveRandomContext(stateMachine, out RandomShipStateFields fields, out int? state, "prefix");
        if (context == null || stateMachine == null)
            return;

        context.MoveNextCalls++;
        if (state.HasValue)
            context.StatesSeen.Add(state.Value);

        try
        {
            object? beforeUseSmall = fields.UseSmallAmountTries?.GetValue(stateMachine);
            object? beforeTries = fields.TriesTotal?.GetValue(stateMachine);
            if (beforeUseSmall is bool useSmall && !useSmall)
                fields.UseSmallAmountTries!.SetValue(stateMachine, true);
            if (beforeTries is int triesTotal && triesTotal > 1)
                fields.TriesTotal!.SetValue(stateMachine, 1);
        }
        catch (Exception ex)
        {
            context.CapFailure = ExceptionChainToken(ex);
        }

        if (state is int stateValue && stateValue is >= 2 and <= 4)
        {
            if (!context.EarlyDefaultsApplied)
                ApplySmartAiEarlyDefaults(context, "state-" + stateValue);

            if (fields.State != null)
            {
                try
                {
                    fields.State.SetValue(stateMachine, 7);
                    context.SkippedVanillaSetupStates.Add(stateValue + "->7");
                }
                catch (Exception ex)
                {
                    context.EarlyStateSkipFailure = ExceptionChainToken(ex);
                }
            }

            return;
        }

        if (state is int stateValueAfterSetup && stateValueAfterSetup >= 8)
        {
            if (!context.PreDefaultsApplied)
                ApplySmartAiPrePartDefaults(context, "state-" + stateValueAfterSetup);

            if (stateValueAfterSetup == 8 && !context.SmartPartsAttempted)
            {
                RunSmartAiPartsAtGeneratorState(context);
                if (context.SmartPartsSucceeded && fields.State != null)
                {
                    try
                    {
                        fields.State.SetValue(stateMachine, 9);
                        context.SkippedVanillaPartsState = true;
                        context.StateSkip = "8->9";
                    }
                    catch (Exception ex)
                    {
                        context.StateSkip = "failed:" + ExceptionChainToken(ex);
                    }
                }
            }
        }
    }

    internal static void SmartAiRandomShipMoveNextPostfix(object? stateMachine, bool moveNextResult)
    {
        SmartAiRandomGenerationContext? context = TryGetActiveRandomContext(stateMachine, out _, out int? state, "postfix");
        if (context == null)
            return;

        if (state.HasValue)
            context.StatesSeen.Add(state.Value);
        if (!moveNextResult)
            context.Completed = true;
    }

    internal static void SmartAiRandomShipMoveNextFinalizer(object? stateMachine, Exception? exception)
    {
        if (exception == null)
            return;

        SmartAiRandomGenerationContext? context = TryGetActiveRandomContext(stateMachine, out _, out _, "finalizer");
        if (context == null)
            return;

        context.MoveNextFailure = ExceptionChainToken(exception);
    }

    private static void ApplySmartAiPrePartDefaults(SmartAiRandomGenerationContext context, string phase)
    {
        if (context.PreDefaultsApplied)
            return;

        context.PreDefaultsApplied = true;
        try
        {
            context.DefaultsPre = DesignNewHullDefaultsPatch.ApplySmartAiDesignDefaults(
                context.Candidate,
                includePartDependentDefaults: false) + "_phase=" + phase;
            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(context.Candidate, "smart-ai", "after-pre-part-defaults-" + phase);
        }
        catch (Exception ex)
        {
            context.DefaultsPre = "failed:" + ExceptionChainToken(ex) + "_phase=" + phase;
        }
    }

    private static void ApplySmartAiEarlyDefaults(SmartAiRandomGenerationContext context, string phase)
    {
        if (context.EarlyDefaultsApplied)
            return;

        context.EarlyDefaultsApplied = true;
        float beforeTonnage = Safe(() => context.Candidate.Tonnage(), 0f);
        try
        {
            string summary = DesignNewHullDefaultsPatch.ApplySmartAiDesignDefaults(
                context.Candidate,
                includePartDependentDefaults: false);
            if (VerboseDiagnostics)
                DesignAutoDesignLitePatch.LogDiagnosticSnapshot(context.Candidate, "smart-ai", "after-early-defaults-" + phase);
            float afterTonnage = Safe(() => context.Candidate.Tonnage(), 0f);
            context.EarlyDefaultsSummary =
                $"{summary}_phase={phase}_tons={beforeTonnage.ToString("0.##", CultureInfo.InvariantCulture)}->{afterTonnage.ToString("0.##", CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            context.EarlyDefaultsSummary = "failed:" + ExceptionChainToken(ex) + "_phase=" + phase;
        }
    }

    private static void RunSmartAiPartsAtGeneratorState(SmartAiRandomGenerationContext context)
    {
        context.SmartPartsAttempted = true;
        int beforeParts = Safe(() => context.Candidate.parts?.Count ?? 0, 0);
        try
        {
            bool success = DesignAutoDesignLitePatch.TryRunPartsOnlyForAi(context.Candidate, out string summary, VerboseDiagnostics);
            int afterParts = Safe(() => context.Candidate.parts?.Count ?? 0, 0);
            context.SmartPartsSummary = $"success={BoolText(success)} parts={beforeParts}->{afterParts} summary={LogToken(summary)}";
            context.SmartPartsSucceeded = success && afterParts > 0;
        }
        catch (Exception ex)
        {
            int afterParts = Safe(() => context.Candidate.parts?.Count ?? 0, 0);
            context.SmartPartsSummary = $"exception={ExceptionChainToken(ex)} parts={beforeParts}->{afterParts}";
            context.SmartPartsSucceeded = false;
        }
    }

    private static SmartAiRandomGenerationContext? TryGetActiveRandomContext(
        object? stateMachine,
        out RandomShipStateFields fields,
        out int? state,
        string phase)
    {
        fields = default;
        state = null;
        SmartAiRandomGenerationContext? context = activeRandomGeneration;
        if (context == null || stateMachine == null)
            return null;

        Type stateMachineType = stateMachine.GetType();
        try
        {
            fields = ResolveRandomShipStateFields(stateMachineType);
            object? rawState = fields.State?.GetValue(stateMachine);
            if (rawState is int stateValue)
                state = stateValue;

            context.StateMachineKey = ObjectKey(stateMachine);

            if (fields.Ship == null)
            {
                context.RecordFallbackClaim(stateMachineType, phase, "no-ship-field", state, fields);
                return context;
            }

            Ship? ship;
            try
            {
                ship = fields.Ship.GetValue(stateMachine) as Ship;
            }
            catch (Exception ex)
            {
                context.RecordFallbackClaim(stateMachineType, phase, "ship-read:" + ExceptionChainToken(ex), state, fields);
                return context;
            }

            if (ship == null)
            {
                context.RecordUnmatched(stateMachineType, phase, "ship-null", state, fields, null);
                return null;
            }

            if (!SameShipReference(ship, context.Candidate))
            {
                context.RecordUnmatched(stateMachineType, phase, "pointer-mismatch", state, fields, ship);
                return null;
            }

            return context;
        }
        catch (Exception ex)
        {
            context.StateReadFailure = ExceptionChainToken(ex);
            context.RecordUnmatched(stateMachineType, phase, "exception:" + context.StateReadFailure, state, fields, null);
            return null;
        }
    }

    private static RandomShipStateFields ResolveRandomShipStateFields(Type type)
    {
        if (RandomShipStateFieldsByType.TryGetValue(type, out RandomShipStateFields cached))
            return cached;

        RandomShipStateFields fields = new(
            ResolveMember(type, "<>1__state", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "__1__state", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "1__state", memberType => memberType == typeof(int)),
            ResolveMember(type, "<>4__this", memberType => typeof(Ship).IsAssignableFrom(memberType)) ??
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => typeof(Ship).IsAssignableFrom(field.FieldType))
                    .Select(field => new MemberAccessor(field, null))
                    .FirstOrDefault(),
            ResolveMember(type, "useSmallAmountTries", memberType => memberType == typeof(bool)),
            ResolveMember(type, "<triesTotal>5__4", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "triesTotal", memberType => memberType == typeof(int)),
            ResolveMember(type, "<tryN>5__5", memberType => memberType == typeof(int)) ??
                ResolveMember(type, "tryN", memberType => memberType == typeof(int)));

        RandomShipStateFieldsByType[type] = fields;
        return fields;
    }

    private static MemberAccessor? ResolveMember(Type type, string name, Func<Type, bool> typePredicate)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && typePredicate(field.FieldType))
            return new MemberAccessor(field, null);

        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetMethod != null && typePredicate(property.PropertyType))
            return new MemberAccessor(null, property);

        field = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && typePredicate(candidate.FieldType));
        if (field != null)
            return new MemberAccessor(field, null);

        property = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.GetMethod != null && candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && typePredicate(candidate.PropertyType));
        return property == null ? null : new MemberAccessor(null, property);
    }

    private static string BuildRandomGenerationSummary(
        SmartAiRandomGenerationContext context,
        string changeHull,
        bool enteredConstructorEver,
        bool constructorOpen,
        bool leftConstructor,
        string routineKind,
        int steps,
        bool callbackInvoked,
        bool callbackResult,
        int callbackTries,
        float callbackBuildTime,
        string detail)
    {
        string states = context.StatesSeen.Count == 0
            ? "none"
            : string.Join("/", context.StatesSeen.OrderBy(static value => value));
        string failures =
            $"cap={LogToken(context.CapFailure)} earlySkipFailure={LogToken(context.EarlyStateSkipFailure)} stateRead={LogToken(context.StateReadFailure)} moveNext={LogToken(context.MoveNextFailure)} leave={LogToken(context.LeaveConstructorFailure)}";
        string unmatched = context.UnmatchedExamples.Count == 0 ? "none" : string.Join("|", context.UnmatchedExamples.Select(LogToken));
        string fallbackClaims = context.FallbackClaimExamples.Count == 0 ? "none" : string.Join("|", context.FallbackClaimExamples.Select(LogToken));
        string skippedSetup = context.SkippedVanillaSetupStates.Count == 0 ? "none" : string.Join("/", context.SkippedVanillaSetupStates);
        return
            $"changeHull={LogToken(changeHull)} enteredConstructorEver={BoolText(enteredConstructorEver)} constructorOpen={BoolText(constructorOpen)} leftConstructor={BoolText(leftConstructor)} " +
            $"started={BoolText(context.Started)} completed={BoolText(context.Completed)} stoppedAfterSmartParts={BoolText(context.StoppedAfterSmartParts)} endAutodesign={LogToken(context.EndAutodesignSummary)} routine={LogToken(routineKind)} steps={steps} moveNextCalls={context.MoveNextCalls} states={LogToken(states)} " +
            $"earlyDefaults={LogToken(context.EarlyDefaultsSummary)} skippedVanillaSetupStates={LogToken(skippedSetup)} " +
            $"smartParts={LogToken(context.SmartPartsSummary)} smartPartsOk={BoolText(context.SmartPartsSucceeded)} skipVanillaParts={BoolText(context.SkippedVanillaPartsState)} stateSkip={LogToken(context.StateSkip)} " +
            $"callback={BoolText(callbackInvoked)}:{BoolText(callbackResult)} tries={callbackTries} buildTime={callbackBuildTime.ToString("0.###", CultureInfo.InvariantCulture)} " +
            $"stateMachine={LogToken(context.StateMachineKey)} unmatched={LogToken(unmatched)} fallbackClaims={LogToken(fallbackClaims)} {failures} detail={LogToken(detail)}";
    }

    private static bool SameShipReference(Ship? left, Ship? right)
    {
        if (left == null || right == null)
            return false;

        if (ReferenceEquals(left, right))
            return true;

        long leftPointer = Safe(() => left.Pointer.ToInt64(), 0L);
        long rightPointer = Safe(() => right.Pointer.ToInt64(), 0L);
        return leftPointer != 0L && leftPointer == rightPointer;
    }

    private static StrictValidationResult ValidateRequiredPartsOnly(Ship ship, string stage)
    {
        try { ship.CalcWeightAndCost(true, true); }
        catch (Exception ex) { return StrictValidationResult.Reject(stage, "calc:" + ex.GetType().Name); }

        try
        {
            bool reqValid = ship.IsValidCostReqParts(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo> notPassed,
                out Il2CppSystem.Collections.Generic.Dictionary<Part, string> badParts);
            if (!reqValid || HasItems(notPassed) || HasItems(badParts))
            {
                return StrictValidationResult.Reject(
                    stage,
                    "reqParts:false reason=" + NormalizeReason(reason) +
                    " notPassed=" + FormatReqInfoFailures(ship, notPassed) +
                    " badParts=" + FormatBadPartFailures(badParts));
            }
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "reqParts:" + ex.GetType().Name);
        }

        return StrictValidationResult.Pass(stage);
    }

    private static SmallShipRescueResult TryRunSmallShipProtectionRescue(Ship ship, string normalizedType)
    {
        if (!IsSmallShipProtectionRescueType(normalizedType))
            return SmallShipRescueResult.Pass("post-parts-rescue", "skipped:type");

        WeightCheckResult initial = CheckCostWeightBarbette(ship);
        if (initial.CheckFailed)
            return SmallShipRescueResult.Reject("post-parts-rescue", initial.Reason, "check-failed:" + initial.Reason);
        if (initial.Valid)
            return SmallShipRescueResult.Pass("post-parts-rescue", "not-needed");
        if (!IsWeightReason(initial.Reason))
        {
            string reason = CostWeightBarbetteReason(initial);
            return SmallShipRescueResult.Reject(
                "post-parts-weight",
                reason,
                $"not-fixable type={normalizedType} initial={FmtTons(initial.Weight)}/{FmtTons(initial.Capacity)} reason={initial.Reason}");
        }

        List<string> steps = new();
        WeightCheckResult current = initial;

        SmallShipRescueResult? result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "barbette:none",
            () => UninstallComponentFamilyStep(ship, "barbette", "barbette:none"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "antiFlood:none",
            () => UninstallComponentFamilyStep(ship, "antiflooding", "antiFlood:none"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "bulkheadComponent:standard",
            () => InstallComponentByNameStep(ship, "buklheads_0", "bulkheadComponent:standard"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "hullBottom:single",
            () => InstallComponentByNameStep(ship, "multi_bottom_0", "hullBottom:single"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "survivability:medium",
            () => SetSurvivabilityStep(ship, Ship.Survivability.Medium, "survivability:medium"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        result = RunSmallShipRescueStep(
            ship,
            normalizedType,
            "survivability:veryLow",
            () => SetSurvivabilityStep(ship, Ship.Survivability.VeryLow, "survivability:veryLow"),
            initial,
            steps,
            ref current);
        if (result.HasValue)
            return result.Value;

        string summary = SmallShipRescueSummary(normalizedType, initial, current, steps, "failed", current.Reason);
        return SmallShipRescueResult.Reject(
            "post-parts-rescue",
            "smallShipRescueFailed " + CostWeightBarbetteReason(current) + " rescue=" + summary,
            summary);
    }

    private static SmallShipRescueResult? RunSmallShipRescueStep(
        Ship ship,
        string normalizedType,
        string fallbackLabel,
        Func<string> action,
        WeightCheckResult initial,
        List<string> steps,
        ref WeightCheckResult current)
    {
        float beforeWeight = current.Weight;
        string label;
        try
        {
            label = action();
        }
        catch (Exception ex)
        {
            label = fallbackLabel + ":exception:" + ex.GetType().Name;
        }

        WeightCheckResult after = CheckCostWeightBarbette(ship);
        float delta = after.Weight - beforeWeight;
        steps.Add(label + ":" + FmtDeltaTons(delta));

        if (after.CheckFailed)
        {
            string summary = SmallShipRescueSummary(normalizedType, initial, after, steps, "failed", after.Reason);
            return SmallShipRescueResult.Reject("post-parts-rescue", after.Reason, summary);
        }

        if (after.Valid)
        {
            string summary = SmallShipRescueSummary(normalizedType, initial, after, steps, "valid", null);
            return SmallShipRescueResult.Pass("post-parts-rescue", summary);
        }

        if (!IsWeightReason(after.Reason))
        {
            string summary = SmallShipRescueSummary(normalizedType, initial, after, steps, "failed", after.Reason);
            return SmallShipRescueResult.Reject("post-parts-weight", CostWeightBarbetteReason(after), summary);
        }

        current = after;
        return null;
    }

    private static WeightCheckResult CheckCostWeightBarbette(Ship ship)
    {
        try { ship.CalcWeightAndCost(true, true); }
        catch (Exception ex)
        {
            return new(false, "check:" + ex.GetType().Name, "none", ShipWeight(ship), ShipCapacity(ship), true);
        }

        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);
            return new(
                valid,
                valid ? "valid" : NormalizeReason(reason),
                FormatPartList(errorBarbettePart),
                ShipWeight(ship),
                ShipCapacity(ship),
                false);
        }
        catch (Exception ex)
        {
            return new(false, "check:" + ex.GetType().Name, "none", ShipWeight(ship), ShipCapacity(ship), true);
        }
    }

    private static string UninstallComponentFamilyStep(Ship ship, string family, string labelPrefix)
    {
        TryUninstallComponentFamily(ship, family, labelPrefix, out string label);
        return label;
    }

    private static string InstallComponentByNameStep(Ship ship, string componentName, string labelPrefix)
    {
        TryInstallComponentByName(ship, componentName, labelPrefix, out string label);
        return label;
    }

    private static string SetSurvivabilityStep(Ship ship, Ship.Survivability target, string labelPrefix)
    {
        TrySetSurvivability(ship, target, labelPrefix, out string label);
        return label;
    }

    private static bool TryUninstallComponentFamily(Ship ship, string family, string labelPrefix, out string label)
    {
        label = labelPrefix + ":missing";
        try
        {
            var components = ship.components;
            if (components == null)
            {
                label = labelPrefix + ":no-components";
                return false;
            }

            foreach (var pair in components)
            {
                ComponentData? component = pair.Value;
                if (!ComponentMatchesFamily(component, family))
                    continue;

                string current = ComponentLabel(component);
                ship.UninstallComponent(pair.Key);
                label = labelPrefix + ":" + current;
                return true;
            }
        }
        catch (Exception ex)
        {
            label = labelPrefix + ":" + ex.GetType().Name;
        }

        return false;
    }

    private static bool TryInstallComponentByName(Ship ship, string componentName, string labelPrefix, out string label)
    {
        label = labelPrefix + ":missing";
        try
        {
            var components = G.GameData?.components;
            if (components == null || !components.TryGetValue(componentName, out ComponentData component) || component == null)
                return false;

            if (CurrentComponentMatches(ship, component))
            {
                label = labelPrefix + ":already";
                return false;
            }

            if (!component.enabled)
            {
                label = labelPrefix + ":disabled";
                return false;
            }

            string reason;
            if (!ship.IsComponentAvailable(component, out reason))
            {
                label = labelPrefix + ":unavailable:" + NormalizeReason(reason);
                return false;
            }

            if (ship.InstallComponent(component, true))
            {
                label = labelPrefix;
                return true;
            }

            label = labelPrefix + ":install-failed";
        }
        catch (Exception ex)
        {
            label = labelPrefix + ":" + ex.GetType().Name;
        }

        return false;
    }

    private static bool CurrentComponentMatches(Ship ship, ComponentData component)
    {
        try
        {
            var components = ship.components;
            if (components == null)
                return false;

            CompType? slot = Safe(() => component.typex, null);
            return slot != null &&
                components.TryGetValue(slot, out ComponentData current) &&
                SameComponent(current, component);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetSurvivability(Ship ship, Ship.Survivability target, string labelPrefix, out string label)
    {
        label = labelPrefix + ":not-needed";
        try
        {
            Ship.Survivability current = ship.survivability;
            if (current <= target)
            {
                label = labelPrefix + ":already-" + current;
                return false;
            }

            ship.SetSurvivability(target);
            label = labelPrefix;
            return true;
        }
        catch (Exception ex)
        {
            label = labelPrefix + ":" + ex.GetType().Name;
            return false;
        }
    }

    private static bool ComponentMatchesFamily(ComponentData? component, string family)
    {
        if (component == null)
            return false;

        string name = SafeString(() => component.name);
        string type = SafeString(() => component.type);
        return family switch
        {
            "barbette" => name.StartsWith("barbette_thickness_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "barbette", StringComparison.OrdinalIgnoreCase),
            "antiflooding" => name.StartsWith("Anti_Flooding_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("anti_flooding_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "antiflooding", StringComparison.OrdinalIgnoreCase),
            "bulkheads" => name.StartsWith("buklheads_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("bulkheads_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "bulkheads", StringComparison.OrdinalIgnoreCase),
            "multi_bottom" => name.StartsWith("multi_bottom_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "multi_bottom", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool SameComponent(ComponentData? left, ComponentData? right)
    {
        if (left == null || right == null)
            return false;

        long leftPointer = Safe(() => left.Pointer.ToInt64(), 0L);
        long rightPointer = Safe(() => right.Pointer.ToInt64(), 0L);
        if (leftPointer != 0L && leftPointer == rightPointer)
            return true;

        return string.Equals(ComponentLabel(left), ComponentLabel(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComponentLabel(ComponentData? component)
        => LogToken(SafeString(() => component?.name));

    private static bool IsSmallShipProtectionRescueType(string normalizedType)
        => string.Equals(normalizedType, "DD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "TB", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeightReason(string reason)
        => reason.Contains("weight", StringComparison.OrdinalIgnoreCase);

    private static string CostWeightBarbetteReason(WeightCheckResult check)
        => "costWeightBarbette:false reason=" + check.Reason + " parts=" + check.Parts;

    private static string SmallShipRescueSummary(
        string normalizedType,
        WeightCheckResult initial,
        WeightCheckResult final,
        IReadOnlyList<string> steps,
        string result,
        string? reason)
    {
        string stepText = steps.Count == 0 ? "none" : string.Join("|", steps.Select(LogToken));
        string summary =
            $"started type={normalizedType} initial={FmtTons(initial.Weight)}/{FmtTons(initial.Capacity)} over={FmtTons(initial.Overweight)} " +
            $"steps={stepText} result={result} final={FmtTons(final.Weight)}/{FmtTons(final.Capacity)}";
        if (!string.IsNullOrWhiteSpace(reason))
            summary += " reason=" + reason;
        return summary;
    }

    private static float ShipWeight(Ship ship)
        => Safe(() => ship.Weight(true, true), Safe(() => ship.Weight(), 0f));

    private static float ShipCapacity(Ship ship)
        => Safe(() => ship.Tonnage(), 0f);

    private static StrictValidationResult ValidateStrictNewDesign(Ship ship, string stage)
    {
        try { ship.CalcWeightAndCost(true, true); }
        catch (Exception ex) { return StrictValidationResult.Reject(stage, "calc:" + ex.GetType().Name); }

        try
        {
            bool reqValid = ship.IsValidCostReqParts(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo> notPassed,
                out Il2CppSystem.Collections.Generic.Dictionary<Part, string> badParts);
            if (!reqValid || HasItems(notPassed) || HasItems(badParts))
            {
                return StrictValidationResult.Reject(
                    stage,
                    "reqParts:false reason=" + NormalizeReason(reason) +
                    " notPassed=" + FormatReqInfoFailures(ship, notPassed) +
                    " badParts=" + FormatBadPartFailures(badParts));
            }
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "reqParts:" + ex.GetType().Name);
        }

        try
        {
            bool weightValid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);
            if (!weightValid)
            {
                return StrictValidationResult.Reject(
                    stage,
                    "costWeightBarbette:false reason=" + NormalizeReason(reason) +
                    " parts=" + FormatPartList(errorBarbettePart));
            }
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "costWeightBarbette:" + ex.GetType().Name);
        }

        try
        {
            if (!ship.IsValid(false))
                return StrictValidationResult.Reject(stage, "ship-valid-false-after-explicit-checks");
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "shipValid:" + ex.GetType().Name);
        }

        StrictValidationResult serializedTonnage = ValidateSerializedRealTonnage(ship, stage);
        if (!serializedTonnage.Valid)
            return serializedTonnage;

        try
        {
            bool canBuild = AiDesignBuildability.CanBuildDesign(
                Safe(() => ship.player, null),
                ship,
                1,
                "SmartAiStrictValidation:" + stage,
                out string buildReason);
            if (!canBuild)
                return StrictValidationResult.Reject(stage, "canBuild:false reason=" + buildReason);
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "canBuild:" + ex.GetType().Name);
        }

        return StrictValidationResult.Pass(stage);
    }

    private static StrictValidationResult ValidateSerializedRealTonnage(Ship ship, string stage)
    {
        try
        {
            Ship.Store? store = SafeToStore(ship);
            if (store == null)
                return StrictValidationResult.Reject(stage, "storeRealTonnage:null");

            float realTonnage = Safe(() => store.RealTonnage(), -1f);
            float capacity = ShipCapacity(ship);
            if (realTonnage <= 0f || capacity <= 0f)
                return StrictValidationResult.Reject(
                    stage,
                    $"storeRealTonnage:invalid real={FmtTons(realTonnage)} capacity={FmtTons(capacity)}");

            const float toleranceTons = 1f;
            if (realTonnage > capacity + toleranceTons)
            {
                return StrictValidationResult.Reject(
                    stage,
                    $"storeRealTonnage:over-capacity real={FmtTons(realTonnage)} capacity={FmtTons(capacity)} " +
                    $"weight={FmtTons(ShipWeight(ship))} beam={FmtTons(Safe(() => ship.Beam(), 0f))} draught={FmtTons(Safe(() => ship.Draught(), 0f))} " +
                    $"bonus={FmtTons(Safe(() => ship.BeamDraughtBonus(), 1f))} storeBeam={FmtTons(Safe(() => store.beam, 0f))} storeDraught={FmtTons(Safe(() => store.draught, 0f))}");
            }

            return StrictValidationResult.Pass(stage);
        }
        catch (Exception ex)
        {
            return StrictValidationResult.Reject(stage, "storeRealTonnage:" + ex.GetType().Name);
        }
    }

    private static Top3GateResult EvaluateTop3Gate(Player player, Ship candidate)
    {
        string type = AiDesignCompetitiveness.NormalizeShipType(candidate.shipType);
        CampaignAiDesignRosterPrunePatch.RosterDesign candidateRank =
            CampaignAiDesignRosterPrunePatch.BuildRosterDesign(candidate);

        List<CampaignAiDesignRosterPrunePatch.RosterDesign> existing = SafeShipList(player.designs)
            .Where(CampaignAiDesignRosterPrunePatch.ShouldConsiderDesign)
            .Where(design => !CampaignAiDesignRosterPrunePatch.SameDesign(design, candidate))
            .Select(CampaignAiDesignRosterPrunePatch.BuildRosterDesign)
            .Where(design => string.Equals(design.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(design => design.Buildable)
            .ToList();

        List<CampaignAiDesignRosterPrunePatch.RosterDesign> currentTop =
            CampaignAiDesignRosterPrunePatch.RankDesigns(existing)
                .Take(CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
                .ToList();
        if (currentTop.Count < CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
        {
            return Top3GateResult.Pass(
                $"candidate={FormatRoster(candidateRank)} currentTop={FormatTop(currentTop)} reason=fewer-than-3");
        }

        CampaignAiDesignRosterPrunePatch.RosterDesign third = currentTop.Last();
        float ratio = third.AdjustedPower > 0f
            ? candidateRank.AdjustedPower / third.AdjustedPower
            : 1f;
        List<CampaignAiDesignRosterPrunePatch.RosterDesign> combinedTop =
            CampaignAiDesignRosterPrunePatch.RankDesigns(existing.Append(candidateRank))
                .Take(CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
                .ToList();
        bool inTop3 = combinedTop.Any(entry => CampaignAiDesignRosterPrunePatch.SameDesign(entry.Ship, candidate));
        bool ratioPass = ratio >= Top3MinimumRatio;
        string detail =
            $"candidate={FormatRoster(candidateRank)} third={FormatRoster(third)} ratio={FmtRatio(ratio)} " +
            $"threshold={FmtRatio(Top3MinimumRatio)} inTop3={BoolText(inTop3)} currentTop={FormatTop(currentTop)} combinedTop={FormatTop(combinedTop)}";
        return inTop3 || ratioPass
            ? Top3GateResult.Pass(detail)
            : Top3GateResult.Reject(detail);
    }

    private static void FinalizeAcceptedDesign(Ship candidate, CampaignController controller, bool prewarming)
    {
        ClearSmartAiSharedMarkers(candidate, "finalize");

        try { candidate.CalcWeightAndCost(true, true); }
        catch { }

        if (prewarming)
            return;

        try { G.ui?.ReportNewDesign(candidate); }
        catch { }
    }

    private static void CleanupRejectedCandidate(Player player, Ship? candidate)
    {
        if (candidate == null)
            return;

        UnregisterAcceptedSmartAiDesign(candidate);

        try
        {
            if (ContainsShipByIdentity(SafeShipList(player.designs), candidate))
                CampaignController.Instance?.DeleteDesign(candidate);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "delete:" + ex.GetType().Name,
                $"{LogPrefix}: rejected-candidate DeleteDesign failed nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))}. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (!Safe(() => candidate.isErased, false))
                candidate.Erase();
        }
        catch (Exception ex)
        {
            WarnOnce(
                "erase:" + ex.GetType().Name,
                $"{LogPrefix}: rejected-candidate Erase failed nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ClearSmartAiSharedMarkers(Ship candidate, string phase)
    {
        if (candidate == null)
            return;

        bool storeSharedBefore = StoreShared(SafeToStore(candidate));
        List<string> cleared = new();
        foreach (string member in new[] { "IsSharedDesign", "isSharedDesign", "isShared" })
        {
            bool before = ReadBoolMember(candidate, member);
            bool set = TrySetBoolMember(candidate, member, false);
            bool after = ReadBoolMember(candidate, member);
            if ((before || set) && !after)
                cleared.Add(member);
        }

        bool storeSharedAfter = StoreShared(SafeToStore(candidate));
        if (cleared.Count == 0 && storeSharedBefore == storeSharedAfter)
            return;

        string clearedText = cleared.Count == 0 ? "none" : string.Join(",", cleared.Distinct(StringComparer.Ordinal));
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: cleared shared markers phase={LogToken(phase)} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))} cleared={LogToken(clearedText)} storeSharedBefore={BoolText(storeSharedBefore)} storeSharedAfter={BoolText(storeSharedAfter)}.");
    }

    private static bool ReadBoolMember(object? target, string memberName)
    {
        if (target == null)
            return false;

        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(target) is bool propertyValue)
                return propertyValue;

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target) is bool fieldValue && fieldValue;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetBoolMember(object? target, string memberName, bool value)
    {
        if (target == null)
            return false;

        bool wrote = false;
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.SetMethod != null)
            {
                property.SetValue(target, value);
                wrote = true;
            }

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(target, value);
                wrote = true;
            }
        }
        catch
        {
            return wrote;
        }

        return wrote;
    }

    private static Ship.Store? SafeToStore(Ship? ship)
        => ship == null ? null : Safe(() => ship.ToStore(false), null);

    private static string SerializedTonnageSummary(Ship? ship)
    {
        if (ship == null)
            return "none";

        Ship.Store? store = SafeToStore(ship);
        if (store == null)
            return "store=null";

        return
            $"real={FmtTons(Safe(() => store.RealTonnage(), -1f))}" +
            $"_capacity={FmtTons(Safe(() => ship.Tonnage(), 0f))}" +
            $"_weight={FmtTons(ShipWeight(ship))}" +
            $"_beam={FmtTons(Safe(() => ship.Beam(), 0f))}" +
            $"_draught={FmtTons(Safe(() => ship.Draught(), 0f))}" +
            $"_bonus={FmtTons(Safe(() => ship.BeamDraughtBonus(), 1f))}" +
            $"_storeBeam={FmtTons(Safe(() => store.beam, 0f))}" +
            $"_storeDraught={FmtTons(Safe(() => store.draught, 0f))}";
    }

    private static HullSummary ResolveHullSummary(Ship? ship, Ship.Store? store, string fallbackHull)
    {
        string id = SafeString(() => ship?.hull?.data?.name);
        if (string.Equals(id, "<empty>", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id))
            id = SafeString(() => store?.hullName);
        if (string.Equals(id, "<empty>", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id))
            id = fallbackHull;
        if (string.IsNullOrWhiteSpace(id))
            id = "?";

        string display = SafeString(() => ship?.hull?.data?.nameUi);
        if (string.Equals(display, "<empty>", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(display))
            display = id;

        return new HullSummary(display, id);
    }

    private static bool StoreShared(Ship.Store? store)
        => Safe(() => store?.isSharedDesign ?? false, false);

    private static Il2CppSystem.Nullable<Il2CppSystem.Guid> NewManualShipId()
    {
        Il2CppSystem.Guid id = Il2CppSystem.Guid.NewGuid();
        return new Il2CppSystem.Nullable<Il2CppSystem.Guid>(id);
    }

    private static void EnsureSmartAiDesignIdentity(Ship? candidate, Ship.Store? sourceStore, string phase)
    {
        if (candidate == null)
            return;

        try
        {
            Il2CppSystem.Guid oldId = Safe(() => candidate.id, Il2CppSystem.Guid.Empty);
            bool changed = false;
            if (oldId == Il2CppSystem.Guid.Empty)
            {
                candidate.id = Il2CppSystem.Guid.NewGuid();
                changed = true;
            }

            Il2CppSystem.Guid newId = Safe(() => candidate.id, Il2CppSystem.Guid.Empty);
            Il2CppSystem.Guid sourceId = Safe(() => sourceStore?.id ?? Il2CppSystem.Guid.Empty, Il2CppSystem.Guid.Empty);
            if (!changed && sourceId != Il2CppSystem.Guid.Empty)
                return;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: normalized smart design identity phase={LogToken(phase)} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))} oldId={LogToken(oldId.ToString())} newId={LogToken(newId.ToString())} sourceStoreId={LogToken(sourceId.ToString())}.");
        }
        catch (Exception ex)
        {
            WarnOnce(
                "smart-ai-identity:" + ex.GetType().Name,
                $"{LogPrefix}: smart design identity normalization failed phase={LogToken(phase)} design={LogToken(AiDesignCompetitiveness.ShipLabel(candidate))}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RegisterAcceptedSmartAiDesign(Ship? ship)
    {
        foreach (string key in SmartAiDesignKeys(ship))
        {
            if (!AcceptedSmartAiDesignKeys.Add(key))
                continue;

            AcceptedSmartAiDesignKeyOrder.Enqueue(key);
            while (AcceptedSmartAiDesignKeyOrder.Count > MaxAcceptedSmartAiDesignKeys)
            {
                string stale = AcceptedSmartAiDesignKeyOrder.Dequeue();
                AcceptedSmartAiDesignKeys.Remove(stale);
            }
        }
    }

    private static void UnregisterAcceptedSmartAiDesign(Ship? ship)
    {
        foreach (string key in SmartAiDesignKeys(ship))
            AcceptedSmartAiDesignKeys.Remove(key);
    }

    private static IEnumerable<string> SmartAiDesignKeys(Ship? ship)
    {
        if (ship == null)
            yield break;

        long pointer = Safe(() => ship.Pointer.ToInt64(), 0L);
        if (pointer != 0L)
            yield return "ptr:" + pointer.ToString(CultureInfo.InvariantCulture);

        string id = Safe(() => ship.id.ToString(), string.Empty);
        if (IsUsefulGuidText(id))
            yield return "id:" + id;

        Ship.Store? store = SafeToStore(ship);
        string storeId = Safe(() => store?.id.ToString() ?? string.Empty, string.Empty);
        if (IsUsefulGuidText(storeId))
            yield return "store:" + storeId;

        string designId = Safe(() => store?.designId.ToString() ?? string.Empty, string.Empty);
        if (IsUsefulGuidText(designId))
            yield return "design:" + designId;
    }

    private static bool IsUsefulGuidText(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           !string.Equals(value, Il2CppSystem.Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase);

    private static void RecordTurnSummary(
        int turnIndex,
        string turn,
        string nation,
        bool prewarming,
        string type,
        bool accepted,
        string stage,
        string reason,
        string design,
        HullSummary hull,
        Ship? ship,
        int attemptsUsed,
        int acceptedAttempt,
        IReadOnlyList<SmartAiInnerAttemptResult> attemptResults,
        long elapsedMs)
    {
        string key = TurnSummaryKey(turnIndex, turn, prewarming);
        if (!PendingTurnSummaries.TryGetValue(key, out SmartAiDesignTurnSummary? summary))
        {
            summary = new SmartAiDesignTurnSummary(turnIndex, turn, prewarming);
            PendingTurnSummaries[key] = summary;
        }

        int normalizedAttemptsUsed = Math.Max(1, attemptsUsed);
        summary.Add(new SmartAiDesignTurnEntry(
            nation,
            type,
            design,
            hull.Display,
            hull.Id,
            accepted,
            stage,
            reason,
            normalizedAttemptsUsed,
            MaxSmartAiDesignAttempts,
            acceptedAttempt,
            WeightCapacitySummary(ship),
            elapsedMs,
            normalizedAttemptsUsed > 1 ? FirstFailureSummary(attemptResults) : "none"));
    }

    private static string WeightCapacitySummary(Ship? ship)
    {
        if (ship == null)
            return "none";

        try { ship.CalcWeightAndCost(true, true); }
        catch { }

        return $"{FmtTons(ShipWeight(ship))}/{FmtTons(ShipCapacity(ship))}";
    }

    private static string FormatPriorFailures(IReadOnlyList<SmartAiInnerAttemptResult> attempts)
    {
        string text = string.Join(
            "|",
            attempts
                .Where(attempt => !attempt.Accepted)
                .Select(attempt => $"{attempt.Attempt}:{ShortLogToken(attempt.Stage, 32)}:{ShortLogToken(attempt.Reason, 96)}"));
        return string.IsNullOrWhiteSpace(text) ? "none" : text;
    }

    private static string FirstFailureSummary(IReadOnlyList<SmartAiInnerAttemptResult> attempts)
    {
        SmartAiInnerAttemptResult failure = attempts.FirstOrDefault(attempt => !attempt.Accepted);
        return failure.Attempt <= 0
            ? "none"
            : $"{failure.Stage}:{ShortLogToken(failure.Reason, 72)}";
    }

    private static string FormatInnerAttemptDetails(
        IReadOnlyList<SmartAiInnerAttemptResult> attempts,
        int maxAttempts)
    {
        if (attempts.Count == 0)
            return "none";

        return string.Join(
            "|",
            attempts.Select(attempt =>
                $"{attempt.Attempt}/{maxAttempts}:{(attempt.Accepted ? "accepted" : "rejected")}:{ShortLogToken(attempt.Stage, 48)}:{ShortLogToken(attempt.Reason, 144)}" +
                $":elapsedMs={attempt.ElapsedMs.ToString(CultureInfo.InvariantCulture)}" +
                $":parts={ShortLogToken(attempt.PartsSummary, 144)}" +
                $":rescue={ShortLogToken(attempt.RescueSummary, 96)}" +
                $":armor={ShortLogToken(attempt.ArmorSummary, 144)}" +
                $":power={ShortLogToken(attempt.PowerSummary, 96)}"));
    }

    private static string TurnSummaryKey(int turnIndex, string turn, bool prewarming)
        => $"{turnIndex}:{LogToken(turn)}:{(prewarming ? "prewarm" : "normal")}";

    private static int RequestedYear(CampaignController controller, bool prewarming)
    {
        if (prewarming)
            return Safe(() => controller.StartYear, 1890);

        return Safe(() => controller.CurrentDate.AsDate().Year, Safe(() => controller.StartYear, 1890));
    }

    private static DesignBookLookup CurrentDesignBook(CampaignController controller, bool prewarming)
    {
        List<string> details = new();
        if (CurrentDesignsField == null)
        {
            details.Add("fieldInfo=null");
        }
        else
        {
            try
            {
                object? raw = CurrentDesignsField.GetValue(controller);
                if (raw is CampaignDesigns fieldBook)
                    return new DesignBookLookup(fieldBook, "field=ok");

                details.Add(raw == null ? "fieldValue=null" : "fieldType=" + raw.GetType().Name);
            }
            catch (Exception ex)
            {
                details.Add("fieldGet=" + ex.GetType().Name);
            }
        }

        try
        {
            CampaignDesigns? rebuilt = controller.CheckPredefinedDesigns(prewarming);
            if (rebuilt != null)
            {
                details.Add("checkPredefined=ok");
                return new DesignBookLookup(rebuilt, string.Join(",", details));
            }

            details.Add("checkPredefined=null");
        }
        catch (Exception ex)
        {
            details.Add("checkPredefined=" + ex.GetType().Name);
        }

        return new DesignBookLookup(null, string.Join(",", details));
    }

    private static string StoreHull(Ship.Store? store)
        => SafeString(() => store?.hullName);

    private static string StoreDesign(Ship.Store? store)
        => SafeString(() => store?.vesselName);

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

    private static bool ContainsShipByIdentity(IEnumerable<Ship> ships, Ship target)
    {
        foreach (Ship ship in ships)
        {
            if (CampaignAiDesignRosterPrunePatch.SameDesign(ship, target))
                return true;
        }

        return false;
    }

    private static bool HasItems<T>(Il2CppSystem.Collections.Generic.List<T>? items)
        => Safe(() => items != null && items.Count > 0, false);

    private static bool HasItems<TKey, TValue>(Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>? items)
        where TKey : notnull
        => Safe(() => items != null && items.Count > 0, false);

    private static string FormatReqInfoFailures(
        Ship ship,
        Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo>? notPassed)
    {
        try
        {
            if (notPassed == null || notPassed.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (ShipType.ReqInfo req in notPassed)
            {
                if (labels.Count >= 6)
                    break;

                string stat = LogToken(SafeString(() => req.stat?.name));
                float value = Safe(() => ship.stats[req.stat].total, float.NaN);
                string valueText = float.IsNaN(value) ? "?" : value.ToString("0.##", CultureInfo.InvariantCulture);
                labels.Add($"{stat}={valueText}({req.min}-{req.max})");
            }

            int hidden = Math.Max(0, notPassed.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string FormatBadPartFailures(Il2CppSystem.Collections.Generic.Dictionary<Part, string>? badParts)
    {
        try
        {
            if (badParts == null || badParts.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (var pair in badParts)
            {
                if (labels.Count >= 6)
                    break;

                labels.Add($"{PartLabel(pair.Key)}:{LogToken(pair.Value)}");
            }

            int hidden = Math.Max(0, badParts.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string FormatPartList(Il2CppSystem.Collections.Generic.List<Part>? parts)
    {
        try
        {
            if (parts == null || parts.Count == 0)
                return "none";

            List<string> labels = new();
            foreach (Part part in parts)
            {
                if (labels.Count >= 6)
                    break;

                labels.Add(PartLabel(part));
            }

            int hidden = Math.Max(0, parts.Count - labels.Count);
            if (hidden > 0)
                labels.Add($"+{hidden}more");

            return string.Join("|", labels);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string PartLabel(Part? part)
    {
        string name = SafeString(() => part?.data?.name);
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return LogToken(name);

        return LogToken(SafeString(() => part?.name));
    }

    private static void TrySetInactive(Ship? ship)
    {
        try { ship?.SetActive(false); }
        catch { }
    }

    private static string FormatRoster(CampaignAiDesignRosterPrunePatch.RosterDesign design)
        => CampaignAiDesignRosterPrunePatch.FormatRosterDesignCompact(design);

    private static string FormatTop(IReadOnlyList<CampaignAiDesignRosterPrunePatch.RosterDesign> designs)
        => designs.Count == 0
            ? "none"
            : string.Join(",", designs.Select(FormatRoster));

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "none";

        string normalized = reason.Trim().Trim('$');
        normalized = normalized.Replace("Ui_Constr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
    }

    private static string ReadableReason(string stage, string reason)
    {
        if (reason.Contains("weight", StringComparison.OrdinalIgnoreCase))
            return $"{ReadableLogText(stage)} overweight";

        string text = $"{stage} {ShortLogToken(reason, 96)}";
        return ReadableLogText(text);
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

    private static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        return value
            .Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace(";", ",")
            .Replace("[", "(")
            .Replace("]", ")")
            .Replace(" ", "_");
    }

    private static string ShortLogToken(string? value, int maxLength)
    {
        string token = LogToken(value);
        if (token.Length <= maxLength)
            return token;

        return token[..Math.Max(0, maxLength)] + "...";
    }

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string FmtRatio(float ratio)
        => (ratio * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string FmtTons(float tons)
        => tons.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FmtDeltaTons(float tons)
        => tons.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    private static string BoolText(bool value)
        => value.ToString().ToLowerInvariant();

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning(message);
    }

    private static string ExceptionChainToken(Exception ex)
    {
        List<string> parts = new();
        Exception? current = ex;
        int depth = 0;
        while (current != null && depth < 3)
        {
            parts.Add(current.GetType().Name + ":" + LogToken(current.Message));
            current = current.InnerException;
            depth++;
        }

        return string.Join(">", parts);
    }

    private static string ObjectKey(object value)
    {
        try
        {
            PropertyInfo? pointer = value.GetType().GetProperty(
                "Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? pointerValue = pointer?.GetValue(value);
            if (pointerValue != null)
                return pointerValue.ToString() ?? RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        return RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture);
    }

    private sealed class SmartAiDesignTurnSummary
    {
        private readonly List<SmartAiDesignTurnEntry> entries = new();

        internal SmartAiDesignTurnSummary(int turnIndex, string turnLabel, bool prewarming)
        {
            TurnIndex = turnIndex;
            TurnLabel = turnLabel;
            Prewarming = prewarming;
        }

        internal int TurnIndex { get; }
        internal string TurnLabel { get; }
        internal bool Prewarming { get; }
        internal int Entries => entries.Count;
        internal int Accepted => entries.Count(entry => entry.Accepted);
        internal int Rejected => entries.Count(entry => !entry.Accepted);
        internal int Retries => entries.Count(entry => entry.AttemptsUsed > 1);
        internal long TotalMs => entries.Sum(entry => entry.ElapsedMs);

        internal void Add(SmartAiDesignTurnEntry entry)
            => entries.Add(entry);

        internal string ToLogLine(string logPrefix)
        {
            if (Entries <= 0)
                return $"{logPrefix} summary turn={LogToken(TurnLabel)} entries=0";

            string designs = string.Join(" | ", entries.Select(entry => entry.ToLogText()));
            string prewarming = Prewarming ? " prewarming=true" : string.Empty;
            return
                $"{logPrefix} summary turn={LogToken(TurnLabel)} entries={Entries} accepted={Accepted} rejected={Rejected} retries={Retries} totalMs={TotalMs}{prewarming} designs={designs}";
        }
    }

    private readonly record struct SmartAiDesignTurnEntry(
        string Nation,
        string Type,
        string Design,
        string HullDisplay,
        string HullId,
        bool Accepted,
        string Stage,
        string Reason,
        int AttemptsUsed,
        int MaxAttempts,
        int AcceptedAttempt,
        string WeightCapacity,
        long ElapsedMs,
        string FirstFailure)
    {
        internal string ToLogText()
        {
            string status = Accepted
                ? $"accepted {Math.Max(1, AcceptedAttempt)}/{MaxAttempts}"
                : $"rejected {AttemptsUsed}/{MaxAttempts}, {ReadableReason(Stage, Reason)}";
            string retry = AttemptsUsed > 1 && !string.Equals(FirstFailure, "none", StringComparison.OrdinalIgnoreCase)
                ? ", retry=" + LogToken(FirstFailure)
                : string.Empty;
            return
                $"{ReadableLogText(Nation)} {Type} {ReadableLogText(Design)}, hull {ReadableLogText(HullDisplay)} ({LogToken(HullId)}): " +
                $"{status}, {WeightCapacity}t, {ElapsedMs.ToString(CultureInfo.InvariantCulture)}ms{retry}";
        }
    }

    private readonly record struct HullSummary(string Display, string Id);

    private readonly record struct DesignBookLookup(CampaignDesigns? Book, string Detail);

    private readonly record struct SmartAiInnerAttemptResult(
        int Attempt,
        bool Accepted,
        string Stage,
        string Reason,
        string DefaultsPre,
        string DefaultsPost,
        string PartsSummary,
        string GeneratorSummary,
        string RescueSummary,
        string ArmorSummary,
        string PowerSummary,
        long ElapsedMs)
    {
        internal static SmartAiInnerAttemptResult Empty { get; } = new(
            0,
            false,
            "not-run",
            "no-attempts",
            "not-run",
            "not-run",
            "not-run",
            "not-run",
            "not-run",
            "not-run",
            "not-run",
            0);
    }

    private readonly record struct RandomGenerationResult(
        bool Success,
        string Stage,
        string Reason,
        string Summary,
        string DefaultsPre,
        string DefaultsPost,
        bool AllowVanillaFallback);

    private readonly record struct StrictValidationResult(bool Valid, string Stage, string Reason)
    {
        internal static StrictValidationResult Pass(string stage)
            => new(true, stage, "valid");

        internal static StrictValidationResult Reject(string stage, string reason)
            => new(false, stage, reason);
    }

    private readonly record struct SmallShipRescueResult(bool Valid, string Stage, string Reason, string Summary)
    {
        internal static SmallShipRescueResult Pass(string stage, string summary)
            => new(true, stage, "valid", summary);

        internal static SmallShipRescueResult Reject(string stage, string reason, string summary)
            => new(false, stage, reason, summary);
    }

    private readonly record struct WeightCheckResult(
        bool Valid,
        string Reason,
        string Parts,
        float Weight,
        float Capacity,
        bool CheckFailed)
    {
        internal float Overweight => Math.Max(0f, Weight - Capacity);
    }

    private readonly record struct Top3GateResult(bool Accept, string Detail)
    {
        internal static Top3GateResult Pass(string detail)
            => new(true, detail);

        internal static Top3GateResult Reject(string detail)
            => new(false, detail);
    }

    private sealed class SmartAiRandomGenerationContext
    {
        internal SmartAiRandomGenerationContext(Ship candidate)
        {
            Candidate = candidate;
        }

        internal Ship Candidate { get; }
        internal bool Started { get; set; }
        internal bool Completed { get; set; }
        internal bool EarlyDefaultsApplied { get; set; }
        internal string EarlyDefaultsSummary { get; set; } = "not-run";
        internal List<string> SkippedVanillaSetupStates { get; } = new();
        internal bool PreDefaultsApplied { get; set; }
        internal string DefaultsPre { get; set; } = "not-run";
        internal string DefaultsPost { get; set; } = "not-run";
        internal int MoveNextCalls { get; set; }
        internal SortedSet<int> StatesSeen { get; } = new();
        internal bool CallbackInvoked { get; set; }
        internal bool CallbackResult { get; set; }
        internal int CallbackTries { get; set; }
        internal float CallbackBuildTime { get; set; }
        internal string StateMachineKey { get; set; } = "none";
        internal string CapFailure { get; set; } = "none";
        internal string EarlyStateSkipFailure { get; set; } = "none";
        internal string StateReadFailure { get; set; } = "none";
        internal string MoveNextFailure { get; set; } = "none";
        internal string LeaveConstructorFailure { get; set; } = "none";
        internal bool StoppedAfterSmartParts { get; set; }
        internal string EndAutodesignSummary { get; set; } = "not-run";
        internal bool SmartPartsAttempted { get; set; }
        internal bool SmartPartsSucceeded { get; set; }
        internal string SmartPartsSummary { get; set; } = "not-run";
        internal bool SkippedVanillaPartsState { get; set; }
        internal string StateSkip { get; set; } = "none";
        internal List<string> UnmatchedExamples { get; } = new();
        internal List<string> FallbackClaimExamples { get; } = new();
        private HashSet<string> LoggedGeneratorTypeKeys { get; } = new(StringComparer.Ordinal);

        internal void RecordUnmatched(
            Type stateMachineType,
            string phase,
            string reason,
            int? state,
            RandomShipStateFields fields,
            Ship? ship)
        {
            string detail = StateMachineDiagnostic(stateMachineType, phase, reason, state, fields, ship);
            if (UnmatchedExamples.Count < 3)
                UnmatchedExamples.Add(detail);

            string key = stateMachineType.FullName + ":unmatched:" + reason;
            if (VerboseDiagnostics && LoggedGeneratorTypeKeys.Add(key) && UnmatchedExamples.Count <= 3)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{LogPrefix}: generator-context-unmatched {detail}.");
            }
        }

        internal void RecordFallbackClaim(
            Type stateMachineType,
            string phase,
            string reason,
            int? state,
            RandomShipStateFields fields)
        {
            string detail = StateMachineDiagnostic(stateMachineType, phase, reason, state, fields, null);
            if (FallbackClaimExamples.Count < 3)
                FallbackClaimExamples.Add(detail);

            string key = stateMachineType.FullName + ":fallback:" + reason;
            if (VerboseDiagnostics && LoggedGeneratorTypeKeys.Add(key) && FallbackClaimExamples.Count <= 3)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"{LogPrefix}: generator-context-fallback-claim {detail}.");
            }
        }

        private string StateMachineDiagnostic(
            Type stateMachineType,
            string phase,
            string reason,
            int? state,
            RandomShipStateFields fields,
            Ship? ship)
        {
            long candidatePointer = Safe(() => Candidate.Pointer.ToInt64(), 0L);
            long shipPointer = Safe(() => ship?.Pointer.ToInt64() ?? 0L, 0L);
            string typeName = stateMachineType.FullName ?? stateMachineType.Name;
            return
                $"phase={LogToken(phase)} reason={LogToken(reason)} type={LogToken(typeName)} state={state?.ToString(CultureInfo.InvariantCulture) ?? "?"} " +
                $"stateField={LogToken(fields.State?.Name)} shipField={LogToken(fields.Ship?.Name)} useSmallField={LogToken(fields.UseSmallAmountTries?.Name)} triesField={LogToken(fields.TriesTotal?.Name)} " +
                $"candidatePtr={candidatePointer} shipPtr={shipPointer}";
        }
    }

    private readonly record struct RandomShipStateFields(
        MemberAccessor? State,
        MemberAccessor? Ship,
        MemberAccessor? UseSmallAmountTries,
        MemberAccessor? TriesTotal,
        MemberAccessor? TryN);

    private sealed class MemberAccessor
    {
        private readonly FieldInfo? field;
        private readonly PropertyInfo? property;

        internal MemberAccessor(FieldInfo? field, PropertyInfo? property)
        {
            this.field = field;
            this.property = property;
            Name = field?.Name ?? property?.Name ?? "?";
        }

        internal string Name { get; }

        internal object? GetValue(object instance)
            => field != null ? field.GetValue(instance) : property?.GetValue(instance);

        internal void SetValue(object instance, object? value)
        {
            if (field != null)
                field.SetValue(instance, value);
            else if (property?.SetMethod != null)
                property.SetValue(instance, value);
        }
    }
}

internal readonly record struct SmartAiDesignAttemptResult(
    bool Attempted,
    bool Accepted,
    bool SuppressVanillaFallback,
    string Stage,
    string Reason)
{
    internal static SmartAiDesignAttemptResult NotAttempted { get; } = new(false, false, false, "not-attempted", "disabled-or-out-of-scope");

    internal static SmartAiDesignAttemptResult Suppress(string reason)
        => new(true, false, true, "not-attempted", reason);
}
