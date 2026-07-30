using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UADVanillaPlus.Services;

namespace UADVanillaPlus.Harmony;

// Diagnostic-only tracing for AI design maintenance. GenerateRandomDesigns is
// an IEnumerator, so the result is summarized at the next downstream
// BuildNewShips call after vanilla design maintenance has had a chance to run.
internal static class CampaignAiDesignGenerationDiagnostics
{
    private const string LogPrefix = "[AI DesignGen]";
    private static bool VerboseDiagnostics => false;
    private const float RefreshFreshnessThresholdYears = 1f;
    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly Dictionary<long, GenerationContext> ActiveByPlayer = new();
    private static readonly Dictionary<Type, MemberInfo?> DisplayClassPlayerMemberByType = new();
    private static readonly Dictionary<string, RandomAttemptInfo> ActiveRandomAttempts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedExpectedShipgenFailures = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, RandomStateMachineFields> RandomStateMachineFieldsByType = new();
    private static readonly Dictionary<string, GenerateMovePrefixInfo> ActiveGenerateMoves = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, GenerateStateMachineFields> GenerateStateMachineFieldsByType = new();
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFreshnessOverrides = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedCountGateRefreshes = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedReplacementCountBypasses = new(StringComparer.Ordinal);

    internal static void BeginGenerate(CampaignController? controller, Player? player, bool prewarming)
    {
        if (!ShouldTrace(player))
            return;

        try
        {
            long playerPointer = PlayerPointer(player!);
            GenerationContext context = new(
                playerPointer,
                player!,
                PlayerLabel(player),
                CurrentTurnIndex(),
                CurrentTurnLabel(),
                prewarming,
                SharedUsageLabel(controller),
                DesignUsageLabel(controller),
                DesignSnapshot.For(player!));
            ActiveByPlayer[playerPointer] = context;

            if (VerboseDiagnostics)
            {
                Log(
                    $"start turn={context.TurnLabel} nation={context.Nation} prewarming={BoolText(context.Prewarming)} advancedAiBuilder={AdvancedAiBuilderLabel()} designUsage={context.DesignUsage} sharedUsage={context.SharedUsage} before={context.Before.CountText()} newestBefore={context.Before.NewestText()} buildableBefore={context.Before.BuildableText()} hulls={context.Before.HullAvailabilityText()}.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce("begin", $"begin failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void CompleteBeforeBuild(CampaignController? controller, Player? player)
    {
        if (!ShouldTrace(player))
            return;

        long playerPointer = PlayerPointer(player!);
        if (!ActiveByPlayer.TryGetValue(playerPointer, out GenerationContext? context))
            return;

        try
        {
            DesignSnapshot after = DesignSnapshot.For(player!);
            List<Ship> newDesigns = after.Designs
                .Where(design => !context.Before.DesignKeys.Contains(DesignKey(design)))
                .OrderBy(design => NormalizeShipType(design.shipType), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(design => Safe(() => EffectiveDesignDate(design).turn, -1))
                .ThenBy(design => ShipLabel(design), StringComparer.Ordinal)
                .ToList();

            foreach (Ship newDesign in newDesigns.Where(design => !IsSharedDesign(design)))
                CampaignGeneratedDesignSanitizer.SanitizeAiGeneratedDesign(newDesign, player!, "maintenance", context.TurnLabel);

            GenerateGateSnapshot gate = context.GateSnapshotOrDefault(player!);
            string gateReason = GateReasonFor(context, newDesigns, gate);
            string outcome = OutcomeFor(context, newDesigns);
            string source = SourceFor(context, newDesigns);
            string newText = newDesigns.Count == 0 ? "none" : string.Join(",", newDesigns.Select(DesignLabel));
            string freshCover = context.FreshCoverCandidateText();
            string freshCoveredTypes = context.FreshCoveredTypeText();
            string replacementAfterFresh = context.ReplacementAfterFreshText(gate);

            if (VerboseDiagnostics)
            {
                Log(
                    $"result turn={context.TurnLabel} nation={context.Nation} prewarming={BoolText(context.Prewarming)} advancedAiBuilder={AdvancedAiBuilderLabel()} designUsage={context.DesignUsage} sharedUsage={context.SharedUsage} before={context.Before.CountText()} after={after.CountText()} newestBefore={context.Before.NewestText()} newestAfter={after.NewestText()} buildableBefore={context.Before.BuildableText()} buildableAfter={after.BuildableText()} hulls={after.HullAvailabilityText()} new={newText} source={source} missingCandidates={context.Missing.Summary()} replacementCandidates={context.Replacement.Summary()} freshCoverCandidates={context.Stale.Summary()} staleCandidates=renamedToFreshCover selectedOrAcceptedTypes={context.AcceptedCandidateText()} sharedAttempted={context.SharedAttempted} sharedAccepted={context.SharedAccepted} randomAttempted={RandomAttemptText(context, newDesigns)} randomAttempts={context.RandomAttempts} randomSuccess={context.RandomSuccesses} randomFailures={context.RandomFailures} randomErased={context.RandomErased} outcome={outcome} gateReason={gateReason} designCount={gate.DesignCount} buildableTypes={gate.BuildableTypesText()} representedTypes={gate.RepresentedTypesText()} missingAfterExisting={gate.MissingAfterExistingText()} freshCoveredTypes={freshCoveredTypes} replacementAfterFresh={replacementAfterFresh} freshCover={freshCover} selectedType={context.SelectedTypeText(gate)} state={context.StateText(gate)} tries={context.TriesText(gate)} i={context.IText(gate)} j={context.JText(gate)}.");

                if (newDesigns.Count == 0)
                {
                    Log(
                        $"gate turn={context.TurnLabel} nation={context.Nation} prewarming={BoolText(context.Prewarming)} advancedAiBuilder={AdvancedAiBuilderLabel()} reason={gateReason} designCount={gate.DesignCount} buildableTypes={gate.BuildableTypesText()} representedTypes={gate.RepresentedTypesText()} missingAfterExisting={gate.MissingAfterExistingText()} freshCoveredTypes={freshCoveredTypes} replacementAfterFresh={replacementAfterFresh} selectedType={context.SelectedTypeText(gate)} state={context.StateText(gate)} tries={context.TriesText(gate)} i={context.IText(gate)} j={context.JText(gate)} predicates=missing:{context.Missing.Summary()} replacement:{context.Replacement.Summary()} freshCover:{context.Stale.Summary()} freshCoverDetails={freshCover} sharedAttempted={context.SharedAttempted} randomAttempts={context.RandomAttempts}.");
                }
            }
            else if (ShouldLogCompactResult(context, newDesigns, source, outcome, gateReason))
            {
                Log(
                    $"result turn={context.TurnLabel} nation={context.Nation} prewarming={BoolText(context.Prewarming)} new={newText} source={source} outcome={outcome} gateReason={gateReason} before={context.Before.CountText()} after={after.CountText()} randomAttempts={context.RandomAttempts} randomSuccess={context.RandomSuccesses} randomFailures={context.RandomFailures} randomErased={context.RandomErased}.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce("complete", $"complete failed for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CampaignSharedDesignDiagnosticsPatch.RecordDesignGenerationCompleted(controller, player);
            ActiveByPlayer.Remove(playerPointer);
        }
    }

    internal static void RecordSharedAttempt(Player? player, ShipType? shipType)
    {
        if (!TryGetContext(player, out GenerationContext? context))
            return;

        context!.SharedAttempted++;
        context.SharedTypes.Add(NormalizeShipType(shipType));
    }

    internal static void RecordSharedResult(long playerPointer, string? type, bool accepted)
    {
        if (!ActiveByPlayer.TryGetValue(playerPointer, out GenerationContext? context))
            return;

        if (accepted)
            context.SharedAccepted++;

        if (!string.IsNullOrWhiteSpace(type))
            context.SharedTypes.Add(NormalizeShipType(type));
    }

    internal static void RecordShipTypePredicateVanilla(object displayClass, string branch, ShipType? candidate, bool result)
    {
        if (!TryGetContext(displayClass, out GenerationContext? context))
            return;

        string type = NormalizeShipType(candidate);
        context!.Branch(branch).RecordVanilla(type, type, result);
    }

    internal static void RecordShipTypePredicateFinal(object displayClass, string branch, ShipType? candidate, bool result)
    {
        if (!TryGetContext(displayClass, out GenerationContext? context))
            return;

        string type = NormalizeShipType(candidate);
        context!.Branch(branch).RecordFinal(type, type, result);
        if (result)
            context.AcceptedTypes.Add(type);
    }

    internal static void RecordStalePredicateVanilla(object displayClass, Ship? design, bool result)
    {
        if (!TryGetContext(displayClass, out GenerationContext? context))
            return;

        string type = NormalizeShipType(design?.shipType);
        context!.Stale.RecordVanilla(type, FreshCoverLabel(design), result);
    }

    internal static void RecordStalePredicateFinal(object displayClass, Ship? design, bool result)
    {
        if (!TryGetContext(displayClass, out GenerationContext? context))
            return;

        string type = NormalizeShipType(design?.shipType);
        context!.Stale.RecordFinal(type, FreshCoverLabel(design), result);
        if (result)
            context.AcceptedTypes.Add(type);
    }

    internal static void ApplyMissingPredicateRefreshOverride(object displayClass, ShipType? candidate, ref bool result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled || result || candidate == null)
            return;

        if (!TryGetContext(displayClass, out GenerationContext? context) || context == null || context.Prewarming)
            return;

        Player? player = TryGetDisplayClassPlayer(displayClass);
        if (!ShouldTrace(player))
            return;

        string candidateType = NormalizeShipType(candidate);
        if (!TrySelectRefreshCandidate(player!, out string refreshType, out Ship? design, out float ageYears, out _))
            return;
        if (!string.Equals(candidateType, refreshType, StringComparison.OrdinalIgnoreCase))
            return;

        result = true;
        context.AcceptedTypes.Add(refreshType);

        string key = $"{context.PlayerPointer}:{context.Turn}:{refreshType}:count-refresh";
        if (LoggedCountGateRefreshes.Add(key))
        {
            TrimLogSets();
            Log(
                $"count-gate-refresh nation={context.Nation} type={refreshType} design={LogToken(ShipLabel(design))} age={Fmt(ageYears)} reason=staleRepresentedType thresholdYears={Fmt(RefreshFreshnessThresholdYears)}.");
        }
    }

    internal static void ApplyRandomFromShipTypeRefreshOverride(ref ShipType? result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled || result != null || ActiveByPlayer.Count != 1)
            return;

        GenerationContext context = ActiveByPlayer.Values.First();
        if (context.Prewarming ||
            context.ReplacementCountBypassApplied ||
            context.SharedAttempted > 0 ||
            context.RandomAttempts > 0 ||
            !ShouldTrace(context.Player))
        {
            return;
        }

        if (!TrySelectRefreshCandidate(context.Player, out string refreshType, out Ship? design, out float ageYears, out int designCount))
            return;

        ShipType? selectedType = FindShipType(refreshType);
        if (selectedType == null)
            return;

        result = selectedType;
        context.ReplacementCountBypassApplied = true;
        context.ReplacementCountBypassType = refreshType;
        context.AcceptedTypes.Add(refreshType);

        string key = $"{context.PlayerPointer}:{context.Turn}:{refreshType}:replacement-count-bypass";
        if (LoggedReplacementCountBypasses.Add(key))
        {
            TrimLogSets();
            Log(
                $"replacement-count-bypass nation={context.Nation} type={refreshType} design={DesignLabel(design)} age={Fmt(ageYears)} designCount={designCount} thresholdYears={Fmt(RefreshFreshnessThresholdYears)} reason=staleRepresentedType.");
        }
    }

    internal static void ApplyExistingDesignTypeSelectorRefreshOverride(Ship? design, ref ShipType? result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled || design == null || result == null)
            return;

        GenerationContext? context = null;
        Player? player = Safe(() => design.player, null);
        if (player != null)
            TryGetContext(player, out context);
        if (context == null && ActiveByPlayer.Count == 1)
            context = ActiveByPlayer.Values.First();
        if (context == null ||
            context.Prewarming ||
            !ShouldTrace(context.Player))
        {
            return;
        }

        string resultType = NormalizeShipType(result);
        if (context.ReplacementCountBypassApplied)
        {
            if (string.Equals(resultType, context.ReplacementCountBypassType, StringComparison.OrdinalIgnoreCase))
                result = null;
            return;
        }

        if (!TrySelectRefreshCandidate(context.Player, out string refreshType, out Ship? refreshDesign, out float ageYears, out int designCount))
            return;
        if (!string.Equals(resultType, refreshType, StringComparison.OrdinalIgnoreCase))
            return;

        result = null;
        context.ReplacementCountBypassApplied = true;
        context.ReplacementCountBypassType = refreshType;
        context.AcceptedTypes.Add(refreshType);

        string key = $"{context.PlayerPointer}:{context.Turn}:{refreshType}:replacement-count-bypass-selector";
        if (LoggedReplacementCountBypasses.Add(key))
        {
            TrimLogSets();
            Log(
                $"replacement-count-bypass nation={context.Nation} type={refreshType} design={DesignLabel(refreshDesign)} age={Fmt(ageYears)} designCount={designCount} thresholdYears={Fmt(RefreshFreshnessThresholdYears)} reason=staleRepresentedType.");
        }
    }

    internal static void ApplyFreshnessOverride(object displayClass, Ship? design, ref bool result)
    {
        if (!ModSettings.AdvancedAiBuilderEnabled || !result || design == null)
            return;

        if (!TryGetContext(displayClass, out GenerationContext? context) || context == null || context.Prewarming)
            return;

        Player? player = TryGetDisplayClassPlayer(displayClass);
        if (!ShouldTrace(player))
            return;

        float ageYears = DesignCreatedAgeYears(design);
        if (float.IsNaN(ageYears) || ageYears < RefreshFreshnessThresholdYears)
            return;

        result = false;

        string type = NormalizeShipType(design.shipType);
        string key = $"{context.PlayerPointer}:{context.Turn}:{type}:{DesignKey(design)}:freshness";
        if (LoggedFreshnessOverrides.Add(key))
        {
            TrimLogSets();
            Log(
                $"freshness-override nation={context.Nation} type={type} design={LogToken(ShipLabel(design))} age={Fmt(ageYears)} vanillaFresh=true vpFresh=false thresholdYears={Fmt(RefreshFreshnessThresholdYears)}.");
        }
    }

    internal static void WarnMissingPredicateTarget(string branch)
        => WarnOnce($"target:{branch}", $"GenerateRandomDesigns {branch} predicate target not found; predicate diagnostics disabled for this runtime.");

    internal static MethodBase? GenerateRandomShipMoveNextTarget()
    {
        return typeof(Ship)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => new
            {
                Type = type,
                MoveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            })
            .Where(candidate =>
                candidate.MoveNext != null &&
                candidate.Type.Name.Contains("GenerateRandomShip", StringComparison.Ordinal) &&
                candidate.MoveNext.ReturnType == typeof(bool) &&
                candidate.MoveNext.GetParameters().Length == 0)
            .Select(candidate => candidate.MoveNext)
            .FirstOrDefault();
    }

    internal static MethodBase? GenerateRandomDesignsMoveNextTarget()
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
                candidate.Type.Name.Contains("GenerateRandomDesigns", StringComparison.Ordinal) &&
                candidate.MoveNext.ReturnType == typeof(bool) &&
                candidate.MoveNext.GetParameters().Length == 0)
            .Select(candidate => candidate.MoveNext)
            .FirstOrDefault();
    }

    internal static void RecordGenerateMoveNextPrefix(object? stateMachine)
    {
        if (stateMachine == null)
            return;

        try
        {
            GenerateGateSnapshot? snapshot = ReadGenerateGateSnapshot(stateMachine);
            if (snapshot == null)
                return;

            ActiveGenerateMoves[ObjectKey(stateMachine)] = new GenerateMovePrefixInfo(snapshot);
        }
        catch (Exception ex)
        {
            WarnOnce("generateMovePrefix:" + ex.GetType().Name, $"GenerateRandomDesigns MoveNext prefix diagnostics failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordGenerateMoveNextPostfix(object? stateMachine, bool moveNextResult)
    {
        if (stateMachine == null)
            return;

        try
        {
            string key = ObjectKey(stateMachine);
            ActiveGenerateMoves.TryGetValue(key, out GenerateMovePrefixInfo? beforeInfo);
            ActiveGenerateMoves.Remove(key);

            GenerateGateSnapshot? after = ReadGenerateGateSnapshot(stateMachine);
            GenerateGateSnapshot? before = beforeInfo?.Snapshot ?? after;
            Player? player = after?.Player ?? before?.Player;
            if (!TryGetContext(player, out GenerationContext? context) || context == null)
                return;

            context.RecordMove(before, after, moveNextResult);
        }
        catch (Exception ex)
        {
            WarnOnce("generateMovePostfix:" + ex.GetType().Name, $"GenerateRandomDesigns MoveNext postfix diagnostics failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordRandomMoveNextPrefix(object? stateMachine)
    {
        if (stateMachine == null)
            return;

        try
        {
            string key = ObjectKey(stateMachine);
            if (ActiveRandomAttempts.ContainsKey(key))
                return;

            RandomStateMachineFields fields = ResolveRandomStateMachineFields(stateMachine.GetType());
            Ship? ship = fields.Ship?.GetValue(stateMachine) as Ship;
            Player? player = Safe(() => ship?.player, null);
            if (ship == null || !TryGetContext(player, out GenerationContext? context))
                return;

            DesignSnapshot before = DesignSnapshot.For(player!);
            string type = NormalizeShipType(ship.shipType);
            string hull = HullLabel(ship);
            int tryN = ReadIntField(fields.TryN, stateMachine);
            int triesTotal = ReadIntField(fields.TriesTotal, stateMachine);
            string info = StateMachineInfoText(fields, stateMachine);

            ActiveRandomAttempts[key] = new RandomAttemptInfo(
                key,
                context!,
                player!,
                ship,
                type,
                hull,
                before.DesignKeys,
                before.Designs.Count,
                tryN,
                triesTotal,
                info);

            context!.RandomAttempts++;
            Log(
                $"random-start turn={context.TurnLabel} nation={context.Nation} type={type} hull={hull} prewarming={BoolText(context.Prewarming)} try={TryText(tryN, triesTotal)} existingDesigns={before.Designs.Count} info={LogToken(info)}.");
        }
        catch (Exception ex)
        {
            WarnOnce("randomStart:" + ex.GetType().Name, $"random-start failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordRandomMoveNextPostfix(object? stateMachine, bool moveNextResult)
    {
        if (stateMachine == null || moveNextResult)
            return;

        try
        {
            string key = ObjectKey(stateMachine);
            if (!ActiveRandomAttempts.TryGetValue(key, out RandomAttemptInfo? attempt))
                return;

            ActiveRandomAttempts.Remove(key);
            DesignSnapshot after = DesignSnapshot.For(attempt.Player);
            List<Ship> newDesigns = after.Designs
                .Where(design => !attempt.BeforeDesignKeys.Contains(DesignKey(design)))
                .OrderByDescending(design => Safe(() => EffectiveDesignDate(design).turn, -1))
                .ThenBy(design => ShipLabel(design), StringComparer.Ordinal)
                .ToList();

            foreach (Ship newDesign in newDesigns.Where(design => !IsSharedDesign(design)))
                CampaignGeneratedDesignSanitizer.SanitizeAiGeneratedDesign(newDesign, attempt.Player, "random", attempt.Context.TurnLabel);

            Ship? ship = attempt.Ship;
            bool erased = Safe(() => ship == null || ship.isErased, true);
            string valid = BoolProbe(() => ship != null && ship.IsValid(false));
            string info = StateMachineInfoText(ResolveRandomStateMachineFields(stateMachine.GetType()), stateMachine);
            if (string.Equals(info, "none", StringComparison.OrdinalIgnoreCase))
                info = attempt.InitialInfo;

            bool success = newDesigns.Count > 0;
            if (success)
                attempt.Context.RandomSuccesses++;
            else
                attempt.Context.RandomFailures++;
            if (erased)
                attempt.Context.RandomErased++;

            string shipText = newDesigns.Count == 0 ? ShipLabel(ship) : string.Join("|", newDesigns.Select(DesignLabel));
            string reason =
                success ? "createdDesign" :
                erased ? "erased" :
                string.Equals(valid, "false", StringComparison.OrdinalIgnoreCase) ? "invalid" :
                !string.Equals(info, "none", StringComparison.OrdinalIgnoreCase) ? info :
                "noNewDesign";

            Log(
                $"random-end turn={attempt.Context.TurnLabel} nation={attempt.Context.Nation} type={attempt.Type} hull={attempt.Hull} success={BoolText(success)} erased={BoolText(erased)} valid={valid} reason={LogToken(reason)} designsBefore={attempt.DesignsBefore} designsAfter={after.Designs.Count} ship={LogToken(shipText)} info={LogToken(info)}.");
        }
        catch (Exception ex)
        {
            WarnOnce("randomEnd:" + ex.GetType().Name, $"random-end failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordRandomMoveNextFinalizer(object? stateMachine, Exception? exception)
    {
        if (stateMachine == null || exception == null)
            return;

        try
        {
            ActiveRandomAttempts.Remove(ObjectKey(stateMachine));
        }
        catch
        {
            // Best-effort cleanup only; the original exception must keep flowing.
        }
    }

    internal static bool TrySuppressExpectedCampaignAiShipgenFailure(string? condition)
    {
        if (!IsExpectedShipgenFailureMessage(condition) || ActiveRandomAttempts.Count == 0)
            return false;

        RandomAttemptInfo? attempt = ActiveRandomAttempts.Values.LastOrDefault();
        if (attempt == null)
            return false;

        string type = ExtractBetween(condition!, "type '", "'") ?? attempt.Type;
        string reason = ExtractBetween(condition!, "reason '", "'") ?? "unknown";
        string hull = ExtractBetween(condition!, "hull '", "'") ?? attempt.Hull;
        string key = $"{attempt.Context.Turn}:{attempt.Context.PlayerPointer}:{type}:{hull}:{reason}";
        if (LoggedExpectedShipgenFailures.Add(key))
        {
            TrimLogSets();
            Log(
                $"expected-random-failure turn={attempt.Context.TurnLabel} nation={attempt.Context.Nation} type={LogToken(type)} hull={LogToken(hull)} reason={LogToken(reason)} message={LogToken(TrimForLog(condition!, 220))}.");
        }

        return true;
    }

    private static bool IsExpectedShipgenFailureMessage(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return false;

        return condition.StartsWith("failed to generate random ship of type '", StringComparison.Ordinal) ||
               condition.StartsWith("failed to generate ship of type '", StringComparison.Ordinal);
    }

    private static string? ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
            return null;

        startIndex += start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex <= startIndex)
            return null;

        return text[startIndex..endIndex];
    }

    private static string TrimForLog(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..Math.Max(0, maxLength)] + "...";

    private static GenerateGateSnapshot? ReadGenerateGateSnapshot(object stateMachine)
    {
        GenerateStateMachineFields fields = ResolveGenerateStateMachineFields(stateMachine.GetType());
        Player? player = fields.Player?.GetValue(stateMachine) as Player;
        if (!ShouldTrace(player))
            return null;

        return GenerateGateSnapshot.For(
            player!,
            ReadBoolField(fields.Prewarming, stateMachine),
            ReadIntField(fields.State, stateMachine),
            ShipTypeText(fields.SelectedType?.GetValue(stateMachine) as ShipType),
            ReadIntField(fields.Tries, stateMachine),
            ReadIntField(fields.I, stateMachine),
            ReadIntField(fields.J, stateMachine),
            fields.Ship?.GetValue(stateMachine) as Ship);
    }

    private static GenerateStateMachineFields ResolveGenerateStateMachineFields(Type type)
    {
        if (GenerateStateMachineFieldsByType.TryGetValue(type, out GenerateStateMachineFields? cached))
            return cached;

        FieldInfo? player = FindField(type, field => field.FieldType == typeof(Player) && field.Name.Equals("player", StringComparison.Ordinal));
        player ??= FindField(type, field => field.FieldType == typeof(Player));
        FieldInfo? prewarming = FindField(type, field => field.FieldType == typeof(bool) && field.Name.Equals("prewarming", StringComparison.Ordinal));
        FieldInfo? state = FindField(type, field => field.FieldType == typeof(int) && field.Name.Contains("__state", StringComparison.Ordinal));
        FieldInfo? selectedType = FindField(type, field => field.FieldType == typeof(ShipType) && field.Name.Contains("shipType", StringComparison.Ordinal));
        FieldInfo? tries = FindField(type, field => field.FieldType == typeof(int) && field.Name.Contains("tries", StringComparison.Ordinal));
        FieldInfo? ship = FindField(type, field => field.FieldType == typeof(Ship) && field.Name.Contains("ship", StringComparison.Ordinal));
        FieldInfo? i = FindField(type, field => field.FieldType == typeof(int) && field.Name.Contains("<i>", StringComparison.Ordinal));
        FieldInfo? j = FindField(type, field => field.FieldType == typeof(int) && field.Name.Contains("<j>", StringComparison.Ordinal));
        FieldInfo? displayClass = FindField(type, field => field.Name.Contains("__1", StringComparison.Ordinal) || field.FieldType.Name.Contains("DisplayClass202", StringComparison.Ordinal));

        GenerateStateMachineFields fields = new(player, prewarming, state, selectedType, tries, ship, i, j, displayClass);
        GenerateStateMachineFieldsByType[type] = fields;
        return fields;
    }

    private static bool ReadBoolField(FieldInfo? field, object stateMachine)
    {
        try
        {
            object? value = field?.GetValue(stateMachine);
            return value is bool boolValue && boolValue;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetContext(Player? player, out GenerationContext? context)
    {
        context = null;
        if (!ShouldTrace(player))
            return false;

        return ActiveByPlayer.TryGetValue(PlayerPointer(player!), out context);
    }

    private static bool TryGetContext(object? displayClass, out GenerationContext? context)
    {
        context = null;
        Player? player = TryGetDisplayClassPlayer(displayClass);
        if (player != null)
            return TryGetContext(player, out context);

        if (ActiveByPlayer.Count == 1)
        {
            context = ActiveByPlayer.Values.First();
            return true;
        }

        return false;
    }

    internal static Player? TryGetDisplayClassPlayer(object? displayClass)
    {
        if (displayClass == null)
            return null;

        try
        {
            MemberInfo? member = ResolveDisplayClassPlayerMember(displayClass.GetType());
            return member switch
            {
                PropertyInfo property => property.GetValue(displayClass) as Player,
                FieldInfo field => field.GetValue(displayClass) as Player,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static MemberInfo? ResolveDisplayClassPlayerMember(Type displayClassType)
    {
        if (DisplayClassPlayerMemberByType.TryGetValue(displayClassType, out MemberInfo? cached))
            return cached;

        MemberInfo? member = displayClassType
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(property => property.PropertyType == typeof(Player) && property.GetMethod != null);

        member ??= displayClassType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(field => field.FieldType == typeof(Player));

        DisplayClassPlayerMemberByType[displayClassType] = member;
        return member;
    }

    private static string OutcomeFor(GenerationContext context, IReadOnlyCollection<Ship> newDesigns)
    {
        if (context.Prewarming)
            return newDesigns.Count > 0 ? "prewarm-created" : "prewarm";
        if (newDesigns.Any(SmartAiDesignService.IsAcceptedSmartAiDesign))
            return "created";
        if (newDesigns.Count > 0)
            return context.SharedAccepted > 0 || newDesigns.Any(IsSharedDesign) ? "shared" : "created";
        if (context.TotalPredicateCalls == 0)
            return "noChangeNoPredicate";
        if (context.TotalFinalAccepted == 0)
            return "noCandidate";
        return "candidateNoDesign";
    }

    private static bool ShouldLogCompactResult(
        GenerationContext context,
        IReadOnlyCollection<Ship> newDesigns,
        string source,
        string outcome,
        string gateReason)
    {
        if (newDesigns.Count > 0 ||
            context.RandomFailures > 0 ||
            context.RandomErased > 0)
        {
            return true;
        }

        if (context.RandomAttempts > 0 && context.RandomSuccesses == 0)
            return true;

        if (string.Equals(source, "mixed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outcome, "candidateNoDesign", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(gateReason, "selected-type-but-no-start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(gateReason, "random-started-no-design", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GateReasonFor(GenerationContext context, IReadOnlyCollection<Ship> newDesigns, GenerateGateSnapshot gate)
    {
        if (newDesigns.Count > 0)
            return "created";

        bool selectedType = !string.Equals(context.SelectedTypeText(gate), "none", StringComparison.OrdinalIgnoreCase);
        if (context.SharedAttempted > 0 && context.SharedAccepted == 0 && context.RandomAttempts == 0)
            return "shared-failed-before-random";
        if (context.RandomAttempts > 0 && context.RandomSuccesses == 0)
            return "random-started-no-design";
        if (selectedType && context.SharedAttempted == 0 && context.RandomAttempts == 0)
            return "selected-type-but-no-start";
        if (context.Missing.FinalAccepted > 0 &&
            context.Replacement.FinalCalls == 0 &&
            context.SharedAttempted == 0 &&
            context.RandomAttempts == 0 &&
            gate.DesignCount > 6)
        {
            return "replacement-count-blocked";
        }
        if (context.TotalPredicateCalls == 0 && !selectedType)
            return "cadence-skip";
        if (context.Stale.FinalCalls > 0 && context.Stale.FinalAccepted == 0)
            return "fresh-designs";
        if (context.TotalPredicateCalls > 0 && context.TotalFinalAccepted == 0)
            return "predicate-empty";
        if (gate.MissingAfterExisting.Count == 0 && gate.BuildableTypes.Count > 0)
            return "no-missing-types";

        return "unknown-gate";
    }

    private static string SourceFor(GenerationContext context, IReadOnlyCollection<Ship> newDesigns)
    {
        if (newDesigns.Count == 0)
            return "none";

        bool anySmartAi = newDesigns.Any(SmartAiDesignService.IsAcceptedSmartAiDesign);
        bool anySharedDesign = newDesigns.Any(design => IsSharedDesign(design) && !SmartAiDesignService.IsAcceptedSmartAiDesign(design));
        bool anyShared = anySharedDesign || (context.SharedAccepted > 0 && !anySmartAi);
        bool anyRandom = newDesigns.Any(design =>
            !IsSharedDesign(design) &&
            !SmartAiDesignService.IsAcceptedSmartAiDesign(design)) &&
            context.SharedAccepted == 0;
        int sourceKinds = (anySmartAi ? 1 : 0) + (anyShared ? 1 : 0) + (anyRandom ? 1 : 0);
        if (sourceKinds > 1)
            return "mixed";
        if (anySmartAi)
            return "smart-ai";
        if (anyShared)
            return "shared";
        return "random";
    }

    private static string RandomAttemptText(GenerationContext context, IReadOnlyCollection<Ship> newDesigns)
    {
        if (context.RandomAttempts > 0)
            return "true";
        if (newDesigns.Count > 0 && context.SharedAccepted == 0)
            return "true";
        if (newDesigns.Count == 0 && context.TotalFinalAccepted > 0)
            return "maybe";
        return "false";
    }

    private static RandomStateMachineFields ResolveRandomStateMachineFields(Type type)
    {
        if (RandomStateMachineFieldsByType.TryGetValue(type, out RandomStateMachineFields? cached))
            return cached;

        FieldInfo? ship = FindField(type, field => field.FieldType == typeof(Ship) && field.Name.Contains("this", StringComparison.Ordinal));
        FieldInfo? tryN = FindField(type, field => field.Name.Contains("tryN", StringComparison.Ordinal));
        FieldInfo? triesTotal = FindField(type, field => field.Name.Contains("triesTotal", StringComparison.Ordinal));
        FieldInfo? info = FindField(type, field => field.FieldType == typeof(StringBuilder) || field.Name.Equals("info", StringComparison.Ordinal));
        RandomStateMachineFields fields = new(ship, tryN, triesTotal, info);
        RandomStateMachineFieldsByType[type] = fields;
        return fields;
    }

    private static FieldInfo? FindField(Type type, Func<FieldInfo, bool> predicate)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(predicate);

    private static string StateMachineInfoText(RandomStateMachineFields fields, object stateMachine)
    {
        StringBuilder? info = Safe(() => fields.Info?.GetValue(stateMachine) as StringBuilder, null);
        string text = info?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? "none" : text.Trim();
    }

    private static int ReadIntField(FieldInfo? field, object stateMachine)
    {
        try
        {
            object? value = field?.GetValue(stateMachine);
            return value is int intValue ? intValue : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string HullLabel(Ship? ship)
    {
        string hull = SafeString(() => ship?.hull?.data?.name);
        if (!string.Equals(hull, "<empty>", StringComparison.Ordinal))
            return LogToken(hull);

        hull = SafeString(() => ship?.hull?.data?.nameUi);
        return LogToken(string.Equals(hull, "<empty>", StringComparison.Ordinal) ? "none" : hull);
    }

    private static string TryText(int tryN, int triesTotal)
        => tryN < 0 && triesTotal < 0
            ? "?"
            : $"{(tryN < 0 ? "?" : tryN.ToString(CultureInfo.InvariantCulture))}/{(triesTotal < 0 ? "?" : triesTotal.ToString(CultureInfo.InvariantCulture))}";

    private static string ShipTypeText(ShipType? shipType)
    {
        if (shipType == null)
            return "none";

        string type = NormalizeShipType(shipType);
        return string.IsNullOrWhiteSpace(type) || string.Equals(type, "UNK", StringComparison.OrdinalIgnoreCase)
            ? "none"
            : type;
    }

    private static string ObjectKey(object value)
        => $"{value.GetType().FullName}:{RuntimeHelpers.GetHashCode(value)}";

    private static string BoolProbe(Func<bool> read)
    {
        try
        {
            return BoolText(read());
        }
        catch (Exception ex)
        {
            return "unavailable:" + ex.GetType().Name;
        }
    }

    private static bool IsSharedDesign(Ship design)
        => Safe(() => ReadBoolMember(design, "isSharedDesign") ||
                      ReadBoolMember(design, "IsSharedDesign") ||
                      ReadBoolMember(design, "isShared"), false);

    private static bool ReadBoolMember(object target, string memberName)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(target) is bool propertyValue)
            return propertyValue;

        FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(target) is bool fieldValue && fieldValue;
    }

    private static bool ShouldTrace(Player? player)
        => player != null && Safe(() => player.isAi && !player.isMain, false);

    private static ShipType? FindShipType(string type)
    {
        try
        {
            var shipTypes = G.GameData?.shipTypes;
            if (shipTypes == null)
                return null;

            foreach (var pair in shipTypes)
            {
                ShipType? shipType = pair.Value;
                if (shipType != null && string.Equals(NormalizeShipType(shipType), type, StringComparison.OrdinalIgnoreCase))
                    return shipType;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool ShipTypeAvailable(Player player, ShipType? shipType)
        => shipType != null &&
           Safe(() => shipType.canBuild, false) &&
           Safe(() => Ship.GetHull(player, shipType, true) != null, false);

    private static bool CanBuildDesign(Ship design)
    {
        return AiDesignBuildability.CanBuildDesign(
            Safe(() => design.player, null),
            design,
            1,
            "DesignGenSnapshot",
            out _);
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

    private static bool IsActiveDesign(Ship? design)
    {
        if (design == null || Safe(() => design.isErased, true))
            return false;

        if (CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            return false;

        return Safe(() => design.isDesign || design.isRefitDesign, false);
    }

    private static string DesignKey(Ship? design)
    {
        if (design == null)
            return "<null>";

        long pointer = Safe(() => design.Pointer.ToInt64(), 0L);
        string id = Safe(() => design.id.ToString(), string.Empty);
        return !string.IsNullOrWhiteSpace(id) ? id : pointer.ToString(CultureInfo.InvariantCulture);
    }

    private static string DesignLabel(Ship? ship)
    {
        if (ship == null)
            return "none";

        string type = NormalizeShipType(ship.shipType);
        string name = ShipLabel(ship);
        int year = GameDateYear(EffectiveDesignDate(ship));
        float tons = Safe(() => ship.Tonnage(), 0f);
        return $"{type}:{name}:{year}:{Fmt(tons)}t";
    }

    private static string FreshCoverLabel(Ship? ship)
    {
        if (ship == null)
            return "none";

        GameDate created = Safe(() => ship.dateCreated, default);
        GameDate finished = Safe(() => ship.dateFinished, default);
        GameDate refit = Safe(() => ship.dateCreatedRefit, default);
        return string.Join(
            ":",
            $"freshCover={NormalizeShipType(ship.shipType)}",
            LogToken(ShipLabel(ship)),
            $"effectiveYear={GameDateYear(EffectiveDesignDate(ship))}",
            $"dateCreated={Safe(() => created.turn, -1)}/{GameDateYear(created)}",
            $"dateFinished={Safe(() => finished.turn, -1)}/{GameDateYear(finished)}",
            $"dateCreatedRefit={Safe(() => refit.turn, -1)}/{GameDateYear(refit)}",
            $"age={DesignAgeText(ship)}",
            $"shared={BoolText(IsSharedDesign(ship))}",
            $"refit={BoolText(Safe(() => ship.isRefitDesign, false))}");
    }

    private static string DesignAgeText(Ship? ship)
    {
        if (ship == null)
            return "?";

        float age = Safe(() => CampaignController.Instance!.CurrentDate.YearsPassedSince(EffectiveDesignDate(ship)), float.NaN);
        return float.IsNaN(age) || float.IsInfinity(age) || age < 0f
            ? "?"
            : age.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static bool TrySelectRefreshCandidate(Player player, out string refreshType, out Ship? refreshDesign, out float ageYears, out int designCount)
    {
        refreshType = "none";
        refreshDesign = null;
        ageYears = float.NaN;
        designCount = 0;

        List<Ship> designs = SafeShipList(player.designs)
            .Where(IsActiveDesign)
            .Where(design => SurfaceTypes.Contains(NormalizeShipType(design.shipType), StringComparer.OrdinalIgnoreCase))
            .ToList();
        designCount = designs.Count;
        if (designs.Count == 0)
            return false;

        HashSet<string> represented = designs
            .Select(design => NormalizeShipType(design.shipType))
            .Where(type => SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> buildable = SurfaceTypes
            .Where(type => ShipTypeAvailable(player, FindShipType(type)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (buildable.Count == 0 || buildable.Any(type => !represented.Contains(type)))
            return false;

        foreach (string type in SurfaceTypes)
        {
            if (!buildable.Contains(type))
                continue;

            Ship? newestCreated = designs
                .Where(design => string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(design => Safe(() => design.dateCreated.turn, -1))
                .ThenByDescending(design => Safe(() => design.Tonnage(), 0f))
                .FirstOrDefault();
            if (newestCreated == null)
                continue;

            float candidateAge = DesignCreatedAgeYears(newestCreated);
            if (float.IsNaN(candidateAge) || candidateAge < RefreshFreshnessThresholdYears)
                continue;

            refreshType = type;
            refreshDesign = newestCreated;
            ageYears = candidateAge;
            return true;
        }

        return false;
    }

    private static float DesignCreatedAgeYears(Ship? ship)
    {
        if (ship == null)
            return float.NaN;

        return Safe(() => CampaignController.Instance!.CurrentDate.YearsPassedSince(ship.dateCreated), float.NaN);
    }

    private static void TrimLogSets()
    {
        if (LoggedFreshnessOverrides.Count > 2048)
            LoggedFreshnessOverrides.Clear();
        if (LoggedCountGateRefreshes.Count > 2048)
            LoggedCountGateRefreshes.Clear();
        if (LoggedReplacementCountBypasses.Count > 2048)
            LoggedReplacementCountBypasses.Clear();
        if (LoggedExpectedShipgenFailures.Count > 2048)
            LoggedExpectedShipgenFailures.Clear();
    }

    private static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "<null>";

        string name = SafeString(() => ship.Name(false, false, false, false, true));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => ship.vesselName);
    }

    private static GameDate EffectiveDesignDate(Ship design)
        => Safe(() => design.isRefitDesign ? design.dateCreatedRefit : design.dateCreated, design.dateCreated);

    private static int GameDateYear(GameDate date)
    {
        string text = Safe(() => date.ToString(true), string.Empty);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) ? year : 0;
    }

    private static string NormalizeShipType(ShipType? type)
        => AiDesignCompetitiveness.NormalizeShipType(type);

    private static string NormalizeShipType(string? value)
        => AiDesignCompetitiveness.NormalizeShipType(value);

    private static string PlayerLabel(Player? player)
        => AiDesignCompetitiveness.PlayerLabel(player);

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static int CurrentTurnIndex()
        => AiDesignCompetitiveness.CurrentTurnIndex();

    private static string CurrentTurnLabel()
        => AiDesignCompetitiveness.CurrentTurnLabel();

    private static string SharedUsageLabel(CampaignController? controller)
        => SafeString(() => controller?.SharedDesignsUsage.ToString());

    private static string DesignUsageLabel(CampaignController? controller)
        => SafeString(() => controller?.designsUsage.ToString());

    private static string AdvancedAiBuilderLabel()
        => ModSettings.AdvancedAiBuilderModeText(ModSettings.AdvancedAiBuilderEnabled);

    private static string BoolText(bool value)
        => value.ToString().ToLowerInvariant();

    private static string Fmt(float value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

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

    private static void Log(string message)
        => Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} {message}");

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} {message}");
    }

    private sealed class GenerationContext
    {
        internal GenerationContext(
            long playerPointer,
            Player player,
            string nation,
            int turn,
            string turnLabel,
            bool prewarming,
            string sharedUsage,
            string designUsage,
            DesignSnapshot before)
        {
            PlayerPointer = playerPointer;
            Player = player;
            Nation = nation;
            Turn = turn;
            TurnLabel = turnLabel;
            Prewarming = prewarming;
            SharedUsage = sharedUsage;
            DesignUsage = designUsage;
            Before = before;
        }

        internal long PlayerPointer { get; }
        internal Player Player { get; }
        internal string Nation { get; }
        internal int Turn { get; }
        internal string TurnLabel { get; }
        internal bool Prewarming { get; }
        internal string SharedUsage { get; }
        internal string DesignUsage { get; }
        internal DesignSnapshot Before { get; }
        internal BranchStats Missing { get; } = new();
        internal BranchStats Replacement { get; } = new();
        internal BranchStats Stale { get; } = new();
        internal HashSet<string> AcceptedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> SharedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int SharedAttempted { get; set; }
        internal int SharedAccepted { get; set; }
        internal int RandomAttempts { get; set; }
        internal int RandomSuccesses { get; set; }
        internal int RandomFailures { get; set; }
        internal int RandomErased { get; set; }
        internal bool ReplacementCountBypassApplied { get; set; }
        internal string ReplacementCountBypassType { get; set; } = "none";
        internal int GenerateMoveNextCalls { get; private set; }
        internal bool LastGenerateMoveNextResult { get; private set; }
        internal GenerateGateSnapshot? LastGateBefore { get; private set; }
        internal GenerateGateSnapshot? LastGateAfter { get; private set; }
        internal string LastSelectedType { get; private set; } = "none";
        internal int TotalPredicateCalls => Missing.VanillaCalls + Replacement.VanillaCalls + Stale.VanillaCalls;
        internal int TotalFinalAccepted => Missing.FinalAccepted + Replacement.FinalAccepted + Stale.FinalAccepted;

        internal BranchStats Branch(string branch)
            => branch switch
            {
                "replacement" => Replacement,
                "stale" => Stale,
                _ => Missing
            };

        internal string AcceptedCandidateText()
        {
            string[] values = AcceptedTypes
                .Where(type => !string.IsNullOrWhiteSpace(type) && !string.Equals(type, "UNK", StringComparison.OrdinalIgnoreCase))
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return values.Length == 0 ? "none" : string.Join(",", values);
        }

        internal string FreshCoverCandidateText()
            => Stale.DetailText("freshCover");

        internal string FreshCoveredTypeText()
            => Stale.AcceptedTypeText();

        internal string ReplacementAfterFreshText(GenerateGateSnapshot gate)
        {
            HashSet<string> candidates = Replacement.AcceptedTypes();
            if (candidates.Count == 0 && Replacement.FinalCalls == 0)
                candidates = gate.BuildableTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            candidates.ExceptWith(Stale.AcceptedTypes());
            string[] values = candidates
                .Where(type => !string.IsNullOrWhiteSpace(type) && !string.Equals(type, "UNK", StringComparison.OrdinalIgnoreCase))
                .OrderBy(type => Array.IndexOf(SurfaceTypes, type))
                .ThenBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return values.Length == 0 ? "none" : string.Join(",", values);
        }

        internal void RecordMove(GenerateGateSnapshot? before, GenerateGateSnapshot? after, bool moveNextResult)
        {
            GenerateMoveNextCalls++;
            LastGateBefore = before;
            LastGateAfter = after;
            LastGenerateMoveNextResult = moveNextResult;
            RememberSelectedType(before?.SelectedType);
            RememberSelectedType(after?.SelectedType);
        }

        internal GenerateGateSnapshot GateSnapshotOrDefault(Player player)
            => LastGateAfter ?? LastGateBefore ?? GenerateGateSnapshot.For(player, Prewarming, -1, "none", -1, -1, -1, null);

        internal string SelectedTypeText(GenerateGateSnapshot gate)
            => !string.Equals(LastSelectedType, "none", StringComparison.OrdinalIgnoreCase)
                ? LastSelectedType
                : gate.SelectedType;

        internal string StateText(GenerateGateSnapshot gate)
            => LastGateBefore == null && LastGateAfter == null
                ? "none"
                : $"{IntText(LastGateBefore?.State)}->{IntText((LastGateAfter ?? gate).State)}:{BoolText(LastGenerateMoveNextResult)}";

        internal string TriesText(GenerateGateSnapshot gate)
            => LastGateBefore == null && LastGateAfter == null
                ? IntText(gate.Tries)
                : $"{IntText(LastGateBefore?.Tries)}->{IntText((LastGateAfter ?? gate).Tries)}";

        internal string IText(GenerateGateSnapshot gate)
            => LastGateBefore == null && LastGateAfter == null
                ? IntText(gate.I)
                : $"{IntText(LastGateBefore?.I)}->{IntText((LastGateAfter ?? gate).I)}";

        internal string JText(GenerateGateSnapshot gate)
            => LastGateBefore == null && LastGateAfter == null
                ? IntText(gate.J)
                : $"{IntText(LastGateBefore?.J)}->{IntText((LastGateAfter ?? gate).J)}";

        private void RememberSelectedType(string? type)
        {
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "none", StringComparison.OrdinalIgnoreCase))
                LastSelectedType = type;
        }

        private static string IntText(int? value)
            => value.HasValue && value.Value >= 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : "?";
    }

    private sealed class GenerateGateSnapshot
    {
        private GenerateGateSnapshot(
            Player player,
            bool prewarming,
            int state,
            string selectedType,
            int tries,
            int i,
            int j,
            string generatedShip,
            int designCount,
            HashSet<string> buildableTypes,
            HashSet<string> representedTypes)
        {
            Player = player;
            Prewarming = prewarming;
            State = state;
            SelectedType = selectedType;
            Tries = tries;
            I = i;
            J = j;
            GeneratedShip = generatedShip;
            DesignCount = designCount;
            BuildableTypes = buildableTypes;
            RepresentedTypes = representedTypes;
            MissingAfterExisting = buildableTypes
                .Where(type => !representedTypes.Contains(type))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal Player Player { get; }
        internal bool Prewarming { get; }
        internal int State { get; }
        internal string SelectedType { get; }
        internal int Tries { get; }
        internal int I { get; }
        internal int J { get; }
        internal string GeneratedShip { get; }
        internal int DesignCount { get; }
        internal HashSet<string> BuildableTypes { get; }
        internal HashSet<string> RepresentedTypes { get; }
        internal HashSet<string> MissingAfterExisting { get; }

        internal static GenerateGateSnapshot For(
            Player player,
            bool prewarming,
            int state,
            string selectedType,
            int tries,
            int i,
            int j,
            Ship? generatedShip)
        {
            List<Ship> designs = SafeShipList(player.designs)
                .Where(IsActiveDesign)
                .Where(design => SurfaceTypes.Contains(NormalizeShipType(design.shipType), StringComparer.OrdinalIgnoreCase))
                .ToList();
            HashSet<string> represented = designs
                .Select(design => NormalizeShipType(design.shipType))
                .Where(type => SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> buildable = SurfaceTypes
                .Where(type => ShipTypeAvailable(player, FindShipType(type)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new GenerateGateSnapshot(
                player,
                prewarming,
                state,
                selectedType,
                tries,
                i,
                j,
                DesignLabel(generatedShip),
                designs.Count,
                buildable,
                represented);
        }

        internal string BuildableTypesText()
            => TypeSetText(BuildableTypes);

        internal string RepresentedTypesText()
            => TypeSetText(RepresentedTypes);

        internal string MissingAfterExistingText()
            => TypeSetText(MissingAfterExisting);

        private static string TypeSetText(IEnumerable<string> types)
        {
            string[] values = types
                .Where(type => !string.IsNullOrWhiteSpace(type) && !string.Equals(type, "UNK", StringComparison.OrdinalIgnoreCase))
                .OrderBy(type => Array.IndexOf(SurfaceTypes, type))
                .ThenBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return values.Length == 0 ? "none" : string.Join(",", values);
        }
    }

    private sealed class DesignSnapshot
    {
        private DesignSnapshot(
            List<Ship> designs,
            Dictionary<string, int> counts,
            Dictionary<string, string> newestByType,
            Dictionary<string, int> buildableCounts,
            Dictionary<string, bool> hullAvailability)
        {
            Designs = designs;
            Counts = counts;
            NewestByType = newestByType;
            BuildableCounts = buildableCounts;
            HullAvailability = hullAvailability;
            DesignKeys = designs.Select(DesignKey).ToHashSet(StringComparer.Ordinal);
        }

        internal List<Ship> Designs { get; }
        internal HashSet<string> DesignKeys { get; }
        private Dictionary<string, int> Counts { get; }
        private Dictionary<string, string> NewestByType { get; }
        private Dictionary<string, int> BuildableCounts { get; }
        private Dictionary<string, bool> HullAvailability { get; }

        internal static DesignSnapshot For(Player player)
        {
            List<Ship> designs = SafeShipList(player.designs)
                .Where(IsActiveDesign)
                .Where(design => SurfaceTypes.Contains(NormalizeShipType(design.shipType), StringComparer.OrdinalIgnoreCase))
                .ToList();

            Dictionary<string, int> counts = SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> newestByType = SurfaceTypes.ToDictionary(type => type, _ => "none", StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> buildableCounts = SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> hullAvailability = SurfaceTypes.ToDictionary(type => type, type => ShipTypeAvailable(player, FindShipType(type)), StringComparer.OrdinalIgnoreCase);

            foreach (string type in SurfaceTypes)
            {
                List<Ship> typed = designs
                    .Where(design => string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                counts[type] = typed.Count;
                buildableCounts[type] = typed.Count(CanBuildDesign);
                Ship? newest = typed
                    .OrderByDescending(design => Safe(() => EffectiveDesignDate(design).turn, -1))
                    .ThenByDescending(design => Safe(() => design.Tonnage(), 0f))
                    .FirstOrDefault();
                newestByType[type] = DesignLabel(newest);
            }

            return new DesignSnapshot(designs, counts, newestByType, buildableCounts, hullAvailability);
        }

        internal string CountText()
            => string.Join(",", SurfaceTypes.Select(type => $"{type}:{Counts.GetValueOrDefault(type)}"));

        internal string NewestText()
            => string.Join(",", SurfaceTypes.Select(type => $"{type}:{NewestByType.GetValueOrDefault(type, "none")}"));

        internal string BuildableText()
            => string.Join(",", SurfaceTypes.Select(type => $"{type}:{BuildableCounts.GetValueOrDefault(type)}"));

        internal string HullAvailabilityText()
            => string.Join(",", SurfaceTypes.Select(type => $"{type}:{(HullAvailability.GetValueOrDefault(type) ? "yes" : "no")}"));
    }

    private sealed class BranchStats
    {
        private readonly Dictionary<string, CandidateStats> byLabel = new(StringComparer.Ordinal);
        private readonly HashSet<string> acceptedTypes = new(StringComparer.OrdinalIgnoreCase);

        internal int VanillaCalls { get; private set; }
        internal int VanillaAccepted { get; private set; }
        internal int FinalCalls { get; private set; }
        internal int FinalAccepted { get; private set; }

        internal void RecordVanilla(string type, string label, bool result)
        {
            VanillaCalls++;
            if (result)
                VanillaAccepted++;

            CandidateStats stats = StatsFor(label, type);
            stats.VanillaCalls++;
            if (result)
                stats.VanillaAccepted++;
        }

        internal void RecordFinal(string type, string label, bool result)
        {
            FinalCalls++;
            if (result)
            {
                FinalAccepted++;
                acceptedTypes.Add(type);
            }

            CandidateStats stats = StatsFor(label, type);
            stats.FinalCalls++;
            if (result)
                stats.FinalAccepted++;
        }

        internal string Summary()
        {
            if (VanillaCalls == 0 && FinalCalls == 0)
                return "none";

            string accepted = acceptedTypes.Count == 0
                ? "none"
                : string.Join("/", acceptedTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase));
            string changed = ChangedText();
            return $"calls={VanillaCalls},vanillaAccepted={VanillaAccepted},finalAccepted={FinalAccepted},accepted={accepted}{changed}";
        }

        internal HashSet<string> AcceptedTypes()
            => acceptedTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        internal string AcceptedTypeText()
        {
            if (acceptedTypes.Count == 0)
                return "none";

            string[] values = acceptedTypes
                .OrderBy(type => Array.IndexOf(SurfaceTypes, type))
                .ThenBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return string.Join(",", values);
        }

        internal string DetailText(string prefix)
        {
            if (FinalCalls == 0 && VanillaCalls == 0)
                return "none";

            string[] values = byLabel.Values
                .OrderBy(stats => stats.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(stats => stats.Label, StringComparer.Ordinal)
                .Select(stats =>
                {
                    bool result = stats.FinalCalls > 0 ? stats.FinalAccepted > 0 : stats.VanillaAccepted > 0;
                    string label = stats.Label.StartsWith(prefix + "=", StringComparison.Ordinal)
                        ? stats.Label
                        : $"{prefix}={stats.Label}";
                    return stats.FinalCalls > 0 && !label.Contains(":result=", StringComparison.Ordinal)
                        ? $"{label}:result={BoolText(result)}"
                        : label;
                })
                .Take(8)
                .ToArray();

            int hidden = Math.Max(0, byLabel.Count - values.Length);
            string text = values.Length == 0 ? "none" : string.Join("|", values);
            return hidden > 0 ? $"{text}|+{hidden}more" : text;
        }

        private string ChangedText()
        {
            string[] changed = byLabel.Values
                .Where(stats => stats.VanillaAccepted != stats.FinalAccepted)
                .Select(stats => $"{stats.Label}:{stats.VanillaAccepted}->{stats.FinalAccepted}")
                .Take(6)
                .ToArray();
            return changed.Length == 0 ? string.Empty : $",changed={string.Join("/", changed)}";
        }

        private CandidateStats StatsFor(string label, string type)
        {
            string key = string.IsNullOrWhiteSpace(label) ? type : label;
            if (!byLabel.TryGetValue(key, out CandidateStats? stats))
            {
                stats = new CandidateStats(type, key);
                byLabel[key] = stats;
            }

            return stats;
        }
    }

    private sealed class CandidateStats
    {
        internal CandidateStats(string type, string label)
        {
            Type = type;
            Label = label;
        }

        internal string Type { get; }
        internal string Label { get; }
        internal int VanillaCalls { get; set; }
        internal int VanillaAccepted { get; set; }
        internal int FinalCalls { get; set; }
        internal int FinalAccepted { get; set; }
    }

    private sealed class RandomAttemptInfo
    {
        internal RandomAttemptInfo(
            string key,
            GenerationContext context,
            Player player,
            Ship ship,
            string type,
            string hull,
            HashSet<string> beforeDesignKeys,
            int designsBefore,
            int tryN,
            int triesTotal,
            string initialInfo)
        {
            Key = key;
            Context = context;
            Player = player;
            Ship = ship;
            Type = type;
            Hull = hull;
            BeforeDesignKeys = beforeDesignKeys;
            DesignsBefore = designsBefore;
            TryN = tryN;
            TriesTotal = triesTotal;
            InitialInfo = initialInfo;
        }

        internal string Key { get; }
        internal GenerationContext Context { get; }
        internal Player Player { get; }
        internal Ship Ship { get; }
        internal string Type { get; }
        internal string Hull { get; }
        internal HashSet<string> BeforeDesignKeys { get; }
        internal int DesignsBefore { get; }
        internal int TryN { get; }
        internal int TriesTotal { get; }
        internal string InitialInfo { get; }
    }

    private sealed class RandomStateMachineFields
    {
        internal RandomStateMachineFields(FieldInfo? ship, FieldInfo? tryN, FieldInfo? triesTotal, FieldInfo? info)
        {
            Ship = ship;
            TryN = tryN;
            TriesTotal = triesTotal;
            Info = info;
        }

        internal FieldInfo? Ship { get; }
        internal FieldInfo? TryN { get; }
        internal FieldInfo? TriesTotal { get; }
        internal FieldInfo? Info { get; }
    }

    private sealed class GenerateMovePrefixInfo
    {
        internal GenerateMovePrefixInfo(GenerateGateSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        internal GenerateGateSnapshot Snapshot { get; }
    }

    private sealed class GenerateStateMachineFields
    {
        internal GenerateStateMachineFields(
            FieldInfo? player,
            FieldInfo? prewarming,
            FieldInfo? state,
            FieldInfo? selectedType,
            FieldInfo? tries,
            FieldInfo? ship,
            FieldInfo? i,
            FieldInfo? j,
            FieldInfo? displayClass)
        {
            Player = player;
            Prewarming = prewarming;
            State = state;
            SelectedType = selectedType;
            Tries = tries;
            Ship = ship;
            I = i;
            J = j;
            DisplayClass = displayClass;
        }

        internal FieldInfo? Player { get; }
        internal FieldInfo? Prewarming { get; }
        internal FieldInfo? State { get; }
        internal FieldInfo? SelectedType { get; }
        internal FieldInfo? Tries { get; }
        internal FieldInfo? Ship { get; }
        internal FieldInfo? I { get; }
        internal FieldInfo? J { get; }
        internal FieldInfo? DisplayClass { get; }
    }
}

internal static class CampaignAiDesignGenerationPreflight
{
    private const string LogPrefix = "[AI DesignGen]";
    private static readonly bool Enabled = false;
    private static readonly Dictionary<string, PreflightResult> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSkips = new(StringComparer.Ordinal);

    internal static void ApplyShipTypePredicatePreflight(object displayClass, ShipType? shipType, ref bool result, string branch)
    {
        if (!result || shipType == null)
            return;

        Player? player = CampaignAiDesignGenerationDiagnostics.TryGetDisplayClassPlayer(displayClass);
        if (!ShouldApply(player, prewarming: false))
            return;

        PreflightResult preflight = For(player!, shipType);
        if (preflight.Pass)
            return;

        result = false;
        LogSkip(player!, shipType, preflight, branch);
    }

    internal static bool ShouldSkipSharedDesign(Player? player, ShipType? shipType, bool prewarming)
    {
        if (!ShouldApply(player, prewarming) || shipType == null)
            return false;

        PreflightResult preflight = For(player!, shipType);
        if (preflight.Pass)
            return false;

        LogSkip(player!, shipType, preflight, "shared");
        return true;
    }

    private static bool ShouldApply(Player? player, bool prewarming)
    {
        if (!Enabled)
            return false;

        if (prewarming || player == null)
            return false;

        if (AiDesignCompetitiveness.CurrentTurnIndex() <= 0)
            return false;

        return Safe(() => player.isAi && !player.isMain, false);
    }

    private static PreflightResult For(Player player, ShipType shipType)
    {
        string key = $"{PlayerPointer(player)}:{AiDesignCompetitiveness.CurrentTurnIndex()}:{NormalizeShipType(shipType)}";
        if (Cache.TryGetValue(key, out PreflightResult? cached))
            return cached;

        PreflightResult result = Build(player, shipType);
        Cache[key] = result;
        if (Cache.Count > 512)
            Cache.Clear();
        return result;
    }

    private static PreflightResult Build(Player player, ShipType shipType)
    {
        float cash = Safe(() => player.cash, 0f);
        float revenue = Safe(() => player.Revenue(), 0f);
        float aiShipbuilding = Math.Max(0f, Safe(() => player.GetAiPersonality()?.aiShipbuilding ?? 0f, 0f));
        float cashNeed = revenue * aiShipbuilding;
        bool cashGate = cash >= cashNeed;

        int crew = Safe(() => player.crewPool, 0);
        float minCrew = Safe(() => CampaignController.Param("build_ships_min_crew_pool_amount", 0f), 0f);
        bool crewGate = crew >= minCrew;

        float buildingTons = Safe(() => player.ShipTonnageUnderConstruction(), 0f);
        float capacityLimit = Safe(() => player.ShipbuildingCapacityLimit(), 0f);
        float overrideLimit = Safe(() => CampaignController.Param("override_shipybuilding_limit", 1f), 1f);
        float queueLimit = capacityLimit * Math.Max(0f, overrideLimit);
        bool queueGate = buildingTons < queueLimit;

        float stateBudget = Safe(() => player.StateBudget(), 0f);
        float aiShipGdpRatio = Safe(() => CampaignController.Param("ai_ship_gdp_ratio", 1f), 1f);
        CampaignAiShipbuildingDiagnosticsPatch.FleetMetricSnapshot fleetMetrics =
            CampaignAiShipbuildingDiagnosticsPatch.FleetMetricSnapshot.For(player, stateBudget, aiShipGdpRatio);
        bool fleetGdpGate = fleetMetrics.VanillaFleetGdpGate;

        bool typeGate = Safe(() => shipType.canBuild, false) &&
                        Safe(() => Ship.GetHull(player, shipType, true) != null, false);

        float maxShipyard = Safe(() => player.MaxShipyard(), 0f);
        bool shipyardGate = maxShipyard > 0f;

        List<string> reasons = new();
        if (!cashGate)
            reasons.Add("cash");
        if (!crewGate)
            reasons.Add("crew");
        if (!queueGate)
            reasons.Add("queue");
        if (!fleetGdpGate)
            reasons.Add("fleetGDP");
        if (!typeGate)
            reasons.Add("type");
        if (!shipyardGate)
            reasons.Add("shipyard");

        return new PreflightResult(
            reasons.Count == 0,
            reasons.Count == 0 ? "pass" : string.Join(",", reasons),
            cash,
            cashNeed,
            crew,
            minCrew,
            queueGate,
            buildingTons,
            queueLimit,
            fleetGdpGate,
            fleetMetrics.VanillaFleetMetric,
            fleetMetrics.VanillaFleetCapTons,
            fleetMetrics.VanillaFleetBudgetRatio,
            typeGate,
            maxShipyard);
    }

    private static void LogSkip(Player player, ShipType shipType, PreflightResult preflight, string phase)
    {
        string key = $"{PlayerPointer(player)}:{AiDesignCompetitiveness.CurrentTurnIndex()}:{NormalizeShipType(shipType)}:{phase}:{preflight.Reason}";
        if (!LoggedSkips.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} preflight-skip nation={AiDesignCompetitiveness.PlayerLabel(player)} type={NormalizeShipType(shipType)} phase={phase} reason={preflight.Reason} cash={Fmt(preflight.Cash)} cashNeed={Fmt(preflight.CashNeed)} crew={preflight.Crew}/{Fmt(preflight.MinCrew)} queue={PassFail(preflight.QueueGate)} buildingTons={Fmt(preflight.BuildingTons)} queueLimit={Fmt(preflight.QueueLimit)} vanillaFleetGDP={PassFail(preflight.FleetGdpGate)} vanillaFleetMetric={Fmt(preflight.VanillaFleetMetric)} vanillaFleetCapTons={Fmt(preflight.VanillaFleetCapTons)} vanillaFleetBudgetRatio={Fmt(preflight.VanillaFleetBudgetRatio)} typeGate={PassFail(preflight.TypeGate)} maxShipyard={Fmt(preflight.MaxShipyard)}.");

        if (LoggedSkips.Count > 1024)
            LoggedSkips.Clear();
    }

    private static string NormalizeShipType(ShipType? shipType)
        => AiDesignCompetitiveness.NormalizeShipType(shipType);

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static string PassFail(bool value)
        => value ? "pass" : "block";

    private static string Fmt(float value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

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

    private sealed class PreflightResult
    {
        internal PreflightResult(
            bool pass,
            string reason,
            float cash,
            float cashNeed,
            int crew,
            float minCrew,
            bool queueGate,
            float buildingTons,
            float queueLimit,
            bool fleetGdpGate,
            float vanillaFleetMetric,
            float vanillaFleetCapTons,
            float vanillaFleetBudgetRatio,
            bool typeGate,
            float maxShipyard)
        {
            Pass = pass;
            Reason = reason;
            Cash = cash;
            CashNeed = cashNeed;
            Crew = crew;
            MinCrew = minCrew;
            QueueGate = queueGate;
            BuildingTons = buildingTons;
            QueueLimit = queueLimit;
            FleetGdpGate = fleetGdpGate;
            VanillaFleetMetric = vanillaFleetMetric;
            VanillaFleetCapTons = vanillaFleetCapTons;
            VanillaFleetBudgetRatio = vanillaFleetBudgetRatio;
            TypeGate = typeGate;
            MaxShipyard = maxShipyard;
        }

        internal bool Pass { get; }
        internal string Reason { get; }
        internal float Cash { get; }
        internal float CashNeed { get; }
        internal int Crew { get; }
        internal float MinCrew { get; }
        internal bool QueueGate { get; }
        internal float BuildingTons { get; }
        internal float QueueLimit { get; }
        internal bool FleetGdpGate { get; }
        internal float VanillaFleetMetric { get; }
        internal float VanillaFleetCapTons { get; }
        internal float VanillaFleetBudgetRatio { get; }
        internal bool TypeGate { get; }
        internal float MaxShipyard { get; }
    }
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationGenerateRandomDesignsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI DesignGen] GenerateRandomDesigns target not found; design-generation diagnostics disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "GenerateRandomDesigns", new[] { typeof(Player), typeof(bool) });

    [HarmonyPrefix]
    private static void Prefix(CampaignController __instance, Player player, bool prewarming)
        => CampaignAiDesignGenerationDiagnostics.BeginGenerate(__instance, player, prewarming);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationBuildNewShipsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI DesignGen] BuildNewShips target not found; design-generation result diagnostics disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "BuildNewShips", new[] { typeof(Player), typeof(float) });

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static void Prefix(CampaignController __instance, Player player)
        => CampaignAiDesignGenerationDiagnostics.CompleteBeforeBuild(__instance, player);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationGenerateRandomDesignsMoveNextPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI DesignGen] GenerateRandomDesigns MoveNext target not found; gate diagnostics disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignGenerationDiagnostics.GenerateRandomDesignsMoveNextTarget();

    [HarmonyPrefix]
    private static void Prefix(object __instance)
        => CampaignAiDesignGenerationDiagnostics.RecordGenerateMoveNextPrefix(__instance);

    [HarmonyPostfix]
    private static void Postfix(object __instance, bool __result)
        => CampaignAiDesignGenerationDiagnostics.RecordGenerateMoveNextPostfix(__instance, __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationRandomShipMoveNextPatch
{
    [HarmonyPrepare]
    private static void Prepare()
    {
        if (TargetMethod() == null)
            Melon<UADVanillaPlusMod>.Logger.Warning("[AI DesignGen] Ship.GenerateRandomShip MoveNext target not found; random-attempt diagnostics disabled.");
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignGenerationDiagnostics.GenerateRandomShipMoveNextTarget();

    [HarmonyPrefix]
    private static void Prefix(object __instance)
        => CampaignAiDesignGenerationDiagnostics.RecordRandomMoveNextPrefix(__instance);

    [HarmonyPostfix]
    private static void Postfix(object __instance, bool __result)
        => CampaignAiDesignGenerationDiagnostics.RecordRandomMoveNextPostfix(__instance, __result);

    [HarmonyFinalizer]
    private static void Finalizer(object __instance, Exception? __exception)
        => CampaignAiDesignGenerationDiagnostics.RecordRandomMoveNextFinalizer(__instance, __exception);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationExistingDesignTypeSelectorPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            CampaignAiDesignGenerationDiagnostics.WarnMissingPredicateTarget("existing-type-selector");

        return available;
    }

    private static MethodBase? TargetMethod()
    {
        try
        {
            return typeof(CampaignController)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                .FirstOrDefault(method =>
                {
                    if (!method.Name.Contains("GenerateRandomDesigns", StringComparison.Ordinal) ||
                        !method.Name.EndsWith("b__202_1", StringComparison.Ordinal) ||
                        method.ReturnType != typeof(ShipType))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(Ship);
                });
        }
        catch
        {
            return null;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(Ship s, ref ShipType? __result)
        => CampaignAiDesignGenerationDiagnostics.ApplyExistingDesignTypeSelectorRefreshOverride(s, ref __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationRandomFromShipTypePatch
{
    private static bool Prepare()
    {
        // Disabled: patching this Il2Cpp generic method specialization corrupts
        // the shared RandomFrom<T> trampoline for unrelated T values during startup.
        return false;
    }

    private static MethodBase? TargetMethod()
    {
        try
        {
            return typeof(Util)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method =>
                    method.Name.Equals("RandomFrom", StringComparison.Ordinal) &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 3)
                .Select(method =>
                {
                    try
                    {
                        return method.MakeGenericMethod(typeof(ShipType));
                    }
                    catch
                    {
                        return null;
                    }
                })
                .FirstOrDefault(method => method != null);
        }
        catch
        {
            return null;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(ref ShipType? __result)
        => CampaignAiDesignGenerationDiagnostics.ApplyRandomFromShipTypeRefreshOverride(ref __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationMissingPredicateVanillaPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            CampaignAiDesignGenerationDiagnostics.WarnMissingPredicateTarget("missing");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__0", typeof(ShipType), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(object __instance, ShipType t, bool __result)
        => CampaignAiDesignGenerationDiagnostics.RecordShipTypePredicateVanilla(__instance, "missing", t, __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationMissingPredicateFinalPatch
{
    private static bool Prepare()
        => TargetMethod() != null;

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__0", typeof(ShipType), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance, ShipType t, ref bool __result)
    {
        CampaignAiDesignGenerationPreflight.ApplyShipTypePredicatePreflight(__instance, t, ref __result, "missing");
        CampaignAiDesignGenerationDiagnostics.RecordShipTypePredicateFinal(__instance, "missing", t, __result);
    }
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationReplacementPredicateVanillaPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            CampaignAiDesignGenerationDiagnostics.WarnMissingPredicateTarget("replacement");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__2", typeof(ShipType), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(object __instance, ShipType t, bool __result)
        => CampaignAiDesignGenerationDiagnostics.RecordShipTypePredicateVanilla(__instance, "replacement", t, __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationReplacementPredicateFinalPatch
{
    private static bool Prepare()
        => TargetMethod() != null;

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__2", typeof(ShipType), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance, ShipType t, ref bool __result)
    {
        CampaignAiDesignGenerationPreflight.ApplyShipTypePredicatePreflight(__instance, t, ref __result, "replacement");
        CampaignAiDesignGenerationDiagnostics.RecordShipTypePredicateFinal(__instance, "replacement", t, __result);
    }
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationStalePredicateVanillaPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            CampaignAiDesignGenerationDiagnostics.WarnMissingPredicateTarget("stale");

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__3", typeof(Ship), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(object __instance, Ship s, bool __result)
        => CampaignAiDesignGenerationDiagnostics.RecordStalePredicateVanilla(__instance, s, __result);
}

[HarmonyPatch]
internal static class CampaignAiDesignGenerationStalePredicateFinalPatch
{
    private static bool Prepare()
        => TargetMethod() != null;

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__3", typeof(Ship), typeof(bool));

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance, Ship s, ref bool __result)
    {
        CampaignAiDesignGenerationDiagnostics.ApplyFreshnessOverride(__instance, s, ref __result);
        CampaignAiDesignGenerationPreflight.ApplyShipTypePredicatePreflight(__instance, s?.shipType, ref __result, "stale");
        CampaignAiDesignGenerationDiagnostics.RecordStalePredicateFinal(__instance, s, __result);
    }
}
