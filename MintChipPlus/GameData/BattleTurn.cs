using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MintChipPlus.GameData;

// Battle reverse-course hotkeys: R reverses each SELECTED division to PORT (left), T to STARBOARD
// (right). The actual maneuver is chosen at runtime from the options menu (ModSettings.BattleReverseMethod):
//
//   Single180        - reverse the column order + a single MoveDir(~179 to the chosen side).
//   NinetySwapNinety - MoveDir(90); once the turn is INITIATED (leader has swung ~20) swap the column
//                      order; once the whole line reaches ~90 finish with MoveDir(180).
//   SplitRejoin      - keep the leader in the division and split every FOLLOWER out into its own
//                      one-ship division, command each ship to reverse on the same frame (true
//                      simultaneous start); once each is turning the right way, add the followers back
//                      into the original division (reversed order) to finish the turn together.
//   Rudder           - put each ship hard over via SetRudderManual; once turning, hand back to the
//                      division to finish (experimental — typically only the lead responds).
//
// SplitRejoin/Rudder fall back to NinetySwapNinety if their machinery can't start. While SplitRejoin is
// the selected method, a UADMC_TOPO line is logged whenever player division membership changes, so a
// manual in-game split/rejoin can be captured. Player-initiated (hotkey), so no balance toggle.
internal static class BattleTurn
{
    private const float ReverseDegrees = 179f;     // single per-ship swing to the chosen side (unambiguous)
    private const float NinetyDegrees = 90f;
    private const float OneEightyDegrees = 180f;
    private const float InitiatedDeg = 20f;         // turned this far from the start heading = "turn initiated"
    private const float AlignToleranceDeg = 15f;    // 90/90 final: whole line must be this close to 90
    private const float CarrotDegrees = 150f;       // split: re-aim each ship this far ahead of its CURRENT heading each tick (a moving carrot < 180 = unambiguous hard turn)
    private const float ReachToleranceDeg = 20f;    // split: a ship has "come round" once within this of its reverse heading
    private const float MaxGameSeconds = 30f;       // safety: finish/merge even if a milestone never hits
    private const int MaxSplitShips = 8;            // sanity cap for the split path
    private const int RudderFallbackHalf = 30;      // used only if rudderManualHalfLimit reads <= 0
    private const float SampleSeconds = 1.0f;

    private enum Mode { Ninety, Split, Rudder }

    private sealed class ShipLeg
    {
        public Ship Ship = null!;
        public Vector3 Start;              // heading when the turn began
        public Vector3 ReverseDir;         // this ship's own heading reversed 180 (the per-ship goal)
        public Division TempDiv = null!;   // the leader's leg keeps the ORIGINAL division here
        public bool IsLead;
        public bool Reached;               // has come round to (near) its reverse heading
    }

    private sealed class PendingTurn
    {
        public Mode Mode;
        public string DivId = "????";
        public float Age;
        public int Samples;

        // Division modes (Ninety, Rudder)
        public Division Div = null!;
        public Vector3 LeadStart;
        public Vector3 Step1Dir;
        public Vector3 FinalDir;
        public Vector3 CommonReverse;
        public Il2CppSystem.Collections.Generic.List<Ship> Reorder = null!;
        public bool Swapped;
        public List<Ship>? RudderShips;

        // Split mode
        public Division Original = null!;
        public List<ShipLeg> Legs = null!;
        public List<Ship> Order = null!;
        public float Sign;
        public bool Merged;
    }

    private static readonly Dictionary<IntPtr, PendingTurn> Pending = new();
    private static string lastTopoSig = string.Empty;
    private static float lastTurnActivity = -999f;

    private static float SafeNow() { try { return Time.realtimeSinceStartup; } catch { return 0f; } }

    // True while a reverse maneuver is running (plus a short tail through the unit finish) so the
    // follow-steer damper doesn't fight a commanded turn.
    internal static bool IsTurning => Pending.Count > 0 || (SafeNow() - lastTurnActivity) < 12f;

    internal static void TryHotkey(Ui? ui)
    {
        try
        {
            if (ui == null || !GameManager.IsBattle)
            {
                if (Pending.Count > 0)
                    Pending.Clear();
                lastTopoSig = string.Empty;
                return;
            }

            LogTopologyIfChanged();
            ProcessPending();

            bool port = Input.GetKeyDown(KeyCode.R);
            bool starboard = Input.GetKeyDown(KeyCode.T);
            if (port == starboard)
                return;

            ReverseSelected(ui, starboard);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC reverse-course failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Reverse every currently-selected division 180° to PORT (starboard=false) or STARBOARD (starboard=true)
    // using the maneuver chosen in the options menu (ModSettings.BattleReverseMethod). Shared by the R/T
    // hotkeys and the HELM panel's on-screen Port/Stbd buttons so both paths behave identically.
    internal static void ReverseSelected(Ui? ui, bool starboard)
    {
        try
        {
            if (ui == null || !GameManager.IsBattle)
                return;

            float sign = starboard ? 1f : -1f;
            string side = starboard ? "starboard" : "port";
            ModSettings.BattleTurnMethod method = ModSettings.BattleReverseMethod;

            var selected = ui.selectedShips;
            if (selected == null || selected.Count == 0)
                return;

            var seen = new HashSet<IntPtr>();
            int turned = 0;
            foreach (Ship s in selected)
            {
                if (s == null)
                    continue;
                Division? d = SafeDiv(s);
                if (d == null)
                    continue;
                IntPtr ptr;
                try { ptr = d.Pointer; } catch { continue; }
                if (!seen.Add(ptr))
                    continue;
                if (StartTurn(d, sign, side, method))
                    turned++;
            }

            if (turned > 0)
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC reverse-course [{ModSettings.BattleTurnMethodText(method)}]: {turned} division(s) to {side}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC reverse-course failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool StartTurn(Division d, float sign, string side, ModSettings.BattleTurnMethod method)
    {
        List<Ship> order = SnapshotOrder(d);
        if (order.Count < 1)
            return false;

        if (order.Count == 1)
        {
            try { d.MoveDir(Rotate(ForwardOf(order[0]), sign * ReverseDegrees), true); return true; } catch { return false; }
        }

        switch (method)
        {
            case ModSettings.BattleTurnMethod.Single180:
                return StartSingle180(d, order, sign, side);
            case ModSettings.BattleTurnMethod.SplitRejoin:
                if (order.Count <= MaxSplitShips && TryStartSplit(d, order, sign, side)) return true;
                return StartNinety(d, order, sign, side);
            case ModSettings.BattleTurnMethod.Rudder:
                if (StartRudder(d, order, sign, side)) return true;
                return StartNinety(d, order, sign, side);
            case ModSettings.BattleTurnMethod.NinetySwapNinety:
            default:
                return StartNinety(d, order, sign, side);
        }
    }

    // ---- Single 180 ----

    private static bool StartSingle180(Division d, List<Ship> order, float sign, string side)
    {
        try
        {
            Vector3 forward = ForwardOf(order[0]);
            Vector3 target = Rotate(forward, sign * ReverseDegrees);
            var reorder = ReversedList(order);
            ReorderAndRefresh(d, reorder);
            d.MoveDir(target, true);
            Log($"single180 div={DivId(SafePtr(d))} side={side} ships={order.Count} leadHdg={Deg(HeadingOf(forward))} -> Reorder + MoveDir({Deg(HeadingOf(target))})");
            return true;
        }
        catch (Exception ex) { Log($"single180 FAILED {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    // ---- 90 / swap-when-initiated / 90 ----

    private static bool StartNinety(Division d, List<Ship> order, float sign, string side)
    {
        try
        {
            Vector3 forward = ForwardOf(order[0]);
            Vector3 step1 = Rotate(forward, sign * NinetyDegrees);
            Vector3 final = Rotate(forward, sign * OneEightyDegrees);
            var reorder = ReversedList(order);

            d.MoveDir(step1, true);
            string divId = DivId(SafePtr(d));
            Log($"ninety start div={divId} side={side} ships={order.Count} leadHdg={Deg(HeadingOf(forward))} step1={Deg(HeadingOf(step1))} final={Deg(HeadingOf(final))}");
            Pending[SafePtr(d)] = new PendingTurn
            {
                Mode = Mode.Ninety, DivId = divId, Div = d,
                LeadStart = forward, Step1Dir = step1, FinalDir = final, Reorder = reorder,
                Age = 0f, Samples = 0
            };
            return true;
        }
        catch (Exception ex) { Log($"ninety start FAILED {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private static void ProcessNinety(IntPtr key, PendingTurn p, float dt, List<IntPtr> done)
    {
        p.Age += dt;

        if (!p.Swapped)
        {
            float ledTurned = LeaderTurned(p.Div, p.LeadStart);
            if (ledTurned >= InitiatedDeg)
            {
                ReorderAndRefresh(p.Div, p.Reorder);
                p.Swapped = true;
                Log($"ninety swap div={p.DivId} t={Fmt(p.Age)}s ledTurned={Deg(ledTurned)} (column reversed)");
            }
        }

        float maxToStep1 = MaxAngleTo(p.Div, p.Step1Dir);
        bool aligned = maxToStep1 >= 0f && maxToStep1 <= AlignToleranceDeg;
        bool timedOut = p.Age >= MaxGameSeconds;

        if (p.Age >= p.Samples * SampleSeconds)
        {
            p.Samples++;
            Log($"ninety t={Fmt(p.Age)}s div={p.DivId} swapped={p.Swapped} maxToStep1={Deg(maxToStep1)} hdgs={Hdgs(p.Div)}");
        }

        if (!(aligned || timedOut))
            return;

        if (!p.Swapped) ReorderAndRefresh(p.Div, p.Reorder);
        try { p.Div.MoveDir(p.FinalDir, true); } catch { }
        Log($"ninety final div={p.DivId} t={Fmt(p.Age)}s reason={(aligned ? "aligned" : "timeout")} -> MoveDir({Deg(HeadingOf(p.FinalDir))})");
        done.Add(key);
    }

    // ---- Split / turn together / rejoin-when-initiated ----
    // Keep the leader in the original division (so it never empties); split only the followers out.

    private static bool TryStartSplit(Division d, List<Ship> order, float sign, string side)
    {
        string divId = DivId(SafePtr(d));
        Vector3 leadForward = ForwardOf(order[0]);
        Vector3 commonReverse = Rotate(leadForward, OneEightyDegrees);
        IntPtr dPtr = SafePtr(d);
        var legs = new List<ShipLeg>();

        // PHASE A — structure only: split every follower into its own one-ship division FIRST, before
        // any steering order. RemoveShip on the original wipes pending orders, so steering issued during
        // the split (the old bug: the lead never turned) gets clobbered. Lead stays in d.
        try
        {
            for (int i = 0; i < order.Count; i++)
            {
                Ship s = order[i];
                Vector3 start = ForwardOf(s);

                if (i == 0)
                {
                    legs.Add(new ShipLeg { Ship = s, Start = start, TempDiv = d, IsLead = true });
                    continue;
                }

                // RemoveShip(s,true,null) removes s from the original (leaves it division-less), then
                // Create(s) adds the orphan to a fresh one-ship division (no duplicate).
                try { d.RemoveShip(s, true, null); } catch (Exception ex) { Log($"split RemoveShip FAILED div={divId} ship=\"{SafeName(s)}\" {ex.GetType().Name}: {ex.Message}"); }
                Division? nd = null;
                try { nd = DivisionsManager.Create(s); } catch (Exception ex) { Log($"split Create FAILED div={divId} ship=\"{SafeName(s)}\" {ex.GetType().Name}: {ex.Message}"); }
                Division? sdiv = SafeDiv(s);
                bool stillInOriginal = Contains(d, s);
                if (nd == null || sdiv == null || SafePtr(sdiv) == dPtr || stillInOriginal)
                {
                    Log($"split bad-state div={divId} ship=\"{SafeName(s)}\" ndNull={nd == null} sdiv={(sdiv == null ? "null" : DivId(SafePtr(sdiv)))} stillInOrig={stillInOriginal} -> restore + fallback");
                    legs.Add(new ShipLeg { Ship = s, Start = start, TempDiv = sdiv ?? d });
                    RestoreSplit(legs, d);
                    return false;
                }
                legs.Add(new ShipLeg { Ship = s, Start = start, TempDiv = sdiv });
            }
        }
        catch (Exception ex)
        {
            Log($"split setup FAILED div={divId} {ex.GetType().Name}: {ex.Message}");
            RestoreSplit(legs, d);
            return false;
        }

        // PHASE B — steering: structure is final, so no later RemoveShip can wipe these. Each ship's
        // goal is its OWN heading reversed 180; ProcessSplit drives it there with a "moving carrot" —
        // every tick it re-aims the ship CarrotDegrees (<180) ahead of its CURRENT heading, so it keeps
        // turning hard the same way (unambiguous, scatter-proof) until it comes round. Issue the first
        // carrot now.
        foreach (ShipLeg leg in legs)
        {
            leg.ReverseDir = Rotate(leg.Start, OneEightyDegrees);
            Division? div = SafeDiv(leg.Ship);
            try { if (div != null) div.MoveDir(Rotate(ForwardOf(leg.Ship), sign * CarrotDegrees), true); } catch { }
        }

        Pending[dPtr] = new PendingTurn
        {
            Mode = Mode.Split, DivId = divId, Original = d, Legs = legs, Order = order, Sign = sign,
            CommonReverse = commonReverse, Reorder = ReversedList(order), Age = 0f, Samples = 0
        };
        Log($"split start div={divId} side={side} ships={legs.Count} leadHdg={Deg(HeadingOf(leadForward))} carrot={CarrotDegrees:0} revHdg={Deg(HeadingOf(commonReverse))} {SplitStates(legs)}");
        LogTopology("after-split");
        return true;
    }

    private static void ProcessSplit(IntPtr key, PendingTurn p, float dt, List<IntPtr> done)
    {
        p.Age += dt;
        bool timedOut = p.Age >= MaxGameSeconds;
        bool allReached = true;
        foreach (ShipLeg leg in p.Legs)
        {
            if (leg.Reached) continue;
            Division? div = SafeDiv(leg.Ship);
            float toRev = AngleTo(leg.Ship, leg.ReverseDir);
            if (toRev >= 0f && toRev <= ReachToleranceDeg)
            {
                // Come round — stop the carrot and hold the reverse so it doesn't overshoot while it
                // waits for stragglers.
                leg.Reached = true;
                try { div?.MoveDir(leg.ReverseDir, true); } catch { }
            }
            else
            {
                allReached = false;
                // Re-aim the carrot CarrotDegrees ahead of the CURRENT heading -> keep turning hard.
                try { div?.MoveDir(Rotate(ForwardOf(leg.Ship), p.Sign * CarrotDegrees), true); } catch { }
            }
        }

        if (p.Age >= p.Samples * SampleSeconds)
        {
            p.Samples++;
            Log($"split t={Fmt(p.Age)}s div={p.DivId} {SplitStates(p.Legs)}");
        }

        if (!(allReached || timedOut) || p.Merged)
            return;

        // Everyone is turning the right way -> move the followers back into the original division and
        // finish to the full 180 as a unit. (A move is RemoveShip-from-source THEN add-to-target; a
        // bare AddShip leaves the ship in BOTH.)
        p.Merged = true;
        Division d = p.Original;
        foreach (ShipLeg leg in p.Legs)
        {
            if (leg.IsLead) continue;
            MoveShipTo(leg.Ship, d, "merge");
        }

        // Erase any now-empty temp divisions.
        foreach (ShipLeg leg in p.Legs)
        {
            if (leg.IsLead || leg.TempDiv == null) continue;
            try { if (SafePtr(leg.TempDiv) != SafePtr(d) && DivisionShipCount(leg.TempDiv) == 0) DivisionsManager.Erase(leg.TempDiv); } catch { }
        }

        ReorderAndRefresh(d, p.Reorder);
        try { d.MoveDir(p.CommonReverse, true); } catch { }
        Log($"split merge div={p.DivId} t={Fmt(p.Age)}s reason={(timedOut ? "timeout" : "all-round")} -> rejoin + Reorder + MoveDir({Deg(HeadingOf(p.CommonReverse))}) {SplitStates(p.Legs)}");
        LogTopology("after-merge");
        done.Add(key);
    }

    // Best-effort: pull any split-out followers back into the original division if a split aborts.
    private static void RestoreSplit(List<ShipLeg> legs, Division original)
    {
        foreach (ShipLeg leg in legs)
        {
            if (leg.IsLead) continue;
            if (!Contains(original, leg.Ship))
                MoveShipTo(leg.Ship, original, "restore");
            try
            {
                if (leg.TempDiv != null && SafePtr(leg.TempDiv) != SafePtr(original) && DivisionShipCount(leg.TempDiv) == 0)
                    DivisionsManager.Erase(leg.TempDiv);
            }
            catch { }
        }
        LogTopology("after-restore");
    }

    // ---- Rudder (experimental) ----

    private static bool StartRudder(Division d, List<Ship> order, float sign, string side)
    {
        try
        {
            int half = RudderFallbackHalf;
            try { int h = Ship.rudderManualHalfLimit; if (h > 0) half = h; } catch { }
            int rudder = (int)(sign * half);

            Vector3 forward = ForwardOf(order[0]);
            var rudderShips = new List<Ship>();
            foreach (Ship s in order)
            {
                try { s.SetRudderManual(new Il2CppSystem.Nullable<int>(rudder)); rudderShips.Add(s); }
                catch (Exception ex) { Log($"rudder set FAILED ship=\"{SafeName(s)}\" {ex.GetType().Name}"); }
            }
            if (rudderShips.Count == 0)
                return false;

            string divId = DivId(SafePtr(d));
            Log($"rudder start div={divId} side={side} rudder={rudder} half={half} ships={rudderShips.Count} leadHdg={Deg(HeadingOf(forward))}");
            Pending[SafePtr(d)] = new PendingTurn
            {
                Mode = Mode.Rudder, DivId = divId, Div = d, LeadStart = forward,
                CommonReverse = Rotate(forward, OneEightyDegrees), Reorder = ReversedList(order),
                RudderShips = rudderShips, Age = 0f, Samples = 0
            };
            return true;
        }
        catch (Exception ex) { Log($"rudder start FAILED {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private static void ProcessRudder(IntPtr key, PendingTurn p, float dt, List<IntPtr> done)
    {
        p.Age += dt;
        float ledTurned = LeaderTurned(p.Div, p.LeadStart);
        bool timedOut = p.Age >= MaxGameSeconds;

        if (p.Age >= p.Samples * SampleSeconds)
        {
            p.Samples++;
            Log($"rudder t={Fmt(p.Age)}s div={p.DivId} ledTurned={Deg(ledTurned)} hdgs={Hdgs(p.Div)}");
        }

        if (!(ledTurned >= InitiatedDeg || timedOut))
            return;

        if (p.RudderShips != null)
            foreach (Ship s in p.RudderShips)
            {
                try { s.SetRudderManual(new Il2CppSystem.Nullable<int>()); } catch { }
            }
        ReorderAndRefresh(p.Div, p.Reorder);
        try { p.Div.MoveDir(p.CommonReverse, true); } catch { }
        Log($"rudder hand-off div={p.DivId} t={Fmt(p.Age)}s reason={(timedOut ? "timeout" : "initiated")} ledTurned={Deg(ledTurned)} -> Reorder + MoveDir({Deg(HeadingOf(p.CommonReverse))})");
        done.Add(key);
    }

    // ---- shared ----

    private static void ProcessPending()
    {
        if (Pending.Count == 0)
            return;

        lastTurnActivity = SafeNow();

        float dt = 0f;
        try { dt = Time.deltaTime; } catch { }

        var done = new List<IntPtr>();
        foreach (var kv in Pending)
        {
            try
            {
                switch (kv.Value.Mode)
                {
                    case Mode.Split: ProcessSplit(kv.Key, kv.Value, dt, done); break;
                    case Mode.Rudder: ProcessRudder(kv.Key, kv.Value, dt, done); break;
                    default: ProcessNinety(kv.Key, kv.Value, dt, done); break;
                }
            }
            catch (Exception ex) { Log($"process FAILED div={kv.Value.DivId} {ex.GetType().Name}: {ex.Message}"); done.Add(kv.Key); }
        }

        foreach (IntPtr k in done)
            Pending.Remove(k);
    }

    // ---- topology diagnostics (active only while Split is the selected method) ----

    private static void LogTopologyIfChanged()
    {
        if (ModSettings.BattleReverseMethod != ModSettings.BattleTurnMethod.SplitRejoin)
        {
            lastTopoSig = string.Empty;
            return;
        }
        string sig = TopologyString(out string readable);
        if (sig == lastTopoSig)
            return;
        lastTopoSig = sig;
        Log("TOPO change " + readable, "UADMC_TOPO ");
    }

    private static void LogTopology(string tag)
    {
        TopologyString(out string readable);
        Log("TOPO " + tag + " " + readable, "UADMC_TOPO ");
    }

    // Returns a stable signature (for change detection) and a readable dump of the player's divisions.
    private static string TopologyString(out string readable)
    {
        readable = "[]";
        try
        {
            var dm = DivisionsManager.Instance;
            if (dm == null) return string.Empty;
            var divs = dm.MainPlayerDivisions;
            if (divs == null) return string.Empty;

            var sig = new StringBuilder();
            var rb = new StringBuilder();
            for (int i = 0; i < divs.Count; i++)
            {
                Division d = divs[i];
                if (d == null) continue;
                string did = DivId(SafePtr(d));
                sig.Append(did).Append(':');
                if (rb.Length > 0) rb.Append("  ");
                rb.Append("div=").Append(did).Append('[');
                var ships = d.ships;
                if (ships != null)
                    for (int j = 0; j < ships.Count; j++)
                    {
                        Ship s = ships[j];
                        if (s == null) continue;
                        sig.Append(SafePtr(s).ToInt64() & 0xFFFF).Append(',');
                        if (j > 0) rb.Append(", ");
                        rb.Append(SafeName(s));
                    }
                rb.Append(']');
                sig.Append(';');
            }
            readable = rb.ToString();
            return sig.ToString();
        }
        catch { return string.Empty; }
    }

    // ---- helpers ----

    private static void Log(string msg, string prefix = "UADMC_TURNLOG ") => Melon<MintChipPlusMod>.Logger.Msg(prefix + msg);

    private static List<Ship> SnapshotOrder(Division d)
    {
        var order = new List<Ship>();
        try
        {
            var ships = d.ships;
            if (ships != null)
                for (int i = 0; i < ships.Count; i++)
                {
                    Ship s = ships[i];
                    if (s != null) order.Add(s);
                }
        }
        catch { }
        return order;
    }

    private static Il2CppSystem.Collections.Generic.List<Ship> ReversedList(List<Ship> order)
    {
        var list = new Il2CppSystem.Collections.Generic.List<Ship>();
        for (int i = order.Count - 1; i >= 0; i--)
            if (order[i] != null) list.Add(order[i]);
        return list;
    }

    // Reorder the division's ships AND redraw its group UI. Division.leader is COMPUTED from the ship
    // order, so Reorder alone flips the leader internally (rear ship -> new lead) — but the UIDivision
    // panel caches its layout and only rebuilds on RefreshUI, so without this the on-screen group keeps
    // the pre-turn order (the ship now leading still shows at the back). No-op for a 0/1-ship order.
    private static void ReorderAndRefresh(Division d, Il2CppSystem.Collections.Generic.List<Ship> order)
    {
        if (d == null || order == null || order.Count <= 1)
            return;
        try { d.Reorder(order); }
        catch (Exception ex) { Log($"reorder FAILED div={DivId(SafePtr(d))} {ex.GetType().Name}: {ex.Message}"); }
        try { var ui = d.UIElement; if (ui != null) ui.RefreshUI(); }
        catch (Exception ex) { Log($"refreshUI FAILED div={DivId(SafePtr(d))} {ex.GetType().Name}: {ex.Message}"); }
    }

    private static bool Contains(Division d, Ship s)
    {
        try
        {
            var ships = d.ships;
            if (ships == null) return false;
            IntPtr sp = SafePtr(s);
            for (int i = 0; i < ships.Count; i++)
            {
                Ship o = ships[i];
                if (o != null && SafePtr(o) == sp) return true;
            }
        }
        catch { }
        return false;
    }

    private static int DivisionShipCount(Division d)
    {
        try { var ships = d.ships; return ships == null ? 0 : ships.Count; } catch { return 0; }
    }

    // Move a ship into target. Every "add" call on this game (AddShip/Create) adds WITHOUT removing
    // from the current division, so a clean move must RemoveShip from the source first (which leaves
    // the ship division-less) and then add it to the target.
    private static void MoveShipTo(Ship s, Division target, string tag)
    {
        Division? src = SafeDiv(s);
        if (src != null && SafePtr(src) == SafePtr(target)) return;
        try { if (src != null) src.RemoveShip(s, true, null); } catch (Exception ex) { Log($"move RemoveShip FAILED {tag} ship=\"{SafeName(s)}\" {ex.GetType().Name}"); }
        try { target.AddShip(s); } catch (Exception ex) { Log($"move AddShip FAILED {tag} ship=\"{SafeName(s)}\" {ex.GetType().Name}"); }
    }

    private static Division? SafeDiv(Ship s) { try { return s.division; } catch { return null; } }
    private static Ship? SafeLeader(Division d) { try { return d.leader; } catch { return null; } }
    private static IntPtr SafePtr(Il2CppSystem.Object o) { try { return o.Pointer; } catch { return IntPtr.Zero; } }

    private static Vector3 ForwardOf(Ship s)
    {
        Vector3 f = Vector3.forward;
        try { f = s.transform.forward; } catch { }
        f.y = 0f;
        if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
        return f.normalized;
    }

    private static Vector3 Rotate(Vector3 dir, float deg)
    {
        Vector3 r = Quaternion.AngleAxis(deg, Vector3.up) * dir;
        r.y = 0f;
        if (r.sqrMagnitude < 0.0001f) return dir;
        return r.normalized;
    }

    private static float LeaderTurned(Division d, Vector3 start)
    {
        Ship? l = SafeLeader(d);
        if (l == null) return -1f;
        return AngleTo(l, start);
    }

    private static string DivId(IntPtr ptr) { try { return (ptr.ToInt64() & 0xFFFF).ToString("X4"); } catch { return "????"; } }

    private static string SafeName(Ship s)
    {
        try { string v = s.vesselName; if (!string.IsNullOrWhiteSpace(v)) return v; } catch { }
        try { string n = s.name; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return "?";
    }

    private static float AngleTo(Ship s, Vector3 target)
    {
        try
        {
            Vector3 f = s.transform.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return -1f;
            return Vector3.Angle(f, target);
        }
        catch { return -1f; }
    }

    private static float MaxAngleTo(Division d, Vector3 target)
    {
        try
        {
            var ships = d.ships;
            if (ships == null || ships.Count == 0) return -1f;
            float max = -1f;
            for (int i = 0; i < ships.Count; i++)
            {
                Ship s = ships[i];
                if (s == null) continue;
                float a = AngleTo(s, target);
                if (a > max) max = a;
            }
            return max;
        }
        catch { return -1f; }
    }

    private static float ShipHdg(Ship s) { try { return s.transform.eulerAngles.y; } catch { return -1f; } }

    private static string SplitStates(List<ShipLeg> legs)
    {
        try
        {
            var sb = new StringBuilder("[");
            foreach (ShipLeg leg in legs)
            {
                if (sb.Length > 1) sb.Append(", ");
                sb.Append(SafeName(leg.Ship)).Append(leg.IsLead ? "(L)" : "").Append(':').Append(Deg(ShipHdg(leg.Ship)))
                  .Append("/rev").Append(Deg(AngleTo(leg.Ship, leg.ReverseDir))).Append(leg.Reached ? "*" : "");
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch { return "[]"; }
    }

    private static string Hdgs(Division d)
    {
        try
        {
            var ships = d.ships;
            if (ships == null) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < ships.Count; i++)
            {
                Ship s = ships[i];
                if (s == null) continue;
                if (sb.Length > 1) sb.Append(", ");
                sb.Append(SafeName(s)).Append(':').Append(Deg(ShipHdg(s)));
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch { return "[]"; }
    }

    private static float HeadingOf(Vector3 dir)
    {
        Vector3 d = dir; d.y = 0f;
        if (d.sqrMagnitude < 0.0001f) return 0f;
        float deg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        return (deg % 360f + 360f) % 360f;
    }

    private static string Deg(float deg) => deg < 0f ? "?" : deg.ToString("0", CultureInfo.InvariantCulture);
    private static string Fmt(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
}
