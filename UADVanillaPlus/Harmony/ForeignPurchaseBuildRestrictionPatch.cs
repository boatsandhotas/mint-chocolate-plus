using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Foreign-purchase build restriction (refit-yes / build-no). A ship CLASS the player BOUGHT from an
// allied major may be REFITTED but never built anew (buy more from the ally). Vanilla's build gate
// (PlayerController.CanBuildShipsFromDesign x2 + CanBuildFromDesignOnInit) would let the player queue
// new copies of the purchased design, so this postfix forces the gate to FALSE whenever the design's
// own identity Guid — or any Guid in its refit lineage (refitDesignListID) — is in the mod-side
// purchased-restricted set. [HarmonyPriority(Priority.Last)] so its FALSE wins over the obsolete-
// retention (force-true) and AI arms-race (force-false) postfixes on the same methods. Keyed on
// design.player / the design Guid, never __instance.
[HarmonyPatch]
internal static class ForeignPurchaseBuildRestrictionPatch
{
    private const string RestrictedReason = "Purchased from ally — refit only; buy more from your ally to build new.";
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? m;
        m = AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign),
            new[] { typeof(Ship), typeof(int), typeof(string).MakeByRefType() });
        if (m != null) yield return m;
        m = AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign),
            new[] { typeof(Ship), typeof(string).MakeByRefType() });
        if (m != null) yield return m;
        m = AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildFromDesignOnInit),
            new[] { typeof(Ship), typeof(int), typeof(bool), typeof(string).MakeByRefType() });
        if (m != null) yield return m;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Ship design, ref string reason, ref bool __result)
    {
        if (!__result || design == null) // only ever BLOCK; never un-block
            return;

        try
        {
            if (!ModSettings.AllyShipPurchaseEnabled)
                return;

            Player? p = design.player; // VesselEntity.player (inherited)
            if (p == null || !p.isMain || p.isAi)
                return;

            if (!IsRestrictedLineage(design))
                return;

            // Refit-yes / build-no. Both the refit-editor build button and the design-list "build new" go
            // through CanBuildShipsFromDesign, so we must tell them apart by CONTEXT. The game's own live
            // Ui.isConstructorRefitMode flag is the authoritative signal: true only while the ship designer
            // is in refit mode (a refit COMMIT -> allow); false for any design-list build, whether of the
            // base design OR a saved refit design (-> block, closing the buy-1-refit-build-many loophole).
            // (Earlier attempts — a mod flag toggled on Ui.ExitFromRefitMode, then design.isRefitDesign —
            // failed: the exit hook doesn't fire so the flag stuck true; and a saved refit design is itself
            // isRefitDesign=true so that let new copies of refits through.)
            if (RefitModeTracker.IsInRefitEditor())
            {
                LogExemptOnce(design);
                return;
            }

            __result = false;
            reason = RestrictedReason;
            LogOnce(design);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP foreign-purchase build gate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsRestrictedLineage(Ship design)
    {
        HashSet<string> restricted = AllyPurchaseState.RestrictedDesignIds;
        if (restricted.Count == 0)
            return false;

        string self = GuidStr(design);
        if (self.Length > 0 && restricted.Contains(self))
            return true;

        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppSystem.Guid> lineage = design.refitDesignListID;
            if (lineage != null)
            {
                foreach (Il2CppSystem.Guid g in lineage)
                {
                    string s;
                    try { s = g.ToString() ?? string.Empty; } catch { s = string.Empty; }
                    if (s.Length > 0 && restricted.Contains(s))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static string GuidStr(Ship design)
    {
        try { return design.id.ToString() ?? string.Empty; } // VesselEntity.id -> Il2CppSystem.Guid
        catch { return string.Empty; }
    }


    private static void LogOnce(Ship design)
    {
        try
        {
            string id = GuidStr(design);
            if (!Logged.Add(id)) return;
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_ALLYBUY blocked NEW build of purchased class (designId={id}); refit still allowed.");
        }
        catch { }
    }

    private static void LogExemptOnce(Ship design)
    {
        try
        {
            string id = GuidStr(design);
            if (!Logged.Add("exempt:" + id)) return;
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_ALLYBUY refit-editor: ALLOWING build of purchased-lineage design (designId={id}) — Ui.isConstructorRefitMode=true (refit commit, not new build).");
        }
        catch { }
    }
}
