using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// THROWAWAY DIAGNOSTIC PROBE — remove before any real release.
// Pure logging, no game state is modified. Every line is prefixed "UADMC_PROBE"
// so it can be filtered out of Latest.log. It answers the open runtime questions
// for the three planned features:
//   (Phase 3 capacity) per-major shipyard + home/overseas port-capacity magnitudes
//     each turn (calibrates BaseRate / weighting), capture events with the captured
//     province's Development + port capacity, and whether vanilla mutates
//     Player.shipyard inside AdvanceShips (write-timing).
//   (Phase 1 vanquished) what CampaignController.DisablePlayer changes (cash / shipyard
//     / ships before vs after) and whether it fires at all.
//   (Phase 2 naming) whether Ship.GenerateRandomName is the single naming chokepoint
//     (fires for AI + human builds) and what className/result it produces.
[HarmonyPatch]
internal static class UADMC_DiagnosticProbePatch
{
    private static int _turn;
    private static bool _dumpedProvinces;
    private static int _nameLogs;
    private const int MaxNameLogs = 40;
    private static readonly Dictionary<string, string> _lastController = new(StringComparer.Ordinal);

    private static void Log(string msg) => Melon<MintChipPlusMod>.Logger.Msg("UADMC_PROBE " + msg);

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    private static string PName(Player p) => Safe(() => (p?.data?.name) ?? "neutral", "neutral");
    private static string PDName(PlayerData pd) => Safe(() => (pd?.name) ?? "none", "none");

    private static int ProvincePortCap(Province pr)
        => Safe(() =>
        {
            int sum = 0;
            var ports = pr.Ports;
            if (ports != null)
                foreach (PortElement pe in ports)
                    if (pe != null)
                        sum += pe.GetPortCapacityWithoutDamage();
            return sum;
        }, 0);

    // ---------- per-turn snapshot: capacity magnitudes + captures ----------
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CampaignController), "OnNewTurn")]
    private static void OnNewTurnPostfix(CampaignController __instance)
    {
        try
        {
            _turn++;
            string year = Safe(() => __instance.CurrentDate.ToString(true), "?");
            Log($"=== turn#{_turn} year={year} ===");

            var homeCap = new Dictionary<string, double>(StringComparer.Ordinal);
            var overseasCap = new Dictionary<string, double>(StringComparer.Ordinal);
            var provCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            var provById = new Dictionary<string, Province>(StringComparer.Ordinal);

            var data = __instance.CampaignData;
            var byPlayer = data?.ProvincesByPlayer;
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

                        string id = Safe(() => pr.Id, "?");
                        string controller = Safe(() => (pr.ControllerPlayer?.data?.name) ?? "neutral", "neutral");
                        string claim = Safe(() => (pr.ClaimPlayer?.name) ?? "none", "none");
                        bool isHome = Safe(() => pr.IsHome, false);
                        int cap = ProvincePortCap(pr);

                        current[id] = controller;
                        provById[id] = pr;
                        provCount.TryGetValue(controller, out int pcPrev);
                        provCount[controller] = pcPrev + 1;

                        if (cap > 0)
                        {
                            bool homeForController = isHome && string.Equals(claim, controller, StringComparison.Ordinal);
                            var bucket = homeForController ? homeCap : overseasCap;
                            bucket.TryGetValue(controller, out double prev);
                            bucket[controller] = prev + cap;
                        }

                        if (!_dumpedProvinces && cap > 0)
                            Log($"prov id={id} dev={Safe(() => pr.Development, -1f):0.0} home={isHome} claim={claim} ctrl={controller} portCap={cap}");
                    }
                }
                _dumpedProvinces = true;
            }

            // Per-major summary: shipyard vs weighted port capacity (calibration gold).
            var majors = data?.PlayersMajor;
            if (majors != null)
            {
                foreach (Player p in majors)
                {
                    if (p == null)
                        continue;

                    string name = PName(p);
                    float yard = Safe(() => p.shipyard, -1f);
                    float cash = Safe(() => p.cash, -1f);
                    float totalPortCap = Safe(() => p.GetTotalPortCapacity(), -1f);
                    homeCap.TryGetValue(name, out double hc);
                    overseasCap.TryGetValue(name, out double oc);
                    provCount.TryGetValue(name, out int pc);
                    Log($"major name={name} shipyard={yard:0} cash={cash:0} totalPortCap={totalPortCap:0} homePortCap={hc:0} overseasPortCap={oc:0} provinces={pc}");
                }
            }

            // Capture detection vs last turn.
            foreach (var kv in current)
            {
                if (_lastController.TryGetValue(kv.Key, out string? prev) && !string.Equals(prev, kv.Value, StringComparison.Ordinal))
                {
                    provById.TryGetValue(kv.Key, out Province? pr);
                    int cap = pr != null ? ProvincePortCap(pr) : -1;
                    float dev = pr != null ? Safe(() => pr.Development, -1f) : -1f;
                    bool home = pr != null && Safe(() => pr.IsHome, false);
                    Log($"CAPTURE prov={kv.Key} {prev}->{kv.Value} dev={dev:0.0} home={home} portCap={cap}");
                }
            }

            _lastController.Clear();
            foreach (var kv in current)
                _lastController[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Log("OnNewTurn error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------- shipyard write-timing: does vanilla change shipyard inside AdvanceShips? ----------
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.AdvanceShips))]
    private static void AdvanceShipsPrefix(Player player, bool prewarm, out float __state)
        => __state = Safe(() => player != null ? player.shipyard : -1f, -1f);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.AdvanceShips))]
    private static void AdvanceShipsPostfix(Player player, bool prewarm, float __state)
    {
        try
        {
            if (prewarm)
                return;

            float after = Safe(() => player != null ? player.shipyard : -1f, -1f);
            if (Math.Abs(after - __state) > 0.001f)
                Log($"AdvanceShips shipyard CHANGED name={PName(player)} {__state:0.0}->{after:0.0}");
        }
        catch (Exception ex)
        {
            Log("AdvanceShips error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------- elimination: what does DisablePlayer change? ----------
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.DisablePlayer))]
    private static void DisablePlayerPrefix(Player player, out string __state)
        => __state = Snapshot(player, "before");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CampaignController), nameof(CampaignController.DisablePlayer))]
    private static void DisablePlayerPostfix(Player player, string __state)
    {
        Log("DISABLE " + __state);
        Log("DISABLE " + Snapshot(player, "after_"));
    }

    private static string Snapshot(Player p, string when)
        => Safe(() => $"{when} name={PName(p)} major={p.isMajor} shipyard={p.shipyard:0} cash={p.cash:0} ships={CountFleet(p)}", when + " <err>");

    private static int CountFleet(Player p)
        => Safe(() =>
        {
            int n = 0;
            foreach (Ship s in p.GetFleetAll())
            {
                n++;
                if (n > 100000)
                    break;
            }
            return n;
        }, -1);

    // ---------- naming chokepoint: does GenerateRandomName fire for AI + human builds? ----------
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Ship), nameof(Ship.GenerateRandomName))]
    private static void GenerateRandomNamePostfix(bool isDesign, string className, PlayerData playerData, string __result)
    {
        try
        {
            if (_nameLogs >= MaxNameLogs)
                return;

            _nameLogs++;
            Log($"GenerateRandomName isDesign={isDesign} class=\"{className}\" player={PDName(playerData)} -> \"{__result}\"" + (_nameLogs == MaxNameLogs ? " [further name logs suppressed]" : ""));
        }
        catch (Exception ex)
        {
            Log("GenerateRandomName error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
