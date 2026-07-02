using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// AI arms-race retention. Vanilla design cleanup (CampaignController.DeleteOldDesigns) is
// age/buildability based and can erase an AI nation's last strong same-type design. While
// Arms Race is active we keep the cull from deleting the best buildable competitive design
// per surface type, without affecting manual deletes or Arms Race rejection deletes. This is
// done by snapshotting the protected designs when the AI's cull window opens (BeginCleanup)
// and suppressing only those specific erases (ShouldSuppressDelete). Applies to AI players
// only; the main human player's cull runs vanilla.
internal static class CampaignAiDesignRetentionPatch
{
    private const string LogPrefix = "UADVP AI Design retention";
    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };

    [ThreadStatic]
    private static CleanupContext? activeContext;

    internal static void BeginCleanup(Player? player)
    {
        activeContext = null;

        if (!ModSettings.AiArmsRaceEnabled || !IsAiPlayer(player))
            return;

        Dictionary<string, ProtectedDesign> protectedByType = BuildProtectedDesigns(player!);
        if (protectedByType.Count == 0)
            return;

        activeContext = new CleanupContext(PlayerPointer(player!), protectedByType.Values.ToDictionary(design => design.Key, StringComparer.Ordinal));
        LogCleanupSummary(player!, protectedByType);
    }

    internal static void EndCleanup()
    {
        activeContext = null;
    }

    // Suppress an erase only when it targets an AI design we snapshotted as protected at the
    // start of THIS player's cull window. When no AI cull window is open (activeContext == null)
    // nothing is suppressed -- so the main player's refit-save archival and manual deletes always
    // go through, and the main player's own cull is handled by skipping it outright (above).
    internal static bool ShouldSuppressDelete(Ship? design)
    {
        if (design == null)
            return false;

        CleanupContext? context = activeContext;
        if (context == null)
            return false;

        if (!SamePlayer(design.player, context.PlayerPointer))
            return false;

        string key = DesignKey(design);
        if (!context.ProtectedDesigns.TryGetValue(key, out ProtectedDesign protectedDesign))
            return false;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(design.player)} type={protectedDesign.Type} protected={protectedDesign.Name} adjusted={AiDesignCompetitiveness.FormatCompactPower(protectedDesign.AdjustedPower)} benchmark={AiDesignCompetitiveness.FormatCompactPower(protectedDesign.BenchmarkPower)} ratio={AiDesignCompetitiveness.FormatRatio(protectedDesign.Ratio)} reason=bestCompetitiveSameType cleanup=DeleteOldDesigns.");
        return true;
    }

    private static Dictionary<string, ProtectedDesign> BuildProtectedDesigns(Player player)
    {
        Dictionary<string, ProtectedDesign> protectedByType = new(StringComparer.OrdinalIgnoreCase);
        foreach (Ship design in SafeShipList(player.designs))
        {
            if (!TryBuildProtectedDesign(player, design, out ProtectedDesign candidate))
                continue;

            if (!protectedByType.TryGetValue(candidate.Type, out ProtectedDesign current) || IsBetter(candidate, current))
                protectedByType[candidate.Type] = candidate;
        }

        return protectedByType;
    }

    private static bool TryBuildProtectedDesign(Player player, Ship? design, out ProtectedDesign protectedDesign)
    {
        protectedDesign = default;
        if (design == null || Safe(() => design.isErased, true))
            return false;

        if (!Safe(() => design.isDesign || design.isRefitDesign, false))
            return false;

        if (!SamePlayer(design.player, PlayerPointer(player)))
            return false;

        string type = AiDesignCompetitiveness.NormalizeShipType(design.shipType);
        if (!SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return false;

        if (!AiDesignCompetitiveness.IsCompetitive(design, out AiDesignCompetitiveness.CompetitivenessInfo info))
            return false;

        if (info.Estimate <= 0f || info.Benchmark <= 0f || info.Ratio < AiDesignCompetitiveness.MinimumCompetitiveRatio)
            return false;

        if (!CanBuildDesign(design))
            return false;

        GameDate effectiveDate = EffectiveDesignDate(design);
        protectedDesign = new ProtectedDesign(
            DesignKey(design),
            type,
            AiDesignCompetitiveness.ShipLabel(design),
            info.Estimate,
            info.Benchmark,
            info.Ratio,
            Safe(() => effectiveDate.turn, -1),
            AiDesignCompetitiveness.ShipId(design));
        return true;
    }

    private static bool CanBuildDesign(Ship design)
    {
        try
        {
            PlayerController? controller = PlayerController.Instance;
            if (controller == null)
                return false;

            string reason;
            return controller.CanBuildShipsFromDesign(design, 1, out reason);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: buildability check failed for {AiDesignCompetitiveness.ShipLabel(design)}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsBetter(ProtectedDesign candidate, ProtectedDesign current)
    {
        int powerCompare = Compare(candidate.AdjustedPower, current.AdjustedPower);
        if (powerCompare != 0)
            return powerCompare > 0;

        int ratioCompare = Compare(candidate.Ratio, current.Ratio);
        if (ratioCompare != 0)
            return ratioCompare > 0;

        if (candidate.EffectiveTurn != current.EffectiveTurn)
            return candidate.EffectiveTurn > current.EffectiveTurn;

        return string.Compare(candidate.StableId, current.StableId, StringComparison.Ordinal) > 0;
    }

    private static int Compare(float left, float right)
    {
        if (float.IsNaN(left) || float.IsInfinity(left))
            left = 0f;
        if (float.IsNaN(right) || float.IsInfinity(right))
            right = 0f;

        return left.CompareTo(right);
    }

    private static void LogCleanupSummary(Player player, Dictionary<string, ProtectedDesign> protectedByType)
    {
        string summary = string.Join(", ",
            SurfaceTypes
                .Where(type => protectedByType.ContainsKey(type))
                .Select(type =>
                {
                    ProtectedDesign design = protectedByType[type];
                    return $"{type}={design.Name}:{AiDesignCompetitiveness.FormatRatio(design.Ratio)}";
                }));

        if (string.IsNullOrWhiteSpace(summary))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: turn={AiDesignCompetitiveness.CurrentTurnLabel()} nation={AiDesignCompetitiveness.PlayerLabel(player)} protected {summary}.");
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

    private static GameDate EffectiveDesignDate(Ship design)
        => Safe(() => design.isRefitDesign ? design.dateCreatedRefit : design.dateCreated, design.dateCreated);

    private static string DesignKey(Ship design)
        => $"{AiDesignCompetitiveness.ShipId(design)}:{Safe(() => design.Pointer.ToString(), "<no-pointer>")}";

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static bool SamePlayer(Player? player, long pointer)
        => player != null && PlayerPointer(player) == pointer;

    private static bool IsAiPlayer(Player? player)
    {
        if (player == null)
            return false;

        return Safe(() => player.isAi && !player.isMain, false);
    }

    private static bool IsMainHumanPlayer(Player? player)
    {
        if (player == null)
            return false;

        return Safe(() => player.isMain && !player.isAi, false);
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

    private sealed class CleanupContext
    {
        internal CleanupContext(long playerPointer, Dictionary<string, ProtectedDesign> protectedDesigns)
        {
            PlayerPointer = playerPointer;
            ProtectedDesigns = protectedDesigns;
        }

        internal long PlayerPointer { get; }
        internal Dictionary<string, ProtectedDesign> ProtectedDesigns { get; }
    }

    private readonly record struct ProtectedDesign(
        string Key,
        string Type,
        string Name,
        float AdjustedPower,
        float BenchmarkPower,
        float Ratio,
        int EffectiveTurn,
        string StableId);
}

[HarmonyPatch]
internal static class CampaignAiDesignRetentionDeleteOldDesignsPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design retention: DeleteOldDesigns target not found; retention guard disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "DeleteOldDesigns", new[] { typeof(Player) });

    [HarmonyPrefix]
    private static void Prefix(Player player)
        => CampaignAiDesignRetentionPatch.BeginCleanup(player);

    [HarmonyFinalizer]
    private static void Finalizer()
        => CampaignAiDesignRetentionPatch.EndCleanup();
}

[HarmonyPatch]
internal static class CampaignAiDesignRetentionDeleteDesignPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design retention: DeleteDesign target not found; retention guard disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), nameof(CampaignController.DeleteDesign), new[] { typeof(Ship) });

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(Ship ship)
        => !CampaignAiDesignRetentionPatch.ShouldSuppressDelete(ship);
}

// The AI obsolescence cull erases designs via Ship.Erase() (logged as "[Ship] DeleteDesign ..."),
// which the DeleteDesign guard above doesn't cover. Suppress those too, but ONLY inside an AI cull
// window (activeContext set by BeginCleanup) for that AI's protected designs. With no window open
// nothing is suppressed, so the main player's erases (refit-save archival, manual deletes, sinking)
// are never blocked here.
[HarmonyPatch]
internal static class CampaignAiDesignRetentionShipErasePatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP AI Design retention: Ship.Erase target not found; erase guard disabled.");

        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(Ship), nameof(Ship.Erase), Type.EmptyTypes);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(Ship __instance)
        => !CampaignAiDesignRetentionPatch.ShouldSuppressDelete(__instance);
}
