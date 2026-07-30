using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Temporary diagnostic: identify which CampaignController.NextTurn state mutates
// transport-loss dictionaries, and whether the area-vessel helper is involved.
[HarmonyPatch]
internal static class CampaignTransportLossNextTurnStateDiagnosticsPatch
{
    private const string LogPrefix = "UADVP TR state diag";
    private const int MaxEnemyDetails = 6;
    private const int TransportLossState = 8;
    private const string TransportLossStateSource = "NextTurnState8";

    private static readonly LossDictionaryAccessor TrLossesPerTurn = new("trLosesPerTurn");
    private static readonly LossDictionaryAccessor TrLossesByIgnorePerTurn = new("trLosesByIgnorePerTurn");

    private static Type? stateMachineType;
    private static MethodInfo? moveNextMethod;
    private static MemberAccessor? stateMember;
    private static MemberAccessor? campaignMember;
    private static bool resolved;

    private static bool hasContext;
    private static IntPtr contextCampaign;
    private static int contextStateBefore;
    private static int vesselsInAreaCalls;
    private static int wouldFilterCalls;
    private static int wouldFilterVessels;
    private static int movingTaskForces;
    private static int totalTaskForces;
    private static bool filterActivatedForState;
    private static readonly HashSet<IntPtr> MovingVesselIds = new();
    private static Dictionary<string, LossEntry> beforeLosses = new(StringComparer.Ordinal);
    private static Dictionary<string, LossEntry> beforeIgnoredLosses = new(StringComparer.Ordinal);
    private static string lastFailure = string.Empty;

    private static MethodBase? TargetMethod()
    {
        ResolveTarget();
        return moveNextMethod;
    }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        ResolveTarget();
        bool available = moveNextMethod != null &&
                         stateMember != null &&
                         campaignMember != null &&
                         TrLossesPerTurn.Available;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} proof: MoveNext target={(moveNextMethod != null ? "yes" : "no")} " +
            $"type={LogToken(stateMachineType?.FullName)} stateField={LogToken(stateMember?.Name)} " +
            $"thisField={LogToken(campaignMember?.Name)} losses={TrLossesPerTurn.AccessorKind} " +
            $"ignored={TrLossesByIgnorePerTurn.AccessorKind}.");

        if (!available && moveNextMethod != null && stateMachineType != null)
            LogMemberDump(stateMachineType);

        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix} unavailable: NextTurn state-machine target or transport-loss accessor missing.");

        return available;
    }

    [HarmonyPrefix]
    private static void Prefix(object __instance)
    {
        try
        {
            filterActivatedForState = false;
            CampaignController? campaign = ReadCampaign(__instance);
            contextStateBefore = ReadState(__instance);
            contextCampaign = campaign?.Pointer ?? IntPtr.Zero;
            vesselsInAreaCalls = 0;
            wouldFilterCalls = 0;
            wouldFilterVessels = 0;
            movingTaskForces = 0;
            totalTaskForces = 0;
            MovingVesselIds.Clear();

            if (campaign == null)
            {
                hasContext = false;
                beforeLosses.Clear();
                beforeIgnoredLosses.Clear();
                return;
            }

            if (contextStateBefore == TransportLossState)
            {
                CampaignTransportLossAreaVesselScope.BeginForTransportLossState(campaign, TransportLossStateSource);
                filterActivatedForState = true;
            }

            beforeLosses = SnapshotLosses(ReadLossDictionary(campaign, TrLossesPerTurn), "regular");
            beforeIgnoredLosses = SnapshotLosses(ReadLossDictionary(campaign, TrLossesByIgnorePerTurn), "ignored");
            CacheMovingVessels(campaign);
            hasContext = true;
        }
        catch (Exception ex)
        {
            if (filterActivatedForState)
            {
                CampaignTransportLossAreaVesselScope.EndForTransportLossState(TransportLossStateSource);
                filterActivatedForState = false;
            }

            ClearContext();
            LogFailureOnce("prefix", ex);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        try
        {
            CampaignController? campaign = ReadCampaign(__instance);
            int stateAfter = ReadState(__instance);
            if (campaign == null)
            {
                ClearContext();
                return;
            }

            Dictionary<string, LossEntry> afterLosses = SnapshotLosses(ReadLossDictionary(campaign, TrLossesPerTurn), "regular");
            Dictionary<string, LossEntry> afterIgnoredLosses = SnapshotLosses(ReadLossDictionary(campaign, TrLossesByIgnorePerTurn), "ignored");
            LogPositiveDeltas(campaign, "regular", beforeLosses, afterLosses, contextStateBefore, stateAfter);
            LogPositiveDeltas(campaign, "ignored", beforeIgnoredLosses, afterIgnoredLosses, contextStateBefore, stateAfter);
        }
        catch (Exception ex)
        {
            LogFailureOnce("postfix", ex);
        }
        finally
        {
            if (filterActivatedForState)
            {
                CampaignTransportLossAreaVesselScope.EndForTransportLossState(TransportLossStateSource);
                filterActivatedForState = false;
            }

            ClearContext();
        }
    }

    internal static void RecordVesselsInAreaCall(CampaignController? campaign, Area? area, Player? player)
    {
        if (!hasContext || campaign == null || area == null || player == null)
            return;

        if (contextCampaign != IntPtr.Zero && campaign.Pointer != contextCampaign)
            return;

        try
        {
            vesselsInAreaCalls++;
            int movingInCall = CountMovingCachedVessels(campaign, area, player);
            if (movingInCall <= 0)
                return;

            wouldFilterCalls++;
            wouldFilterVessels += movingInCall;
        }
        catch (Exception ex)
        {
            LogFailureOnce("record-vessels", ex);
        }
    }

    private static void ResolveTarget()
    {
        if (resolved)
            return;

        resolved = true;
        Type controllerType = typeof(CampaignController);
        Type[] nestedTypes = controllerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        stateMachineType = nestedTypes.FirstOrDefault(IsPreferredNextTurnStateMachine) ??
                           nestedTypes.FirstOrDefault(IsAnyNextTurnStateMachine);
        if (stateMachineType == null)
            return;

        moveNextMethod = stateMachineType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(static method => method.Name == "MoveNext" &&
                                             method.ReturnType == typeof(bool) &&
                                             method.GetParameters().Length == 0);
        stateMember = ResolveStateMember(stateMachineType);
        campaignMember = ResolveCampaignMember(stateMachineType);
    }

    private static bool IsPreferredNextTurnStateMachine(Type type)
    {
        string name = type.FullName ?? type.Name;
        return name.Contains("NextTurn", StringComparison.Ordinal) &&
               (name.Contains("108", StringComparison.Ordinal) || name.Contains("<NextTurn>d__108", StringComparison.Ordinal));
    }

    private static bool IsAnyNextTurnStateMachine(Type type)
        => (type.FullName ?? type.Name).Contains("NextTurn", StringComparison.Ordinal) &&
           type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               .Any(static method => method.Name == "MoveNext" &&
                                     method.ReturnType == typeof(bool) &&
                                     method.GetParameters().Length == 0);

    private static MemberAccessor? ResolveStateMember(Type type)
        => ResolveMember(type, "<>1__state", static memberType => memberType == typeof(int)) ??
           ResolveMember(type, "__1__state", static memberType => memberType == typeof(int)) ??
           ResolveMember(type, "1__state", static memberType => memberType == typeof(int)) ??
           ResolveAnyStateMember(type);

    private static MemberAccessor? ResolveCampaignMember(Type type)
        => ResolveMember(type, "<>4__this", static memberType => typeof(CampaignController).IsAssignableFrom(memberType)) ??
           ResolveMember(type, "__4__this", static memberType => typeof(CampaignController).IsAssignableFrom(memberType)) ??
           ResolveMember(type, "4__this", static memberType => typeof(CampaignController).IsAssignableFrom(memberType)) ??
           ResolveAnyCampaignMember(type);

    private static MemberAccessor? ResolveMember(Type type, string name, Func<Type, bool> typePredicate)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo? field = type.GetField(name, flags);
        if (field != null && typePredicate(field.FieldType))
            return new MemberAccessor(field, null);

        PropertyInfo? property = type.GetProperty(name, flags);
        if (property?.GetMethod != null && typePredicate(property.PropertyType))
            return new MemberAccessor(null, property);

        field = type.GetFields(flags)
            .FirstOrDefault(candidate =>
                candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                typePredicate(candidate.FieldType));
        if (field != null)
            return new MemberAccessor(field, null);

        property = type.GetProperties(flags)
            .FirstOrDefault(candidate =>
                candidate.GetMethod != null &&
                candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                typePredicate(candidate.PropertyType));
        return property == null ? null : new MemberAccessor(null, property);
    }

    private static MemberAccessor? ResolveAnyStateMember(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo? field = type.GetFields(flags)
            .FirstOrDefault(static candidate =>
                candidate.FieldType == typeof(int) &&
                candidate.Name.Contains("state", StringComparison.OrdinalIgnoreCase));
        if (field != null)
            return new MemberAccessor(field, null);

        PropertyInfo? property = type.GetProperties(flags)
            .FirstOrDefault(static candidate =>
                candidate.GetMethod != null &&
                candidate.PropertyType == typeof(int) &&
                candidate.Name.Contains("state", StringComparison.OrdinalIgnoreCase));
        return property == null ? null : new MemberAccessor(null, property);
    }

    private static MemberAccessor? ResolveAnyCampaignMember(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo? field = type.GetFields(flags)
            .FirstOrDefault(static candidate => typeof(CampaignController).IsAssignableFrom(candidate.FieldType));
        if (field != null)
            return new MemberAccessor(field, null);

        PropertyInfo? property = type.GetProperties(flags)
            .FirstOrDefault(static candidate =>
                candidate.GetMethod != null &&
                typeof(CampaignController).IsAssignableFrom(candidate.PropertyType));
        return property == null ? null : new MemberAccessor(null, property);
    }

    private static CampaignController? ReadCampaign(object stateMachine)
    {
        if (campaignMember == null)
            return null;

        return campaignMember.GetValue(stateMachine) as CampaignController;
    }

    private static int ReadState(object stateMachine)
    {
        if (stateMember == null)
            return int.MinValue;

        object? value = stateMember.GetValue(stateMachine);
        return value is int state ? state : int.MinValue;
    }

    private static object? ReadLossDictionary(CampaignController campaign, LossDictionaryAccessor accessor)
    {
        if (!accessor.Available)
            return null;

        try
        {
            return accessor.GetValue(campaign);
        }
        catch (Exception ex)
        {
            LogFailureOnce("read-losses-" + accessor.Name, ex);
            return null;
        }
    }

    private static Dictionary<string, LossEntry> SnapshotLosses(object? rawLosses, string kind)
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
                    AddLossEntry(result, player, regionEntry.Key, regionEntry.Value, kind);
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
                    AddLossEntry(result, player, regionEntry.Key, regionEntry.Value, kind);
            }

            return result;
        }

        LogFailureOnce("snapshot-losses", new InvalidOperationException($"unexpected loss dictionary type {rawLosses.GetType().FullName}"));
        return result;
    }

    private static void AddLossEntry(Dictionary<string, LossEntry> result, Player player, string? region, int count, string kind)
    {
        string regionText = string.IsNullOrWhiteSpace(region) ? "<empty>" : region.Trim();
        string key = $"{kind}|{PlayerIdentity(player)}|{NormalizeAreaName(regionText)}";
        result[key] = new LossEntry(player, regionText, count, kind);
    }

    private static void LogPositiveDeltas(
        CampaignController campaign,
        string kind,
        Dictionary<string, LossEntry> before,
        Dictionary<string, LossEntry> after,
        int stateBefore,
        int stateAfter)
    {
        foreach (KeyValuePair<string, LossEntry> entry in after)
        {
            before.TryGetValue(entry.Key, out LossEntry previous);
            int delta = entry.Value.Count - previous.Count;
            if (delta <= 0)
                continue;

            LogDelta(campaign, kind, entry.Value, delta, stateBefore, stateAfter);
        }
    }

    private static void LogDelta(CampaignController campaign, string kind, LossEntry loss, int delta, int stateBefore, int stateAfter)
    {
        Area? area = ResolveArea(campaign, loss.Region);
        string enemyActiveSummary = area != null && loss.Player != null
            ? EnemyActiveSummary(campaign, area, loss.Player)
            : "unresolved";

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: stateBefore={stateBefore} stateAfter={stateAfter} turn={LogToken(AiDesignCompetitiveness.CurrentTurnLabel())} " +
            $"kind={LogToken(kind)} victim={LogToken(PlayerLabel(loss.Player))} region={Quote(loss.Region)} delta={delta} total={loss.Count} " +
            $"area={LogToken(AreaLabel(area))} vesselsCalls={vesselsInAreaCalls} wouldFilterCalls={wouldFilterCalls} " +
            $"movingVessels={MovingVesselIds.Count} movingTF={movingTaskForces}/{totalTaskForces} wouldFilterVessels={wouldFilterVessels} " +
            $"enemyActiveSummary={LogToken(enemyActiveSummary)}.");
    }

    private static void CacheMovingVessels(CampaignController campaign)
    {
        MovingVesselIds.Clear();
        totalTaskForces = 0;
        movingTaskForces = 0;

        var taskForces = Safe(() => campaign.CampaignData?.TaskForces, null);
        if (taskForces == null)
            return;

        foreach (CampaignController.TaskForce group in taskForces)
        {
            if (group == null)
                continue;

            totalTaskForces++;
            if (!IsTaskForceMoving(group))
                continue;

            movingTaskForces++;
            var vessels = Safe(() => group.Vessels, null);
            if (vessels == null)
                continue;

            foreach (VesselEntity vessel in vessels)
            {
                if (vessel != null)
                    MovingVesselIds.Add(vessel.Pointer);
            }
        }
    }

    private static int CountMovingCachedVessels(CampaignController campaign, Area area, Player player)
    {
        if (MovingVesselIds.Count == 0)
            return 0;

        Il2CppSystem.Collections.Generic.List<VesselEntity>? vessels = CachedVesselsInArea(campaign, area, player);
        if (vessels == null)
            return 0;

        int count = 0;
        foreach (VesselEntity vessel in vessels)
        {
            if (vessel != null && MovingVesselIds.Contains(vessel.Pointer))
                count++;
        }

        return count;
    }

    private static Il2CppSystem.Collections.Generic.List<VesselEntity>? CachedVesselsInArea(CampaignController campaign, Area area, Player player)
    {
        var byArea = Safe(() => campaign.CampaignData?.VesselsInAreaOfPlayer, null);
        if (byArea == null || !byArea.ContainsKey(area))
            return null;

        var byPlayer = byArea[area];
        if (byPlayer == null || !byPlayer.ContainsKey(player))
            return null;

        return byPlayer[player];
    }

    private static string EnemyActiveSummary(CampaignController campaign, Area area, Player victim)
    {
        List<string> entries = new();
        foreach (Player enemy in WarEnemies(campaign, victim))
        {
            var vessels = CachedVesselsInArea(campaign, area, enemy);
            if (vessels == null)
                continue;

            int all = 0;
            int moving = 0;
            foreach (VesselEntity vessel in vessels)
            {
                if (vessel == null)
                    continue;

                all++;
                if (MovingVesselIds.Contains(vessel.Pointer))
                    moving++;
            }

            if (all <= 0)
                continue;

            entries.Add($"{LogToken(PlayerLabel(enemy))}:all={all}:active={all - moving}:moving={moving}");
            if (entries.Count >= MaxEnemyDetails)
                break;
        }

        return entries.Count == 0 ? "none" : string.Join("|", entries);
    }

    private static IEnumerable<Player> WarEnemies(CampaignController campaign, Player victim)
    {
        List<Player> enemies = new();
        var relations = Safe(() => campaign.CampaignData?.Relations, null);
        if (relations == null)
            return enemies;

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

        return enemies;

        void AddEnemy(Player? enemy)
        {
            if (enemy == null || enemies.Any(existing => SamePlayer(existing, enemy)))
                return;

            enemies.Add(enemy);
        }
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
        var vesselsByAreaAndPlayer = Safe(() => data?.VesselsInAreaOfPlayer, null);
        if (vesselsByAreaAndPlayer != null)
        {
            foreach (var entry in vesselsByAreaAndPlayer)
                AddArea(entry.Key);
        }

        var vesselsInArea = Safe(() => data?.VesselsInArea, null);
        if (vesselsInArea != null)
        {
            foreach (var entry in vesselsInArea)
                AddArea(entry.Key);
        }

        return areas;
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

    private static bool IsTaskForceMoving(CampaignController.TaskForce group)
    {
        bool moving = Safe(() => group.IsMoving(), false);
        if (moving)
            return true;

        return Safe(() => group.Path != null && group.CurrentPositionIndex < group.Path.Length - 1, false);
    }

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

    private static string NormalizeAreaName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace("\r", " ").Replace("\n", " ").ToUpperInvariant();
    }

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

    private static void ClearContext()
    {
        hasContext = false;
        contextCampaign = IntPtr.Zero;
        contextStateBefore = int.MinValue;
        vesselsInAreaCalls = 0;
        wouldFilterCalls = 0;
        wouldFilterVessels = 0;
        movingTaskForces = 0;
        totalTaskForces = 0;
        filterActivatedForState = false;
        MovingVesselIds.Clear();
        beforeLosses.Clear();
        beforeIgnoredLosses.Clear();
    }

    private static void LogFailureOnce(string action, Exception ex)
    {
        string failure = $"{action}:{ex.GetType().Name}:{ex.Message}";
        if (string.Equals(lastFailure, failure, StringComparison.Ordinal))
            return;

        lastFailure = failure;
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: {action} failed; diagnostic remains read-only. {ex.GetType().Name}: {ex.Message}");
    }

    private static void LogMemberDump(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string fields = string.Join(",", type.GetFields(flags).Take(24).Select(static field => $"{field.Name}:{field.FieldType.Name}"));
        string properties = string.Join(",", type.GetProperties(flags).Take(24).Select(static property => $"{property.Name}:{property.PropertyType.Name}"));

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix} members: fields={LogToken(fields)} properties={LogToken(properties)}.");
    }

    private readonly record struct LossEntry(Player? Player, string Region, int Count, string Kind);

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
    }

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
}
