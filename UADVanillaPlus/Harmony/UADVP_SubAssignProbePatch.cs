using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// THROWAWAY DIAGNOSTIC PROBE — remove before any real release.
// Pure logging, NO game state is modified. Every line is prefixed "UADVP_SUBPROBE"
// so it can be grep-filtered out of the MelonLoader log.
//
// Purpose: de-risk the planned PLAYER-side auto-assignment QoL feature (port the
// offline save-tool's two systems — submarine->port and patrol->region — into the
// uad-vp runtime). It answers the open runtime questions:
//   (A) Submarine model: how the human's submarines enumerate at runtime, which
//       SubmarineType.mines values mark a minelayer, and — critically — WHICH port
//       field reflects "this sub is based here" (PortLocation vs
//       VesselBuildingSelectedLocation) for already-built vs under-construction subs.
//       This decides whether the feature can mirror the save tool's passive field
//       write or must form/move task forces.
//   (B) Port need model: enumerate the human's controlled ports with capacity and the
//       count of minelayers currently based at each -> the per-port deficit the
//       feature will sort by.
//   (C) Region/patrol need model: for each area the human has presence in, the
//       NeededTonnage / AreaCurrentTonnage / AreaRequiredTonnage values, plus a count
//       of idle DD/CL/CA available to dispatch -> validates the region need-sort and
//       confirms the ship-type codes.
//
// Detailed tables dump ONCE (first resolved turn); a compact summary logs every turn.
[HarmonyPatch]
internal static class UADVP_SubAssignProbePatch
{
    private const int TargetMinelayersPerPort = 1; // probe-only: just to show a deficit column
    private const int MaxSubDetail = 80;
    private const int MaxPortDetail = 200;
    private const int MaxAreaDetail = 80;
    private const int MaxSubDumps = 8; // submarine-only re-dumps to catch the building->built field transition

    private static int _turn;
    private static bool _dumpedBaseline;   // full baseline dump (ports/areas/subs) done once
    private static int _subDumps;          // count of submarine-only dumps emitted so far

    private static void Log(string msg) => Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_SUBPROBE " + msg);

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    // Human-correlatable area label: first province id in the area (areas have no
    // top-level Id property; the Store does, but we avoid ToStore() side effects).
    private static string AreaLabel(Area? a)
    {
        if (a == null)
            return "-";

        return Safe(() =>
        {
            var ps = a.Provinces;
            if (ps != null)
                foreach (Province p in ps)
                    if (p != null)
                        return "@" + p.Id;
            return a.Pointer.ToString();
        }, "?");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CampaignController), "OnNewTurn")]
    private static void OnNewTurnPostfix(CampaignController __instance)
    {
        try
        {
            _turn++;
            string year = Safe(() => __instance.CurrentDate.ToString(true), "?");

            var data = __instance.CampaignData;
            if (data == null)
            {
                Log($"turn#{_turn} year={year}: no CampaignData");
                return;
            }

            // ----- locate the human player -----
            Player? human = null;
            var players = Safe(() => data.Players, null);
            if (players != null)
                foreach (Player p in players)
                    if (p != null && Safe(() => p.isMain, false))
                    {
                        human = p;
                        break;
                    }

            if (human == null)
            {
                Log($"turn#{_turn} year={year}: no human (isMain) player found");
                return;
            }

            string humanName = Safe(() => human.data?.name, "human") ?? "human";

            // ----- (A) submarines owned by the human -----
            int subTotal = 0, mineTotal = 0, mineInPort = 0, mineIdle = 0;
            var minelayersByPort = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeSeen = new Dictionary<string, string>(StringComparer.Ordinal); // typeId -> formatted descriptor
            var subDetail = new List<string>();

            var subs = Safe(() => data.GetSubmarines, null);
            if (subs != null)
            {
                foreach (Submarine sub in subs)
                {
                    if (sub == null)
                        continue;
                    if (!Safe(() => sub.player?.isMain ?? false, false))
                        continue;
                    if (Safe(() => sub.isSunk || sub.isScrapped, false))
                        continue;

                    subTotal++;

                    var t = Safe(() => sub.Type, null);
                    string typeId = Safe(() => t?.type, "?") ?? "?";
                    int mines = Safe(() => t?.mines ?? 0, 0);
                    bool isMine = mines > 0;

                    string? portId = Safe(() => sub.PortLocation?.Id, null);
                    string? buildId = Safe(() => sub.VesselBuildingSelectedLocation?.Id, null);
                    string? prevId = Safe(() => sub.PrevPortLocation?.Id, null);
                    string atSea = AreaLabel(Safe(() => sub.location, null));
                    string role = Safe(() => sub.CurrentRole.ToString(), "?");
                    string? basePort = portId ?? buildId;

                    if (!typeSeen.ContainsKey(typeId))
                        typeSeen[typeId] = $"type={typeId} nameUi=\"{Safe(() => t?.nameUi, "?")}\" mines={mines} speed={Safe(() => t?.speed ?? 0f, 0f):0.0} -> {(isMine ? "MINELAYER" : "attack")}";

                    if (isMine)
                    {
                        mineTotal++;
                        if (basePort != null)
                        {
                            minelayersByPort.TryGetValue(basePort, out int c);
                            minelayersByPort[basePort] = c + 1;
                            if (portId != null)
                                mineInPort++;
                        }
                        else
                        {
                            mineIdle++;
                        }
                    }

                    if (subDetail.Count < MaxSubDetail)
                        subDetail.Add($"sub \"{Safe(() => sub.Name(true), "?")}\" type={typeId} mines={mines} role={role} port={portId ?? "-"} build={buildId ?? "-"} prev={prevId ?? "-"} sea={atSea}");
                }
            }

            // ----- (B) the human's controlled ports + distinct areas -----
            var portRows = new List<string>();
            var seenPorts = new HashSet<string>(StringComparer.Ordinal);
            var areaSet = new Dictionary<IntPtr, Area>();
            int portTotal = 0, portsZeroMinelayer = 0;

            var byPlayer = Safe(() => data.ProvincesByPlayer, null);
            if (byPlayer != null)
            {
                foreach (var kvp in byPlayer)
                {
                    var provs = kvp.Value;
                    if (provs == null)
                        continue;

                    foreach (Province pr in provs)
                    {
                        if (pr == null)
                            continue;
                        if (!Safe(() => pr.ControllerPlayer?.isMain ?? false, false))
                            continue;

                        Area? ar = Safe(() => pr.CurrentArea, null);
                        if (ar != null)
                        {
                            IntPtr ap = Safe(() => ar.Pointer, IntPtr.Zero);
                            if (ap != IntPtr.Zero && !areaSet.ContainsKey(ap))
                                areaSet[ap] = ar;
                        }

                        var ports = Safe(() => pr.Ports, null);
                        if (ports == null)
                            continue;

                        foreach (PortElement pe in ports)
                        {
                            if (pe == null)
                                continue;
                            string? pid = Safe(() => pe.Id, null);
                            if (pid == null || !seenPorts.Add(pid))
                                continue;

                            portTotal++;
                            int cap = Safe(() => pe.GetPortCapacityWithoutDamage(), 0);
                            minelayersByPort.TryGetValue(pid, out int mlc);
                            if (mlc == 0)
                                portsZeroMinelayer++;

                            if (portRows.Count < MaxPortDetail)
                                portRows.Add($"port {pid} prov={Safe(() => pr.Id, "?")} area={AreaLabel(ar)} cap={cap} minelayers={mlc} deficit={Math.Max(0, TargetMinelayersPerPort - mlc)}");
                        }
                    }
                }
            }

            // ----- (C) per-area tonnage (patrol/region need) -----
            var areaRows = new List<string>();
            foreach (Area ar in areaSet.Values)
            {
                float cur = Safe(() => __instance.AreaCurrentTonnage(ar, human), -1f);
                float need = Safe(() => __instance.NeededTonnage(ar, human, false), -1f);
                float req = Safe(() => __instance.AreaRequiredTonnage(ar, human), -1f);
                if (areaRows.Count < MaxAreaDetail)
                    areaRows.Add($"area {AreaLabel(ar)} current={cur:0} needed={need:0} required={req:0} deficit={Math.Max(0f, need - cur):0}");
            }

            // ----- idle surface patrol ships (DD/CL/CA) -----
            int patrolTotal = 0, patrolInPort = 0;
            var typeCodes = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (Ship s in human.GetFleetAll())
                {
                    if (s == null)
                        continue;
                    if (Safe(() => s.isSunk || s.isScrapped || s.isDesign, false))
                        continue;
                    string? code = Safe(() => s.shipType?.name, null);
                    if (string.IsNullOrEmpty(code))
                        continue;
                    code = code.ToLowerInvariant();
                    typeCodes.Add(code);
                    if (code == "dd" || code == "cl" || code == "ca")
                    {
                        patrolTotal++;
                        if (Safe(() => s.PortLocation != null, false))
                            patrolInPort++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("fleet enum error: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Detail dump policy: a full baseline dump once (ports/areas/subs), then a compact
            // SUBMARINE-only dump on each of the next few sub-bearing turns — so we capture the
            // building->built field transition without re-spamming the 110-port table every turn.
            if (!_dumpedBaseline)
            {
                Log($"=== DETAIL DUMP turn#{_turn} year={year} player={humanName} ===");
                Log($"submarine types seen ({typeSeen.Count}):");
                foreach (var kv in typeSeen)
                    Log("  " + kv.Value);
                Log($"submarines ({subDetail.Count} shown of {subTotal}):");
                foreach (string r in subDetail)
                    Log("  " + r);
                Log($"ports ({portRows.Count} shown of {portTotal}):");
                foreach (string r in portRows)
                    Log("  " + r);
                Log($"areas ({areaRows.Count} shown of {areaSet.Count}):");
                foreach (string r in areaRows)
                    Log("  " + r);
                Log($"fleet ship-type codes seen: [{string.Join(",", typeCodes)}]");
                _dumpedBaseline = true;
                if (subTotal > 0)
                    _subDumps++;
                else
                    Log("NOTE: player has 0 submarines — build/acquire minelayer subs and advance turns to capture sub data (in-building subs are fine).");
            }
            else if (subTotal > 0 && _subDumps < MaxSubDumps)
            {
                _subDumps++;
                Log($"=== SUB DUMP #{_subDumps} turn#{_turn} year={year} ===");
                Log($"submarine types seen ({typeSeen.Count}):");
                foreach (var kv in typeSeen)
                    Log("  " + kv.Value);
                Log($"submarines ({subDetail.Count} shown of {subTotal}):");
                foreach (string r in subDetail)
                    Log("  " + r);
            }

            // ----- every turn: compact summary -----
            Log($"turn#{_turn} year={year} player={humanName} subs={subTotal} minelayers={mineTotal} (inPort={mineInPort} idle={mineIdle}) " +
                $"ports={portTotal} portsNoMinelayer={portsZeroMinelayer} | patrol(dd/cl/ca)={patrolTotal} inPort={patrolInPort} areas={areaSet.Count}");
        }
        catch (Exception ex)
        {
            Log("OnNewTurn error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
