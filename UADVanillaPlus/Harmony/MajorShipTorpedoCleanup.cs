using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

internal sealed class MajorShipTorpedoAuditResult
{
    internal bool Applied { get; init; }
    internal string Reason { get; init; } = "none";
    internal string Result { get; init; } = "skipped";
    internal int LiveLaunchers { get; init; }
    internal int CacheLaunchers { get; init; }
    internal int StoreLaunchers { get; init; } = -1;
    internal int ReloadLaunchers { get; init; } = -1;
    internal bool HaveTorpedoes { get; init; }
    internal int TorpedoesAllCount { get; init; }
    internal int SupportComponents { get; init; }
    internal int ExcludedTorpedoEquipment { get; init; }
}

internal sealed class MajorShipTorpedoCleanupResult
{
    internal bool Applied { get; init; }
    internal string Reason { get; init; } = "none";
    internal int LivePartsBefore { get; init; }
    internal int LivePartsAfter { get; init; }
    internal int CacheBefore { get; init; }
    internal int CacheAfter { get; init; }
    internal bool HaveTorpedoesBefore { get; init; }
    internal bool HaveTorpedoesAfter { get; init; }
    internal int TorpedoesAllBefore { get; init; }
    internal int TorpedoesAllAfter { get; init; }
    internal int RemovedByRemovePart { get; init; }
    internal int RemovedStaleCache { get; init; }
    internal int RemovedSupportComponents { get; init; }
    internal int StoreNameTubes { get; init; } = -1;
    internal int ReloadTubes { get; init; } = -1;
    internal bool RecalcOk { get; init; }
    internal bool WeaponCacheRefreshOk { get; init; }
    internal string Valid { get; init; } = "?";
    internal float TonsBefore { get; init; }
    internal float TonsAfter { get; init; }
    internal HashSet<string> RemovedTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> RemovedComponents { get; init; } = new();

    internal int RemovedLaunchers => RemovedByRemovePart + RemovedStaleCache;
    internal string RemovedComponentsText => MajorShipTorpedoCleanup.FormatRemovedItems(RemovedComponents);
}

internal static class MajorShipTorpedoCleanup
{
    private static readonly HashSet<string> LoggedAuditKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedCleanAuditKeys = new(StringComparer.Ordinal);
    private static readonly MethodInfo? CWeapMethod = AccessTools.Method(typeof(Ship), "CWeap", new[] { typeof(bool) });

    internal static MajorShipTorpedoAuditResult Audit(
        Ship? ship,
        Player? player,
        string context,
        string turnLabel,
        bool requireAiNonMain = true,
        bool logClean = false)
    {
        MajorShipTorpedoAuditResult result = BuildAudit(ship, player, requireAiNonMain);
        if (!result.Applied)
            return result;

        bool shouldLog = !string.Equals(result.Result, "clean", StringComparison.OrdinalIgnoreCase) || logClean;
        if (shouldLog)
            LogAudit(result, ship, player, context, turnLabel, logClean);

        return result;
    }

    internal static MajorShipTorpedoCleanupResult Cleanup(Ship? ship, Player? player, bool requireAiNonMain = true)
    {
        if (!ModSettings.MajorShipTorpedoesRestricted)
            return Skipped("option-off");
        if (ship == null || player == null)
            return Skipped("missing-context");
        if (requireAiNonMain && !Safe(() => player.isAi && !player.isMain, false))
            return Skipped("not-ai");
        if (!IsMajorSurfaceCombatant(ship.shipType))
            return Skipped("not-major-surface");

        int liveBefore = CountTorpedoLauncherParts(ship);
        int cacheBefore = CountTorpedoLauncherCacheEntries(ship);
        bool haveTorpedoesBefore = HasTorpedoes(ship);
        int torpedoesAllBefore = CountTorpedoesAllEntries(ship);
        List<string> supportBeforeTokens = new();
        int supportBefore = CountTorpedoSupportComponents(ship, supportBeforeTokens);
        if (liveBefore <= 0 && cacheBefore <= 0 && supportBefore <= 0 && !haveTorpedoesBefore)
            return Skipped("no-launchers");

        float tonsBefore = Safe(() => ship.Tonnage(), 0f);
        HashSet<string> removedTokens = new(StringComparer.OrdinalIgnoreCase);
        List<string> removedComponents = new();
        List<Part> launchers = TorpedoLauncherParts(ship);
        int removedByRemovePart = 0;

        foreach (Part part in launchers)
        {
            PartData? data = Safe(() => part?.data, null);
            AddEquipmentTokens(removedTokens, data);
            try
            {
                ship.RemovePart(part, true, true);
                removedByRemovePart++;
            }
            catch
            {
            }
        }

        int removedStaleCache = RemoveStaleTorpedoLauncherCacheEntries(ship, removedTokens);
        int removedSupport = RemoveComponents(ship, IsTorpedoSupportComponent, removedTokens, removedComponents);

        bool recalcOk = Recalculate(ship);
        bool weaponCacheRefreshOk = ForceWeaponCacheRefresh(ship);
        float tonsAfter = Safe(() => ship.Tonnage(), tonsBefore);
        int liveAfter = CountTorpedoLauncherParts(ship);
        int cacheAfter = CountTorpedoLauncherCacheEntries(ship);
        bool haveTorpedoesAfter = HasTorpedoes(ship);
        int torpedoesAllAfter = CountTorpedoesAllEntries(ship);
        Ship.Store? store = Safe(() => ship.ToStore(false), null);
        int storeNameTubes = StoreTorpedoLauncherCount(store);
        int reloadTubes = ReloadTorpedoLauncherCount(store);
        ShipEffectivePowerCalculator.Invalidate(ship);
        bool removedAnything = removedByRemovePart > 0 || removedStaleCache > 0 || removedSupport > 0;
        string reason = removedAnything
            ? "cleaned"
            : !haveTorpedoesAfter && cacheAfter <= 0
                ? "cache-refreshed"
                : "cache-refresh-stale";

        return new MajorShipTorpedoCleanupResult
        {
            Applied = true,
            Reason = reason,
            LivePartsBefore = liveBefore,
            LivePartsAfter = liveAfter,
            CacheBefore = cacheBefore,
            CacheAfter = cacheAfter,
            HaveTorpedoesBefore = haveTorpedoesBefore,
            HaveTorpedoesAfter = haveTorpedoesAfter,
            TorpedoesAllBefore = torpedoesAllBefore,
            TorpedoesAllAfter = torpedoesAllAfter,
            RemovedByRemovePart = removedByRemovePart,
            RemovedStaleCache = removedStaleCache,
            RemovedSupportComponents = removedSupport,
            StoreNameTubes = storeNameTubes,
            ReloadTubes = reloadTubes,
            RecalcOk = recalcOk,
            WeaponCacheRefreshOk = weaponCacheRefreshOk,
            Valid = BoolProbe(() => ship.IsValid(false)),
            TonsBefore = tonsBefore,
            TonsAfter = tonsAfter,
            RemovedTokens = removedTokens,
            RemovedComponents = removedComponents,
        };
    }

    private static MajorShipTorpedoAuditResult BuildAudit(Ship? ship, Player? player, bool requireAiNonMain)
    {
        if (!ModSettings.MajorShipTorpedoesRestricted)
            return AuditSkipped("option-off");
        if (ship == null || player == null)
            return AuditSkipped("missing-context");
        if (requireAiNonMain && !Safe(() => player.isAi && !player.isMain, false))
            return AuditSkipped("not-ai");
        if (!IsMajorSurfaceCombatant(ship.shipType))
            return AuditSkipped("not-major-surface");

        int live = CountTorpedoLauncherParts(ship);
        int cache = CountTorpedoLauncherCacheEntries(ship);
        bool haveTorpedoes = HasTorpedoes(ship);
        int torpedoesAllCount = CountTorpedoesAllEntries(ship);
        Ship.Store? store = Safe(() => ship.ToStore(false), null);
        int storeLaunchers = StoreTorpedoLauncherCount(store);
        int reload = ReloadTorpedoLauncherCount(store);
        List<string> supportTokens = new();
        int support = CountTorpedoSupportComponents(ship, supportTokens);
        int excluded = CountExcludedTorpedoEquipment(ship);

        string result =
            live > 0 ? "live-leak" :
            cache > 0 ? "cache-leak" :
            haveTorpedoes && torpedoesAllCount == 0 ? "flag-leak" :
            storeLaunchers > 0 ? "store-leak" :
            reload > 0 ? "reload-leak" :
            support > 0 ? "support-only" :
            "clean";

        return new MajorShipTorpedoAuditResult
        {
            Applied = true,
            Reason = "audited",
            Result = result,
            LiveLaunchers = live,
            CacheLaunchers = cache,
            StoreLaunchers = storeLaunchers,
            ReloadLaunchers = reload,
            HaveTorpedoes = haveTorpedoes,
            TorpedoesAllCount = torpedoesAllCount,
            SupportComponents = support,
            ExcludedTorpedoEquipment = excluded,
        };
    }

    private static MajorShipTorpedoAuditResult AuditSkipped(string reason)
        => new() { Applied = false, Reason = reason, Result = "skipped" };

    private static void LogAudit(
        MajorShipTorpedoAuditResult result,
        Ship? ship,
        Player? player,
        string context,
        string turnLabel,
        bool logClean)
    {
        string turn = LogToken(turnLabel);
        string nation = AiDesignCompetitiveness.PlayerLabel(player);
        string type = AiDesignCompetitiveness.NormalizeShipType(ship?.shipType);
        string shipName = LogToken(AiDesignCompetitiveness.ShipLabel(ship));
        string key = $"{turn}:{nation}:{context}:{type}:{shipName}:{result.Result}:{result.LiveLaunchers}:{result.CacheLaunchers}:{result.StoreLaunchers}:{result.ReloadLaunchers}:{result.HaveTorpedoes}:{result.TorpedoesAllCount}:{result.SupportComponents}";
        if (string.Equals(result.Result, "clean", StringComparison.OrdinalIgnoreCase))
        {
            string cleanKey = $"{turn}:{nation}:{context}:clean";
            if (!logClean || !LoggedCleanAuditKeys.Add(cleanKey))
                return;
        }
        else if (!LoggedAuditKeys.Add(key))
        {
            return;
        }

        if (LoggedAuditKeys.Count > 256)
            LoggedAuditKeys.Clear();
        if (LoggedCleanAuditKeys.Count > 128)
            LoggedCleanAuditKeys.Clear();

        List<string> launcherParts = ship == null ? new() : TorpedoLauncherTokens(ship);
        List<string> supportComponents = ship == null ? new() : TorpedoSupportComponentTokens(ship);
        Ship.Store? store = Safe(() => ship?.ToStore(false), null);
        List<string> storeParts = StoreTorpedoLauncherTokens(store);
        Ship? linkedDesign = Safe(() => ship?.design, null);

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP torpedo-audit turn={turn} nation={nation} context={LogToken(context)} type={type} ship={shipName} isDesign={BoolText(Safe(() => ship?.isDesign ?? false, false))} isRefitDesign={BoolText(Safe(() => ship?.isRefitDesign ?? false, false))} isShared={BoolText(IsSharedDesignMarker(ship))} design={DesignLinkLabel(linkedDesign)} liveLaunchers={result.LiveLaunchers} cacheLaunchers={result.CacheLaunchers} haveTorpedoes={BoolText(result.HaveTorpedoes)} torpedoesAll={result.TorpedoesAllCount} storeLaunchers={TubeCountText(result.StoreLaunchers)} reloadLaunchers={TubeCountText(result.ReloadLaunchers)} supportCount={result.SupportComponents} supportComponents={FormatRemovedItems(supportComponents)} excludedTorpedoEquipment={result.ExcludedTorpedoEquipment} launcherParts={FormatRemovedItems(launcherParts)} storeParts={FormatRemovedItems(storeParts)} result={result.Result} reason={result.Reason}.");
    }

    internal static string FormatRemovedItems(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return "none";

        List<string> tokens = items
            .Where(item => !string.IsNullOrWhiteSpace(item) && !string.Equals(item, "<empty>", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(LogToken)
            .ToList();
        int hidden = Math.Max(0, items.Distinct(StringComparer.OrdinalIgnoreCase).Count() - tokens.Count);
        if (hidden > 0)
            tokens.Add($"+{hidden}more");

        return tokens.Count == 0 ? "none" : string.Join(",", tokens);
    }

    internal static string TubeCountText(int value)
        => value < 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);

    private static List<string> TorpedoLauncherTokens(Ship ship)
    {
        List<string> tokens = new();
        foreach (PartData data in TorpedoLauncherPartData(ship))
            tokens.Add(PartToken(data));

        return tokens;
    }

    private static List<PartData> TorpedoLauncherPartData(Ship ship)
    {
        List<PartData> result = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        AddLauncherPartDataFromParts(ship, result, seen);
        AddLauncherPartDataFromCache(ship, result, seen);
        return result;
    }

    private static void AddLauncherPartDataFromParts(Ship ship, List<PartData> result, HashSet<string> seen)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return;

            foreach (Part part in parts)
            {
                PartData? data = Safe(() => part?.data, null);
                if (IsTorpedoLauncherPart(data))
                    AddDistinctPartData(result, seen, data!);
            }
        }
        catch
        {
        }
    }

    private static void AddLauncherPartDataFromCache(Ship ship, List<PartData> result, HashSet<string> seen)
    {
        try
        {
            var torpedoes = ship.torpedoesAll;
            if (torpedoes == null)
                return;

            foreach (Part part in torpedoes)
            {
                PartData? data = Safe(() => part?.data, null);
                if (IsTorpedoLauncherPart(data))
                    AddDistinctPartData(result, seen, data!);
            }
        }
        catch
        {
        }
    }

    private static void AddDistinctPartData(List<PartData> result, HashSet<string> seen, PartData data)
    {
        string key = $"{SafeString(() => data.name)}|{SafeString(() => data.type)}|{SafeString(() => data.nameUi)}";
        if (seen.Add(key))
            result.Add(data);
    }

    private static List<string> TorpedoSupportComponentTokens(Ship ship)
    {
        List<string> tokens = new();
        CountTorpedoSupportComponents(ship, tokens);
        return tokens;
    }

    private static int CountTorpedoSupportComponents(Ship ship, List<string> tokens)
    {
        try
        {
            var components = ship.components;
            if (components == null)
                return 0;

            int count = 0;
            foreach (var pair in components)
            {
                if (!IsTorpedoSupportComponent(pair.Value))
                    continue;

                count++;
                tokens.Add(ComponentToken(pair.Value));
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountExcludedTorpedoEquipment(Ship ship)
    {
        try
        {
            int count = 0;
            var parts = ship.parts;
            if (parts == null)
                return 0;

            foreach (Part part in parts)
            {
                PartData? data = Safe(() => part?.data, null);
                if (data == null)
                    continue;

                bool hasTorpedoText = StrictContains(data.name, "torpedo") ||
                                      StrictContains(data.type, "torpedo") ||
                                      StrictContains(data.nameUi, "torpedo");
                if (hasTorpedoText && (IsTorpedoProtectionOrDetectionText(data.name) ||
                                       IsTorpedoProtectionOrDetectionText(data.type) ||
                                       IsTorpedoProtectionOrDetectionText(data.nameUi)))
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

    private static List<string> StoreTorpedoLauncherTokens(Ship.Store? store)
    {
        List<string> tokens = new();
        try
        {
            if (store?.parts == null)
                return tokens;

            foreach (Part.Store part in store.parts)
            {
                string name = SafeString(() => part.name);
                if (!IsTorpedoProtectionOrDetectionText(name) && StrictContains(name, "torpedo"))
                    tokens.Add(name);
            }
        }
        catch
        {
        }

        return tokens;
    }

    private static string DesignLinkLabel(Ship? design)
    {
        if (design == null)
            return "none";

        return $"{AiDesignCompetitiveness.NormalizeShipType(design.shipType)}:{LogToken(AiDesignCompetitiveness.ShipLabel(design))}";
    }

    private static MajorShipTorpedoCleanupResult Skipped(string reason)
        => new() { Applied = false, Reason = reason };

    private static List<Part> TorpedoLauncherParts(Ship ship)
    {
        List<Part> result = new();
        HashSet<IntPtr> seen = new();
        AddTorpedoLauncherParts(ship, result, seen);
        AddTorpedoLauncherCacheEntries(ship, result, seen);
        return result;
    }

    private static void AddTorpedoLauncherParts(Ship ship, List<Part> result, HashSet<IntPtr> seen)
    {
        try
        {
            var parts = ship.parts;
            if (parts == null)
                return;

            foreach (Part part in parts)
            {
                if (part != null && IsTorpedoLauncherPart(Safe(() => part.data, null)))
                    AddDistinctPart(result, seen, part);
            }
        }
        catch
        {
        }
    }

    private static void AddTorpedoLauncherCacheEntries(Ship ship, List<Part> result, HashSet<IntPtr> seen)
    {
        try
        {
            var torpedoes = ship.torpedoesAll;
            if (torpedoes == null)
                return;

            foreach (Part part in torpedoes)
            {
                if (part != null && IsTorpedoLauncherPart(Safe(() => part.data, null)))
                    AddDistinctPart(result, seen, part);
            }
        }
        catch
        {
        }
    }

    private static void AddDistinctPart(List<Part> result, HashSet<IntPtr> seen, Part part)
    {
        IntPtr pointer = Safe(() => part.Pointer, IntPtr.Zero);
        if (pointer != IntPtr.Zero)
        {
            if (!seen.Add(pointer))
                return;
        }
        else if (result.Any(existing => ReferenceEquals(existing, part)))
        {
            return;
        }

        result.Add(part);
    }

    private static int RemoveStaleTorpedoLauncherCacheEntries(Ship ship, HashSet<string> removedTokens)
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
                {
                    AddEquipmentTokens(removedTokens, data);
                    remove.Add(part);
                }
            }

            int removed = 0;
            foreach (Part part in remove)
            {
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

    private static bool HasTorpedoes(Ship ship)
        => Safe(() => ship.haveTorpedoes, false);

    private static int CountTorpedoesAllEntries(Ship ship)
    {
        try
        {
            return ship.torpedoesAll?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool ForceWeaponCacheRefresh(Ship ship)
    {
        if (CWeapMethod == null)
            return false;

        try
        {
            CWeapMethod.Invoke(ship, new object[] { true });
            return true;
        }
        catch
        {
            return false;
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

    private static int ReloadTorpedoLauncherCount(Ship.Store? store)
    {
        if (store == null)
            return -1;

        Ship? temp = null;
        try
        {
            temp = Ship.Create(null, null, false, false, false);
            var emptyGuid = new Il2CppSystem.Nullable<Il2CppSystem.Guid>();
            temp.FromStore(store, emptyGuid, null, null, false);
            return CountTorpedoLauncherParts(temp) + CountTorpedoLauncherCacheEntries(temp);
        }
        catch
        {
            return -1;
        }
        finally
        {
            try
            {
                if (temp != null && !temp.isErased)
                    temp.Erase();
            }
            catch
            {
            }
        }
    }

    private static bool Recalculate(Ship ship)
    {
        try
        {
            ship.CalcWeightAndCost(true, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMajorSurfaceCombatant(ShipType? shipType)
    {
        string label = AiDesignCompetitiveness.NormalizeShipType(shipType);
        return label is "CA" or "BC" or "BB";
    }

    internal static bool IsTorpedoLauncherPartData(PartData? part)
        => IsTorpedoLauncherPart(part);

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

    private static void AddEquipmentTokens(HashSet<string> tokens, PartData? part)
    {
        if (part == null)
            return;

        AddToken(tokens, SafeString(() => part.name));
        AddToken(tokens, SafeString(() => part.type));
        AddToken(tokens, SafeString(() => part.nameUi));
    }

    private static void AddEquipmentTokens(HashSet<string> tokens, ComponentData? component)
    {
        if (component == null)
            return;

        AddToken(tokens, SafeString(() => component.name));
        AddToken(tokens, SafeString(() => component.nameShort));
        AddToken(tokens, SafeString(() => component.type));
        AddToken(tokens, SafeString(() => component.typex?.name));
    }

    private static void AddToken(HashSet<string> tokens, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "<empty>", StringComparison.Ordinal))
            return;

        tokens.Add(value.Trim());
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

    private static string PartToken(PartData? part)
    {
        if (part == null)
            return "unknown";

        string name = SafeString(() => part.name);
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        string type = SafeString(() => part.type);
        if (!string.Equals(type, "<empty>", StringComparison.Ordinal))
            return type;

        string nameUi = SafeString(() => part.nameUi);
        return string.Equals(nameUi, "<empty>", StringComparison.Ordinal) ? "unknown" : nameUi;
    }

    private static bool IsSharedDesignMarker(Ship? ship)
        => ship != null && Safe(() => ReadBoolMember(ship, "isSharedDesign") ||
                                     ReadBoolMember(ship, "IsSharedDesign") ||
                                     ReadBoolMember(ship, "isShared"), false);

    private static bool ReadBoolMember(object target, string name)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null && property.PropertyType == typeof(bool))
                return (bool)(property.GetValue(target) ?? false);

            FieldInfo? field = type.GetField(name, flags);
            if (field != null && field.FieldType == typeof(bool))
                return (bool)(field.GetValue(target) ?? false);
        }
        catch
        {
        }

        return false;
    }

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

    internal static string BoolText(bool value)
        => value.ToString().ToLowerInvariant();

    internal static string Fmt(float value)
        => float.IsNaN(value) || float.IsInfinity(value)
            ? "?"
            : value.ToString("0.0", CultureInfo.InvariantCulture);

    internal static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        return value.Trim()
            .Replace(" ", "_")
            .Replace(",", "_")
            .Replace(";", "_")
            .Replace("=", "-");
    }

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }
        catch
        {
            return "<empty>";
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
}
