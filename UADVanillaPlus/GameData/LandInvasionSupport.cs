using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace UADVanillaPlus.GameData;

// "Reinforce with Navy": naval tonnage the player parks at a land battle's REACHABLE COAST adds army force
// to that battle (attacking or defending). If the target province is coastal the circle sits on it; if it's
// landlocked the circle sits on the nearest coast and the effect is DIMINISHED the further inland it is.
//
// The force lever is PROVEN (spike 0.5.276): writing battle.PlayerArmyForce[side] in a
// ProvinceBattleManager.CalculateLosses prefix changes the battle's advance/losses. Native re-fills that
// stored value each turn, so we RE-APPLY the bonus every resolve from a persisted commitment.
//
// Persisted per-campaign via ModCampaignState (PlayerPrefs side-car; never touches the vanilla save).
// All logging under "UADVP_REINFORCE".
internal static class LandInvasionSupport
{
    private const string Feature = "land_reinforce";
    private const char RS = '', US = '';

    internal static void Log(string m) => Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_REINFORCE " + m);

    // One player commitment to reinforce a specific land battle (identified by its two provinces, which are
    // stable across the battle's life and survive save/load). Circle center + inland hops are resolved once
    // at commit time (nearest reachable coast) and stored.
    internal sealed class Commitment
    {
        public string AttackerNation = "";
        public string DefProvinceId = "";
        public string AtkProvinceId = "";
        public int StartTurn;
        public float CenterX, CenterY, CenterZ;  // flat-world coord of the reinforcement circle (the coast)
        public string CoastProvId = "";          // province whose sea area we count tonnage in (== target if coastal)
        public int InlandHops;                    // 0 = target is coastal; N = target is N provinces inland
        public string PlayerProvId = "";          // the PLAYER'S OWN province in the battle (Attacker's if invading,
                                                   // Defender's if defending) — the province whose ArmyForceForProvince
                                                   // the battle reads for the player's side, so this is what we boost.

        public Vector3 Center => new(CenterX, CenterY, CenterZ);
    }

    private static readonly List<Commitment> commitments = new();
    private static string loadedKey = ""; // sentinel != any real campaign key

    internal static List<Commitment> AllCommitments() { EnsureLoaded(); return commitments; }

    // ---- persistence ------------------------------------------------------

    internal static void Reconcile()
    {
        loadedKey = "";
        EnsureLoaded();
        Log($"reconcile: commitments={commitments.Count} (campaign={ModCampaignState.DebugIdentity()})");
    }

    private static void EnsureLoaded()
    {
        string key = "";
        try { key = ModCampaignState.CampaignKey(); } catch { }
        if (key == loadedKey) return;
        LoadBlob();
        loadedKey = key;
    }

    private static void LoadBlob()
    {
        commitments.Clear();
        try
        {
            string blob = ModCampaignState.Load(Feature);
            if (string.IsNullOrEmpty(blob)) return;
            foreach (string rec in blob.Split(RS))
            {
                if (string.IsNullOrEmpty(rec)) continue;
                string[] f = rec.Split(US);
                if (f.Length >= 5 && f[0] == "C")
                {
                    var c = new Commitment { AttackerNation = f[1], DefProvinceId = f[2], AtkProvinceId = f[3], StartTurn = ParseI(f[4]) };
                    if (f.Length >= 10)
                    {
                        c.CenterX = ParseF(f[5]); c.CenterY = ParseF(f[6]); c.CenterZ = ParseF(f[7]);
                        c.CoastProvId = f[8]; c.InlandHops = ParseI(f[9]);
                    }
                    c.PlayerProvId = f.Length >= 11 ? f[10] : c.DefProvinceId; // back-compat: old saves default to target
                    commitments.Add(c);
                }
            }
        }
        catch (Exception ex) { Log("load failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void Persist()
    {
        try
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (Commitment c in commitments)
            {
                if (!first) sb.Append(RS); first = false;
                sb.Append('C').Append(US).Append(Clean(c.AttackerNation)).Append(US).Append(Clean(c.DefProvinceId))
                  .Append(US).Append(Clean(c.AtkProvinceId)).Append(US).Append(c.StartTurn.ToString(CultureInfo.InvariantCulture))
                  .Append(US).Append(F(c.CenterX)).Append(US).Append(F(c.CenterY)).Append(US).Append(F(c.CenterZ))
                  .Append(US).Append(Clean(c.CoastProvId)).Append(US).Append(c.InlandHops.ToString(CultureInfo.InvariantCulture))
                  .Append(US).Append(Clean(c.PlayerProvId));
            }
            ModCampaignState.Save(Feature, sb.ToString());
        }
        catch (Exception ex) { Log("save failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ---- commitments ------------------------------------------------------

    internal static Commitment? FindForBattle(ProvinceBattle pb)
    {
        EnsureLoaded();
        if (pb == null || commitments.Count == 0) return null;
        string defId = "", atkId = "";
        try { if (pb.DefenderProvince != null) defId = pb.DefenderProvince.Id; } catch { }
        try { if (pb.AttackerProvince != null) atkId = pb.AttackerProvince.Id; } catch { }
        if (defId.Length == 0 || atkId.Length == 0) return null;
        foreach (Commitment c in commitments)
            if (c.DefProvinceId == defId && c.AtkProvinceId == atkId) return c;
        return null;
    }

    internal static bool IsCommitted(ProvinceBattle pb) => FindForBattle(pb) != null;

    // Toggle: returns true if a commitment was ADDED, false if it was removed (or couldn't be added).
    internal static bool ToggleCommitment(ProvinceBattle pb)
    {
        EnsureLoaded();
        if (pb == null) return false;
        Commitment? existing = FindForBattle(pb);
        if (existing != null)
        {
            commitments.Remove(existing);
            Persist();
            Log($"removed commitment {existing.AtkProvinceId}->{existing.DefProvinceId}");
            return false;
        }
        string defId = "", atkId = "", nation = "";
        try { defId = pb.DefenderProvince.Id; } catch { }
        try { atkId = pb.AttackerProvince.Id; } catch { }
        try { nation = pb.Attacker.Name(false); } catch { }
        if (defId.Length == 0 || atkId.Length == 0) { Log("commit: battle has no province ids"); return false; }
        int turn = 0; try { turn = CampaignController.Instance.CurrentDate.turn; } catch { }

        // Resolve the coast from the PLAYER'S OWN province in this battle (attacker if invading, defender if
        // defending): the fleet stages from the player's own coast near the front, NOT the enemy's distant
        // coast (Georgia->Eastern Turkey must land on Georgia's Poti coast, not far-off Constantinople).
        // A land battle is ALWAYS your province vs the enemy's — identify YOUR province directly by control,
        // and buff that one (its ArmyForceForProvince is your combat strength in this fight). This is more
        // reliable than inferring your side from pb.Attacker/pb.Defender.
        Player? main = PlayerController.Instance;
        Province? playerProv = null;
        try { if (pb.AttackerProvince != null && pb.AttackerProvince.ControllerPlayer == main) playerProv = pb.AttackerProvince; } catch { }
        if (playerProv == null) { try { if (pb.DefenderProvince != null && pb.DefenderProvince.ControllerPlayer == main) playerProv = pb.DefenderProvince; } catch { } }
        if (playerProv == null) // fallback: infer from side
        {
            Player? side = PlayerSideInBattle(pb); bool pa = false;
            try { pa = side != null && side == pb.Attacker; } catch { }
            try { playerProv = pa ? pb.AttackerProvince : pb.DefenderProvince; } catch { }
        }
        string playerProvId = ""; try { if (playerProv != null) playerProvId = playerProv.Id; } catch { }

        var c = new Commitment { AttackerNation = nation, DefProvinceId = defId, AtkProvinceId = atkId, StartTurn = turn, CoastProvId = defId, PlayerProvId = playerProvId };
        if (ResolveCoast(playerProv, out Vector3 center, out string coastId, out int hops))
        {
            c.CenterX = center.x; c.CenterY = center.y; c.CenterZ = center.z;
            c.CoastProvId = coastId; c.InlandHops = hops;
            Log($"added commitment {nation} {atkId}->{defId}; coast={coastId} inlandHops={hops} center=({center.x:0},{center.z:0})");
        }
        else
        {
            Log($"added commitment {nation} {atkId}->{defId}; NO reachable coast found (fleet can't reinforce)");
        }
        commitments.Add(c);
        Persist();
        return true;
    }

    internal static void ClearAll() { EnsureLoaded(); commitments.Clear(); Persist(); Log("cleared all commitments"); }

    internal static void PurgeStale()
    {
        EnsureLoaded();
        if (commitments.Count == 0) return;
        try
        {
            var battles = ProvinceBattleManager.Battles;
            var live = new HashSet<string>(StringComparer.Ordinal);
            if (battles != null)
                foreach (var kv in battles)
                {
                    ProvinceBattle pb = kv.Value; if (pb == null) continue;
                    string a = "", d = "";
                    try { a = pb.AttackerProvince != null ? pb.AttackerProvince.Id : ""; } catch { }
                    try { d = pb.DefenderProvince != null ? pb.DefenderProvince.Id : ""; } catch { }
                    if (a.Length > 0 && d.Length > 0) live.Add(a + "|" + d);
                }
            int before = commitments.Count;
            commitments.RemoveAll(c => !live.Contains(c.AtkProvinceId + "|" + c.DefProvinceId));
            if (commitments.Count != before) { Persist(); Log($"purged {before - commitments.Count} stale commitment(s)"); }
        }
        catch (Exception ex) { Log("purge failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ---- coast resolution -------------------------------------------------

    // Nearest reachable coast for a target province. If the target has a port it IS the coast (hops 0);
    // otherwise BFS its land neighbours until we hit a province with a port. center = that port's world coord.
    private static bool ResolveCoast(Province? target, out Vector3 center, out string coastProvId, out int hops)
    {
        center = default; coastProvId = ""; hops = 0;
        if (target == null) return false;
        try
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<Province> { target };
            int depth = 0;
            while (frontier.Count > 0 && depth < 8)
            {
                var next = new List<Province>();
                foreach (Province p in frontier)
                {
                    if (p == null) continue;
                    string pid = ""; try { pid = p.Id; } catch { }
                    if (pid.Length == 0 || !visited.Add(pid)) continue;

                    if (TryPortCoord(p, out Vector3 wc))
                    {
                        center = wc; coastProvId = pid; hops = depth; return true;
                    }
                    var nb = SafeNeighbours(p);
                    if (nb != null) foreach (Province n in nb) if (n != null) next.Add(n);
                }
                frontier = next;
                depth++;
            }
        }
        catch { }
        return false;
    }

    private static bool TryPortCoord(Province p, out Vector3 wc)
    {
        wc = default;
        try
        {
            bool hasPort = false; try { hasPort = p.HavePort; } catch { }
            var ports = p.Ports;
            if (ports == null || ports.Count == 0) return false;
            if (!hasPort && ports.Count == 0) return false;
            PortElement pe = ports[0];
            if (pe == null) return false;
            wc = pe.WorldCoord;
            return true;
        }
        catch { return false; }
    }

    private static Il2CppSystem.Collections.Generic.List<Province>? SafeNeighbours(Province p)
    {
        try { return p.NeighbourProvinces; } catch { return null; }
    }

    private static Province? FindProvinceById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var players = CampaignController.Instance?.CampaignData?.Players;
            if (players == null) return null;
            foreach (Player pl in players)
            {
                var provs = pl?.provinces;
                if (provs == null) continue;
                foreach (Province pr in provs)
                    if (pr != null && pr.Id == id) return pr;
            }
        }
        catch { }
        return null;
    }

    // ---- tonnage + injection ---------------------------------------------

    // Player naval tonnage in a coast province's sea area.
    private static float MeasureTonnageAt(string coastProvId)
    {
        try
        {
            CampaignController cc = CampaignController.Instance;
            Player? main = PlayerController.Instance;
            if (cc == null || main == null || string.IsNullOrEmpty(coastProvId)) return 0f;
            Province? prov = FindProvinceById(coastProvId);
            if (prov == null) return 0f;
            Area? area = prov.CurrentArea;
            if (area == null) return 0f;
            return cc.AreaCurrentTonnage(area, main);
        }
        catch { return 0f; }
    }

    internal static float MeasureTonnage(ProvinceBattle pb)
    {
        Commitment? c = FindForBattle(pb);
        return c != null ? MeasureTonnageAt(c.CoastProvId) : 0f;
    }

    // Effect diminishes with inland distance: hops 0 -> full, 1 -> 67%, 2 -> 50%, 3 -> 40% ...
    private static float Diminish(int hops) => 1f / (1f + 0.5f * Math.Max(0, hops));

    private static float BonusForCommitment(Commitment c)
    {
        float tonnage = MeasureTonnageAt(c.CoastProvId);
        if (tonnage <= 0f) return 0f;
        return tonnage * ModSettings.NavalReinforcementForcePerTon * Diminish(c.InlandHops); // ArmyForceForProvince (projected) units
    }

    // Tonnage-scaled reinforcement, diminished by inland distance. NO cap (a big fleet is a big impact). This
    // is in PROJECTED-force (ArmyForceForProvince) units — the value that actually drives the battle.
    internal static float BonusFor(ProvinceBattle pb)
    {
        Commitment? c = FindForBattle(pb);
        return c != null ? BonusForCommitment(c) : 0f;
    }

    // Projected-force ~= troops, so this is directly the added troop count.
    internal static float SoldiersFor(ProvinceBattle pb) => BonusFor(pb);

    // ---- per-turn budget cost -------------------------------------------
    // Keeping a reinforcement supplied costs treasury each turn, scaled by the tonnage committed (a bigger
    // fleet costs more to supply). This is what distinguishes a plain land invasion (free) from a reinforced
    // one. Charged in the per-turn hook; stops when you pull the fleet (tonnage 0) or cancel. TUNE the rate.
    internal const float CostPerTonPerTurn = 1000f;

    internal static float CostPerTurnFor(ProvinceBattle pb)
    {
        Commitment? c = FindForBattle(pb);
        if (c == null) return 0f;
        return MeasureTonnageAt(c.CoastProvId) * CostPerTonPerTurn;
    }

    internal static void ChargeTurnCosts()
    {
        try
        {
            if (!ModSettings.NavalReinforcementEnabled) return;
            EnsureLoaded();
            if (commitments.Count == 0) return;
            Player? main = PlayerController.Instance;
            if (main == null) return;
            float total = 0f;
            foreach (Commitment c in commitments)
            {
                float cost = MeasureTonnageAt(c.CoastProvId) * CostPerTonPerTurn;
                if (cost <= 0f) continue;
                try { main.cash -= cost; } catch { }
                total += cost;
            }
            if (total > 0f)
            {
                float cash = 0f; try { cash = main.cash; } catch { }
                Log($"turn cost: charged {total:N0} to supply {commitments.Count} reinforcement(s); cash now {cash:N0}");
            }
        }
        catch (Exception ex) { Log("cost charge err " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ---- the REAL lever: ArmyForceForProvince postfix -------------------
    // Called from a postfix on Player.ArmyForceForProvince for EVERY call, so it must be cheap: bail fast for
    // non-main-player / non-committed provinces; the per-commitment tonnage->bonus is cached ~1/20 frames.
    private static int projCacheNextFrame;
    private static bool inProjCompute;
    private static readonly Dictionary<string, float> projBonus = new(StringComparer.Ordinal); // DefProvinceId -> bonus

    internal static float ProjBonusFor(Player? player, Province? province)
    {
        try
        {
            if (inProjCompute || !ModSettings.NavalReinforcementEnabled || province == null) return 0f;
            Player? main = PlayerController.Instance;
            if (main == null || player != main) return 0f;
            string pid = ""; try { pid = province.Id; } catch { }
            if (pid.Length == 0) return 0f;
            if (Time.frameCount >= projCacheNextFrame)
            {
                projCacheNextFrame = Time.frameCount + 20;
                inProjCompute = true;
                try { RebuildProjCache(); } finally { inProjCompute = false; }
            }
            return projBonus.TryGetValue(pid, out float b) ? b : 0f;
        }
        catch { return 0f; }
    }

    private static void RebuildProjCache()
    {
        try
        {
            projBonus.Clear();
            EnsureLoaded();
            foreach (Commitment c in commitments)
            {
                float b = BonusForCommitment(c);
                if (b <= 0f) continue;
                string pp = string.IsNullOrEmpty(c.PlayerProvId) ? c.DefProvinceId : c.PlayerProvId;
                projBonus[pp] = b;                                          // battle strength = your OWN province
                if (c.DefProvinceId != pp) projBonus[c.DefProvinceId] = b;  // display = your ArmyForceForProvince at the target
            }
        }
        catch { }
    }

    internal static Player? PlayerSideInBattle(ProvinceBattle pb)
    {
        try
        {
            Player? main = PlayerController.Instance;
            if (main == null || pb == null) return null;
            Player? atk = null, def = null;
            try { atk = pb.Attacker; } catch { }
            try { def = pb.Defender; } catch { }
            if (atk == main) return atk;
            if (def == main) return def;
            return null;
        }
        catch { return null; }
    }

    // Popup label for a COMMITTED battle: the troop count the navy is contributing (or a "can't reach"
    // note when no coast / no ships). Kept short so it doesn't wrap onto the next popup row.
    internal static string ReinforcementLabel(ProvinceBattle pb)
    {
        try
        {
            if (!ModSettings.NavalReinforcementEnabled) return "";
            Commitment? c = FindForBattle(pb);
            if (c == null) return "";
            if (c.CenterX == 0f && c.CenterZ == 0f) return "  (navy: no coast in reach)"; // ResolveCoast failed
            float tonnage = MeasureTonnageAt(c.CoastProvId);
            if (tonnage <= 0f) return "  (navy: no ships at coast)";
            float soldiers = SoldiersFor(pb);
            string inland = c.InlandHops > 0 ? $", -{(int)((1f - Diminish(c.InlandHops)) * 100f)}% inland" : "";
            return soldiers > 0f ? $"  (+{soldiers:N0} troops{inland})" : "  (navy)";
        }
        catch { return ""; }
    }

    // Suffix for the popup attacker/force line: committed -> troop count; yours-but-uncommitted -> prompt.
    internal static string PopupSuffix(ProvinceBattle pb)
    {
        try
        {
            if (!ModSettings.NavalReinforcementEnabled) return "";
            if (PlayerSideInBattle(pb) == null) return "";
            if (FindForBattle(pb) != null) return ReinforcementLabel(pb);
            return "  [Ctrl+Shift+N: reinforce w/ navy]";
        }
        catch { return ""; }
    }


    // Investigation dump (Ctrl+Shift+I): for each of the player's committed battles, log EVERY plausible
    // advance-driver so we can see which one correlates with the advance (i.e. which is < the enemy's when the
    // player is losing). One-shot on demand.
    internal static void DumpBattleDrivers()
    {
        try
        {
            var battles = ProvinceBattleManager.Battles;
            if (battles == null) { Log("DRIVERS: no battles"); return; }
            Log("=== UADVP_REINFORCE BATTLE DRIVERS (your committed battles) ===");
            int nn = 0;
            foreach (var kv in battles)
            {
                ProvinceBattle pb = kv.Value;
                if (pb == null || FindForBattle(pb) == null) continue;
                if (nn++ >= 8) break;
                DumpOneBattle(kv.Key, pb);
            }
            if (nn == 0) Log("DRIVERS: no committed battles (commit one with Ctrl+Shift+N first)");
        }
        catch (Exception ex) { Log("DRIVERS err " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void DumpOneBattle(string key, ProvinceBattle pb)
    {
        Player? atk = Sf(() => pb.Attacker); Player? def = Sf(() => pb.Defender);
        Province? ap = Sf(() => pb.AttackerProvince); Province? dp = Sf(() => pb.DefenderProvince);
        Log($"DRIVER '{key}' atk={Nm(atk)} def={Nm(def)} | adv={Sn(() => pb.Advance):0.0}% atkLoss={Sn(() => (float)pb.AttackerLosses):0} defLoss={Sn(() => (float)pb.DefenderLosses):0} redAdvTurns={Sn(() => (float)pb.RedAdvanceTurns):0}");
        float da = 0f, dd = 0f; try { var m = pb.PlayerArmyForce; if (m != null) { if (atk != null) m.TryGetValue(atk, out da); if (def != null) m.TryGetValue(def, out dd); } } catch { }
        Log($"  dictForce atk={da:0} def={dd:0} | totalArmy atk={Sn(() => atk!.ArmyForce()):0} def={Sn(() => def!.ArmyForce()):0}");
        Log($"  PROJ@defProv atk={Sn(() => atk!.ArmyForceForProvince(dp)):0} def={Sn(() => def!.ArmyForceForProvince(dp)):0} | PROJ@atkProv atk={Sn(() => atk!.ArmyForceForProvince(ap)):0} def={Sn(() => def!.ArmyForceForProvince(ap)):0}");
        Log($"  help@defProv atk={Sn(() => atk!.ArmyForceForHelp(dp)):0} def={Sn(() => def!.ArmyForceForHelp(dp)):0} | allies@defProv atk={Sn(() => atk!.ArmyForceFromAllies(dp)):0} def={Sn(() => def!.ArmyForceFromAllies(dp)):0}");
        Log($"  defProv[{Nmp(dp)}] pop={Sn(() => dp!.Population):0} armyPct={Sn(() => dp!.ProvinceArmyPercentage):0.000} defBonus={Sn(() => dp!.ProvinceDefenderBonus):0.000} armyLoss={Sn(() => dp!.ArmyLosses):0}");
        Log($"  atkProv[{Nmp(ap)}] pop={Sn(() => ap!.Population):0} armyPct={Sn(() => ap!.ProvinceArmyPercentage):0.000} defBonus={Sn(() => ap!.ProvinceDefenderBonus):0.000} armyLoss={Sn(() => ap!.ArmyLosses):0}");
    }

    private static T? Sf<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    private static float Sn(Func<float> f) { try { return f(); } catch { return -1f; } }
    private static string Nm(Player? p) { try { return p != null ? p.Name(false) : "?"; } catch { return "?"; } }
    private static string Nmp(Province? p) { try { return p != null ? p.Id : "?"; } catch { return "?"; } }

    private static string Clean(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace(RS, ' ').Replace(US, ' ');
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static float ParseF(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    private static int ParseI(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
