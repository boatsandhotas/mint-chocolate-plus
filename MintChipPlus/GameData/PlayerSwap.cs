using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// "Defect" — switch which nation the player controls mid-campaign. There is no live "switch player"
// path in the game, and the controlled-player bindings (PlayerController.Instance, the division list,
// camera, economy/UI) are independently cached at load. So we mirror the proven save-edit recipe: flip
// isMain/isAi on both nations, carry the campaign-id GUID (the human's aiName, which MC keys its
// per-campaign state on) onto the new nation, give the abandoned nation a fresh AI id, then SAVE and
// RELOAD — the game's load pipeline rebuilds every binding from the flags and the AI adopts your old
// empire automatically (AI gate everywhere is isAi && !isMain).
internal static class PlayerSwap
{
    internal static Player? CurrentHuman()
    {
        try
        {
            var players = CampaignController.Instance?.CampaignData?.Players;
            if (players == null)
                return null;
            foreach (Player p in players)
                if (p != null && SafeBool(() => p.isMain))
                    return p;
        }
        catch { }
        return null;
    }

    // Major nations the player can become (majors other than the current human).
    internal static List<Player> SwitchTargets()
    {
        var list = new List<Player>();
        try
        {
            var players = CampaignController.Instance?.CampaignData?.Players;
            if (players == null)
                return list;
            foreach (Player p in players)
            {
                if (p == null) continue;
                try { if (p.isMajor && !p.isMain) list.Add(p); } catch { }
            }
        }
        catch { }
        return list;
    }

    internal static bool CanSwitch(out string reason)
    {
        reason = string.Empty;
        try
        {
            if (CampaignController.Instance == null) { reason = "not in a campaign"; return false; }
            if (GameManager.IsBattle) { reason = "leave the current battle first"; return false; }
        }
        catch { reason = "campaign state unavailable"; return false; }
        return true;
    }

    internal static string NationLabel(Player? p)
    {
        if (p == null) return "?";
        try { string? ui = p.data?.nameUi; if (!string.IsNullOrWhiteSpace(ui)) return ui; } catch { }
        try { string? n = p.data?.name; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return "?";
    }

    internal static void SwitchTo(Player target)
    {
        if (!CanSwitch(out string reason))
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC nation switch: cannot switch — {reason}.");
            return;
        }
        if (target == null)
            return;

        Player? oldHuman = CurrentHuman();
        if (oldHuman == null)
        {
            Melon<MintChipPlusMod>.Logger.Warning("UADMC nation switch: no current human player found.");
            return;
        }
        try { if (target.Pointer == oldHuman.Pointer) { Melon<MintChipPlusMod>.Logger.Warning("UADMC nation switch: already controlling that nation."); return; } } catch { }

        string oldName = NationLabel(oldHuman);
        string newName = NationLabel(target);

        try
        {
            // Carry the campaign-id GUID to the new human FIRST so MC per-campaign state stays keyed
            // (ModCampaignState keys on the human's aiName; if it didn't move it would reset/restamp).
            string campaignGuid = SafeStr(() => oldHuman.aiName);

            try { target.prevAiName = SafeStr(() => target.aiName); } catch { }
            if (!string.IsNullOrEmpty(campaignGuid))
                try { target.aiName = campaignGuid; } catch { }

            // The abandoned nation becomes a normal AI with its own fresh id (not the campaign key).
            try { oldHuman.prevAiName = campaignGuid; } catch { }
            try { oldHuman.aiName = Guid.NewGuid().ToString(); } catch { }

            // Flip control: new nation -> human, old nation -> AI.
            try { target.isMain = true; target.isAi = false; } catch { }
            try { oldHuman.isMain = false; oldHuman.isAi = true; } catch { }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC nation switch: flag swap failed — {ex.GetType().Name}: {ex.Message}. Aborting before save.");
            return;
        }

        Melon<MintChipPlusMod>.Logger.Msg($"UADMC nation switch: {oldName} -> {newName}. Saving and returning to main menu — click Continue to resume as {newName}.");

        try { GameManager.Save(true, true); }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC nation switch: save failed — {ex.GetType().Name}: {ex.Message}."); return; }

        // Route the reload through the main menu rather than an in-session ContinueCampaign: driving
        // the load programmatically from mid-World leaves the loading screen stuck ("dontChangeLoadingScreen
        // improperly set"). ToMainMenu uses the game's own clean transition; the player clicks Continue
        // to load the just-saved campaign as the new nation.
        try { GameManager.Instance?.ToMainMenu(); }
        catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC nation switch: to-main-menu failed — {ex.GetType().Name}: {ex.Message}. The switch is saved; load the campaign manually."); }
    }

    private static string SafeStr(Func<string?> f) { try { return f() ?? string.Empty; } catch { return string.Empty; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
}
