using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: keep vanilla AI shipbuilding gates intact, but replace the
// final random surface-type pick with a deficit-weighted pick when VP's AI
// Fleet Mix profile is active. The picker also probes vanilla buildability so
// stale/obsolete designs do not make a ship type look selectable.
[HarmonyPatch]
internal static class CampaignAiWeightedBuildTypePickerPatch
{
    private const string LogPrefix = "UADVP AI Build weighted type pick";
    private const float MinimumWeight = 0.0001f;

    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly string[] CoreGrowthFallbackTypes = { "BB", "BC", "CA", "CL" };
    private static readonly Dictionary<string, WeightedPickDecision> DecisionCache = new(StringComparer.Ordinal);
    private static readonly object RandomLock = new();
    private static readonly Random WeightedRandom = new();
    private static PropertyInfo? localsProperty;
    private static PropertyInfo? playerProperty;
    private static bool loggedMissingTarget;

    private static bool Prepare()
    {
        bool available = ResolveTargetMethod() != null;
        if (!available && !loggedMissingTarget)
        {
            loggedMissingTarget = true;
            Melon<UADVanillaPlusMod>.Logger.Warning(
                "UADVP AI Build weighted type pick: BuildNewShips ship-type predicate target not found; weighted picker disabled for this runtime.");
        }

        return available;
    }

    private static MethodBase? TargetMethod()
        => ResolveTargetMethod();

    private static MethodInfo? ResolveTargetMethod()
    {
        foreach (Type displayType in typeof(CampaignController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!displayType.Name.Contains("DisplayClass", StringComparison.Ordinal))
                continue;

            MethodInfo? method = displayType
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(IsWeightedTypePredicateTarget);
            if (method == null)
                continue;

            ResolvePlayerProperties(displayType);
            if (playerProperty == null)
                continue;

            return method;
        }

        return null;
    }

    private static bool IsWeightedTypePredicateTarget(MethodInfo method)
    {
        if (!method.Name.Contains("BuildNewShips", StringComparison.Ordinal))
            return false;
        if (!method.Name.EndsWith("b__4", StringComparison.Ordinal) &&
            !method.Name.EndsWith("b__198_4", StringComparison.Ordinal))
        {
            return false;
        }
        if (method.ReturnType != typeof(bool))
            return false;

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(ShipType);
    }

    private static void ResolvePlayerProperties(Type displayType)
    {
        localsProperty = null;
        playerProperty = null;

        PropertyInfo? directPlayer = displayType
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(property => property.PropertyType == typeof(Player) && property.GetMethod != null);
        if (directPlayer != null)
        {
            playerProperty = directPlayer;
            return;
        }

        foreach (PropertyInfo property in displayType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (property.GetMethod == null)
                continue;

            PropertyInfo? nestedPlayer = property.PropertyType
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(nestedProperty => nestedProperty.PropertyType == typeof(Player) && nestedProperty.GetMethod != null);
            if (nestedPlayer == null)
                continue;

            localsProperty = property;
            playerProperty = nestedPlayer;
            return;
        }
    }

    private static void Postfix(object __instance, ShipType t, ref bool __result)
    {
        if (t == null)
            return;

        if (ModSettings.AiFleetComposition == ModSettings.AiFleetCompositionMode.Vanilla)
            return;

        Player? player = TryGetPlayer(__instance);
        if (!ShouldApply(player))
            return;

        bool vanillaResultBefore = __result;
        WeightedPickDecision decision = DecisionFor(player!);
        if (decision.SuppressVanilla)
        {
            __result = false;
            CampaignAiShipbuildingDiagnosticsPatch.RecordWeightedTypePredicate(
                player,
                t,
                vanillaResultBefore,
                __result,
                decision.SelectedType,
                decision.Reason,
                decision.Fallback,
                decision.SuppressVanilla);
            return;
        }

        if (string.IsNullOrWhiteSpace(decision.SelectedType))
        {
            CampaignAiShipbuildingDiagnosticsPatch.RecordWeightedTypePredicate(
                player,
                t,
                vanillaResultBefore,
                __result,
                decision.SelectedType,
                decision.Reason,
                decision.Fallback,
                decision.SuppressVanilla);
            return;
        }

        bool selected = string.Equals(NormalizeShipType(t), decision.SelectedType, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(decision.Reason, "growthFallback", StringComparison.Ordinal))
        {
            __result = selected;
            CampaignAiShipbuildingDiagnosticsPatch.RecordWeightedTypePredicate(
                player,
                t,
                vanillaResultBefore,
                __result,
                decision.SelectedType,
                decision.Reason,
                decision.Fallback,
                decision.SuppressVanilla);
            return;
        }

        if (!__result)
        {
            CampaignAiShipbuildingDiagnosticsPatch.RecordWeightedTypePredicate(
                player,
                t,
                vanillaResultBefore,
                __result,
                decision.SelectedType,
                decision.Reason,
                decision.Fallback,
                decision.SuppressVanilla);
            return;
        }

        __result = selected;
        CampaignAiShipbuildingDiagnosticsPatch.RecordWeightedTypePredicate(
            player,
            t,
            vanillaResultBefore,
            __result,
            decision.SelectedType,
            decision.Reason,
            decision.Fallback,
            decision.SuppressVanilla);
    }

    internal static void LogDisabledForVanilla(Player? player, string dateLabel)
    {
        if (!ShouldTracePlayer(player) || ModSettings.AiFleetComposition != ModSettings.AiFleetCompositionMode.Vanilla)
            return;

        string key = $"disabled:{PlayerPointer(player!)}:{CampaignTurn()}";
        if (!DecisionCache.ContainsKey(key))
        {
            DecisionCache[key] = WeightedPickDecision.Disabled;
            Log($"nation={PlayerLabel(player)} turn={dateLabel} mix=Vanilla disabled=true reason=vanilla-mode.");
        }
    }

    private static WeightedPickDecision DecisionFor(Player player)
    {
        string key = DecisionKey(player);
        if (DecisionCache.TryGetValue(key, out WeightedPickDecision cached))
            return cached;

        WeightedPickDecision decision = BuildDecision(player);
        DecisionCache[key] = decision;
        LogDecision(player, decision);
        return decision;
    }

    private static WeightedPickDecision BuildDecision(Player player)
    {
        List<TypeCandidate> entries = BuildTypeCandidates(player);
        List<TypeCandidate> weightedCandidates = entries.Where(entry => entry.IsCandidate && entry.Weight > MinimumWeight).ToList();
        if (weightedCandidates.Count == 0)
            return GrowthFallbackDecision(entries, "noWeightedCandidates");

        float totalWeight = weightedCandidates.Sum(entry => entry.Weight);
        if (totalWeight <= MinimumWeight || float.IsNaN(totalWeight) || float.IsInfinity(totalWeight))
            return GrowthFallbackDecision(entries, "invalidWeight");

        float roll;
        lock (RandomLock)
            roll = (float)WeightedRandom.NextDouble() * totalWeight;

        float accumulated = 0f;
        foreach (TypeCandidate candidate in weightedCandidates)
        {
            accumulated += candidate.Weight;
            if (roll <= accumulated)
                return new WeightedPickDecision(candidate.Type, false, false, "weightedCandidate", entries);
        }

        return new WeightedPickDecision(weightedCandidates[^1].Type, false, false, "weightedCandidate", entries);
    }

    private static WeightedPickDecision GrowthFallbackDecision(IReadOnlyList<TypeCandidate> entries, string suppressReason)
    {
        foreach (string type in CoreGrowthFallbackTypes)
        {
            TypeCandidate entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
            if (IsCoreGrowthFallbackBuildable(entry))
                return new WeightedPickDecision(entry.Type, false, false, "growthFallback", entries);
        }

        return new WeightedPickDecision(null, false, true, suppressReason, entries);
    }

    private static bool IsCoreGrowthFallbackBuildable(TypeCandidate entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Type))
            return false;
        if (entry.BuildableCount <= 0 || entry.DesiredShare <= 0f)
            return false;

        return string.Equals(entry.Reason, "shareSatisfied", StringComparison.Ordinal) ||
            (entry.IsCandidate && entry.Weight <= MinimumWeight);
    }

    private static List<TypeCandidate> BuildTypeCandidates(Player player)
    {
        Dictionary<string, ShipType?> shipTypes = SurfaceTypes.ToDictionary(type => type, FindShipType, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> counts = SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);
        int surfaceCount = 0;
        foreach (Ship ship in SafeShipList(player.fleet))
        {
            string type = NormalizeShipType(ship.shipType);
            if (!counts.ContainsKey(type))
                continue;

            counts[type]++;
            surfaceCount++;
        }

        Dictionary<string, float> finalRatios = new(StringComparer.OrdinalIgnoreCase);
        foreach (string type in SurfaceTypes)
        {
            ShipType? shipType = shipTypes[type];
            float baseRatio = Safe(() => shipType?.buildRatio ?? 0f, 0f);
            finalRatios[type] = shipType == null ? 0f : Safe(() => player.GetShipBuildRatio(shipType.name, baseRatio), baseRatio);
        }

        float desiredTotal = finalRatios.Values.Where(value => value > 0f).Sum();
        List<TypeCandidate> result = new();
        foreach (string type in SurfaceTypes)
        {
            ShipType? shipType = shipTypes[type];
            float finalRatio = finalRatios[type];
            float desiredShare = desiredTotal <= 0f ? 0f : finalRatio / desiredTotal;
            float currentShare = surfaceCount <= 0 ? 0f : counts[type] / (float)surfaceCount;
            float weight = surfaceCount <= 0 ? desiredShare : Math.Max(0f, desiredShare - currentShare);
            ValidationSummary validation = ValidateDesigns(player, type);
            string reason = CandidateNoReason(shipType, finalRatio, desiredShare, currentShare, validation);
            bool candidate = string.Equals(reason, "candidate", StringComparison.Ordinal);

            result.Add(new TypeCandidate(
                type,
                candidate,
                candidate ? weight : 0f,
                reason,
                desiredShare,
                currentShare,
                validation.BuildableCount));
        }

        return result;
    }

    private static string CandidateNoReason(
        ShipType? shipType,
        float finalRatio,
        float desiredShare,
        float currentShare,
        ValidationSummary validation)
    {
        if (shipType == null)
            return "unavailableType";
        if (!Safe(() => shipType.canBuild, false))
            return "unavailable";
        if (finalRatio <= 0f || desiredShare <= 0f)
            return "zeroRatio";
        if (validation.DesignCount <= 0)
            return "noDesign";
        if (validation.BuildableCount <= 0)
            return string.IsNullOrWhiteSpace(validation.BlockReason)
                ? "noBuildableDesign"
                : "noBuildableDesign:" + validation.BlockReason;
        if (currentShare > desiredShare)
            return "shareSatisfied";

        return "candidate";
    }

    private static ValidationSummary ValidateDesigns(Player player, string type)
    {
        int designCount = 0;
        int buildableCount = 0;
        Dictionary<string, int> failureReasons = new(StringComparer.Ordinal);

        foreach (Ship design in SafeShipList(player.designs))
        {
            if (!string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
                continue;

            designCount++;
            if (ModSettings.AiArmsRaceEnabled &&
                !AiDesignCompetitiveness.IsCompetitive(design, out AiDesignCompetitiveness.CompetitivenessInfo info))
            {
                AddFailureReason(failureReasons, "vp-" + info.Reason);
                continue;
            }

            if (!AiDesignBuildability.CanBuildDesign(player, design, 1, "weighted-build-type-picker", out string reason))
            {
                AddFailureReason(failureReasons, reason);
                continue;
            }

            buildableCount++;
        }

        return new ValidationSummary(designCount, buildableCount, DominantFailureReason(failureReasons));
    }

    private static void AddFailureReason(Dictionary<string, int> reasons, string reason)
    {
        string normalized = CompactReason(reason);
        reasons[normalized] = reasons.TryGetValue(normalized, out int count) ? count + 1 : 1;
    }

    private static string DominantFailureReason(Dictionary<string, int> reasons)
        => reasons.Count == 0
            ? string.Empty
            : reasons
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .First()
                .Key;

    private static string CompactReason(string? reason)
    {
        string normalized = AiDesignBuildability.NormalizeReason(reason);
        if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase))
            return "unknown";

        int separator = normalized.IndexOf(':');
        if (separator > 0)
            normalized = normalized[..separator];

        return normalized
            .Replace("|", "_", StringComparison.Ordinal)
            .Replace(",", "_", StringComparison.Ordinal);
    }

    private static void LogDecision(Player player, WeightedPickDecision decision)
    {
        IEnumerable<string> candidates = decision.Candidates
            .Where(candidate => candidate.IsCandidate)
            .Select(candidate => $"{candidate.Type}:w{Fmt(candidate.Weight)}")
            .DefaultIfEmpty("none");

        IEnumerable<string> blocked = decision.Candidates
            .Where(candidate => !candidate.IsCandidate)
            .Select(candidate => $"{candidate.Type}|{candidate.Reason}")
            .DefaultIfEmpty("none");

        Log(
            $"nation={PlayerLabel(player)} mix={ModSettings.AiFleetCompositionModeText(ModSettings.AiFleetComposition)} candidates={string.Join(",", candidates)} blocked={string.Join(",", blocked)} selected={decision.SelectedType ?? "none"} fallback={decision.Fallback.ToString().ToLowerInvariant()} suppressVanilla={decision.SuppressVanilla.ToString().ToLowerInvariant()} reason={decision.Reason}.");
    }

    private static string DecisionKey(Player player)
    {
        Dictionary<string, int> counts = SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);
        int surfaceCount = 0;
        foreach (Ship ship in SafeShipList(player.fleet))
        {
            string type = NormalizeShipType(ship.shipType);
            if (!counts.ContainsKey(type))
                continue;

            counts[type]++;
            surfaceCount++;
        }

        string countSignature = string.Join(",", SurfaceTypes.Select(type => $"{type}{counts[type]}"));
        return $"{PlayerPointer(player)}:{CampaignTurn()}:{ModSettings.AiFleetComposition}:{surfaceCount}:{countSignature}";
    }

    private static Player? TryGetPlayer(object displayClass)
    {
        try
        {
            object? source = localsProperty == null ? displayClass : localsProperty.GetValue(displayClass);
            return source == null ? null : playerProperty?.GetValue(source) as Player;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldApply(Player? player)
        => ShouldTracePlayer(player) && player!.designs != null;

    private static bool ShouldTracePlayer(Player? player)
    {
        if (player == null)
            return false;

        try
        {
            return player.isAi && !player.isMain;
        }
        catch
        {
            return false;
        }
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
    {
        string raw = SafeString(() => type?.name);
        if (string.Equals(raw, "<empty>", StringComparison.OrdinalIgnoreCase))
            raw = SafeString(() => type?.nameUi);

        return NormalizeShipType(raw);
    }

    private static string NormalizeShipType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNK";

        string compact = value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        return compact switch
        {
            "B" or "BB" or "BATTLESHIP" => "BB",
            "BC" or "BATTLECRUISER" => "BC",
            "CA" or "HEAVYCRUISER" => "CA",
            "CL" or "LIGHTCRUISER" => "CL",
            "DD" or "DESTROYER" => "DD",
            "TB" or "TORPEDOBOAT" => "TB",
            _ => compact
        };
    }

    private static string PlayerLabel(Player? player)
    {
        if (player == null)
            return "<null>";

        string name = SafeString(() => player.Name(false));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => player.data?.name);
    }

    private static long PlayerPointer(Player player)
        => player.Pointer.ToInt64();

    private static int CampaignTurn()
        => Safe(() => CampaignController.Instance?.CurrentDate.turn ?? -1, -1);

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

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
        => Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: {message}");

    private readonly record struct ValidationSummary(int DesignCount, int BuildableCount, string BlockReason);

    private readonly record struct TypeCandidate(
        string Type,
        bool IsCandidate,
        float Weight,
        string Reason,
        float DesiredShare,
        float CurrentShare,
        int BuildableCount);

    private readonly record struct WeightedPickDecision(
        string? SelectedType,
        bool Fallback,
        bool SuppressVanilla,
        string Reason,
        IReadOnlyList<TypeCandidate> Candidates)
    {
        internal static WeightedPickDecision Disabled { get; } = new(null, true, false, "disabled", Array.Empty<TypeCandidate>());
    }
}
