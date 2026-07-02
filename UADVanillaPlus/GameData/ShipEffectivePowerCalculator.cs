using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.Harmony;

namespace UADVanillaPlus.GameData;

internal readonly record struct EffectivePowerResult(
    string Type,
    float BasePower,
    float AdjustedPower,
    float BenchmarkAdjustedPower,
    float CompetitivenessRatio,
    float QualityMultiplier,
    float GunQualityMultiplier,
    float GunReloadFactor,
    float GunIntrinsicAccuracyFactor,
    float ShipAccuracyFactor,
    float GunRangeFactor,
    float TorpedoThreatFactor,
    float TorpedoRangeFactor,
    float TorpedoSpeedFactor,
    float TorpedoReloadFactor,
    float TorpedoAccuracyFactor,
    float WeightedGunReloadScore,
    float WeightedGunRangeScore,
    float WeightedGunAccuracyScore,
    float ShipAccuracyScore,
    float WeightedTorpedoRange,
    float WeightedTorpedoSpeed,
    float WeightedTorpedoReloadScore,
    float WeightedTorpedoAccuracyScore,
    int GunGroupCount,
    int TorpedoTubeCount,
    bool Success,
    string Reason);

internal readonly record struct EffectivePowerBenchmark(
    string Type,
    float BasePower,
    float AdjustedPower,
    int SampleCount,
    string TopDesignLabels,
    EffectivePowerMetrics Metrics);

internal readonly record struct EffectivePowerMetrics(
    string Type,
    float BasePower,
    float WeightedGunReloadScore,
    float WeightedGunRangeScore,
    float WeightedGunAccuracyScore,
    float ShipAccuracyScore,
    float WeightedTorpedoRange,
    float WeightedTorpedoSpeed,
    float WeightedTorpedoReloadScore,
    float WeightedTorpedoAccuracyScore,
    int GunGroupCount,
    int TorpedoTubeCount,
    bool HasTorpedoes,
    string Label);

internal static class ShipEffectivePowerCalculator
{
    private static readonly HashSet<string> SurfaceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BB",
        "BC",
        "CA",
        "CL",
        "DD",
        "TB"
    };

    private static readonly Dictionary<string, EffectivePowerBenchmark> BenchmarkByType = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, EffectivePowerMetrics> MetricsByDesign = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.OrdinalIgnoreCase);
    private static int CachedBenchmarkTurn = int.MinValue;

    internal static EffectivePowerResult Calculate(Ship? design, EffectivePowerBenchmark? benchmark = null)
    {
        if (design == null)
            return EmptyResult("unknown", "missingDesign");

        try
        {
            EffectivePowerMetrics metrics = MetricsForDesign(design);
            EffectivePowerBenchmark resolvedBenchmark = benchmark ?? BenchmarkForType(metrics.Type);
            return Calculate(metrics, resolvedBenchmark);
        }
        catch (Exception ex)
        {
            LogFailureOnce("calculate:" + ex.GetType().Name, $"UADVP effective power: failed to calculate design power: {ex.Message}");
            string type = NormalizeShipType(design);
            float basePower = EstimateBasePower(design);
            return new EffectivePowerResult(
                type,
                basePower,
                basePower,
                0f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                0f,
                0f,
                0f,
                0f,
                0,
                0,
                false,
                "exception");
        }
    }

    internal static EffectivePowerBenchmark BenchmarkForType(string type)
    {
        EnsureBenchmarksCurrent();
        type = NormalizeShipType(type);
        if (BenchmarkByType.TryGetValue(type, out EffectivePowerBenchmark benchmark))
            return benchmark;

        return NeutralBenchmark(type);
    }

    internal static IReadOnlyCollection<EffectivePowerBenchmark> CurrentBenchmarks()
    {
        EnsureBenchmarksCurrent();
        return BenchmarkByType.Values.ToArray();
    }

    internal static void Invalidate(Ship? design)
    {
        try
        {
            if (design != null)
                MetricsByDesign.Remove(DesignIdentityKey(design));
        }
        catch
        {
        }

        MetricsByDesign.Clear();
        BenchmarkByType.Clear();
        CachedBenchmarkTurn = int.MinValue;
    }

    internal static bool IsSurfaceType(string type)
    {
        return SurfaceTypes.Contains(NormalizeShipType(type));
    }

    internal static string NormalizeShipType(Ship? ship)
    {
        try
        {
            return NormalizeShipType(ship?.shipType?.name);
        }
        catch
        {
            return "unknown";
        }
    }

    internal static string NormalizeShipType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "unknown";

        string compact = type.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
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

    internal static int CurrentTurnIndex()
    {
        try
        {
            int turn = CampaignController.Instance?.CurrentDate.turn ?? -1;
            if (turn >= 0)
                return turn;
        }
        catch
        {
        }

        return 0;
    }

    internal static string CurrentTurnLabel()
    {
        try
        {
            CampaignController? campaign = CampaignController.Instance;
            if (campaign?.CurrentDate != null)
                return campaign.CurrentDate.ToString(false);
        }
        catch
        {
        }

        return CurrentTurnIndex().ToString(CultureInfo.InvariantCulture);
    }

    internal static string FormatCompactPower(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            return "-";

        if (value >= 1_000_000f)
            return FormatScaled(value / 1_000_000f, "M");

        if (value >= 100_000f)
            return $"{Math.Round(value / 1_000f):0}k";

        if (value >= 10_000f)
            return $"{Math.Round(value / 1_000f):0.#}k";

        if (value >= 1_000f)
            return $"{Math.Round(value / 1_000f, 1):0.#}k";

        return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
    }

    internal static string FormatRatio(float ratio)
    {
        if (float.IsNaN(ratio) || float.IsInfinity(ratio) || ratio <= 0f)
            return "-";

        return $"{ratio * 100f:0.0}%";
    }

    internal static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "unknown";

        string nation = PlayerLabel(ship.player);
        string type = NormalizeShipType(ship);
        string name = SafeShipName(ship);
        return string.IsNullOrEmpty(nation) ? $"{type}/{name}" : $"{nation}/{name}";
    }

    internal static string PlayerLabel(Player? player)
    {
        if (player == null)
            return string.Empty;

        try
        {
            string? dataName = player.data?.name;
            if (!string.IsNullOrWhiteSpace(dataName))
                return dataName!;
        }
        catch
        {
        }

        try
        {
            string? name = player.Name(false);
            if (!string.IsNullOrWhiteSpace(name))
                return name!;
        }
        catch
        {
        }

        return string.Empty;
    }

    private static EffectivePowerResult Calculate(EffectivePowerMetrics metrics, EffectivePowerBenchmark benchmark)
    {
        float gunReloadFactor = Factor(metrics.WeightedGunReloadScore, benchmark.Metrics.WeightedGunReloadScore, 0.35f, 0.80f, 1.25f);
        float gunIntrinsicAccuracyFactor = Factor(metrics.WeightedGunAccuracyScore, benchmark.Metrics.WeightedGunAccuracyScore, 0.50f, 0.75f, 1.35f);
        float shipAccuracyFactor = Factor(metrics.ShipAccuracyScore, benchmark.Metrics.ShipAccuracyScore, 0.40f, 0.85f, 1.20f);
        float gunRangeFactor = Factor(metrics.WeightedGunRangeScore, benchmark.Metrics.WeightedGunRangeScore, 0.20f, 0.90f, 1.15f);
        float gunQualityMultiplier = Clamp(gunReloadFactor * gunIntrinsicAccuracyFactor * shipAccuracyFactor * gunRangeFactor, 0.65f, 1.75f);

        float torpedoRangeFactor = 1f;
        float torpedoSpeedFactor = 1f;
        float torpedoReloadFactor = 1f;
        float torpedoAccuracyFactor = 1f;
        float torpedoThreatFactor = 1f;

        if (metrics.HasTorpedoes && metrics.TorpedoTubeCount > 0)
        {
            torpedoRangeFactor = Factor(metrics.WeightedTorpedoRange, benchmark.Metrics.WeightedTorpedoRange, 0.45f, 0.70f, 1.60f);
            torpedoSpeedFactor = Factor(metrics.WeightedTorpedoSpeed, benchmark.Metrics.WeightedTorpedoSpeed, 0.35f, 0.75f, 1.40f);
            torpedoReloadFactor = Factor(metrics.WeightedTorpedoReloadScore, benchmark.Metrics.WeightedTorpedoReloadScore, 0.25f, 0.85f, 1.25f);
            torpedoAccuracyFactor = Factor(metrics.WeightedTorpedoAccuracyScore, benchmark.Metrics.WeightedTorpedoAccuracyScore, 0.25f, 0.85f, 1.25f);
            float torpedoQuality = Clamp(torpedoRangeFactor * torpedoSpeedFactor * torpedoReloadFactor * torpedoAccuracyFactor, 0.60f, 1.90f);
            float influence = TorpedoInfluence(metrics.Type);
            torpedoThreatFactor = 1f + (influence * (torpedoQuality - 1f));
            (float min, float max) = TorpedoThreatClamp(metrics.Type);
            torpedoThreatFactor = Clamp(torpedoThreatFactor, min, max);
        }

        float adjustedPower = Math.Max(0f, metrics.BasePower * gunQualityMultiplier * torpedoThreatFactor);
        float benchmarkPower = benchmark.AdjustedPower > 0f ? benchmark.AdjustedPower : 0f;
        float ratio = benchmarkPower > 0f ? adjustedPower / benchmarkPower : 1f;

        return new EffectivePowerResult(
            metrics.Type,
            metrics.BasePower,
            adjustedPower,
            benchmarkPower,
            ratio,
            gunQualityMultiplier * torpedoThreatFactor,
            gunQualityMultiplier,
            gunReloadFactor,
            gunIntrinsicAccuracyFactor,
            shipAccuracyFactor,
            gunRangeFactor,
            torpedoThreatFactor,
            torpedoRangeFactor,
            torpedoSpeedFactor,
            torpedoReloadFactor,
            torpedoAccuracyFactor,
            metrics.WeightedGunReloadScore,
            metrics.WeightedGunRangeScore,
            metrics.WeightedGunAccuracyScore,
            metrics.ShipAccuracyScore,
            metrics.WeightedTorpedoRange,
            metrics.WeightedTorpedoSpeed,
            metrics.WeightedTorpedoReloadScore,
            metrics.WeightedTorpedoAccuracyScore,
            metrics.GunGroupCount,
            metrics.TorpedoTubeCount,
            true,
            benchmark.SampleCount > 0 ? "ok" : "noBenchmark");
    }

    private static void EnsureBenchmarksCurrent()
    {
        int turn = CurrentTurnIndex();
        if (CachedBenchmarkTurn == turn)
            return;

        CachedBenchmarkTurn = turn;
        BenchmarkByType.Clear();
        MetricsByDesign.Clear();

        try
        {
            Dictionary<string, List<EffectivePowerMetrics>> byType = new(StringComparer.OrdinalIgnoreCase);
            foreach (Player player in CampaignPlayers())
            {
                if (!IsBenchmarkPlayer(player))
                    continue;

                foreach (Ship design in PlayerDesigns(player))
                {
                    EffectivePowerMetrics metrics = BuildMetrics(design);
                    if (!SurfaceTypes.Contains(metrics.Type) || metrics.BasePower <= 0f)
                        continue;

                    if (!byType.TryGetValue(metrics.Type, out List<EffectivePowerMetrics>? list))
                    {
                        list = new List<EffectivePowerMetrics>();
                        byType[metrics.Type] = list;
                    }

                    list.Add(metrics);
                }
            }

            foreach ((string type, List<EffectivePowerMetrics> designs) in byType)
            {
                List<EffectivePowerMetrics> top = designs
                    .OrderByDescending(static item => item.BasePower)
                    .Take(3)
                    .ToList();

                if (top.Count == 0)
                    continue;

                EffectivePowerMetrics averageMetrics = AverageMetrics(type, top);
                EffectivePowerBenchmark provisional = new(
                    type,
                    top.Average(static item => item.BasePower),
                    0f,
                    top.Count,
                    string.Join(",", top.Select(static item => item.Label)),
                    averageMetrics);

                float adjustedAverage = top.Average(item => Calculate(item, provisional).AdjustedPower);
                BenchmarkByType[type] = provisional with { AdjustedPower = adjustedAverage };
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("benchmark:" + ex.GetType().Name, $"UADVP effective power: failed to refresh benchmarks: {ex.Message}");
        }
    }

    private static EffectivePowerBenchmark NeutralBenchmark(string type)
    {
        EffectivePowerMetrics metrics = new(
            NormalizeShipType(type),
            0f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            0,
            0,
            false,
            string.Empty);

        return new EffectivePowerBenchmark(metrics.Type, 0f, 0f, 0, string.Empty, metrics);
    }

    private static EffectivePowerResult EmptyResult(string type, string reason)
    {
        return new EffectivePowerResult(
            type,
            0f,
            0f,
            0f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            1f,
            0f,
            0f,
            0f,
            0f,
            0,
            0,
            false,
            reason);
    }

    private static EffectivePowerMetrics MetricsForDesign(Ship design)
    {
        string key = DesignKey(design);
        if (MetricsByDesign.TryGetValue(key, out EffectivePowerMetrics cached))
            return cached;

        EffectivePowerMetrics metrics = BuildMetrics(design);
        MetricsByDesign[key] = metrics;
        return metrics;
    }

    private static EffectivePowerMetrics BuildMetrics(Ship design)
    {
        string type = NormalizeShipType(design);
        float basePower = EstimateBasePower(design);

        GunProfile guns = BuildGunProfile(design);
        TorpedoProfile torpedoes = BuildTorpedoProfile(design);
        LogTorpedoAuditMismatchIfAny(design, torpedoes);

        return new EffectivePowerMetrics(
            type,
            basePower,
            guns.ReloadScore,
            guns.RangeScore,
            guns.IntrinsicAccuracyScore,
            guns.ShipAccuracyScore,
            torpedoes.RangeScore,
            torpedoes.SpeedScore,
            torpedoes.ReloadScore,
            torpedoes.AccuracyScore,
            guns.GroupCount,
            torpedoes.TubeCount,
            torpedoes.TubeCount > 0,
            ShipLabel(design));
    }

    private static EffectivePowerMetrics AverageMetrics(string type, List<EffectivePowerMetrics> metrics)
    {
        return new EffectivePowerMetrics(
            NormalizeShipType(type),
            metrics.Average(static item => item.BasePower),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedGunReloadScore)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedGunRangeScore)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedGunAccuracyScore)),
            metrics.Average(static item => PositiveOrNeutral(item.ShipAccuracyScore)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedTorpedoRange)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedTorpedoSpeed)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedTorpedoReloadScore)),
            metrics.Average(static item => PositiveOrNeutral(item.WeightedTorpedoAccuracyScore)),
            (int)Math.Round(metrics.Average(static item => item.GunGroupCount)),
            (int)Math.Round(metrics.Average(static item => item.TorpedoTubeCount)),
            metrics.Any(static item => item.HasTorpedoes),
            string.Empty);
    }

    // VP throw-weight power model — replaces vanilla Ship.EstimatePower as the display
    // BasePower. Vanilla badly misranks ships: confirmed on a live fleet, light fast-firing
    // cruisers read HIGHER than every capital ship (a 13.5k-ton CL at ~1.9M vs a 33.6k-ton
    // BB with the most guns at ~480k). This model scores by per-salvo throw-weight (the
    // game's own per-shell damage dmgMod, which ≈ caliber^3) times a hull survivability/
    // presence term, so capital ships dominate and "fewer, bigger guns" RAISES power.
    // Influence (Ship.PowerProjection) is a separate native formula, already ranks
    // capitals > cruisers > DDs, and is intentionally left UNTOUCHED. See memory:
    // uadvp-ship-power-pwr. Constants are tuned to keep the display near the familiar
    // ~1-2M range for capitals; adjust and compare against vanillaEstimate in the probe log.
    internal static bool UseThrowWeightModel = true;
    internal static float ModelScale = 1.0f;       // overall scale knob (tune so a strong BB reads ~1-1.5M)
    internal static float ModelHullWeight = 3f;    // gunless/light-ship offense floor (throw-weight units)
    internal static float ModelRofExponent = 0f;   // rate-of-fire credit on offense: 0 = pure per-salvo throw-weight
    internal static float ModelOffenseExp = 0.5f;  // <1 compresses raw gun throw-weight so guns don't dominate
    internal static float ModelArmorFloor = 50f;   // base durability for an unarmored hull (so DDs aren't ~0)

    private static float EstimateBasePower(Ship design)
    {
        if (UseThrowWeightModel)
        {
            try
            {
                return ComputeModelPower(design);
            }
            catch (Exception ex)
            {
                LogFailureOnce("model:" + ex.GetType().Name, $"UADVP effective power: throw-weight model failed, using vanilla: {ex.Message}");
            }
        }

        return VanillaEstimatePower(design);
    }

    // Capability = compressed gun throw-weight (offense) x hull-size-and-armor (durability).
    // Offense^0.5 keeps guns from dominating; durability = sqrt(tonnage) x (floor + armor) so
    // protection/up-armor refits register and tougher ships rate higher. dmgMod already encodes
    // gun/shell tech grade, so gun-tech upgrades show up via offense; armor upgrades via durability.
    private static float ComputeModelPower(Ship design)
    {
        float offense = ModelHullWeight;
        foreach (GunGroupDiagnostic g in DescribeGunGroups(design))
        {
            float salvo = g.Count * g.Barrels * Math.Max(0.01f, g.DamageMod);
            if (ModelRofExponent > 0f)
            {
                float rpm = g.Reload > 0f ? 60f / g.Reload : 0f;
                salvo *= (float)Math.Pow(Math.Max(0.01f, rpm), ModelRofExponent);
            }

            offense += salvo;
        }

        float tonnage = Math.Max(1f, SafeFloat(() => design.tonnage, 1f));
        // Only Belt + Deck are reliable: the Barbette (and some Turret) zones carry garbage
        // sentinel values on light/fast hulls (e.g. barbette=29337mm), which would blow up
        // durability and put cruisers back above capitals. Belt + deck read clean on all hulls.
        (float belt, float deck, _, _) = ArmorZones(design);
        float protection = belt + (0.7f * deck);
        float durability = (float)Math.Sqrt(tonnage) * (ModelArmorFloor + protection);

        return ModelScale * (float)Math.Pow(Math.Max(0.01f, offense), ModelOffenseExp) * durability;
    }

    private static float VanillaEstimatePower(Ship design)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<Ship> ships = new();
            ships.Add(design);
            return Ship.EstimatePower(ships.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Ship>>(), false);
        }
        catch (Exception ex)
        {
            LogFailureOnce("base:" + ex.GetType().Name, $"UADVP effective power: vanilla EstimatePower failed: {ex.Message}");
            return 0f;
        }
    }

    // ship.armor is a per-zone Dictionary<Ship.A, float> (values in mm). Read the citadel
    // zones we care about by key — light hulls carry garbage values in inner/superstructure
    // zones, so do NOT max/avg across the whole dictionary.
    private static (float Belt, float Deck, float Turret, float Barbette) ArmorZones(Ship ship)
    {
        float belt = 0f, deck = 0f, turret = 0f, barbette = 0f;
        try
        {
            var armor = ship.armor;
            if (armor != null)
            {
                foreach (var kv in armor)
                {
                    float v = kv.Value;
                    if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0f)
                        continue;

                    switch (kv.Key)
                    {
                        case Ship.A.Belt: belt = v; break;
                        case Ship.A.Deck: deck = v; break;
                        case Ship.A.TurretTop: turret = v; break;
                        case Ship.A.Barbette: barbette = v; break;
                    }
                }
            }
        }
        catch
        {
        }

        return (belt, deck, turret, barbette);
    }

    // Enumerates each gun group with the fields the power model needs (caliber, barrels,
    // count, reload, range, throw-weight, dmgMod).
    internal static IReadOnlyList<GunGroupDiagnostic> DescribeGunGroups(Ship design)
    {
        List<GunGroupDiagnostic> rows = new();
        if (design == null)
            return rows;

        try
        {
            foreach ((PartData data, int count) in GunGroups(design))
            {
                if (data == null || count <= 0)
                    continue;

                int barrels = Math.Max(1, SafeInt(() => data.barrels, 1));
                float caliberInches = CaliberInches(SafeFloat(() => data.caliber, 1f));
                float reload = SafeWeaponReload(data, design);
                float range = GunRange(design, data);
                float weight = GunWeight(design, data, count);

                float damageMod = 1f;
                GunData? gunData = GetGunData(design, data);
                if (gunData != null)
                    damageMod = Math.Max(0.25f, SafeFloat(() => gunData.DamageMod(design, data), SafeFloat(() => gunData.damageMod, 1f)));

                string name = SafeString(() => data.nameUi);
                if (string.IsNullOrWhiteSpace(name))
                    name = SafeString(() => data.name);
                if (string.IsNullOrWhiteSpace(name))
                    name = "gun";

                rows.Add(new GunGroupDiagnostic(name, caliberInches, barrels, count, reload, range, weight, damageMod));
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("describe:guns:" + ex.GetType().Name, $"UADVP power probe: gun-group describe failed: {ex.Message}");
        }

        return rows;
    }

    private static GunProfile BuildGunProfile(Ship ship)
    {
        float totalWeight = 0f;
        float reloadScore = 0f;
        float rangeScore = 0f;
        float accuracyScore = 0f;
        int groupCount = 0;

        foreach ((PartData data, int count) in GunGroups(ship))
        {
            if (data == null || count <= 0)
                continue;

            float weight = GunWeight(ship, data, count);
            if (weight <= 0f)
                continue;

            totalWeight += weight;
            groupCount++;

            float reload = SafeWeaponReload(data, ship);
            float range = GunRange(ship, data);
            float intrinsicAccuracy = GunIntrinsicAccuracy(ship, data);

            reloadScore += weight * (1f / Math.Max(1f, reload));
            rangeScore += weight * Math.Max(1f, range);
            accuracyScore += weight * Math.Max(0.01f, intrinsicAccuracy);
        }

        float shipAccuracy = ShipAccuracyScore(ship);
        if (totalWeight <= 0f)
            return new GunProfile(1f, 1f, 1f, shipAccuracy, 0);

        return new GunProfile(
            reloadScore / totalWeight,
            rangeScore / totalWeight,
            accuracyScore / totalWeight,
            shipAccuracy,
            groupCount);
    }

    private static TorpedoProfile BuildTorpedoProfile(Ship ship)
    {
        float totalWeight = 0f;
        float rangeScore = 0f;
        float speedScore = 0f;
        float reloadScore = 0f;
        float accuracyScore = 0f;
        int tubeCount = 0;

        foreach ((PartData data, int count) in TorpedoGroups(ship))
        {
            if (data == null || count <= 0)
                continue;

            int barrels = Math.Max(1, SafeInt(() => data.barrels, 1));
            float weight = Math.Max(1, count * barrels);
            totalWeight += weight;
            tubeCount += Math.Max(1, count * barrels);

            float range = TorpedoRange(ship, data);
            float speed = SafeFloat(() => ship.TorpedoSpeed(true), 0f);
            float reload = SafeWeaponReload(data, ship);
            float accuracy = TorpedoAccuracy(ship, data);

            rangeScore += weight * Math.Max(1f, range);
            speedScore += weight * Math.Max(1f, speed);
            reloadScore += weight * (1f / Math.Max(1f, reload));
            accuracyScore += weight * Math.Max(0.01f, accuracy);
        }

        if (totalWeight <= 0f)
            return new TorpedoProfile(1f, 1f, 1f, 1f, 0);

        return new TorpedoProfile(
            rangeScore / totalWeight,
            speedScore / totalWeight,
            reloadScore / totalWeight,
            accuracyScore / totalWeight,
            tubeCount);
    }

    private static IEnumerable<(PartData Data, int Count)> GunGroups(Ship ship)
    {
        List<(PartData Data, int Count)> result = new();
        HashSet<IntPtr> seen = new();

        try
        {
            if (ship.gunGroups != null)
            {
                foreach (var pair in ship.gunGroups)
                {
                    PartData data = pair.Key;
                    if (data == null || !SafeBool(() => data.isGun))
                        continue;

                    int count = Math.Max(1, pair.Value?.Count ?? 1);
                    seen.Add(data.Pointer);
                    result.Add((data, count));
                }
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("guns:groups:" + ex.GetType().Name, $"UADVP effective power: gun group scan failed: {ex.Message}");
        }

        Dictionary<IntPtr, (PartData Data, int Count)> fallback = new();
        try
        {
            if (ship.parts != null)
            {
                foreach (Part part in ship.parts)
                {
                    if (part == null)
                        continue;

                    PartData data = part.data;
                    if (data == null || seen.Contains(data.Pointer) || !SafeBool(() => data.isGun))
                        continue;

                    fallback.TryGetValue(data.Pointer, out (PartData Data, int Count) existing);
                    fallback[data.Pointer] = (data, existing.Count + 1);
                }
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("guns:parts:" + ex.GetType().Name, $"UADVP effective power: gun part scan failed: {ex.Message}");
        }

        foreach ((PartData data, int count) in fallback.Values)
            result.Add((data, count));

        return result;
    }

    private static IEnumerable<(PartData Data, int Count)> TorpedoGroups(Ship ship)
    {
        Dictionary<IntPtr, (PartData Data, int Count)> groups = new();

        try
        {
            if (ship.torpedoesAll != null)
            {
                foreach (Part part in ship.torpedoesAll)
                {
                    if (part == null)
                        continue;

                    PartData data = part.data;
                    if (!MajorShipTorpedoCleanup.IsTorpedoLauncherPartData(data))
                        continue;

                    groups.TryGetValue(data.Pointer, out (PartData Data, int Count) existing);
                    groups[data.Pointer] = (data, existing.Count + 1);
                }
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("torps:list:" + ex.GetType().Name, $"UADVP effective power: torpedo list scan failed: {ex.Message}");
        }

        if (groups.Count == 0)
        {
            try
            {
                if (ship.parts != null)
                {
                    foreach (Part part in ship.parts)
                    {
                        if (part == null)
                            continue;

                        PartData data = part.data;
                        if (!MajorShipTorpedoCleanup.IsTorpedoLauncherPartData(data))
                            continue;

                        groups.TryGetValue(data.Pointer, out (PartData Data, int Count) existing);
                        groups[data.Pointer] = (data, existing.Count + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailureOnce("torps:parts:" + ex.GetType().Name, $"UADVP effective power: torpedo part scan failed: {ex.Message}");
            }
        }

        foreach ((PartData data, int count) in groups.Values)
            yield return (data, count);
    }

    private static float GunWeight(Ship ship, PartData data, int count)
    {
        int barrels = Math.Max(1, SafeInt(() => data.barrels, 1));
        float caliber = CaliberInches(SafeFloat(() => data.caliber, 1f));
        float damageMod = 1f;
        GunData? gunData = GetGunData(ship, data);
        if (gunData != null)
            damageMod = Math.Max(0.25f, SafeFloat(() => gunData.DamageMod(ship, data), SafeFloat(() => gunData.damageMod, 1f)));

        return Math.Max(0.01f, count * barrels * caliber * caliber * damageMod);
    }

    private static float GunIntrinsicAccuracy(Ship ship, PartData data)
    {
        GunData? gunData = GetGunData(ship, data);
        if (gunData == null)
            return 1f;

        int grade = ClampGrade(SafeInt(() => ship.TechGunGrade(data, true), 1));
        float accuracy = Math.Max(0.01f, GunAccuracy(gunData, grade));
        float hitChanceMult = Math.Max(0.10f, SafeFloat(() => gunData.HitChanceMult(ship, data), SafeFloat(() => gunData.hitChanceMult, 1f)));
        float curver = Math.Max(0.10f, SafeFloat(() => gunData.HitChanceCurver(ship, data), SafeFloat(() => gunData.hitChanceCurver, 1f)));
        float maxRangeQuality = Math.Max(0.10f, SafeFloat(() => gunData.HitChanceMaxRange(ship, data), SafeFloat(() => gunData.hitChanceMaxRange, 1f)));

        float bandScore = 0f;
        bandScore += 0.35f * RangeBandAccuracy(0.50f, curver);
        bandScore += 0.45f * RangeBandAccuracy(0.75f, curver);
        bandScore += 0.20f * RangeBandAccuracy(1.00f, curver);

        return accuracy * hitChanceMult * bandScore * Math.Max(0.25f, maxRangeQuality);
    }

    private static float RangeBandAccuracy(float band, float curver)
    {
        float remaining = Math.Max(0.05f, 1f - (band * 0.65f));
        float exponent = Clamp(curver * 0.10f, 0.10f, 2.50f);
        return (float)Math.Pow(remaining, exponent);
    }

    private static float ShipAccuracyScore(Ship ship)
    {
        float accuracy = SafeFloat(() => ship.StatEffect("accuracy"), 0f);
        float longAccuracy = SafeFloat(() => ship.StatEffect("accuracy_long"), 0f);
        return Clamp(1f + ((accuracy + longAccuracy) / 100f), 0.10f, 3f);
    }

    private static float TorpedoAccuracy(Ship ship, PartData data)
    {
        TorpedosData? torpData = GetTorpedoData(data);
        if (torpData == null)
            return 1f;

        int grade = ClampGrade(SafeInt(() => ship.TechTorpedoGrade(data, true), 1));
        if (torpData.torpedosAccuracy != null && torpData.torpedosAccuracy.TryGetValue(grade, out float fromDictionary))
            return Math.Max(0.01f, fromDictionary);

        return grade switch
        {
            1 => Math.Max(0.01f, SafeFloat(() => torpData.torpAcc_1, 1f)),
            2 => Math.Max(0.01f, SafeFloat(() => torpData.torpAcc_2, 1f)),
            3 => Math.Max(0.01f, SafeFloat(() => torpData.torpAcc_3, 1f)),
            4 => Math.Max(0.01f, SafeFloat(() => torpData.torpAcc_4, 1f)),
            _ => Math.Max(0.01f, SafeFloat(() => torpData.torpAcc_5, 1f))
        };
    }

    private static GunData? GetGunData(Ship ship, PartData data)
    {
        try
        {
            string id = data.GetGunDataId(ship);
            if (!string.IsNullOrWhiteSpace(id) && G.GameData?.guns != null && G.GameData.guns.TryGetValue(id, out GunData gunData))
                return gunData;
        }
        catch (Exception ex)
        {
            LogFailureOnce("gundata:" + ex.GetType().Name, $"UADVP effective power: gun data lookup failed: {ex.Message}");
        }

        return null;
    }

    private static TorpedosData? GetTorpedoData(PartData data)
    {
        try
        {
            if (G.GameData?.torpedoTubes == null)
                return null;

            foreach (string? key in new[] { SafeString(() => data.name), SafeString(() => data.type), SafeString(() => data.param), SafeString(() => data.group) })
            {
                if (!string.IsNullOrWhiteSpace(key) && G.GameData.torpedoTubes.TryGetValue(key, out TorpedosData torpData))
                    return torpData;
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce("torpdata:" + ex.GetType().Name, $"UADVP effective power: torpedo data lookup failed: {ex.Message}");
        }

        return null;
    }

    private static float GunAccuracy(GunData gunData, int grade)
    {
        if (gunData.accuracies != null && gunData.accuracies.TryGetValue(grade, out float fromDictionary))
            return fromDictionary;

        return grade switch
        {
            1 => SafeFloat(() => gunData.accuracy_1, 1f),
            2 => SafeFloat(() => gunData.accuracy_2, 1f),
            3 => SafeFloat(() => gunData.accuracy_3, 1f),
            4 => SafeFloat(() => gunData.accuracy_4, 1f),
            _ => SafeFloat(() => gunData.accuracy_5, 1f)
        };
    }

    private static float SafeWeaponReload(PartData data, Ship ship)
    {
        return Math.Max(1f, SafeFloat(() => Part.WeaponReloadTime(data, ship), 1f));
    }

    private static float SafeWeaponRange(PartData data, Ship ship)
    {
        return Math.Max(1f, SafeFloat(() => Part.WeaponRange(data, ship, null), 1f));
    }

    private static float GunRange(Ship ship, PartData data)
    {
        GunData? gunData = GetGunData(ship, data);
        int grade = ClampGrade(SafeInt(() => ship.TechGunGrade(data, true), 1));
        if (gunData != null)
        {
            float direct = SafeFloat(() => gunData.Range(ship, data, grade), 0f);
            if (direct > 1f)
                return direct;

            float fromLoadedGrade = GunRangeFromLoadedData(gunData, grade);
            if (fromLoadedGrade > 1f)
                return fromLoadedGrade;
        }

        float fallback = SafeWeaponRange(data, ship);
        if (fallback <= 1f)
            LogFailureOnce("range:gun:fallback", "UADVP effective power: gun range could not be resolved from GunData for at least one design; using neutral fallback.");

        return fallback;
    }

    private static float GunRangeFromLoadedData(GunData gunData, int grade)
    {
        if (gunData.ranges != null && gunData.ranges.TryGetValue(grade, out float fromDictionary) && fromDictionary > 1f)
            return fromDictionary;

        return grade switch
        {
            1 => SafeFloat(() => gunData.range_1, 0f),
            2 => SafeFloat(() => gunData.range_2, 0f),
            3 => SafeFloat(() => gunData.range_3, 0f),
            4 => SafeFloat(() => gunData.range_4, 0f),
            _ => SafeFloat(() => gunData.range_5, 0f)
        };
    }

    private static float TorpedoRange(Ship ship, PartData data)
    {
        float weaponRange = SafeWeaponRange(data, ship);
        if (weaponRange > 1f)
            return weaponRange;

        float techRange = SafeFloat(() => ship.TechMax("torpedo_range", 0f), 0f);
        if (techRange > 1f)
        {
            float techRangeMod = SafeFloat(() => ship.TechSum("torpedo_range_mod"), 0f);
            return techRange * NormalizeTechAdditiveMultiplier(techRangeMod);
        }

        LogFailureOnce("range:torpedo:fallback", "UADVP effective power: torpedo range could not be resolved from weapon or tech-effect data for at least one design; using neutral fallback.");
        return 1f;
    }

    private static float NormalizeTechAdditiveMultiplier(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 1f;

        return Clamp(1f + (value / 100f), 0.10f, 3f);
    }

    private static IEnumerable<Player> CampaignPlayers()
    {
        HashSet<IntPtr> seen = new();
        Player? mainPlayer = ExtraGameData.MainPlayer();
        if (mainPlayer != null && seen.Add(mainPlayer.Pointer))
            yield return mainPlayer;

        var players = CampaignController.Instance?.CampaignData?.PlayersMajor;
        if (players == null)
            yield break;

        try
        {
            foreach (Player player in players)
            {
                if (player != null && seen.Add(player.Pointer))
                    yield return player;
            }
        }
        finally
        {
        }
    }

    private static IEnumerable<Ship> PlayerDesigns(Player player)
    {
        try
        {
            if (player.designs != null)
            {
                Il2CppSystem.Collections.Generic.List<Ship> designs = new(player.designs);
                foreach (Ship ship in designs)
                {
                    if (ship != null && (SafeBool(() => ship.isDesign) || SafeBool(() => ship.isRefitDesign)) && !SafeBool(() => ship.isErased))
                        yield return ship;
                }
            }
        }
        finally
        {
        }
    }

    private static bool IsBenchmarkPlayer(Player player)
    {
        try
        {
            return player.isMajor || player.isMain;
        }
        catch
        {
            return false;
        }
    }

    private static string DesignKey(Ship design)
        => $"{DesignIdentityKey(design)}:{TorpedoStateSignature(design)}";

    private static string DesignIdentityKey(Ship design)
    {
        try
        {
            return design.id.ToString();
        }
        catch
        {
        }

        try
        {
            return design.Pointer.ToString("X", CultureInfo.InvariantCulture);
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static string TorpedoStateSignature(Ship ship)
    {
        try
        {
            List<string> groups = TorpedoGroups(ship)
                .Select(group => $"{PartDataKey(group.Data)}x{group.Count}")
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToList();

            return groups.Count == 0 ? "torp:none" : "torp:" + string.Join(",", groups);
        }
        catch
        {
            return "torp:unknown";
        }
    }

    private static string PartDataKey(PartData? data)
    {
        if (data == null)
            return "unknown";

        try
        {
            IntPtr pointer = data.Pointer;
            if (pointer != IntPtr.Zero)
                return pointer.ToString("X", CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        string name = SafeString(() => data.name);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        string type = SafeString(() => data.type);
        if (!string.IsNullOrWhiteSpace(type))
            return type;

        return SafeString(() => data.nameUi);
    }

    private static void LogTorpedoAuditMismatchIfAny(Ship design, TorpedoProfile torpedoes)
    {
        if (torpedoes.TubeCount <= 0)
            return;

        try
        {
            MajorShipTorpedoAuditResult audit = MajorShipTorpedoCleanup.Audit(
                design,
                SafePlayer(design),
                "effective-power-check",
                CurrentTurnLabel(),
                requireAiNonMain: true,
                logClean: false);

            if (!audit.Applied || !string.Equals(audit.Result, "clean", StringComparison.OrdinalIgnoreCase))
                return;

            string label = ShipLabel(design);
            LogFailureOnce(
                "torpedo-audit-mismatch:" + DesignIdentityKey(design),
                $"UADVP effective power: torpedo tube mismatch for {label}; powerTubes={torpedoes.TubeCount} but torpedo audit is clean; source={TorpedoStateSignature(design)}.");
        }
        catch
        {
        }
    }

    private static string SafeShipName(Ship ship)
    {
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

        return NormalizeShipType(ship);
    }

    private static Player? SafePlayer(Ship ship)
    {
        try
        {
            return ship.player;
        }
        catch
        {
            return null;
        }
    }

    private static float Factor(float value, float benchmark, float exponent, float min, float max)
    {
        if (value <= 0f || benchmark <= 0f || float.IsNaN(value) || float.IsInfinity(value) || float.IsNaN(benchmark) || float.IsInfinity(benchmark))
            return 1f;

        float ratio = value / benchmark;
        if (ratio <= 0f)
            return 1f;

        return Clamp((float)Math.Pow(ratio, exponent), min, max);
    }

    private static float TorpedoInfluence(string type)
    {
        return NormalizeShipType(type) switch
        {
            "TB" => 0.60f,
            "DD" => 0.50f,
            "CL" => 0.25f,
            "CA" => 0.18f,
            "BC" => 0.10f,
            "BB" => 0.08f,
            _ => 0.15f
        };
    }

    private static (float Min, float Max) TorpedoThreatClamp(string type)
    {
        return NormalizeShipType(type) switch
        {
            "BB" or "BC" => (0.95f, 1.12f),
            "CA" or "CL" => (0.90f, 1.25f),
            "DD" or "TB" => (0.75f, 1.45f),
            _ => (0.90f, 1.25f)
        };
    }

    private static int ClampGrade(int grade)
    {
        if (grade <= 0)
            return 1;

        return Math.Min(5, grade);
    }

    private static float CaliberInches(float caliber)
    {
        if (caliber <= 0f)
            return 1f;

        return caliber > 25f ? caliber / 25.4f : caliber;
    }

    private static float Clamp(float value, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return min;

        return Math.Min(max, Math.Max(min, value));
    }

    private static float PositiveOrNeutral(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : 1f;
    }

    private static string FormatScaled(float value, string suffix)
    {
        return value >= 10f
            ? $"{Math.Round(value):0}{suffix}"
            : $"{Math.Round(value, 1):0.#}{suffix}";
    }

    private static bool SafeBool(Func<bool> getter, bool fallback = false)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static int SafeInt(Func<int> getter, int fallback = 0)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static float SafeFloat(Func<float> getter, float fallback = 0f)
    {
        try
        {
            float value = getter();
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string SafeString(Func<string> getter)
    {
        try
        {
            return getter() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogFailureOnce(string key, string message)
    {
        if (!LoggedFailures.Add(key))
            return;

        MelonLogger.Warning(message);
    }

    private readonly record struct GunProfile(
        float ReloadScore,
        float RangeScore,
        float IntrinsicAccuracyScore,
        float ShipAccuracyScore,
        int GroupCount);

    private readonly record struct TorpedoProfile(
        float RangeScore,
        float SpeedScore,
        float ReloadScore,
        float AccuracyScore,
        int TubeCount);

    // Per-gun-group row used by the throw-weight power model.
    internal readonly record struct GunGroupDiagnostic(
        string Name,
        float CaliberInches,
        int Barrels,
        int Count,
        float Reload,
        float Range,
        float Weight,
        float DamageMod);
}
