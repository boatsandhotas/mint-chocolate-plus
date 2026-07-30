using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace MintChipPlus.Harmony;

// Vanilla PortPopupUI.Show lists some ships twice (e.g. a vessel that's both stationed and part
// of a task force sitting in the port) — a display-only duplicate; the port tonnage total is
// computed correctly. After Show builds the rows, hide any row whose text (name + stats) is an
// exact repeat of an earlier one, keeping the first. (PortPopupUI.vessels is on a nested type
// and not accessible, so we dedupe by the rows' own rendered text.)
[HarmonyPatch(typeof(PortPopupUI), nameof(PortPopupUI.Show))]
internal static class CampaignPortShipListDedupePatch
{
    [HarmonyPostfix]
    private static void Postfix(PortPopupUI __instance)
    {
        try
        {
            var rows = __instance.vesselsToDestroy;
            if (rows == null || rows.Count == 0)
                return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int hidden = 0;
            foreach (GameObject row in rows)
            {
                if (row == null || !row.activeSelf)
                    continue;
                string key = RowKey(row);
                if (string.IsNullOrEmpty(key))
                    continue; // can't identify it — leave it alone
                if (seen.Add(key))
                    continue; // first time we've seen this exact row
                row.SetActive(false);
                hidden++;
            }

            if (hidden > 0)
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC port ship-list: hid {hidden} duplicate row(s).");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC port ship-list dedupe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string RowKey(GameObject row)
    {
        try
        {
            var texts = row.GetComponentsInChildren<Text>(true);
            if (texts == null)
                return string.Empty;
            var sb = new StringBuilder();
            foreach (Text t in texts)
            {
                if (t != null)
                    sb.Append(t.text).Append('|');
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }
}
