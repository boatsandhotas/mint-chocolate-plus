using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Stability guard: vanilla GunData.BaseWeight and PartData.GetCaliber run a
// FirstOrDefault over ship.shipGunCaliber without null-guarding the entry or its
// turretPartData (the only reference-type field on Ship.TurretCaliber). A live
// design/ship whose turretPartData failed to resolve (null) makes that predicate
// throw NullReferenceException. Inside CampaignController.UpdateAllShipsWeightCost
// the throw propagates through Parallel.ForEach as an AggregateException and aborts
// the entire per-turn weight/cost update for every ship.
//
// Reloading the campaign heals it because Ship.FromStore re-resolves the serialized
// turretPartDataName (a string) back into a real PartData; the live object only keeps
// the resolved PartData and can be left null. This prefix repairs/scrubs the caliber
// list at the common chokepoint (Ship.CWeight, which both crash stacks pass through)
// so the native calc can never NRE on it, and logs the offending ship so the true
// source can be traced.
[HarmonyPatch(typeof(Ship), nameof(Ship.CWeight), new Type[] { typeof(bool) })]
internal static class ShipGunCaliberRepairPatch
{
    [ThreadStatic] private static bool _inRepair;
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly object LogLock = new();

    [HarmonyPrefix]
    private static void Prefix(Ship __instance)
    {
        if (_inRepair)
            return;

        try
        {
            Ship ship = __instance;
            if (ship == null || !HasBadEntry(ship))
                return; // fast path: clean (the overwhelmingly common case)

            // First attempt: let vanilla reconcile calibers from the ship's actual gun parts.
            _inRepair = true;
            try { ship.CheckCaliberOnShip(null); }
            catch { }
            finally { _inRepair = false; }

            if (!HasBadEntry(ship))
            {
                LogRepair(ship, "checkCaliber", -1);
                return;
            }

            // Last resort: drop entries that would NRE the native FirstOrDefault.
            var calibers = ship.shipGunCaliber;
            if (calibers == null)
                return;

            int removed = 0;
            for (int i = calibers.Count - 1; i >= 0; i--)
            {
                if (IsBad(calibers[i]))
                {
                    try { calibers.RemoveAt(i); removed++; }
                    catch { }
                }
            }

            LogRepair(ship, "dropped", removed);
        }
        catch
        {
            // never let the guard itself break weight calc
        }
        finally
        {
            _inRepair = false;
        }
    }

    private static bool HasBadEntry(Ship ship)
    {
        var calibers = ship.shipGunCaliber;
        if (calibers == null)
            return false;

        int n = calibers.Count;
        for (int i = 0; i < n; i++)
        {
            if (IsBad(calibers[i]))
                return true;
        }

        return false;
    }

    private static bool IsBad(Ship.TurretCaliber caliber)
    {
        if (caliber == null)
            return true;

        try { return caliber.turretPartData == null; }
        catch { return true; }
    }

    private static void LogRepair(Ship ship, string action, int removed)
    {
        try
        {
            string type = AiDesignCompetitiveness.NormalizeShipType(ship.shipType);
            string name = AiDesignCompetitiveness.ShipLabel(ship);
            string key = type + ":" + name + ":" + action;
            lock (LogLock)
            {
                if (!Logged.Add(key))
                    return;
                if (Logged.Count > 512)
                    Logged.Clear();
            }

            string dropped = removed >= 0 ? " dropped=" + removed : string.Empty;
            Melon<UADVanillaPlusMod>.Logger.Warning(
                "UADVP gun-caliber-repair ship=\"" + name + "\" type=" + type + " action=" + action + dropped +
                " - null turretPartData in shipGunCaliber would have NRE'd GunData.BaseWeight; repaired to protect UpdateAllShipsWeightCost.");
        }
        catch
        {
        }
    }
}
