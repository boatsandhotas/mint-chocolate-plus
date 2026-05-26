using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: let players keep designing with already researched technology
// that vanilla hides as "obsolete", without teaching the AI to use old parts.
// Unlock checks still run normally; this only suppresses the obsolete filter.
[HarmonyPatch(typeof(Player))]
internal static class DesignObsoleteHullRetentionPatch
{
    private static bool loggedFirstHull;
    private static bool loggedFirstHullPartData;
    private static bool loggedFirstSubmarine;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.IsHullObsolete), typeof(string))]
    internal static bool IsHullObsoletePrefix(Player __instance, string part, ref bool __result)
    {
        if (!ShouldRetainFor(__instance) || string.IsNullOrWhiteSpace(part))
            return true;

        __result = false;
        LogFirstHull(part);
        return false;
    }

    // Vanilla exposes a second IsHullObsolete(PartData) overload that isn't a thin
    // wrapper over the String one — confirmed missing from our patch via uad_recon's
    // Q-Refit-2 dump. The Shipyard's refit-button gate goes through this overload
    // (the design's hull part is already a PartData by the time the gate fires), so
    // the String-only patch never ran on that path and obsolete hulls stayed blocked
    // from refit even with "Retain" on.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.IsHullObsolete), typeof(PartData))]
    internal static bool IsHullObsoletePartDataPrefix(Player __instance, PartData part, ref bool __result)
    {
        if (!ShouldRetainFor(__instance) || part == null)
            return true;

        __result = false;
        LogFirstHullPartData(part);
        return false;
    }

    // Same retention semantics for submarine hulls. Vanilla's IsSubmarineObsolete
    // is a parallel gate (Q-Refit-2) that, like the hull version, would otherwise
    // block refits on retired sub classes for the player.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.IsSubmarineObsolete), typeof(string))]
    internal static bool IsSubmarineObsoletePrefix(Player __instance, string name, ref bool __result)
    {
        if (!ShouldRetainFor(__instance) || string.IsNullOrWhiteSpace(name))
            return true;

        __result = false;
        LogFirstSubmarine(name);
        return false;
    }

    private static bool ShouldRetainFor(Player? player)
        => ModSettings.ObsoleteDesignRetentionEnabled && player?.isMain == true && player.isAi == false;

    private static void LogFirstHull(string part)
    {
        if (loggedFirstHull)
            return;

        loggedFirstHull = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP design option: retaining obsolete hull availability for player designs. First retained hull id: {part}.");
    }

    private static void LogFirstHullPartData(PartData part)
    {
        if (loggedFirstHullPartData)
            return;

        loggedFirstHullPartData = true;
        string label = part?.name ?? "<unknown>";
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP design option: retaining obsolete hull (PartData) for player designs. First retained hull: {label}.");
    }

    private static void LogFirstSubmarine(string name)
    {
        if (loggedFirstSubmarine)
            return;

        loggedFirstSubmarine = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP design option: retaining obsolete submarine availability for player designs. First retained submarine id: {name}.");
    }
}

[HarmonyPatch]
internal static class DesignObsoleteComponentRetentionPatch
{
    private static bool loggedFirstComponent;

    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(Ship), nameof(Ship.IsComponentAvailable), new[] { typeof(ComponentData), typeof(string).MakeByRefType() })
           ?? throw new MissingMethodException(nameof(Ship), nameof(Ship.IsComponentAvailable));

    [HarmonyPostfix]
    internal static void IsComponentAvailablePostfix(Ship __instance, ComponentData component, ref string reason, ref bool __result)
    {
        if (__result || !IsObsoleteReason(reason) || !ShouldRetainFor(__instance?.player))
            return;

        __result = true;
        reason = string.Empty;
        LogFirstComponent(component);
    }

    private static bool ShouldRetainFor(Player? player)
        => ModSettings.ObsoleteDesignRetentionEnabled && player?.isMain == true && player.isAi == false;

    private static bool IsObsoleteReason(string? reason)
        => string.Equals(reason, "obsolete", StringComparison.OrdinalIgnoreCase);

    private static void LogFirstComponent(ComponentData component)
    {
        if (loggedFirstComponent)
            return;

        loggedFirstComponent = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP design option: retaining obsolete component availability for player designs. First retained component: {ComponentLabel(component)}.");
    }

    private static string ComponentLabel(ComponentData? component)
    {
        string? label = component?.nameUi;
        if (string.IsNullOrWhiteSpace(label))
            label = component?.nameShort;
        if (string.IsNullOrWhiteSpace(label))
            label = component?.name;

        return string.IsNullOrWhiteSpace(label) ? "unknown" : label;
    }
}
