using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Phase 1: vanquished-nation spoils.
//
// Vanilla, on full conquest, strands the dying nation's cash + shipyard and DESTROYS
// its remaining fleet (confirmed via the 0.5.106 diagnostic: ships 12 -> 0 across
// DisablePlayer). So wiping a nation out yields nothing, while leaving it a one-province
// rump lets you annex its assets — a perverse incentive. This fixes it: in a
// CampaignController.DisablePlayer PREFIX (while the fleet still exists), distribute the
// dying major's fleet and a cash indemnity to the victors (majors now holding its
// CLAIMED territory, weighted by port capacity), scuttling a fraction of the fleet.
//
// Territory -> shipyard capacity is already handled per-capture by PortCapacityRebuild
// as the nation lost provinces, so this deliberately does NOT touch shipyard (no
// double-count). Runs only for eliminated AI majors (never the human; that's game over).
internal static class VanquishedTransfer
{
    // Scuttle/seize fractions come from ModSettings (Vanquished Spoils Share level).

    // Bonus weight (as a fraction of the territorial pot) granted to the FINISHER — the major holding
    // the most of the dying nation's last-held territory (i.e. who took its final provinces). Rewards
    // delivering the kill / conquering its colonies, not just sitting on its homeland.
    private const double FinisherBonusFraction = 0.5;

    // Per-turn snapshot of each major's controlled provinces (by player name). Captured each resolved
    // turn (and on load) so that at DisablePlayer — when the dying nation already controls nothing — we
    // still know its last-held empire (homeland AND colonies) and can credit whoever now holds it.
    // Only overwritten with a NON-empty list, so a nation's final holdings survive into the death turn.
    internal static readonly Dictionary<string, List<Province>> LastEmpire = new(StringComparer.Ordinal);
    private static string lastFinisher = string.Empty;

    internal static void SnapshotEmpires(CampaignController.Data? data)
    {
        try
        {
            var byPlayer = data?.ProvincesByPlayer;
            if (byPlayer == null)
                return;
            foreach (var kvp in byPlayer)
            {
                string name = SafeStr(() => kvp.Key?.name);
                if (string.IsNullOrEmpty(name))
                    continue;
                var provs = kvp.Value;
                if (provs == null || provs.Count == 0)
                    continue; // keep the prior (non-empty) snapshot through the death turn
                var list = new List<Province>();
                foreach (Province pr in provs)
                    if (pr != null)
                        list.Add(pr);
                if (list.Count > 0)
                    LastEmpire[name] = list;
            }
        }
        catch { }
    }

    internal static void OnDisablePlayer(CampaignController cc, Player dying)
    {
        if (!ModSettings.VanquishedSpoilsEnabled)
            return;

        try
        {
            if (cc == null || dying == null)
                return;
            if (!SafeBool(() => dying.isMajor) || SafeBool(() => dying.isMain))
                return; // majors only; never the human player

            var data = cc.CampaignData;
            if (data == null)
                return;

            string dyingName = SafeStr(() => dying.data?.name);
            if (string.IsNullOrEmpty(dyingName))
                return;

            var players = BuildPlayers(data);
            var weight = new Dictionary<string, double>(StringComparer.Ordinal);
            CollectVictors(data, dyingName, players, weight);

            double totalW = 0;
            foreach (double w in weight.Values)
                totalW += w;
            bool haveVictors = weight.Count > 0 && totalW > 0;

            // Materialize the dying fleet before mutating it.
            var ships = new List<Ship>();
            foreach (Ship s in dying.GetFleetAll())
                if (s != null)
                    ships.Add(s);
            int n = ships.Count;

            // Cash indemnity to victors by weight.
            double cashMoved = 0;
            if (haveVictors)
            {
                double pot = Math.Max(0, SafeFloat(() => dying.cash)) * ModSettings.VanquishedCashSeizeFraction;
                if (pot > 0)
                {
                    foreach (var kv in weight)
                    {
                        Player? v = Get(players, kv.Key);
                        if (v == null)
                            continue;
                        double share = pot * (kv.Value / totalW);
                        AddCash(v, share);
                        cashMoved += share;
                    }
                }
            }

            // Fleet: scuttle a fraction, distribute the rest by victor weight (no victors => scuttle all).
            int scuttle = haveVictors ? (int)Math.Round(n * ModSettings.VanquishedScuttleFraction) : n;
            if (scuttle > n) scuttle = n;
            int seize = n - scuttle;

            Dictionary<string, int> alloc = AllocateShips(weight, totalW, seize);
            var taken = new Il2CppSystem.Collections.Generic.List<Ship>();

            int scuttled = 0, transferred = 0, idx = 0;
            for (; idx < scuttle && idx < n; idx++)
                if (TryScuttle(cc, ships[idx]))
                    scuttled++;

            foreach (var kv in alloc)
            {
                Player? v = Get(players, kv.Key);
                if (v == null)
                    continue;
                for (int c = 0; c < kv.Value && idx < n; c++, idx++)
                {
                    if (TryTransfer(cc, v, ships[idx], ref taken))
                        transferred++;
                    else if (TryScuttle(cc, ships[idx]))
                        scuttled++;
                }
            }

            // Any leftover from rounding -> scuttle so nothing is left for vanilla to destroy uncredited.
            for (; idx < n; idx++)
                if (TryScuttle(cc, ships[idx]))
                    scuttled++;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP vanquished spoils: {dyingName} eliminated — victors={FormatVictors(weight)} finisher={(string.IsNullOrEmpty(lastFinisher) ? "none" : lastFinisher)} ships={n} (transferred={transferred} scuttled={scuttled}) cashSeized={cashMoved:0}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP vanquished spoils failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CollectVictors(
        CampaignController.Data data, string dyingName,
        Dictionary<string, Player> players, Dictionary<string, double> weight)
    {
        lastFinisher = string.Empty;
        try
        {
            // The dying nation's FORMER territory = its claimed homeland (claim==dying, even if lost
            // long ago) UNION its last-held provinces from the per-turn snapshot (captures colonies,
            // whose claim is their own region, not the dying nation). Dedup by province pointer.
            var former = new Dictionary<IntPtr, Province>();

            var byPlayer = data.ProvincesByPlayer;
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
                        string claim = SafeStr(() => pr.ClaimPlayer?.name);
                        if (string.Equals(claim, dyingName, StringComparison.Ordinal))
                            TryAddProvince(former, pr);
                    }
                }
            }

            List<Province>? snapshot = LastEmpire.TryGetValue(dyingName, out var snap) ? snap : null;
            if (snapshot != null)
                foreach (Province pr in snapshot)
                    if (pr != null)
                        TryAddProvince(former, pr);

            // Base territorial weight: port cap of each former province by its CURRENT (major) holder.
            foreach (Province pr in former.Values)
            {
                string controller = SafeStr(() => pr.ControllerPlayer?.data?.name);
                if (string.IsNullOrEmpty(controller) || string.Equals(controller, dyingName, StringComparison.Ordinal))
                    continue;
                if (!IsMajor(players, controller))
                    continue;
                double cap = Math.Max(1, PortCap(pr));
                weight.TryGetValue(controller, out double prev);
                weight[controller] = prev + cap;
            }

            // Finisher = the major holding the most of the dying nation's LAST-HELD provinces (its
            // final territory, incl. colonies). They get a bonus for delivering the kill.
            var recent = new Dictionary<string, double>(StringComparer.Ordinal);
            if (snapshot != null)
                foreach (Province pr in snapshot)
                {
                    if (pr == null)
                        continue;
                    string controller = SafeStr(() => pr.ControllerPlayer?.data?.name);
                    if (string.IsNullOrEmpty(controller) || string.Equals(controller, dyingName, StringComparison.Ordinal))
                        continue;
                    if (!IsMajor(players, controller))
                        continue;
                    recent.TryGetValue(controller, out double prev);
                    recent[controller] = prev + Math.Max(1, PortCap(pr));
                }

            string finisher = string.Empty;
            double best = 0;
            foreach (var kv in recent)
                if (kv.Value > best) { best = kv.Value; finisher = kv.Key; }

            if (!string.IsNullOrEmpty(finisher))
            {
                double totalBase = 0;
                foreach (double w in weight.Values)
                    totalBase += w;
                double bonus = totalBase * FinisherBonusFraction;
                if (bonus > 0)
                {
                    weight.TryGetValue(finisher, out double prev);
                    weight[finisher] = prev + bonus;
                    lastFinisher = finisher;
                }
            }
        }
        catch
        {
        }
    }

    private static void TryAddProvince(Dictionary<IntPtr, Province> set, Province pr)
    {
        try { IntPtr p = pr.Pointer; if (p != IntPtr.Zero) set[p] = pr; } catch { }
    }

    private static Dictionary<string, int> AllocateShips(Dictionary<string, double> weight, double totalW, int seize)
    {
        var alloc = new Dictionary<string, int>(StringComparer.Ordinal);
        if (seize <= 0 || totalW <= 0 || weight.Count == 0)
            return alloc;

        var sorted = new List<string>(weight.Keys);
        sorted.Sort((a, b) => weight[b].CompareTo(weight[a]));

        int assigned = 0;
        foreach (string name in sorted)
        {
            int c = (int)Math.Floor(weight[name] / totalW * seize);
            alloc[name] = c;
            assigned += c;
        }
        int rem = seize - assigned;
        foreach (string name in sorted)
        {
            if (rem <= 0)
                break;
            alloc[name] += 1;
            rem--;
        }
        return alloc;
    }

    private static bool TryScuttle(CampaignController cc, Ship ship)
    {
        if (ship == null)
            return false;
        try { cc.ScrapShip(ship, false); return true; }
        catch
        {
            try { ship.Sink("UADVP vanquished"); return true; }
            catch { return false; }
        }
    }

    private static bool TryTransfer(CampaignController cc, Player victor, Ship ship, ref Il2CppSystem.Collections.Generic.List<Ship> taken)
    {
        if (ship == null || victor == null)
            return false;
        try
        {
            Il2CppSystem.Guid id = ship.id;
            cc.TransferShipToNewOwner(victor, id, ref taken, null, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddCash(Player p, double amount)
    {
        try { p.cash = (float)(p.cash + amount); } catch { }
    }

    private static int PortCap(Province pr)
        => SafeInt(() =>
        {
            int sum = 0;
            var ports = pr.Ports;
            if (ports != null)
                foreach (PortElement pe in ports)
                    if (pe != null)
                        sum += pe.GetPortCapacityWithoutDamage();
            return sum;
        });

    private static Dictionary<string, Player> BuildPlayers(CampaignController.Data data)
    {
        var map = new Dictionary<string, Player>(StringComparer.Ordinal);
        try
        {
            var players = data.Players;
            if (players != null)
                foreach (Player p in players)
                {
                    if (p == null) continue;
                    string name = SafeStr(() => p.data?.name);
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                        map[name] = p;
                }
        }
        catch { }
        return map;
    }

    private static Player? Get(Dictionary<string, Player> players, string name)
        => (!string.IsNullOrEmpty(name) && players.TryGetValue(name, out Player? p)) ? p : null;

    private static bool IsMajor(Dictionary<string, Player> players, string name)
    {
        Player? p = Get(players, name);
        return p != null && SafeBool(() => p.isMajor);
    }

    private static string FormatVictors(Dictionary<string, double> weight)
    {
        if (weight.Count == 0)
            return "none";
        var parts = new List<string>();
        foreach (var kv in weight)
            parts.Add($"{kv.Key}:{kv.Value:0}");
        return string.Join(",", parts);
    }

    private static T SafeT<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
    private static string SafeStr(Func<string?> f) { try { return f() ?? string.Empty; } catch { return string.Empty; } }
    private static float SafeFloat(Func<float> f) => SafeT(f, 0f);
    private static int SafeInt(Func<int> f) => SafeT(f, 0);
    private static bool SafeBool(Func<bool> f) => SafeT(f, false);
}
