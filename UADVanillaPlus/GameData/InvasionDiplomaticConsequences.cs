using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Ports the third-party diplomatic-consequence model from uad_save_files\Helpers\
// DiplomaticConsequences to live campaign data. Vanilla doesn't apply third-party
// attitude penalties when an invasion fires (because vanilla invasions only
// happen mid-war, where penalties are already absorbed). VP lets the player
// launch invasions in peacetime, so we need to model the global reaction.
//
// Tiered penalty model (mirrors the offline tool):
//   - allied-major          -25   (any major allied with the defender)
//   - province-direct-border -25   (the invaded province directly borders a major)
//   - country-borders-major  -10   (any of defender's provinces border a major)
//   - area-presence          -5..-15 by port-capacity sum in the same area
//   - general aggression     -5    (every remaining major, scaled by current attitude)
//
// Exemptions:
//   - attacker's own allies (attitude >= 100) take no penalty
//   - majors already at war with attacker (attitude == -100) are untouched
//   - cumulative penalties cap at attitude = -99 (prevents auto-triggering war
//     from diplomatic blowback alone; the defender war path is handled separately)
internal static class InvasionDiplomaticConsequences
{
    private const float ProvinceDirectBorderPenalty = -25f;
    private const float CountryBordersPenalty       = -10f;
    private const float AlliedMajorPenalty          = -25f;
    private const float AreaTerritoryPenalty        = -25f;
    private const float GeneralAggressionPenalty    = -5f;

    private const float AttitudeFloorForPenalties = -99f;
    private const float AttackerAllyFloor = 100f;
    private const float WarAttitude = -100f;

    // Applied penalties are jittered by ±this fraction so identical situations
    // don't always produce identical reactions. Preview values show the base
    // (pre-jitter) penalty for predictability.
    private const float JitterFraction = 0.10f;
    private static readonly System.Random Rng = new();

    internal sealed class Penalty
    {
        public Player Target { get; init; } = null!;
        public float Base { get; init; }     // ScaledByAttitude-but-not-jittered
        public string Reason { get; init; } = string.Empty;
    }

    // Computes the list of penalty entries without applying them — used to
    // preview the diplomatic blowback in the confirmation popup. Identical
    // logic to ApplyForInvasion's selection rules; only the AdjustAttitude
    // call is omitted.
    internal static List<Penalty> Preview(Player attacker, Player defender, Province province)
    {
        List<Penalty> result = new();
        var data = CampaignController.Instance?.CampaignData;
        if (data?.Players == null || data.Relations == null) return result;

        List<Player> majorPowers = new();
        foreach (Player p in data.Players)
            if (p != null && p.isMajor && p != attacker)
                majorPowers.Add(p);

        BorderAnalysis analysis = AnalyzeMajorPowerBorders(majorPowers, defender, province, attacker);

        HashSet<Player> alliedMajors = new();
        foreach (Player major in majorPowers)
        {
            Relation? rel = RelationExt.Between(data.Relations, defender, major);
            if (rel != null && rel.isAlliance)
            {
                alliedMajors.Add(major);
                AddPreviewPenalty(result, attacker, major, AlliedMajorPenalty,
                    $"allied with {defender.Name(false)}");
            }
        }

        foreach (Player major in majorPowers)
        {
            if (alliedMajors.Contains(major)) continue;
            Relation? attackerRel = RelationExt.Between(data.Relations, attacker, major);
            if (attackerRel != null && attackerRel.attitude >= AttackerAllyFloor) continue;

            if (analysis.ProvinceDirectlyBorders.Contains(major))
                AddPreviewPenalty(result, attacker, major, ProvinceDirectBorderPenalty, "borders invaded province");
            else if (analysis.CountryBorders.Contains(major))
                AddPreviewPenalty(result, attacker, major, CountryBordersPenalty, $"borders {defender.Name(false)}");
            else if (analysis.AreaTerritory.Contains(major))
                AddPreviewPenalty(result, attacker, major, AreaTerritoryPenalty, "holds territory in invaded area");
            else
                AddPreviewPenalty(result, attacker, major, GeneralAggressionPenalty, "general aggression");
        }
        return result;
    }

    private static void AddPreviewPenalty(List<Penalty> list, Player attacker, Player target, float basePenalty, string reason)
    {
        var relations = CampaignController.Instance?.CampaignData?.Relations;
        if (relations == null) return;
        Relation? rel = RelationExt.Between(relations, attacker, target);
        if (rel == null) return;
        if (rel.attitude <= WarAttitude) return;  // already at war, would be skipped

        float scaled = ScalePenaltyByCurrentAttitude(basePenalty, rel.attitude);
        if (scaled == 0f) return;

        float target_ = Math.Max(AttitudeFloorForPenalties, rel.attitude + scaled);
        float delta = target_ - rel.attitude;
        if (delta >= 0f) return;

        list.Add(new Penalty { Target = target, Base = delta, Reason = reason });
    }

    internal static void ApplyForInvasion(Player attacker, Player defender, Province province)
    {
        var data = CampaignController.Instance?.CampaignData;
        if (data?.Players == null || data.Relations == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP invasion-consequences: campaign data unavailable.");
            return;
        }

        // Use vanilla's curated major-powers list when available; fall back
        // to filtering Players by isMajor in case PlayersMajor is empty.
        List<Player> majorPowers = new();
        if (data.PlayersMajor != null && data.PlayersMajor.Count > 0)
        {
            foreach (Player p in data.PlayersMajor)
                if (p != null && p != attacker) majorPowers.Add(p);
        }
        else
        {
            foreach (Player p in data.Players)
                if (p != null && p.isMajor && p != attacker) majorPowers.Add(p);
        }

        BorderAnalysis analysis = AnalyzeMajorPowerBorders(majorPowers, defender, province, attacker);

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP invasion-consequences: begin attacker={attacker.Name(false)} defender={defender.Name(false)} " +
            $"province={province.Id} majors={majorPowers.Count} ({string.Join("/", majorPowers.Select(m => m.Name(false)))}) " +
            $"directBorders={JoinMajors(analysis.ProvinceDirectlyBorders)} " +
            $"countryBorders={JoinMajors(analysis.CountryBorders)} " +
            $"areaTerritory={JoinMajors(analysis.AreaTerritory)}.");

        HashSet<Player> alliedMajors = new();
        foreach (Player major in majorPowers)
        {
            Relation? rel = RelationExt.Between(data.Relations, defender, major);
            if (rel == null) continue;
            if (rel.isAlliance) alliedMajors.Add(major);
        }

        // Per-major outcome log: every major considered + how they were
        // handled (category, applied delta, or skipped reason). Lets the
        // user verify completeness and trace stacking across multiple
        // invasions in the same turn.
        List<string> outcomes = new();

        foreach (Player major in majorPowers)
        {
            // Read attitude before applying so we can log the actual delta.
            Relation? rel = RelationExt.Between(data.Relations, attacker, major);
            float beforeAttitude = rel?.attitude ?? float.NaN;

            string outcome;
            if (rel == null)
            {
                outcome = $"{major.Name(false)}: SKIP (no relation)";
            }
            else if (rel.isWar)
            {
                outcome = $"{major.Name(false)}: SKIP (already at war)";
            }
            else if (rel.attitude >= AttackerAllyFloor)
            {
                outcome = $"{major.Name(false)}: SKIP (allied with attacker)";
            }
            else if (alliedMajors.Contains(major))
            {
                float applied = TryApplyPenalty(attacker, major, AlliedMajorPenalty, null);
                outcome = $"{major.Name(false)}: ALLIED-OF-DEFENDER {beforeAttitude:0.0}→{rel.attitude:0.0} ({applied:+0.0;-0.0;0})";
            }
            else if (analysis.ProvinceDirectlyBorders.Contains(major))
            {
                float applied = TryApplyPenalty(attacker, major, ProvinceDirectBorderPenalty, null);
                outcome = $"{major.Name(false)}: DIRECT-BORDER {beforeAttitude:0.0}→{rel.attitude:0.0} ({applied:+0.0;-0.0;0})";
            }
            else if (analysis.CountryBorders.Contains(major))
            {
                float applied = TryApplyPenalty(attacker, major, CountryBordersPenalty, null);
                outcome = $"{major.Name(false)}: COUNTRY-BORDER {beforeAttitude:0.0}→{rel.attitude:0.0} ({applied:+0.0;-0.0;0})";
            }
            else if (analysis.AreaTerritory.Contains(major))
            {
                float applied = TryApplyPenalty(attacker, major, AreaTerritoryPenalty, null);
                outcome = $"{major.Name(false)}: AREA-TERRITORY {beforeAttitude:0.0}→{rel.attitude:0.0} ({applied:+0.0;-0.0;0})";
            }
            else
            {
                float applied = TryApplyPenalty(attacker, major, GeneralAggressionPenalty, null);
                outcome = applied == 0f
                    ? $"{major.Name(false)}: GENERAL-AT-FLOOR {beforeAttitude:0.0} (no change)"
                    : $"{major.Name(false)}: GENERAL {beforeAttitude:0.0}→{rel.attitude:0.0} ({applied:+0.0;-0.0;0})";
            }
            outcomes.Add(outcome);
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP invasion-consequences: per-major outcomes vs {defender.Name(false)}:\n  • " +
            string.Join("\n  • ", outcomes));

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP invasion-consequences: complete attacker={attacker.Name(false)} defender={defender.Name(false)}.");
    }

    private static float TryApplyPenalty(Player attacker, Player target, float basePenalty, string? reason)
    {
        var relations = CampaignController.Instance?.CampaignData?.Relations;
        if (relations == null) return 0f;

        Relation? rel = RelationExt.Between(relations, attacker, target);
        if (rel == null) return 0f;
        if (rel.attitude <= WarAttitude) return 0f;  // already at war, skip

        float scaled = ScalePenaltyByCurrentAttitude(basePenalty, rel.attitude);
        if (scaled == 0f) return 0f;

        // ±10% jitter so the actual reaction varies trip-to-trip even with
        // identical inputs. Multiplier sampled uniformly in [1-J, 1+J].
        float jitter = 1f + (((float)Rng.NextDouble() * 2f - 1f) * JitterFraction);
        scaled *= jitter;

        float before = rel.attitude;
        float floorClampedTarget = Math.Max(AttitudeFloorForPenalties, before + scaled);
        float clampedDelta = floorClampedTarget - before;
        if (clampedDelta >= 0f) return 0f;  // already at or below the diplomatic floor

        CampaignController? controller = CampaignController.Instance;
        if (controller == null) return 0f;

        try
        {
            controller.AdjustAttitude(
                rel,
                clampedDelta,
                true,        // canFullyAdjust
                false,       // init
                $"UADVP invasion consequence ({reason ?? "general aggression"})",
                true,        // raiseEvents
                true,        // force
                false);      // fromCommonEnemy
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP invasion-consequences: AdjustAttitude failed for {target.Name(false)}. {ex.GetType().Name}: {ex.Message}");
            return 0f;
        }

        if (reason != null)
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP invasion-consequences: {target.Name(false)} attitude {before:0.0}→{rel.attitude:0.0} ({clampedDelta:+0.0;-0.0;0}) — {reason}");

        return clampedDelta;
    }

    private static float ScalePenaltyByCurrentAttitude(float basePenalty, float currentAttitude)
    {
        // Mirrors the offline tool: as relations worsen, marginal penalty drops.
        if (currentAttitude >= 0f) return basePenalty;
        float scale = currentAttitude switch
        {
            >= -10f => 0.8f,
            >= -20f => 0.6f,
            >= -30f => 0.4f,
            _       => 0.2f,
        };
        return basePenalty * scale;
    }

    private static BorderAnalysis AnalyzeMajorPowerBorders(
        List<Player> majorPowers,
        Player defender,
        Province invadedProvince,
        Player attacker)
    {
        BorderAnalysis analysis = new();
        string invadedAreaId = invadedProvince.AreaId;

        // Area-territory: any major (not attacker/defender) that holds at
        // least one province in the invaded area takes the AreaTerritory
        // penalty. Port capacity no longer matters — just "do they have a
        // territorial stake in this region?". Matches the user's request
        // that an invasion in e.g. the Labrador Sea pulls a -25 from any
        // major with territory in Labrador Sea.
        if (!string.IsNullOrEmpty(invadedAreaId))
        {
            foreach (Player major in majorPowers)
            {
                if (major == defender) continue;
                Il2CppSystem.Collections.Generic.List<Province>? majorProvs = major.provinces;
                if (majorProvs == null) continue;
                foreach (Province p in majorProvs)
                {
                    if (p == null || p.AreaId != invadedAreaId) continue;
                    analysis.AreaTerritory.Add(major);
                    break;
                }
            }
        }

        // Direct neighbours of the invaded province.
        Il2CppSystem.Collections.Generic.List<Province>? directNeighbours = invadedProvince.NeighbourProvinces;
        if (directNeighbours != null)
        {
            foreach (Province n in directNeighbours)
            {
                Player? controller = n?.ControllerPlayer;
                if (controller == null || !controller.isMajor) continue;
                if (controller == attacker || controller == defender) continue;
                if (majorPowers.Contains(controller))
                    analysis.ProvinceDirectlyBorders.Add(controller);
            }
        }

        // Country-borders: walk every defender-controlled province's neighbours.
        Il2CppSystem.Collections.Generic.List<Province>? defenderProvs = defender.provinces;
        if (defenderProvs != null)
        {
            foreach (Province p in defenderProvs)
            {
                Il2CppSystem.Collections.Generic.List<Province>? neighbours = p?.NeighbourProvinces;
                if (neighbours == null) continue;
                foreach (Province n in neighbours)
                {
                    Player? controller = n?.ControllerPlayer;
                    if (controller == null || !controller.isMajor) continue;
                    if (controller == attacker || controller == defender) continue;
                    if (majorPowers.Contains(controller))
                        analysis.CountryBorders.Add(controller);
                }
            }
        }

        return analysis;
    }

    private static string JoinMajors(HashSet<Player> set)
    {
        if (set.Count == 0) return "<none>";
        return string.Join(",", set.Select(p => p.Name(false)));
    }

    private sealed class BorderAnalysis
    {
        internal HashSet<Player> ProvinceDirectlyBorders { get; } = new();
        internal HashSet<Player> CountryBorders { get; } = new();
        internal HashSet<Player> AreaTerritory { get; } = new();
    }
}
