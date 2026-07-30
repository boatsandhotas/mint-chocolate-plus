using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// TEMPORARY diagnostic: dump each player division's formation + the per-follower STATION geometry so
// we can see why the native "abreast" (Formation.Line) packs as a delta, and design a true-abreast
// override. The station a follower is told to hold comes from Division.CalcFollowing — followers DO
// natively steer to it (ordinary formation-keeping works, unlike manual per-ship steering), so reading
// that station is exactly what we need.
//
// Per follower it logs, relative to the LEADER (axes = leader heading):
//   pos(a/l)  = current position: a = along heading (+ ahead / - behind), l = lateral (+ starboard)
//   want(a/l) = the CalcFollowing desired station, same decomposition
// Plus a one-line note whenever a division's Formation value changes. Gated behind Battle Runtime
// Diagnostics; makes NO changes. Remove once true-abreast is designed.
internal static class FormationProbe
{
    private static float lastSample;
    private static readonly Dictionary<IntPtr, int> LastFormation = new();

    internal static void Tick()
    {
        try
        {
            if (!GameManager.IsBattle || !ModSettings.BattleRuntimeDiagnosticsEnabled)
                return;
            float now = Time.realtimeSinceStartup;
            if (now - lastSample < 1.0f)
                return;
            lastSample = now;

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
                if (lead == null)
                    continue;

                string form = SafeForm(d);
                // Change note
                try
                {
                    IntPtr key = d.Pointer;
                    int fv = (int)d.formation;
                    if (!LastFormation.TryGetValue(key, out int prev))
                        LastFormation[key] = fv;
                    else if (prev != fv)
                    {
                        LastFormation[key] = fv;
                        Melon<MintChipPlusMod>.Logger.Msg($"UADMC_FORMLOG div={DivId(d)} formation CHANGED -> {form}");
                    }
                }
                catch { }

                Vector3 lp, fwd, right;
                try
                {
                    lp = lead.transform.position;
                    fwd = lead.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude < 0.0001f ? Vector3.forward : fwd.normalized;
                    right = lead.transform.right; right.y = 0f; right = right.sqrMagnitude < 0.0001f ? Vector3.right : right.normalized;
                }
                catch { continue; }

                var parts = new List<string>();
                for (int i = 0; i < ships.Count; i++)
                {
                    Ship s = ships[i];
                    if (s == null || SamePtr(s, lead))
                        continue;
                    Ship? ahead = i > 0 ? ships[i - 1] : lead;

                    (float a, float l) pos = Decompose(SafePos(s) - lp, fwd, right);
                    string want = "?";
                    try
                    {
                        Vector3 station = d.CalcFollowing(s, ahead, false, true, 1f);
                        (float a, float l) w = Decompose(station - lp, fwd, right);
                        want = $"{N(w.a)}/{N(w.l)}";
                    }
                    catch { }

                    parts.Add($"{Name(s)}:pos={N(pos.a)}/{N(pos.l)} want={want}");
                }
                if (parts.Count == 0)
                    continue;

                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC_FORMLOG div={DivId(d)} form={form} lead={Name(lead)} [{string.Join(", ", parts)}]");
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC_FORMLOG failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (float a, float l) Decompose(Vector3 off, Vector3 fwd, Vector3 right)
    {
        off.y = 0f;
        return (Vector3.Dot(off, fwd), Vector3.Dot(off, right));
    }

    private static Il2CppSystem.Collections.Generic.List<Ship>? SafeShips(Division d) { try { return d.ships; } catch { return null; } }
    private static Ship? SafeLead(Division d) { try { return d.leader; } catch { return null; } }
    private static bool SamePtr(Ship a, Ship b) { try { return a.Pointer == b.Pointer; } catch { return false; } }
    private static Vector3 SafePos(Ship s) { try { return s.transform.position; } catch { return Vector3.zero; } }
    private static string SafeForm(Division d) { try { return d.formation.ToString(); } catch { return "?"; } }
    private static string DivId(Division d) { try { return (d.Pointer.ToInt64() & 0xFFFF).ToString("X4"); } catch { return "????"; } }
    private static string Name(Ship? s) { try { return s == null ? "?" : (string.IsNullOrWhiteSpace(s.vesselName) ? s.name : s.vesselName); } catch { return "?"; } }
    private static string N(float v) => Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture);
}
