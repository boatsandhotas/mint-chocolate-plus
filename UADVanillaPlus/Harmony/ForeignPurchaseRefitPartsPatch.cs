using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// A ship BOUGHT from a foreign ally uses that nation's hull/components (e.g. bb_4_japan, fuel_coal),
// which the buyer hasn't unlocked — so vanilla's availability checks fail and the refit editor shows
// "Can not build: (Unknown reason)". Since the player OWNS the ship and is only REFITTING it (building
// new copies is blocked separately by ForeignPurchaseBuildRestrictionPatch), force availability — but
// ONLY for the parts/components THIS SHIP ALREADY USES (its current hull + installed components), so
// the refit is buildable without opening the whole foreign catalog in the part picker.
[HarmonyPatch(typeof(Ship))]
internal static class ForeignPurchaseRefitPartsPatch
{
    // --- IsPartAvailable: only free the ship's OWN hull ---
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(Ship.IsPartAvailable), new Type[] { typeof(PartData) })]
    private static void PartInstance(Ship __instance, PartData part, ref bool __result)
    {
        if (__result || __instance == null) return;
        if (IsPurchased(__instance) && IsShipHull(__instance, part)) __result = true;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(Ship.IsPartAvailable), new Type[] { typeof(PartData), typeof(Player), typeof(ShipType), typeof(Ship) })]
    private static void PartStatic(PartData part, Ship ship, ref bool __result)
    {
        if (__result || ship == null) return;
        if (IsPurchased(ship) && IsShipHull(ship, part)) __result = true;
    }

    // --- IsComponentAvailable (1-arg): only free the ship's OWN installed components ---
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(Ship.IsComponentAvailable), new Type[] { typeof(ComponentData) })]
    private static void CompInstance(Ship __instance, ComponentData component, ref bool __result)
    {
        if (__result || __instance == null) return;
        if (PurchasedAndUses(__instance, component)) __result = true;
    }

    // Combined check used by both this class and the by-ref overload patch below.
    internal static bool PurchasedAndUses(Ship ship, ComponentData component)
        => IsPurchased(ship) && ShipUsesComponent(ship, component);

    private static bool IsShipHull(Ship ship, PartData part)
    {
        // ship.hull is a Part (instance); part is PartData (catalog). Compare by name (e.g. bb_5_bismarck).
        try
        {
            string hn = ship.hull != null ? (ship.hull.name ?? "") : "";
            string pn = part != null ? (part.name ?? "") : "";
            return hn.Length > 0 && string.Equals(hn, pn, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool ShipUsesComponent(Ship ship, ComponentData component)
    {
        if (component == null) return false;
        try
        {
            var comps = ship.components; // Dictionary<CompType, ComponentData>
            if (comps == null) return false;
            foreach (var kv in comps)
            {
                ComponentData c = kv.Value;
                if (c != null && c.Pointer == component.Pointer) return true;
            }
        }
        catch { }
        return false;
    }

    // True when the design being checked is (or descends from) a purchased/adopted class.
    private static bool IsPurchased(Ship ship)
    {
        try
        {
            if (!ModSettings.AllyShipPurchaseEnabled) return false;
            HashSet<string> restricted = AllyPurchaseState.RestrictedDesignIds;
            if (restricted.Count == 0) return false;

            if (InSet(restricted, Safe(() => ship.id.ToString(), ""))) return true;
            if (InSet(restricted, Safe(() => ship.design != null ? ship.design.id.ToString() : "", ""))) return true;

            var lineage = Safe(() => ship.refitDesignListID, null);
            if (lineage != null)
                foreach (Il2CppSystem.Guid g in lineage)
                    if (InSet(restricted, Safe(() => g.ToString(), ""))) return true;
        }
        catch { }
        return false;
    }

    private static bool InSet(HashSet<string> set, string? id) => !string.IsNullOrEmpty(id) && set.Contains(id!);

    private static T Safe<T>(Func<T> f, T fb) { try { return f(); } catch { return fb; } }
}

// The build validation calls IsComponentAvailable(ComponentData, out string reason) — the by-ref
// overload can't be named in a [HarmonyPatch] attribute, so target it via TargetMethod().
[HarmonyPatch]
internal static class ForeignPurchaseRefitComponentReasonPatch
{
    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(Ship), nameof(Ship.IsComponentAvailable), new[] { typeof(ComponentData), typeof(string).MakeByRefType() });

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Ship __instance, ComponentData component, ref bool __result)
    {
        if (__result || __instance == null) return;
        if (ForeignPurchaseRefitPartsPatch.PurchasedAndUses(__instance, component)) __result = true;
    }
}
