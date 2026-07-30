using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: let controlled non-home ports contribute a configurable share
// of their normal port-capacity term to national shipbuilding capacity while
// leaving vanilla's home-port calculation untouched.
[HarmonyPatch(typeof(Player), nameof(Player.ShipbuildingCapacityLimit))]
internal static class CampaignForeignPortCapacityPatch
{
    private const string LogPrefix = "UADVP foreign-port capacity";
    private static readonly HashSet<string> LoggedCapacityAdds = new(StringComparer.Ordinal);

    [HarmonyPostfix]
    private static void Postfix(Player __instance, ref float __result)
    {
        ModSettings.ForeignPortCapacityMode mode = ModSettings.ForeignPortCapacity;
        float multiplier = ModSettings.ForeignPortCapacityMultiplier(mode);
        if (__instance == null || multiplier <= 0f)
            return;

        try
        {
            ForeignPortCapacityResult result = CalculateForeignPortCapacity(__instance, multiplier);
            if (result.ExtraCapacity <= 0f)
                return;

            __result += result.ExtraCapacity;
            LogCapacityAddOnce(__instance, mode, result);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to add foreign port capacity for {AiDesignCompetitiveness.PlayerLabel(__instance)}; keeping vanilla result. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ForeignPortCapacityResult CalculateForeignPortCapacity(Player player, float multiplier)
    {
        HashSet<long> homePorts = new();
        foreach (Province province in SafeList(() => player.homeProvinces))
        {
            foreach (PortElement port in SafeList(() => province.Ports))
            {
                long pointer = PortPointer(port);
                if (pointer != 0L)
                    homePorts.Add(pointer);
            }
        }

        HashSet<long> countedForeignPorts = new();
        int foreignPortCount = 0;
        float foreignPortCapacity = 0f;
        foreach (Province province in SafeList(() => player.provincesWithPort))
        {
            foreach (PortElement port in SafeList(() => province.Ports))
            {
                long pointer = PortPointer(port);
                if (pointer != 0L)
                {
                    if (homePorts.Contains(pointer) || !countedForeignPorts.Add(pointer))
                        continue;
                }

                int capacity = Math.Max(0, Safe(() => port.GetPortCapacity(), 0));
                if (capacity <= 0)
                    continue;

                foreignPortCount++;
                foreignPortCapacity += capacity;
            }
        }

        if (foreignPortCapacity <= 0f)
            return new ForeignPortCapacityResult(0, 0f, 0f, 0f, 0f);

        float ratio = Math.Max(0f, Safe(() => CampaignController.Param("shipbuilding_capacity_ratio", 0.11f), 0.11f));
        float penalty = Math.Max(0f, Safe(() => player.shipbuildingCapacityPenalty, 1f));
        float extra = foreignPortCapacity * ratio * multiplier * penalty;
        return new ForeignPortCapacityResult(foreignPortCount, foreignPortCapacity, ratio, penalty, extra);
    }

    private static void LogCapacityAddOnce(Player player, ModSettings.ForeignPortCapacityMode mode, ForeignPortCapacityResult result)
    {
        string key =
            $"{PlayerPointer(player)}|{(int)mode}|{result.ForeignPortCount}|{MathF.Round(result.RawPortCapacity)}|{MathF.Round(result.ExtraCapacity)}";
        if (!LoggedCapacityAdds.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"{LogPrefix}: player={AiDesignCompetitiveness.PlayerLabel(player)} mode={ModSettings.ForeignPortCapacityModeText(mode)} foreignPorts={result.ForeignPortCount} rawPortCapacity={result.RawPortCapacity:0.###} ratio={result.Ratio:0.###} penalty={result.Penalty:0.###} extraCapacity={result.ExtraCapacity:0.###}.");
    }

    private static List<T> SafeList<T>(Func<Il2CppSystem.Collections.Generic.List<T>?> sourceFactory)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<T>? source = sourceFactory();
            if (source == null)
                return new List<T>();

            List<T> result = new(source.Count);
            foreach (T? item in source)
            {
                if (item != null)
                    result.Add(item);
            }

            return result;
        }
        catch
        {
            return new List<T>();
        }
    }

    private static long PortPointer(PortElement? port)
        => Safe(() => port?.Pointer.ToInt64() ?? 0L, 0L);

    private static long PlayerPointer(Player? player)
        => Safe(() => player?.Pointer.ToInt64() ?? 0L, 0L);

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private readonly record struct ForeignPortCapacityResult(
        int ForeignPortCount,
        float RawPortCapacity,
        float Ratio,
        float Penalty,
        float ExtraCapacity);
}
