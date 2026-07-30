using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// The vanilla campaign AI grossly under-funds merchant/transport capacity (observed: a major sitting
// at ~8% transport when ~200% is the healthy max and is essential to its economy — it funds tech
// before transport). This makes AI majors fund their economy by the player-requested PRIORITY LADDER:
//   1. transport capacity up to 200%, then 2. technology, then 3. crew training.
//
// The three AI budgets are set, in order, by CampaignController.AiTechBudget -> AiTransportCapacityBudget
// -> AiCrewTrainingBudget during turn resolution (before the economy spends). We postfix the LAST one
// (AiCrewTrainingBudget) and rewrite all three together so our values are what the economy consumes
// this turn (a per-turn OnNewTurn pass would be clobbered by the AI next turn before spend).
//
// The reallocation is denomination-agnostic and bankruptcy-safe: it only REDISTRIBUTES the AI's own
// already-chosen naval budget total toward transport (never increases total spend), and self-limits as
// transport approaches 200%. Gated to AI majors only (never the human). First pass logs the budget
// figures so the (native-opaque) units can be confirmed and tuned.
[HarmonyPatch(typeof(CampaignController))]
internal static class CampaignAiEconomyPrioritiesPatch
{
    private const float TransportTarget = 2.0f; // transportCapacity is a need-ratio: 2.0 = 200%
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    [HarmonyPostfix]
    [HarmonyPatch("AiCrewTrainingBudget")]
    private static void AiCrewTrainingBudgetPostfix(Player player)
    {
        try
        {
            if (!ModSettings.AiEconomyPrioritiesEnabled || player == null)
                return;
            if (!Safe(() => player.isMajor) || Safe(() => player.isMain))
                return;

            float cap = SafeF(() => player.transportCapacity);
            float bTr = SafeF(() => player.transportCapacityBudget);
            float bTech = SafeF(() => player.techBudget);
            float bCrew = SafeF(() => player.trainingBudget);
            float maxCrew = SafeF(() => player.GetMaxCrewTrainingBudget());

            LogOnce(player, cap, bTr, bTech, bCrew, maxCrew);

            if (cap >= TransportTarget)
                return; // already at/above 200% — leave the AI's own split alone

            float total = bTr + bTech + bCrew;
            if (total <= 0f)
                return;

            // Bias toward transport while below target (full bias at 0%, fading to 0 at 200%).
            float t = (TransportTarget - cap) / TransportTarget;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;

            float newTr = bTr + (total - bTr) * t;           // step 1: transport first
            float left = Math.Max(0f, total - newTr);
            float newTech = Math.Min(bTech, left);           // step 2: tech next
            left = Math.Max(0f, left - newTech);
            float newCrew = left;                            // step 3: crew last
            if (maxCrew > 0f && newCrew > maxCrew)
                newCrew = maxCrew;                           // respect the crew-training cap

            try { player.transportCapacityBudget = newTr; } catch { }
            try { player.techBudget = newTech; } catch { }
            try { player.trainingBudget = newCrew; } catch { }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC AI economy priorities failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogOnce(Player player, float cap, float bTr, float bTech, float bCrew, float maxCrew)
    {
        try
        {
            string name = SafeStr(() => player.data?.name);
            if (string.IsNullOrEmpty(name) || !Logged.Add(name))
                return;
            float budget = SafeF(() => player.Budget());
            float state = SafeF(() => player.StateBudget());
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC_ECONLOG {name}: transportCap={cap:0.000} budgets[tr={bTr:0.##} tech={bTech:0.##} crew={bCrew:0.##}] maxCrew={maxCrew:0.##} navalBudget={budget:0} stateBudget={state:0}");
        }
        catch { }
    }

    private static float SafeF(Func<float> f) { try { return f(); } catch { return 0f; } }
    private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }
    private static string SafeStr(Func<string?> f) { try { return f() ?? string.Empty; } catch { return string.Empty; } }
}
