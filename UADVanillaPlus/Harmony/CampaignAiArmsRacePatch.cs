using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: keep AI design/build behavior vanilla unless a same-type design
// is obviously obsolete versus the current world design book. This gate does
// not optimize designs or compare across ship classes; it only blocks the
// lowest outliers from being retained or ordered.
internal static class CampaignAiArmsRacePatch
{
    private const string UncompetitiveReason = "uadvpArmsRaceUncompetitive";
    private static readonly Dictionary<string, DesignSnapshot> PendingDesignSnapshots = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedBuildBlocks = new(StringComparer.Ordinal);

    internal static void CaptureGeneratedDesignSnapshot(Player? player, bool prewarming)
    {
        if (!ModSettings.AiArmsRaceEnabled || !IsAiPlayer(player))
            return;

        PendingDesignSnapshots[PlayerKey(player!)] = new DesignSnapshot(
            AiDesignCompetitiveness.CurrentTurnIndex(),
            prewarming,
            SnapshotDesignKeys(player!));
    }

    internal static void ApplyGeneratedDesignGate(Player? player)
    {
        if (!ModSettings.AiArmsRaceEnabled || !IsAiPlayer(player))
            return;

        string playerKey = PlayerKey(player!);
        if (!PendingDesignSnapshots.TryGetValue(playerKey, out DesignSnapshot snapshot))
            return;

        PendingDesignSnapshots.Remove(playerKey);
        List<Ship> newDesigns = SafeShipList(player!.designs)
            .Where(design => design != null && !snapshot.DesignKeys.Contains(DesignKey(design)))
            .ToList();
        if (newDesigns.Count == 0)
        {
            CampaignAiDesignPriorityPatch.RecordGeneratedDesignResult(player!, Array.Empty<Ship>());
            return;
        }

        foreach (Ship design in newDesigns)
        {
            if (!ShouldCheckAiDesign(design))
                continue;

            if (AiDesignCompetitiveness.IsCompetitive(design, out AiDesignCompetitiveness.CompetitivenessInfo info))
                continue;

            bool removed = TryRemoveGeneratedDesign(player!, design);
            LogRejectedDesign(player!, design, info, removed);
        }

        CampaignAiDesignPriorityPatch.RecordGeneratedDesignResult(player!, newDesigns);
    }

    internal static void ApplyBuildValidationGate(Ship? design, ref string reason, ref bool result)
    {
        if (!result || !ModSettings.AiArmsRaceEnabled || !ShouldCheckAiDesign(design))
            return;

        if (AiDesignCompetitiveness.IsCompetitive(design, out AiDesignCompetitiveness.CompetitivenessInfo info))
            return;

        result = false;
        reason = UncompetitiveReason;
        LogBuildBlocked(design!, info);
    }

    private static bool ShouldCheckAiDesign(Ship? design)
    {
        if (design == null || design.isErased || (!design.isDesign && !design.isRefitDesign))
            return false;

        if (CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            return false;

        if (!IsAiPlayer(design.player))
            return false;

        string type = AiDesignCompetitiveness.NormalizeShipType(design.shipType);
        return type is "BB" or "BC" or "CA" or "CL" or "DD" or "TB";
    }

    private static bool TryRemoveGeneratedDesign(Player player, Ship design)
    {
        bool removed = false;
        try
        {
            CampaignController.Instance?.DeleteDesign(design);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP AI Arms Race: CampaignController.DeleteDesign failed for {DesignLabel(design)}. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (!design.isErased)
                design.Erase();
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP AI Arms Race: design erase failed for {DesignLabel(design)}. {ex.GetType().Name}: {ex.Message}");
        }

        removed = removed || !SafeShipList(player.designs).Any(existing => existing.Pointer == design.Pointer);
        return removed || Safe(() => design.isErased, false);
    }

    private static void LogRejectedDesign(Player player, Ship design, AiDesignCompetitiveness.CompetitivenessInfo info, bool removed)
    {
        string message =
            $"UADVP AI Arms Race rejected design: turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} type={info.Type} design={AiDesignCompetitiveness.ShipLabel(design)} base={AiDesignCompetitiveness.FormatCompactPower(info.BasePower)} adjusted={AiDesignCompetitiveness.FormatCompactPower(info.Estimate)} benchmark={AiDesignCompetitiveness.FormatCompactPower(info.Benchmark)} ratio={AiDesignCompetitiveness.FormatRatio(info.Ratio)} threshold={AiDesignCompetitiveness.FormatRatio(AiDesignCompetitiveness.MinimumCompetitiveRatio)}.";

        if (removed)
            Melon<UADVanillaPlusMod>.Logger.Msg(message);
        else
            Melon<UADVanillaPlusMod>.Logger.Warning($"{message} Removal did not confirm.");
    }

    private static void LogBuildBlocked(Ship design, AiDesignCompetitiveness.CompetitivenessInfo info)
    {
        string key = $"{AiDesignCompetitiveness.CurrentTurnIndex()}:{DesignKey(design)}";
        if (!LoggedBuildBlocks.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP AI Arms Race blocked build: turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(design.player)} type={info.Type} design={AiDesignCompetitiveness.ShipLabel(design)} base={AiDesignCompetitiveness.FormatCompactPower(info.BasePower)} adjusted={AiDesignCompetitiveness.FormatCompactPower(info.Estimate)} benchmark={AiDesignCompetitiveness.FormatCompactPower(info.Benchmark)} ratio={AiDesignCompetitiveness.FormatRatio(info.Ratio)} threshold={AiDesignCompetitiveness.FormatRatio(AiDesignCompetitiveness.MinimumCompetitiveRatio)} reason=uncompetitive.");
    }

    private static HashSet<string> SnapshotDesignKeys(Player player)
        => SafeShipList(player.designs).Select(DesignKey).ToHashSet(StringComparer.Ordinal);

    private static string DesignKey(Ship? design)
        => $"{AiDesignCompetitiveness.ShipId(design)}:{design?.Pointer}";

    private static string DesignLabel(Ship? design)
        => $"{AiDesignCompetitiveness.NormalizeShipType(design?.shipType)} {AiDesignCompetitiveness.ShipLabel(design)}";

    private static string PlayerKey(Player player)
        => player.Pointer.ToString();

    private static bool IsAiPlayer(Player? player)
    {
        if (player == null)
            return false;

        return Safe(() => player.isAi && !player.isMain, false);
    }

    private static List<Ship> SafeShipList(Il2CppSystem.Collections.Generic.IEnumerable<Ship>? ships)
    {
        List<Ship> result = new();
        if (ships == null)
            return result;

        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Ship>(ships);
            foreach (Ship ship in list)
            {
                if (ship != null)
                    result.Add(ship);
            }
        }
        catch
        {
        }

        return result;
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

    private readonly record struct DesignSnapshot(int Turn, bool Prewarming, HashSet<string> DesignKeys);
}

[HarmonyPatch]
internal static class CampaignAiArmsRaceGenerateRandomDesignsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Arms Race: GenerateRandomDesigns target not found; generated-design rejection disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "GenerateRandomDesigns", new[] { typeof(Player), typeof(bool) });

    [HarmonyPrefix]
    private static void Prefix(Player player, bool prewarming)
        => CampaignAiArmsRacePatch.CaptureGeneratedDesignSnapshot(player, prewarming);
}

[HarmonyPatch]
internal static class CampaignAiArmsRaceBuildNewShipsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Arms Race: BuildNewShips target not found; generated-design rejection disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "BuildNewShips", new[] { typeof(Player), typeof(float) });

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player player)
        => CampaignAiArmsRacePatch.ApplyGeneratedDesignGate(player);
}

[HarmonyPatch]
internal static class CampaignAiArmsRaceCanBuildShipsFromDesignPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Arms Race: CanBuildShipsFromDesign target not found; build gate disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign), new[] { typeof(Ship), typeof(int), typeof(string).MakeByRefType() });

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void Postfix(Ship design, int amount, ref string reason, ref bool __result)
        => CampaignAiArmsRacePatch.ApplyBuildValidationGate(design, ref reason, ref __result);
}
