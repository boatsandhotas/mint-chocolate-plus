using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// Region-weakness model for the patrol tool (lean on the save tool's PatrolShips.LogPlayerRegions /
// AutoPatrol). For the human player: rank the areas it has a presence in by tonnage DEFICIT
// (AreaRequiredTonnage - AreaCurrentTonnage) = "where I'm weakest", and list in-port ships that can be
// dispatched. Dispatch itself goes through the shared VesselAssignment core.
internal static class PatrolPlanner
{
    internal sealed class RegionRow
    {
        public Area Area = null!;
        public string Label = "";
        public PortElement TargetPort = null!; // default destination = largest controlled port
        public readonly List<PortElement> AllPorts = new(); // all controlled ports, largest first
        public readonly List<string> PortNames = new();     // display names, parallel to AllPorts
        public readonly List<int> PortCaps = new();         // port capacity, parallel to AllPorts
        public readonly List<int> PortShipCounts = new();   // vessels currently in that port
        public readonly List<float> PortTons = new();       // tonnage currently in that port
        public float Current;
        public float Required;
        public float Deficit;
        public int Ports;
    }

    internal sealed class ShipRow
    {
        public Ship Ship = null!;
        public string Name = "?";
        public string Type = "?";   // hull type (bb/cl/dd)
        public string Class = "?";  // className (the design/class, e.g. "Bellerophon")
        public bool IsBuilding;
        public float Tonnage;
        public string PortId = "?";
        public string Loc = "?";    // display location: port name, or "building @ X"
    }

    internal sealed class PatrolPlan
    {
        public bool HasHuman;
        public Player Human = null!;
        public readonly List<RegionRow> Regions = new();
        public readonly List<ShipRow> Pool = new();
    }

    private static bool _loggedPool; // one-time pool diagnostic dump

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    internal static PatrolPlan Compute()
    {
        var plan = new PatrolPlan();
        var cc = CampaignController.Instance;
        if (cc == null) return plan;
        var data = cc.CampaignData;
        if (data == null) return plan;

        Player? human = null;
        var players = Safe(() => data.Players, null);
        if (players != null)
            foreach (Player p in players)
                if (p != null && Safe(() => p.isMain, false)) { human = p; break; }
        if (human == null) return plan;
        plan.HasHuman = true;
        plan.Human = human;

        // accumulate the human's controlled ports per area
        var rows = new Dictionary<IntPtr, RegionRow>();
        var portsByArea = new Dictionary<IntPtr, List<PortElement>>();
        var byPlayer = Safe(() => data.ProvincesByPlayer, null);
        if (byPlayer != null)
        {
            foreach (var kvp in byPlayer)
            {
                var provs = kvp.Value;
                if (provs == null) continue;
                foreach (Province pr in provs)
                {
                    if (pr == null) continue;
                    if (!Safe(() => pr.ControllerPlayer?.isMain ?? false, false)) continue;
                    Area? area = Safe(() => pr.CurrentArea, null);
                    if (area == null) continue;
                    IntPtr ap = Safe(() => area.Pointer, IntPtr.Zero);
                    if (ap == IntPtr.Zero) continue;

                    if (!portsByArea.TryGetValue(ap, out var plist))
                    {
                        portsByArea[ap] = plist = new List<PortElement>();
                        rows[ap] = new RegionRow { Area = area, Label = MinelayerPlanner.Prettify(Safe(() => pr.AreaId, "") ?? "") };
                    }
                    var ports = Safe(() => pr.Ports, null);
                    if (ports != null)
                        foreach (PortElement pe in ports)
                            if (pe != null) plist.Add(pe);
                }
            }
        }

        // current occupancy per port (vessel count + tonnage) for the destination picker
        var occ = new Dictionary<string, KeyValuePair<int, float>>(StringComparer.Ordinal);
        var vip = Safe(() => data.VesselsInPort, null);
        if (vip != null)
        {
            foreach (var entry in vip)
            {
                PortElement? pe = entry.Key;
                var vessels = entry.Value;
                string? pid = Safe(() => pe?.Id, null);
                if (pid == null || vessels == null) continue;
                int c = 0; float t = 0f;
                foreach (VesselEntity v in vessels)
                {
                    if (v == null) continue;
                    if (Safe(() => v.isSunk || v.isScrapped, false)) continue;
                    c++;
                    t += Safe(() => v.Tonnage(), 0f);
                }
                occ[pid] = new KeyValuePair<int, float>(c, t);
            }
        }

        foreach (var kv in rows)
        {
            RegionRow r = kv.Value;
            var ports = portsByArea[kv.Key];
            if (ports.Count == 0) continue;

            // sort the region's controlled ports by capacity, largest first
            var sorted = new List<PortElement>(ports);
            sorted.Sort((a, b) => Safe(() => b.GetPortCapacityWithoutDamage(), 0).CompareTo(Safe(() => a.GetPortCapacityWithoutDamage(), 0)));
            if (sorted.Count == 0) continue;
            foreach (PortElement pe in sorted)
            {
                r.AllPorts.Add(pe);
                string pn = Safe(() => pe.Name, null);
                r.PortNames.Add(string.IsNullOrWhiteSpace(pn) ? (Safe(() => pe.Id, "?") ?? "?") : pn);
                r.PortCaps.Add(Safe(() => pe.GetPortCapacityWithoutDamage(), 0));
                string pid = Safe(() => pe.Id, "") ?? "";
                if (occ.TryGetValue(pid, out var o)) { r.PortShipCounts.Add(o.Key); r.PortTons.Add(o.Value); }
                else { r.PortShipCounts.Add(0); r.PortTons.Add(0f); }
            }
            r.TargetPort = sorted[0]; // default dispatch destination = largest
            r.Ports = sorted.Count;
            r.Current = Safe(() => cc.AreaCurrentTonnage(r.Area, human), 0f);
            r.Required = Safe(() => cc.AreaRequiredTonnage(r.Area, human), 0f);
            r.Deficit = Math.Max(0f, r.Required - r.Current);
            plan.Regions.Add(r);
        }
        plan.Regions.Sort((a, b) => b.Deficit.CompareTo(a.Deficit));

        // ship pool: the human's OWN BUILDING ships (assignable via field write) + in-port, non-deployed
        // ships (dispatchable via move). Grouped by class in the panel. Ships built for OTHER nations
        // (player.isMain == false) are excluded.
        bool dumpOnce = !_loggedPool;
        int dbgCount = 0;
        try
        {
            foreach (Ship s in human.GetFleetAll())
            {
                if (s == null) continue;
                if (Safe(() => s.isSunk || s.isScrapped || s.isDesign, false)) continue;

                bool mine = Safe(() => s.player?.isMain ?? false, false);
                // ForSaleTo != null = this hull is being built UNDER CONTRACT for another nation (the
                // real owner is ForSaleTo, even though the builder/controller `player` reads as us).
                bool forSale = Safe(() => s.ForSaleTo != null, false);

                // class = the design ship's name (s.design is the empty-Guid design template)
                string clsLabel = Safe(() => s.design != null ? s.design.Name(false, false, false, false, true) : null, null)
                                  ?? (Safe(() => s.shipType?.name, "?") ?? "?");
                bool sailing = Safe(() => s.IsSailing, false);

                // one-time diagnostic for EVERY candidate (before filtering): pin down ownership + class identity.
                if (dumpOnce && dbgCount < 200)
                {
                    dbgCount++;
                    bool grouped = Safe(() => s.SeaGroupId.ToString() != "00000000-0000-0000-0000-000000000000", false);
                    Melon<MintChipPlusMod>.Logger.Msg(
                        $"UADMC_PATROLDBG class=\"{clsLabel}\" type={Safe(() => s.shipType?.name, "?")} owner={Safe(() => s.player?.data?.name, "?")} isMain={mine} forSale={forSale} saleTo={Safe(() => s.ForSaleTo?.name, "-")} building={Safe(() => s.isBuilding, false)} sailing={sailing} grouped={grouped} port={Safe(() => s.PortLocation?.Id, "-")}");
                }

                if (!mine || forSale) continue; // ONLY my own ships (exclude contracts built for other nations)

                // Include ALL of my ships regardless of current state — docked, out at sea on patrol, or
                // building — so a flight that's underway (sailing, but homed at a port) is still dispatchable.
                bool building = Safe(() => s.isBuilding, false);
                PortElement? port = Safe(() => s.PortLocation, null);

                string portName = port != null && Safe(() => !string.IsNullOrWhiteSpace(port.Name), false) ? port.Name : Safe(() => port?.Id, "") ?? "";
                string loc;
                if (building)
                {
                    string bp = Safe(() => s.VesselBuildingSelectedLocation?.Name, null) ?? Safe(() => s.VesselBuildingSelectedLocation?.Id, null) ?? "";
                    loc = string.IsNullOrWhiteSpace(bp) ? "building" : ("building @ " + bp);
                }
                else if (port != null)
                {
                    loc = "@ " + portName; // homed/docked here (IsSailing is unreliable, so don't label "at sea")
                }
                else
                {
                    loc = "at sea";
                }

                plan.Pool.Add(new ShipRow
                {
                    Ship = s,
                    Name = Safe(() => s.Name(false, true), "?") ?? "?", // full name with the game's color markup
                    Type = Safe(() => s.shipType?.name, "?") ?? "?",
                    Class = clsLabel,
                    IsBuilding = building,
                    Tonnage = Safe(() => s.tonnage, 0f),
                    PortId = Safe(() => port?.Id, "?") ?? "?",
                    Loc = loc,
                });
            }
            if (dumpOnce) _loggedPool = true;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC patrol pool enumeration failed: {ex.GetType().Name}: {ex.Message}");
        }

        return plan;
    }
}
