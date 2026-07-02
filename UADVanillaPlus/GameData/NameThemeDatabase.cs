using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Phase 2 Stage 1: ship-naming theme database (pure data/logic, no game or Il2Cpp
// dependency). Ports the save-tool's NameDatabase + the shuffle / pick-next helpers.
// The CSV name pool ships embedded in the DLL (no loose config file).
//
// A "theme" is a named flat name-pool. Three groups: national (per-country), universal
// (shadowed by a same-named national theme, optionally culture-gated), and conquered
// territory (another nation's national themes, prefixed). Naming = shuffle the pool and
// take the first name not already used (callers pass base-name-stripped taken sets).
internal static class NameThemeDatabase
{
    internal sealed class ThemeInfo
    {
        internal string ThemeName = string.Empty;
        internal int NameCount;
        internal int MinShips;
        internal string Description = string.Empty;
        internal bool IsNational;
        internal bool IsConquered;
        internal string? Territory;
    }

    // country -> theme -> list of (name, minShips)
    private static Dictionary<string, Dictionary<string, List<(string name, int minShips)>>>? _db;
    // theme -> set of country keys that culturally own it (gates universal culture themes)
    private static Dictionary<string, HashSet<string>>? _themeCultures;
    private static bool _initialized;

    internal static void EnsureLoaded()
    {
        if (_initialized)
            return;
        _initialized = true;

        _db = new Dictionary<string, Dictionary<string, List<(string, int)>>>(StringComparer.Ordinal);
        _themeCultures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        try
        {
            string? text = ReadEmbeddedCsv();
            if (string.IsNullOrEmpty(text))
            {
                Melon<UADVanillaPlusMod>.Logger.Warning("UADVP naming-db: embedded ship_names.csv not found; themes unavailable.");
                return;
            }

            int rows = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length < 3)
                    continue;

                string country = parts[0].Trim().ToLowerInvariant();
                if (country.Length == 0 || country == "country") // skip the CSV header row
                    continue;

                string theme = parts[1].Trim();
                string name = parts[2].Trim();
                if (theme.Length == 0 || name.Length == 0)
                    continue;

                int minShips = parts.Length > 3 && int.TryParse(parts[3].Trim(), out int min) ? min : 0;

                if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]))
                {
                    if (!_themeCultures.TryGetValue(theme, out var set))
                        _themeCultures[theme] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string c in parts[4].Split('|'))
                    {
                        string cKey = c.Trim().ToLowerInvariant();
                        if (cKey.Length > 0)
                            set.Add(cKey);
                    }
                }

                if (!_db.TryGetValue(country, out var themes))
                    _db[country] = themes = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
                if (!themes.TryGetValue(theme, out var list))
                    themes[theme] = list = new List<(string, int)>();
                list.Add((name, minShips));
                rows++;
            }

            LogLoadSummary(rows);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP naming-db: load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ReadEmbeddedCsv()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ship_names.csv", StringComparison.OrdinalIgnoreCase));
        if (resName == null)
            return null;

        using Stream? stream = asm.GetManifestResourceStream(resName);
        if (stream == null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void LogLoadSummary(int rows)
    {
        try
        {
            var countries = _db!.Keys.Where(k => k != "universal").OrderBy(k => k, StringComparer.Ordinal).ToList();
            int universalThemes = _db.TryGetValue("universal", out var u) ? u.Count : 0;
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP naming-db: loaded {rows} names — countries({countries.Count})=[{string.Join(",", countries)}] universalThemes={universalThemes} cultureGated={_themeCultures!.Count}.");
        }
        catch
        {
        }
    }

    // --- queries (mirror the save-tool NameDatabase) ---

    internal static List<ThemeInfo> GetAvailableThemes(string country, int classSize, List<string>? conqueredTerritories = null)
    {
        EnsureLoaded();
        country = (country ?? string.Empty).ToLowerInvariant();

        var national = new List<ThemeInfo>();
        var universal = new List<ThemeInfo>();
        var conquered = new List<ThemeInfo>();

        bool countryExists = _db!.ContainsKey(country);
        if (countryExists)
            foreach (var theme in _db[country])
                AddIfAny(national, theme.Key, theme.Value, classSize, GetDescription(theme.Key, country), isNational: true);

        if (_db.TryGetValue("universal", out var universalThemes))
        {
            foreach (var theme in universalThemes)
            {
                if (countryExists && _db[country].ContainsKey(theme.Key))
                    continue; // national theme of same name shadows it

                if (_themeCultures!.TryGetValue(theme.Key, out var cultures))
                {
                    bool native = cultures.Contains(country);
                    bool viaConquest = conqueredTerritories != null &&
                        conqueredTerritories.Any(t => cultures.Contains((t ?? string.Empty).ToLowerInvariant()));
                    if (!native && !viaConquest)
                        continue;
                }

                AddIfAny(universal, theme.Key, theme.Value, classSize, GetDescription(theme.Key, "universal"), isNational: false);
            }
        }

        if (conqueredTerritories != null)
        {
            foreach (string territory in conqueredTerritories)
            {
                string key = (territory ?? string.Empty).ToLowerInvariant();
                if (key.Length == 0 || !_db.TryGetValue(key, out var territoryThemes))
                    continue;

                string display = TitleCase(territory!);
                foreach (var theme in territoryThemes)
                {
                    var names = theme.Value.Where(n => n.minShips <= classSize).ToList();
                    if (names.Count == 0)
                        continue;
                    conquered.Add(new ThemeInfo
                    {
                        ThemeName = $"{display} {theme.Key}",
                        NameCount = names.Count,
                        MinShips = theme.Value.Min(n => n.minShips),
                        Description = $"{theme.Key} from conquered {display}",
                        IsConquered = true,
                        Territory = display,
                    });
                }
            }
        }

        IEnumerable<ThemeInfo> Sort(List<ThemeInfo> l) => l.OrderBy(t => t.MinShips).ThenBy(t => t.ThemeName, StringComparer.Ordinal);
        return Sort(national).Concat(Sort(universal)).Concat(Sort(conquered)).ToList();
    }

    private static void AddIfAny(List<ThemeInfo> dst, string themeName, List<(string name, int minShips)> all, int classSize, string desc, bool isNational)
    {
        int count = all.Count(n => n.minShips <= classSize);
        if (count == 0)
            return;
        dst.Add(new ThemeInfo
        {
            ThemeName = themeName,
            NameCount = count,
            MinShips = all.Min(n => n.minShips),
            Description = desc,
            IsNational = isNational,
        });
    }

    internal static List<string> GetNamesForTheme(string themeName, string country, List<string>? conqueredTerritories = null)
    {
        EnsureLoaded();
        country = (country ?? string.Empty).ToLowerInvariant();
        var names = new List<string>();

        // Conquered-territory theme ("<Territory> <Theme>").
        if (themeName.Contains(' ') && conqueredTerritories != null)
        {
            string[] parts = themeName.Split(new[] { ' ' }, 2);
            string territoryKey = parts[0].ToLowerInvariant();
            string actualTheme = parts[1];
            if (conqueredTerritories.Any(t => (t ?? string.Empty).ToLowerInvariant() == territoryKey) &&
                _db!.TryGetValue(territoryKey, out var tThemes) && tThemes.TryGetValue(actualTheme, out var tNames))
            {
                names.AddRange(tNames.Select(n => n.name));
            }
        }

        // National shadows universal.
        if (_db!.TryGetValue(country, out var cThemes) && cThemes.TryGetValue(themeName, out var cNames))
            names.AddRange(cNames.Select(n => n.name));
        else if (_db.TryGetValue("universal", out var uThemes) && uThemes.TryGetValue(themeName, out var uNames))
            names.AddRange(uNames.Select(n => n.name));

        return names;
    }

    internal static List<string> GetAvailableCountries()
    {
        EnsureLoaded();
        return _db!.Keys.Where(k => k != "universal").ToList();
    }

    internal static bool HasCountry(string country)
    {
        EnsureLoaded();
        return _db!.ContainsKey((country ?? string.Empty).ToLowerInvariant());
    }

    // --- naming helpers (ported from RenameGenericShips) ---

    internal static List<string> Shuffle(List<string> list)
    {
        var rng = new Random();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // First name not already in taken (adds it). Caller seeds taken with base-name-stripped
    // names already in use. Returns null when the pool is exhausted.
    internal static string? PickNextUnused(IEnumerable<string> names, HashSet<string> taken)
    {
        foreach (string name in names)
            if (taken.Add(name))
                return name;
        return null;
    }

    private static string GetDescription(string themeName, string country)
    {
        try
        {
            if (_db!.TryGetValue(country, out var themes) && themes.TryGetValue(themeName, out var list) && list.Count > 0)
                return string.Join(", ", list.Take(4).Select(n => n.name)) + ", etc.";
        }
        catch
        {
        }
        return themeName;
    }

    private static string TitleCase(string s)
        => string.Join(" ", s.Split(' ').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w));
}
