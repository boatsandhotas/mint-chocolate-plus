using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.Harmony;

// Diagnostic: log the id NATIVE passes to CampaignProvinceBattlePopupUI.Show, so we learn the exact id form
// GetBattleFromUi accepts (our 5 globe candidates all fail -> strat=-1 -> we fall back to manually populating
// the popup, which shows the wrong army-force quantity). On the FLAT map, native's own hover calls
// Show(goodId); this captures it. Once we know the form, the globe can call native Show() and get the exact
// numbers/flags/advance the 2D popup shows, and drop the manual populate. (While strat=-1 our globe code
// never calls Show, so every Show logged here is native's.)
[HarmonyPatch(typeof(CampaignProvinceBattlePopupUI), nameof(CampaignProvinceBattlePopupUI.Show))]
internal static class CampaignBattlePopupShowProbe
{
    private static string lastId = ""; // sentinel

    [HarmonyPrefix]
    private static void Prefix(string id)
    {
        try
        {
            if (id == lastId) return;
            lastId = id;
            bool resolves = false;
            try { resolves = ProvinceBattleManager.GetBattleFromUi(id) != null; } catch { }
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC_GLOBE Show(id) native call id='{id}' GetBattleFromUi.resolves={resolves}");
        }
        catch { }
    }
}
