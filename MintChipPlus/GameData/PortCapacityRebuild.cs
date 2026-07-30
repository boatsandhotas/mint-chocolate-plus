using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// Phase 3: multi-year shipyard-capacity rebuild on conquest.
//
// Vanilla never ties Player.shipyard (national build capacity) to territory: a
// nation keeps its full shipyard even after losing every province. This models
// the missing link — when a major captures a province from another major, the
// loser instantly loses that province's proportional share of its shipyard, and
// the captor gains it gradually ("rebuild") over a development-scaled number of
// years.
//
//   share(p)  = portCap(p) * w / SUM(portCap * w over loser's provinces)
//               w = 1.0 home (IsHome && claim==loser) / OverseasWeight colony
//   amount    = loserShipyardBefore * share(p)         (instant deduction)
//   duration  = MIN + (MAX-MIN) * (sizeFactor + remoteFactor)/2   (additive 2x2)
//               sizeFactor   = clamp(amount / (loserShipyard*0.5), 0, 1)
//               remoteFactor = clamp(1 - Development/DevHi, 0, 1)
//   then amount ramps linearly into captor.shipyard over `duration`.
//
// Re-capture mid-ramp transfers only the developed-so-far (undeveloped remainder
// is lost). State (active/completed ramps) persists per-campaign via ModCampaignState.
// Runs in the OnNewTurn postfix (after the turn resolves; Player.shipyard writes
// there persist — confirmed via the 0.5.106 diagnostic run).
internal static class PortCapacityRebuild
{
    private const string Feature = "portcap";

    // Balance tuning (on/off is the in-game/ModSettings toggle).
    private const float MinYears = 0.5f;
    private const float MaxYears = 6f;
    private const float DevHi = 25f;            // Development at/above which rebuild runs at full speed
    private const double SizeRefFraction = 0.5; // amount == 50% of loser shipyard => "large"
    private const int TurnsPerYear = 12;        // GameDate.turn is a month index

    private sealed class Schedule
    {
        internal string ToPlayer = string.Empty;
        internal double Total;
        internal double Developed;
        internal int StartTurn;
        internal int EndTurn;
    }

    private struct ProvInfo
    {
        internal int PortCap;
        internal float Dev;
        internal bool IsHome;
        internal string Claim;
    }

    private static readonly Dictionary<string, Schedule> _schedules = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _lastController = new(StringComparer.Ordinal);
    private static string _loadedKey = string.Empty;
    private static bool _seeded;

    // ---- entry points (called from the per-turn dispatcher) ----

    internal static void Reconcile(CampaignController cc)
    {
        try
        {
            string key = ModCampaignState.CampaignKey();
            LoadFor(key);
            _lastController.Clear();
            _seeded = false; // re-seed snapshot on next ProcessTurn so a load isn't read as captures
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC shipyard-rebuild: reconciled {ModCampaignState.DebugIdentity()} schedules={_schedules.Count} enabled={ModSettings.MultiYearShipyardRebuildEnabled}.");
        }
        catch (Exception ex) { Warn("reconcile", ex); }
    }

    internal static void ProcessTurn(CampaignController cc)
    {
        if (!ModSettings.MultiYearShipyardRebuildEnabled)
            return;

        try
        {
            string key = ModCampaignState.CampaignKey();
            if (string.IsNullOrEmpty(key))
                return;
            if (!string.Equals(key, _loadedKey, StringComparison.Ordinal))
            {
                LoadFor(key);
                _lastController.Clear();
                _seeded = false;
            }

            var data = cc.CampaignData;
            if (data == null)
                return;

            int turn = SafeTurn(cc);
            Dictionary<string, Player> players = BuildPlayers(data);

            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            var info = new Dictionary<string, ProvInfo>(StringComparer.Ordinal);
            var afterWeighted = new Dictionary<string, double>(StringComparer.Ordinal);
            ScanProvinces(data, current, info, afterWeighted);

            // First pass after a load: just seed the snapshot, don't treat existing
            // ownership as captures.
            if (!_seeded)
            {
                CopyInto(current, _lastController);
                _seeded = true;
                AdvanceSchedules(turn, players);
                Persist();
                return;
            }

            // Collect captures this turn.
            var captures = new List<(string prov, string loser, string captor)>();
            foreach (var kv in current)
            {
                if (_lastController.TryGetValue(kv.Key, out string? prev) &&
                    !string.Equals(prev, kv.Value, StringComparison.Ordinal))
                {
                    captures.Add((kv.Key, prev!, kv.Value));
                }
            }

            if (captures.Count > 0)
            {
                // Pre-turn weighted-port totals = current (after) totals + everything
                // captured from each loser this turn (which already moved to captors).
                var beforeWeighted = new Dictionary<string, double>(afterWeighted, StringComparer.Ordinal);
                foreach (var c in captures)
                {
                    if (info.TryGetValue(c.prov, out ProvInfo pi))
                    {
                        double w = WeightFor(pi, c.loser);
                        beforeWeighted.TryGetValue(c.loser, out double prev);
                        beforeWeighted[c.loser] = prev + pi.PortCap * w;
                    }
                }

                // Snapshot loser shipyards before any deduction this turn.
                var beforeShipyard = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var c in captures)
                {
                    if (!beforeShipyard.ContainsKey(c.loser))
                    {
                        Player? lp = Get(players, c.loser);
                        beforeShipyard[c.loser] = lp != null ? SafeFloat(() => lp.shipyard) : 0;
                    }
                }

                foreach (var c in captures)
                    HandleCapture(c.prov, c.loser, c.captor, turn, players, info, beforeWeighted, beforeShipyard);
            }

            AdvanceSchedules(turn, players);
            CopyInto(current, _lastController);
            Persist();
        }
        catch (Exception ex) { Warn("process", ex); }
    }

    // ---- capture handling ----

    private static void HandleCapture(
        string prov, string loser, string captor, int turn,
        Dictionary<string, Player> players, Dictionary<string, ProvInfo> info,
        Dictionary<string, double> beforeWeighted, Dictionary<string, double> beforeShipyard)
    {
        // Majors only (minors have a shipyard field but it is not meaningful build
        // capacity). If a tracked province falls out of major-vs-major hands, drop
        // its schedule so we stop crediting a non-builder.
        bool loserMajor = IsMajor(players, loser);
        bool captorMajor = IsMajor(players, captor);
        if (!loserMajor || !captorMajor)
        {
            _schedules.Remove(prov);
            return;
        }

        Player? loserP = Get(players, loser);
        Player? captorP = Get(players, captor);
        if (loserP == null || captorP == null || !info.TryGetValue(prov, out ProvInfo pi))
            return;

        double amount;
        if (_schedules.TryGetValue(prov, out Schedule? existing))
        {
            // Re-capture mid/after ramp: only the developed-so-far moves on; the
            // undeveloped remainder is lost.
            amount = existing.Developed;
            _schedules.Remove(prov);
            DeductShipyard(loserP, amount);
        }
        else
        {
            double w = WeightFor(pi, loser);
            double denom = beforeWeighted.TryGetValue(loser, out double bw) ? bw : 0;
            double before = beforeShipyard.TryGetValue(loser, out double bs) ? bs : SafeFloat(() => loserP.shipyard);
            if (denom <= 0 || before <= 0 || pi.PortCap <= 0)
                return; // nothing to attribute (e.g. already-defeated nation with no ports)

            amount = before * (pi.PortCap * w) / denom;
            double live = SafeFloat(() => loserP.shipyard);
            if (amount > live) amount = live; // can't transfer more than they currently hold
            if (amount <= 0) return;
            DeductShipyard(loserP, amount);
        }

        // Development-scaled rebuild duration (additive 2x2).
        double beforeYard = beforeShipyard.TryGetValue(loser, out double byd) ? byd : amount;
        double sizeFactor = Clamp01(amount / Math.Max(1.0, beforeYard * SizeRefFraction));
        double remoteFactor = Clamp01(1.0 - pi.Dev / DevHi);
        double durationYears = MinYears + (MaxYears - MinYears) * (sizeFactor + remoteFactor) / 2.0;
        int durationTurns = Math.Max(1, (int)Math.Round(durationYears * TurnsPerYear));

        _schedules[prov] = new Schedule
        {
            ToPlayer = Sanitize(captor),
            Total = amount,
            Developed = 0,
            StartTurn = turn,
            EndTurn = turn + durationTurns,
        };

        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC shipyard-rebuild capture prov={prov} {loser}->{captor} amount={amount:0} dev={pi.Dev:0.0} home={pi.IsHome && string.Equals(pi.Claim, loser, StringComparison.Ordinal)} durYears={durationYears:0.0} loserYard->{SafeFloat(() => loserP.shipyard):0}.");
    }

    private static void AdvanceSchedules(int turn, Dictionary<string, Player> players)
    {
        if (_schedules.Count == 0)
            return;

        foreach (var kv in _schedules)
        {
            Schedule s = kv.Value;
            double target = (turn >= s.EndTurn || s.EndTurn <= s.StartTurn)
                ? s.Total
                : s.Total * (double)(turn - s.StartTurn) / (s.EndTurn - s.StartTurn);
            if (target < 0) target = 0;
            if (target > s.Total) target = s.Total;

            double delta = target - s.Developed;
            if (Math.Abs(delta) <= 0.01)
                continue;

            Player? owner = Get(players, s.ToPlayer);
            if (owner == null)
                continue; // captor temporarily absent (e.g. eliminated); resume if it returns

            AddShipyard(owner, delta);
            s.Developed = target;
            if (target >= s.Total)
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC shipyard-rebuild complete prov={kv.Key} player={s.ToPlayer} total={s.Total:0}.");
        }
    }

    // ---- helpers ----

    private static double WeightFor(ProvInfo pi, string owner)
        => (pi.IsHome && string.Equals(pi.Claim, owner, StringComparison.Ordinal)) ? 1.0 : ModSettings.RebuildOverseasWeight;

    private static void ScanProvinces(
        CampaignController.Data data,
        Dictionary<string, string> current,
        Dictionary<string, ProvInfo> info,
        Dictionary<string, double> afterWeighted)
    {
        var byPlayer = data.ProvincesByPlayer;
        if (byPlayer == null)
            return;

        foreach (var kvp in byPlayer)
        {
            var provs = kvp.Value;
            if (provs == null)
                continue;

            foreach (Province pr in provs)
            {
                if (pr == null)
                    continue;

                string id = SafeStr(() => pr.Id);
                if (string.IsNullOrEmpty(id))
                    continue;

                string controller = SafeStr(() => pr.ControllerPlayer?.data?.name);
                if (string.IsNullOrEmpty(controller))
                    continue;

                var pi = new ProvInfo
                {
                    PortCap = PortCap(pr),
                    Dev = SafeFloat(() => pr.Development),
                    IsHome = SafeBool(() => pr.IsHome),
                    Claim = SafeStr(() => pr.ClaimPlayer?.name),
                };

                current[id] = controller;
                info[id] = pi;
                if (pi.PortCap > 0)
                {
                    double w = WeightFor(pi, controller);
                    afterWeighted.TryGetValue(controller, out double prev);
                    afterWeighted[controller] = prev + pi.PortCap * w;
                }
            }
        }
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

    private static void DeductShipyard(Player p, double amount)
    {
        try { p.shipyard = (float)Math.Max(0.0, p.shipyard - amount); } catch { }
    }

    private static void AddShipyard(Player p, double amount)
    {
        try { p.shipyard = (float)Math.Max(0.0, p.shipyard + amount); } catch { }
    }

    private static void CopyInto(Dictionary<string, string> src, Dictionary<string, string> dst)
    {
        dst.Clear();
        foreach (var kv in src)
            dst[kv.Key] = kv.Value;
    }

    private static int SafeTurn(CampaignController cc)
        => SafeInt(() => cc.CurrentDate.turn);

    // ---- persistence ----

    private static void LoadFor(string key)
    {
        _schedules.Clear();
        _loadedKey = key ?? string.Empty;
        if (string.IsNullOrEmpty(_loadedKey))
            return;

        string raw = ModCampaignState.Load(Feature);
        if (string.IsNullOrEmpty(raw))
            return;

        foreach (string entry in raw.Split(';'))
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            string[] f = entry.Split('|');
            if (f.Length != 6)
                continue;

            string prov = f[0];
            if (string.IsNullOrEmpty(prov))
                continue;

            _schedules[prov] = new Schedule
            {
                ToPlayer = f[1],
                Total = ParseD(f[2]),
                Developed = ParseD(f[3]),
                StartTurn = ParseI(f[4]),
                EndTurn = ParseI(f[5]),
            };
        }
    }

    private static void Persist()
    {
        if (string.IsNullOrEmpty(_loadedKey))
            return;

        var sb = new StringBuilder();
        foreach (var kv in _schedules)
        {
            Schedule s = kv.Value;
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(kv.Key).Append('|')
              .Append(s.ToPlayer).Append('|')
              .Append(s.Total.ToString("0.##", CultureInfo.InvariantCulture)).Append('|')
              .Append(s.Developed.ToString("0.##", CultureInfo.InvariantCulture)).Append('|')
              .Append(s.StartTurn.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(s.EndTurn.ToString(CultureInfo.InvariantCulture));
        }
        ModCampaignState.Save(Feature, sb.ToString());
    }

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Replace('|', '_').Replace(';', '_');

    private static double ParseD(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static int ParseI(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static void Warn(string where, Exception ex)
        => Melon<MintChipPlusMod>.Logger.Warning($"UADMC shipyard-rebuild {where} failed: {ex.GetType().Name}: {ex.Message}");

    private static T SafeT<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
    private static string SafeStr(Func<string?> f) { try { return f() ?? string.Empty; } catch { return string.Empty; } }
    private static float SafeFloat(Func<float> f) => SafeT(f, 0f);
    private static int SafeInt(Func<int> f) => SafeT(f, 0);
    private static bool SafeBool(Func<bool> f) => SafeT(f, false);
}
