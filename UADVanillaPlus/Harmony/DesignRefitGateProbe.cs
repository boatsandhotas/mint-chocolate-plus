using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// TEMP diagnostic: find the exact gate that blocks CREATING a refit of an obsolete-hull ship. VP already
// patches IsHullObsolete / IsComponentAvailable(out string) / CanBuildShipsFromDesign (Retain), and the
// log shows those work — so the refit-creation block is elsewhere: most likely the bool-only
// Ship.IsPartAvailable(PartData) / Ship.IsComponentAvailable(ComponentData) (no reason string), or an
// erased base design. This logs a refit attempt + every availability FALSE during it, so the next refit
// attempt on an obsolete hull names the culprit. Gated behind Battle Runtime Diagnostics.
internal static class DesignRefitGateProbe
{
    internal static bool Active;
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    internal static void Begin(Ship? ship)
    {
        if (!ModSettings.BattleRuntimeDiagnosticsEnabled)
            return;
        Active = true;
        Logged.Clear();
        try
        {
            string name = SafeName(ship);
            string hull = "?"; try { hull = ship != null ? ship.hull?.name ?? "?" : "?"; } catch { }
            bool isDesign = false, isRefit = false, isErased = false;
            try { if (ship != null) isDesign = ship.isDesign; } catch { }
            try { if (ship != null) isRefit = ship.isRefitDesign; } catch { }
            try { if (ship != null) isErased = ship.isErased; } catch { }
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP_REFITPROBE refit attempt: ship='{name}' hull={hull} isDesign={isDesign} isRefitDesign={isRefit} isErased={isErased}");
        }
        catch { }
    }

    internal static void End()
    {
        if (Active)
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_REFITPROBE refit mode exited.");
        Active = false;
    }

    internal static void LogAvailFalse(string method, Ship? owner, string itemName)
    {
        try
        {
            if (!Active)
                return;
            string key = method + "|" + itemName;
            if (!Logged.Add(key) || Logged.Count > 60)
                return;
            bool isRefit = false, isDesign = false, isErased = false;
            try { if (owner != null) isRefit = owner.isRefitDesign; } catch { }
            try { if (owner != null) isDesign = owner.isDesign; } catch { }
            try { if (owner != null) isErased = owner.isErased; } catch { }
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP_REFITPROBE BLOCK {method}=FALSE item={itemName} on[isRefitDesign={isRefit} isDesign={isDesign} isErased={isErased}]");
        }
        catch { }
    }

    internal static string SafeName(Ship? s)
    {
        if (s == null) return "?";
        try { string n = s.Name(false, false, false, false, true); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        try { string v = s.vesselName; if (!string.IsNullOrWhiteSpace(v)) return v; } catch { }
        return "?";
    }

    internal static string PartName(PartData p)
    {
        try { string? n = p?.name; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return "?";
    }

    internal static string CompName(ComponentData c)
    {
        try { string? n = c?.name; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return "?";
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.RefitShip))]
internal static class DesignRefitGateProbeBeginPatch
{
    [HarmonyPostfix]
    private static void Postfix(Ship ship) => DesignRefitGateProbe.Begin(ship);
}

[HarmonyPatch(typeof(Ui), nameof(Ui.ExitFromRefitMode))]
internal static class DesignRefitGateProbeEndPatch
{
    [HarmonyPostfix]
    private static void Postfix() => DesignRefitGateProbe.End();
}

[HarmonyPatch(typeof(Ship), nameof(Ship.IsPartAvailable), new Type[] { typeof(PartData) })]
internal static class DesignRefitGateProbePartPatch
{
    [HarmonyPostfix]
    private static void Postfix(Ship __instance, PartData part, bool __result)
    {
        if (DesignRefitGateProbe.Active && !__result)
            DesignRefitGateProbe.LogAvailFalse("IsPartAvailable", __instance, DesignRefitGateProbe.PartName(part));
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.IsComponentAvailable), new Type[] { typeof(ComponentData) })]
internal static class DesignRefitGateProbeCompPatch
{
    [HarmonyPostfix]
    private static void Postfix(Ship __instance, ComponentData component, bool __result)
    {
        if (DesignRefitGateProbe.Active && !__result)
            DesignRefitGateProbe.LogAvailFalse("IsComponentAvailable", __instance, DesignRefitGateProbe.CompName(component));
    }
}

// The real symptom: a refit of an obsolete hull is saved, then VANISHES from the design tab. Most
// likely the design is erased on commit (Ship.Erase()), which VP's cull-window guard doesn't cover at
// save time. Log every design/refit erase so the next attempt shows the new refit being erased.
[HarmonyPatch(typeof(Ship), nameof(Ship.Erase), new Type[] { })]
internal static class DesignRefitGateProbeErasePatch
{
    [HarmonyPrefix]
    private static void Prefix(Ship __instance)
    {
        if (!ModSettings.BattleRuntimeDiagnosticsEnabled || __instance == null)
            return;
        try
        {
            bool isRefit = false, isDesign = false;
            try { isRefit = __instance.isRefitDesign; } catch { }
            try { isDesign = __instance.isDesign; } catch { }
            if (!isRefit && !isDesign)
                return; // only care about design/refit erases, not real ships
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP_REFITPROBE ERASE design '{DesignRefitGateProbe.SafeName(__instance)}' isRefitDesign={isRefit} isDesign={isDesign} (refitProbeActive={DesignRefitGateProbe.Active})");
        }
        catch { }
    }
}
