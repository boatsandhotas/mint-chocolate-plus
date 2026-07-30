using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Follow-steer diagnostics (read-only). Roughly once per second while in battle, for each player
// division that has followers, logs each follower's steering state so we can characterize the
// "S-pattern" weave a fast hull with a slow rudder shows while station-keeping, and size a
// derivative-damping rudder controller for it:
//   hdg  = compass heading
//   av   = Ship.angularVelocity (yaw rate) — the candidate damping (D-term) signal
//   rud  = Ship.rudderManual (current manual rudder; '-' = auto/null, '?' = unreadable)
//   eLead= signed heading error vs the division leader (the weave shows as this oscillating)
//   eStn = heading error to the ship's CalcFollowing station bearing (what the follow controller chases)
//   dStn = distance (m) to that station point — large while rejoining, small in formation
//   evd  = evadingTorpedoes (T/-) — set while torpedo avoidance has taken over this ship's steering
//   obs  = evadingObstacleRatio — >0 while dodging a collision/obstacle
//   tag  = what the damper decided: DAMP (yaw clipped) / rejoin (off-station, skipped) /
//          evade (actively dodging, skipped) / ok (low yaw, untouched) / '-' (damping off)
// The damper EXEMPTS a follower only while it is dodging torpedoes (evd=T) — that dodge needs full rudder.
// Routine collision avoidance (obs>0) is NOT exempt (it fires constantly in formation; exempting it would
// gut the damper) — obs is logged only. To size the post-dodge rejoin handling: enable Battle Runtime
// Diagnostics, watch UADMC_FOLLOWLOG through a torpedo engagement, and see whether ships coming off an
// evade (evd T->-) get tagged DAMP while still far out (large dStn) — if so, add a distance-to-station
// gate (damp only when dStn is small) sized from those numbers, rather than guessing it now.
// Driven from MC's existing Ui.Update postfix (alongside BattleTurn). Gated behind Battle Runtime
// Diagnostics for the log; the damper itself runs only when Follow Steering Damping is On.
internal static class FollowSteerProbe
{
    // Soft-clip damping. Yaw rate up to DampThreshold is left ALONE so followers can still turn to
    // keep station; only the EXCESS above the threshold is scaled by DampExcessKeep (lower = more
    // damping). This shaves the oversteer spikes that cause the weave without zeroing the yaw — a flat
    // per-frame multiply froze the followers (station error ballooned), which was the "overtuned" feel.
    private const float DampThreshold = 0.7f;
    private const float DampExcessKeep = 0.5f;
    // Only damp a follower that is roughly IN formation (its heading is within this of its station
    // bearing). A ship far off station is rejoining and needs full rudder — damping its big correction
    // strands it (observed: a follower stuck 130 off its leader, crawling back at av 0.7). Damping is
    // for the small steady-state weave, not large rejoin turns.
    private const float DampMaxStationErrDeg = 30f;
    private static float lastSample;

    internal static void Tick(Ui? ui)
    {
        try
        {
            if (ui == null || !GameManager.IsBattle)
                return;

            if (ModSettings.FollowSteerDampingEnabled)
                ApplyDamping();

            if (ModSettings.BattleRuntimeDiagnosticsEnabled)
            {
                float now = Time.realtimeSinceStartup;
                if (now - lastSample >= 1.0f)
                {
                    lastSample = now;
                    Dump();
                }
            }
        }
        catch { }
    }

    // Bleed off each follower's yaw rate every frame (leaders untouched) to dampen the overshoot that
    // produces the S-pattern weave.
    private static void ApplyDamping()
    {
        if (BattleTurn.IsTurning)
            return; // never fight a commanded reverse maneuver
        var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
        if (divisions == null)
            return;
        foreach (Division d in divisions)
        {
            if (d == null)
                continue;
            var ships = SafeShips(d);
            if (ships == null || ships.Count < 2)
                continue;
            Ship? lead = SafeLead(d);
            for (int i = 0; i < ships.Count; i++)
            {
                Ship s = ships[i];
                if (s == null)
                    continue;
                if (lead != null && SamePtr(s, lead))
                    continue; // leader steers normally
                try
                {
                    Ship? ahead = i > 0 ? ships[i - 1] : lead;
                    float av = s.angularVelocity;
                    if (Classify(d, s, ahead, av, out float damped) == DampDecision.Damped)
                        s.angularVelocity = damped;
                }
                catch { }
            }
        }
    }

    private enum DampDecision { Damped, SkipOffStation, SkipLowYaw, SkipEvading }

    // Single source of truth for the damping decision, shared by ApplyDamping (acts) and Dump (reports
    // the tag) so the log shows exactly what the damper did to each follower. dampedAv is the new yaw.
    private static DampDecision Classify(Division d, Ship s, Ship? ahead, float av, out float dampedAv)
    {
        dampedAv = av;
        // A ship actively dodging torpedoes has handed its rudder to the game's avoidance AI — it needs
        // full rudder, exactly like a rejoin or a commanded reverse. Never damp it. (Narrowed to torpedoes
        // ONLY; the post-dodge "hard turn back into line" final approach is still gated by stnErr below —
        // size a tighter rejoin test from a real torpedo-engagement FOLLOWLOG before changing that.)
        if (IsEvading(s))
            return DampDecision.SkipEvading;
        float stnErr = StationErr(d, s, ahead);
        // Far off station (or unknown) -> rejoining, needs full rudder.
        if (stnErr < 0f || stnErr > DampMaxStationErrDeg)
            return DampDecision.SkipOffStation;
        float mag = av < 0f ? -av : av;
        if (mag <= DampThreshold)
            return DampDecision.SkipLowYaw; // gentle turn (e.g. a well-behaved slow BB) — untouched
        float damped = DampThreshold + (mag - DampThreshold) * DampExcessKeep;
        dampedAv = av < 0f ? -damped : damped;
        return DampDecision.Damped;
    }

    private static string Tag(DampDecision dec) => dec switch
    {
        DampDecision.Damped => "DAMP",
        DampDecision.SkipOffStation => "rejoin",
        DampDecision.SkipEvading => "evade",
        _ => "ok",
    };

    // Exempt ONLY an active torpedo dodge. While evadingTorpedoes the game's avoidance AI owns the rudder
    // (turn parallel to the fish, weave side-to-side); clipping its yaw would blunt the dodge. We do NOT
    // exempt routine collision/obstacle avoidance (evadingObstacleRatio) — it fires constantly in close
    // formation, so exempting it would gut the damper. obs is still logged for visibility.
    private static bool IsEvading(Ship s)
    {
        try { return s.evadingTorpedoes; } catch { return false; }
    }

    private static void Dump()
    {
        var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
        if (divisions == null)
            return;

        foreach (Division d in divisions)
        {
            if (d == null)
                continue;
            var ships = SafeShips(d);
            if (ships == null || ships.Count < 2)
                continue; // only divisions that actually have followers

            Ship? lead = SafeLead(d);
            float leadHdg = lead != null ? Yaw(lead) : -1f;

            var parts = new List<string>();
            for (int i = 0; i < ships.Count; i++)
            {
                Ship s = ships[i];
                if (s == null)
                    continue;
                if (lead != null && SamePtr(s, lead))
                    continue; // followers only
                Ship? ahead = i > 0 ? ships[i - 1] : lead;
                float av = 0f; try { av = s.angularVelocity; } catch { }
                string tag = ModSettings.FollowSteerDampingEnabled ? Tag(Classify(d, s, ahead, av, out _)) : "-";
                parts.Add(
                    $"{Name(s)}:hdg={D(Yaw(s))} spd={Spd(s)} av={AV(s)} eLead={D(SignedErr(Yaw(s), leadHdg))} " +
                    $"eStn={D(StationErr(d, s, ahead))} dStn={Dist(StationDist(d, s, ahead))} evd={Evd(s)} obs={Obs(s)} {tag}");
            }
            if (parts.Count == 0)
                continue;

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC_FOLLOWLOG div={DivId(d)} damp={(ModSettings.FollowSteerDampingEnabled ? "On" : "Off")} lead={Name(lead)}:hdg={D(leadHdg)}/av={(lead != null ? AV(lead) : "?")} foll=[{string.Join(", ", parts)}]");
        }
    }

    // Distance (meters) from the ship to its CalcFollowing station point. Large while rejoining, small in
    // formation — pairs with eStn (heading error) to show how far out of formation a follower really is.
    private static float StationDist(Division d, Ship s, Ship? ahead)
    {
        try
        {
            if (ahead == null)
                return -1f;
            Vector3 station = d.CalcFollowing(s, ahead, false, true, 1f);
            Vector3 pos = s.transform.position;
            Vector3 to = station - pos; to.y = 0f;
            return to.magnitude;
        }
        catch { return -1f; }
    }

    private static string Dist(float v) => v < 0f ? "?" : v.ToString("0", CultureInfo.InvariantCulture);
    private static string Evd(Ship s) { try { return s.evadingTorpedoes ? "T" : "-"; } catch { return "?"; } }
    private static string Obs(Ship s) { try { return s.evadingObstacleRatio.ToString("0.0", CultureInfo.InvariantCulture); } catch { return "?"; } }

    // Heading error (degrees) between the ship's forward and the bearing to its CalcFollowing station.
    private static float StationErr(Division d, Ship s, Ship? ahead)
    {
        try
        {
            if (ahead == null)
                return -1f;
            Vector3 station = d.CalcFollowing(s, ahead, false, true, 1f);
            Vector3 pos = s.transform.position;
            Vector3 toStation = station - pos; toStation.y = 0f;
            if (toStation.sqrMagnitude < 0.01f)
                return 0f;
            Vector3 fwd = s.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f)
                return -1f;
            return Vector3.Angle(fwd, toStation);
        }
        catch { return -1f; }
    }

    // ---- helpers ----

    private static Il2CppSystem.Collections.Generic.List<Ship>? SafeShips(Division d) { try { return d.ships; } catch { return null; } }
    private static Ship? SafeLead(Division d) { try { return d.leader; } catch { return null; } }
    private static string Spd(Ship s) { try { return s.savedCurrentSpeed.ToString("0", System.Globalization.CultureInfo.InvariantCulture); } catch { return "?"; } }
    private static bool SamePtr(Ship a, Ship b) { try { return a.Pointer == b.Pointer; } catch { return false; } }
    private static float Yaw(Ship s) { try { return s.transform.eulerAngles.y; } catch { return -1f; } }

    private static string AV(Ship s)
    {
        try { return s.angularVelocity.ToString("0.00", CultureInfo.InvariantCulture); } catch { return "?"; }
    }

    private static string Rud(Ship s)
    {
        try { var r = s.rudderManual; return r.HasValue ? r.Value.ToString(CultureInfo.InvariantCulture) : "-"; } catch { return "?"; }
    }

    // Signed heading delta a-b normalized to [-180, 180]; -999 if either invalid.
    private static float SignedErr(float a, float b)
    {
        if (a < 0f || b < 0f) return -999f;
        float e = (a - b) % 360f;
        if (e > 180f) e -= 360f;
        if (e < -180f) e += 360f;
        return e;
    }

    private static string DivId(Division d) { try { return (d.Pointer.ToInt64() & 0xFFFF).ToString("X4"); } catch { return "????"; } }
    private static string Name(Ship? s) { try { return s == null ? "?" : (string.IsNullOrWhiteSpace(s.vesselName) ? s.name : s.vesselName); } catch { return "?"; } }
    private static string D(float v) => v <= -999f ? "?" : v.ToString("0", CultureInfo.InvariantCulture);
}
