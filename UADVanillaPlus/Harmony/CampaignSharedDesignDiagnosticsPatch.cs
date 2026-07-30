using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Traces why the vanilla AI shared-design path accepts or rejects candidates,
// and normalizes accepted AI campaign imports so stale serialized design dates
// do not make fresh shared designs sort as obsolete.
internal static class CampaignSharedDesignDiagnosticsPatch
{
    private const string LogPrefix = "[AI SharedDesign]";
    private const string DebugLogPrefix = "[AI SharedDesign Debug]";
    private const int MaxRejectDetails = 6;
    private const int MaxMissingTechDetails = 24;
    private const int MaxUnlockDetails = 20;
    private const int SharedDesignPastYearWindow = 7;
    private const int SharedDesignFutureYearWindow = 3;
    private const int MaxBlueprintRemovedItems = 8;
    private const float InternalWeightRescueMarginTons = 0.5f;

    private static readonly Stack<AttemptContext> ActiveAttempts = new();
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly MethodInfo? TechMatchMethod =
        AccessTools.Method(typeof(CampaignController), "TechMatch", new[] { typeof(Ship), typeof(Player) });
    private static readonly List<PropertyInfo> ShipTechCollectionProperties = FindShipTechCollectionProperties();
    private static readonly List<FieldInfo> ShipTechCollectionFields = FindShipTechCollectionFields();
    private static readonly HashSet<string> LoggedTechCollectionFields = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSanitizedTechs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<SharedDesignGapRecord>> SharedDesignGapsByTurn = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<long>> SharedDesignGapCompletedByTurn = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PendingSharedDesignGapSummaryTurns = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSharedDesignGapSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSharedDesignOnlyBlocks = new(StringComparer.Ordinal);
    private static string? ActiveSharedDesignGapTurn;

    internal static AttemptContext? BeginAttempt(CampaignController? controller, Player? player, ShipType? shipType, bool prewarming)
    {
        if (!ShouldTrace(player))
            return null;

        int year = RequestedYear(controller, prewarming);
        AttemptContext context = new(
            PlayerPointer(player!),
            NationKey(player),
            PlayerLabel(player),
            NormalizeShipType(shipType),
            year,
            prewarming,
            SharedUsageLabel(controller),
            DesignUsageLabel(controller));

        ActiveAttempts.Push(context);
        CampaignAiDesignGenerationDiagnostics.RecordSharedAttempt(player, shipType);
        Log(
            $"attempt nation={context.Nation} key={context.NationKey} type={context.Type} year={context.Year} prewarm={context.Prewarming.ToString().ToLowerInvariant()} advancedAiBuilder={AdvancedAiBuilderLabel()} sharedUsage={context.SharedUsage} designUsage={context.DesignUsage}.");
        return context;
    }

    internal static void EndAttempt(AttemptContext? context, bool result)
    {
        if (context == null)
            return;

        try
        {
            CampaignAiDesignGenerationDiagnostics.RecordSharedResult(context.PlayerPointer, context.Type, result);
            string resultText = context.OnlyFallbackBlocked
                ? "blocked-random-fallback"
                : result ? "success" : "fallback";
            string selected = string.IsNullOrWhiteSpace(context.SelectedDesign)
                ? "none"
                : context.SelectedDesign;
            string reason = context.OnlyFallbackBlocked
                ? "shared designs only blocked random fallback"
                : result ? "shared design taken" : "no accepted shared design";
            Log(
                $"{resultText} nation={context.Nation} type={context.Type} year={context.Year} advancedAiBuilder={AdvancedAiBuilderLabel()} selected={selected} reason={reason}.");
        }
        finally
        {
            PopAttempt(context);
        }
    }

    internal static void ApplyOnlyFallbackBlock(
        CampaignController? controller,
        Player? player,
        ShipType? shipType,
        bool prewarming,
        AttemptContext? context,
        ref bool result)
    {
        if (result ||
            context == null ||
            prewarming ||
            !CampaignSharedDesignUsageSettings.IsOnlyModeActive ||
            player == null ||
            !Safe(() => player.isAi && !player.isMain, false))
        {
            return;
        }

        result = true;
        context.OnlyFallbackBlocked = true;

        string turn = CurrentTurnLabel();
        string nation = PlayerLabel(player);
        string type = NormalizeShipType(shipType);
        string key = $"{turn}|{NationKey(player)}|{type}";
        if (LoggedSharedDesignOnlyBlocks.Add(key))
        {
            LogRaw(
                $"[AI SharedDesign Only] blocked-random-fallback turn={LogToken(turn)} nation={LogToken(nation)} type={LogToken(type)} reason=noAcceptedSharedDesign");
        }
    }

    internal static void TraceGetSharedDesign(
        CampaignController? controller,
        Player? player,
        ShipType? shipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        SharedDesignBookSnapshot? existingDesigns = null)
    {
        if (!ShouldTrace(player))
            return;

        try
        {
            existingDesigns ??= CaptureExistingSharedDesigns(player, shipType);
            CandidateSummary summary = AnalyzeCandidates(controller, player!, shipType, year, checkTech, isEarlySavedShip, existingDesigns);
            Log(
                $"candidates nation={PlayerLabel(player)} key={NationKey(player)} type={NormalizeShipType(shipType)} year={year} window={SharedDesignWindowText()} order=yearDesc advancedAiBuilder={AdvancedAiBuilderLabel()} checkTech={checkTech.ToString().ToLowerInvariant()} earlySaved={isEarlySavedShip.ToString().ToLowerInvariant()} total={summary.Total} typeMatch={summary.TypeMatch} yearMatch={summary.YearMatch} fromStore={summary.FromStore} buildReject={summary.BuildReject} techReject={summary.TechReject} duplicateSkip={summary.DuplicateReject} accepted={summary.Accepted} buildReasons={summary.BuildReasonText()} rejectDetails={summary.RejectDetailText()} noCandidate={summary.NoCandidateReason()}.");
            if (checkTech && !isEarlySavedShip)
                RecordSharedDesignGap(controller, player!, shipType, year, summary);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "trace:" + ex.GetType().Name,
                $"candidate trace failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {year}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordDesignGenerationCompleted(CampaignController? controller, Player? player)
    {
        if (!ShouldTrace(player))
            return;

        try
        {
            string turn = CurrentTurnLabel();
            EnsureSharedDesignGapTurn(controller, turn);
            if (!SharedDesignGapCompletedByTurn.TryGetValue(turn, out HashSet<long>? completed))
            {
                completed = new HashSet<long>();
                SharedDesignGapCompletedByTurn[turn] = completed;
            }

            completed.Add(PlayerPointer(player!));
            int expected = ExpectedSharedDesignGapCompletionCount(controller);
            if (expected <= 0 || completed.Count >= expected)
                PendingSharedDesignGapSummaryTurns.Add(turn);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "gapComplete:" + ex.GetType().Name,
                $"shared-design gap completion failed for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RecordSharedDesignGap(
        CampaignController? controller,
        Player player,
        ShipType? shipType,
        int suggestedYear,
        CandidateSummary summary)
    {
        if (summary.Accepted > 0 || !IsSharedDesignGapBuildableType(player, shipType))
            return;

        AttemptContext? attempt = ActiveAttemptFor(player, shipType, suggestedYear);
        if (attempt?.Prewarming == true)
            return;

        try
        {
            string turn = CurrentTurnLabel();
            EnsureSharedDesignGapTurn(controller, turn);
            if (!SharedDesignGapsByTurn.TryGetValue(turn, out List<SharedDesignGapRecord>? gaps))
            {
                gaps = new List<SharedDesignGapRecord>();
                SharedDesignGapsByTurn[turn] = gaps;
            }

            SharedDesignGapRecord record = SharedDesignGapRecord.Create(
                player,
                shipType,
                suggestedYear,
                summary,
                turn);
            string key = record.Key;
            int existingIndex = gaps.FindIndex(gap => string.Equals(gap.Key, key, StringComparison.Ordinal));
            if (existingIndex >= 0)
                gaps[existingIndex] = gaps[existingIndex].Merge(record);
            else
                gaps.Add(record);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "gapRecord:" + ex.GetType().Name,
                $"shared-design gap record failed for {PlayerLabel(player)} {NormalizeShipType(shipType)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void EnsureSharedDesignGapTurn(CampaignController? controller, string turn)
    {
        if (string.IsNullOrWhiteSpace(turn))
            return;

        if (!string.IsNullOrWhiteSpace(ActiveSharedDesignGapTurn) &&
            !string.Equals(ActiveSharedDesignGapTurn, turn, StringComparison.Ordinal))
        {
            FlushSharedDesignGapSummary(ActiveSharedDesignGapTurn!);
        }

        ActiveSharedDesignGapTurn = turn;
    }

    internal static void FlushPendingSharedDesignGapSummariesAfterNextTurn()
    {
        try
        {
            if (PendingSharedDesignGapSummaryTurns.Count == 0)
                return;

            List<string> pendingTurns = PendingSharedDesignGapSummaryTurns
                .OrderBy(static turn => turn, StringComparer.Ordinal)
                .ToList();
            PendingSharedDesignGapSummaryTurns.Clear();

            foreach (string pendingTurn in pendingTurns)
                FlushSharedDesignGapSummary(pendingTurn);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "gapFlushAfterNextTurn:" + ex.GetType().Name,
                $"shared-design deferred gap summary flush failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ResetTurnScopedSharedDesignDiagnostics(string context)
    {
        ActiveAttempts.Clear();
        SharedDesignGapsByTurn.Clear();
        SharedDesignGapCompletedByTurn.Clear();
        PendingSharedDesignGapSummaryTurns.Clear();
        LoggedSharedDesignGapSummaries.Clear();
        LoggedSharedDesignOnlyBlocks.Clear();
        ActiveSharedDesignGapTurn = null;
        Log($"reset turn-scoped diagnostics context={LogToken(context)}.");
    }

    private static void FlushSharedDesignGapSummary(string turn)
    {
        if (string.IsNullOrWhiteSpace(turn) || !LoggedSharedDesignGapSummaries.Add(turn))
            return;

        SharedDesignGapsByTurn.TryGetValue(turn, out List<SharedDesignGapRecord>? gaps);
        List<SharedDesignGapRecord> ordered = (gaps ?? new List<SharedDesignGapRecord>())
            .OrderBy(gap => gap.Nation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(gap => SurfaceTypeOrder(gap.Type))
            .ThenBy(gap => gap.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogGap($"[AI SharedDesign Gaps] turn={LogToken(turn)} entries={ordered.Count}");
        foreach (SharedDesignGapRecord gap in ordered)
            LogGap(gap.ToLogLine());

        SharedDesignGapsByTurn.Remove(turn);
        SharedDesignGapCompletedByTurn.Remove(turn);
        PendingSharedDesignGapSummaryTurns.Remove(turn);
    }

    private static int ExpectedSharedDesignGapCompletionCount(CampaignController? controller)
    {
        try
        {
            var players = (controller ?? CampaignController.Instance)?.CampaignData?.PlayersMajor;
            if (players == null)
                return 0;

            int count = 0;
            foreach (Player player in players)
            {
                if (player != null &&
                    Safe(() => player.isAi && !player.isMain, false) &&
                    !Safe(() => player.IsDisabled(), false))
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    internal static MethodBase? NextTurnMoveNextTarget()
    {
        return typeof(CampaignController)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => new
            {
                Type = type,
                MoveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            })
            .Where(candidate =>
                candidate.MoveNext != null &&
                candidate.Type.Name.Contains("NextTurn", StringComparison.Ordinal) &&
                candidate.MoveNext.ReturnType == typeof(bool) &&
                candidate.MoveNext.GetParameters().Length == 0)
            .Select(candidate => candidate.MoveNext)
            .FirstOrDefault();
    }

    private static bool IsSharedDesignGapBuildableType(Player player, ShipType? shipType)
        => shipType != null &&
           Safe(() => shipType.canBuild, false) &&
           Safe(() => Ship.GetHull(player, shipType, true) != null, false);

    private static int SurfaceTypeOrder(string type)
        => type switch
        {
            "BB" => 0,
            "BC" => 1,
            "CA" => 2,
            "CL" => 3,
            "DD" => 4,
            "TB" => 5,
            _ => 99
        };

    internal static void NormalizeImportedSharedDesignDate(
        CampaignController? controller,
        Player? player,
        ShipType? shipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        Ship? result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled || result == null || !checkTech || isEarlySavedShip || !ShouldTrace(player))
            return;

        try
        {
            NormalizeImportedSharedDesignIdentity(player!, result, null);
            AttemptContext? attempt = ActiveAttemptFor(player, shipType, year);
            bool? prewarming = attempt?.Prewarming;
            int currentYear = Safe(() => (controller ?? CampaignController.Instance)!.CurrentDate.AsDate().Year, -1);
            int startYear = Safe(() => (controller ?? CampaignController.Instance)!.StartYear, -1);
            string requestedYearSource = prewarming == true ? "startYear" : "currentDate";
            Ship.Store? storeBefore = SafeToStore(result);

            if (Safe(() => result.isRefitDesign, false))
            {
                Log(
                    $"normalized-date skipped-refit nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} design={ShipLabel(result)} prewarming={BoolText(prewarming)} currentYear={currentYear} startYear={startYear} requestedYear={year} requestedYearSource={requestedYearSource} {ShipDateDetails(result, storeBefore)}.");
                return;
            }

            int beforeYear = ShipYear(result);
            int beforeCreatedTurn = Safe(() => result.dateCreated.turn, -1);
            int beforeFinishedTurn = Safe(() => result.dateFinished.turn, -1);
            int beforeRefitTurn = Safe(() => result.dateCreatedRefit.turn, -1);
            GameDate normalizedDate = GameDateForYear(controller, year);

            result.dateCreated = normalizedDate;
            result.dateFinished = normalizedDate;

            Log(
                $"normalized-date nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} design={ShipLabel(result)} prewarming={BoolText(prewarming)} currentYear={currentYear} startYear={startYear} requestedYear={year} requestedYearSource={requestedYearSource} storeBefore={StoreDateDetails(storeBefore)} beforeEffectiveYear={beforeYear} afterEffectiveYear={ShipYear(result)} dateCreatedTurn={beforeCreatedTurn}->{Safe(() => result.dateCreated.turn, -1)} dateFinishedTurn={beforeFinishedTurn}->{Safe(() => result.dateFinished.turn, -1)} dateCreatedRefitTurn={beforeRefitTurn}->{Safe(() => result.dateCreatedRefit.turn, -1)} isRefitDesign={Safe(() => result.isRefitDesign, false).ToString().ToLowerInvariant()}.");

            if (prewarming == true && currentYear > 0 && year > currentYear)
            {
                Log(
                    $"prewarm-date-forward nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} currentYear={currentYear} startYear={startYear} requestedYear={year} design={ShipLabel(result)} beforeEffectiveYear={beforeYear} afterEffectiveYear={ShipYear(result)}.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                "normalize:" + ex.GetType().Name,
                $"shared-design date normalization failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {year}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void TraceGetSharedDesignResult(Player? player, ShipType? shipType, int year, Ship? result)
    {
        if (!ShouldTrace(player))
            return;

        string selected = ShipSummary(result);
        Log(
            $"result nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} year={year} advancedAiBuilder={AdvancedAiBuilderLabel()} selected={selected}.");

        if (ActiveAttempts.Count <= 0)
            return;

        AttemptContext context = ActiveAttempts.Peek();
        if (context.PlayerPointer == PlayerPointer(player!) &&
            string.Equals(context.Type, NormalizeShipType(shipType), StringComparison.OrdinalIgnoreCase) &&
            context.Year == year)
        {
            context.SelectedDesign = selected;
        }
    }

    private static AttemptContext? ActiveAttemptFor(Player? player, ShipType? shipType, int year)
    {
        if (player == null || ActiveAttempts.Count <= 0)
            return null;

        long playerPointer = PlayerPointer(player);
        string type = NormalizeShipType(shipType);
        foreach (AttemptContext context in ActiveAttempts)
        {
            if (context.PlayerPointer == playerPointer &&
                context.Year == year &&
                string.Equals(context.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return context;
            }
        }

        return null;
    }

    internal static void ApplyRelaxedSharedDesignTechMatch(
        CampaignController? controller,
        Player? player,
        ShipType? shipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        SharedDesignBookSnapshot? existingDesigns,
        ref Ship? result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled ||
            !checkTech ||
            isEarlySavedShip ||
            !ShouldTrace(player))
        {
            return;
        }

        try
        {
            existingDesigns ??= CaptureExistingSharedDesigns(player, shipType);
            SharedDesignVariantMatch vanillaVariant = SharedDesignVariantMatch.None;
            if (result != null &&
                TryFindExistingSharedDesign(existingDesigns, result, null, out SharedDesignDuplicateMatch vanillaDuplicate, out vanillaVariant))
            {
                LogDuplicateSkip(player!, result, null, vanillaDuplicate);
                TryErase(result);
                result = null;
            }

            Ship? selected = FindRelaxedSharedDesignCandidate(controller, player!, shipType, year, existingDesigns);
            if (selected == null)
            {
                if (result != null && vanillaVariant.IsVariant)
                    ApplySharedDesignVariant(player!, result, null, existingDesigns, vanillaVariant);

                return;
            }

            Ship? previous = result;
            if (previous != null && IsEquivalentSharedDesign(previous, selected))
            {
                result = selected;
                Log(
                    $"selected-vp-equivalent-sanitized nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} year={year} replacedVanilla={ShipSummary(previous)} selected={ShipSummary(selected)} reason=option-sanitized window={SharedDesignWindowText()} order=yearDesc.");
                if (previous.Pointer != selected.Pointer)
                    TryErase(previous);
                return;
            }

            result = selected;
            if (previous != null && previous.Pointer != selected.Pointer)
            {
                Log(
                    $"selected-vp-candidate nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} year={year} replacedVanilla={ShipSummary(previous)} selected={ShipSummary(selected)} window={SharedDesignWindowText()} order=yearDesc.");
                TryErase(previous);
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                "relaxedTech:" + ex.GetType().Name,
                $"relaxed shared-design tech match failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {year}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static bool BlockDuplicateSharedDesignResult(
        Player? player,
        ShipType? shipType,
        int year,
        SharedDesignBookSnapshot? existingDesigns,
        ref Ship? result)
    {
        if (result == null || player == null || existingDesigns == null)
            return false;

        try
        {
            if (!TryFindExistingSharedDesign(existingDesigns, result, null, out SharedDesignDuplicateMatch duplicate))
                return false;

            string candidateSummary = ShipSummary(result);
            string existingSummary = ShipSummary(duplicate.Existing);
            LogDuplicateSkip(player, result, null, duplicate);
            int erased = EraseDuplicateSharedDesignCopies(player, result, existingDesigns);
            TryErase(result);
            result = null;

            Log(
                $"vanilla-duplicate-block nation={PlayerLabel(player)} type={NormalizeShipType(shipType)} year={year} candidate={candidateSummary} existing={existingSummary} reason={duplicate.Reason} erased={erased} fingerprintHash={FingerprintHash(duplicate.Fingerprint)} result=rejected.");
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce(
                "vanillaDuplicateBlock:" + ex.GetType().Name,
                $"shared-design vanilla duplicate guard failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {year}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static bool ApplyPostCleanupSharedDesignAcceptanceGate(
        Player? player,
        ShipType? shipType,
        int year,
        SharedDesignBookSnapshot? existingDesigns,
        Ship.Store? sourceStore,
        ref Ship? result)
    {
        if (result == null)
            return false;
        if (player == null || existingDesigns == null)
            return true;

        try
        {
            Ship candidate = result;
            string type = NormalizeShipType(candidate.shipType);
            string candidateFingerprint = SharedDesignFingerprint(candidate, sourceStore);
            Il2CppSystem.Guid sourceId = CandidateSharedDesignId(candidate, sourceStore);

            if (TryFindSnapshotDesign(existingDesigns, candidate, out SharedDesignExistingDesign existingSnapshot))
            {
                LogPostCleanupDuplicateBlock(
                    player,
                    type,
                    year,
                    candidate,
                    existingSnapshot.Ship,
                    "snapshotResult",
                    candidateFingerprint,
                    existingSnapshot.Fingerprint,
                    sourceId,
                    erased: 0);
                result = null;
                return false;
            }

            SharedDesignExistingDesign? sameBaseVariant = null;
            SharedDesignVariantMatch sourceVariant = SharedDesignVariantMatch.None;
            if (TryFindExistingSharedDesign(existingDesigns, candidate, sourceStore, out SharedDesignDuplicateMatch sharedDuplicate, out sourceVariant))
            {
                LogDuplicateSkip(player, candidate, sourceStore, sharedDuplicate);
                LogPostCleanupDuplicateBlock(
                    player,
                    type,
                    year,
                    candidate,
                    sharedDuplicate.Existing,
                    "finalFingerprint",
                    candidateFingerprint,
                    sharedDuplicate.Fingerprint,
                    sourceId,
                    erased: 1);
                TryErase(candidate);
                result = null;
                return false;
            }

            foreach (SharedDesignExistingDesign existing in existingDesigns.Designs)
            {
                if (!CanComparePostCleanupRosterDesign(existing, candidate, type))
                    continue;

                string existingFingerprint = existing.Fingerprint;
                bool sameFingerprint = string.Equals(existingFingerprint, candidateFingerprint, StringComparison.Ordinal);
                bool sameBaseName = string.Equals(
                    SharedDesignBaseName(ShipLabel(existing.Ship)),
                    SharedDesignBaseName(ShipLabel(candidate)),
                    StringComparison.OrdinalIgnoreCase);

                if (sameFingerprint)
                {
                    string reason = sameBaseName ? "sameVisibleFamily" : "finalFingerprint";
                    TryErase(candidate);
                    result = null;
                    LogPostCleanupDuplicateBlock(
                        player,
                        type,
                        year,
                        candidate,
                        existing.Ship,
                        reason,
                        candidateFingerprint,
                        existingFingerprint,
                        sourceId,
                        erased: 1);
                    return false;
                }

                if (sameBaseName && sameBaseVariant == null)
                    sameBaseVariant = existing;
            }

            if (!CandidatePassesTop3SharedDesignGate(player, candidate, year, existingDesigns))
            {
                TryErase(candidate);
                result = null;
                return false;
            }

            if (sameBaseVariant != null)
            {
                ApplyPostCleanupSharedDesignVariantName(
                    player,
                    candidate,
                    sourceStore,
                    existingDesigns,
                    "sameBaseMaterialVariant",
                    sourceId,
                    sameBaseVariant.Fingerprint,
                    candidateFingerprint);
            }
            else if (sourceVariant.IsVariant)
            {
                ApplyPostCleanupSharedDesignVariantName(
                    player,
                    candidate,
                    sourceStore,
                    existingDesigns,
                    "sameSourceMaterialVariant",
                    sourceVariant.SourceId,
                    sourceVariant.ExistingFingerprint,
                    candidateFingerprint);
            }
            else
            {
                NormalizeImportedSharedDesignIdentity(player, candidate, sourceStore);
            }

            result = candidate;
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce(
                "postCleanupSharedGate:" + ex.GetType().Name,
                $"post-cleanup shared-design acceptance gate failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {year}. {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static bool CanComparePostCleanupRosterDesign(SharedDesignExistingDesign existing, Ship candidate, string candidateType)
    {
        if (existing.Ship == null ||
            ReferenceEquals(existing.Ship, candidate) ||
            Safe(() => existing.Ship.isErased, true))
        {
            return false;
        }

        long candidatePointer = Safe(() => candidate.Pointer.ToInt64(), 0L);
        if (candidatePointer != 0L && existing.Pointer == candidatePointer)
            return false;

        return string.Equals(existing.Type, candidateType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindSnapshotDesign(SharedDesignBookSnapshot existingDesigns, Ship candidate, out SharedDesignExistingDesign snapshot)
    {
        long candidatePointer = Safe(() => candidate.Pointer.ToInt64(), 0L);
        foreach (SharedDesignExistingDesign existing in existingDesigns.Designs)
        {
            if (existing.Ship == null)
                continue;

            if (ReferenceEquals(existing.Ship, candidate) ||
                (candidatePointer != 0L && existing.Pointer == candidatePointer))
            {
                snapshot = existing;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private static bool CandidatePassesTop3SharedDesignGate(
        Player player,
        Ship candidate,
        int year,
        SharedDesignBookSnapshot existingDesigns)
    {
        string type = NormalizeShipType(candidate.shipType);
        List<CampaignAiDesignRosterPrunePatch.RosterDesign> existing = existingDesigns.Designs
            .Where(existing => existing.Ship != null &&
                               !Safe(() => existing.Ship.isErased, true) &&
                               string.Equals(existing.Type, type, StringComparison.OrdinalIgnoreCase) &&
                               Safe(() => existing.Ship.isDesign || existing.Ship.isRefitDesign, false))
            .Select(existing => CampaignAiDesignRosterPrunePatch.BuildRosterDesign(existing.Ship))
            .ToList();

        CampaignAiDesignRosterPrunePatch.RosterDesign candidateRank = CampaignAiDesignRosterPrunePatch.BuildRosterDesign(candidate);
        List<CampaignAiDesignRosterPrunePatch.RosterDesign> currentTop = CampaignAiDesignRosterPrunePatch
            .RankDesigns(existing)
            .Take(CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
            .ToList();

        if (existing.Count < CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
        {
            LogTop3SharedDesignAccept(player, type, year, candidateRank, "none", currentTop);
            return true;
        }

        List<CampaignAiDesignRosterPrunePatch.RosterDesign> combinedTop = CampaignAiDesignRosterPrunePatch
            .RankDesigns(existing.Append(candidateRank))
            .Take(CampaignAiDesignRosterPrunePatch.MaxDesignsPerType)
            .ToList();
        bool candidateInTop = combinedTop.Any(entry => ReferenceEquals(entry.Ship, candidate));
        CampaignAiDesignRosterPrunePatch.RosterDesign worstCurrent = currentTop.LastOrDefault();
        if (!candidateInTop)
        {
            Log(
                $"top3-shared-design-reject nation={PlayerLabel(player)} type={type} year={year} candidate={ShipLabel(candidate)} candidateAdjustedPower={ShipEffectivePowerCalculator.FormatCompactPower(candidateRank.AdjustedPower)} worstTop3={LogToken(worstCurrent.Name)} worstAdjustedPower={ShipEffectivePowerCalculator.FormatCompactPower(worstCurrent.AdjustedPower)} keptTop3={FormatTop3Roster(currentTop)}.");
            return false;
        }

        LogTop3SharedDesignAccept(player, type, year, candidateRank, CampaignAiDesignRosterPrunePatch.FormatRosterDesignCompact(worstCurrent), combinedTop);
        return true;
    }

    private static void LogTop3SharedDesignAccept(
        Player player,
        string type,
        int year,
        CampaignAiDesignRosterPrunePatch.RosterDesign candidate,
        string displaces,
        IReadOnlyList<CampaignAiDesignRosterPrunePatch.RosterDesign> keptTop3)
    {
        Log(
            $"top3-shared-design-accept nation={PlayerLabel(player)} type={type} year={year} candidate={candidate.Name} candidateAdjustedPower={ShipEffectivePowerCalculator.FormatCompactPower(candidate.AdjustedPower)} displaces={displaces} keptTop3={FormatTop3Roster(keptTop3)}.");
    }

    private static string FormatTop3Roster(IReadOnlyList<CampaignAiDesignRosterPrunePatch.RosterDesign> designs)
        => designs.Count == 0
            ? "none"
            : string.Join(",", designs.Select(CampaignAiDesignRosterPrunePatch.FormatRosterDesignCompact));

    private static void LogPostCleanupDuplicateBlock(
        Player player,
        string type,
        int year,
        Ship candidate,
        Ship? existing,
        string reason,
        string candidateFingerprint,
        string existingFingerprint,
        Il2CppSystem.Guid sourceId,
        int erased)
    {
        Log(
            $"post-cleanup-duplicate-block nation={PlayerLabel(player)} type={type} year={year} candidate={ShipLabel(candidate)} existing={ShipLabel(existing)} reason={reason} candidateFingerprintHash={FingerprintHash(candidateFingerprint)} existingFingerprintHash={FingerprintHash(existingFingerprint)} sourceId={GuidText(sourceId)} erased={erased}.");
    }

    private static void ApplyPostCleanupSharedDesignVariantName(
        Player player,
        Ship candidate,
        Ship.Store? sourceStore,
        SharedDesignBookSnapshot existingDesigns,
        string reason,
        Il2CppSystem.Guid sourceId,
        string existingFingerprint,
        string candidateFingerprint)
    {
        string baseName = SharedDesignBaseName(ShipLabel(candidate));
        string variantName = ApplySharedDesignVariantName(player, candidate, existingDesigns, baseName);
        NormalizeImportedSharedDesignIdentity(player, candidate, sourceStore, forceNewId: true, reason: reason);
        Log(
            $"post-cleanup-variant-name nation={PlayerLabel(player)} type={NormalizeShipType(candidate.shipType)} base=\"{baseName}\" variant=\"{variantName}\" reason={reason} sourceId={GuidText(sourceId)} existingFingerprintHash={FingerprintHash(existingFingerprint)} candidateFingerprintHash={FingerprintHash(candidateFingerprint)}.");
    }

    private static int EraseDuplicateSharedDesignCopies(Player player, Ship duplicateResult, SharedDesignBookSnapshot existingDesigns)
    {
        string duplicateFingerprint = SharedDesignFingerprint(duplicateResult, null);
        string duplicateType = NormalizeShipType(duplicateResult.shipType);
        int erased = 0;

        foreach (Ship design in SafeShipList(player.designs))
        {
            if (design == null ||
                Safe(() => design.isErased, false) ||
                IsDesignFromSnapshot(existingDesigns, design) ||
                !string.Equals(NormalizeShipType(design.shipType), duplicateType, StringComparison.OrdinalIgnoreCase) ||
                !IsSharedDesign(design))
            {
                continue;
            }

            Ship.Store? store = SafeToStore(design);
            string fingerprint = SharedDesignFingerprint(design, store);
            if (!string.Equals(fingerprint, duplicateFingerprint, StringComparison.Ordinal))
                continue;

            TryErase(design);
            erased++;
        }

        return erased;
    }

    private static bool IsDesignFromSnapshot(SharedDesignBookSnapshot existingDesigns, Ship design)
    {
        long pointer = Safe(() => design.Pointer.ToInt64(), 0L);
        foreach (SharedDesignExistingDesign existing in existingDesigns.Designs)
        {
            if (ReferenceEquals(existing.Ship, design) ||
                (pointer != 0L && existing.Pointer == pointer))
            {
                return true;
            }
        }

        return false;
    }

    internal static void FinalizeAcceptedSharedDesignBlueprint(
        Player? player,
        ShipType? shipType,
        bool checkTech,
        bool isEarlySavedShip,
        Ship? result)
    {
        if (!checkTech ||
            isEarlySavedShip ||
            result == null ||
            !ShouldTrace(player))
        {
            return;
        }

        try
        {
            if (ModSettings.AdvancedAiBuilderEnabled)
            {
                ApplySharedDesignOptionSanitizer(result, null, player!);
                ApplySharedDesignSafeDowngrades(result, null, player!);
                SanitizeSharedDesignTechs(result, null, player!);
                ApplySharedDesignGunLengthClamp(result, null, player!);
            }

            ApplyFinalSharedDesignTorpedoSanitizer(result, player!);
            MajorShipTorpedoCleanup.Audit(
                result,
                player!,
                "shared-accepted-post-cleanup",
                CurrentTurnLabel(),
                requireAiNonMain: false,
                logClean: true);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "finalBlueprintSanitize:" + ex.GetType().Name,
                $"final shared-design blueprint cleanup failed for {PlayerLabel(player)} {NormalizeShipType(shipType)} {ShipLabel(result)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyFinalSharedDesignTorpedoSanitizer(Ship ship, Player player)
    {
        MajorShipTorpedoCleanupResult cleanup = MajorShipTorpedoCleanup.Cleanup(ship, player, requireAiNonMain: false);
        if (!cleanup.Applied)
            return;

        int techsPruned = PruneRemovedEquipmentTechs(ship, cleanup.RemovedTokens, player);
        RecalculateSharedDesignCandidate(ship, player);

        bool canBuild = CanBuildSharedCandidate(ship, player, "SharedDesignFinalTorpedoSanitize", out string buildReason);
        bool clearLive = cleanup.LivePartsAfter <= 0 && cleanup.CacheAfter <= 0 && !cleanup.HaveTorpedoesAfter;
        bool clearStore = cleanup.StoreNameTubes == 0 || cleanup.StoreNameTubes < 0;
        bool clearReload = cleanup.ReloadTubes == 0 || cleanup.ReloadTubes < 0;
        string result = clearLive && clearStore && clearReload && canBuild ? "accepted" :
            clearLive && clearStore && clearReload ? "post-cleanup-invalid" :
            "remaining-torpedoes";

        Log(
            $"SharedDesign final-torpedo-sanitize nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" reason={MajorShipTorpedoCleanup.LogToken(cleanup.Reason)} beforeTubes={cleanup.LivePartsBefore + cleanup.CacheBefore} afterTubes={cleanup.LivePartsAfter + cleanup.CacheAfter} livePartsBefore={cleanup.LivePartsBefore} livePartsAfter={cleanup.LivePartsAfter} cacheBefore={cleanup.CacheBefore} cacheAfter={cleanup.CacheAfter} haveTorpedoesBefore={BoolText(cleanup.HaveTorpedoesBefore)} haveTorpedoesAfter={BoolText(cleanup.HaveTorpedoesAfter)} torpedoesAllBefore={cleanup.TorpedoesAllBefore} torpedoesAllAfter={cleanup.TorpedoesAllAfter} weaponCacheRefresh={BoolText(cleanup.WeaponCacheRefreshOk)} removedLaunchers={cleanup.RemovedLaunchers} removedByRemovePart={cleanup.RemovedByRemovePart} removedStaleCache={cleanup.RemovedStaleCache} removedSupport={cleanup.RemovedSupportComponents} removedComponents={cleanup.RemovedComponentsText} techsPruned={techsPruned} storeNameTubes={MajorShipTorpedoCleanup.TubeCountText(cleanup.StoreNameTubes)} reloadTubes={MajorShipTorpedoCleanup.TubeCountText(cleanup.ReloadTubes)} buildValid={BoolText(canBuild)} buildReason={LogToken(buildReason)} result={result}.");
    }

    private static CandidateSummary AnalyzeCandidates(
        CampaignController? controller,
        Player player,
        ShipType? requestedShipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        SharedDesignBookSnapshot existingDesigns)
    {
        CandidateSummary summary = new();
        List<SharedDesignCandidate> candidates = SharedDesignCandidates(player, requestedShipType, year, out int total, out int typeMatch);
        summary.Total = total;
        summary.TypeMatch = typeMatch;
        summary.YearMatch = candidates.Count;

        foreach (SharedDesignCandidate candidate in candidates)
        {
            Ship.Store store = candidate.Store;
            Ship? ship = null;
            try
            {
                ship = Ship.Create(null, null, false, false, false);
                var emptyGuid = new Il2CppSystem.Nullable<Il2CppSystem.Guid>();
                if (ship == null || !ship.FromStore(store, emptyGuid, null, null, false))
                {
                    summary.AddReject(StoreSummary(store), "fromStore", "failed");
                    TryErase(ship);
                    continue;
                }

                summary.FromStore++;
                TrySetInactive(ship);
                if (ModSettings.AdvancedAiBuilderEnabled)
                {
                    ApplySharedDesignOptionSanitizer(ship, store, player);
                    ApplySharedDesignSafeDowngrades(ship, store, player);
                    SanitizeSharedDesignTechs(ship, store, player);
                    ApplySharedDesignGunLengthClamp(ship, store, player);
                }

                SharedDesignValidationResult validation = ValidateSharedDesignCandidate(
                    controller,
                    ship,
                    store,
                    player,
                    checkTech,
                    isEarlySavedShip,
                    logRelaxedPass: false);
                if (!validation.Accepted)
                {
                    if (string.Equals(validation.Stage, "build", StringComparison.OrdinalIgnoreCase))
                    {
                        summary.BuildReject++;
                        summary.AddBuildReason(validation.BuildReason);
                    }
                    else if (string.Equals(validation.Stage, "tech", StringComparison.OrdinalIgnoreCase))
                    {
                        summary.TechReject++;
                    }

                    summary.AddReject(ShipSummary(ship, store, player), validation.Stage, validation.Reason);
                    LogTechRejectDetail(player, requestedShipType, ship, store, checkTech, validation.Stage, validation.Reason, validation.BuildReason);
                    TryErase(ship);
                    continue;
                }

                if (TryFindExistingSharedDesign(existingDesigns, ship, store, out SharedDesignDuplicateMatch duplicate))
                {
                    summary.DuplicateReject++;
                    summary.AddReject(ShipSummary(ship, store, player), "duplicate", duplicate.Reason);
                    TryErase(ship);
                    continue;
                }

                summary.Accepted++;
                summary.AddAccepted(ShipSummary(ship, store, player) + validation.AcceptDetail);
                TryErase(ship);
            }
            catch (Exception ex)
            {
                summary.AddReject(StoreSummary(store), "error", ex.GetType().Name);
                TryErase(ship);
            }
        }

        return summary;
    }

    private static void LogTechRejectDetail(
        Player player,
        ShipType? requestedShipType,
        Ship? ship,
        Ship.Store? store,
        bool checkTech,
        string stage,
        string reason,
        string buildReason = "none")
    {
        if (!string.Equals(stage, "tech", StringComparison.OrdinalIgnoreCase))
            return;

        string liveName = ShipLabel(ship);
        string storeName = SafeString(() => store?.vesselName);
        string type = CandidateTypeLabel(requestedShipType, ship, store);
        string details = ship == null
            ? StoreSummary(store)
            : $"{ShipDateDetails(ship, store)} {BuildNumericDetails(ship, player)}";
        int effectiveYear = ship == null ? Safe(() => store?.YearCreated ?? 0, 0) : ShipYear(ship);

        LogDebug(
            $"tech-reject nation={PlayerLabel(player)} key={NationKey(player)} type={type} design=\"{liveName}\" storeDesign=\"{storeName}\" effectiveYear={effectiveYear} checkTech={checkTech.ToString().ToLowerInvariant()} stage={LogToken(stage)} buildReason={LogToken(buildReason)} reason={FormatRejectReason(reason)} {details}.");
    }

    private static string CandidateTypeLabel(ShipType? requestedShipType, Ship? ship, Ship.Store? store)
    {
        string shipType = NormalizeShipType(Safe(() => ship?.shipType, null));
        if (!string.IsNullOrWhiteSpace(shipType) &&
            !string.Equals(shipType, "<empty>", StringComparison.Ordinal) &&
            !string.Equals(shipType, "?", StringComparison.Ordinal))
        {
            return shipType;
        }

        string requested = NormalizeShipType(requestedShipType);
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(requested, "<empty>", StringComparison.Ordinal) &&
            !string.Equals(requested, "?", StringComparison.Ordinal))
        {
            return requested;
        }

        return NormalizeShipType(SafeString(() => store?.shipType));
    }

    private static Ship? FindRelaxedSharedDesignCandidate(
        CampaignController? controller,
        Player player,
        ShipType? requestedShipType,
        int year,
        SharedDesignBookSnapshot existingDesigns)
    {
        List<SharedDesignCandidate> candidates = SharedDesignCandidates(player, requestedShipType, year, out _, out _);
        foreach (SharedDesignCandidate candidate in candidates)
        {
            Ship.Store store = candidate.Store;
            Ship? ship = null;
            try
            {
                ship = Ship.Create(null, null, false, false, false);
                var emptyGuid = new Il2CppSystem.Nullable<Il2CppSystem.Guid>();
                if (ship == null || !ship.FromStore(store, emptyGuid, null, null, false))
                {
                    TryErase(ship);
                    continue;
                }

                TrySetInactive(ship);
                ApplySharedDesignOptionSanitizer(ship, store, player);
                ApplySharedDesignSafeDowngrades(ship, store, player);
                SanitizeSharedDesignTechs(ship, store, player);
                ApplySharedDesignGunLengthClamp(ship, store, player);
                SharedDesignValidationResult validation = ValidateSharedDesignCandidate(
                    controller,
                    ship,
                    store,
                    player,
                    checkTech: true,
                    isEarlySavedShip: false,
                    logRelaxedPass: true);
                if (!validation.Accepted)
                {
                    TryErase(ship);
                    continue;
                }

                Ship? gatedShip = ship;
                if (!ApplyPostCleanupSharedDesignAcceptanceGate(player, requestedShipType, year, existingDesigns, store, ref gatedShip))
                    continue;

                return gatedShip;
            }
            catch
            {
                TryErase(ship);
            }
        }

        return null;
    }

    private static List<SharedDesignCandidate> SharedDesignCandidates(Player player, ShipType? requestedShipType, int year, out int total, out int typeMatch)
    {
        total = 0;
        typeMatch = 0;
        List<SharedDesignCandidate> candidates = new();
        string nationKey = NationKey(player);
        string requestedName = SafeString(() => requestedShipType?.name);

        var sharedDesigns = G.GameData?.sharedDesignsPerNation;
        if (sharedDesigns == null ||
            string.Equals(nationKey, "<empty>", StringComparison.Ordinal) ||
            !sharedDesigns.TryGetValue(nationKey, out var designs) ||
            designs == null)
        {
            return candidates;
        }

        foreach (var tuple in designs)
        {
            Ship.Store? store = tuple.Item1;
            if (store == null)
                continue;

            total++;
            if (!string.Equals(SafeString(() => store.shipType), requestedName, StringComparison.Ordinal))
                continue;

            typeMatch++;
            int createdYear = Safe(() => store.YearCreated, 0);
            if (!IsSharedDesignCandidateYear(createdYear, year))
                continue;

            candidates.Add(new SharedDesignCandidate(
                store,
                createdYear,
                SafeString(() => store.vesselName)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Year)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static SharedDesignBookSnapshot CaptureExistingSharedDesigns(Player? player, ShipType? requestedShipType)
        => SharedDesignBookSnapshot.Capture(player, requestedShipType);

    private static bool TryFindExistingSharedDesign(
        SharedDesignBookSnapshot existingDesigns,
        Ship candidate,
        Ship.Store? store,
        out SharedDesignDuplicateMatch duplicate)
        => TryFindExistingSharedDesign(existingDesigns, candidate, store, out duplicate, out _);

    private static bool TryFindExistingSharedDesign(
        SharedDesignBookSnapshot existingDesigns,
        Ship candidate,
        Ship.Store? store,
        out SharedDesignDuplicateMatch duplicate,
        out SharedDesignVariantMatch variant)
    {
        duplicate = SharedDesignDuplicateMatch.None;
        variant = SharedDesignVariantMatch.None;
        if (candidate == null)
            return false;

        Il2CppSystem.Guid candidateId = CandidateSharedDesignId(candidate, store);
        bool hasCandidateId = !IsEmptyGuid(candidateId);
        string candidateFingerprint = SharedDesignFingerprint(candidate, store);
        long candidatePointer = Safe(() => candidate.Pointer.ToInt64(), 0L);

        foreach (SharedDesignExistingDesign existing in existingDesigns.Designs)
        {
            if (existing.Ship == null ||
                ReferenceEquals(existing.Ship, candidate) ||
                (candidatePointer != 0L && existing.Pointer == candidatePointer))
            {
                continue;
            }

            if (!string.Equals(existing.Type, NormalizeShipType(candidate.shipType), StringComparison.OrdinalIgnoreCase))
                continue;

            if (!existing.IsShared)
                continue;

            bool sameSourceId = hasCandidateId && !IsEmptyGuid(existing.Id) && Safe(() => existing.Id == candidateId, false);
            bool sameFingerprint = string.Equals(existing.Fingerprint, candidateFingerprint, StringComparison.Ordinal);
            if (sameFingerprint)
            {
                duplicate = new SharedDesignDuplicateMatch(existing.Ship, sameSourceId ? "id+fingerprint" : "fingerprint", candidateFingerprint);
                return true;
            }

            if (sameSourceId && !variant.IsVariant)
                variant = new SharedDesignVariantMatch(existing.Ship, existing.Fingerprint, candidateFingerprint, candidateId);
        }

        return false;
    }

    private static bool IsEquivalentSharedDesign(Ship a, Ship b)
    {
        if (a == null || b == null)
            return false;

        long aPointer = Safe(() => a.Pointer.ToInt64(), 0L);
        long bPointer = Safe(() => b.Pointer.ToInt64(), 0L);
        if (aPointer != 0L && aPointer == bPointer)
            return true;

        Ship.Store? aStore = SafeToStore(a);
        Ship.Store? bStore = SafeToStore(b);
        string aFingerprint = SharedDesignFingerprint(a, aStore);
        string bFingerprint = SharedDesignFingerprint(b, bStore);
        Il2CppSystem.Guid aId = CandidateSharedDesignId(a, aStore);
        Il2CppSystem.Guid bId = CandidateSharedDesignId(b, bStore);
        if (!IsEmptyGuid(aId) && !IsEmptyGuid(bId) && Safe(() => aId == bId, false))
            return string.Equals(aFingerprint, bFingerprint, StringComparison.Ordinal);

        return string.Equals(aFingerprint, bFingerprint, StringComparison.Ordinal);
    }

    private static void LogDuplicateSkip(Player player, Ship candidate, Ship.Store? store, SharedDesignDuplicateMatch duplicate)
    {
        Log(
            $"duplicate-skip nation={PlayerLabel(player)} type={NormalizeShipType(candidate.shipType)} candidate={ShipLabel(candidate)} existing={ShipLabel(duplicate.Existing)} reason={duplicate.Reason} candidateId={GuidText(CandidateSharedDesignId(candidate, store))} existingId={GuidText(Safe(() => duplicate.Existing?.id ?? Il2CppSystem.Guid.Empty, Il2CppSystem.Guid.Empty))} fingerprintHash={FingerprintHash(duplicate.Fingerprint)}.");
    }

    private static void ApplySharedDesignVariant(
        Player player,
        Ship result,
        Ship.Store? sourceStore,
        SharedDesignBookSnapshot existingDesigns,
        SharedDesignVariantMatch variant)
    {
        string baseName = SharedDesignBaseName(ShipLabel(result));
        string variantName = ApplySharedDesignVariantName(player, result, existingDesigns, baseName);
        NormalizeImportedSharedDesignIdentity(player, result, sourceStore, forceNewId: true, reason: "sameSourceVariant");
        Log(
            $"variant-allowed nation={PlayerLabel(player)} type={NormalizeShipType(result.shipType)} base=\"{baseName}\" variant=\"{variantName}\" sourceId={GuidText(variant.SourceId)} existingFingerprint={FingerprintHash(variant.ExistingFingerprint)} candidateFingerprint={FingerprintHash(variant.CandidateFingerprint)} reason=sameSourceDifferentFingerprint.");
    }

    private static string ApplySharedDesignVariantName(Player player, Ship ship, SharedDesignBookSnapshot existingDesigns, string baseName)
    {
        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
        int maxIndex = 1;
        string type = NormalizeShipType(ship.shipType);
        foreach (SharedDesignExistingDesign existing in existingDesigns.Designs)
        {
            if (existing.Ship == null || !string.Equals(existing.Type, type, StringComparison.OrdinalIgnoreCase))
                continue;

            string existingName = ShipLabel(existing.Ship);
            usedNames.Add(existingName);
            if (!string.Equals(SharedDesignBaseName(existingName), baseName, StringComparison.OrdinalIgnoreCase))
                continue;

            maxIndex = Math.Max(maxIndex, SharedDesignVariantIndex(existingName));
        }

        string variantName;
        int index = Math.Max(2, maxIndex + 1);
        do
        {
            variantName = $"{baseName} Mk {RomanNumeral(index)}";
            index++;
        } while (usedNames.Contains(variantName));

        Safe(() =>
        {
            ship.SetShipName(variantName);
            ship.vesselName = variantName;
            return true;
        }, false);

        return ShipLabel(ship);
    }

    private static string SharedDesignBaseName(string? name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "Shared Design" : name.Trim();
        int marker = trimmed.LastIndexOf(" Mk ", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return trimmed;

        string suffix = trimmed[(marker + 4)..].Trim();
        if (SharedDesignVariantSuffixIndex(suffix) <= 1)
            return trimmed;

        return trimmed[..marker].Trim();
    }

    private static int SharedDesignVariantIndex(string? name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        int marker = trimmed.LastIndexOf(" Mk ", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return 1;

        return Math.Max(1, SharedDesignVariantSuffixIndex(trimmed[(marker + 4)..].Trim()));
    }

    private static int SharedDesignVariantSuffixIndex(string suffix)
    {
        if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return numeric;

        return RomanNumeralValue(suffix);
    }

    private static int RomanNumeralValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        Dictionary<char, int> values = new()
        {
            ['I'] = 1,
            ['V'] = 5,
            ['X'] = 10,
            ['L'] = 50
        };
        int total = 0;
        int previous = 0;
        foreach (char raw in value.Trim().ToUpperInvariant().Reverse())
        {
            if (!values.TryGetValue(raw, out int current))
                return 0;

            total += current < previous ? -current : current;
            previous = Math.Max(previous, current);
        }

        return total;
    }

    private static string RomanNumeral(int value)
    {
        if (value <= 0)
            return value.ToString(CultureInfo.InvariantCulture);

        if (value > 50)
            return value.ToString(CultureInfo.InvariantCulture);

        (int Value, string Token)[] numerals =
        {
            (50, "L"),
            (40, "XL"),
            (10, "X"),
            (9, "IX"),
            (5, "V"),
            (4, "IV"),
            (1, "I")
        };

        string result = string.Empty;
        int remaining = value;
        foreach ((int numeralValue, string token) in numerals)
        {
            while (remaining >= numeralValue)
            {
                result += token;
                remaining -= numeralValue;
            }
        }

        return result;
    }

    private static void NormalizeImportedSharedDesignIdentity(Player player, Ship result, Ship.Store? sourceStore, bool forceNewId = false, string reason = "default")
    {
        if (result == null)
            return;

        try
        {
            Il2CppSystem.Guid oldId = Safe(() => result.id, Il2CppSystem.Guid.Empty);
            bool assignedId = false;
            if (forceNewId || IsEmptyGuid(oldId))
            {
                result.id = Il2CppSystem.Guid.NewGuid();
                assignedId = true;
            }

            bool wasShared = IsSharedDesign(result);
            bool setShared = false;
            if (!wasShared)
                setShared = TrySetBoolMember(result, "IsSharedDesign", true) ||
                            TrySetBoolMember(result, "isSharedDesign", true) ||
                            TrySetBoolMember(result, "isShared", true);

            if (!assignedId && !setShared)
                return;

            Log(
                $"normalized-identity nation={PlayerLabel(player)} type={NormalizeShipType(result.shipType)} design={ShipLabel(result)} oldId={GuidText(oldId)} newId={GuidText(Safe(() => result.id, Il2CppSystem.Guid.Empty))} sourceStoreId={GuidText(Safe(() => sourceStore?.id ?? Il2CppSystem.Guid.Empty, Il2CppSystem.Guid.Empty))} shared={wasShared.ToString().ToLowerInvariant()}->{IsSharedDesign(result).ToString().ToLowerInvariant()} reason={reason} store={StoreIdentityDetails(SafeToStore(result))}.");
        }
        catch (Exception ex)
        {
            WarnOnce(
                "normalizeIdentity:" + ex.GetType().Name,
                $"shared-design identity normalization failed for {PlayerLabel(player)} {NormalizeShipType(result.shipType)} {ShipLabel(result)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Il2CppSystem.Guid CandidateSharedDesignId(Ship candidate, Ship.Store? store)
    {
        Il2CppSystem.Guid storeId = Safe(() => store?.id ?? Il2CppSystem.Guid.Empty, Il2CppSystem.Guid.Empty);
        if (!IsEmptyGuid(storeId))
            return storeId;

        return Safe(() => candidate.id, Il2CppSystem.Guid.Empty);
    }

    private static bool HasSharedDesignId(Ship existing, Il2CppSystem.Guid id)
    {
        if (IsEmptyGuid(id))
            return false;

        if (Safe(() => existing.id == id, false))
            return true;

        Ship.Store? store = SafeToStore(existing);
        return store != null && Safe(() => store.id == id, false);
    }

    private static Ship.Store? SafeToStore(Ship? ship)
    {
        if (ship == null)
            return null;

        return Safe(() => ship.ToStore(false), null);
    }

    private static string StoreIdentityDetails(Ship.Store? store)
    {
        if (store == null)
            return "store=<null>";

        return $"storeId={GuidText(Safe(() => store.id, Il2CppSystem.Guid.Empty))},storeDesignId={GuidText(Safe(() => store.designId, Il2CppSystem.Guid.Empty))},storeShared={Safe(() => store.isSharedDesign, false).ToString().ToLowerInvariant()}";
    }

    private static string SharedDesignFingerprint(Ship ship, Ship.Store? store)
    {
        string type = NormalizeShipType(ship.shipType);
        float tons = Safe(() => ship.Tonnage(), Safe(() => store?.RealTonnage() ?? 0f, 0f));
        float tonBucket = (float)Math.Round(tons / 10f, MidpointRounding.AwayFromZero) * 10f;
        string hull = FingerprintToken(SafeString(() => ship.hull?.data?.name));
        if (string.Equals(hull, FingerprintToken("<empty>"), StringComparison.Ordinal) ||
            string.Equals(hull, "empty", StringComparison.Ordinal))
            hull = FingerprintToken(SafeString(() => store?.hullName));

        string components = FingerprintList(SharedDesignComponentTokens(ship, store));
        string parts = FingerprintList(SharedDesignPartTokens(ship));
        string guns = FingerprintList(SharedDesignGunTokens(ship));
        string armor = FingerprintList(SharedDesignArmorTokens(ship, store));
        return $"{type}|t{Fmt(tonBucket)}|h{hull}|c{components}|p{parts}|g{guns}|a{armor}";
    }

    private static IEnumerable<string> SharedDesignComponentTokens(Ship ship, Ship.Store? store)
    {
        List<string> tokens = new();
        try
        {
            if (ship.components != null)
            {
                foreach (var pair in ship.components)
                    tokens.Add($"{SafeString(() => pair.Key.ToString())}={ComponentToken(pair.Value)}");
            }
        }
        catch
        {
        }

        if (tokens.Count == 0)
        {
            try
            {
                if (store?.components != null)
                {
                    foreach (var pair in store.components)
                        tokens.Add($"{SafeString(() => pair.Key)}={SafeString(() => pair.Value)}");
                }
            }
            catch
            {
            }
        }

        return tokens;
    }

    private static IEnumerable<string> SharedDesignPartTokens(Ship ship)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ship.parts == null)
                return counts.Select(pair => $"{pair.Key}x{pair.Value}");

            foreach (Part part in ship.parts)
            {
                PartData? data = Safe(() => part?.data, null);
                string token = SafeString(() => data?.name);
                if (string.Equals(token, "<empty>", StringComparison.Ordinal))
                    token = SafeString(() => data?.model);
                token = FingerprintToken(token);
                if (string.IsNullOrWhiteSpace(token) || string.Equals(token, "empty", StringComparison.Ordinal))
                    continue;

                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }
        catch
        {
        }

        return counts.Select(pair => $"{pair.Key}x{pair.Value}");
    }

    private static IEnumerable<string> SharedDesignGunTokens(Ship ship)
    {
        List<string> tokens = new();
        try
        {
            var guns = ship.shipGunCaliber;
            if (guns == null)
                return tokens;

            foreach (Ship.TurretCaliber caliber in guns)
            {
                if (caliber == null)
                    continue;

                PartData? part = Safe(() => caliber.turretPartData, null);
                string partToken = FingerprintToken(SafeString(() => part?.name));
                if (string.Equals(partToken, "empty", StringComparison.Ordinal))
                    partToken = FingerprintToken(SafeString(() => part?.model));
                string category = GunLengthCategoryLabel(GunLengthCategoryFor(caliber));
                float diameter = Safe(() => caliber.diameter, 0f);
                int length = Safe(() => caliber.length, 0);
                bool casemate = Safe(() => caliber.isCasemateGun, false);
                tokens.Add($"{category}:{partToken}:d{diameter.ToString("0.##", CultureInfo.InvariantCulture)}:l{length}:casemate{casemate.ToString().ToLowerInvariant()}");
            }
        }
        catch
        {
        }

        return tokens;
    }

    private static IEnumerable<string> SharedDesignArmorTokens(Ship ship, Ship.Store? store)
    {
        Dictionary<string, float> values = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ship.armor != null)
            {
                foreach (var pair in ship.armor)
                    values[SafeString(() => pair.Key.ToString())] = pair.Value;
            }
        }
        catch
        {
        }

        if (values.Count == 0)
        {
            try
            {
                if (store?.armor != null)
                {
                    foreach (var pair in store.armor)
                        values[SafeString(() => pair.Key.ToString())] = pair.Value;
                }
            }
            catch
            {
            }
        }

        return values.Select(pair => $"{FingerprintToken(pair.Key)}={Math.Round(pair.Value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)}");
    }

    private static string FingerprintList(IEnumerable<string> values)
    {
        List<string> normalized = values
            .Select(FingerprintToken)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "empty", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        return normalized.Count == 0 ? "none" : string.Join(",", normalized);
    }

    private static string FingerprintToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "<empty>", StringComparison.Ordinal))
            return "empty";

        string token = value.Trim().ToLowerInvariant();
        char[] chars = token
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static bool IsSharedDesign(Ship? ship)
        => ship != null && Safe(() => ReadBoolMember(ship, "IsSharedDesign") ||
                                     ReadBoolMember(ship, "isSharedDesign") ||
                                     ReadBoolMember(ship, "isShared"), false);

    private static bool ReadBoolMember(object target, string memberName)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(target) is bool propertyValue)
            return propertyValue;

        FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(target) is bool fieldValue && fieldValue;
    }

    private static bool TrySetBoolMember(object target, string memberName, bool value)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? setter = property?.GetSetMethod(true);
            if (property != null && setter != null)
            {
                property.SetValue(target, value);
                return true;
            }

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsEmptyGuid(Il2CppSystem.Guid value)
        => value == Il2CppSystem.Guid.Empty;

    private static string GuidText(Il2CppSystem.Guid value)
        => SafeString(() => value.ToString());

    private static bool IsSharedDesignCandidateYear(int createdYear, int requestedYear)
        => createdYear >= requestedYear - SharedDesignPastYearWindow &&
           createdYear <= requestedYear + SharedDesignFutureYearWindow;

    private static string SharedDesignWindowText()
        => $"-{SharedDesignPastYearWindow}/+{SharedDesignFutureYearWindow}";

    private static void ApplySharedDesignOptionSanitizer(Ship ship, Ship.Store? store, Player player)
    {
        if (ship == null)
            return;

        try
        {
            BlueprintSanitizeResult result = new();
            HashSet<string> removedTokens = new(StringComparer.OrdinalIgnoreCase);

            if (ModSettings.MajorShipTorpedoesRestricted && IsMajorSurfaceCombatant(ship.shipType))
            {
                result.MajorTorpedoesRemoved = RemoveParts(ship, IsTorpedoLauncherPart, removedTokens);
                if (result.MajorTorpedoesRemoved > 0 && !HasTorpedoLauncherParts(ship))
                    result.ComponentsRemoved += RemoveComponents(ship, IsTorpedoSupportComponent, removedTokens, result.RemovedComponentLabels);
            }

            if (ModSettings.MineWarfareDisabled)
            {
                result.MinesRemoved = RemoveParts(ship, MineWarfareDetector.IsMinePart, removedTokens);
                result.ComponentsRemoved += RemoveComponents(ship, MineWarfareDetector.IsMineComponent, removedTokens, result.RemovedComponentLabels);
            }

            if (ModSettings.SubmarineWarfareDisabled)
            {
                result.AswRemoved = RemoveParts(ship, IsAswPart, removedTokens);
                result.ComponentsRemoved += RemoveComponents(ship, IsAswComponent, removedTokens, result.RemovedComponentLabels);
            }

            if (!result.HasChanges)
                return;

            result.TechsPruned = PruneRemovedEquipmentTechs(ship, removedTokens, player);
            RecalculateSharedDesignCandidate(ship, player);
            Log(
                $"SharedDesign blueprint-sanitize nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" year={ShipYear(ship)} actions=majorTorps:{result.MajorTorpedoesRemoved} mines:{result.MinesRemoved} asw:{result.AswRemoved} removedComponents={FormatRemovedItems(result.RemovedComponentLabels)} result=continue.");
        }
        catch (Exception ex)
        {
            WarnOnce(
                "blueprintSanitize:" + ex.GetType().Name,
                $"shared-design blueprint sanitizer failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplySharedDesignSafeDowngrades(Ship ship, Ship.Store? store, Player player)
    {
        if (ship == null)
            return;

        try
        {
            TechIdentitySet actualTechs = PlayerTechIdentities(player, includeEndTechs: false);
            TechIdentitySet knownTechs = PlayerTechIdentities(player, includeEndTechs: true);
            HashSet<string> actualComponents = PlayerUnlockedComponentNames(player, includeEndTechs: false);
            HashSet<string> knownComponents = PlayerUnlockedComponentNames(player, includeEndTechs: true);
            List<ComponentData> allComponents = AllComponentData();
            List<ComponentDowngradeAction> actions = BuildSharedDesignDowngradeActions(
                ship,
                actualTechs,
                knownTechs,
                actualComponents,
                knownComponents,
                allComponents);
            if (actions.Count == 0)
                return;

            List<string> labels = new();
            foreach (ComponentDowngradeAction action in actions)
            {
                if (!ApplyComponentDowngrade(ship, action))
                    continue;

                action.Applied = true;
                labels.Add(action.Label);
            }

            if (labels.Count == 0)
                return;

            int techsRewritten = RewriteDowngradedComponentTechs(ship, labels.Count == actions.Count ? actions : actions.Where(action => action.Applied).ToList());
            RecalculateSharedDesignCandidate(ship, player);
            Log(
                $"SharedDesign blueprint-downgrade nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" actions={string.Join(",", labels.Select(LogToken))} techsRewritten={techsRewritten} result=continue.");
        }
        catch (Exception ex)
        {
            WarnOnce(
                "blueprintDowngrade:" + ex.GetType().Name,
                $"shared-design safe downgrade failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<ComponentDowngradeAction> BuildSharedDesignDowngradeActions(
        Ship ship,
        TechIdentitySet actualTechs,
        TechIdentitySet knownTechs,
        HashSet<string> actualComponents,
        HashSet<string> knownComponents,
        IReadOnlyList<ComponentData> allComponents)
    {
        List<ComponentDowngradeAction> result = new();
        try
        {
            var components = ship.components;
            if (components == null)
                return result;

            List<(CompType Slot, ComponentData Component)> installed = new();
            foreach (var pair in components)
            {
                if (pair.Key != null && pair.Value != null)
                    installed.Add((pair.Key, pair.Value));
            }

            foreach ((CompType slot, ComponentData current) in installed)
            {
                SharedDesignDowngradeFamily family = DowngradeFamily(current);
                if (family == SharedDesignDowngradeFamily.None)
                    continue;

                if (ComponentIsKnownForSharedDesignDowngrade(current, actualTechs, knownTechs, actualComponents, knownComponents))
                    continue;

                ComponentData? replacement = FindBestDowngradeComponent(
                    current,
                    family,
                    actualTechs,
                    knownTechs,
                    actualComponents,
                    knownComponents,
                    allComponents);
                bool canRemove = replacement == null && CanRemoveDowngradeFamily(family);
                if (replacement == null && !canRemove)
                    continue;

                if (replacement != null && SameComponent(current, replacement))
                    continue;

                result.Add(new ComponentDowngradeAction(slot, current, replacement, family));
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool ApplyComponentDowngrade(Ship ship, ComponentDowngradeAction action)
    {
        try
        {
            if (action.To == null)
            {
                ship.UninstallComponent(action.Slot);
                return true;
            }

            if (ship.InstallComponent(action.To, true))
                return true;

            var components = ship.components;
            if (components == null)
                return false;

            components[action.Slot] = action.To;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RewriteDowngradedComponentTechs(Ship ship, IReadOnlyList<ComponentDowngradeAction> actions)
    {
        List<TechnologyData> designTechs = DesignTechs(ship);
        if (designTechs.Count == 0 || actions.Count == 0)
            return 0;

        HashSet<string> replacedComponents = new(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentDowngradeAction action in actions)
            AddUsedComponent(replacedComponents, ComponentToken(action.From));

        HashSet<string> usedComponents = ShipUsedComponents(ship, null);
        List<TechnologyData> kept = new(designTechs.Count + actions.Count);
        int removed = 0;
        foreach (TechnologyData tech in designTechs)
        {
            string component = TechComponentToken(tech);
            if (ComponentIsUsed(component, replacedComponents) && !ComponentIsUsed(component, usedComponents))
            {
                removed++;
                continue;
            }

            kept.Add(tech);
        }

        int added = 0;
        foreach (ComponentDowngradeAction action in actions)
        {
            TechnologyData? replacementTech = Safe(() => action.To?.tech, null);
            if (replacementTech == null || TechnologyListContains(kept, replacementTech))
                continue;

            kept.Add(replacementTech);
            added++;
        }

        if (removed <= 0 && added <= 0)
            return 0;

        return RewriteShipActualTechs(ship, kept) ? removed + added : 0;
    }

    private static void ApplySharedDesignGunLengthClamp(Ship ship, Ship.Store? store, Player player)
    {
        if (ship == null)
            return;

        try
        {
            GunLengthCaps caps = GunLengthCaps.ForPlayer(player);
            List<string> actions = new();
            HashSet<GunLengthCategory> clampedCategories = new();
            var gunCalibers = ship.shipGunCaliber;
            if (gunCalibers == null)
                return;

            foreach (Ship.TurretCaliber caliber in gunCalibers)
            {
                if (caliber == null || Safe(() => caliber.turretPartData, null) == null)
                    continue;

                float current = Safe(() => (float)caliber.length, -1f);
                if (current < 0f)
                    continue;

                GunLengthCategory category = GunLengthCategoryFor(caliber);
                float cap = caps.For(category);
                if (current <= cap)
                    continue;

                float target = Math.Max(0f, (float)Math.Floor(cap));
                if (target >= current)
                    continue;

                ship.SetCaliberLength(caliber, target);
                clampedCategories.Add(category);
                actions.Add($"{GunLengthCategoryLabel(category)}:{Fmt(current)}->{Fmt(target)}");
            }

            int techsPruned = clampedCategories.Count > 0
                ? PruneGunLengthClampTechs(ship, clampedCategories)
                : 0;
            int satisfiedOrUnusedTechsPruned = PruneSatisfiedOrUnusedGunLengthTechs(ship, player, caps);
            if (actions.Count == 0 && satisfiedOrUnusedTechsPruned <= 0)
                return;

            RecalculateSharedDesignCandidate(ship, player);
            bool valid = Safe(() => ship.IsValid(false), false);
            if (actions.Count > 0)
            {
                Log(
                    $"SharedDesign gun-length-clamp nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" actions={string.Join(",", actions.Select(LogToken))} techsPruned={techsPruned + satisfiedOrUnusedTechsPruned} valid={BoolText(valid)} result={(valid ? "continue" : "reject")} reason={(valid ? "clamped" : "validation")}.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                "gunLengthClamp:" + ex.GetType().Name,
                $"shared-design gun length clamp failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int PruneGunLengthClampTechs(Ship ship, HashSet<GunLengthCategory> clampedCategories)
    {
        if (clampedCategories.Count == 0)
            return 0;

        List<TechnologyData> designTechs = DesignTechs(ship);
        if (designTechs.Count == 0)
            return 0;

        List<TechnologyData> kept = new(designTechs.Count);
        int removed = 0;
        foreach (TechnologyData tech in designTechs)
        {
            if (IsGunLengthClampTech(tech, clampedCategories))
            {
                removed++;
                continue;
            }

            kept.Add(tech);
        }

        if (removed <= 0)
            return 0;

        return RewriteShipActualTechs(ship, kept) ? removed : 0;
    }

    private static int PruneSatisfiedOrUnusedGunLengthTechs(Ship ship, Player player, GunLengthCaps caps)
    {
        List<TechnologyData> designTechs = DesignTechs(ship);
        if (designTechs.Count == 0)
            return 0;

        if (!TryBuildLiveGunLengthSummary(ship, out Dictionary<GunLengthCategory, float> liveMaxByCategory))
            return 0;

        TechIdentitySet actual = PlayerTechIdentities(player, includeEndTechs: false);
        List<TechnologyData> kept = new(designTechs.Count);
        List<string> removedTechs = new();
        foreach (TechnologyData tech in designTechs)
        {
            GunLengthCategory category = GunLengthCategoryForTech(tech);
            if (category == GunLengthCategory.None ||
                !TechHasNoComponent(tech) ||
                actual.Contains(tech) ||
                GunLengthTechStillRequired(category, liveMaxByCategory, caps))
            {
                kept.Add(tech);
                continue;
            }

            removedTechs.Add(TechKey(tech));
        }

        if (removedTechs.Count == 0)
            return 0;

        if (!RewriteShipActualTechs(ship, kept))
        {
            WarnOnce(
                "gunLengthTechPruneWrite:" + NormalizeShipType(ship.shipType),
                $"unable to rewrite gun-length-pruned shared-design tech list for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}.");
            return 0;
        }

        Log(
            $"SharedDesign gun-length-tech-prune nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" removedTechs={FormatRemovedItems(removedTechs)} reason=satisfied-or-unused-length-tech result=continue.");
        return removedTechs.Count;
    }

    private static bool TryBuildLiveGunLengthSummary(Ship ship, out Dictionary<GunLengthCategory, float> liveMaxByCategory)
    {
        liveMaxByCategory = new Dictionary<GunLengthCategory, float>();
        try
        {
            var gunCalibers = ship.shipGunCaliber;
            if (gunCalibers == null)
                return false;

            foreach (Ship.TurretCaliber caliber in gunCalibers)
            {
                if (caliber == null || Safe(() => caliber.turretPartData, null) == null)
                    continue;

                float current = Safe(() => (float)caliber.length, -1f);
                if (current < 0f)
                    continue;

                GunLengthCategory category = GunLengthCategoryFor(caliber);
                if (category == GunLengthCategory.None)
                    continue;

                liveMaxByCategory.TryGetValue(category, out float previous);
                liveMaxByCategory[category] = Math.Max(previous, current);
            }

            return true;
        }
        catch
        {
            liveMaxByCategory.Clear();
            return false;
        }
    }

    private static bool GunLengthTechStillRequired(
        GunLengthCategory category,
        IReadOnlyDictionary<GunLengthCategory, float> liveMaxByCategory,
        GunLengthCaps caps)
    {
        if (!liveMaxByCategory.TryGetValue(category, out float liveMax))
            return false;

        float cap = caps.For(category);
        return liveMax > cap + 0.01f;
    }

    private static bool IsGunLengthClampTech(TechnologyData? tech, HashSet<GunLengthCategory> clampedCategories)
    {
        if (tech == null || !TechHasNoComponent(tech))
            return false;

        GunLengthCategory category = GunLengthCategoryForTech(tech);
        return category != GunLengthCategory.None && clampedCategories.Contains(category);
    }

    private static bool TechHasNoComponent(TechnologyData tech)
    {
        string component = SafeString(() => tech.component);
        if (!string.Equals(component, "<empty>", StringComparison.Ordinal))
            return false;

        ComponentData? componentData = Safe(() => tech.componentx, null);
        return componentData == null || string.Equals(ComponentToken(componentData), "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static GunLengthCategory GunLengthCategoryForTech(TechnologyData tech)
    {
        string effect = SafeString(() => tech.effect);
        if (effect.IndexOf("tech_gun_length_limit_casemates", StringComparison.OrdinalIgnoreCase) >= 0)
            return GunLengthCategory.Casemate;
        if (effect.IndexOf("tech_gun_length_limit_small", StringComparison.OrdinalIgnoreCase) >= 0)
            return GunLengthCategory.Small;
        if (effect.IndexOf("tech_gun_length_limit(", StringComparison.OrdinalIgnoreCase) >= 0)
            return GunLengthCategory.Large;

        if (!TryGunMechIndex(TechKey(tech), out int index))
            return GunLengthCategory.None;

        if (index is >= 34 and <= 41)
            return GunLengthCategory.Large;
        if (index is >= 42 and <= 49)
            return GunLengthCategory.Small;
        if (index is >= 50 and <= 54)
            return GunLengthCategory.Casemate;

        return GunLengthCategory.None;
    }

    private static bool TryGunMechIndex(string? name, out int index)
    {
        index = -1;
        const string prefix = "gun_mech_";
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(name[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    private static GunLengthCategory GunLengthCategoryFor(Ship.TurretCaliber caliber)
    {
        if (Safe(() => caliber.isCasemateGun, false) ||
            StrictContains(SafeString(() => caliber.turretPartData?.type), "casemate") ||
            StrictContains(SafeString(() => caliber.turretPartData?.name), "casemate"))
        {
            return GunLengthCategory.Casemate;
        }

        float diameter = Safe(() => caliber.diameter, 0f);
        return diameter > 2f ? GunLengthCategory.Large : GunLengthCategory.Small;
    }

    private static string GunLengthCategoryLabel(GunLengthCategory category)
        => category switch
        {
            GunLengthCategory.Large => "large",
            GunLengthCategory.Small => "small",
            GunLengthCategory.Casemate => "casemate",
            _ => "unknown"
        };

    private static ComponentData? FindBestDowngradeComponent(
        ComponentData current,
        SharedDesignDowngradeFamily family,
        TechIdentitySet actualTechs,
        TechIdentitySet knownTechs,
        HashSet<string> actualComponents,
        HashSet<string> knownComponents,
        IReadOnlyList<ComponentData> allComponents)
    {
        int currentTier = ComponentTier(current);
        if (currentTier < 0)
            return null;

        List<ComponentData> candidates = allComponents
            .Where(component => component != null)
            .Where(component => !SameComponent(component, current))
            .Where(component => ComponentIsKnownForSharedDesignDowngrade(component, actualTechs, knownTechs, actualComponents, knownComponents))
            .ToList();

        IEnumerable<ComponentData> primary = candidates
            .Where(component => DowngradeFamily(component) == family)
            .Where(component => ComponentTier(component) >= 0 && ComponentTier(component) < currentTier);

        ComponentData? best = BestDowngradeCandidate(primary);
        if (best != null)
            return best;

        if (family is SharedDesignDowngradeFamily.RangefinderStereo)
        {
            IEnumerable<ComponentData> optical = candidates
                .Where(component => DowngradeFamily(component) is SharedDesignDowngradeFamily.RangefinderCoinc or SharedDesignDowngradeFamily.RangefinderStereo)
                .Where(component => ComponentTier(component) >= 0 && ComponentTier(component) <= currentTier);

            return BestDowngradeCandidate(optical);
        }

        return null;
    }

    private static ComponentData? BestDowngradeCandidate(IEnumerable<ComponentData> candidates)
        => candidates
            .OrderByDescending(ComponentTier)
            .ThenByDescending(component => Safe(() => component.tech?.year ?? 0, 0))
            .ThenBy(component => ComponentToken(component), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static List<ComponentData> AllComponentData()
    {
        List<ComponentData> result = new();
        try
        {
            var components = G.GameData?.components;
            if (components == null)
                return result;

            foreach (var pair in components)
            {
                if (pair.Value != null)
                    result.Add(pair.Value);
            }
        }
        catch
        {
        }

        return result;
    }

    private static HashSet<string> PlayerUnlockedComponentNames(Player? player, bool includeEndTechs)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (player == null)
            return result;

        try
        {
            var techs = includeEndTechs ? player.technologiesResearchedAll : player.technologiesResearchedActual;
            if (techs == null)
                return result;

            var list = new Il2CppSystem.Collections.Generic.List<Technology>(techs);
            foreach (Technology tech in list)
            {
                TechnologyData? data = tech?.data;
                AddUsedComponent(result, SafeString(() => data?.component));
                AddComponent(result, Safe(() => data?.componentx, null));
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool ComponentIsKnownForSharedDesignDowngrade(
        ComponentData? component,
        TechIdentitySet actualTechs,
        TechIdentitySet knownTechs,
        HashSet<string> actualComponents,
        HashSet<string> knownComponents)
    {
        if (component == null)
            return false;

        TechnologyData? tech = Safe(() => component.tech, null);
        if (tech == null)
            return true;

        if (actualTechs.Contains(tech) || knownTechs.Contains(tech))
            return true;

        string techComponent = TechComponentToken(tech);
        if (ComponentIsUsed(techComponent, actualComponents) || ComponentIsUsed(techComponent, knownComponents))
            return true;

        string componentName = ComponentToken(component);
        return ComponentIsUsed(componentName, actualComponents) || ComponentIsUsed(componentName, knownComponents);
    }

    private static SharedDesignDowngradeFamily DowngradeFamily(ComponentData? component)
    {
        if (component == null)
            return SharedDesignDowngradeFamily.None;

        string name = SafeString(() => component.name);
        string type = SafeString(() => component.type);
        if (name.StartsWith("fuel_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "fuel", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Fuel;
        if (name.StartsWith("boiler_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "boilers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "boiler", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.Boiler;
        }
        if (IsNumberedComponent(name, "armor_") && string.Equals(type, "armor", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.ArmorQuality;
        if (name.StartsWith("torpedo_diameter_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "torpedo_size", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.TorpedoSize;
        if (name.StartsWith("torpedo_belt_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "torpedo_belt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "torpedo_protection", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.TorpedoBelt;
        }
        if (name.StartsWith("radio_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Radio;
        if (name.StartsWith("rangefinder_coinc_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.RangefinderCoinc;
        if (name.StartsWith("rangefinder_stereo_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.RangefinderStereo;
        if (name.StartsWith("rangefinder_radar_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "radar", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Radar;
        if (name.StartsWith("steering_gear_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "steering", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Steering;
        if (name.StartsWith("aux_engine_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "aux_eng", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.AuxEngine;
        if (name.StartsWith("drive_shaft_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "shaft", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.DriveShaft;
        if (name.StartsWith("rudder_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "rudder", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Rudder;
        if (name.StartsWith("turret_traverse_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "turret_traverse", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.TurretTraverse;
        if (name.StartsWith("gun_reload_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "gun_reload", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.GunReload;
        if (name.StartsWith("buklheads_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("bulkheads_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "bulkheads", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.Bulkheads;
        }
        if (name.StartsWith("Anti_Flooding_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("anti_flooding_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "antiflooding", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.AntiFlooding;
        }
        if (name.StartsWith("propellant_", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "propellant", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Propellant;

        return SharedDesignDowngradeFamily.None;
    }

    private static bool CanRemoveDowngradeFamily(SharedDesignDowngradeFamily family)
        => family is SharedDesignDowngradeFamily.Radio or
            SharedDesignDowngradeFamily.Radar or
            SharedDesignDowngradeFamily.RangefinderCoinc or
            SharedDesignDowngradeFamily.TorpedoBelt or
            SharedDesignDowngradeFamily.AuxEngine or
            SharedDesignDowngradeFamily.DriveShaft or
            SharedDesignDowngradeFamily.AntiFlooding;

    private static bool SameComponent(ComponentData? a, ComponentData? b)
    {
        if (a == null || b == null)
            return false;

        long aPointer = Safe(() => a.Pointer.ToInt64(), 0L);
        long bPointer = Safe(() => b.Pointer.ToInt64(), 0L);
        if (aPointer != 0L && aPointer == bPointer)
            return true;

        return string.Equals(ComponentToken(a), ComponentToken(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TechnologyListContains(IEnumerable<TechnologyData> techs, TechnologyData tech)
    {
        long pointer = Safe(() => tech.Pointer.ToInt64(), 0L);
        string name = TechKey(tech);
        foreach (TechnologyData candidate in techs)
        {
            long candidatePointer = Safe(() => candidate.Pointer.ToInt64(), 0L);
            if (pointer != 0L && candidatePointer == pointer)
                return true;

            if (!string.IsNullOrWhiteSpace(name) && string.Equals(TechKey(candidate), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string TechComponentToken(TechnologyData? tech)
    {
        if (tech == null)
            return string.Empty;

        string component = SafeString(() => tech.component);
        if (!string.Equals(component, "<empty>", StringComparison.Ordinal))
            return component;

        return ComponentToken(Safe(() => tech.componentx, null));
    }

    private static bool IsNumberedComponent(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return ComponentTier(name) >= 0;
    }

    private static int ComponentTier(ComponentData? component)
        => ComponentTier(SafeString(() => component?.name));

    private static int ComponentTier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        if (string.Equals(name, "fuel_coal", StringComparison.OrdinalIgnoreCase))
            return 0;

        int end = name.Length - 1;
        while (end >= 0 && char.IsDigit(name[end]))
            end--;

        if (end == name.Length - 1)
            return -1;

        string digits = name[(end + 1)..];
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier)
            ? tier
            : -1;
    }

    private static int RemoveParts(Ship ship, Func<PartData?, bool> shouldRemove, HashSet<string> removedTokens)
    {
        List<Part> remove = new();
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return 0;

            foreach (Part part in parts)
            {
                PartData? data = Safe(() => part?.data, null);
                if (part != null && shouldRemove(data))
                    remove.Add(part);
            }
        }
        catch
        {
            return 0;
        }

        int removed = 0;
        foreach (Part part in remove)
        {
            PartData? data = Safe(() => part?.data, null);
            AddEquipmentTokens(removedTokens, data);
            try
            {
                ship.RemovePart(part, true, true);
                removed++;
            }
            catch
            {
            }
        }

        return removed;
    }

    private static int RemoveComponents(
        Ship ship,
        Func<ComponentData?, bool> shouldRemove,
        HashSet<string> removedTokens,
        List<string> removedLabels)
    {
        List<CompType> keys = new();
        try
        {
            var components = ship.components;
            if (components == null)
                return 0;

            foreach (var pair in components)
            {
                if (shouldRemove(pair.Value))
                    keys.Add(pair.Key);
            }

            int removed = 0;
            foreach (CompType key in keys)
            {
                if (!components.TryGetValue(key, out ComponentData component) || component == null)
                    continue;

                AddEquipmentTokens(removedTokens, component);
                removedLabels.Add(ComponentToken(component));
                if (components.Remove(key))
                    removed++;
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    private static int PruneRemovedEquipmentTechs(Ship ship, HashSet<string> removedTokens, Player player)
    {
        if (removedTokens.Count == 0)
            return 0;

        List<TechnologyData> designTechs = DesignTechs(ship);
        if (designTechs.Count == 0)
            return 0;

        List<TechnologyData> kept = new(designTechs.Count);
        List<string> removed = new();
        foreach (TechnologyData tech in designTechs)
        {
            if (TechReferencesRemovedEquipment(tech, removedTokens))
            {
                removed.Add(TechKey(tech));
                continue;
            }

            kept.Add(tech);
        }

        if (removed.Count == 0 || !RewriteShipActualTechs(ship, kept))
            return 0;

        Log(
            $"SharedDesign blueprint-tech-prune nation={PlayerLabel(player)} design=\"{ShipLabel(ship)}\" removedTechs={FormatRemovedItems(removed)} reason=removed-disabled-option-equipment.");
        return removed.Count;
    }

    private static bool TechReferencesRemovedEquipment(TechnologyData? tech, HashSet<string> removedTokens)
    {
        if (tech == null)
            return false;

        string component = SafeString(() => tech.component);
        if (ComponentIsUsed(component, removedTokens))
            return true;

        string effect = SafeString(() => tech.effect);
        if (string.Equals(effect, "<empty>", StringComparison.Ordinal))
            return false;

        foreach (string token in removedTokens)
        {
            if (token.Length > 2 && effect.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static void RecalculateSharedDesignCandidate(Ship ship, Player player)
    {
        try
        {
            ship.CalcWeightAndCost(true, true);
        }
        catch (Exception ex)
        {
            WarnOnce(
                "blueprintRecalc:" + ex.GetType().Name,
                $"shared-design blueprint sanitizer could not recalculate {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool HasTorpedoLauncherParts(Ship ship)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return false;

            foreach (Part part in parts)
            {
                if (IsTorpedoLauncherPart(Safe(() => part?.data, null)))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool HasTorpedoLauncherCacheEntries(Ship ship)
    {
        try
        {
            var torpedoes = ship.torpedoesAll;
            if (torpedoes == null)
                return false;

            foreach (Part part in torpedoes)
            {
                if (IsTorpedoLauncherPart(Safe(() => part?.data, null)))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool HasAnyTorpedoLauncher(Ship ship)
        => HasTorpedoLauncherParts(ship) || HasTorpedoLauncherCacheEntries(ship);

    private static int CountTorpedoLaunchersForPower(Ship ship)
    {
        int cached = CountTorpedoLauncherCacheEntries(ship);
        return cached > 0 ? cached : CountTorpedoLauncherParts(ship);
    }

    private static int CountTorpedoLauncherParts(Ship ship)
    {
        try
        {
            int count = 0;
            var parts = ship.parts;
            if (parts == null)
                return 0;

            foreach (Part part in parts)
            {
                if (IsTorpedoLauncherPart(Safe(() => part?.data, null)))
                    count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountTorpedoLauncherCacheEntries(Ship ship)
    {
        try
        {
            int count = 0;
            var torpedoes = ship.torpedoesAll;
            if (torpedoes == null)
                return 0;

            foreach (Part part in torpedoes)
            {
                if (IsTorpedoLauncherPart(Safe(() => part?.data, null)))
                    count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static int RemoveTorpedoLauncherCacheEntries(Ship ship, HashSet<string> removedTokens)
    {
        List<Part> remove = new();
        try
        {
            var torpedoes = ship.torpedoesAll;
            if (torpedoes == null)
                return 0;

            foreach (Part part in torpedoes)
            {
                PartData? data = Safe(() => part?.data, null);
                if (part != null && IsTorpedoLauncherPart(data))
                    remove.Add(part);
            }

            int removed = 0;
            foreach (Part part in remove)
            {
                PartData? data = Safe(() => part?.data, null);
                AddEquipmentTokens(removedTokens, data);
                while (torpedoes.Remove(part))
                    removed++;
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    private static int StoreTorpedoLauncherCount(Ship.Store? store)
    {
        try
        {
            if (store?.parts == null)
                return -1;

            int count = 0;
            foreach (Part.Store part in store.parts)
            {
                string name = SafeString(() => part.name);
                if (!IsTorpedoProtectionOrDetectionText(name) && StrictContains(name, "torpedo"))
                    count++;
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsMajorSurfaceCombatant(ShipType? shipType)
    {
        string label = NormalizeShipType(shipType);
        return label is "CA" or "BC" or "BB";
    }

    private static bool IsTorpedoLauncherPart(PartData? part)
    {
        if (part == null || IsTorpedoProtectionOrDetectionText(part.type) || IsTorpedoProtectionOrDetectionText(part.name))
            return false;

        return Safe(() => part.isTorpedo, false) ||
               StrictContains(part.type, "torpedo") ||
               StrictContains(part.name, "torpedo");
    }

    private static bool IsTorpedoSupportComponent(ComponentData? component)
        => ComponentMatchesFamily(component, "torpedo_prop") ||
           ComponentMatchesFamily(component, "torpedo_size") ||
           ComponentMatchesFamily(component, "ammo_torp");

    private static bool IsAswPart(PartData? part)
    {
        if (part == null)
            return false;

        return ContainsAswMarker(part.type) ||
               ContainsAswMarker(part.name) ||
               ContainsAswMarker(part.nameUi) ||
               ContainsAswMarker(part.param) ||
               ContainsAswMarker(part.stats);
    }

    private static bool IsAswComponent(ComponentData? component)
    {
        if (component == null)
            return false;

        return ContainsAswMarker(component.type) ||
               ContainsAswMarker(component.typex?.name) ||
               ContainsAswMarker(component.name) ||
               ContainsAswMarker(component.nameShort) ||
               ContainsAswMarker(component.nameUi) ||
               ContainsAswMarker(component.param) ||
               ContainsAswMarker(component.typex?.param);
    }

    private static bool ComponentMatchesFamily(ComponentData? component, string family)
    {
        if (component == null)
            return false;

        return TokenEqualsOrStartsWith(component.type, family) ||
               TokenEqualsOrStartsWith(component.typex?.name, family) ||
               TokenEqualsOrStartsWith(component.name, family) ||
               TokenEqualsOrStartsWith(component.nameShort, family);
    }

    private static bool TokenEqualsOrStartsWith(string? value, string family)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string token = value.Trim();
        return string.Equals(token, family, StringComparison.OrdinalIgnoreCase) ||
               token.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAswMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (IsSurfaceSubmarineSensorText(value))
            return false;

        return value.IndexOf("depth_charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("depth charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("depthcharge", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("anti_sub", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("anti-sub", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("antisub", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("asw", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsSurfaceSubmarineSensorText(string value)
        => value.IndexOf("sonar", StringComparison.OrdinalIgnoreCase) >= 0 ||
           value.IndexOf("hydrophone", StringComparison.OrdinalIgnoreCase) >= 0 ||
           value.IndexOf("hydrophones", StringComparison.OrdinalIgnoreCase) >= 0 ||
           value.IndexOf("hydro", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsTorpedoProtectionOrDetectionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.IndexOf("torpedo_belt", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("torpedo protection", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("torpedo_protection", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("torpedo_detection", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("torpedo detect", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool StrictContains(string? value, string marker)
        => !string.IsNullOrWhiteSpace(value) &&
           value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void AddEquipmentTokens(HashSet<string> tokens, PartData? data)
    {
        if (data == null)
            return;

        AddUsedComponent(tokens, SafeString(() => data.name));
        AddUsedComponent(tokens, SafeString(() => data.type));
        AddUsedComponent(tokens, SafeString(() => data.nameUi));
        AddUsedComponent(tokens, SafeString(() => data.model));
    }

    private static void AddEquipmentTokens(HashSet<string> tokens, ComponentData? data)
    {
        if (data == null)
            return;

        AddUsedComponent(tokens, SafeString(() => data.name));
        AddUsedComponent(tokens, SafeString(() => data.nameShort));
        AddUsedComponent(tokens, SafeString(() => data.nameUi));
        AddUsedComponent(tokens, SafeString(() => data.type));
        AddUsedComponent(tokens, SafeString(() => data.typex?.name));
    }

    private static string ComponentToken(ComponentData? component)
    {
        if (component == null)
            return "unknown";

        string name = SafeString(() => component.name);
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        string shortName = SafeString(() => component.nameShort);
        if (!string.Equals(shortName, "<empty>", StringComparison.Ordinal))
            return shortName;

        string type = SafeString(() => component.type);
        return string.Equals(type, "<empty>", StringComparison.Ordinal) ? "unknown" : type;
    }

    private static string FormatRemovedItems(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return "none";

        List<string> tokens = items
            .Where(item => !string.IsNullOrWhiteSpace(item) && !string.Equals(item, "<empty>", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxBlueprintRemovedItems)
            .Select(LogToken)
            .ToList();
        int hidden = Math.Max(0, items.Distinct(StringComparer.OrdinalIgnoreCase).Count() - tokens.Count);
        if (hidden > 0)
            tokens.Add($"+{hidden}more");

        return tokens.Count == 0 ? "none" : string.Join(",", tokens);
    }

    private static bool CanBuildSharedCandidate(Ship ship, Player? player, string caller, out string reason)
    {
        reason = "none";
        if (AiDesignBuildability.IsAiPlayer(player))
        {
            return AiDesignBuildability.CanBuildDesign(
                player,
                ship,
                1,
                caller,
                out reason);
        }

        PlayerController? controller = PlayerController.Instance;
        if (controller == null)
        {
            reason = "noPlayerController";
            return false;
        }

        string localReason = "none";
        bool result = CampaignAiShipbuildingDiagnosticsPatch.WithoutValidationAggregation(() =>
        {
            bool canBuild = controller.CanBuildShipsFromDesign(ship, out string reportedReason);
            localReason = SharedBuildRejectReason(reportedReason);
            return canBuild;
        });

        reason = localReason;
        return result;
    }

    private static SharedDesignValidationResult ValidateSharedDesignCandidate(
        CampaignController? controller,
        Ship ship,
        Ship.Store? store,
        Player player,
        bool checkTech,
        bool isEarlySavedShip,
        bool logRelaxedPass)
    {
        if (!isEarlySavedShip && !CanBuildSharedCandidate(ship, player, "SharedDesignValidation", out string buildReason))
        {
            if (IsTonnageBuildReject(buildReason))
            {
                if (checkTech && !TechMatches(controller, ship, player, store, logRelaxedPass, out string techReason))
                    return SharedDesignValidationResult.Rejected("tech", techReason);

                bool rescued = TrySharedDesignTonnageRescue(ship, store, player, buildReason, out TonnageRescueResult rescue);
                if (rescued)
                    return SharedDesignValidationResult.AcceptedWith($" tonnageRescue={rescue.Detail}");

                return SharedDesignValidationResult.Rejected(
                    "build",
                    $"{BuildRejectDetails(ship, store, player, buildReason)} tonnageRescue={rescue.Detail}",
                    buildReason);
            }

            if (IsInvalidDesignBuildReject(buildReason))
            {
                if (checkTech && !TechMatches(controller, ship, player, store, logRelaxedPass, out string techReason))
                    return SharedDesignValidationResult.Rejected("tech", techReason);

                bool rescued = TrySharedDesignInternalWeightRescue(ship, store, player, buildReason, out InternalWeightRescueResult rescue);
                if (rescued)
                    return SharedDesignValidationResult.AcceptedWith($" internalWeightRescue={rescue.Detail}");

                return SharedDesignValidationResult.Rejected(
                    "build",
                    $"{BuildRejectDetails(ship, store, player, buildReason)} internalWeightRescue={rescue.Detail}",
                    buildReason);
            }

            return SharedDesignValidationResult.Rejected(
                "build",
                BuildRejectDetails(ship, store, player, buildReason),
                buildReason);
        }

        if (checkTech && !TechMatches(controller, ship, player, store, logRelaxedPass, out string finalTechReason))
            return SharedDesignValidationResult.Rejected("tech", finalTechReason);

        return SharedDesignValidationResult.AcceptedWith(string.Empty);
    }

    private static bool IsTonnageBuildReject(string? reason)
        => string.Equals(NormalizeReason(reason), "tonnage", StringComparison.OrdinalIgnoreCase);

    private static bool IsInvalidDesignBuildReject(string? reason)
        => string.Equals(SharedBuildRejectReason(reason), "invalidDesign", StringComparison.OrdinalIgnoreCase);

    private static string SharedBuildRejectReason(string? reason)
    {
        string normalized = NormalizeReason(reason);
        return string.Equals(normalized, "valid", StringComparison.OrdinalIgnoreCase)
            ? "invalidDesign"
            : normalized;
    }

    private static bool TrySharedDesignTonnageRescue(
        Ship ship,
        Ship.Store? store,
        Player player,
        string currentRejectReason,
        out TonnageRescueResult result)
    {
        result = TonnageRescueResult.None;
        try
        {
            if (!IsTonnageBuildReject(currentRejectReason))
            {
                result = TonnageRescueResult.Skipped("not-tonnage");
                return false;
            }

            float originalTons = Safe(() => ship.Tonnage(), -1f);
            float limit = SharedDesignTonnageLimit(ship, player);
            if (originalTons <= 0f || limit <= 0f)
            {
                result = TonnageRescueResult.Skipped("missing-limit", originalTons, originalTons, limit);
                LogTonnageRescue(player, ship, result);
                return false;
            }

            float overweight = originalTons / limit - 1f;
            if (overweight <= 0f)
            {
                result = TonnageRescueResult.Skipped("not-overweight", originalTons, originalTons, limit);
                LogTonnageRescue(player, ship, result);
                return false;
            }

            if (overweight > 0.08f)
            {
                result = TonnageRescueResult.Skipped("over-limit-too-large", originalTons, originalTons, limit);
                LogTonnageRescue(player, ship, result);
                return false;
            }

            VesselEntity.OpRange originalRange = Safe(() => ship.opRange, VesselEntity.OpRange.Medium);
            VesselEntity.OpRange currentRange = originalRange;
            float originalSpeed = Safe(() => ship.speedMax, 0f);
            float currentSpeed = originalSpeed;
            float finalTons = originalTons;

            for (int step = 0; step < 2; step++)
            {
                if ((int)currentRange <= (int)VesselEntity.OpRange.VeryLow)
                    break;

                currentRange = (VesselEntity.OpRange)((int)currentRange - 1);
                ship.SetOpRange(currentRange, false);
                RecalculateSharedDesignCandidate(ship, player);
                finalTons = Safe(() => ship.Tonnage(), finalTons);
                if (CanBuildSharedCandidate(ship, player, "SharedDesignTonnageRescue:range", out string rangeReason))
                {
                    result = TonnageRescueResult.Accepted(originalTons, finalTons, limit, originalRange, currentRange, originalSpeed, currentSpeed);
                    LogTonnageRescue(player, ship, result);
                    return true;
                }

                if (!IsTonnageBuildReject(rangeReason))
                {
                    result = TonnageRescueResult.Rejected($"build-{NormalizeReason(rangeReason)}", originalTons, finalTons, limit, originalRange, currentRange, originalSpeed, currentSpeed);
                    LogTonnageRescue(player, ship, result);
                    return false;
                }
            }

            for (int step = 0; step < 4; step++)
            {
                if (currentSpeed <= 0.5f)
                    break;

                currentSpeed = Math.Max(0.5f, currentSpeed - 0.5f);
                ship.SetSpeedMax(currentSpeed);
                ship.SetEngineCustomSpeed(currentSpeed);
                RecalculateSharedDesignCandidate(ship, player);
                finalTons = Safe(() => ship.Tonnage(), finalTons);
                if (CanBuildSharedCandidate(ship, player, "SharedDesignTonnageRescue:speed", out string speedReason))
                {
                    result = TonnageRescueResult.Accepted(originalTons, finalTons, limit, originalRange, currentRange, originalSpeed, currentSpeed);
                    LogTonnageRescue(player, ship, result);
                    return true;
                }

                if (!IsTonnageBuildReject(speedReason))
                {
                    result = TonnageRescueResult.Rejected($"build-{NormalizeReason(speedReason)}", originalTons, finalTons, limit, originalRange, currentRange, originalSpeed, currentSpeed);
                    LogTonnageRescue(player, ship, result);
                    return false;
                }
            }

            result = TonnageRescueResult.Rejected("still-overweight", originalTons, finalTons, limit, originalRange, currentRange, originalSpeed, currentSpeed);
            LogTonnageRescue(player, ship, result);
            return false;
        }
        catch (Exception ex)
        {
            result = TonnageRescueResult.Skipped("error-" + ex.GetType().Name);
            WarnOnce(
                "tonnageRescue:" + ex.GetType().Name,
                $"shared-design tonnage rescue failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySharedDesignInternalWeightRescue(
        Ship ship,
        Ship.Store? store,
        Player player,
        string currentRejectReason,
        out InternalWeightRescueResult result)
    {
        result = InternalWeightRescueResult.None;
        try
        {
            if (!IsInvalidDesignBuildReject(currentRejectReason))
            {
                result = InternalWeightRescueResult.Skipped("not-invalidDesign");
                return false;
            }

            if (!TryReadInternalWeightFailure(ship, out InternalWeightFailureInfo failure, out string failureReason))
            {
                result = InternalWeightRescueResult.Skipped(failureReason);
                LogInternalWeightRescue(player, ship, result);
                return false;
            }

            float originalTons = Safe(() => ship.Tonnage(), -1f);
            float weight = failure.Weight;
            float limit = SharedDesignTonnageLimit(ship, player);
            if (originalTons <= 0f || weight <= 0f || limit <= 0f)
            {
                result = InternalWeightRescueResult.Skipped("missing-tonnage-data", originalTons, weight, originalTons, limit);
                LogInternalWeightRescue(player, ship, result);
                return false;
            }

            if (weight <= originalTons)
            {
                result = InternalWeightRescueResult.Skipped("not-internal-overweight", originalTons, weight, originalTons, limit);
                LogInternalWeightRescue(player, ship, result);
                return false;
            }

            float targetTons = (float)Math.Ceiling(weight + InternalWeightRescueMarginTons);
            if (targetTons > limit)
            {
                result = InternalWeightRescueResult.Skipped("target-over-limit", originalTons, weight, targetTons, limit);
                LogInternalWeightRescue(player, ship, result);
                return false;
            }

            ship.SetTonnage(targetTons);
            RecalculateSharedDesignCandidate(ship, player);
            float finalTons = Safe(() => ship.Tonnage(), targetTons);

            if (CanBuildSharedCandidate(ship, player, "SharedDesignInternalWeightRescue", out string finalReason))
            {
                result = InternalWeightRescueResult.Accepted(originalTons, weight, finalTons, limit);
                LogInternalWeightRescue(player, ship, result);
                return true;
            }

            result = InternalWeightRescueResult.Rejected("build-" + NormalizeReason(finalReason), originalTons, weight, finalTons, limit);
            LogInternalWeightRescue(player, ship, result);
            Safe(() =>
            {
                ship.SetTonnage(originalTons);
                RecalculateSharedDesignCandidate(ship, player);
                return true;
            }, false);
            return false;
        }
        catch (Exception ex)
        {
            result = InternalWeightRescueResult.Skipped("error-" + ex.GetType().Name);
            WarnOnce(
                "internalWeightRescue:" + ex.GetType().Name,
                $"shared-design internal weight rescue failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryReadInternalWeightFailure(Ship ship, out InternalWeightFailureInfo failure, out string reason)
    {
        failure = InternalWeightFailureInfo.Empty;
        reason = "unknown";

        try
        {
            bool reqPartsValid = ship.IsValidCostReqParts(
                out string reqReason,
                out Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo> notPassed,
                out Il2CppSystem.Collections.Generic.Dictionary<Part, string> badParts);
            if (!reqPartsValid || HasItems(notPassed) || HasItems(badParts))
            {
                reason = "costReqParts";
                return false;
            }

            bool weightOffsetValid = ship.IsValidWeightOffset();
            if (!weightOffsetValid)
            {
                reason = "weightOffset";
                return false;
            }

            bool costWeightBarbetteValid = ship.IsValidCostWeightBarbette(
                out string weightReason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);
            if (costWeightBarbetteValid)
            {
                reason = "costWeightBarbette-valid";
                return false;
            }

            if (!string.Equals(NormalizeReason(weightReason), "weight", StringComparison.OrdinalIgnoreCase))
            {
                reason = "costWeightBarbette-" + NormalizeReason(weightReason);
                return false;
            }

            if (HasItems(errorBarbettePart))
            {
                reason = "badBarbettes";
                return false;
            }

            float originalTons = Safe(() => ship.Tonnage(), -1f);
            float weight = Safe(() => ship.Weight(true, true), -1f);
            if (originalTons <= 0f || weight <= originalTons)
            {
                reason = "no-internal-overweight";
                return false;
            }

            failure = new InternalWeightFailureInfo(originalTons, weight);
            reason = "weight";
            return true;
        }
        catch (Exception ex)
        {
            reason = "unavailable-" + ex.GetType().Name;
            return false;
        }
    }

    private static bool HasItems<T>(Il2CppSystem.Collections.Generic.List<T>? items)
        => Safe(() => items != null && items.Count > 0, false);

    private static bool HasItems<TKey, TValue>(Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>? items)
        where TKey : notnull
        => Safe(() => items != null && items.Count > 0, false);

    private static void LogInternalWeightRescue(Player player, Ship ship, InternalWeightRescueResult result)
    {
        Log(
            $"SharedDesign internal-weight-rescue nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" {result.Detail}.");
    }

    private static float SharedDesignTonnageLimit(Ship ship, Player player)
    {
        float limit = Safe(() => player.TonnageLimit(ship.shipType), -1f);
        if (limit > 0f)
            return limit;

        return Safe(() => player.TechTonnage(ship.shipType), -1f);
    }

    private static void LogTonnageRescue(Player player, Ship ship, TonnageRescueResult result)
    {
        Log(
            $"SharedDesign tonnage-rescue nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design=\"{ShipLabel(ship)}\" {result.Detail}.");
    }

    private static string BuildRejectDetails(Ship ship, Ship.Store? store, Player? player, string reason)
    {
        string normalized = SharedBuildRejectReason(reason);
        if (string.Equals(normalized, "invalidDesign", StringComparison.OrdinalIgnoreCase))
            return $"invalidDesign invalidDetails=[{InvalidDesignDetails(ship, store, player)}]";

        if (!string.Equals(normalized, "unlock", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return $"{normalized} unlockDetails=[{UnlockDetails(ship, store, player)}]";
    }

    private static string InvalidDesignDetails(Ship ship, Ship.Store? store, Player? player)
    {
        try
        {
            List<string> details = new()
            {
                $"isValid={BoolProbe(() => ship.IsValid(false))}",
                $"autoValid={BoolProbe(() => ship.IsValid(true))}",
                $"weightOffset={BoolProbe(() => ship.IsValidWeightOffset())}",
                CostReqPartsDetails(ship),
                CostWeightBarbetteDetails(ship),
                InvalidDesignTonnageDetails(ship, player),
                $"hull={LogToken(SafeString(() => ship.hull?.data?.name) == "<empty>" ? SafeString(() => store?.hullName) : SafeString(() => ship.hull?.data?.name))}",
                $"components={Safe(() => ship.components?.Count ?? -1, -1)}",
                $"parts={Safe(() => ship.parts?.Count ?? -1, -1)}"
            };

            return string.Join(",", details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string CostReqPartsDetails(Ship ship)
    {
        try
        {
            bool valid = ship.IsValidCostReqParts(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<ShipType.ReqInfo> notPassed,
                out Il2CppSystem.Collections.Generic.Dictionary<Part, string> badParts);

            List<string> bits = new()
            {
                $"costReqParts={valid.ToString().ToLowerInvariant()}",
                $"reason={LogToken(NormalizeReason(reason))}",
                $"notPassed={FormatReqInfoFailures(ship, notPassed)}",
                $"badParts={FormatBadPartFailures(badParts)}"
            };

            return string.Join(" ", bits);
        }
        catch (Exception ex)
        {
            return $"costReqParts=unavailable:{ex.GetType().Name}";
        }
    }

    private static string CostWeightBarbetteDetails(Ship ship)
    {
        try
        {
            bool valid = ship.IsValidCostWeightBarbette(
                out string reason,
                out Il2CppSystem.Collections.Generic.List<Part> errorBarbettePart);

            return string.Join(
                " ",
                $"costWeightBarbette={valid.ToString().ToLowerInvariant()}",
                $"reason={LogToken(NormalizeReason(reason))}",
                $"badBarbettes={FormatPartList(errorBarbettePart)}");
        }
        catch (Exception ex)
        {
            return $"costWeightBarbette=unavailable:{ex.GetType().Name}";
        }
    }

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
        PartData? data = Safe(() => part?.data, null);
        if (data == null)
            return "unknown";

        string name = SafeString(() => data.name);
        if (string.Equals(name, "<empty>", StringComparison.Ordinal))
            name = SafeString(() => data.nameUi);

        return LogToken(name);
    }

    private static string InvalidDesignTonnageDetails(Ship ship, Player? player)
    {
        Player? owner = player ?? Safe(() => ship.player, null);
        return string.Join(
            " ",
            $"tons={Fmt(Safe(() => ship.Tonnage(), -1f))}",
            $"weight={Fmt(Safe(() => ship.Weight(true, true), -1f))}",
            $"techTonnage={Fmt(Safe(() => owner?.TechTonnage(ship.shipType) ?? -1f, -1f))}",
            $"tonnageLimit={Fmt(Safe(() => owner?.TonnageLimit(ship.shipType) ?? -1f, -1f))}",
            $"maxShipyard={Fmt(Safe(() => owner?.MaxShipyard() ?? -1f, -1f))}");
    }

    private static string BoolProbe(Func<bool> probe)
    {
        try
        {
            return probe().ToString().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static string UnlockDetails(Ship ship, Ship.Store? store, Player? player)
    {
        try
        {
            List<string> details = new();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            AddHullUnlockDetails(details, seen, ship, store, player);
            AddComponentUnlockDetails(details, seen, ship);
            AddPartUnlockDetails(details, seen, ship, player);

            if (details.Count == 0)
                return "none";

            int hidden = Math.Max(0, details.Count - MaxUnlockDetails);
            string suffix = hidden > 0 ? $";+{hidden}more" : string.Empty;
            return string.Join(";", details.Take(MaxUnlockDetails)) + suffix;
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static void AddHullUnlockDetails(List<string> details, HashSet<string> seen, Ship ship, Ship.Store? store, Player? player)
    {
        PartData? hull = FirstHullPart(ship);
        if (hull != null)
        {
            AddPartAvailabilityDetail(details, seen, hull, player, ship, forceHull: true);
            return;
        }

        string hullName = SafeString(() => store?.hullName);
        if (!string.Equals(hullName, "<empty>", StringComparison.Ordinal))
        {
            AddUnlockDetail(
                details,
                seen,
                $"hull:{hullName}",
                $"item={LogToken(hullName)} kind=hull name=? year=? component=? need=? available=? reason=noMaterializedHullPart");
        }
    }

    private static void AddComponentUnlockDetails(List<string> details, HashSet<string> seen, Ship ship)
    {
        try
        {
            var components = ship.components;
            if (components == null)
                return;

            foreach (var pair in components)
            {
                ComponentData? component = pair.Value;
                if (component == null)
                    continue;

                string reason = "none";
                bool available = Safe(() => ship.IsComponentAvailable(component, out reason), true);
                if (available)
                    continue;

                string item = SafeString(() => component.name);
                AddUnlockDetail(
                    details,
                    seen,
                    $"component:{item}",
                    $"item={LogToken(item)} kind=component name={LogToken(SafeString(() => component.nameUi))} year=? component={LogToken(SafeString(() => component.type))} need=? available=false reason={LogToken(reason)}");
            }
        }
        catch
        {
        }
    }

    private static void AddPartUnlockDetails(List<string> details, HashSet<string> seen, Ship ship, Player? player)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return;

            foreach (Part part in parts)
            {
                PartData? data = Safe(() => part?.data, null);
                if (data == null)
                    continue;

                AddPartAvailabilityDetail(details, seen, data, player, ship, forceHull: false);
            }
        }
        catch
        {
        }
    }

    private static void AddPartAvailabilityDetail(
        List<string> details,
        HashSet<string> seen,
        PartData data,
        Player? player,
        Ship ship,
        bool forceHull)
    {
        bool isHull = forceHull || Safe(() => data.isHull, false);
        bool? unlocked = null;
        bool? available = null;

        if (player != null)
        {
            unlocked = isHull
                ? Safe<bool?>(() => player.IsHullUnlocked(data), null)
                : Safe<bool?>(() => player.IsPartUnlocked(data), null);
            available = Safe<bool?>(() => Ship.IsPartAvailable(data, player, ship.shipType, ship), null);
        }

        if (unlocked == true && available != false)
            return;

        string item = SafeString(() => data.name);
        string need = PartNeedSummary(data);
        string detailReason =
            available == false ? "partAvailable=false" :
            unlocked == false ? "partUnlocked=false" :
            "availabilityUnknown";

        AddUnlockDetail(
            details,
            seen,
            $"part:{item}:{KindForPart(data, isHull)}",
            $"item={LogToken(item)} kind={KindForPart(data, isHull)} name={LogToken(SafeString(() => data.nameUi))} year=? component=? need={LogToken(need)} available={BoolText(available)} unlocked={BoolText(unlocked)} reason={LogToken(detailReason)}");
    }

    private static PartData? FirstHullPart(Ship ship)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return null;

            foreach (Part part in parts)
            {
                PartData? data = Safe(() => part?.data, null);
                if (data != null && Safe(() => data.isHull, false))
                    return data;
            }
        }
        catch
        {
        }

        return null;
    }

    private static void AddUnlockDetail(List<string> details, HashSet<string> seen, string key, string detail)
    {
        if (!seen.Add(key))
            return;

        details.Add(detail);
    }

    private static string KindForPart(PartData data, bool isHull)
    {
        if (isHull)
            return "hull";
        if (Safe(() => data.isTowerAny, false))
            return "tower";
        if (Safe(() => data.isFunnel, false))
            return "funnel";
        if (Safe(() => data.isTorpedo, false))
            return "torpedo";
        if (Safe(() => data.isGun, false))
            return "gun";
        if (Safe(() => data.isBarbette, false))
            return "barbette";

        return "part";
    }

    private static string PartNeedSummary(PartData data)
    {
        string needUnlock = SafeString(() => data.NeedUnlock);
        string paramNeed = ParamxSummary(data, "need");
        string paramUnlock = ParamxSummary(data, "needunlock");
        string rawParam = SafeString(() => data.param);
        List<string> bits = new();
        if (!string.Equals(needUnlock, "<empty>", StringComparison.Ordinal))
            bits.Add($"NeedUnlock={needUnlock}");
        if (!string.Equals(paramNeed, "<empty>", StringComparison.Ordinal))
            bits.Add($"need={paramNeed}");
        if (!string.Equals(paramUnlock, "<empty>", StringComparison.Ordinal))
            bits.Add($"needunlock={paramUnlock}");
        if (rawParam.IndexOf("need", StringComparison.OrdinalIgnoreCase) >= 0)
            bits.Add($"param={rawParam}");

        return bits.Count == 0 ? "none" : string.Join(",", bits);
    }

    private static string ParamxSummary(PartData data, string key)
    {
        try
        {
            if (data.paramx == null ||
                !data.paramx.TryGetValue(key, out Il2CppSystem.Collections.Generic.List<string> values) ||
                values == null)
                return "<empty>";

            return string.Join("|", values);
        }
        catch
        {
            return "<empty>";
        }
    }

    private static bool TechMatches(CampaignController? controller, Ship ship, Player player, Ship.Store? store, bool logRelaxedPass, out string reason)
    {
        reason = "false";
        SanitizeSharedDesignTechs(ship, store, player);

        if (controller == null || TechMatchMethod == null)
        {
            reason = "unavailable";
            WarnOnce("techMatchTarget", "CampaignController.TechMatch target not found; shared-design tech reject diagnostics are incomplete.");
            return true;
        }

        try
        {
            object? result = TechMatchMethod.Invoke(controller, new object[] { ship, player });
            bool matches = result is bool value && value;
            if (matches)
            {
                reason = "true";
                return true;
            }

            if (RelaxedTechMatches(ship, player, out int knownNotActual, out int unknown, out int shipTechs))
            {
                reason = $"relaxed knownNotActual={knownNotActual} unknown={unknown} shipTechs={shipTechs}";
                if (logRelaxedPass)
                {
                    Log(
                        $"relaxed-tech-match nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design={ShipLabel(ship)} knownNotActual={knownNotActual} unknown={unknown} shipTechs={shipTechs} result=pass.");
                }

                return true;
            }

            reason = $"false {MissingTechDetails(ship, store, player)}";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"error:{ex.GetType().Name} {MissingTechDetails(ship, store, player)}";
            WarnOnce(
                "techMatchInvoke:" + ex.GetType().Name,
                $"CampaignController.TechMatch diagnostic call failed. {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static void SanitizeSharedDesignTechs(Ship ship, Ship.Store? store, Player player)
    {
        try
        {
            List<TechnologyData> designTechs = DesignTechs(ship);
            if (designTechs.Count == 0)
                return;

            TechIdentitySet actual = PlayerTechIdentities(player, includeEndTechs: false);
            TechIdentitySet all = PlayerTechIdentities(player, includeEndTechs: true);
            HashSet<string> usedComponents = ShipUsedComponents(ship, store);
            HashSet<string> liveComponents = ShipUsedComponents(ship, null);
            List<TechnologyData> kept = new(designTechs.Count);
            int removedGlobal = 0;
            List<string> ignoredStatTechs = new();
            List<string> removedClassScopedTechs = new();
            int keptUsed = 0;
            int keptKnown = 0;
            int keptUnknown = 0;
            string designType = NormalizeShipType(ship.shipType);

            foreach (TechnologyData tech in designTechs)
            {
                bool hasActual = actual.Contains(tech);
                bool hasAll = all.Contains(tech);
                if (hasActual || hasAll)
                {
                    kept.Add(tech);
                    keptKnown++;
                    continue;
                }

                if (IsSafeIgnorableSharedDesignStatTech(tech))
                {
                    ignoredStatTechs.Add(TechKey(tech));
                    continue;
                }

                if (IsDowngradeableComponentTech(tech) && !TechIsUsedByShip(tech, liveComponents))
                {
                    removedGlobal++;
                    continue;
                }

                bool used = TechIsUsedByShip(tech, usedComponents);
                if (used)
                {
                    kept.Add(tech);
                    keptUsed++;
                    continue;
                }

                if (IsSurfaceSharedDesign(ship) && IsUnusedSubmarineBaggageTech(tech))
                {
                    removedGlobal++;
                    continue;
                }

                if (IsUnusedClassScopedSharedDesignTech(tech, designType, out string classScopeDetail))
                {
                    removedClassScopedTechs.Add(classScopeDetail);
                    continue;
                }

                if (IsUnusedGlobalSharedDesignTech(tech))
                {
                    removedGlobal++;
                    continue;
                }

                kept.Add(tech);
                keptUnknown++;
            }

            if (removedGlobal <= 0 && ignoredStatTechs.Count <= 0 && removedClassScopedTechs.Count <= 0)
                return;

            if (!RewriteShipActualTechs(ship, kept))
            {
                WarnOnce(
                    "sanitizeTechsWrite:" + NormalizeShipType(ship.shipType),
                    $"unable to rewrite sanitized shared-design tech list for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}.");
                return;
            }

            string key = $"{PlayerPointer(player)}:{NormalizeShipType(ship.shipType)}:{ShipLabel(ship)}:{ShipYear(ship)}:{removedGlobal}:{ignoredStatTechs.Count}:{removedClassScopedTechs.Count}:{kept.Count}";
            if (LoggedSanitizedTechs.Add(key))
            {
                Log(
                    $"sanitized-techs nation={PlayerLabel(player)} type={NormalizeShipType(ship.shipType)} design={ShipLabel(ship)} removedGlobal={removedGlobal} removedClassScoped={FormatRemovedItems(removedClassScopedTechs)} ignoredStatTechs={FormatRemovedItems(ignoredStatTechs)} keptUsed={keptUsed} keptKnown={keptKnown} keptUnknown={keptUnknown} result=continue.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                "sanitizeTechs:" + ex.GetType().Name,
                $"shared-design tech sanitization failed for {PlayerLabel(player)} {NormalizeShipType(ship.shipType)} {ShipLabel(ship)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool RewriteShipActualTechs(Ship ship, IReadOnlyList<TechnologyData> kept)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<TechnologyData>? techsActual = ship.techsActual;
            if (techsActual == null)
                return false;

            techsActual.Clear();
            foreach (TechnologyData tech in kept)
            {
                if (tech != null)
                    techsActual.Add(tech);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnusedGlobalSharedDesignTech(TechnologyData? tech)
    {
        if (tech == null)
            return false;

        if (IsHardSharedDesignTechDependency(tech))
            return false;

        string component = SafeString(() => tech.component);
        return string.IsNullOrWhiteSpace(component) ||
               string.Equals(component, "<empty>", StringComparison.Ordinal);
    }

    private static bool IsUnusedClassScopedSharedDesignTech(TechnologyData? tech, string designType, out string detail)
    {
        detail = string.Empty;
        if (tech == null)
            return false;

        if (!IsRecognizedShipClass(designType))
            return false;

        string component = SafeString(() => tech.component);
        if (!string.IsNullOrWhiteSpace(component) && !string.Equals(component, "<empty>", StringComparison.Ordinal))
            return false;

        HashSet<string> scopedTypes = ClassScopesForTechEffect(tech);
        if (scopedTypes.Count == 0 || scopedTypes.Contains(designType))
            return false;

        string scopes = string.Join("+", scopedTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).Select(type => type.ToLowerInvariant()));
        detail = $"{TechKey(tech)}:scope={scopes}:ship={designType.ToLowerInvariant()}";
        return true;
    }

    private static HashSet<string> ClassScopesForTechEffect(TechnologyData tech)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        string effect = SafeString(() => tech.effect);
        if (string.IsNullOrWhiteSpace(effect) || string.Equals(effect, "<empty>", StringComparison.Ordinal))
            return result;

        int searchFrom = 0;
        while (searchFrom < effect.Length)
        {
            int open = effect.IndexOf('(', searchFrom);
            if (open < 0)
                break;

            int close = effect.IndexOf(')', open + 1);
            if (close < 0)
                break;

            string args = effect.Substring(open + 1, close - open - 1);
            foreach (string rawToken in args.Split(new[] { ';', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryNormalizeShipClassToken(rawToken, out string shipClass))
                    result.Add(shipClass);
            }

            searchFrom = close + 1;
        }

        return result;
    }

    private static bool TryNormalizeShipClassToken(string? rawToken, out string shipClass)
    {
        shipClass = string.Empty;
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        string token = rawToken.Trim().Trim('"', '\'', '[', ']', '(', ')').ToLowerInvariant();
        shipClass = token switch
        {
            "bb" or "battleship" or "battleships" => "BB",
            "bc" or "battlecruiser" or "battlecruisers" => "BC",
            "ca" or "heavy_cruiser" or "heavycruiser" or "heavy_cruisers" or "heavycruisers" => "CA",
            "cl" or "light_cruiser" or "lightcruiser" or "light_cruisers" or "lightcruisers" => "CL",
            "dd" or "destroyer" or "destroyers" => "DD",
            "tb" or "torpedo_boat" or "torpedoboat" or "torpedo_boats" or "torpedoboats" => "TB",
            "ss" or "sub" or "submarine" or "submarines" => "SS",
            _ => string.Empty,
        };

        return shipClass.Length > 0;
    }

    private static bool IsRecognizedShipClass(string? shipType)
        => shipType is "BB" or "BC" or "CA" or "CL" or "DD" or "TB" or "SS";

    private static bool IsUnusedSubmarineBaggageTech(TechnologyData? tech)
    {
        if (tech == null)
            return false;

        return LooksLikeSubmarineTech(
            TechKey(tech),
            SafeString(() => tech.type),
            SafeString(() => tech.component));
    }

    private static bool IsDowngradeableComponentTech(TechnologyData? tech)
    {
        string component = TechComponentToken(tech);
        return DowngradeFamilyForComponentToken(component) != SharedDesignDowngradeFamily.None;
    }

    private static SharedDesignDowngradeFamily DowngradeFamilyForComponentToken(string? component)
    {
        if (string.IsNullOrWhiteSpace(component) || string.Equals(component, "<empty>", StringComparison.Ordinal))
            return SharedDesignDowngradeFamily.None;

        string name = component.Trim();
        if (name.StartsWith("fuel_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Fuel;
        if (name.StartsWith("boiler_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Boiler;
        if (name.StartsWith("armor_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.ArmorQuality;
        if (name.StartsWith("torpedo_diameter_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.TorpedoSize;
        if (name.StartsWith("torpedo_belt_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.TorpedoBelt;
        if (name.StartsWith("radio_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Radio;
        if (name.StartsWith("rangefinder_coinc_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.RangefinderCoinc;
        if (name.StartsWith("rangefinder_stereo_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.RangefinderStereo;
        if (name.StartsWith("rangefinder_radar_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Radar;
        if (name.StartsWith("steering_gear_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Steering;
        if (name.StartsWith("aux_engine_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.AuxEngine;
        if (name.StartsWith("drive_shaft_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.DriveShaft;
        if (name.StartsWith("rudder_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Rudder;
        if (name.StartsWith("turret_traverse_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.TurretTraverse;
        if (name.StartsWith("gun_reload_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.GunReload;
        if (name.StartsWith("buklheads_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("bulkheads_", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.Bulkheads;
        }
        if (name.StartsWith("Anti_Flooding_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("anti_flooding_", StringComparison.OrdinalIgnoreCase))
        {
            return SharedDesignDowngradeFamily.AntiFlooding;
        }
        if (name.StartsWith("propellant_", StringComparison.OrdinalIgnoreCase))
            return SharedDesignDowngradeFamily.Propellant;

        return SharedDesignDowngradeFamily.None;
    }

    private static bool IsSurfaceSharedDesign(Ship? ship)
    {
        string type = NormalizeShipType(Safe(() => ship?.shipType, null));
        return type is "BB" or "BC" or "CA" or "CL" or "DD" or "TB";
    }

    private static bool IsSafeIgnorableSharedDesignStatTech(TechnologyData? tech)
    {
        if (tech == null)
            return false;

        string component = SafeString(() => tech.component);
        if (!string.IsNullOrWhiteSpace(component) && !string.Equals(component, "<empty>", StringComparison.Ordinal))
            return false;

        string name = TechKey(tech);
        if (IsExplicitSafeSharedDesignStatTech(name))
            return true;

        if (IsHardSharedDesignTechDependency(tech))
            return false;

        string type = SafeString(() => tech.type);
        string effect = SafeString(() => tech.effect);
        if (name.StartsWith("surv_internal_", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("surv_internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (effect.IndexOf("module_repair_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            effect.IndexOf("repair(", StringComparison.OrdinalIgnoreCase) >= 0 ||
            effect.IndexOf("stat(", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsExplicitSafeSharedDesignStatTech(string name)
        => string.Equals(name, "engine_special_7", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "engine_special_8", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "engine_special_9", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "gun_mech_3", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "gun_mech_4", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "gun_mech_12", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "gun_mech_13", StringComparison.OrdinalIgnoreCase);

    private static bool IsHardSharedDesignTechDependency(TechnologyData? tech)
    {
        if (tech == null)
            return false;

        string name = TechKey(tech);
        string type = SafeString(() => tech.type);
        string combined = $"{name} {type}";
        return combined.Contains("gun_layout", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("gun_mech", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("gun_sec", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("gun_main", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("gun_mark", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("hull", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("engine_engine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RelaxedTechMatches(Ship ship, Player player, out int knownNotActual, out int unknown, out int shipTechs)
    {
        knownNotActual = 0;
        unknown = 0;
        List<TechnologyData> designTechs = DesignTechs(ship);
        shipTechs = designTechs.Count;
        if (shipTechs == 0)
            return false;

        TechIdentitySet actual = PlayerTechIdentities(player, includeEndTechs: false);
        TechIdentitySet all = PlayerTechIdentities(player, includeEndTechs: true);
        foreach (TechnologyData tech in designTechs)
        {
            bool isEnd = Safe(() => tech.isEnd, false);
            bool hasActual = actual.Contains(tech);
            bool hasAll = all.Contains(tech);
            if (isEnd)
            {
                if (!hasAll)
                    unknown++;

                continue;
            }

            if (hasActual)
                continue;

            if (hasAll)
            {
                knownNotActual++;
                continue;
            }

            unknown++;
        }

        return unknown == 0;
    }

    private static int RequestedYear(CampaignController? controller, bool prewarming)
    {
        if (controller == null)
            return -1;

        return prewarming
            ? Safe(() => controller.StartYear, -1)
            : Safe(() => controller.CurrentDate.AsDate().Year, Safe(() => controller.StartYear, -1));
    }

    private static bool ShouldTrace(Player? player)
    {
        if (player == null)
            return false;

        return Safe(() => player.isAi && !player.isMain, false);
    }

    private static string ShipSummary(Ship? ship)
    {
        if (ship == null)
            return "none";

        try
        {
            EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(ship);
            return $"{NormalizeShipType(ship.shipType)} {ShipLabel(ship)} {ShipDateDetails(ship, null)} power={ShipEffectivePowerCalculator.FormatCompactPower(power.AdjustedPower)}";
        }
        catch
        {
            return $"{NormalizeShipType(ship.shipType)} {ShipLabel(ship)} {ShipDateDetails(ship, null)}";
        }
    }

    private static string ShipSummary(Ship? ship, Ship.Store store, Player? player = null)
    {
        if (ship == null)
            return StoreSummary(store);

        return $"{NormalizeShipType(ship.shipType)} {ShipLabel(ship)} {ShipDateDetails(ship, store)} {BuildNumericDetails(ship, player)}";
    }

    private static string StoreSummary(Ship.Store? store)
    {
        if (store == null)
            return "<null>";

        return $"{NormalizeShipType(SafeString(() => store.shipType))} {SafeString(() => store.vesselName)} {StoreDateDetails(store)}";
    }

    private static string ShipLabel(Ship? ship)
    {
        string label = SafeString(() => ship?.vesselName);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        return "<unnamed>";
    }

    private static int ShipYear(Ship? ship)
        => Safe(() => (ship!.isRefitDesign ? ship.dateCreatedRefit : ship.dateCreated).AsDate().Year, 0);

    private static string ShipDateDetails(Ship? ship, Ship.Store? store)
    {
        string storeDetails = store == null ? "storeYear=?" : StoreDateDetails(store);
        if (ship == null)
            return $"{storeDetails} shipYear=? dateCreatedTurn=? dateFinishedTurn=? dateCreatedRefitTurn=? isRefitDesign=?";

        return $"{storeDetails} shipYear={ShipYear(ship)} dateCreatedTurn={Safe(() => ship.dateCreated.turn, -1)} dateFinishedTurn={Safe(() => ship.dateFinished.turn, -1)} dateCreatedRefitTurn={Safe(() => ship.dateCreatedRefit.turn, -1)} isRefitDesign={Safe(() => ship.isRefitDesign, false).ToString().ToLowerInvariant()}";
    }

    private static string StoreDateDetails(Ship.Store? store)
    {
        if (store == null)
            return "storeYear=? storeDateCreatedTurn=? storeDateFinishedTurn=? storeDateCreatedRefitTurn=?";

        return $"storeYear={Safe(() => store.YearCreated, 0)} storeDateCreatedTurn={Safe(() => store.dateCreated.turn, -1)} storeDateFinishedTurn={Safe(() => store.dateFinished.turn, -1)} storeDateCreatedRefitTurn={Safe(() => store.dateCreatedRefit.turn, -1)}";
    }

    private static string BuildNumericDetails(Ship? ship, Player? player)
    {
        if (ship == null)
            return "tons=? techTonnage=? tonnageLimit=? maxShipyard=?";

        Player? owner = player ?? Safe(() => ship.player, null);
        return string.Join(
            " ",
            $"tons={Fmt(Safe(() => ship.Tonnage(), -1f))}",
            $"techTonnage={Fmt(Safe(() => owner?.TechTonnage(ship.shipType) ?? -1f, -1f))}",
            $"tonnageLimit={Fmt(Safe(() => owner?.TonnageLimit(ship.shipType) ?? -1f, -1f))}",
            $"maxShipyard={Fmt(Safe(() => owner?.MaxShipyard() ?? -1f, -1f))}");
    }

    private static string MissingTechDetails(Ship? ship, Ship.Store? store, Player? player)
    {
        try
        {
            List<TechnologyData> designTechs = DesignTechs(ship);
            TechIdentitySet actual = PlayerTechIdentities(player, includeEndTechs: false);
            TechIdentitySet all = PlayerTechIdentities(player, includeEndTechs: true);
            List<TechnologyData> missing = new();

            foreach (TechnologyData tech in designTechs)
            {
                bool isEnd = Safe(() => tech.isEnd, false);
                bool hasActual = actual.Contains(tech);
                bool hasAll = all.Contains(tech);
                bool isMissing = isEnd ? !hasAll : !hasActual && !hasAll;
                if (isMissing)
                    missing.Add(tech);
            }

            if (missing.Count == 0 && designTechs.Count == 0)
                return StoreTechFallbackDetails(store, player, "shipTechsUnavailable");

            if (missing.Count == 0)
                return $"exactMissing=[] shipTechs={designTechs.Count} playerActual={actual.Count} playerAll={all.Count}";

            HashSet<string> usedComponents = ShipUsedComponents(ship, store);
            return $"exactMissing=[{FormatMissingTechs(missing, actual, all, usedComponents)}] shipTechs={designTechs.Count} storeTechs={Safe(() => store?.techs?.Count ?? -1, -1)} playerActual={actual.Count} playerAll={all.Count}";
        }
        catch (Exception ex)
        {
            return StoreTechFallbackDetails(store, player, ex.GetType().Name);
        }
    }

    private static string StoreTechFallbackDetails(Ship.Store? store, Player? player, string reason)
    {
        try
        {
            HashSet<string> playerTechs = PlayerTechNames(player, includeEndTechs: true);
            List<string> storeTechs = new();
            if (store?.techs != null)
            {
                foreach (string tech in store.techs)
                {
                    if (!string.IsNullOrWhiteSpace(tech))
                        storeTechs.Add(tech.Trim());
                }
            }

            List<string> missing = storeTechs
                .Where(tech => !playerTechs.Contains(tech))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            int hidden = Math.Max(0, storeTechs.Distinct(StringComparer.OrdinalIgnoreCase).Count(tech => !playerTechs.Contains(tech)) - missing.Count);
            string suffix = hidden > 0 ? $";+{hidden}more" : string.Empty;
            return $"exactMissing=unavailable reason={reason} storeMissingHint=[{string.Join(";", missing)}{suffix}] storeTechs={storeTechs.Count} playerAll={playerTechs.Count}";
        }
        catch (Exception ex)
        {
            return $"exactMissing=unavailable reason={reason}/{ex.GetType().Name}";
        }
    }

    private static List<TechnologyData> DesignTechs(Ship? ship)
    {
        List<TechnologyData> result = new();
        if (ship == null)
            return result;

        if (TryReadDirectTechCollection(ship, result))
            return result;

        foreach (PropertyInfo property in ShipTechCollectionProperties)
        {
            object? raw = property.GetValue(ship);
            if (TryReadTechCollection(raw, result))
            {
                string key = $"{property.DeclaringType?.FullName}.{property.Name}";
                if (LoggedTechCollectionFields.Add(key))
                {
                    Log(
                        $"tech-field property={key} count={result.Count}.");
                }

                return result;
            }
        }

        foreach (FieldInfo field in ShipTechCollectionFields)
        {
            object? raw = field.GetValue(ship);
            if (!TryReadTechCollection(raw, result))
                continue;

            string key = $"{field.DeclaringType?.FullName}.{field.Name}";
            if (LoggedTechCollectionFields.Add(key))
            {
                Log(
                    $"tech-field field={key} count={result.Count}.");
            }

            return result;
        }

        result.Clear();
        return result;
    }

    private static bool TryReadDirectTechCollection(Ship ship, List<TechnologyData> result)
    {
        object? raw = Safe<object?>(() => ship.techsActual, null);
        if (TryReadTechCollection(raw, result))
        {
            LogTechCollectionSource("direct=VesselEntity.techsActual", result.Count);
            return true;
        }

        LogTechCollectionReadFailure("direct=VesselEntity.techsActual", raw);

        raw = Safe<object?>(() => ship.techsList, null);
        if (TryReadTechCollection(raw, result))
        {
            LogTechCollectionSource("direct=VesselEntity.techsList", result.Count);
            return true;
        }

        LogTechCollectionReadFailure("direct=VesselEntity.techsList", raw);

        raw = Safe<object?>(() => ship.techs, null);
        if (TryReadTechCollection(raw, result))
        {
            LogTechCollectionSource("direct=VesselEntity.techs", result.Count);
            return true;
        }

        LogTechCollectionReadFailure("direct=VesselEntity.techs", raw);
        return false;
    }

    private static void LogTechCollectionSource(string source, int count)
    {
        if (LoggedTechCollectionFields.Add(source))
            Log($"tech-field {source} count={count}.");
    }

    private static void LogTechCollectionReadFailure(string source, object? raw)
    {
        string rawType = raw?.GetType().FullName ?? "<null>";
        string key = $"fail:{source}:{rawType}";
        if (LoggedTechCollectionFields.Add(key))
            Log($"tech-field {source} rawType={rawType} readable=false count=0.");
    }

    private static bool TryReadTechCollection(object? raw, List<TechnologyData> result)
    {
        result.Clear();
        if (raw is null)
            return false;

        if (raw is Il2CppSystem.Collections.Generic.List<TechnologyData> il2CppList)
        {
            foreach (TechnologyData tech in il2CppList)
            {
                if (tech != null)
                    result.Add(tech);
            }
        }
        else if (raw is Il2CppSystem.Collections.Generic.HashSet<TechnologyData> il2CppHashSet)
        {
            foreach (TechnologyData tech in il2CppHashSet)
            {
                if (tech != null)
                    result.Add(tech);
            }
        }
        else if (raw is Il2CppSystem.Collections.Generic.IEnumerable<TechnologyData> il2CppTechs)
        {
            var list = new Il2CppSystem.Collections.Generic.List<TechnologyData>(il2CppTechs);
            foreach (TechnologyData tech in list)
            {
                if (tech != null)
                    result.Add(tech);
            }
        }
        else if (raw is System.Collections.Generic.IEnumerable<TechnologyData> managedTechs)
        {
            foreach (TechnologyData tech in managedTechs)
            {
                if (tech != null)
                    result.Add(tech);
            }
        }
        else if (raw is System.Collections.IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item is TechnologyData tech)
                    result.Add(tech);
            }
        }

        return result.Count > 0;
    }

    private static List<PropertyInfo> FindShipTechCollectionProperties()
    {
        List<PropertyInfo> properties = new();
        for (Type? type = typeof(Ship); type != null; type = type.BaseType)
        {
            properties.AddRange(type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(IsTechnologyDataCollectionProperty));
        }

        return properties
            .OrderBy(TechMemberPriority)
            .ThenBy(property => property.DeclaringType?.Name, StringComparer.Ordinal)
            .ThenBy(property => property.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<FieldInfo> FindShipTechCollectionFields()
    {
        List<FieldInfo> fields = new();
        for (Type? type = typeof(Ship); type != null; type = type.BaseType)
        {
            fields.AddRange(type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(IsTechnologyDataCollectionField));
        }

        return fields
            .OrderBy(TechMemberPriority)
            .ThenBy(field => field.DeclaringType?.Name, StringComparer.Ordinal)
            .ThenBy(field => field.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTechnologyDataCollectionProperty(PropertyInfo property)
        => IsTechnologyDataCollectionMember(property.Name, property.PropertyType);

    private static bool IsTechnologyDataCollectionField(FieldInfo field)
        => IsTechnologyDataCollectionMember(field.Name, field.FieldType);

    private static bool IsTechnologyDataCollectionMember(string name, Type type)
    {
        string typeName = type.FullName ?? string.Empty;
        return typeName.Contains("TechnologyData", StringComparison.Ordinal) ||
               name.Contains("techs", StringComparison.OrdinalIgnoreCase);
    }

    private static int TechMemberPriority(MemberInfo member)
    {
        string name = member.Name;
        if (name.Equals("techsActual", StringComparison.Ordinal) ||
            name.Contains("<techsActual>", StringComparison.Ordinal))
            return 0;
        if (name.Contains("techsActual", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Equals("techsList", StringComparison.Ordinal) ||
            name.Contains("<techsList>", StringComparison.Ordinal))
            return 2;
        if (name.Equals("techs", StringComparison.Ordinal) ||
            name.Contains("<techs>", StringComparison.Ordinal))
            return 3;

        return 10;
    }

    private static HashSet<string> PlayerTechNames(Player? player, bool includeEndTechs)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (player == null)
            return result;

        try
        {
            var techs = includeEndTechs ? player.technologiesResearchedAll : player.technologiesResearchedActual;
            if (techs == null)
                return result;

            var list = new Il2CppSystem.Collections.Generic.List<Technology>(techs);
            foreach (Technology tech in list)
            {
                string key = TechKey(tech?.data);
                if (!string.IsNullOrWhiteSpace(key))
                    result.Add(key);
            }
        }
        catch
        {
        }

        return result;
    }

    private static TechIdentitySet PlayerTechIdentities(Player? player, bool includeEndTechs)
    {
        TechIdentitySet result = new();
        if (player == null)
            return result;

        try
        {
            var techs = includeEndTechs ? player.technologiesResearchedAll : player.technologiesResearchedActual;
            if (techs == null)
                return result;

            var list = new Il2CppSystem.Collections.Generic.List<Technology>(techs);
            foreach (Technology tech in list)
                result.Add(tech?.data);
        }
        catch
        {
        }

        return result;
    }

    private static string FormatMissingTechs(
        IReadOnlyList<TechnologyData> missing,
        TechIdentitySet actual,
        TechIdentitySet all,
        HashSet<string> usedComponents)
    {
        int take = Math.Min(MaxMissingTechDetails, missing.Count);
        List<string> labels = new();
        for (int i = 0; i < take; i++)
            labels.Add(TechLabel(missing[i], actual, all, usedComponents));

        if (missing.Count > take)
            labels.Add($"+{missing.Count - take}more");

        return string.Join(";", labels);
    }

    private static string TechLabel(
        TechnologyData? tech,
        TechIdentitySet actual,
        TechIdentitySet all,
        HashSet<string> usedComponents)
    {
        if (tech == null)
            return "<null>";

        string name = TechKey(tech);
        string nameUi = SafeString(() => tech.nameUi);
        string component = SafeString(() => tech.component);
        string type = SafeString(() => tech.type);
        bool hasActual = actual.Contains(tech);
        bool hasAll = all.Contains(tech);
        bool isEnd = Safe(() => tech.isEnd, false);
        bool start = TechHasEffect(tech, "start");
        bool defaultComponent = TechHasEffect(tech, "default_component");
        bool componentUsed = TechIsUsedByShip(tech, usedComponents);
        string classification = ClassifyMissingTech(name, type, component, hasActual, hasAll, start, defaultComponent, componentUsed);
        return $"{name}|{nameUi}|type={type}|component={component}|year={Safe(() => tech.year, 0)}|end={isEnd.ToString().ToLowerInvariant()}|actual={hasActual.ToString().ToLowerInvariant()}|all={hasAll.ToString().ToLowerInvariant()}|start={start.ToString().ToLowerInvariant()}|defaultComponent={defaultComponent.ToString().ToLowerInvariant()}|used={componentUsed.ToString().ToLowerInvariant()}|class={classification}";
    }

    private static bool TechHasEffect(TechnologyData tech, string effect)
    {
        bool hasEffect = Safe(() => tech.HasEffect(effect), false);
        if (hasEffect)
            return true;

        string rawEffect = SafeString(() => tech.effect);
        return rawEffect.IndexOf(effect, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TechIsUsedByShip(TechnologyData? tech, HashSet<string> usedComponents)
    {
        if (tech == null)
            return false;

        string component = SafeString(() => tech.component);
        if (ComponentIsUsed(component, usedComponents))
            return true;

        string rawEffect = SafeString(() => tech.effect);
        if (string.Equals(rawEffect, "<empty>", StringComparison.Ordinal))
            return false;

        foreach (string usedComponent in usedComponents)
        {
            if (usedComponent.Length > 2 &&
                rawEffect.IndexOf(usedComponent, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ClassifyMissingTech(
        string name,
        string type,
        string component,
        bool hasActual,
        bool hasAll,
        bool start,
        bool defaultComponent,
        bool componentUsed)
    {
        if (componentUsed)
            return "component-used";
        if (!hasActual && hasAll)
            return "known-not-actual";
        if (start)
            return "start";
        if (defaultComponent)
            return "default-component";
        if (LooksLikeSubmarineTech(name, type, component))
            return "submarine";
        if (string.IsNullOrWhiteSpace(component) || string.Equals(component, "<empty>", StringComparison.Ordinal))
            return "global";

        return "unknown";
    }

    private static bool LooksLikeSubmarineTech(string name, string type, string component)
        => name.Contains("submarine", StringComparison.OrdinalIgnoreCase) ||
           type.Contains("submarine", StringComparison.OrdinalIgnoreCase) ||
           component.Contains("submarine", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("sub_", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("submarine_", StringComparison.OrdinalIgnoreCase);

    private static bool ComponentIsUsed(string component, HashSet<string> usedComponents)
        => !string.IsNullOrWhiteSpace(component) &&
           !string.Equals(component, "<empty>", StringComparison.Ordinal) &&
           usedComponents.Contains(component);

    private static HashSet<string> ShipUsedComponents(Ship? ship, Ship.Store? store)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

        if (ship != null)
        {
            AddShipComponents(result, ship);
            AddShipParts(result, ship);
        }

        AddStoreComponents(result, store);
        return result;
    }

    private static void AddShipComponents(HashSet<string> result, Ship ship)
    {
        try
        {
            var components = ship.components;
            if (components == null)
                return;

            foreach (var pair in components)
                AddComponent(result, pair.Value);
        }
        catch
        {
        }
    }

    private static void AddShipParts(HashSet<string> result, Ship ship)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return;

            foreach (Part part in parts)
                AddPart(result, part);
        }
        catch
        {
        }
    }

    private static void AddStoreComponents(HashSet<string> result, Ship.Store? store)
    {
        try
        {
            if (store?.components == null)
                return;

            foreach (var pair in store.components)
            {
                AddUsedComponent(result, pair.Key);
                AddUsedComponent(result, pair.Value);
            }
        }
        catch
        {
        }
    }

    private static void AddComponent(HashSet<string> result, ComponentData? component)
    {
        if (component == null)
            return;

        AddUsedComponent(result, SafeString(() => component.name));
        AddUsedComponent(result, SafeString(() => component.nameShort));
        AddUsedComponent(result, SafeString(() => component.nameUi));
        AddUsedComponent(result, SafeString(() => component.type));
    }

    private static void AddPart(HashSet<string> result, Part? part)
    {
        PartData? data = Safe(() => part?.data, null);
        if (data == null)
            return;

        AddUsedComponent(result, SafeString(() => data.name));
        AddUsedComponent(result, SafeString(() => data.nameUi));
        AddUsedComponent(result, SafeString(() => data.model));
    }

    private static void AddUsedComponent(HashSet<string> result, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "<empty>", StringComparison.Ordinal))
            return;

        result.Add(value.Trim());
    }

    private static string TechKey(TechnologyData? tech)
        => SafeString(() => tech?.name) == "<empty>" ? string.Empty : SafeString(() => tech?.name);

    private static GameDate GameDateForYear(CampaignController? controller, int year)
    {
        int startYear = Safe(() => controller?.StartYear ?? CampaignController.Instance?.StartYear ?? 1890, 1890);
        int monthIndex = 0;
        try
        {
            var current = (controller ?? CampaignController.Instance)!.CurrentDate.AsDate();
            if (current.Year == year)
                monthIndex = Math.Clamp(current.Month - 1, 0, 11);
        }
        catch
        {
        }

        return new GameDate
        {
            turn = Math.Max(0, (year - startYear) * 12 + monthIndex)
        };
    }

    private static string NormalizeShipType(ShipType? type)
        => AiDesignCompetitiveness.NormalizeShipType(type);

    private static string NormalizeShipType(string? value)
        => AiDesignCompetitiveness.NormalizeShipType(value);

    private static string PlayerLabel(Player? player)
        => AiDesignCompetitiveness.PlayerLabel(player);

    private static string NationKey(Player? player)
        => SafeString(() => player?.data?.name);

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static string CurrentTurnLabel()
        => AiDesignCompetitiveness.CurrentTurnLabel();

    private static string SharedUsageLabel(CampaignController? controller)
        => CampaignSharedDesignUsageSettings.CurrentModeText();

    private static string DesignUsageLabel(CampaignController? controller)
        => SafeString(() => controller?.designsUsage.ToString());

    private static string AdvancedAiBuilderLabel()
        => ModSettings.AdvancedAiBuilderModeText(ModSettings.AdvancedAiBuilderEnabled);

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "none" : reason.Trim().Replace(" ", string.Empty);

    private static string Fmt(float value)
        => value < 0f || float.IsNaN(value) || float.IsInfinity(value)
            ? "?"
            : value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string BoolText(bool? value)
        => value.HasValue ? value.Value.ToString().ToLowerInvariant() : "?";

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

    private static string FingerprintHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "00000000";

        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private static void TrySetInactive(Ship? ship)
    {
        try
        {
            ship?.SetActive(false);
        }
        catch
        {
        }
    }

    private static void TryErase(Ship? ship)
    {
        try
        {
            if (ship != null && !ship.isErased)
                ship.Erase();
        }
        catch
        {
        }
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

    private static void PopAttempt(AttemptContext context)
    {
        if (ActiveAttempts.Count <= 0)
            return;

        if (ReferenceEquals(ActiveAttempts.Peek(), context))
        {
            ActiveAttempts.Pop();
            return;
        }

        AttemptContext[] attempts = ActiveAttempts.ToArray();
        ActiveAttempts.Clear();
        for (int i = attempts.Length - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(attempts[i], context))
                ActiveAttempts.Push(attempts[i]);
        }
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
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} {message}");
    }

    private static void Log(string message)
        => Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} {message}");

    private static void LogDebug(string message)
        => Melon<UADVanillaPlusMod>.Logger.Msg($"{DebugLogPrefix} {message}");

    private static void LogRaw(string message)
        => Melon<UADVanillaPlusMod>.Logger.Msg(message);

    private static void LogGap(string message)
        => Melon<UADVanillaPlusMod>.Logger.Warning(message);

    internal sealed class AttemptContext
    {
        internal AttemptContext(
            long playerPointer,
            string nationKey,
            string nation,
            string type,
            int year,
            bool prewarming,
            string sharedUsage,
            string designUsage)
        {
            PlayerPointer = playerPointer;
            NationKey = nationKey;
            Nation = nation;
            Type = type;
            Year = year;
            Prewarming = prewarming;
            SharedUsage = sharedUsage;
            DesignUsage = designUsage;
        }

        internal long PlayerPointer { get; }
        internal string NationKey { get; }
        internal string Nation { get; }
        internal string Type { get; }
        internal int Year { get; }
        internal bool Prewarming { get; }
        internal string SharedUsage { get; }
        internal string DesignUsage { get; }
        internal string SelectedDesign { get; set; } = "none";
        internal bool OnlyFallbackBlocked { get; set; }
    }

    private sealed class SharedDesignCandidate
    {
        internal SharedDesignCandidate(Ship.Store store, int year, string name)
        {
            Store = store;
            Year = year;
            Name = name;
        }

        internal Ship.Store Store { get; }
        internal int Year { get; }
        internal string Name { get; }
    }

    internal sealed class SharedDesignBookSnapshot
    {
        private SharedDesignBookSnapshot(List<SharedDesignExistingDesign> designs)
        {
            Designs = designs;
        }

        internal List<SharedDesignExistingDesign> Designs { get; }

        internal static SharedDesignBookSnapshot Capture(Player? player, ShipType? requestedShipType)
        {
            List<SharedDesignExistingDesign> designs = new();
            if (player == null)
                return new SharedDesignBookSnapshot(designs);

            string requestedType = NormalizeShipType(requestedShipType);
            foreach (Ship existing in SafeShipList(player.designs))
            {
                if (existing == null || Safe(() => existing.isErased, false))
                    continue;

                string existingType = NormalizeShipType(existing.shipType);
                if (!string.IsNullOrWhiteSpace(requestedType) &&
                    !string.Equals(requestedType, "<empty>", StringComparison.Ordinal) &&
                    !string.Equals(existingType, requestedType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Ship.Store? existingStore = SafeToStore(existing);
                designs.Add(new SharedDesignExistingDesign(
                    existing,
                    Safe(() => existing.Pointer.ToInt64(), 0L),
                    CandidateSharedDesignId(existing, existingStore),
                    IsSharedDesign(existing),
                    existingType,
                    SharedDesignFingerprint(existing, existingStore)));
            }

            return new SharedDesignBookSnapshot(designs);
        }
    }

    internal sealed class SharedDesignExistingDesign
    {
        internal SharedDesignExistingDesign(Ship ship, long pointer, Il2CppSystem.Guid id, bool isShared, string type, string fingerprint)
        {
            Ship = ship;
            Pointer = pointer;
            Id = id;
            IsShared = isShared;
            Type = type;
            Fingerprint = fingerprint;
        }

        internal Ship Ship { get; }
        internal long Pointer { get; }
        internal Il2CppSystem.Guid Id { get; }
        internal bool IsShared { get; }
        internal string Type { get; }
        internal string Fingerprint { get; }
    }

    private sealed class SharedDesignDuplicateMatch
    {
        internal static readonly SharedDesignDuplicateMatch None = new(null, "none", "none");

        internal SharedDesignDuplicateMatch(Ship? existing, string reason, string fingerprint)
        {
            Existing = existing;
            Reason = reason;
            Fingerprint = fingerprint;
        }

        internal Ship? Existing { get; }
        internal string Reason { get; }
        internal string Fingerprint { get; }
    }

    private sealed class SharedDesignVariantMatch
    {
        internal static readonly SharedDesignVariantMatch None = new(null, "none", "none", Il2CppSystem.Guid.Empty);

        internal SharedDesignVariantMatch(Ship? existing, string existingFingerprint, string candidateFingerprint, Il2CppSystem.Guid sourceId)
        {
            Existing = existing;
            ExistingFingerprint = existingFingerprint;
            CandidateFingerprint = candidateFingerprint;
            SourceId = sourceId;
        }

        internal Ship? Existing { get; }
        internal string ExistingFingerprint { get; }
        internal string CandidateFingerprint { get; }
        internal Il2CppSystem.Guid SourceId { get; }
        internal bool IsVariant => Existing != null;
    }

    private sealed class BlueprintSanitizeResult
    {
        internal int MajorTorpedoesRemoved { get; set; }
        internal int MinesRemoved { get; set; }
        internal int AswRemoved { get; set; }
        internal int ComponentsRemoved { get; set; }
        internal int TechsPruned { get; set; }
        internal List<string> RemovedComponentLabels { get; } = new();

        internal bool HasChanges =>
            MajorTorpedoesRemoved > 0 ||
            MinesRemoved > 0 ||
            AswRemoved > 0 ||
            ComponentsRemoved > 0 ||
            TechsPruned > 0;
    }

    private sealed class SharedDesignValidationResult
    {
        private SharedDesignValidationResult(bool accepted, string stage, string reason, string buildReason, string acceptDetail)
        {
            Accepted = accepted;
            Stage = stage;
            Reason = reason;
            BuildReason = buildReason;
            AcceptDetail = acceptDetail;
        }

        internal bool Accepted { get; }
        internal string Stage { get; }
        internal string Reason { get; }
        internal string BuildReason { get; }
        internal string AcceptDetail { get; }

        internal static SharedDesignValidationResult AcceptedWith(string acceptDetail)
            => new(true, "accepted", "accepted", "none", acceptDetail);

        internal static SharedDesignValidationResult Rejected(string stage, string reason, string buildReason = "none")
            => new(false, stage, reason, buildReason, string.Empty);
    }

    private sealed class TonnageRescueResult
    {
        private TonnageRescueResult(
            string result,
            string reason,
            float originalTons = -1f,
            float finalTons = -1f,
            float limit = -1f,
            VesselEntity.OpRange? originalRange = null,
            VesselEntity.OpRange? finalRange = null,
            float originalSpeed = -1f,
            float finalSpeed = -1f)
        {
            Result = result;
            Reason = reason;
            OriginalTons = originalTons;
            FinalTons = finalTons;
            Limit = limit;
            OriginalRange = originalRange;
            FinalRange = finalRange;
            OriginalSpeed = originalSpeed;
            FinalSpeed = finalSpeed;
        }

        internal static TonnageRescueResult None { get; } = new("none", "not-attempted");

        internal string Result { get; }
        internal string Reason { get; }
        internal float OriginalTons { get; }
        internal float FinalTons { get; }
        internal float Limit { get; }
        internal VesselEntity.OpRange? OriginalRange { get; }
        internal VesselEntity.OpRange? FinalRange { get; }
        internal float OriginalSpeed { get; }
        internal float FinalSpeed { get; }

        internal string Detail
        {
            get
            {
                string overweight = OriginalTons > 0f && Limit > 0f
                    ? $"overweight={Math.Max(0f, (OriginalTons / Limit - 1f) * 100f).ToString("0.#", CultureInfo.InvariantCulture)}%"
                    : "overweight=?";
                return string.Join(
                    " ",
                    overweight,
                    $"range={RangeChangeText()}",
                    $"speed={SpeedChangeText()}",
                    $"tons={Fmt(OriginalTons)}->{Fmt(FinalTons)}",
                    $"limit={Fmt(Limit)}",
                    $"result={Result}",
                    $"reason={LogToken(Reason)}");
            }
        }

        internal static TonnageRescueResult Accepted(
            float originalTons,
            float finalTons,
            float limit,
            VesselEntity.OpRange originalRange,
            VesselEntity.OpRange finalRange,
            float originalSpeed,
            float finalSpeed)
            => new("accepted", "accepted", originalTons, finalTons, limit, originalRange, finalRange, originalSpeed, finalSpeed);

        internal static TonnageRescueResult Rejected(
            string reason,
            float originalTons,
            float finalTons,
            float limit,
            VesselEntity.OpRange originalRange,
            VesselEntity.OpRange finalRange,
            float originalSpeed,
            float finalSpeed)
            => new("reject", reason, originalTons, finalTons, limit, originalRange, finalRange, originalSpeed, finalSpeed);

        internal static TonnageRescueResult Skipped(string reason, float originalTons = -1f, float finalTons = -1f, float limit = -1f)
            => new("skip", reason, originalTons, finalTons, limit);

        private string RangeChangeText()
        {
            if (!OriginalRange.HasValue || !FinalRange.HasValue)
                return "?";

            return $"{OriginalRange.Value}->{FinalRange.Value}";
        }

        private string SpeedChangeText()
        {
            if (OriginalSpeed < 0f || FinalSpeed < 0f)
                return "?";

            return $"{OriginalSpeed.ToString("0.#", CultureInfo.InvariantCulture)}->{FinalSpeed.ToString("0.#", CultureInfo.InvariantCulture)}";
        }
    }

    private readonly struct InternalWeightFailureInfo
    {
        internal static InternalWeightFailureInfo Empty { get; } = new(-1f, -1f);

        internal InternalWeightFailureInfo(float originalTons, float weight)
        {
            OriginalTons = originalTons;
            Weight = weight;
        }

        internal float OriginalTons { get; }
        internal float Weight { get; }
    }

    private sealed class InternalWeightRescueResult
    {
        private InternalWeightRescueResult(
            string result,
            string reason,
            float originalTons = -1f,
            float weight = -1f,
            float finalTons = -1f,
            float limit = -1f)
        {
            Result = result;
            Reason = reason;
            OriginalTons = originalTons;
            Weight = weight;
            FinalTons = finalTons;
            Limit = limit;
        }

        internal static InternalWeightRescueResult None { get; } = new("none", "not-attempted");

        internal string Result { get; }
        internal string Reason { get; }
        internal float OriginalTons { get; }
        internal float Weight { get; }
        internal float FinalTons { get; }
        internal float Limit { get; }

        internal string Detail =>
            string.Join(
                " ",
                $"result={Result}",
                $"reason={LogToken(Reason)}",
                $"originalTons={Fmt(OriginalTons)}",
                $"weight={Fmt(Weight)}",
                $"finalTons={Fmt(FinalTons)}",
                $"limit={Fmt(Limit)}");

        internal static InternalWeightRescueResult Accepted(float originalTons, float weight, float finalTons, float limit)
            => new("accepted", "accepted", originalTons, weight, finalTons, limit);

        internal static InternalWeightRescueResult Rejected(string reason, float originalTons, float weight, float finalTons, float limit)
            => new("reject", reason, originalTons, weight, finalTons, limit);

        internal static InternalWeightRescueResult Skipped(string reason, float originalTons = -1f, float weight = -1f, float finalTons = -1f, float limit = -1f)
            => new("skip", reason, originalTons, weight, finalTons, limit);
    }

    private readonly struct GunLengthCaps
    {
        private GunLengthCaps(float large, float small, float casemate)
        {
            Large = large;
            Small = small;
            Casemate = casemate;
        }

        private float Large { get; }
        private float Small { get; }
        private float Casemate { get; }

        internal float For(GunLengthCategory category)
            => category switch
            {
                GunLengthCategory.Casemate => Casemate,
                GunLengthCategory.Small => Small,
                GunLengthCategory.Large => Large,
                _ => 0f
            };

        internal static GunLengthCaps ForPlayer(Player? player)
        {
            float large = 2.5f;
            float small = 2f;
            float casemate = 2f;

            try
            {
                var techs = player?.technologiesResearchedActual;
                if (techs == null)
                    return new GunLengthCaps(large, small, casemate);

                var list = new Il2CppSystem.Collections.Generic.List<Technology>(techs);
                foreach (Technology tech in list)
                {
                    string effect = SafeString(() => tech?.data?.effect);
                    large = Math.Max(large, EffectMax(effect, "tech_gun_length_limit"));
                    small = Math.Max(small, EffectMax(effect, "tech_gun_length_limit_small"));
                    casemate = Math.Max(casemate, EffectMax(effect, "tech_gun_length_limit_casemates"));
                }
            }
            catch
            {
            }

            return new GunLengthCaps(large, small, casemate);
        }

        private static float EffectMax(string effect, string key)
        {
            float result = float.MinValue;
            int searchFrom = 0;
            string needle = key + "(";
            while (searchFrom < effect.Length)
            {
                int index = effect.IndexOf(needle, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                int start = index + needle.Length;
                int end = effect.IndexOf(')', start);
                if (end < 0)
                    break;

                string raw = effect[start..end].Trim();
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    result = Math.Max(result, value);

                searchFrom = end + 1;
            }

            return result == float.MinValue ? 0f : result;
        }
    }

    private enum GunLengthCategory
    {
        None,
        Large,
        Small,
        Casemate
    }

    private enum SharedDesignDowngradeFamily
    {
        None,
        Fuel,
        Boiler,
        ArmorQuality,
        TorpedoSize,
        TorpedoBelt,
        Radio,
        RangefinderCoinc,
        RangefinderStereo,
        Radar,
        Steering,
        AuxEngine,
        DriveShaft,
        Rudder,
        TurretTraverse,
        GunReload,
        Bulkheads,
        AntiFlooding,
        Propellant
    }

    private sealed class ComponentDowngradeAction
    {
        internal ComponentDowngradeAction(
            CompType slot,
            ComponentData from,
            ComponentData? to,
            SharedDesignDowngradeFamily family)
        {
            Slot = slot;
            From = from;
            To = to;
            Family = family;
        }

        internal CompType Slot { get; }
        internal ComponentData From { get; }
        internal ComponentData? To { get; }
        internal SharedDesignDowngradeFamily Family { get; }
        internal bool Applied { get; set; }

        internal string Label => $"{FamilyLabel(Family)}:{ComponentToken(From)}->{(To == null ? "none" : ComponentToken(To))}";
    }

    private static string FamilyLabel(SharedDesignDowngradeFamily family)
        => family switch
        {
            SharedDesignDowngradeFamily.Fuel => "fuel",
            SharedDesignDowngradeFamily.Boiler => "boiler",
            SharedDesignDowngradeFamily.ArmorQuality => "armor",
            SharedDesignDowngradeFamily.TorpedoSize => "torpedo_size",
            SharedDesignDowngradeFamily.TorpedoBelt => "torpedoBelt",
            SharedDesignDowngradeFamily.Radio => "radio",
            SharedDesignDowngradeFamily.RangefinderCoinc => "rangefinderCoinc",
            SharedDesignDowngradeFamily.RangefinderStereo => "rangefinder",
            SharedDesignDowngradeFamily.Radar => "radar",
            SharedDesignDowngradeFamily.Steering => "steering",
            SharedDesignDowngradeFamily.AuxEngine => "aux",
            SharedDesignDowngradeFamily.DriveShaft => "drive",
            SharedDesignDowngradeFamily.Rudder => "rudder",
            SharedDesignDowngradeFamily.TurretTraverse => "turretTraverse",
            SharedDesignDowngradeFamily.GunReload => "gunReload",
            SharedDesignDowngradeFamily.Bulkheads => "bulkheads",
            SharedDesignDowngradeFamily.AntiFlooding => "antiFlooding",
            SharedDesignDowngradeFamily.Propellant => "propellant",
            _ => "unknown"
        };

    private sealed class CandidateSummary
    {
        private readonly Dictionary<string, int> buildReasons = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> rejectDetails = new();
        private readonly List<string> acceptedDetails = new();

        internal int Total { get; set; }
        internal int TypeMatch { get; set; }
        internal int YearMatch { get; set; }
        internal int FromStore { get; set; }
        internal int BuildReject { get; set; }
        internal int TechReject { get; set; }
        internal int DuplicateReject { get; set; }
        internal int Accepted { get; set; }

        internal void AddBuildReason(string reason)
        {
            string normalized = NormalizeReason(reason);
            buildReasons[normalized] = buildReasons.GetValueOrDefault(normalized) + 1;
        }

        internal int BuildReasonCount(string reason)
            => buildReasons.GetValueOrDefault(NormalizeReason(reason));

        internal int OtherBuildRejectCount(params string[] knownReasons)
        {
            HashSet<string> known = knownReasons
                .Select(NormalizeReason)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int knownCount = buildReasons
                .Where(pair => known.Contains(pair.Key))
                .Sum(pair => pair.Value);
            return Math.Max(0, BuildReject - knownCount);
        }

        internal void AddReject(string candidate, string stage, string reason)
        {
            if (rejectDetails.Count >= MaxRejectDetails)
                return;

            rejectDetails.Add($"{candidate}:{stage}:{FormatRejectReason(reason)}");
        }

        internal void AddAccepted(string candidate)
        {
            if (acceptedDetails.Count >= MaxRejectDetails)
                return;

            acceptedDetails.Add(candidate);
        }

        internal string BuildReasonText()
        {
            if (buildReasons.Count == 0)
                return "{}";

            return "{" + string.Join(",", buildReasons.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")) + "}";
        }

        internal string RejectDetailText()
        {
            if (rejectDetails.Count == 0 && acceptedDetails.Count == 0)
                return "none";

            string rejects = rejectDetails.Count == 0 ? "none" : string.Join(";", rejectDetails);
            string accepted = acceptedDetails.Count == 0 ? "none" : string.Join(";", acceptedDetails);
            int hiddenRejects = Math.Max(0, BuildReject + TechReject + DuplicateReject + Math.Max(0, YearMatch - FromStore) - rejectDetails.Count);
            string suffix = hiddenRejects > 0 ? $";+{hiddenRejects} more rejects" : string.Empty;
            return $"rejects={rejects}{suffix};accepted={accepted}";
        }

        internal string NoCandidateReason()
        {
            if (Total <= 0)
                return "noNationDesigns";
            if (TypeMatch <= 0)
                return "typeMismatch";
            if (YearMatch <= 0)
                return "yearWindow";
            if (FromStore <= 0)
                return "fromStore";
            if (Accepted > 0)
                return "accepted";
            if (DuplicateReject > 0 && BuildReject <= 0 && TechReject <= 0)
                return "duplicate";
            if (BuildReject > 0 && TechReject <= 0)
                return "buildReject";
            if (TechReject > 0 && BuildReject <= 0)
                return "techReject";
            return "mixedRejects";
        }
    }

    private sealed class SharedDesignGapRecord
    {
        private SharedDesignGapRecord(
            string key,
            string turn,
            int year,
            string nation,
            string nationKey,
            string type,
            int suggestedYear,
            int windowStart,
            int windowEnd,
            float maxShipyard,
            float techTonnage,
            float tonnageLimit,
            float targetTons,
            int shipyardReject,
            int tonnageReject,
            int techReject,
            int duplicateReject,
            int invalidReject,
            int otherReject,
            string reason)
        {
            Key = key;
            Turn = turn;
            Year = year;
            Nation = nation;
            NationKey = nationKey;
            Type = type;
            SuggestedYear = suggestedYear;
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            MaxShipyard = maxShipyard;
            TechTonnage = techTonnage;
            TonnageLimit = tonnageLimit;
            TargetTons = targetTons;
            ShipyardReject = shipyardReject;
            TonnageReject = tonnageReject;
            TechReject = techReject;
            DuplicateReject = duplicateReject;
            InvalidReject = invalidReject;
            OtherReject = otherReject;
            Reason = reason;
        }

        internal string Key { get; }
        internal string Turn { get; }
        internal int Year { get; }
        internal string Nation { get; }
        internal string NationKey { get; }
        internal string Type { get; }
        internal int SuggestedYear { get; }
        internal int WindowStart { get; }
        internal int WindowEnd { get; }
        internal float MaxShipyard { get; }
        internal float TechTonnage { get; }
        internal float TonnageLimit { get; }
        internal float TargetTons { get; }
        internal int ShipyardReject { get; }
        internal int TonnageReject { get; }
        internal int TechReject { get; }
        internal int DuplicateReject { get; }
        internal int InvalidReject { get; }
        internal int OtherReject { get; }
        internal string Reason { get; }

        internal static SharedDesignGapRecord Create(
            Player player,
            ShipType? shipType,
            int suggestedYear,
            CandidateSummary summary,
            string turn)
        {
            string type = NormalizeShipType(shipType);
            string nation = PlayerLabel(player);
            string nationKey = NationKey(player);
            float maxShipyard = Safe(() => player.MaxShipyard(), -1f);
            float techTonnage = Safe(() => shipType == null ? -1f : player.TechTonnage(shipType), -1f);
            float tonnageLimit = Safe(() => shipType == null ? -1f : player.TonnageLimit(shipType), -1f);
            float targetTons = RecommendedTargetTons(maxShipyard, techTonnage, tonnageLimit);
            int shipyardReject = summary.BuildReasonCount("shipyard");
            int tonnageReject = summary.BuildReasonCount("tonnage");
            int invalidReject = summary.BuildReasonCount("invalidDesign") + summary.BuildReasonCount("valid");
            int otherReject = summary.OtherBuildRejectCount("shipyard", "tonnage", "invalidDesign", "valid");
            string reason = GapReason(summary, shipyardReject, tonnageReject, invalidReject, otherReject);
            string key = $"{turn}|{nationKey}|{type}|{suggestedYear}";

            return new SharedDesignGapRecord(
                key,
                turn,
                Safe(() => (CampaignController.Instance?.CurrentDate.AsDate().Year) ?? suggestedYear, suggestedYear),
                nation,
                nationKey,
                type,
                suggestedYear,
                suggestedYear - SharedDesignPastYearWindow,
                suggestedYear + SharedDesignFutureYearWindow,
                maxShipyard,
                techTonnage,
                tonnageLimit,
                targetTons,
                shipyardReject,
                tonnageReject,
                summary.TechReject,
                summary.DuplicateReject,
                invalidReject,
                otherReject,
                reason);
        }

        internal SharedDesignGapRecord Merge(SharedDesignGapRecord other)
            => new(
                Key,
                Turn,
                Year,
                Nation,
                NationKey,
                Type,
                SuggestedYear,
                WindowStart,
                WindowEnd,
                MaxShipyard,
                TechTonnage,
                TonnageLimit,
                TargetTons,
                ShipyardReject + other.ShipyardReject,
                TonnageReject + other.TonnageReject,
                TechReject + other.TechReject,
                DuplicateReject + other.DuplicateReject,
                InvalidReject + other.InvalidReject,
                OtherReject + other.OtherReject,
                GapReasonFromCounts(
                    ShipyardReject + other.ShipyardReject,
                    TonnageReject + other.TonnageReject,
                    TechReject + other.TechReject,
                    DuplicateReject + other.DuplicateReject,
                    InvalidReject + other.InvalidReject,
                    OtherReject + other.OtherReject));

        internal string ToLogLine()
            => "[AI SharedDesign Gap] " + string.Join(
                " ",
                $"turn={LogToken(Turn)}",
                $"year={Year}",
                $"nation={Nation}",
                $"key={LogToken(NationKey)}",
                $"type={Type}",
                $"targetTons<={Fmt(TargetTons)}",
                $"maxShipyard={Fmt(MaxShipyard)}",
                $"techTonnage={Fmt(TechTonnage)}",
                $"tonnageLimit={Fmt(TonnageLimit)}",
                $"rejected=shipyard:{ShipyardReject},tonnage:{TonnageReject},tech:{TechReject},duplicate:{DuplicateReject},invalid:{InvalidReject},other:{OtherReject}",
                $"reason={Reason}",
                $"suggestedYear={SuggestedYear}",
                $"window={WindowStart}-{WindowEnd}");

        private static float RecommendedTargetTons(float maxShipyard, float techTonnage, float tonnageLimit)
        {
            List<float> caps = new();
            if (maxShipyard > 0f)
                caps.Add(maxShipyard);
            if (techTonnage > 0f)
                caps.Add(techTonnage);
            else if (tonnageLimit > 0f)
                caps.Add(tonnageLimit);

            return caps.Count == 0 ? -1f : caps.Min();
        }

        private static string GapReason(
            CandidateSummary summary,
            int shipyardReject,
            int tonnageReject,
            int invalidReject,
            int otherReject)
        {
            if (summary.FromStore <= 0 || summary.YearMatch <= 0 || summary.TypeMatch <= 0 || summary.Total <= 0)
                return "noAcceptedSharedDesign";

            return GapReasonFromCounts(
                shipyardReject,
                tonnageReject,
                summary.TechReject,
                summary.DuplicateReject,
                invalidReject,
                otherReject);
        }

        private static string GapReasonFromCounts(
            int shipyardReject,
            int tonnageReject,
            int techReject,
            int duplicateReject,
            int invalidReject,
            int otherReject)
        {
            int oversized = shipyardReject + tonnageReject;
            if (oversized > 0 && techReject <= 0 && duplicateReject <= 0 && invalidReject <= 0 && otherReject <= 0)
                return "onlyOversized";
            if (techReject > 0 && oversized <= 0 && duplicateReject <= 0 && invalidReject <= 0 && otherReject <= 0)
                return "onlyTechBlocked";
            if (duplicateReject > 0 && oversized <= 0 && techReject <= 0 && invalidReject <= 0 && otherReject <= 0)
                return "onlyDuplicate";
            if (invalidReject > 0 && oversized <= 0 && techReject <= 0 && duplicateReject <= 0 && otherReject <= 0)
                return "onlyInvalid";

            return "mixedRejects";
        }
    }

    private static string FormatRejectReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "none"
            : reason.Trim().Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

    private sealed class TechIdentitySet
    {
        private readonly HashSet<long> pointers = new();
        private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        internal int Count => Math.Max(pointers.Count, names.Count);

        internal void Add(TechnologyData? tech)
        {
            if (tech == null)
                return;

            long pointer = Safe(() => tech.Pointer.ToInt64(), 0L);
            if (pointer != 0L)
                pointers.Add(pointer);

            string name = TechKey(tech);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        internal bool Contains(TechnologyData? tech)
        {
            if (tech == null)
                return false;

            long pointer = Safe(() => tech.Pointer.ToInt64(), 0L);
            if (pointer != 0L && pointers.Contains(pointer))
                return true;

            string name = TechKey(tech);
            return !string.IsNullOrWhiteSpace(name) && names.Contains(name);
        }
    }
}

[HarmonyPatch]
internal static class CampaignSharedDesignTryTakeDiagnosticsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI SharedDesign] TryTakeSharedDesign target not found; shared-design attempt diagnostics disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "TryTakeSharedDesign", new[] { typeof(Player), typeof(ShipType), typeof(bool) });

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(CampaignController __instance, Player player, ShipType shipType, bool prewarming, ref bool __result, ref CampaignSharedDesignDiagnosticsPatch.AttemptContext? __state)
    {
        if (CampaignAiDesignGenerationPreflight.ShouldSkipSharedDesign(player, shipType, prewarming))
        {
            __result = false;
            __state = null;
            return false;
        }

        __state = CampaignSharedDesignDiagnosticsPatch.BeginAttempt(__instance, player, shipType, prewarming);
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance, Player player, ShipType shipType, bool prewarming, ref bool __result, CampaignSharedDesignDiagnosticsPatch.AttemptContext? __state)
    {
        bool vanillaResult = __result;
        CampaignSharedDesignDiagnosticsPatch.ApplyOnlyFallbackBlock(__instance, player, shipType, prewarming, __state, ref __result);
        CampaignSharedDesignDiagnosticsPatch.EndAttempt(__state, vanillaResult);
    }
}

[HarmonyPatch]
internal static class CampaignSharedDesignGapNextTurnCompletionPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI SharedDesign] NextTurn MoveNext target not found; deferred gap summary flush will use fallback timing.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignSharedDesignDiagnosticsPatch.NextTurnMoveNextTarget();

    [HarmonyPostfix]
    private static void Postfix(bool __result)
    {
        if (!__result)
            CampaignSharedDesignDiagnosticsPatch.FlushPendingSharedDesignGapSummariesAfterNextTurn();
    }
}

[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.OnLoadingScreenHide))]
internal static class CampaignSharedDesignGapLoadResetPatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => CampaignSharedDesignDiagnosticsPatch.ResetTurnScopedSharedDesignDiagnostics("campaign load");
}

[HarmonyPatch]
internal static class CampaignSharedDesignGetSharedDesignDiagnosticsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI SharedDesign] GetSharedDesign target not found; shared-design candidate diagnostics disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), nameof(CampaignController.GetSharedDesign), new[] { typeof(Player), typeof(ShipType), typeof(int), typeof(bool), typeof(bool) });

    [HarmonyPrefix]
    private static void Prefix(
        CampaignController __instance,
        Player player,
        ShipType shipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        ref CampaignSharedDesignDiagnosticsPatch.SharedDesignBookSnapshot? __state)
    {
        __state = CampaignSharedDesignDiagnosticsPatch.CaptureExistingSharedDesigns(player, shipType);
        CampaignSharedDesignDiagnosticsPatch.TraceGetSharedDesign(__instance, player, shipType, year, checkTech, isEarlySavedShip, __state);
    }

    [HarmonyPostfix]
    private static void Postfix(
        CampaignController __instance,
        Player player,
        ShipType shipType,
        int year,
        bool checkTech,
        bool isEarlySavedShip,
        CampaignSharedDesignDiagnosticsPatch.SharedDesignBookSnapshot? __state,
        ref Ship? __result)
    {
        CampaignSharedDesignDiagnosticsPatch.ApplyRelaxedSharedDesignTechMatch(__instance, player, shipType, year, checkTech, isEarlySavedShip, __state, ref __result);
        CampaignSharedDesignDiagnosticsPatch.BlockDuplicateSharedDesignResult(player, shipType, year, __state, ref __result);
        CampaignSharedDesignDiagnosticsPatch.FinalizeAcceptedSharedDesignBlueprint(player, shipType, checkTech, isEarlySavedShip, __result);
        CampaignSharedDesignDiagnosticsPatch.ApplyPostCleanupSharedDesignAcceptanceGate(player, shipType, year, __state, null, ref __result);
        CampaignSharedDesignDiagnosticsPatch.NormalizeImportedSharedDesignDate(__instance, player, shipType, year, checkTech, isEarlySavedShip, __result);
        CampaignSharedDesignDiagnosticsPatch.TraceGetSharedDesignResult(player, shipType, year, __result);
    }
}
