using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Shared "assign a vessel to a port" core — the unified concept behind both the minelayer-port and
// patrol-region tools. Works for subs and surface ships (both VesselEntity):
//   - vessel still BUILDING  -> set VesselBuildingSelectedLocation (field write, no movement)
//   - vessel already BUILT   -> CampaignShipsMovementManager.MoveVessels(...) to the destination port
//
// Heavy logging (prefix UADVP_VASSIGN) on every path we have not yet proven at runtime — especially the
// MoveVessels dispatch (does it form a task force / find a route?) and the field-write read-back.
internal static class VesselAssignment
{
    private static void Log(string m) => Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_VASSIGN " + m);

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    internal static bool IsBuilding(VesselEntity v) => Safe(() => v.isBuilding, false);

    private static string Name(VesselEntity v) => Safe(() => v.Name(true), "?") ?? "?";
    private static string PortId(PortElement? p) => Safe(() => p?.Id, "?") ?? "?";

    // Assign one vessel. Returns true on success.
    internal static bool AssignToPort(VesselEntity vessel, PortElement port)
    {
        if (vessel == null || port == null) return false;
        if (IsBuilding(vessel))
            return BuildAssign(vessel, port);
        return MoveBuilt(new List<VesselEntity> { vessel }, port) > 0;
    }

    // Assign many vessels to ONE port: field-write the building ones, batch-move the built ones.
    internal static int AssignManyToPort(List<VesselEntity> vessels, PortElement port)
    {
        if (vessels == null || port == null) return 0;
        int done = 0;
        var built = new List<VesselEntity>();
        foreach (VesselEntity v in vessels)
        {
            if (v == null) continue;
            if (IsBuilding(v)) { if (BuildAssign(v, port)) done++; }
            else built.Add(v);
        }
        if (built.Count > 0)
            done += MoveBuilt(built, port);
        return done;
    }

    // Building vessel: set the build/home port field (the save tool's ShipBuildingPortLocation write).
    private static bool BuildAssign(VesselEntity v, PortElement port)
    {
        bool ok = Safe(() => { v.VesselBuildingSelectedLocation = port; return true; }, false);
        string rb = Safe(() => v.VesselBuildingSelectedLocation?.Id, "?") ?? "?";
        Log($"build-assign \"{Name(v)}\" -> {PortId(port)}  ok={ok} read-back={rb}");
        return ok;
    }

    // Built vessels: dispatch via MoveVessels. Group by current origin port so each call has a sane `from`.
    // Returns how many vessels were dispatched (best-effort; we count the whole group on a successful call).
    private static int MoveBuilt(List<VesselEntity> built, PortElement dest)
    {
        int moved = 0;
        var byOrigin = new Dictionary<string, KeyValuePair<PortElement, List<VesselEntity>>>(StringComparer.Ordinal);
        var noOrigin = new List<VesselEntity>();

        foreach (VesselEntity v in built)
        {
            PortElement? op = Safe(() => v.PortLocation, null);
            string? oid = Safe(() => op?.Id, null);
            if (op != null && oid != null)
            {
                if (!byOrigin.TryGetValue(oid, out var g))
                    byOrigin[oid] = g = new KeyValuePair<PortElement, List<VesselEntity>>(op, new List<VesselEntity>());
                g.Value.Add(v);
            }
            else
            {
                noOrigin.Add(v);
            }
        }

        foreach (var kv in byOrigin)
            if (MoveVesselsCall(kv.Value.Value, kv.Value.Key, dest))
                moved += kv.Value.Value.Count;

        // UNSURE: vessels with no current PortLocation (idle / at sea) — what should `from` be? Trying dest
        // as a placeholder; logged so we can see whether MoveVessels still routes them. May need refinement.
        if (noOrigin.Count > 0)
        {
            Log($"MoveBuilt: {noOrigin.Count} vessel(s) have no current PortLocation — using dest as `from` (UNVERIFIED).");
            if (MoveVesselsCall(noOrigin, dest, dest))
                moved += noOrigin.Count;
        }

        return moved;
    }

    private static bool MoveVesselsCall(List<VesselEntity> list, PortElement from, PortElement to)
    {
        var il2 = new Il2CppSystem.Collections.Generic.List<VesselEntity>();
        foreach (VesselEntity v in list)
            if (v != null) il2.Add(v);
        if (il2.Count == 0) return false;

        CampaignController.TaskForce? tf = null;
        bool ok = false;
        string err = "";
        try
        {
            Move fromMove = Move.Port(from);
            Move toMove = Move.Port(to);
            tf = CampaignShipsMovementManager.MoveVessels(il2, fromMove, toMove, MoveSettings.Empty, true);
            ok = tf != null;
        }
        catch (Exception ex)
        {
            err = $" EX={ex.GetType().Name}:{ex.Message}";
        }

        string tfId = tf != null ? Safe(() => tf.Id.ToString(), "?") : "null";
        Log($"MoveVessels n={il2.Count} from={PortId(from)} to={PortId(to)} -> tf={tfId} ok={ok}{err}");

        try { CampaignMap.UpdateTaskForcePositions(); CampaignMap.UI?.RefreshMovingGroups(false); }
        catch (Exception ex) { Log($"map refresh EX {ex.GetType().Name}: {ex.Message}"); }

        return ok;
    }
}
