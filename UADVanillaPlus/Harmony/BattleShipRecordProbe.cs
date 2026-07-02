using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// PHASE 2 capture for per-ship battle records (kill attribution + per-pair ABSOLUTE damage).
//
// Hook: Ui.RegisterTakenDamage(Ship victim, Part from, string hitType, float damage, ...) — the game's
// own per-hit damage registration (the source of the post-battle "dealt 20k" numbers). It is
// managed-reachable (no native bypass — VP already hooks it elsewhere) and carries the ABSOLUTE damage,
// the victim, and the firing part (from.ship = attacker). From this one hook we derive:
//   - total damage dealt / received per ship (absolute),
//   - "sank" (finisher: last attacker to damage a ship while it was still alive),
//   - "wrecked" (who dealt the most absolute damage to that sunk ship).
//
// CONFIRMED in-game (0.5.192): the prior TakeHitRaw+ratio approach already validated attribution and
// finisher/wrecker; this build swaps the damage metric to RegisterTakenDamage's absolute value.
// LOG-ONLY (no persistence / no viewer yet). Gated behind Battle Runtime Diagnostics.
internal static class BattleShipRecorder
{
    private static readonly Dictionary<IntPtr, Dictionary<IntPtr, float>> DamageToVictim = new(); // victim -> attacker -> dmg
    private static readonly Dictionary<IntPtr, IntPtr> Finisher = new();      // victim -> firer of the sinking blow
    private static readonly Dictionary<IntPtr, IntPtr> LastAttacker = new();  // victim -> last firer WHILE ALIVE
    private static readonly Dictionary<IntPtr, Ship?> Ships = new();          // ptr -> Ship (name resolution at end)
    private static readonly Dictionary<IntPtr, float> DealtTotal = new();     // attacker -> total dealt
    private static readonly Dictionary<IntPtr, float> ReceivedTotal = new();  // victim -> total received

    internal static bool Active { get; private set; }
    internal static int HitDiag; // per-battle counter limiting the per-hit diagnostic spam

    internal static void Begin()
    {
        Clear();
        // Active when the records feature OR the diagnostic is on; persistence vs verbose logging gated
        // separately in EndBattleLog.
        Active = ModSettings.ShipServiceRecordsEnabled || ModSettings.BattleRuntimeDiagnosticsEnabled;
        HitDiag = 0;
        if (Active && ModSettings.BattleRuntimeDiagnosticsEnabled)
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_RECPROBE battle started — recording per-ship damage/kills.");
    }

    private static void Clear()
    {
        DamageToVictim.Clear();
        Finisher.Clear();
        LastAttacker.Clear();
        Ships.Clear();
        DealtTotal.Clear();
        ReceivedTotal.Clear();
        Active = false;
    }

    internal static void RecordHit(Ship victim, Ship? attacker, float dmg)
    {
        try
        {
            if (victim == null)
                return;
            IntPtr vp = victim.Pointer;
            Ships[vp] = victim;

            bool dead = false;
            try { dead = victim.isDead; } catch { }

            if (attacker != null)
            {
                IntPtr ap = attacker.Pointer;
                Ships[ap] = attacker;

                if (dmg > 0f)
                {
                    if (!DamageToVictim.TryGetValue(vp, out var byAttacker))
                        DamageToVictim[vp] = byAttacker = new Dictionary<IntPtr, float>();
                    byAttacker.TryGetValue(ap, out float prev);
                    byAttacker[ap] = prev + dmg;

                    DealtTotal.TryGetValue(ap, out float d); DealtTotal[ap] = d + dmg;
                    ReceivedTotal.TryGetValue(vp, out float r); ReceivedTotal[vp] = r + dmg;
                }

                // Track the last attacker WHILE the victim is alive; the moment it's first seen dead,
                // that alive-attacker is the finisher (the shot that took it down — not later corpse hits).
                if (!dead)
                    LastAttacker[vp] = ap;
                else if (!Finisher.ContainsKey(vp))
                    Finisher[vp] = LastAttacker.TryGetValue(vp, out var la) ? la : ap;
            }
        }
        catch { }
    }

    internal static void EndBattleLog()
    {
        try
        {
            if (!Active)
            {
                Clear();
                return;
            }
            var sunk = new HashSet<IntPtr>();
            foreach (var kv in Ships)
            {
                try { if (kv.Value != null && kv.Value.isDead) sunk.Add(kv.Key); } catch { }
            }
            foreach (IntPtr k in Finisher.Keys)
                sunk.Add(k);

            if (ModSettings.BattleRuntimeDiagnosticsEnabled)
            {
                var log = Melon<UADVanillaPlusMod>.Logger;
                foreach (IntPtr vp in sunk)
                {
                    string victim = NameOf(vp);
                    IntPtr fin = Finisher.TryGetValue(vp, out var f) ? f
                               : LastAttacker.TryGetValue(vp, out var la) ? la : IntPtr.Zero;
                    string sank = fin != IntPtr.Zero ? NameOf(fin) : "?";

                    string wrecked = "?"; float wreckDmg = 0f;
                    if (DamageToVictim.TryGetValue(vp, out var byAttacker))
                        foreach (var ad in byAttacker)
                            if (ad.Value > wreckDmg) { wreckDmg = ad.Value; wrecked = NameOf(ad.Key); }

                    ReceivedTotal.TryGetValue(vp, out float recv);
                    log.Msg($"UADVP_RECPROBE SUNK {victim}: sankBy={sank} wreckedBy={wrecked}({wreckDmg:0}) totalReceived={recv:0}");
                }

                int shown = 0;
                foreach (var ad in SortedDesc(DealtTotal))
                {
                    if (shown++ >= 12) break;
                    int kills = 0;
                    foreach (IntPtr vp in sunk)
                    {
                        IntPtr fin = Finisher.TryGetValue(vp, out var f) ? f
                                   : LastAttacker.TryGetValue(vp, out var la) ? la : IntPtr.Zero;
                        if (fin == ad.Key) kills++;
                    }
                    log.Msg($"UADVP_RECPROBE DEALER {NameOf(ad.Key)}: dealt={ad.Value:0} kills={kills}");
                }

                log.Msg($"UADVP_RECPROBE battle ended — {sunk.Count} ship(s) sunk, {DealtTotal.Count} attacker(s) tracked.");
            }

            if (ModSettings.ShipServiceRecordsEnabled)
                PersistPlayerRecords(sunk);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_RECPROBE end-log failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Clear();
        }
    }

    private static IEnumerable<KeyValuePair<IntPtr, float>> SortedDesc(Dictionary<IntPtr, float> d)
    {
        var list = new List<KeyValuePair<IntPtr, float>>(d);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list;
    }

    private static string NameOf(IntPtr ptr)
    {
        if (!Ships.TryGetValue(ptr, out Ship? s) || s == null)
            return "?";
        try { string n = s.Name(false, false, false, false, true); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        try { string v = s.vesselName; if (!string.IsNullOrWhiteSpace(v)) return v; } catch { }
        return "?";
    }

    // Append this battle's results for the MAIN player's participating ships to the persistent records.
    private static void PersistPlayerRecords(HashSet<IntPtr> sunk)
    {
        try
        {
            Player? main = ModCampaignState.MainPlayerOrNull();
            if (main == null)
                return;
            IntPtr mainPtr;
            try { mainPtr = main.Pointer; } catch { return; }

            string date = DateLabel();
            var results = new List<ShipServiceRecords.BattleResult>();
            foreach (var kv in Ships)
            {
                Ship? s = kv.Value;
                if (s == null)
                    continue;
                IntPtr owner = IntPtr.Zero;
                try { if (s.player != null) owner = s.player.Pointer; } catch { }
                if (owner != mainPtr)
                    continue;

                IntPtr sp = kv.Key;
                DealtTotal.TryGetValue(sp, out float dealt);
                ReceivedTotal.TryGetValue(sp, out float recv);
                bool sankThis = sunk.Contains(sp);
                if (dealt <= 0f && recv <= 0f && !sankThis)
                    continue; // didn't meaningfully participate

                // Per-victim breakdown: every enemy this ship dealt damage to, with how much, and
                // whether this ship sank it (finisher) / wrecked it (most damage to a ship that went down).
                int kills = 0, wrecks = 0;
                var victims = new List<ShipServiceRecords.VictimHit>();
                foreach (var vkv in DamageToVictim)
                {
                    IntPtr vp = vkv.Key;
                    if (vp == sp || !vkv.Value.TryGetValue(sp, out float dmgToThem) || dmgToThem <= 0f)
                        continue;
                    bool victimSunk = sunk.Contains(vp);
                    bool sankIt = false, wreckedIt = false;
                    if (victimSunk)
                    {
                        IntPtr fin = Finisher.TryGetValue(vp, out var f) ? f
                                   : LastAttacker.TryGetValue(vp, out var la) ? la : IntPtr.Zero;
                        sankIt = fin == sp;
                        IntPtr top = IntPtr.Zero; float best = 0f;
                        foreach (var ad in vkv.Value) if (ad.Value > best) { best = ad.Value; top = ad.Key; }
                        wreckedIt = top == sp;
                    }
                    if (sankIt) kills++;
                    if (wreckedIt) wrecks++;
                    victims.Add(new ShipServiceRecords.VictimHit
                    {
                        Name = NameOf(vp), Damage = dmgToThem, Sank = sankIt, Wrecked = wreckedIt,
                        Type = TypeOf(vp), Tonnage = TonnageOf(vp),
                    });
                }

                results.Add(new ShipServiceRecords.BattleResult
                {
                    Id = SafeId(s), Name = NameOf(sp), Type = SafeType(s), Date = date,
                    Dealt = dealt, Received = recv, Kills = kills, Wrecks = wrecks, Sunk = sankThis,
                    Victims = victims,
                });
            }

            if (results.Count > 0)
                ShipServiceRecords.RecordBattle(results, date);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP ship records persist failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafeId(Ship s) { try { return s.id.ToString(); } catch { return string.Empty; } }
    private static string SafeType(Ship s) { try { return s.shipType.ToString(); } catch { return "?"; } }

    private static string TypeOf(IntPtr ptr)
    {
        if (Ships.TryGetValue(ptr, out Ship? s) && s != null)
            try { return s.shipType.ToString(); } catch { }
        return "?";
    }

    private static float TonnageOf(IntPtr ptr)
    {
        if (Ships.TryGetValue(ptr, out Ship? s) && s != null)
            try { return s.tonnage; } catch { }
        return 0f;
    }

    private static string DateLabel()
    {
        try { var cc = CampaignController.Instance; if (cc != null) return cc.CurrentDate.ToString(); } catch { }
        return string.Empty;
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.RegisterTakenDamage))]
internal static class BattleShipRegisterDamagePatch
{
    [HarmonyPostfix]
    private static void Postfix(Ship victim, Part from, float damage)
    {
        if (!BattleShipRecorder.Active || victim == null)
            return;
        try
        {
            Ship? attacker = null;
            try { attacker = from?.ship; } catch { }

            if (ModSettings.BattleRuntimeDiagnosticsEnabled && BattleShipRecorder.HitDiag < 20)
            {
                BattleShipRecorder.HitDiag++;
                string vn = "?"; try { vn = victim.Name(false, false, false, false, true); } catch { }
                string an = "null"; try { if (attacker != null) an = attacker.Name(false, false, false, false, true); } catch { }
                bool dead = false; try { dead = victim.isDead; } catch { }
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_RECPROBE HIT#{BattleShipRecorder.HitDiag} from={an} -> {vn} dmg={damage:0} dead={dead}");
            }

            BattleShipRecorder.RecordHit(victim, attacker, damage);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.AcceptBattle))]
internal static class BattleShipRecordBeginAcceptPatch
{
    [HarmonyPostfix]
    private static void Postfix() => BattleShipRecorder.Begin();
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.StartCustomBattle))]
internal static class BattleShipRecordBeginCustomPatch
{
    [HarmonyPostfix]
    private static void Postfix() => BattleShipRecorder.Begin();
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.LeaveBattle))]
internal static class BattleShipRecordEndPatch
{
    [HarmonyPrefix]
    private static void Prefix() => BattleShipRecorder.EndBattleLog();
}
