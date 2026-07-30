using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.GameData;

// Balance intent: cap runaway hull mass ratios while preserving vanilla hull
// variation below each class cap. This mutates loaded PartData only; CSVs stay
// untouched and Vanilla mode restores the captured original values.
internal static class HullWeightAdjustment
{
    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };

    private static readonly Dictionary<string, float> CapsByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BB"] = 0.16f,
        ["BC"] = 0.18f,
        ["CA"] = 0.20f,
        ["CL"] = 0.22f,
        ["DD"] = 0.24f,
        ["TB"] = 0.26f,
    };

    private static readonly Dictionary<string, float> OriginalRatios = new(StringComparer.OrdinalIgnoreCase);

    private static bool? lastAppliedEnabled;
    private static string? lastAppliedSummary;
    private static bool loggedMissingGameData;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.parts == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP hull weight adjustment: option stored; part data is not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        bool enabled = ModSettings.HullWeightAdjustmentEnabled;
        Dictionary<string, int> seenByType = EmptyTypeCounts();
        Dictionary<string, int> cappedByType = EmptyTypeCounts();
        Dictionary<string, int> changedByType = EmptyTypeCounts();
        List<string> examples = new();
        bool anyChanged = false;

        foreach (var pair in gameData.parts)
        {
            PartData? part = pair.Value;
            if (part == null || !Safe(() => part.isHull, false))
                continue;

            string key = StablePartKey(pair.Key, part);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            string type = ResolveHullType(key, part);
            if (!CapsByType.TryGetValue(type, out float cap))
                continue;

            seenByType[type]++;
            if (!OriginalRatios.ContainsKey(key))
                OriginalRatios[key] = Safe(() => part.hullWeightRatio, 0f);

            float original = OriginalRatios[key];
            float target = enabled ? Math.Min(original, cap) : original;
            if (enabled && original > cap + 0.0001f)
                cappedByType[type]++;

            float before = Safe(() => part.hullWeightRatio, 0f);
            if (Math.Abs(before - target) <= 0.0001f)
                continue;

            part.hullWeightRatio = target;
            anyChanged = true;
            changedByType[type]++;
            if (examples.Count < 8)
                examples.Add($"{key} {Fmt(before)}->{Fmt(target)}");
        }

        string seen = FormatCounts(seenByType);
        string capped = FormatCounts(cappedByType);
        string changed = FormatCounts(changedByType);
        string exampleText = examples.Count == 0 ? "none" : string.Join(", ", examples);
        string summary = enabled
            ? $"seen {seen}; capped {capped}; changed {changed}; examples {exampleText}"
            : $"seen {seen}; restored {changed}; examples {exampleText}";

        if (!anyChanged && lastAppliedEnabled == enabled && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedEnabled = enabled;
        lastAppliedSummary = summary;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP hull weight adjustment: applied {ModSettings.HullWeightAdjustmentModeText(enabled)} during {context}; {summary}.");
    }

    private static Dictionary<string, int> EmptyTypeCounts()
        => SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);

    private static string StablePartKey(string? key, PartData part)
    {
        if (!string.IsNullOrWhiteSpace(key))
            return key.Trim();

        return SafeString(() => part.name);
    }

    private static string ResolveHullType(string key, PartData part)
    {
        string type = Safe(() => AiDesignCompetitiveness.NormalizeShipType(part.shipType), "UNK");
        if (CapsByType.ContainsKey(type))
            return type;

        return TypeFromHullId(key);
    }

    private static string TypeFromHullId(string key)
    {
        string id = key.Trim().ToLowerInvariant();
        if (id.StartsWith("bb_", StringComparison.Ordinal) ||
            id.StartsWith("b1_", StringComparison.Ordinal) ||
            id.StartsWith("b2_", StringComparison.Ordinal) ||
            id.StartsWith("b3_", StringComparison.Ordinal) ||
            id.StartsWith("b4_", StringComparison.Ordinal) ||
            id.StartsWith("b5_", StringComparison.Ordinal))
        {
            return "BB";
        }

        if (id.StartsWith("bc_", StringComparison.Ordinal))
            return "BC";
        if (id.StartsWith("ca_", StringComparison.Ordinal))
            return "CA";
        if (id.StartsWith("cl_", StringComparison.Ordinal))
            return "CL";
        if (id.StartsWith("dd_", StringComparison.Ordinal))
            return "DD";
        if (id.StartsWith("tb_", StringComparison.Ordinal))
            return "TB";

        return "UNK";
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts)
        => string.Join(" ", SurfaceTypes.Select(type => $"{type}={counts.GetValueOrDefault(type)}"));

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
