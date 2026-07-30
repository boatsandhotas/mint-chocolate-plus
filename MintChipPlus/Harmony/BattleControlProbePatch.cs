using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.Harmony;

// TEMPORARY diagnostics for the not-yet-built in-battle features (division speed-sync,
// battle-turn, pre-battle divisions, compass-heading). Driven from MC's already-working
// Ui.Update postfix (InGameOptionsMenuPatch.UpdatePostfix calls SampleIfBattle) rather than
// its own Harmony hooks — the earlier dedicated patches on Ui.OnSpeedSliderUp / OnEnterState
// did not attach. Throttled to once every ~8s while in battle. Dumps each player division's
// leader + per-ship set-speed / max / heading(hdg) / ordered-course(moveDir) / ai + LEAD.
// Remove once these features are built.
internal static class BattleControlProbe
{
    private static float lastSample;

    internal static void SampleIfBattle()
    {
        try
        {
            if (!GameManager.IsBattle)
                return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - lastSample < 8f)
                return;
            lastSample = now;
            DumpDivisions("sample");
        }
        catch { }
    }

    internal static void DumpDivisions(string ctx)
    {
        try
        {
            var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
            if (divisions == null)
            {
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_BATTLEPROBE [{ctx}] no player divisions.");
                return;
            }

            int i = 0;
            foreach (Division d in divisions)
            {
                if (d == null)
                    continue;
                Ship? lead = SafeLead(d);
                var ships = d.ships;
                var parts = new List<string>();
                if (ships != null)
                {
                    foreach (Ship s in ships)
                    {
                        if (s == null)
                            continue;
                        bool isLead = lead != null && s.Pointer == lead.Pointer;
                        parts.Add($"{Name(s)}[set={F(() => s.engineCustomSpeed):0.0} max={F(() => s.SpeedMax()):0.0} hdg={F(() => Yaw(s)):0} mdir={F(() => s.moveDir):0} ai={B(() => s.isAiControlled)}{(isLead ? " LEAD" : string.Empty)}]");
                    }
                }
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC_BATTLEPROBE [{ctx}] div{i} leader={Name(lead)} leaderSet={F(() => lead != null ? lead.engineCustomSpeed : -1f):0.0} ships=[{string.Join(", ", parts)}]");
                i++;
            }
            if (i == 0)
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_BATTLEPROBE [{ctx}] 0 player divisions in list.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC_BATTLEPROBE [{ctx}] failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static float Yaw(Ship s) { try { return s.transform.eulerAngles.y; } catch { return -1f; } }
    private static Ship? SafeLead(Division d) { try { return d.leader; } catch { return null; } }
    private static string Name(Ship? s) { try { return s?.name ?? "?"; } catch { return "?"; } }
    private static float F(Func<float> f) { try { return f(); } catch { return -1f; } }
    private static bool B(Func<bool> f) { try { return f(); } catch { return false; } }
}
