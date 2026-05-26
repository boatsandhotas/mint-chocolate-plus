using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Workaround for vanilla silently dropping refit-design saves on obsolete-hull
// classes. Diagnostic trace established that vanilla's flow is:
//   1. Ui.RefitShip(source) clones the source into a new refit-design and adds
//      it to Ui.newRefitShips.
//   2. Player edits in Constructor.
//   3. Ui.SaveDesignOnExitFromConstructor runs.
//      - For valid classes: moves the in-flight clone from newRefitShips into
//        player.designs and fires Ui.ReportNewRefitDesign.
//      - For obsolete-hull classes: short-circuits in ~1ms, calls
//        TryToEraseVessel on the clone, and never adds to player.designs.
//        isConstructorRefitMode flips True → False.
// The "obsolete" inline check inside SaveDesignOnExitFromConstructor isn't a
// method call we can intercept (Cecil sees stripped bodies; Cpp2IL CallAnalyzer
// confirmed only the call graph, not conditionals).
//
// This patch sandwiches the save method: prefix snapshots newRefitShips when
// refit mode is active; postfix scans the snapshot and, for any clone that is
// not isErased and is NOT in player.designs, re-commits it (adds to designs +
// fires the report hook). Gated on ObsoleteDesignRetentionEnabled to match the
// rest of the obsolete-retention behavior; only acts for the main player so AI
// design pipelines stay untouched.
[HarmonyPatch]
internal static class ObsoleteRefitRescuePatch
{
    private static readonly List<Ship> SaveEntrySnapshot = new();
    private static bool SaveEntryWasRefitMode;
    private static bool LoggedRescueOnce;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Ui), nameof(Ui.SaveDesignOnExitFromConstructor))]
    private static void BeforeSave()
    {
        SaveEntrySnapshot.Clear();
        SaveEntryWasRefitMode = false;

        try
        {
            if (!ModSettings.ObsoleteDesignRetentionEnabled) return;

            Ui? ui = G.ui;
            if (ui == null || !ui.isConstructorRefitMode) return;

            SaveEntryWasRefitMode = true;

            Il2CppSystem.Collections.Generic.HashSet<Ship>? newRefits = ui.newRefitShips;
            if (newRefits == null) return;

            foreach (Ship s in newRefits)
                if (s != null) SaveEntrySnapshot.Add(s);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP refit rescue: prefix snapshot threw. {ex.GetType().Name}: {ex.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Ui), nameof(Ui.SaveDesignOnExitFromConstructor))]
    private static void AfterSave()
    {
        if (!SaveEntryWasRefitMode || SaveEntrySnapshot.Count == 0)
            return;

        try
        {
            Player? player = ExtraGameData.MainPlayer();
            if (player == null) return;

            Il2CppSystem.Collections.Generic.List<Ship>? designs = player.designs;
            if (designs == null) return;

            int rescued = 0;
            foreach (Ship snap in SaveEntrySnapshot)
            {
                if (snap == null) continue;
                if (snap.isErased) continue;          // vanilla deliberately erased it — leave it
                if (DesignsContains(designs, snap)) continue;  // vanilla already committed it

                designs.Add(snap);
                rescued++;

                try { G.ui?.ReportNewRefitDesign(snap); }
                catch (Exception ex)
                {
                    Melon<UADVanillaPlusMod>.Logger.Warning(
                        $"UADVP refit rescue: ReportNewRefitDesign threw for {SafeName(snap)}. {ex.GetType().Name}: {ex.Message}");
                }

                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP refit rescue: re-committed dropped refit design '{SafeName(snap)}' " +
                    $"(hull={SafeHull(snap)}) to player.designs.");
            }

            if (rescued > 0 && !LoggedRescueOnce)
            {
                LoggedRescueOnce = true;
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    "UADVP refit rescue: first save-time rescue completed. Subsequent rescues will still log per-design but not this banner.");
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP refit rescue: postfix threw. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SaveEntrySnapshot.Clear();
            SaveEntryWasRefitMode = false;
        }
    }

    private static bool DesignsContains(Il2CppSystem.Collections.Generic.List<Ship> designs, Ship target)
    {
        foreach (Ship d in designs)
            if (d != null && d.Pointer == target.Pointer) return true;
        return false;
    }

    private static string SafeName(Ship? s)
    {
        try { return s?.name ?? "<unnamed>"; }
        catch { return "<name-err>"; }
    }

    private static string SafeHull(Ship? s)
    {
        try { return s?.hull?.data?.name ?? "<no-hull>"; }
        catch { return "<hull-err>"; }
    }
}
