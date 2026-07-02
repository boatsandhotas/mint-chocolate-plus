using System;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace UADVanillaPlus.GameData;

// Phase 0 shared infrastructure: campaign-scoped mod state persisted in a
// PlayerPrefs side-car keyed by start-year + the human player's nation (no clean
// campaign Guid is reachable at runtime). This honors VP's no-loose-config rule,
// and never writes custom fields into the vanilla save (so removing the mod leaves
// a valid save and degrades gracefully). Collisions are only possible across two
// campaigns started in the same year as the same nation; the load-reconcile seeds
// from live state so a stale carryover self-heals rather than corrupts.
internal static class ModCampaignState
{
    // Stable-enough per-campaign key. Empty if unavailable (e.g. outside an active
    // campaign), in which case callers should no-op.
    internal static string CampaignKey()
    {
        try
        {
            var cc = CampaignController.Instance;
            if (cc == null)
                return string.Empty;

            Player? main = MainPlayer(cc);

            // Prefer the human player's aiName: the save-adjust tool stashes a persistent
            // GUID there (Program.cs ensures one), and it survives save/load — giving a
            // stable, unique per-campaign id.
            string aiName = main != null ? SafeStr(() => main.aiName) : string.Empty;
            if (!string.IsNullOrWhiteSpace(aiName))
                return "ai_" + Sanitize(aiName);

            // Fallback when no aiName is set: nation + start year (collision only across
            // two campaigns started the same year as the same nation).
            string nation = main != null ? SafeStr(() => main.data?.name) : string.Empty;
            string key = Sanitize(nation) + "_" + cc.StartYear.ToString();
            return key == "_0" ? string.Empty : key;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Self-sufficiency: when the human player has no aiName, stamp one with a fresh GUID
    // (mirrors the save-adjust tool's Program.cs convention). Vanilla persists aiName
    // across save/load, so once the game next saves this becomes the campaign's stable
    // id with no offline tool required. Idempotent (no-op once an aiName is present).
    internal static void EnsureCampaignId()
    {
        try
        {
            var cc = CampaignController.Instance;
            if (cc == null)
                return;

            Player? main = MainPlayer(cc);
            if (main == null)
                return;

            string aiName = SafeStr(() => main.aiName);
            if (!string.IsNullOrWhiteSpace(aiName))
                return;

            string guid = Guid.NewGuid().ToString();
            main.aiName = guid;
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP campaign-id: stamped main-player aiName GUID {(guid.Length >= 8 ? guid.Substring(0, 8) : guid)} (none was present; persists on next save).");
        }
        catch
        {
        }
    }

    // Human-readable identity for verifying (from the log) that the persistent campaign
    // id survives save/load. Log this on campaign load across two saves and confirm the
    // aiName/key match.
    internal static string DebugIdentity()
    {
        try
        {
            var cc = CampaignController.Instance;
            if (cc == null)
                return "no-campaign";

            Player? main = MainPlayer(cc);
            string mainName = main != null ? SafeStr(() => main.data?.name) : "none";
            string aiName = main != null ? SafeStr(() => main.aiName) : "none";
            return $"mainPlayer={mainName} aiName=\"{aiName}\" key={CampaignKey()}";
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    // The human player's Player object (or null outside an active campaign).
    internal static Player? MainPlayerOrNull()
    {
        try
        {
            var cc = CampaignController.Instance;
            return cc != null ? MainPlayer(cc) : null;
        }
        catch { return null; }
    }

    // True when the given PlayerData is the human player's.
    internal static bool IsMainPlayer(PlayerData pd)
    {
        try
        {
            if (pd == null)
                return false;
            Player? main = MainPlayerOrNull();
            if (main == null)
                return false;
            string a = SafeStr(() => main.data?.name);
            string b = SafeStr(() => pd.name);
            return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // The human player's nation key (lowercased), or empty.
    internal static string MainPlayerNation()
    {
        Player? main = MainPlayerOrNull();
        string n = main != null ? SafeStr(() => main.data?.name) : string.Empty;
        return n.ToLowerInvariant();
    }

    private static Player? MainPlayer(CampaignController cc)
    {
        try
        {
            var players = cc.CampaignData?.Players;
            if (players != null)
                foreach (Player p in players)
                    if (p != null && p.isMain)
                        return p;
        }
        catch
        {
        }
        return null;
    }

    private static string SafeStr(Func<string?> f)
    {
        try { return f() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string PrefKey(string feature, string campaignKey)
        => $"uadvp_state_{feature}_{campaignKey}";

    internal static string Load(string feature)
    {
        string key = CampaignKey();
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        try { return PlayerPrefs.GetString(PrefKey(feature, key), string.Empty); }
        catch { return string.Empty; }
    }

    internal static void Save(string feature, string value)
    {
        string key = CampaignKey();
        if (string.IsNullOrEmpty(key))
            return;

        try
        {
            string pk = PrefKey(feature, key);
            string v = value ?? string.Empty;
            // Only write when changed, to avoid PlayerPrefs churn each turn.
            if (string.Equals(PlayerPrefs.GetString(pk, string.Empty), v, StringComparison.Ordinal))
                return;

            PlayerPrefs.SetString(pk, v);
            PlayerPrefs.Save();
        }
        catch
        {
        }
    }
}
