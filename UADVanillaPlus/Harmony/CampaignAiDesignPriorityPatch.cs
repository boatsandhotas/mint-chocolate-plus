using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: when AI Arms Race is enabled, keep vanilla design generation
// mechanics intact but bias the vanilla ShipType random pick toward surface
// types where the AI's current design book is below the world benchmark.
internal static class CampaignAiDesignPriorityPatch
{
    private const string LogPrefix = "UADVP AI Design priority";
    private const float MinimumWeight = 0.0001f;
    private const int SoftBackoffTurns = 2;
    private const int MediumBackoffTurns = 4;
    private const int HardBackoffTurns = 8;

    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly Dictionary<string, DesignPriorityDecision> DecisionCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DesignPriorityDecision> LastDecisionByPlayerTurn = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, PropertyInfo?> DisplayClassPlayerPropertyByType = new();
    private static readonly Dictionary<string, PriorityBackoffState> BackoffByPlayerTypePhase = new(StringComparer.Ordinal);
    private static readonly HashSet<string> OutcomeLogged = new(StringComparer.Ordinal);
    private static readonly object RandomLock = new();
    private static readonly Random WeightedRandom = new();
    private enum DesignPriorityBranch
    {
        MissingType,
        ReplacementType
    }

    internal static void ApplyShipTypePredicatePriority(object displayClass, ShipType candidate, ref bool result, bool replacementBranch)
    {
        if (!result || candidate == null || !ModSettings.AiArmsRaceEnabled)
            return;

        Player? player = TryGetDisplayClassPlayer(displayClass);
        if (!IsAiPlayer(player))
            return;

        DesignPriorityBranch branch = replacementBranch ? DesignPriorityBranch.ReplacementType : DesignPriorityBranch.MissingType;
        List<ShipType> candidates = CandidateShipTypesForBranch(player!, branch);
        if (!candidates.Any(type => string.Equals(NormalizeShipType(type), NormalizeShipType(candidate), StringComparison.OrdinalIgnoreCase)))
            return;

        DesignPriorityDecision decision = DecisionFor(player!, candidates, branch);
        if (decision.Fallback || string.IsNullOrWhiteSpace(decision.SelectedType))
            return;

        result = string.Equals(NormalizeShipType(candidate), decision.SelectedType, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyStaleDesignPredicatePriority(object displayClass, Ship design, ref bool result)
    {
        if (result || design == null || !ModSettings.AiArmsRaceEnabled || !IsActiveDesign(design))
            return;

        Player? player = TryGetDisplayClassPlayer(displayClass);
        if (!IsAiPlayer(player))
            return;

        List<ShipType> candidates = CandidateShipTypesForBranch(player!, DesignPriorityBranch.ReplacementType);
        DesignPriorityDecision decision = DecisionFor(player!, candidates, DesignPriorityBranch.ReplacementType);
        if (decision.Fallback || string.IsNullOrWhiteSpace(decision.SelectedType))
            return;

        result = string.Equals(NormalizeShipType(design.shipType), decision.SelectedType, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RecordGeneratedDesignResult(Player player, IReadOnlyList<Ship> newDesigns)
    {
        if (!ModSettings.AiArmsRaceEnabled || !IsAiPlayer(player))
            return;

        string playerTurnKey = PlayerTurnKey(player);
        if (!LastDecisionByPlayerTurn.TryGetValue(playerTurnKey, out DesignPriorityDecision decision))
            return;

        if (decision.Fallback || string.IsNullOrWhiteSpace(decision.SelectedType))
            return;

        string outcomeKey = $"{playerTurnKey}:{decision.SelectedType}:{decision.DecisionSignature}";
        if (!OutcomeLogged.Add(outcomeKey))
            return;

        float bestAfter = BestCurrentRatio(player, decision.SelectedType!, out _);
        int selectedNewCount = newDesigns.Count(design =>
            design != null &&
            !Safe(() => design.isErased, true) &&
            string.Equals(NormalizeShipType(design.shipType), decision.SelectedType, StringComparison.OrdinalIgnoreCase));

        string result;
        if (bestAfter >= AiDesignCompetitiveness.MinimumCompetitiveRatio && bestAfter > decision.SelectedBestRatio + 0.001f)
            result = "competitiveDesignGenerated";
        else if (bestAfter > decision.SelectedBestRatio + 0.001f)
            result = "improvedButStillWeak";
        else
            result = "noNewCompetitiveDesign";

        UpdateBackoff(player, decision, result, bestAfter);

        Log(
            $"turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} selected={decision.SelectedType} result={result} newSelected={selectedNewCount} bestBefore={FmtPercent(decision.SelectedBestRatio)} bestAfter={FmtPercent(bestAfter)} threshold={AiDesignCompetitiveness.FormatRatio(AiDesignCompetitiveness.MinimumCompetitiveRatio)}.");
    }

    private static DesignPriorityDecision DecisionFor(Player player, IReadOnlyCollection<ShipType> candidateTypes, DesignPriorityBranch branch)
    {
        string key = DecisionKey(player, candidateTypes, branch);
        if (DecisionCache.TryGetValue(key, out DesignPriorityDecision cached))
            return cached;

        DesignPriorityDecision decision = BuildDecision(player, candidateTypes, branch, key);
        DecisionCache[key] = decision;
        LastDecisionByPlayerTurn[PlayerTurnKey(player)] = decision;
        LogDecision(player, decision);
        return decision;
    }

    private static DesignPriorityDecision BuildDecision(Player player, IReadOnlyCollection<ShipType> candidateTypes, DesignPriorityBranch branch, string decisionSignature)
    {
        HashSet<string> allowedTypes = candidateTypes
            .Select(NormalizeShipType)
            .Where(type => SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<DesignNeed> needs = BuildDesignNeeds(player, allowedTypes, branch);
        List<DesignNeed> weighted = needs
            .Where(need => need.IsCandidate && need.Weight > MinimumWeight)
            .ToList();

        if (weighted.Count == 0)
            return new DesignPriorityDecision(null, true, 0f, BranchName(branch), decisionSignature, needs);

        float totalWeight = weighted.Sum(static need => need.Weight);
        if (totalWeight <= MinimumWeight || float.IsNaN(totalWeight) || float.IsInfinity(totalWeight))
            return new DesignPriorityDecision(null, true, 0f, BranchName(branch), decisionSignature, needs);

        float roll;
        lock (RandomLock)
            roll = (float)WeightedRandom.NextDouble() * totalWeight;

        float accumulated = 0f;
        foreach (DesignNeed need in weighted)
        {
            accumulated += need.Weight;
            if (roll <= accumulated)
                return new DesignPriorityDecision(need.Type, false, need.BestRatio, BranchName(branch), decisionSignature, needs);
        }

        DesignNeed fallback = weighted[^1];
        return new DesignPriorityDecision(fallback.Type, false, fallback.BestRatio, BranchName(branch), decisionSignature, needs);
    }

    private static List<DesignNeed> BuildDesignNeeds(Player player, IReadOnlySet<string> allowedTypes, DesignPriorityBranch branch)
    {
        List<Ship> designs = SafeShipList(player.designs)
            .Where(IsActiveDesign)
            .ToList();

        List<DesignNeed> result = new();
        foreach (string type in SurfaceTypes)
        {
            if (!allowedTypes.Contains(type))
            {
                result.Add(DesignNeed.Unavailable(type, "vanillaFiltered"));
                continue;
            }

            ShipType? shipType = FindShipType(type);
            if (!ShipTypeAvailable(player, shipType))
            {
                result.Add(DesignNeed.Unavailable(type, "unavailable"));
                continue;
            }

            EffectivePowerBenchmark benchmark = ShipEffectivePowerCalculator.BenchmarkForType(type);
            if (benchmark.SampleCount <= 0 || benchmark.AdjustedPower <= 0f)
            {
                result.Add(DesignNeed.Unavailable(type, "noBenchmark"));
                continue;
            }

            float bestRatio = 0f;
            foreach (Ship design in designs)
            {
                if (!string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
                    continue;

                EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(design, benchmark);
                if (power.BenchmarkAdjustedPower <= 0f)
                    continue;

                bestRatio = Math.Max(bestRatio, power.CompetitivenessRatio);
            }

            float gap = Math.Max(0f, AiDesignCompetitiveness.MinimumCompetitiveRatio - bestRatio);
            float weight = gap <= 0f ? 0f : gap / AiDesignCompetitiveness.MinimumCompetitiveRatio;
            weight *= weight;
            BackoffEffect backoff = BackoffFor(player, type, branch);
            bool isCandidate = gap > 0f;
            string reason = isCandidate ? "candidate" : "satisfied";
            if (isCandidate && backoff.IsActive)
            {
                if (backoff.Skip)
                {
                    isCandidate = false;
                    weight = 0f;
                    reason = "skipBackoff";
                }
                else
                {
                    weight *= backoff.WeightMultiplier;
                }
            }

            result.Add(new DesignNeed(
                type,
                true,
                isCandidate,
                bestRatio,
                gap,
                weight,
                reason,
                backoff.TurnsRemaining,
                backoff.FailureCount,
                backoff.WeightMultiplier,
                backoff.Skip));
        }

        return result;
    }

    private static void UpdateBackoff(Player player, DesignPriorityDecision decision, string result, float bestAfter)
    {
        if (string.IsNullOrWhiteSpace(decision.SelectedType))
            return;

        string key = BackoffKey(player, decision.SelectedType!, decision.Branch);
        int turn = AiDesignCompetitiveness.CurrentTurnIndex();

        if (string.Equals(result, "competitiveDesignGenerated", StringComparison.Ordinal))
        {
            if (BackoffByPlayerTypePhase.Remove(key))
            {
                DecisionCache.Clear();
                Log(
                    $"backoff turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} phase={decision.Branch} type={decision.SelectedType} failures=0 cooldownUntil=none result={result}.");
            }

            return;
        }

        BackoffByPlayerTypePhase.TryGetValue(key, out PriorityBackoffState? state);
        state ??= new PriorityBackoffState();

        if (string.Equals(result, "improvedButStillWeak", StringComparison.Ordinal))
        {
            state.FailureCount = Math.Max(1, Math.Min(state.FailureCount, 2));
            state.CooldownUntilTurn = Math.Max(state.CooldownUntilTurn, turn + SoftBackoffTurns);
        }
        else if (string.Equals(result, "noNewCompetitiveDesign", StringComparison.Ordinal))
        {
            state.FailureCount++;
            state.CooldownUntilTurn = turn + CooldownTurnsForFailures(state.FailureCount);
        }
        else
        {
            return;
        }

        state.LastBestRatio = bestAfter;
        state.LastResult = result;
        BackoffByPlayerTypePhase[key] = state;
        DecisionCache.Clear();

        Log(
            $"backoff turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} phase={decision.Branch} type={decision.SelectedType} failures={state.FailureCount} cooldownUntil={CooldownLabel(state.CooldownUntilTurn)} result={result} bestAfter={FmtPercent(bestAfter)}.");
    }

    private static BackoffEffect BackoffFor(Player player, string type, DesignPriorityBranch branch)
    {
        string key = BackoffKey(player, type, BranchName(branch));
        if (!BackoffByPlayerTypePhase.TryGetValue(key, out PriorityBackoffState? state))
            return BackoffEffect.None;

        int turn = AiDesignCompetitiveness.CurrentTurnIndex();
        if (turn < 0 || state.CooldownUntilTurn <= turn)
            return BackoffEffect.None;

        int turnsRemaining = Math.Max(1, state.CooldownUntilTurn - turn);
        if (state.FailureCount >= 3)
            return new BackoffEffect(true, true, turnsRemaining, state.FailureCount, 0f);
        if (state.FailureCount == 2)
            return new BackoffEffect(true, false, turnsRemaining, state.FailureCount, 0.2f);

        return new BackoffEffect(true, false, turnsRemaining, state.FailureCount, 0.5f);
    }

    private static int CooldownTurnsForFailures(int failures)
        => failures switch
        {
            <= 1 => SoftBackoffTurns,
            2 => MediumBackoffTurns,
            _ => HardBackoffTurns
        };

    private static float BestCurrentRatio(Player player, string type, out int designCount)
    {
        designCount = 0;
        EffectivePowerBenchmark benchmark = ShipEffectivePowerCalculator.BenchmarkForType(type);
        if (benchmark.SampleCount <= 0 || benchmark.AdjustedPower <= 0f)
            return 0f;

        float bestRatio = 0f;
        foreach (Ship design in SafeShipList(player.designs))
        {
            if (!IsActiveDesign(design) ||
                !string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            designCount++;
            EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(design, benchmark);
            if (power.BenchmarkAdjustedPower > 0f)
                bestRatio = Math.Max(bestRatio, power.CompetitivenessRatio);
        }

        return bestRatio;
    }

    private static bool IsActiveDesign(Ship? design)
    {
        if (design == null)
            return false;

        if (CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            return false;

        return !Safe(() => design.isErased, true) && Safe(() => design.isDesign || design.isRefitDesign, false);
    }

    private static bool ShipTypeAvailable(Player player, ShipType? shipType)
    {
        if (shipType == null)
            return false;
        if (!Safe(() => shipType.canBuild, false))
            return false;

        return Safe(() => Ship.GetHull(player, shipType, true) != null, false);
    }

    private static void LogDecision(Player player, DesignPriorityDecision decision)
    {
        string candidates = string.Join(";", decision.Needs.Select(FormatNeed));
        Log(
            $"turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} phase={decision.Branch} threshold={AiDesignCompetitiveness.FormatRatio(AiDesignCompetitiveness.MinimumCompetitiveRatio)} candidates={candidates} selected={decision.SelectedType ?? "none"} fallback={decision.Fallback.ToString().ToLowerInvariant()}.");
    }

    private static string FormatNeed(DesignNeed need)
    {
        if (!need.Available)
            return $"{need.Type}:{need.Reason}";

        string text = $"{need.Type}:{FmtPercentValue(need.BestRatio)},gap{FmtPercentValue(need.Gap)}";
        if (need.BackoffTurnsRemaining <= 0)
            return text;

        if (need.BackoffSkip)
            return $"{text},skipBackoff{need.BackoffTurnsRemaining}";

        return $"{text},backoff{need.BackoffTurnsRemaining}x{need.BackoffWeightMultiplier.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    private static string DecisionKey(Player player, IReadOnlyCollection<ShipType> candidateTypes, DesignPriorityBranch branch)
        => $"{PlayerPointer(player)}:{AiDesignCompetitiveness.CurrentTurnIndex()}:{AiDesignCompetitiveness.MinimumCompetitiveRatio.ToString("0.###", CultureInfo.InvariantCulture)}:{BranchName(branch)}:{CandidateSignature(candidateTypes)}:{DesignSignature(player)}";

    private static string PlayerTurnKey(Player player)
        => $"{PlayerPointer(player)}:{AiDesignCompetitiveness.CurrentTurnIndex()}";

    private static string CandidateSignature(IReadOnlyCollection<ShipType> candidateTypes)
        => string.Join(",", candidateTypes.Select(NormalizeShipType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.OrdinalIgnoreCase));

    private static string DesignSignature(Player player)
    {
        unchecked
        {
            long hash = 17;
            int count = 0;
            foreach (Ship design in SafeShipList(player.designs).Where(IsActiveDesign))
            {
                count++;
                hash = (hash * 31) + design.Pointer.ToInt64();
                hash = (hash * 31) + NormalizeShipType(design.shipType).GetHashCode();
                hash = (hash * 31) + Safe(() => design.isRefitDesign ? design.dateCreatedRefit.turn : design.dateCreated.turn, 0);
            }

            return $"{count}:{hash}";
        }
    }

    private static bool IsAiPlayer(Player? player)
        => player != null && Safe(() => player.isAi && !player.isMain, false);

    private static List<ShipType> CandidateShipTypesForBranch(Player player, DesignPriorityBranch branch)
    {
        HashSet<string> representedTypes = SafeShipList(player.designs)
            .Where(IsActiveDesign)
            .Select(design => NormalizeShipType(design.shipType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ShipType> result = new();
        foreach (string type in SurfaceTypes)
        {
            ShipType? shipType = FindShipType(type);
            if (!ShipTypeAvailable(player, shipType))
                continue;

            bool represented = representedTypes.Contains(type);
            if (branch == DesignPriorityBranch.MissingType && represented)
                continue;
            if (branch == DesignPriorityBranch.ReplacementType && !represented)
                continue;

            result.Add(shipType!);
        }

        return result;
    }

    private static Player? TryGetDisplayClassPlayer(object displayClass)
    {
        try
        {
            PropertyInfo? property = ResolveDisplayClassPlayerProperty(displayClass.GetType());
            return property?.GetValue(displayClass) as Player;
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo? ResolveDisplayClassPlayerProperty(Type displayClassType)
    {
        if (DisplayClassPlayerPropertyByType.TryGetValue(displayClassType, out PropertyInfo? cached))
            return cached;

        PropertyInfo? property = displayClassType
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(property => property.PropertyType == typeof(Player) && property.GetMethod != null);
        DisplayClassPlayerPropertyByType[displayClassType] = property;
        return property;
    }

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

    private static string NormalizeShipType(ShipType? type)
        => AiDesignCompetitiveness.NormalizeShipType(type);

    private static string FmtPercent(float ratio)
        => AiDesignCompetitiveness.FormatRatio(ratio);

    private static string FmtPercentValue(float ratio)
        => (ratio * 100f).ToString("0.0", CultureInfo.InvariantCulture);

    private static long PlayerPointer(Player player)
        => player.Pointer.ToInt64();

    private static string BranchName(DesignPriorityBranch branch)
        => branch == DesignPriorityBranch.ReplacementType ? "replacement" : "missing";

    private static string BackoffKey(Player player, string type, string branch)
        => $"{PlayerPointer(player)}:{branch}:{type}";

    private static string CooldownLabel(int cooldownTurn)
    {
        if (cooldownTurn < 0)
            return "none";

        try
        {
            GameDate date = new() { turn = cooldownTurn };
            return date.ToString(false);
        }
        catch
        {
            return $"turn-{cooldownTurn}";
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
        => Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: {message}");

    private readonly record struct DesignNeed(
        string Type,
        bool Available,
        bool IsCandidate,
        float BestRatio,
        float Gap,
        float Weight,
        string Reason,
        int BackoffTurnsRemaining,
        int BackoffFailureCount,
        float BackoffWeightMultiplier,
        bool BackoffSkip)
    {
        internal static DesignNeed Unavailable(string type, string reason)
            => new(type, false, false, 0f, 0f, 0f, reason, 0, 0, 1f, false);
    }

    private readonly record struct DesignPriorityDecision(
        string? SelectedType,
        bool Fallback,
        float SelectedBestRatio,
        string Branch,
        string DecisionSignature,
        IReadOnlyList<DesignNeed> Needs);

    private sealed class PriorityBackoffState
    {
        internal int FailureCount;
        internal int CooldownUntilTurn;
        internal float LastBestRatio;
        internal string LastResult = string.Empty;
    }

    private readonly record struct BackoffEffect(
        bool IsActive,
        bool Skip,
        int TurnsRemaining,
        int FailureCount,
        float WeightMultiplier)
    {
        internal static BackoffEffect None { get; } = new(false, false, 0, 0, 1f);
    }
}

[HarmonyPatch]
internal static class CampaignAiDesignPriorityMissingTypePredicatePatch
{
    private static bool loggedMissingTarget;
    private static bool loggedAttachedTarget;

    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available && !loggedMissingTarget)
        {
            loggedMissingTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design priority: GenerateRandomDesigns missing-type predicate target not found; missing-type priority disabled for this runtime.");
        }
        else if (available && !loggedAttachedTarget)
        {
            loggedAttachedTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP AI Design priority: attached GenerateRandomDesigns missing-type predicate hook.");
        }

        return available;
    }

    private static MethodBase? TargetMethod()
        => ResolvePredicateMethod("b__0", typeof(ShipType), typeof(bool));

    internal static MethodInfo? ResolvePredicateMethod(string suffix, Type parameterType, Type returnType)
    {
        return typeof(CampaignController)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .FirstOrDefault(method =>
            {
                if (!method.Name.Contains("GenerateRandomDesigns", StringComparison.Ordinal) ||
                    !method.Name.EndsWith(suffix, StringComparison.Ordinal) ||
                    method.ReturnType != returnType)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == parameterType;
            });
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, ShipType t, ref bool __result)
        => CampaignAiDesignPriorityPatch.ApplyShipTypePredicatePriority(__instance, t, ref __result, replacementBranch: false);
}

[HarmonyPatch]
internal static class CampaignAiDesignPriorityReplacementTypePredicatePatch
{
    private static bool loggedMissingTarget;
    private static bool loggedAttachedTarget;

    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available && !loggedMissingTarget)
        {
            loggedMissingTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design priority: GenerateRandomDesigns replacement-type predicate target not found; replacement priority disabled for this runtime.");
        }
        else if (available && !loggedAttachedTarget)
        {
            loggedAttachedTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP AI Design priority: attached GenerateRandomDesigns replacement-type predicate hook.");
        }

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__2", typeof(ShipType), typeof(bool));

    [HarmonyPostfix]
    private static void Postfix(object __instance, ShipType t, ref bool __result)
        => CampaignAiDesignPriorityPatch.ApplyShipTypePredicatePriority(__instance, t, ref __result, replacementBranch: true);
}

[HarmonyPatch]
internal static class CampaignAiDesignPriorityStaleDesignPredicatePatch
{
    private static bool loggedMissingTarget;
    private static bool loggedAttachedTarget;

    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available && !loggedMissingTarget)
        {
            loggedMissingTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design priority: GenerateRandomDesigns stale-design predicate target not found; stale-design priority disabled for this runtime.");
        }
        else if (available && !loggedAttachedTarget)
        {
            loggedAttachedTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP AI Design priority: attached GenerateRandomDesigns stale-design predicate hook.");
        }

        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignAiDesignPriorityMissingTypePredicatePatch.ResolvePredicateMethod("b__3", typeof(Ship), typeof(bool));

    [HarmonyPostfix]
    private static void Postfix(object __instance, Ship s, ref bool __result)
        => CampaignAiDesignPriorityPatch.ApplyStaleDesignPredicatePriority(__instance, s, ref __result);
}
