using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MintChipPlus.Harmony;

// STEP-0 feasibility probe for the "Support Land Invasion" idea (player pays naval budget to add force to a
// land offensive; landlocked targets resolve at the nearest coast with diminishing effect). READ-ONLY: it
// dumps the live land-war model — active ProvinceBattles, their army-force numbers, the per-province inputs,
// and the player's army-force levers + cash — so the actual launch/boost can be designed against real data
// WITHOUT risking the save (no writes here; writes come in Step-1 once the data + right lever are confirmed).
//
// Trigger: Ctrl+Shift+L on the campaign map. Output goes to the MelonLoader log (grep "UADMC_LANDPROBE").
[HarmonyPatch(typeof(Cam), "Update")]
internal static class UADMC_LandInvasionSupportProbe
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            if (!(Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.L))) return;
            Probe();
        }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC_LANDPROBE failed {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Probe()
    {
        MelonLogger.Instance L = Melon<MintChipPlusMod>.Logger;
        L.Msg("=== UADMC_LANDPROBE (Step-0, read-only) ===");
        CampaignController cc = CampaignController.Instance;
        if (cc == null) { L.Msg("LANDPROBE no CampaignController (not in campaign?)"); return; }

        Player? main = Safe(() => PlayerController.Instance, (Player?)null);
        if (main != null)
            L.Msg($"LANDPROBE main='{Safe(() => main.Name(false), "?")}' cash={Safe(() => main.cash, 0f):0} armyForce={Safe(() => main.ArmyForce(), 0f):0} helpDistCount={Safe(() => main.ArmyForceForHelpDistribution != null ? main.ArmyForceForHelpDistribution.Count : -1, -1)}");
        else L.Msg("LANDPROBE no main player found");

        int bn = 0;
        try
        {
            var battles = ProvinceBattleManager.Battles;
            if (battles != null)
            {
                foreach (var kv in battles)
                {
                    if (bn++ >= 10) break;
                    ProvinceBattle pb = kv.Value;
                    if (pb == null) continue;
                    Player atk = Safe(() => pb.Attacker, (Player?)null)!;
                    Player def = Safe(() => pb.Defender, (Player?)null)!;
                    float af = 0f, df = 0f;
                    try { if (pb.PlayerArmyForce != null) { if (atk != null) pb.PlayerArmyForce.TryGetValue(atk, out af); if (def != null) pb.PlayerArmyForce.TryGetValue(def, out df); } } catch { }
                    L.Msg($"LANDPROBE battle key='{kv.Key}' title='{Safe(() => ProvinceBattleManager.GetName(pb), "?")}' " +
                          $"atk='{Safe(() => atk.Name(false), "?")}'(force={af:0}) def='{Safe(() => def.Name(false), "?")}'(force={df:0}) " +
                          $"adv={Safe(() => pb.Advance, 0f):0.000} atkLoss={Safe(() => pb.AttackerLosses, 0)} defLoss={Safe(() => pb.DefenderLosses, 0)} " +
                          $"atkProv={ProvName(pb.AttackerProvince)} defProv={ProvName(pb.DefenderProvince)}");
                    if (bn == 1) { ProbeProvince(L, main, Safe(() => pb.AttackerProvince, (Province?)null), "atkProv"); ProbeProvince(L, main, Safe(() => pb.DefenderProvince, (Province?)null), "defProv"); }
                }
            }
            L.Msg($"LANDPROBE active province-battles logged={bn}");
        }
        catch (Exception ex) { L.Msg($"LANDPROBE battles err {ex.GetType().Name}: {ex.Message}"); }

        L.Msg("LANDPROBE primitives (NOT called — Step-1): CampaignController.CreateConquestEvent/CreateLandRebellionEvent/TransferProvinceToNewOwner; ProvinceBattleManager.StartBattle/Conquered; durable levers Province.ProvinceArmyPercentage/Population/ArmyLosses/ProvinceDefenderBonus + Player.ArmyForceForHelpDistribution[prov].");
    }

    private static void ProbeProvince(MelonLogger.Instance L, Player? main, Province? prov, string tag)
    {
        if (prov == null) { L.Msg($"LANDPROBE {tag}=null"); return; }
        L.Msg($"LANDPROBE {tag} id={ProvName(prov)} pop={Safe(() => prov.Population, 0f):0} armyPct={Safe(() => prov.ProvinceArmyPercentage, 0f):0.000} armyLoss={Safe(() => prov.ArmyLosses, 0f):0} defBonus={Safe(() => prov.ProvinceDefenderBonus, 0f):0.000} ctrl='{Safe(() => prov.Controller, "?")}' status={Safe(() => prov.BattleStatus.ToString(), "?")}");
        if (main != null)
            L.Msg($"LANDPROBE {tag} main.ArmyForceForProvince={Safe(() => main.ArmyForceForProvince(prov), 0f):0} fromAllies={Safe(() => main.ArmyForceFromAllies(prov), 0f):0} forHelp={Safe(() => main.ArmyForceForHelp(prov), 0f):0}");
    }

    private static string ProvName(Province? p)
    {
        if (p == null) return "?";
        return Safe(() => p.Id, (string?)null) ?? "?";
    }

    private static T Safe<T>(Func<T> f, T fb) { try { return f(); } catch { return fb; } }
}
