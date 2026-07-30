using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using System.Reflection;
using System.Runtime.InteropServices;
using UADVanillaPlus.GameData;
using UnityEngine;

namespace UADVanillaPlus.Harmony;

// Temporary investigation diagnostic for battle accuracy gaps. It samples slow
// runtime state instead of touching hot combat methods such as Ship.HitChance.
internal static class BattleRuntimeAccuracyDiagnostics
{
    // Retained for future accuracy investigations. Normal builds hard-gate this
    // off through ModSettings.BattleRuntimeDiagnosticsEnabled.
    private const float SampleIntervalSeconds = 5f;
    private const float FirstSampleDelaySeconds = 1f;
    private const float AimDropThreshold = 0.05f;
    private const int InstabilityDamageFallbackOffset = 0x518;

    private static readonly Dictionary<string, SideRuntimeAggregate> SideAggregates = new(StringComparer.Ordinal)
    {
        ["attacker"] = new("attacker"),
        ["defender"] = new("defender"),
    };

    private static readonly Dictionary<string, DamageStateRuntimeAggregate> DamageStateAggregates = new(StringComparer.Ordinal)
    {
        ["attacker"] = new("attacker"),
        ["defender"] = new("defender"),
    };

    private static readonly Dictionary<long, ShipMotionState> PreviousShipMotion = new();
    private static readonly Dictionary<string, AimRuntimeState> PreviousAimState = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSessionMessages = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WeaponRuntimeAggregate> LiveWeaponAggregates = new(StringComparer.Ordinal)
    {
        ["attacker"] = new("attacker"),
        ["defender"] = new("defender"),
    };

    private static int? instabilityDamageOffset;
    private static string instabilityDamageOffsetSource = "unresolved";
    private static bool instabilityDamageResolutionLogged;
    private static bool instabilityDamageReaderDisabled;
    private static bool instabilityDamageReadFailureLogged;
    private static bool active;
    private static bool printedSummary;
    private static float nextSampleTime;
    private static int sessionSamples;
    private static int readFailures;
    private static string sessionContext = "unknown";
    private static string sessionSignature = "pending";
    private static Player? attackerPlayer;

    internal static void EnterBattle(string context)
    {
        if (!ModSettings.BattleRuntimeDiagnosticsEnabled)
            return;

        if (active)
            return;

        ClearSession();
        active = true;
        sessionContext = context;
        nextSampleTime = Time.realtimeSinceStartup + FirstSampleDelaySeconds;
        LogOnce("active:" + context, $"UADVP battle-runtime diagnostics active: context={context} sampleInterval={SampleIntervalSeconds:0.#}s.");
    }

    internal static void UpdateBattle()
    {
        if (!active || !ModSettings.BattleRuntimeDiagnosticsEnabled || !SafeBool(() => GameManager.IsBattle))
            return;

        if (Time.realtimeSinceStartup < nextSampleTime)
            return;

        nextSampleTime = Time.realtimeSinceStartup + SampleIntervalSeconds;
        try
        {
            SampleBattle();
        }
        catch (Exception ex)
        {
            readFailures++;
            LogOnce("sample-failed:" + ex.GetType().Name,
                $"UADVP battle-runtime diagnostics sample skipped. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void LeaveBattle(string context)
    {
        if (!active)
            return;

        PrintSummary(context);
        ClearSession();
    }

    internal static void RecordMadeShot(Part? from, int shots)
    {
        if (!CanRecordWeaponEvent() || shots <= 0)
            return;

        try
        {
            Ship? firingShip = Safe(() => from?.ship, null);
            PartData? partData = Safe(() => from?.data, null);
            AddLiveWeaponEvent(firingShip, partData, shots, 0, 0f);
        }
        catch
        {
            readFailures++;
        }
    }

    internal static void RecordTakenDamage(Part? from, float damage)
    {
        if (!CanRecordWeaponEvent())
            return;

        try
        {
            Ship? firingShip = Safe(() => from?.ship, null);
            PartData? partData = Safe(() => from?.data, null);
            AddLiveWeaponEvent(firingShip, partData, 0, 1, Math.Max(0f, damage));
        }
        catch
        {
            readFailures++;
        }
    }

    private static void SampleBattle()
    {
        List<Ship> ships = RuntimeBattleShips(includeSunk: false);
        if (ships.Count == 0)
            return;

        sessionSamples++;
        sessionSignature = BattleSignature(ships);
        attackerPlayer ??= ResolveAttackerPlayer(ships);

        Dictionary<string, SideSampleCounts> currentCounts = new(StringComparer.Ordinal)
        {
            ["attacker"] = new(),
            ["defender"] = new(),
        };

        foreach (Ship ship in ships)
        {
            string side = SideForShip(ship);
            if (!SideAggregates.TryGetValue(side, out SideRuntimeAggregate? aggregate))
                continue;

            SideSampleCounts counts = currentCounts[side];
            counts.Ships++;
            if (SafeBool(() => ship.isAiControlled))
                counts.AiControlled++;
            if (SafeBool(() => BattleDivisionAiControlPatch.IsShipAiControlledByVp(ship)))
                counts.VpAiControlled++;

            aggregate.ShipSamples++;
            SampleMotion(ship, aggregate);
            SampleAims(ship, aggregate);

            if (DamageStateAggregates.TryGetValue(side, out DamageStateRuntimeAggregate? damageAggregate))
                SampleDamageState(ship, damageAggregate);
        }

        foreach ((string side, SideSampleCounts counts) in currentCounts)
        {
            SideRuntimeAggregate aggregate = SideAggregates[side];
            aggregate.SampleTicks++;
            aggregate.LastShipCount = counts.Ships;
            aggregate.LastAiControlled = counts.AiControlled;
            aggregate.LastVpAiControlled = counts.VpAiControlled;
        }

        PrunePreviousState(ships);
    }

    private static void SampleMotion(Ship ship, SideRuntimeAggregate aggregate)
    {
        try
        {
            long key = ShipPointer(ship);
            if (key == 0)
                return;

            Transform transform = ship.transform;
            Vector3 position = transform.position;
            float yaw = transform.eulerAngles.y;
            float now = Time.realtimeSinceStartup;

            if (PreviousShipMotion.TryGetValue(key, out ShipMotionState previous))
            {
                float deltaTime = Mathf.Max(0.001f, now - previous.Time);
                float speed = Vector3.Distance(position, previous.Position) / deltaTime;
                float turnDelta = Mathf.Abs(Mathf.DeltaAngle(previous.Yaw, yaw)) / deltaTime;
                aggregate.SpeedSum += speed;
                aggregate.SpeedSamples++;
                aggregate.TurnDeltaSum += turnDelta;
                aggregate.TurnSamples++;
            }

            PreviousShipMotion[key] = new ShipMotionState(position, yaw, now);
        }
        catch
        {
            aggregate.ReadFailures++;
            readFailures++;
        }
    }

    private static void SampleAims(Ship ship, SideRuntimeAggregate aggregate)
    {
        try
        {
            SampleAimDictionary(ship, aggregate, "right", ship.enemies);
            SampleAimDictionary(ship, aggregate, "left", ship.enemiesLeftSide);
        }
        catch
        {
            aggregate.ReadFailures++;
            readFailures++;
        }
    }

    private static void SampleAimDictionary(
        Ship ship,
        SideRuntimeAggregate aggregate,
        string aimSide,
        Il2CppSystem.Collections.Generic.Dictionary<PartData, Ship.Aim>? aims)
    {
        if (aims == null)
            return;

        foreach (var entry in aims)
        {
            PartData? partData = entry.Key;
            Ship.Aim? aim = entry.Value;
            if (aim == null)
                continue;

            string aimKey = $"{ShipPointer(ship)}:{aimSide}:{PartToken(partData)}";
            string targetKey = ShipIdentity(Safe(() => aim.target, null));
            float progress = Safe(() => aim.progress, 0f);

            aggregate.AimProgressSum += progress;
            aggregate.AimSamples++;

            if (PreviousAimState.TryGetValue(aimKey, out AimRuntimeState previous))
            {
                if (!string.IsNullOrWhiteSpace(previous.TargetKey) &&
                    !string.IsNullOrWhiteSpace(targetKey) &&
                    !string.Equals(previous.TargetKey, targetKey, StringComparison.Ordinal))
                {
                    aggregate.TargetChanges++;
                }

                if (progress + AimDropThreshold < previous.Progress)
                    aggregate.AimDrops++;
            }

            PreviousAimState[aimKey] = new AimRuntimeState(targetKey, progress);

            try
            {
                if (aim.lastHitChance.HasValue)
                {
                    aggregate.HitChanceSum += aim.lastHitChance.Value;
                    aggregate.HitChanceSamples++;
                }
            }
            catch
            {
            }

            try
            {
                if (aim.lastRange.HasValue)
                {
                    aggregate.RangeSum += aim.lastRange.Value;
                    aggregate.RangeSamples++;
                }
            }
            catch
            {
            }
        }
    }

    private static void PrintSummary(string context)
    {
        if (printedSummary)
            return;

        printedSummary = true;
        try
        {
            List<Ship> ships = RuntimeBattleShips(includeSunk: true);
            if (attackerPlayer == null && ships.Count > 0)
                attackerPlayer = ResolveAttackerPlayer(ships);

            Dictionary<string, WeaponRuntimeAggregate> exitSnapshotWeapons = BuildWeaponAggregates(ships);
            foreach (string side in new[] { "attacker", "defender" })
            {
                SideRuntimeAggregate aggregate = SideAggregates[side];
                Melon<UADVanillaPlusMod>.Logger.Msg(aggregate.Format(sessionSamples, readFailures, sessionSignature, context));

                WeaponRuntimeAggregate weaponAggregate = LiveWeaponAggregates.TryGetValue(side, out WeaponRuntimeAggregate? liveAggregate)
                    ? liveAggregate.Clone()
                    : new WeaponRuntimeAggregate(side);

                if (exitSnapshotWeapons.TryGetValue(side, out WeaponRuntimeAggregate? exitSnapshotAggregate))
                weaponAggregate.MergeMaxFrom(exitSnapshotAggregate);

                Melon<UADVanillaPlusMod>.Logger.Msg(weaponAggregate.Format());

                DamageStateAggregates.TryGetValue(side, out DamageStateRuntimeAggregate? damageAggregate);
                damageAggregate ??= new DamageStateRuntimeAggregate(side);
                Melon<UADVanillaPlusMod>.Logger.Msg(damageAggregate.Format());
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP battle-runtime diagnostics summary failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, WeaponRuntimeAggregate> BuildWeaponAggregates(List<Ship> ships)
    {
        Dictionary<string, WeaponRuntimeAggregate> weapons = new(StringComparer.Ordinal)
        {
            ["attacker"] = new("attacker"),
            ["defender"] = new("defender"),
        };

        foreach (Ship ship in ships)
        {
            string side = SideForShip(ship);
            if (!weapons.TryGetValue(side, out WeaponRuntimeAggregate? aggregate))
                continue;

            try
            {
                var statsByWeapon = ship.statisticsDealtWeaponType;
                if (statsByWeapon == null)
                    continue;

                foreach (var entry in statsByWeapon)
                {
                    PartData? partData = entry.Key;
                    var stats = entry.Value;
                    if (stats == null)
                        continue;

                    WeaponBucket bucket = WeaponBucketFor(ship, partData);
                    aggregate.Add(bucket, Safe(() => stats.shots, 0), Safe(() => stats.hits, 0), Safe(() => stats.damage, 0f));
                }
            }
            catch
            {
                aggregate.ReadFailures++;
                readFailures++;
            }
        }

        return weapons;
    }

    private static void SampleDamageState(Ship ship, DamageStateRuntimeAggregate aggregate)
    {
        try
        {
            aggregate.ShipSamples++;
            aggregate.StructureDamageSum += Safe(() => ship.StructureDamageRatio(), 0f);
            aggregate.FloodingSum += Safe(() => ship.FloodingRatio(), 0f);
            aggregate.ActiveFloodingSum += Safe(() => ship.FloodingRatioActiveFlooding(), 0f);

            if (SafeBool(() => ship.HaveAnyFireOnShip()))
                aggregate.FireShipSamples++;
            if (IsModuleDamaged(ship, "conning_tower"))
                aggregate.ConningDamagedSamples++;
            if (IsModuleDamaged(ship, "fire_control"))
                aggregate.FireControlDamagedSamples++;

            if (TryReadInstabilityDamage(ship, out float instabilityDamage))
            {
                aggregate.DamageInstabilitySum += instabilityDamage;
                aggregate.DamageInstabilitySamples++;
                aggregate.DamageInstabilityMax = Math.Max(aggregate.DamageInstabilityMax, instabilityDamage);
            }
        }
        catch
        {
            aggregate.ReadFailures++;
            readFailures++;
        }
    }

    private static bool CanRecordWeaponEvent()
        => active && ModSettings.BattleRuntimeDiagnosticsEnabled && SafeBool(() => GameManager.IsBattle);

    private static void AddLiveWeaponEvent(Ship? firingShip, PartData? partData, int shots, int hits, float damage)
    {
        if (firingShip == null)
            return;

        if (attackerPlayer == null)
            attackerPlayer = ResolveAttackerPlayer(RuntimeBattleShips(includeSunk: false)) ?? SafePlayer(firingShip);

        string side = SideForShip(firingShip);
        if (!LiveWeaponAggregates.TryGetValue(side, out WeaponRuntimeAggregate? aggregate))
            return;

        aggregate.Add(WeaponBucketFor(firingShip, partData), shots, hits, damage);
    }

    private static WeaponBucket WeaponBucketFor(Ship ship, PartData? partData)
    {
        if (partData == null)
            return WeaponBucket.Other;

        if (MajorShipTorpedoCleanup.IsTorpedoLauncherPartData(partData))
            return WeaponBucket.Torpedo;

        try
        {
            if (ship.IsMainCal(partData))
                return WeaponBucket.Main;
        }
        catch
        {
        }

        try
        {
            if (ship.IsSecondaryCal(partData))
                return WeaponBucket.Secondary;
        }
        catch
        {
        }

        return WeaponBucket.Other;
    }

    private static bool IsModuleDamaged(Ship ship, string moduleKey)
    {
        try
        {
            var modules = ship.modules;
            return modules != null &&
                   modules.TryGetValue(moduleKey, out var module) &&
                   module != null &&
                   module.isDamaged;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadInstabilityDamage(Ship ship, out float value)
    {
        value = 0f;
        if (instabilityDamageReaderDisabled)
            return false;

        if (!TryResolveInstabilityDamageOffset(out int offset))
            return false;

        try
        {
            IntPtr shipPointer = Safe(() => ship.Pointer, IntPtr.Zero);
            if (shipPointer == IntPtr.Zero)
                return false;

            int bits = Marshal.ReadInt32(shipPointer, offset);
            value = BitConverter.Int32BitsToSingle(bits);
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        catch (Exception ex)
        {
            instabilityDamageReaderDisabled = true;
            if (!instabilityDamageReadFailureLogged)
            {
                instabilityDamageReadFailureLogged = true;
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP battle-runtime diagnostics: Ship.instabilityDamage read failed via {instabilityDamageOffsetSource}; damage instability summaries will show n/a. {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private static bool TryResolveInstabilityDamageOffset(out int offset)
    {
        offset = 0;
        if (instabilityDamageOffset.HasValue)
        {
            offset = instabilityDamageOffset.Value;
            return true;
        }

        if (instabilityDamageReaderDisabled)
            return false;

        if (TryResolveNativeInstabilityDamageOffset(out offset, out string source))
        {
            instabilityDamageOffset = offset;
            instabilityDamageOffsetSource = source;
            LogInstabilityDamageResolution(true, offset, source);
            return true;
        }

        offset = InstabilityDamageFallbackOffset;
        instabilityDamageOffset = offset;
        instabilityDamageOffsetSource = "fallback";
        LogInstabilityDamageResolution(true, offset, "fallback");
        return true;
    }

    private static bool TryResolveNativeInstabilityDamageOffset(out int offset, out string source)
    {
        offset = 0;
        source = "unavailable";
        try
        {
            FieldInfo? field = typeof(Ship).GetField(
                    "NativeFieldInfoPtr_instabilityDamage",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                typeof(Ship).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.FieldType == typeof(IntPtr) &&
                                                 candidate.Name.Contains("instabilityDamage", StringComparison.Ordinal));

            if (field == null || field.GetValue(null) is not IntPtr fieldPointer || fieldPointer == IntPtr.Zero)
                return false;

            int nativeOffset = checked((int)IL2CPP.il2cpp_field_get_offset(fieldPointer));
            if (nativeOffset <= 0)
                return false;

            offset = nativeOffset;
            source = field.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LogInstabilityDamageResolution(bool available, int offset, string source)
    {
        if (instabilityDamageResolutionLogged)
            return;

        instabilityDamageResolutionLogged = true;
        if (available)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP battle-runtime diagnostics: resolved Ship.instabilityDamage offset=0x{offset:X} via {source}.");
        }
        else
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                "UADVP battle-runtime diagnostics: Ship.instabilityDamage field unavailable; damage instability summaries will show n/a.");
        }
    }

    private static List<Ship> RuntimeBattleShips(bool includeSunk)
    {
        List<Ship> ships = new();
        try
        {
            if (Ship.AllShips == null)
                return ships;

            foreach (Ship ship in Ship.AllShips)
            {
                if (ship == null ||
                    SafeBool(() => ship.isDesign) ||
                    SafeBool(() => ship.isErased) ||
                    (!includeSunk && SafeBool(() => ship.isSunk)))
                {
                    continue;
                }

                ships.Add(ship);
            }
        }
        catch
        {
            readFailures++;
            ships.Clear();
        }

        return ships;
    }

    private static Player? ResolveAttackerPlayer(List<Ship> ships)
    {
        Player? first = null;
        foreach (Ship ship in ships)
        {
            Player? player = SafePlayer(ship);
            if (player == null)
                continue;

            first ??= player;
            if (SafeBool(() => player.isMain))
                return player;
        }

        return first;
    }

    private static string SideForShip(Ship ship)
    {
        Player? player = SafePlayer(ship);
        if (attackerPlayer == null || SamePlayer(player, attackerPlayer))
            return "attacker";

        return "defender";
    }

    private static void PrunePreviousState(List<Ship> ships)
    {
        HashSet<long> liveShipKeys = new();
        foreach (Ship ship in ships)
        {
            long key = ShipPointer(ship);
            if (key != 0)
                liveShipKeys.Add(key);
        }

        foreach (long key in PreviousShipMotion.Keys.ToList())
        {
            if (!liveShipKeys.Contains(key))
                PreviousShipMotion.Remove(key);
        }

        foreach (string key in PreviousAimState.Keys.ToList())
        {
            int separator = key.IndexOf(':');
            if (separator <= 0 ||
                !long.TryParse(key[..separator], out long shipKey) ||
                !liveShipKeys.Contains(shipKey))
            {
                PreviousAimState.Remove(key);
            }
        }
    }

    private static void ClearSession()
    {
        active = false;
        printedSummary = false;
        nextSampleTime = 0f;
        sessionSamples = 0;
        readFailures = 0;
        sessionContext = "unknown";
        sessionSignature = "pending";
        attackerPlayer = null;
        PreviousShipMotion.Clear();
        PreviousAimState.Clear();
        LoggedSessionMessages.Clear();
        foreach (WeaponRuntimeAggregate aggregate in LiveWeaponAggregates.Values)
            aggregate.Reset();
        foreach (SideRuntimeAggregate aggregate in SideAggregates.Values)
            aggregate.Reset();
        foreach (DamageStateRuntimeAggregate aggregate in DamageStateAggregates.Values)
            aggregate.Reset();
    }

    private static void LogOnce(string key, string message)
    {
        if (LoggedSessionMessages.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Msg(message);
    }

    private static string BattleSignature(List<Ship> ships)
    {
        if (ships.Count == 0)
            return "empty";

        return string.Join(
            "|",
            ships
                .Select(ship => $"{PlayerLabel(SafePlayer(ship))}:{SafeShipType(ship)}:{SafeShipName(ship)}:{ShipIdentity(ship)}")
                .OrderBy(static item => item, StringComparer.Ordinal));
    }

    private static string SafeShipName(Ship? ship)
    {
        if (ship == null)
            return "<ship>";

        try
        {
            string? name = ship.Name(false, false, false, false, true);
            if (!string.IsNullOrWhiteSpace(name))
                return name!;
        }
        catch
        {
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(ship.vesselName))
                return ship.vesselName;
        }
        catch
        {
        }

        return "<ship>";
    }

    private static string SafeShipType(Ship? ship)
        => Safe(() => AiDesignCompetitiveness.NormalizeShipType(ship?.shipType), "?");

    private static Player? SafePlayer(Ship? ship)
        => Safe(() => ship?.player, null);

    private static string PlayerLabel(Player? player)
    {
        try
        {
            return player?.Name(false) ?? "<player>";
        }
        catch
        {
            return "<player>";
        }
    }

    private static string ShipIdentity(Ship? ship)
    {
        if (ship == null)
            return string.Empty;

        string id = Safe(() => ship.id.ToString(), string.Empty);
        if (!string.IsNullOrWhiteSpace(id) && id != "00000000-0000-0000-0000-000000000000")
            return id;

        long pointer = ShipPointer(ship);
        return pointer == 0 ? SafeShipName(ship) : pointer.ToString();
    }

    private static long ShipPointer(Ship? ship)
        => Safe(() => ship?.Pointer.ToInt64() ?? 0L, 0L);

    private static bool SamePlayer(Player? left, Player? right)
    {
        if (left == null || right == null)
            return false;

        try
        {
            return left.Pointer == right.Pointer;
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static string PartToken(PartData? partData)
    {
        if (partData == null)
            return "<part>";

        return FirstNonEmpty(
            Safe(() => partData.name, string.Empty),
            Safe(() => partData.type, string.Empty),
            Safe(() => partData.nameUi, string.Empty),
            "<part>");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value!;
        }

        return string.Empty;
    }

    private static bool SafeBool(Func<bool> action)
        => Safe(action, false);

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch
        {
            return fallback;
        }
    }

    private readonly record struct ShipMotionState(Vector3 Position, float Yaw, float Time);

    private readonly record struct AimRuntimeState(string TargetKey, float Progress);

    private sealed class SideSampleCounts
    {
        internal int Ships;
        internal int AiControlled;
        internal int VpAiControlled;
    }

    private sealed class SideRuntimeAggregate
    {
        private readonly string side;

        internal int SampleTicks;
        internal int LastShipCount;
        internal int LastAiControlled;
        internal int LastVpAiControlled;
        internal int ShipSamples;
        internal int AimSamples;
        internal int HitChanceSamples;
        internal int RangeSamples;
        internal int SpeedSamples;
        internal int TurnSamples;
        internal int TargetChanges;
        internal int AimDrops;
        internal int ReadFailures;
        internal float AimProgressSum;
        internal float HitChanceSum;
        internal float RangeSum;
        internal float SpeedSum;
        internal float TurnDeltaSum;

        internal SideRuntimeAggregate(string side)
        {
            this.side = side;
        }

        internal void Reset()
        {
            SampleTicks = 0;
            LastShipCount = 0;
            LastAiControlled = 0;
            LastVpAiControlled = 0;
            ShipSamples = 0;
            AimSamples = 0;
            HitChanceSamples = 0;
            RangeSamples = 0;
            SpeedSamples = 0;
            TurnSamples = 0;
            TargetChanges = 0;
            AimDrops = 0;
            ReadFailures = 0;
            AimProgressSum = 0f;
            HitChanceSum = 0f;
            RangeSum = 0f;
            SpeedSum = 0f;
            TurnDeltaSum = 0f;
        }

        internal string Format(int sessionSamples, int totalReadFailures, string signature, string context)
            => $"UADVP battle-runtime summary: side={side} ships={LastShipCount} aiControlled={LastAiControlled}/{LastShipCount} vpAi={LastVpAiControlled}/{LastShipCount} samples={SampleTicks}/{sessionSamples} shipSamples={ShipSamples} aimRawAvg={Average(AimProgressSum, AimSamples, "0.00")} aimSamples={AimSamples} hitChanceAvg={HitChanceText()} rangeAvg={RangeText()} targetChanges={TargetChanges} aimDrops={AimDrops} turnDeltaAvg={Average(TurnDeltaSum, TurnSamples, "0.0")}deg/s speedAvg={Average(SpeedSum, SpeedSamples, "0.0")}u/s readFailures={ReadFailures}/{totalReadFailures} context={context} signature={Compact(signature)}";

        private string HitChanceText()
        {
            if (HitChanceSamples <= 0)
                return "n/a";

            float value = HitChanceSum / HitChanceSamples;
            float percent = value <= 1.5f ? value * 100f : value;
            return percent.ToString("0.##") + "%";
        }

        private string RangeText()
        {
            if (RangeSamples <= 0)
                return "n/a";

            float value = RangeSum / RangeSamples;
            float kilometers = value > 100f ? value / 1000f : value;
            return kilometers.ToString("0.##") + "km";
        }

        private static string Average(float sum, int count, string format)
            => count > 0 ? (sum / count).ToString(format) : "n/a";
    }

    private sealed class WeaponRuntimeAggregate
    {
        private readonly string side;
        private readonly WeaponBucketAggregate main = new();
        private readonly WeaponBucketAggregate secondary = new();
        private readonly WeaponBucketAggregate torpedo = new();
        private readonly WeaponBucketAggregate other = new();

        internal int ReadFailures;

        internal WeaponRuntimeAggregate(string side)
        {
            this.side = side;
        }

        internal bool HasData
            => main.HasData || secondary.HasData || torpedo.HasData || other.HasData || ReadFailures > 0;

        internal void Reset()
        {
            main.Reset();
            secondary.Reset();
            torpedo.Reset();
            other.Reset();
            ReadFailures = 0;
        }

        internal void Add(WeaponBucket bucket, int shots, int hits, float damage)
        {
            WeaponBucketAggregate target = bucket switch
            {
                WeaponBucket.Main => main,
                WeaponBucket.Secondary => secondary,
                WeaponBucket.Torpedo => torpedo,
                _ => other,
            };

            target.Shots += Math.Max(0, shots);
            target.Hits += Math.Max(0, hits);
            target.Damage += Math.Max(0f, damage);
        }

        internal WeaponRuntimeAggregate Clone()
        {
            WeaponRuntimeAggregate clone = new(side);
            clone.main.MergeMaxFrom(main);
            clone.secondary.MergeMaxFrom(secondary);
            clone.torpedo.MergeMaxFrom(torpedo);
            clone.other.MergeMaxFrom(other);
            clone.ReadFailures = ReadFailures;
            return clone;
        }

        internal void MergeMaxFrom(WeaponRuntimeAggregate otherAggregate)
        {
            if (!otherAggregate.HasData)
                return;

            main.MergeMaxFrom(otherAggregate.main);
            secondary.MergeMaxFrom(otherAggregate.secondary);
            torpedo.MergeMaxFrom(otherAggregate.torpedo);
            other.MergeMaxFrom(otherAggregate.other);
            ReadFailures = Math.Max(ReadFailures, otherAggregate.ReadFailures);
        }

        internal string Format()
            => $"UADVP battle-runtime weapons: side={side} mainShots={main.Shots} mainHits={main.Hits} mainHitRate={main.HitRateText()} mainDamage={main.Damage:0} secShots={secondary.Shots} secHits={secondary.Hits} secHitRate={secondary.HitRateText()} secDamage={secondary.Damage:0} torpShots={torpedo.Shots} torpHits={torpedo.Hits} torpHitRate={torpedo.HitRateText()} torpDamage={torpedo.Damage:0} otherShots={other.Shots} otherHits={other.Hits} otherDamage={other.Damage:0} readFailures={ReadFailures}";
    }

    private sealed class WeaponBucketAggregate
    {
        internal int Shots;
        internal int Hits;
        internal float Damage;

        internal bool HasData
            => Shots > 0 || Hits > 0 || Damage > 0f;

        internal void Reset()
        {
            Shots = 0;
            Hits = 0;
            Damage = 0f;
        }

        internal void MergeMaxFrom(WeaponBucketAggregate other)
        {
            Shots = Math.Max(Shots, other.Shots);
            Hits = Math.Max(Hits, other.Hits);
            Damage = Math.Max(Damage, other.Damage);
        }

        internal string HitRateText()
            => Shots > 0 ? ((Hits * 100f) / Shots).ToString("0.##") + "%" : "n/a";
    }

    private sealed class DamageStateRuntimeAggregate
    {
        private readonly string side;

        internal int ShipSamples;
        internal int FireShipSamples;
        internal int ConningDamagedSamples;
        internal int FireControlDamagedSamples;
        internal int DamageInstabilitySamples;
        internal int ReadFailures;
        internal float StructureDamageSum;
        internal float FloodingSum;
        internal float ActiveFloodingSum;
        internal float DamageInstabilitySum;
        internal float DamageInstabilityMax;

        internal DamageStateRuntimeAggregate(string side)
        {
            this.side = side;
        }

        internal void Reset()
        {
            ShipSamples = 0;
            FireShipSamples = 0;
            ConningDamagedSamples = 0;
            FireControlDamagedSamples = 0;
            DamageInstabilitySamples = 0;
            ReadFailures = 0;
            StructureDamageSum = 0f;
            FloodingSum = 0f;
            ActiveFloodingSum = 0f;
            DamageInstabilitySum = 0f;
            DamageInstabilityMax = 0f;
        }

        internal string Format()
            => $"UADVP battle-runtime damage-state: side={side} structureDamageAvg={Percent(StructureDamageSum, ShipSamples)} floodingAvg={Percent(FloodingSum, ShipSamples)} activeFloodingAvg={Percent(ActiveFloodingSum, ShipSamples)} fireShipSamples={FireShipSamples}/{ShipSamples} conningDamagedSamples={ConningDamagedSamples}/{ShipSamples} fireControlDamagedSamples={FireControlDamagedSamples}/{ShipSamples} damageInstabilityAvg={Average(DamageInstabilitySum, DamageInstabilitySamples, "0.###")} damageInstabilityMax={DamageInstabilityMax:0.###} samples={ShipSamples} readFailures={ReadFailures}";

        private static string Percent(float sum, int count)
            => count > 0 ? ((sum / count) * 100f).ToString("0.#") + "%" : "n/a";

        private static string Average(float sum, int count, string format)
            => count > 0 ? (sum / count).ToString(format) : "n/a";
    }

    private enum WeaponBucket
    {
        Main,
        Secondary,
        Torpedo,
        Other,
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        string sanitized = value.Replace(' ', '_');
        return sanitized.Length <= 180 ? sanitized : sanitized[..180] + "...";
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnEnterState))]
internal static class BattleRuntimeAccuracyEnterStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleRuntimeAccuracyDiagnostics.EnterBattle("OnEnterState");
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeState), new[] { typeof(GameManager.GameState), typeof(bool) })]
internal static class BattleRuntimeAccuracyChangeStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState newState)
    {
        if (newState == GameManager.GameState.Battle)
            BattleRuntimeAccuracyDiagnostics.EnterBattle("ChangeState");
        else if (newState == GameManager.GameState.CustomBattleSetup)
            BattleRuntimeAccuracyDiagnostics.LeaveBattle("ChangeState CustomBattleSetup");
    }
}

[HarmonyPatch(typeof(Ui), "UpdateBattle")]
internal static class BattleRuntimeAccuracyUpdateBattlePatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => BattleRuntimeAccuracyDiagnostics.UpdateBattle();
}

[HarmonyPatch(typeof(Ui), nameof(Ui.RegisterMadeShot))]
internal static class BattleRuntimeAccuracyMadeShotPatch
{
    [HarmonyPostfix]
    private static void Postfix(Part from, int shots)
        => BattleRuntimeAccuracyDiagnostics.RecordMadeShot(from, shots);
}

[HarmonyPatch(typeof(Ui), nameof(Ui.RegisterTakenDamage))]
internal static class BattleRuntimeAccuracyTakenDamagePatch
{
    [HarmonyPostfix]
    private static void Postfix(Part from, float damage)
        => BattleRuntimeAccuracyDiagnostics.RecordTakenDamage(from, damage);
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.LeaveBattle))]
internal static class BattleRuntimeAccuracyLeaveBattlePatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => BattleRuntimeAccuracyDiagnostics.LeaveBattle("LeaveBattle");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnLeaveState))]
internal static class BattleRuntimeAccuracyLeaveStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleRuntimeAccuracyDiagnostics.LeaveBattle("OnLeaveState");
    }
}
