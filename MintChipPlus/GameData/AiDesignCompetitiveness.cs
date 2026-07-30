using System.Globalization;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// AI Arms Race uses the shared MC effective-power estimate as a coarse
// same-type sanity gate. It deliberately avoids cross-type comparisons.
internal static class AiDesignCompetitiveness
{
    internal static float MinimumCompetitiveRatio => ModSettings.AiArmsRaceMinimumCompetitiveRatio;

    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static int loggedBenchmarkTurn = int.MinValue;

    internal static bool IsCompetitive(Ship? design, out CompetitivenessInfo info)
    {
        info = CompetitivenessInfo.Pass("not-checked");
        if (!ModSettings.AiArmsRaceEnabled || design == null)
            return true;

        string type = NormalizeShipType(design.shipType);
        if (!IsSurfaceType(type))
        {
            info = CompetitivenessInfo.Pass("not-surface");
            return true;
        }

        LogBenchmarksForCurrentTurn();

        EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(design);
        if (power.BasePower <= 0f && power.AdjustedPower <= 0f)
        {
            info = new CompetitivenessInfo(type, power.BasePower, power.AdjustedPower, 0f, 0f, 0, string.Empty, true, "non-positive-estimate");
            return true;
        }

        if (power.BenchmarkAdjustedPower <= 0f)
        {
            EffectivePowerBenchmark benchmark = ShipEffectivePowerCalculator.BenchmarkForType(type);
            info = new CompetitivenessInfo(type, power.BasePower, power.AdjustedPower, 0f, 0f, benchmark.SampleCount, benchmark.TopDesignLabels, true, "missing-benchmark");
            return true;
        }

        bool competitive = power.CompetitivenessRatio >= MinimumCompetitiveRatio;
        EffectivePowerBenchmark resolvedBenchmark = ShipEffectivePowerCalculator.BenchmarkForType(type);
        info = new CompetitivenessInfo(
            type,
            power.BasePower,
            power.AdjustedPower,
            power.BenchmarkAdjustedPower,
            power.CompetitivenessRatio,
            resolvedBenchmark.SampleCount,
            resolvedBenchmark.TopDesignLabels,
            competitive,
            competitive ? "competitive" : "uncompetitive");
        return competitive;
    }

    internal static string CurrentTurnLabel()
    {
        CampaignController? campaign = CampaignController.Instance;
        int turn = CurrentTurnIndex();
        return Safe(() => campaign?.CurrentDate.ToString(false) ?? (turn >= 0 ? $"turn-{turn}" : "unknown"), turn >= 0 ? $"turn-{turn}" : "unknown");
    }

    internal static int CurrentTurnIndex()
        => Safe(() => CampaignController.Instance?.CurrentDate.turn ?? -1, -1);

    internal static string NormalizeShipType(ShipType? type)
    {
        string raw = SafeString(() => type?.name);
        if (string.Equals(raw, "<empty>", StringComparison.OrdinalIgnoreCase))
            raw = SafeString(() => type?.nameUi);

        return NormalizeShipType(raw);
    }

    internal static string NormalizeShipType(string? value)
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

    internal static string ShipId(Ship? ship)
    {
        try
        {
            return ship?.id.ToString() ?? "<null>";
        }
        catch
        {
            return ship?.Pointer.ToString() ?? "<null>";
        }
    }

    internal static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "<null>";

        string name = SafeString(() => ship.Name(false, false, false, false, true));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => ship.vesselName);
    }

    internal static string PlayerLabel(Player? player)
    {
        if (player == null)
            return "<null>";

        string name = SafeString(() => player.Name(false));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => player.data?.name);
    }

    internal static string FormatCompactPower(float value)
        => ShipEffectivePowerCalculator.FormatCompactPower(value);

    internal static string FormatRatio(float ratio)
        => (ratio * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static void LogBenchmarksForCurrentTurn()
    {
        int turn = CurrentTurnIndex();
        if (loggedBenchmarkTurn == turn)
            return;

        loggedBenchmarkTurn = turn;
        string turnLabel = CurrentTurnLabel();
        foreach (EffectivePowerBenchmark benchmark in ShipEffectivePowerCalculator.CurrentBenchmarks())
        {
            if (!IsSurfaceType(benchmark.Type) || benchmark.SampleCount <= 0)
                continue;

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC AI Arms Race benchmark: turn={turnLabel} type={benchmark.Type} base={FormatCompactPower(benchmark.BasePower)} adjusted={FormatCompactPower(benchmark.AdjustedPower)} samples={benchmark.SampleCount} top={benchmark.TopDesignLabels}.");
        }
    }

    private static bool IsSurfaceType(string type)
        => SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

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

    internal readonly record struct CompetitivenessInfo(
        string Type,
        float BasePower,
        float Estimate,
        float Benchmark,
        float Ratio,
        int SampleCount,
        string TopDesignLabels,
        bool Competitive,
        string Reason)
    {
        internal static CompetitivenessInfo Pass(string reason)
            => new("UNK", 0f, 0f, 0f, 0f, 0, string.Empty, true, reason);
    }
}
