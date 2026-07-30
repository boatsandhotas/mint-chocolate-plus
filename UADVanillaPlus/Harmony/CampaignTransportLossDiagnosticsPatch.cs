using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Temporary diagnostic: attribute abstract transport losses to the campaign
// area and nearby war context without changing the loss calculation.
[HarmonyPatch]
internal static class CampaignTransportLossDiagnosticsPatch
{
    private const string LogPrefix = "UADVP TR sea loss diag";
    private const string SnapshotStage = "SubmarineBattles";
    private const string LossStage = "TransportLoses";
    private const int MaxEnemyDetails = 6;

    private static readonly LossDictionaryAccessor TrLossesPerTurn =
        new("trLosesPerTurn");

    private static readonly LossDictionaryAccessor TrLossesByIgnorePerTurn =
        new("trLosesByIgnorePerTurn");

    private static readonly FieldInfo? ShipCurrentRoleField =
        typeof(Ship).GetField("CurrentRole", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? SubmarineCurrentRoleField =
        typeof(Submarine).GetField("CurrentRole", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static Dictionary<string, LossEntry> beforeLosses = new(StringComparer.Ordinal);
    private static Dictionary<string, LossEntry> beforeIgnoredLosses = new(StringComparer.Ordinal);
    private static bool haveSnapshot;
    private static bool loggedActive;
    private static bool loggedFirstMeasureTimeMessage;
    private static bool loggedMissingField;
    private static bool loggedMissingBaseline;
    private static string lastFailure = string.Empty;

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "MeasureTime", new[] { typeof(string) });

    private static MethodBase? ReportTrLosesTargetMethod()
        => AccessTools.Method(typeof(Ui), nameof(Ui.ReportTrLoses)) ??
           typeof(Ui).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               .FirstOrDefault(static method => string.Equals(method.Name, nameof(Ui.ReportTrLoses), StringComparison.Ordinal));

    [HarmonyPrepare]
    private static bool Prepare()
    {
        MethodBase? target = TargetMethod();
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} proof: targetMethod={(target != null ? "yes" : "no")} " +
            $"trLosesPerTurn={TrLossesPerTurn.AccessorKind} " +
            $"trLosesByIgnorePerTurn={TrLossesByIgnorePerTurn.AccessorKind}.");

        bool available = target != null && TrLossesPerTurn.Available;
        if (!available)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix} unavailable: CampaignController.MeasureTime or trLosesPerTurn accessor not found.");
        }

        return available;
    }

    [HarmonyPostfix]
    private static void MeasureTimePostfix(CampaignController __instance, string msg)
    {
        try
        {
            if (__instance == null)
                return;

            LogFirstMeasureTimeMessage(msg);

            if (string.IsNullOrWhiteSpace(msg))
                return;

            if (string.Equals(msg, SnapshotStage, StringComparison.Ordinal))
            {
                beforeLosses = SnapshotLosses(ReadLossDictionary(__instance, TrLossesPerTurn), "regular");
                beforeIgnoredLosses = SnapshotLosses(ReadLossDictionary(__instance, TrLossesByIgnorePerTurn), "ignored");
                haveSnapshot = true;
                return;
            }

            if (!string.Equals(msg, LossStage, StringComparison.Ordinal))
                return;

            LogActiveOnce();

            Dictionary<string, LossEntry> afterLosses = SnapshotLosses(ReadLossDictionary(__instance, TrLossesPerTurn), "regular");
            Dictionary<string, LossEntry> afterIgnoredLosses = SnapshotLosses(ReadLossDictionary(__instance, TrLossesByIgnorePerTurn), "ignored");
            if (!haveSnapshot)
            {
                beforeLosses = afterLosses;
                beforeIgnoredLosses = afterIgnoredLosses;
                haveSnapshot = true;
                LogMissingBaselineOnce();
                return;
            }

            LogPositiveDeltas(__instance, "regular", beforeLosses, afterLosses);
            LogPositiveDeltas(__instance, "ignored", beforeIgnoredLosses, afterIgnoredLosses);

            beforeLosses = afterLosses;
            beforeIgnoredLosses = afterIgnoredLosses;
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
        }
    }

    private static object? ReadLossDictionary(CampaignController campaign, LossDictionaryAccessor accessor)
    {
        if (!accessor.Available)
        {
            LogMissingFieldOnce(accessor.Name);
            return null;
        }

        try
        {
            return accessor.GetValue(campaign);
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
            return null;
        }
    }

    private static Dictionary<string, LossEntry> SnapshotLosses(object? rawLosses, string source)
    {
        Dictionary<string, LossEntry> result = new(StringComparer.Ordinal);
        if (rawLosses == null)
            return result;

        if (rawLosses is Il2CppSystem.Collections.Generic.Dictionary<Player, Il2CppSystem.Collections.Generic.Dictionary<string, int>> il2CppLosses)
        {
            foreach (var playerEntry in il2CppLosses)
            {
                Player? player = playerEntry.Key;
                var regionLosses = playerEntry.Value;
                if (player == null || regionLosses == null)
                    continue;

                foreach (var regionEntry in regionLosses)
                    AddLossEntry(result, player, regionEntry.Key, regionEntry.Value, source);
            }

            return result;
        }

        if (rawLosses is Dictionary<Player, Dictionary<string, int>> managedLosses)
        {
            foreach (KeyValuePair<Player, Dictionary<string, int>> playerEntry in managedLosses)
            {
                Player? player = playerEntry.Key;
                Dictionary<string, int>? regionLosses = playerEntry.Value;
                if (player == null || regionLosses == null)
                    continue;

                foreach (KeyValuePair<string, int> regionEntry in regionLosses)
                    AddLossEntry(result, player, regionEntry.Key, regionEntry.Value, source);
            }

            return result;
        }

        LogFailureOnce(new InvalidOperationException($"unexpected loss dictionary type {rawLosses.GetType().FullName}"));
        return result;
    }

    private static void AddLossEntry(Dictionary<string, LossEntry> result, Player player, string? region, int count, string source)
    {
        string regionText = string.IsNullOrWhiteSpace(region) ? "<empty>" : region.Trim();
        string key = LossKey(player, regionText);
        result[key] = new LossEntry(player, regionText, count, source);
    }

    private static void LogPositiveDeltas(
        CampaignController campaign,
        string source,
        Dictionary<string, LossEntry> before,
        Dictionary<string, LossEntry> after)
    {
        foreach (KeyValuePair<string, LossEntry> entry in after)
        {
            before.TryGetValue(entry.Key, out LossEntry previous);
            int delta = entry.Value.Count - previous.Count;
            if (delta <= 0)
                continue;

            LogLossContext(campaign, entry.Value, delta, source);
        }
    }

    private static void LogLossContext(CampaignController campaign, LossEntry loss, int delta, string source)
    {
        Player? victim = loss.Player;
        Area? area = ResolveArea(campaign, loss.Region);
        AreaFleetSummary own = area != null && victim != null
            ? SummarizeFleet(campaign, area, victim)
            : AreaFleetSummary.Unresolved;
        List<EnemyAreaSummary> enemies = area != null && victim != null
            ? EnemySummaries(campaign, area, victim)
            : new List<EnemyAreaSummary>();

        EnemyAreaSummary? strongestRaid = enemies
            .OrderByDescending(static enemy => enemy.Fleet.Raid)
            .ThenByDescending(static enemy => enemy.Fleet.PowerProjection)
            .FirstOrDefault();
        EnemyAreaSummary? strongestPower = enemies
            .OrderByDescending(static enemy => enemy.Fleet.PowerProjection)
            .ThenByDescending(static enemy => enemy.Fleet.Raid)
            .FirstOrDefault();
        AreaFleetSummary enemyAggregate = AggregateEnemyFleet(enemies);

        string enemyDetails = enemies.Count == 0
            ? "none"
            : string.Join("|", enemies.Take(MaxEnemyDetails).Select(FormatEnemyDetail));
        if (enemies.Count > MaxEnemyDetails)
            enemyDetails += $"+{enemies.Count - MaxEnemyDetails}";

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: turn={LogToken(AiDesignCompetitiveness.CurrentTurnLabel())} source={LogToken(source)} " +
            $"victim={LogToken(PlayerLabel(victim))} region={Quote(loss.Region)} losses={delta} delta={delta} total={loss.Count} " +
            $"trCap={FormatPercent(TransportCapacityPercent(victim))} area={LogToken(AreaLabel(area))} " +
            $"victimAllShips={own.AllShips} victimReachedDestShips={own.ReachedDestShips} victimReadyShips={own.ReadyShips} " +
            $"victimPP={Fmt(own.PowerProjection)} victimEscortAvg={Fmt(own.Escort)} victimRaidAvg={Fmt(own.Raid)} " +
            $"victimTF={LogToken(own.TaskForces.LogText)} enemies={enemies.Count} enemyAllShips={enemyAggregate.AllShips} " +
            $"enemyReachedDestShips={enemyAggregate.ReachedDestShips} enemyReadyShips={enemyAggregate.ReadyShips} " +
            $"enemyPP={Fmt(enemyAggregate.PowerProjection)} enemyEscortAvg={Fmt(enemyAggregate.Escort)} enemyRaidAvg={Fmt(enemyAggregate.Raid)} " +
            $"enemyTF={LogToken(enemyAggregate.TaskForces.LogText)} own={own.LogText} strongestRaid={FormatEnemy(strongestRaid, "raid")} " +
            $"strongestPower={FormatEnemy(strongestPower, "pp")} enemyDetails={LogToken(enemyDetails)} " +
            $"exposure={LogToken(PortExposure(area, victim))}.");
    }

    private static Area? ResolveArea(CampaignController campaign, string region)
    {
        string normalized = NormalizeAreaName(region);
        if (string.IsNullOrEmpty(normalized))
            return null;

        foreach (Area area in KnownAreas(campaign))
        {
            if (MatchesArea(area, normalized))
                return area;
        }

        return null;
    }

    private static bool MatchesArea(Area area, string normalized)
    {
        foreach (string candidate in AreaNameCandidates(area))
        {
            if (string.Equals(NormalizeAreaName(candidate), normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> AreaNameCandidates(Area? area)
    {
        if (area == null)
            yield break;

        string id = SafeString(() => area.Id);
        if (!string.IsNullOrWhiteSpace(id))
            yield return id;

        string name = SafeString(() => area.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;

            string localized = SafeString(() => LocalizeManager.Localize(name));
            if (!string.IsNullOrWhiteSpace(localized))
                yield return localized;
        }
    }

    private static IEnumerable<Area> KnownAreas(CampaignController campaign)
    {
        List<Area> areas = new();

        void AddArea(Area? area)
        {
            if (area == null || areas.Any(existing => existing == area))
                return;

            areas.Add(area);
        }

        var data = Safe(() => campaign.CampaignData, null);

        var vesselsInArea = Safe(() => data?.VesselsInArea, null);
        if (vesselsInArea != null)
        {
            foreach (var entry in vesselsInArea)
                AddArea(entry.Key);
        }

        var vesselsByAreaAndPlayer = Safe(() => data?.VesselsInAreaOfPlayer, null);
        if (vesselsByAreaAndPlayer != null)
        {
            foreach (var entry in vesselsByAreaAndPlayer)
                AddArea(entry.Key);
        }

        var provincesByPlayer = Safe(() => data?.ProvincesByPlayer, null);
        if (provincesByPlayer != null)
        {
            foreach (var playerEntry in provincesByPlayer)
            {
                var provinces = playerEntry.Value;
                if (provinces == null)
                    continue;

                foreach (Province province in provinces)
                    AddArea(Safe(() => province.CurrentArea, null));
            }
        }

        return areas;
    }

    private static AreaFleetSummary SummarizeFleet(CampaignController campaign, Area area, Player player)
    {
        List<VesselEntity> vessels = VesselsInArea(campaign, area, player);
        List<VesselEntity> reachedDestVessels = VesselsInAreaReachedDestination(campaign, area, player);
        List<VesselEntity> readyVessels = VesselsInAreaReadyForBattle(campaign, area, player);
        return new AreaFleetSummary(
            vessels.Count,
            reachedDestVessels.Count,
            readyVessels.Count,
            Safe(() => campaign.AreaCurrentTonnage(area, player), 0f),
            Safe(() => campaign.PowerProjectionInAreaForPlayer(area, player), 0f),
            Safe(() => campaign.EscortPowerAvgInAreaForPlayer(area, player), 0f),
            Safe(() => campaign.RaidPowerAvgInAreaForPlayer(area, player), 0f),
            RoleCounts(vessels),
            TaskForceMovementSummary(campaign, player, vessels));
    }

    private static List<VesselEntity> VesselsInArea(CampaignController campaign, Area area, Player player)
    {
        List<VesselEntity> result = new();
        var vessels = Safe(() => campaign.VesselsInAreaOfPlayer(area, player, false), null);
        CopyVessels(vessels, result);
        return result;
    }

    private static List<VesselEntity> VesselsInAreaReachedDestination(CampaignController campaign, Area area, Player player)
    {
        List<VesselEntity> result = new();
        var vessels = Safe(() => campaign.VesselsInAreaOfPlayerDestenationReached(area, player), null);
        CopyVessels(vessels, result);
        return result;
    }

    private static List<VesselEntity> VesselsInAreaReadyForBattle(CampaignController campaign, Area area, Player player)
    {
        List<VesselEntity> result = new();
        var vessels = Safe(() => campaign.VesselsInAreaOfPlayerReadyForBattle(area, player), null);
        CopyVessels(vessels, result);
        return result;
    }

    private static void CopyVessels(Il2CppSystem.Collections.Generic.List<VesselEntity>? vessels, List<VesselEntity> result)
    {
        if (vessels == null)
            return;

        foreach (VesselEntity vessel in vessels)
        {
            if (vessel != null)
                result.Add(vessel);
        }
    }

    private static List<EnemyAreaSummary> EnemySummaries(CampaignController campaign, Area area, Player victim)
    {
        List<EnemyAreaSummary> enemies = new();
        foreach (Player enemy in WarEnemies(campaign, victim))
        {
            if (enemy == null || SamePlayer(enemy, victim))
                continue;

            enemies.Add(new EnemyAreaSummary(enemy, SummarizeFleet(campaign, area, enemy)));
        }

        return enemies;
    }

    private static IEnumerable<Player> WarEnemies(CampaignController campaign, Player victim)
    {
        List<Player> enemies = new();

        var relations = Safe(() => campaign.CampaignData?.Relations, null);
        if (relations != null)
        {
            foreach (var entry in relations)
            {
                Relation? relation = entry.Value;
                if (relation == null || !relation.isWar)
                    continue;

                if (SamePlayer(relation.a, victim))
                    AddEnemy(relation.b);
                else if (SamePlayer(relation.b, victim))
                    AddEnemy(relation.a);
            }
        }
        else
        {
            var players = Safe(() => campaign.CampaignData?.PlayersMajor, null);
            if (players != null)
            {
                foreach (Player player in players)
                {
                    if (player != null && !SamePlayer(player, victim) && IsAtWarWith(victim, player))
                        AddEnemy(player);
                }
            }
        }

        return enemies;

        void AddEnemy(Player? enemy)
        {
            if (enemy == null || enemies.Any(existing => SamePlayer(existing, enemy)))
                return;

            enemies.Add(enemy);
        }
    }

    private static AreaFleetSummary AggregateEnemyFleet(List<EnemyAreaSummary> enemies)
    {
        if (enemies.Count == 0)
            return AreaFleetSummary.Unresolved;

        return new AreaFleetSummary(
            enemies.Sum(static enemy => enemy.Fleet.AllShips),
            enemies.Sum(static enemy => enemy.Fleet.ReachedDestShips),
            enemies.Sum(static enemy => enemy.Fleet.ReadyShips),
            enemies.Sum(static enemy => enemy.Fleet.Tons),
            enemies.Sum(static enemy => enemy.Fleet.PowerProjection),
            enemies.Count == 0 ? 0f : enemies.Max(static enemy => enemy.Fleet.Escort),
            enemies.Count == 0 ? 0f : enemies.Max(static enemy => enemy.Fleet.Raid),
            "aggregate",
            new TaskForceSummary(
                enemies.Sum(static enemy => enemy.Fleet.TaskForces.Moving),
                enemies.Sum(static enemy => enemy.Fleet.TaskForces.Total),
                enemies.Sum(static enemy => enemy.Fleet.TaskForces.WasMove),
                enemies.Sum(static enemy => enemy.Fleet.TaskForces.ToBattle),
                enemies.Sum(static enemy => enemy.Fleet.TaskForces.FromBattle),
                "aggregate"));
    }

    private static TaskForceSummary TaskForceMovementSummary(CampaignController campaign, Player player, List<VesselEntity> areaVessels)
    {
        var taskForces = Safe(() => campaign.CampaignData?.TaskForces, null);
        if (taskForces == null || areaVessels.Count == 0)
            return TaskForceSummary.Empty;

        HashSet<IntPtr> areaVesselIds = areaVessels
            .Where(static vessel => vessel != null)
            .Select(static vessel => vessel.Pointer)
            .ToHashSet();

        int total = 0;
        int moving = 0;
        int wasMove = 0;
        int toBattle = 0;
        int fromBattle = 0;
        List<string> examples = new();

        foreach (CampaignController.TaskForce group in taskForces)
        {
            if (group == null || !SamePlayer(Safe(() => group.Controller, null), player))
                continue;

            if (!TaskForceHasAreaVessel(group, areaVesselIds))
                continue;

            total++;
            bool isMoving = IsTaskForceMoving(group);
            if (isMoving)
                moving++;
            if (Safe(() => group.WasMove, false))
                wasMove++;
            if (Safe(() => group.GroupMovingToBattle, false))
                toBattle++;
            if (Safe(() => group.GroupMovingFromBattle, false))
                fromBattle++;

            if (examples.Count < 3)
                examples.Add(TaskForceMovementDetail(group, isMoving));
        }

        return new TaskForceSummary(moving, total, wasMove, toBattle, fromBattle, examples.Count == 0 ? "none" : string.Join(",", examples));
    }

    private static bool TaskForceHasAreaVessel(CampaignController.TaskForce group, HashSet<IntPtr> areaVesselIds)
    {
        var vessels = Safe(() => group.Vessels, null);
        if (vessels == null)
            return false;

        foreach (VesselEntity vessel in vessels)
        {
            if (vessel != null && areaVesselIds.Contains(vessel.Pointer))
                return true;
        }

        return false;
    }

    private static bool IsTaskForceMoving(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        if (moving)
            return true;

        return Safe(() => group.Path != null && group.CurrentPositionIndex < group.Path.Length - 1, false);
    }

    private static string TaskForceMovementDetail(CampaignController.TaskForce group, bool isMoving)
    {
        int pathLength = Safe(() => group.Path?.Length ?? 0, 0);
        int index = Safe(() => group.CurrentPositionIndex, -1);
        string from = LogToken(SafeString(() => group.From?.name));
        string to = LogToken(SafeString(() => group.To?.name));
        return $"moving={isMoving}:idx={index}/{pathLength}:from={from}:to={to}:wasMove={Safe(() => group.WasMove, false)}:toBattle={Safe(() => group.GroupMovingToBattle, false)}:fromBattle={Safe(() => group.GroupMovingFromBattle, false)}";
    }

    private static bool IsAtWarWith(Player player, Player controller)
    {
        var relations = Safe(() => CampaignController.Instance?.CampaignData?.Relations, null);
        if (relations == null)
            return false;

        foreach (var entry in relations)
        {
            Relation? relation = entry.Value;
            if (relation == null || !relation.isWar)
                continue;

            if ((SamePlayer(relation.a, player) && SamePlayer(relation.b, controller)) ||
                (SamePlayer(relation.a, controller) && SamePlayer(relation.b, player)))
            {
                return true;
            }
        }

        return false;
    }

    private static string RoleCounts(IEnumerable<VesselEntity> vessels)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["IB"] = 0,
            ["SC"] = 0,
            ["LIM"] = 0,
            ["INV"] = 0,
            ["PRO"] = 0,
            ["DEF"] = 0,
            ["SUB"] = 0
        };

        foreach (VesselEntity vessel in vessels)
        {
            string label = RoleLabel(vessel);
            counts.TryGetValue(label, out int current);
            counts[label] = current + 1;
        }

        return string.Join(",", counts.Select(entry => $"{entry.Key}:{entry.Value}"));
    }

    private static string RoleLabel(VesselEntity? vessel)
    {
        if (vessel == null)
            return "UNK";

        Ship? ship = Safe(() => vessel.TryCast<Ship>(), null);
        if (ship != null)
            return ShipRoleLabel(SafeString(() => ShipCurrentRoleField?.GetValue(ship)?.ToString()));

        Submarine? submarine = Safe(() => vessel.TryCast<Submarine>(), null);
        if (submarine != null)
            return "SUB";

        string role = SafeString(() => SubmarineCurrentRoleField?.GetValue(vessel)?.ToString());
        return string.IsNullOrWhiteSpace(role) ? "UNK" : role;
    }

    private static string ShipRoleLabel(string role)
        => role switch
        {
            "InBeing" => "IB",
            "SeaControl" => "SC",
            "Limited" => "LIM",
            "Invade" => "INV",
            "Protect" => "PRO",
            "Defend" => "DEF",
            _ => string.IsNullOrWhiteSpace(role) ? "UNK" : role
        };

    private static string PortExposure(Area? area, Player? player)
    {
        if (area == null || player == null)
            return "unresolved";

        try
        {
            var provinces = area.Provinces;
            if (provinces == null)
                return "unresolved";

            int ports = 0;
            float development = 0f;
            foreach (Province province in provinces)
            {
                if (province == null || !Safe(() => province.HavePort, false))
                    continue;

                Player? controller = Safe(() => province.ControllerPlayer, null);
                if (!SamePlayer(controller, player))
                    continue;

                ports++;
                development += Safe(() => province.Development, 0f);
            }

            float factor = Safe(() => CampaignController.Param("tr_loss_development", 0f), 0f);
            return $"ports={ports}:dev={Fmt(development)}:factor={Fmt(factor)}:value={Fmt(development * factor)}";
        }
        catch
        {
            return "unresolved";
        }
    }

    private static string FormatEnemy(EnemyAreaSummary? enemy, string metric)
    {
        if (enemy == null)
            return "none";

        string value = string.Equals(metric, "raid", StringComparison.Ordinal)
            ? Fmt(enemy.Fleet.Raid)
            : Fmt(enemy.Fleet.PowerProjection);

        return $"{LogToken(PlayerLabel(enemy.Player))}:{metric}={value}:ships={enemy.Fleet.AllShips}:tons={Fmt(enemy.Fleet.Tons)}";
    }

    private static string FormatEnemyDetail(EnemyAreaSummary enemy)
        => $"{LogToken(PlayerLabel(enemy.Player))}:enemyAllShips={enemy.Fleet.AllShips}:enemyReachedDestShips={enemy.Fleet.ReachedDestShips}:" +
           $"enemyReadyShips={enemy.Fleet.ReadyShips}:tons={Fmt(enemy.Fleet.Tons)}:enemyPP={Fmt(enemy.Fleet.PowerProjection)}:" +
           $"enemyEscortAvg={Fmt(enemy.Fleet.Escort)}:enemyRaidAvg={Fmt(enemy.Fleet.Raid)}:enemyTF={enemy.Fleet.TaskForces.LogText}:roles={enemy.Fleet.Roles}";

    private static string AreaLabel(Area? area)
    {
        if (area == null)
            return "unresolved";

        string name = SafeString(() => area.Name);
        string id = SafeString(() => area.Id);
        if (string.IsNullOrWhiteSpace(name))
            return string.IsNullOrWhiteSpace(id) ? "unresolved" : id;

        return string.IsNullOrWhiteSpace(id) || string.Equals(name, id, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}({id})";
    }

    private static string LossKey(Player? player, string region)
        => $"{PlayerIdentity(player)}|{NormalizeAreaName(region)}";

    private static string PlayerIdentity(Player? player)
    {
        string dataName = SafeString(() => player?.data?.name);
        if (!string.IsNullOrWhiteSpace(dataName))
            return dataName;

        string label = PlayerLabel(player);
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        return SafeString(() => player?.Pointer.ToString()) ?? "<null>";
    }

    private static string PlayerLabel(Player? player)
    {
        string label = AiDesignCompetitiveness.PlayerLabel(player);
        return string.IsNullOrWhiteSpace(label) ? "<unknown>" : label;
    }

    private static bool SamePlayer(Player? a, Player? b)
    {
        if (a == null || b == null)
            return false;

        return a == b || (a.data != null && b.data != null && a.data == b.data);
    }

    private static float TransportCapacityPercent(Player? player)
    {
        if (player == null)
            return 0f;

        float capacity = Safe(() => player.transportCapacity, 0f);
        return capacity <= 3f ? capacity * 100f : capacity;
    }

    private static string NormalizeAreaName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace("\r", " ").Replace("\n", " ").ToUpperInvariant();
    }

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPercent(float value)
        => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Quote(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";

    private static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        return value.Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace(" ", "_");
    }

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

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} active: logging TransportLoses deltas; losses={TrLossesPerTurn.AccessorKind} ignored={TrLossesByIgnorePerTurn.AccessorKind}.");
    }

    private static void LogFirstMeasureTimeMessage(string? msg)
    {
        if (loggedFirstMeasureTimeMessage)
            return;

        loggedFirstMeasureTimeMessage = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} first MeasureTime msg={LogToken(msg)}.");
    }

    private static void LogMissingFieldOnce(string name)
    {
        if (loggedMissingField)
            return;

        loggedMissingField = true;
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} unavailable: transport-loss accessor {name} missing.");
    }

    private static void LogMissingBaselineOnce()
    {
        if (loggedMissingBaseline)
            return;

        loggedMissingBaseline = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: no SubmarineBattles baseline yet; seeded baseline at TransportLoses.");
    }

    private static void LogFailureOnce(Exception ex)
    {
        string failure = $"{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, failure, StringComparison.Ordinal))
            return;

        lastFailure = failure;
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} failed; leaving campaign flow unchanged. {ex.GetType().Name}: {ex.Message}");
    }

    private static void LogReportLosses(string source, object? rawLosses)
    {
        CampaignController? campaign = Safe(() => CampaignController.Instance, null);
        if (campaign == null || rawLosses == null)
            return;

        Dictionary<string, LossEntry> losses = SnapshotLosses(rawLosses, source);
        foreach (LossEntry loss in losses.Values)
        {
            if (loss.Count <= 0)
                continue;

            LogLossContext(campaign, loss, loss.Count, source);
        }
    }

    [HarmonyPatch]
    private static class ReportTrLosesPatch
    {
        private static MethodBase? TargetMethod()
            => ReportTrLosesTargetMethod();

        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodBase? target = TargetMethod();
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix} proof: ReportTrLoses target={(target != null ? "yes" : "no")}.");
            return target != null;
        }

        [HarmonyPrefix]
        private static void Prefix(object[] __args)
        {
            try
            {
                object? seaLosses = __args.Length > 0 ? __args[0] : null;
                object? missionLosses = __args.Length > 1 ? __args[1] : null;
                LogReportLosses("sea", seaLosses);
                LogReportLosses("mission", missionLosses);
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex);
            }
        }
    }

    private readonly record struct LossEntry(Player? Player, string Region, int Count, string Source);

    private sealed class LossDictionaryAccessor
    {
        private const BindingFlags InstanceAccess = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly PropertyInfo? property;
        private readonly MethodInfo? getter;
        private readonly FieldInfo? field;

        internal LossDictionaryAccessor(string name)
        {
            Name = name;
            property = typeof(CampaignController).GetProperty(name, InstanceAccess);
            getter = typeof(CampaignController).GetMethod("get_" + name, InstanceAccess, null, Type.EmptyTypes, null);
            field = typeof(CampaignController).GetField(name, InstanceAccess);
        }

        internal string Name { get; }

        internal bool Available => property != null || getter != null || field != null;

        internal string AccessorKind
        {
            get
            {
                if (property != null)
                    return "property";
                if (getter != null)
                    return "getter";
                return field != null ? "field" : "missing";
            }
        }

        internal object? GetValue(CampaignController campaign)
        {
            if (property != null)
                return property.GetValue(campaign);

            if (getter != null)
                return getter.Invoke(campaign, null);

            return field?.GetValue(campaign);
        }
    }

    private sealed record AreaFleetSummary(
        int AllShips,
        int ReachedDestShips,
        int ReadyShips,
        float Tons,
        float PowerProjection,
        float Escort,
        float Raid,
        string Roles,
        TaskForceSummary TaskForces)
    {
        internal static readonly AreaFleetSummary Unresolved = new(0, 0, 0, 0f, 0f, 0f, 0f, "unresolved", TaskForceSummary.Empty);

        internal string LogText =>
            $"all={AllShips}:reached={ReachedDestShips}:ready={ReadyShips}:tons={Fmt(Tons)}:pp={Fmt(PowerProjection)}:" +
            $"escort={Fmt(Escort)}:raid={Fmt(Raid)}:movingTF={TaskForces.LogText}:roles={Roles}";
    }

    private sealed record TaskForceSummary(int Moving, int Total, int WasMove, int ToBattle, int FromBattle, string Examples)
    {
        internal static readonly TaskForceSummary Empty = new(0, 0, 0, 0, 0, "none");

        internal string LogText =>
            $"moving={Moving}/{Total}:wasMove={WasMove}:toBattle={ToBattle}:fromBattle={FromBattle}:examples={Examples}";
    }

    private sealed record EnemyAreaSummary(Player Player, AreaFleetSummary Fleet);
}
