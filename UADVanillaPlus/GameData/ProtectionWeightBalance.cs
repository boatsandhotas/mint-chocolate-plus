using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace UADVanillaPlus.GameData;

// Balance intent: keep protection upgrades meaningful while removing hidden or
// exaggerated hull-weight penalties from loaded technology data. CSVs stay
// untouched and parsed TechnologyData effects are refreshed after each rewrite.
internal static class ProtectionWeightBalance
{
    private const string AntiTorpedoWeightEffect = "anti_torp_weight";

    private static readonly HashSet<string> AntiTorpedoWeightTechs = new(StringComparer.OrdinalIgnoreCase)
    {
        "surv_hull_4",
        "surv_hull_5",
        "surv_hull_6",
        "surv_hull_7",
        "surv_hull_8",
        "surv_hull_end",
    };

    private static readonly Dictionary<string, EffectRewrite> MultiBottomHullRewrites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["surv_hull_2"] = new("hull(-7.5)", "hull(-4)"),
        ["surv_hull_3"] = new("hull(-12.5)", "hull(-7.5)"),
    };

    private static readonly Dictionary<string, string[]> CitadelWeightTokensByTech = new(StringComparer.OrdinalIgnoreCase)
    {
        ["surv_internal_5"] = new[] { "hull(-5)" },
        ["surv_internal_6"] = new[] { "hull(-7)" },
        ["surv_internal_7"] = new[] { "hull(-12.5)" },
        ["armor_forging_6"] = new[] { "armor(-2.5)", "belt(-12)", "hull(-12)" },
        ["armor_forging_8"] = new[] { "deck(-12)", "hull(-15)" },
    };

    private static readonly HashSet<string> ArmorQualityWeightTechs = new(StringComparer.OrdinalIgnoreCase)
    {
        "armor_quality_3",
        "armor_quality_4",
        "armor_quality_5",
        "armor_quality_6",
        "armor_quality_7",
        "armor_quality_8",
        "armor_quality_9",
        "armor_quality_10",
        "armor_quality_11",
    };

    private static readonly Dictionary<string, string> OriginalEffects = new(StringComparer.OrdinalIgnoreCase);

    private static bool loggedMissingGameData;
    private static string? lastAppliedSummary;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.technologies == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP protection weight balance: technology data is not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        bool enabled = ModSettings.HullWeightAdjustmentEnabled;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> antiTorpedoChanged = new();
        List<string> multiBottomChanged = new();
        List<string> citadelChanged = new();
        List<string> armorQualityChanged = new();
        int antiTorpedoTokensRemoved = 0;
        int citadelTokensRemoved = 0;
        int armorQualityTokensRemoved = 0;
        int restored = 0;

        foreach (var entry in gameData.technologies)
        {
            TechnologyData? tech = entry.Value;
            if (tech == null)
                continue;

            string techKey = TargetTechKey(entry.Key, tech);
            if (string.IsNullOrEmpty(techKey))
                continue;

            seen.Add(techKey);
            string current = SafeString(() => tech.effect);
            if (!OriginalEffects.ContainsKey(techKey))
                OriginalEffects[techKey] = current;

            string original = OriginalEffects[techKey];
            string target = enabled
                ? RewriteProtectionEffects(
                    techKey,
                    original,
                    out int removedAntiTorpedoTokens,
                    out string? multiBottomRewrite,
                    out int removedCitadelWeightTokens,
                    out string? citadelWeightRewrite,
                    out int removedArmorQualityWeightTokens,
                    out string? armorQualityWeightRewrite)
                : original;

            if (string.Equals(current, target, StringComparison.Ordinal))
                continue;

            tech.effect = target;
            try { tech.PostProcess(); }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP protection weight balance: failed to postprocess {techKey} after rewrite. {ex.GetType().Name}: {ex.Message}");
            }

            if (!enabled)
            {
                restored++;
                continue;
            }

            CountAdjustedEffect(
                techKey,
                original,
                ref antiTorpedoTokensRemoved,
                antiTorpedoChanged,
                multiBottomChanged,
                ref citadelTokensRemoved,
                citadelChanged,
                ref armorQualityTokensRemoved,
                armorQualityChanged);
        }

        if (enabled && antiTorpedoChanged.Count == 0 && multiBottomChanged.Count == 0 && citadelChanged.Count == 0 && armorQualityChanged.Count == 0)
            return;
        if (!enabled && restored == 0)
            return;

        string summary = enabled
            ? $"applied {ModSettings.HullWeightAdjustmentModeText(true)} during {context}; removedAntiTorpWeight={antiTorpedoTokensRemoved}; adjustedMultiBottom={multiBottomChanged.Count}" +
                $"; citadelWeightRemoved={citadelChanged.Count}" +
                $"; armorQualityWeightRemoved={armorQualityChanged.Count}" +
                (antiTorpedoChanged.Count > 0 ? $"; antiTorpTechs={FormatList(antiTorpedoChanged)}" : string.Empty) +
                (multiBottomChanged.Count > 0 ? $"; multiBottom={FormatList(multiBottomChanged)}" : string.Empty) +
                (citadelChanged.Count > 0 ? $"; citadel={FormatList(citadelChanged)} tokens={citadelTokensRemoved}" : string.Empty) +
                (armorQualityChanged.Count > 0 ? $"; armorQuality={FormatList(armorQualityChanged)} tokens={armorQualityTokensRemoved}" : string.Empty)
            : $"applied {ModSettings.HullWeightAdjustmentModeText(false)} during {context}; restored={restored}";

        List<string> missing = TargetTechs()
            .Where(target => !seen.Contains(target))
            .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
            summary += $"; missing={string.Join(",", missing)}";

        if (string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedSummary = summary;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP protection weight balance: {summary}.");
    }

    private static IEnumerable<string> TargetTechs()
        => AntiTorpedoWeightTechs
            .Concat(MultiBottomHullRewrites.Keys)
            .Concat(CitadelWeightTokensByTech.Keys)
            .Concat(ArmorQualityWeightTechs)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string TargetTechKey(string? entryKey, TechnologyData tech)
    {
        string key = SafeString(() => entryKey);
        if (AntiTorpedoWeightTechs.Contains(key) || MultiBottomHullRewrites.ContainsKey(key) || CitadelWeightTokensByTech.ContainsKey(key) || ArmorQualityWeightTechs.Contains(key))
            return key;

        string name = SafeString(() => tech.name);
        return AntiTorpedoWeightTechs.Contains(name) || MultiBottomHullRewrites.ContainsKey(name) || CitadelWeightTokensByTech.ContainsKey(name) || ArmorQualityWeightTechs.Contains(name)
            ? name
            : string.Empty;
    }

    private static string RewriteProtectionEffects(
        string techKey,
        string effect,
        out int removedAntiTorpedoTokens,
        out string? multiBottomRewrite,
        out int removedCitadelWeightTokens,
        out string? citadelWeightRewrite,
        out int removedArmorQualityWeightTokens,
        out string? armorQualityWeightRewrite)
    {
        removedAntiTorpedoTokens = 0;
        multiBottomRewrite = null;
        removedCitadelWeightTokens = 0;
        citadelWeightRewrite = null;
        removedArmorQualityWeightTokens = 0;
        armorQualityWeightRewrite = null;
        List<string> rewritten = new();
        List<string> removedCitadelTokens = new();
        List<string> removedArmorQualityTokens = new();

        MultiBottomHullRewrites.TryGetValue(techKey, out EffectRewrite bottomRewrite);
        bool removeAntiTorpedoWeight = AntiTorpedoWeightTechs.Contains(techKey);
        bool removeArmorQualityWeight = ArmorQualityWeightTechs.Contains(techKey);

        foreach (string token in SplitTopLevelParamTokens(effect))
        {
            if (removeAntiTorpedoWeight && IsAntiTorpedoWeightToken(token))
            {
                removedAntiTorpedoTokens++;
                continue;
            }

            if (CitadelWeightTokensByTech.TryGetValue(techKey, out string[]? citadelWeightTokens) &&
                citadelWeightTokens.Any(remove => TokenEquals(token, remove)))
            {
                removedCitadelWeightTokens++;
                removedCitadelTokens.Add(token.Trim());
                continue;
            }

            if (removeArmorQualityWeight && IsPositiveArmorWeightToken(token))
            {
                removedArmorQualityWeightTokens++;
                removedArmorQualityTokens.Add(token.Trim());
                continue;
            }

            if (bottomRewrite.HasValue && TokenEquals(token, bottomRewrite.From))
            {
                rewritten.Add(bottomRewrite.To);
                multiBottomRewrite = $"{bottomRewrite.From}->{bottomRewrite.To}";
                continue;
            }

            rewritten.Add(token);
        }

        if (removedCitadelTokens.Count > 0)
            citadelWeightRewrite = string.Join("|", removedCitadelTokens);
        if (removedArmorQualityTokens.Count > 0)
            armorQualityWeightRewrite = string.Join("|", removedArmorQualityTokens);

        return removedAntiTorpedoTokens > 0 || multiBottomRewrite != null || removedCitadelWeightTokens > 0 || removedArmorQualityWeightTokens > 0
            ? string.Join(",", rewritten)
            : effect;
    }

    private static void CountAdjustedEffect(
        string techKey,
        string originalEffect,
        ref int antiTorpedoTokensRemoved,
        List<string> antiTorpedoChanged,
        List<string> multiBottomChanged,
        ref int citadelTokensRemoved,
        List<string> citadelChanged,
        ref int armorQualityTokensRemoved,
        List<string> armorQualityChanged)
    {
        RewriteProtectionEffects(
            techKey,
            originalEffect,
            out int removedAntiTorpedoTokens,
            out string? multiBottomRewrite,
            out int removedCitadelWeightTokens,
            out string? citadelWeightRewrite,
            out int removedArmorQualityWeightTokens,
            out string? armorQualityWeightRewrite);

        if (removedAntiTorpedoTokens > 0)
        {
            antiTorpedoTokensRemoved += removedAntiTorpedoTokens;
            antiTorpedoChanged.Add(techKey);
        }

        if (multiBottomRewrite != null)
            multiBottomChanged.Add($"{techKey} {multiBottomRewrite}");

        if (removedCitadelWeightTokens > 0)
        {
            citadelTokensRemoved += removedCitadelWeightTokens;
            citadelChanged.Add($"{techKey} {citadelWeightRewrite}");
        }

        if (removedArmorQualityWeightTokens > 0)
        {
            armorQualityTokensRemoved += removedArmorQualityWeightTokens;
            armorQualityChanged.Add($"{techKey} {armorQualityWeightRewrite}");
        }
    }

    private static bool IsAntiTorpedoWeightToken(string token)
    {
        string trimmed = token.Trim();
        if (!trimmed.StartsWith(AntiTorpedoWeightEffect, StringComparison.OrdinalIgnoreCase))
            return false;

        int index = AntiTorpedoWeightEffect.Length;
        while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
            index++;

        return index < trimmed.Length && trimmed[index] == '(';
    }

    private static bool TokenEquals(string token, string expected)
        => string.Equals(token.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsPositiveArmorWeightToken(string token)
    {
        string trimmed = token.Trim();
        int open = trimmed.IndexOf('(');
        int close = trimmed.LastIndexOf(')');
        if (open <= 0 || close <= open)
            return false;

        string name = trimmed[..open].Trim();
        if (!string.Equals(name, "armor", StringComparison.OrdinalIgnoreCase))
            return false;

        string argument = trimmed[(open + 1)..close].Trim();
        if (argument.Contains(';') || argument.Contains(','))
            return false;

        return float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
            value > 0f;
    }

    private static List<string> SplitTopLevelParamTokens(string? param)
    {
        List<string> tokens = new();
        if (string.IsNullOrWhiteSpace(param))
            return tokens;

        int depth = 0;
        int start = 0;
        for (int i = 0; i < param.Length; i++)
        {
            char c = param[i];
            if (c == '(' || c == '[' || c == '{')
                depth++;
            else if ((c == ')' || c == ']' || c == '}') && depth > 0)
                depth--;
            else if (c == ',' && depth == 0)
            {
                AddToken(param[start..i]);
                start = i + 1;
            }
        }

        AddToken(param[start..]);
        return tokens;

        void AddToken(string token)
        {
            token = token.Trim();
            if (!string.IsNullOrEmpty(token))
                tokens.Add(token);
        }
    }

    private static string FormatList(IEnumerable<string> values)
        => string.Join(",", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

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

    private readonly record struct EffectRewrite(string From, string To)
    {
        internal bool HasValue => !string.IsNullOrWhiteSpace(From) && !string.IsNullOrWhiteSpace(To);
    }
}
