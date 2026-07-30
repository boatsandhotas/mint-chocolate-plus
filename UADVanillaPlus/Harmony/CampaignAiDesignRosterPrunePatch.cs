using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// AI design books can accumulate old, duplicate, or weak same-type templates
// from several sources. Before the AI selects new construction, keep only the
// strongest few surface designs per type for that nation. This is deliberately
// local to each nation's own roster; it is not the retired Arms Race global
// competitiveness/buildability gate.
internal static class CampaignAiDesignRosterPrunePatch
{
    private const string LogPrefix = "AI design roster prune";
    internal const int MaxDesignsPerType = 3;
    internal static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);

    internal static void PruneBeforeBuildNewShips(Player? player)
    {
        if (!IsAiPlayer(player))
            return;

        try
        {
            List<RosterDesign> designs = SafeShipList(player!.designs)
                .Where(ShouldConsiderDesign)
                .Select(design => BuildRosterDesign(design))
                .Where(candidate => SurfaceTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (designs.Count <= MaxDesignsPerType)
                return;

            foreach (IGrouping<string, RosterDesign> group in designs.GroupBy(design => design.Type, StringComparer.OrdinalIgnoreCase))
            {
                List<RosterDesign> ranked = RankDesigns(group);

                if (ranked.Count <= MaxDesignsPerType)
                    continue;

                List<RosterDesign> kept = ranked.Take(MaxDesignsPerType).ToList();
                List<RosterDesign> deleted = ranked.Skip(MaxDesignsPerType).ToList();
                int removed = 0;
                foreach (RosterDesign design in deleted)
                {
                    if (TryDeleteDesign(player!, design.Ship))
                        removed++;
                }

                LogPrune(player!, group.Key, kept, deleted, removed);
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"roster-prune:{ex.GetType().Name}",
                $"{LogPrefix} failed for nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static RosterDesign BuildRosterDesign(Ship design)
    {
        string type = AiDesignCompetitiveness.NormalizeShipType(design.shipType);
        EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(design);
        bool buildable = CanBuildDesign(design, out string buildReason);
        float projection = Safe(() => design.PowerProjection(true), 0f);
        GameDate effectiveDate = EffectiveDesignDate(design);
        return new RosterDesign(
            design,
            type,
            AiDesignCompetitiveness.ShipLabel(design),
            buildable,
            buildReason,
            power.AdjustedPower,
            projection,
            Safe(() => effectiveDate.turn, -1),
            StableDesignKey(design));
    }

    internal static bool ShouldConsiderDesign(Ship? design)
    {
        if (design == null || Safe(() => design.isErased, true))
            return false;

        if (!Safe(() => design.isDesign || design.isRefitDesign, false))
            return false;

        Player? owner = Safe(() => design.player, null);
        if (!IsAiPlayer(owner))
            return false;

        return !CampaignSmartRefitPatch.ShouldProtectAiRefitDesignFromDeletion(
            owner,
            design,
            out _,
            out _,
            out _);
    }

    private static bool TryDeleteDesign(Player player, Ship design)
    {
        if (CampaignSmartRefitPatch.ShouldProtectAiRefitDesignFromDeletion(
                player,
                design,
                out string protectReason,
                out int liveRefs,
                out string liveRefExamples))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: skip-delete nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))} reason={LogToken(protectReason)} liveRefs={liveRefs} liveRefExamples={LogToken(liveRefExamples)}.");
            return false;
        }

        try
        {
            CampaignController.Instance?.DeleteDesign(design);
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"delete:{StableDesignKey(design)}:{ex.GetType().Name}",
                $"{LogPrefix}: DeleteDesign failed nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))}. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (!Safe(() => design.isErased, false))
                design.Erase();
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"erase:{StableDesignKey(design)}:{ex.GetType().Name}",
                $"{LogPrefix}: Erase failed nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} design={LogToken(AiDesignCompetitiveness.ShipLabel(design))}. {ex.GetType().Name}: {ex.Message}");
        }

        return Safe(() => design.isErased, false) ||
               !SafeShipList(player.designs).Any(existing => SameDesign(existing, design));
    }

    private static bool CanBuildDesign(Ship design, out string reason)
    {
        return AiDesignBuildability.CanBuildDesign(
            Safe(() => design.player, null),
            design,
            1,
            "DesignRosterPrune",
            out reason);
    }

    private static GameDate EffectiveDesignDate(Ship design)
    {
        try
        {
            GameDate refitDate = design.dateCreatedRefit;
            if (refitDate.turn > 0)
                return refitDate;
        }
        catch
        {
        }

        return Safe(() => design.dateCreated, default);
    }

    private static void LogPrune(Player player, string type, IReadOnlyList<RosterDesign> kept, IReadOnlyList<RosterDesign> deleted, int removed)
    {
        string keptText = string.Join(",", kept.Select(FormatRosterDesign));
        string deletedText = string.Join(",", deleted.Select(design => $"{FormatRosterDesign(design)}:roster-cap"));
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} turn={LogToken(AiDesignCompetitiveness.CurrentTurnLabel())} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} type={type} kept={keptText} deleted={deletedText} removed={removed}/{deleted.Count} max={MaxDesignsPerType} source=BuildNewShips.");
    }

    internal static List<RosterDesign> RankDesigns(IEnumerable<RosterDesign> designs)
        => designs
            .OrderByDescending(design => design.Buildable)
            .ThenByDescending(design => SanitizeScore(design.AdjustedPower))
            .ThenByDescending(design => design.EffectiveTurn)
            .ThenBy(design => design.StableKey, StringComparer.Ordinal)
            .ToList();

    internal static string FormatRosterDesign(RosterDesign design)
        => $"{LogToken(design.Name)}:{ShipEffectivePowerCalculator.FormatCompactPower(design.AdjustedPower)}:{(design.Buildable ? "buildable" : "unbuildable-" + LogToken(design.BuildReason))}:pp={Fmt(design.PowerProjection)}:turn={design.EffectiveTurn}";

    internal static string FormatRosterDesignCompact(RosterDesign design)
        => $"{LogToken(design.Name)}:{ShipEffectivePowerCalculator.FormatCompactPower(design.AdjustedPower)}:{(design.Buildable ? "buildable" : "unbuildable-" + LogToken(design.BuildReason))}:turn={design.EffectiveTurn}";

    internal static string StableDesignKey(Ship? design)
    {
        string id = AiDesignCompetitiveness.ShipId(design);
        long pointer = Safe(() => design?.Pointer.ToInt64() ?? 0L, 0L);
        return $"{id}:{pointer}";
    }

    internal static bool SameDesign(Ship? left, Ship? right)
    {
        if (left == null || right == null)
            return false;

        long leftPointer = Safe(() => left.Pointer.ToInt64(), 0L);
        long rightPointer = Safe(() => right.Pointer.ToInt64(), 0L);
        if (leftPointer != 0L && leftPointer == rightPointer)
            return true;

        string leftId = AiDesignCompetitiveness.ShipId(left);
        string rightId = AiDesignCompetitiveness.ShipId(right);
        return !string.IsNullOrWhiteSpace(leftId) &&
               !string.Equals(leftId, "<null>", StringComparison.Ordinal) &&
               string.Equals(leftId, rightId, StringComparison.Ordinal);
    }

    private static bool IsAiPlayer(Player? player)
        => player != null && Safe(() => player.isAi && !player.isMain, false);

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

    private static float SanitizeScore(float value)
        => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "none";

        string normalized = reason.Trim().Trim('$');
        normalized = normalized.Replace("Ui_Constr_", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
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

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WarnOnce(string key, string message)
    {
        if (LoggedFailures.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning(message);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    internal readonly record struct RosterDesign(
        Ship Ship,
        string Type,
        string Name,
        bool Buildable,
        string BuildReason,
        float AdjustedPower,
        float PowerProjection,
        int EffectiveTurn,
        string StableKey);
}

[HarmonyPatch]
internal static class CampaignAiDesignRosterPruneBuildNewShipsPatch
{
    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "BuildNewShips", new[] { typeof(Player), typeof(float) });

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool found = TargetMethod() != null;
        if (!found)
            Melon<UADVanillaPlusMod>.Logger.Warning("AI design roster prune: BuildNewShips target not found; roster cleanup disabled.");

        return found;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player player)
        => CampaignAiDesignRosterPrunePatch.PruneBeforeBuildNewShips(player);
}
