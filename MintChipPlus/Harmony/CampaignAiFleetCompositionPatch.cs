using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.Harmony;

// Patch intent: expose AI surface fleet composition as a campaign data weight
// profile by changing only the loaded ShipType.buildRatio values vanilla uses
// during future AI BuildNewShips type selection.
[HarmonyPatch(typeof(UadGameData))]
internal static class CampaignAiFleetCompositionDataPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UadGameData.PostProcessAll))]
    private static void PostProcessAllPostfix(UadGameData __instance)
        => CampaignAiFleetCompositionPatch.ApplyCurrentSetting("game data postprocess", __instance);
}

[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.Init))]
internal static class CampaignAiFleetCompositionCampaignInitPatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => CampaignAiFleetCompositionPatch.ApplyCurrentSetting("campaign init");
}

internal static class CampaignAiFleetCompositionPatch
{
    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly Dictionary<string, float> OriginalBuildRatios = new(StringComparer.OrdinalIgnoreCase);
    private static ModSettings.AiFleetCompositionMode? lastAppliedMode;
    private static string? lastAppliedSummary;
    private static bool originalRatiosCaptured;
    private static bool loggedMissingGameData;

    internal static void ApplyCurrentSetting(string context = "manual", UadGameData? gameDataOverride = null)
    {
        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.shipTypes == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<MintChipPlusMod>.Logger.Msg("UADMC AI Fleet Mix: option stored; ship type data is not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        CaptureOriginalRatios(gameData);

        ModSettings.AiFleetCompositionMode mode = ModSettings.AiFleetComposition;
        Dictionary<string, float> applied = new(StringComparer.OrdinalIgnoreCase);

        foreach (ShipType? shipType in SurfaceShipTypes(gameData))
        {
            string type = NormalizeShipType(shipType);
            if (!IsSurfaceType(type))
                continue;

            if (!TryProfileRatio(mode, type, out float ratio) &&
                !OriginalBuildRatios.TryGetValue(type, out ratio))
            {
                continue;
            }

            shipType!.buildRatio = ratio;
            applied[type] = ratio;
        }

        string summary = BuildRatioSummary(applied);
        if (lastAppliedMode != mode || !string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
        {
            lastAppliedMode = mode;
            lastAppliedSummary = summary;
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC AI Fleet Mix: applied {ModSettings.AiFleetCompositionModeText(mode)} profile during {context}; {summary}.");
        }
    }

    private static void CaptureOriginalRatios(UadGameData gameData)
    {
        if (originalRatiosCaptured)
            return;

        foreach (ShipType? shipType in SurfaceShipTypes(gameData))
        {
            string type = NormalizeShipType(shipType);
            if (IsSurfaceType(type) && !OriginalBuildRatios.ContainsKey(type))
                OriginalBuildRatios[type] = shipType!.buildRatio;
        }

        originalRatiosCaptured = true;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC AI Fleet Mix: cached original surface build ratios; {BuildRatioSummary(OriginalBuildRatios)}.");
    }

    private static IEnumerable<ShipType?> SurfaceShipTypes(UadGameData gameData)
    {
        foreach (var pair in gameData.shipTypes)
        {
            ShipType? shipType = pair.Value;
            if (IsSurfaceType(NormalizeShipType(shipType)))
                yield return shipType;
        }
    }

    private static bool TryProfileRatio(ModSettings.AiFleetCompositionMode mode, string type, out float ratio)
    {
        ratio = 0f;
        switch (mode)
        {
            case ModSettings.AiFleetCompositionMode.Balanced:
                ratio = 10f;
                return true;
            case ModSettings.AiFleetCompositionMode.Heavy:
                ratio = type switch
                {
                    "BB" => 30f,
                    "BC" => 15f,
                    "CA" => 30f,
                    "CL" => 30f,
                    "DD" => 10f,
                    "TB" => 10f,
                    _ => 0f,
                };
                return ratio > 0f;
            default:
                return false;
        }
    }

    private static bool IsSurfaceType(string type)
        => SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeShipType(ShipType? type)
    {
        string raw = SafeString(() => type?.name);
        if (string.Equals(raw, "<empty>", StringComparison.OrdinalIgnoreCase))
            raw = SafeString(() => type?.nameUi);

        return NormalizeShipType(raw);
    }

    private static string NormalizeShipType(string? value)
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

    private static string BuildRatioSummary(IReadOnlyDictionary<string, float> ratios)
        => string.Join(" ", SurfaceTypes.Select(type =>
            ratios.TryGetValue(type, out float ratio)
                ? $"{type}={Fmt(ratio)}"
                : $"{type}=missing"));

    private static string Fmt(float value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

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
}
