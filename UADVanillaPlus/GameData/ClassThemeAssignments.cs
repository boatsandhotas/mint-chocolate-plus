using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UADVanillaPlus.GameData;

// Phase 2 Stage 2: per-campaign storage of ship-class naming-theme choices, persisted
// via ModCampaignState's side-car (no loose config, keyed by campaign). Keyed by the
// base class name (refit-date suffix stripped) so all refits of a class share a theme.
internal static class ClassThemeAssignments
{
    internal enum Mode { Off = 0, ThemePool = 1, Sequential = 2 }

    internal sealed class Choice
    {
        internal Mode Mode = Mode.Off;
        internal string ThemeName = string.Empty;
        internal int SeqNext;   // next index for Sequential mode
    }

    private const string Feature = "class_themes";
    private static readonly Dictionary<string, Choice> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string _loadedKey = string.Empty;

    // Keys are pre-normalized by callers via ShipNameParts.BaseName(name, shipType)
    // so theme keying matches the game's real refit base-name logic exactly.

    private static void EnsureLoaded()
    {
        string key = ModCampaignState.CampaignKey();
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
            return;

        _loadedKey = key;
        _map.Clear();
        if (string.IsNullOrEmpty(key))
            return;

        string raw = ModCampaignState.Load(Feature);
        if (string.IsNullOrEmpty(raw))
            return;

        foreach (string entry in raw.Split(';'))
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            string[] f = entry.Split('|');
            if (f.Length < 4)
                continue;
            string baseName = Unescape(f[0]);
            if (baseName.Length == 0)
                continue;
            _map[baseName] = new Choice
            {
                Mode = Enum.TryParse(f[1], out Mode m) ? m : Mode.Off,
                ThemeName = Unescape(f[2]),
                SeqNext = int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
            };
        }
    }

    internal static Choice? Get(string key)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(key) && _map.TryGetValue(key, out Choice? c) ? c : null;
    }

    internal static void Set(string key, Choice choice)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key))
            return;
        if (choice == null || choice.Mode == Mode.Off)
            _map.Remove(key);
        else
            _map[key] = choice;
        Persist();
    }

    internal static void BumpSeq(string key)
    {
        Choice? c = Get(key);
        if (c == null || c.Mode != Mode.Sequential)
            return;
        c.SeqNext++;
        Persist();
    }

    private static void Persist()
    {
        var sb = new StringBuilder();
        foreach (var kv in _map)
        {
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(Escape(kv.Key)).Append('|')
              .Append((int)kv.Value.Mode).Append('|')
              .Append(Escape(kv.Value.ThemeName)).Append('|')
              .Append(kv.Value.SeqNext.ToString(CultureInfo.InvariantCulture));
        }
        ModCampaignState.Save(Feature, sb.ToString());
    }

    private static string Escape(string s) => (s ?? string.Empty).Replace("|", "%7C").Replace(";", "%3B");
    private static string Unescape(string s) => (s ?? string.Empty).Replace("%7C", "|").Replace("%3B", ";");
}
