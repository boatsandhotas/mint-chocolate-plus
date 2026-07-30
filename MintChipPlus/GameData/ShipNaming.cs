using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// Phase 2: family-wide class rename. Mirrors the save tool's RenameGenericShips family
// rename, reusing the established refit logic (ShipNameParts) for base/suffix. Renaming a
// class to a theme: every design template and the lead ship take the theme's first name;
// the remaining ships take successive theme names. Each member keeps its own refit suffix
// ("(Jul. 1904)") so refit variants stay dated. Human player only.
internal static class ShipNaming
{
    // Returns the new base class key (theme's first name) when the class was renamed, so
    // callers can re-key the theme assignment; empty string if nothing was renamed.
    internal static string RenameClassToTheme(string baseKey, string themeName, string nation)
    {
        try
        {
            if (string.IsNullOrEmpty(baseKey) || string.IsNullOrEmpty(themeName))
                return string.Empty;

            Player? player = ModCampaignState.MainPlayerOrNull();
            if (player == null)
                return string.Empty;

            // Theme name pool in CSV order (first = class/lead name, "first N" behavior).
            List<string> pool = NameThemeDatabase.GetNamesForTheme(themeName, nation);
            if (pool == null || pool.Count == 0)
                return string.Empty;

            // Family design templates: player designs whose base name matches the class key.
            var designs = new Il2CppSystem.Collections.Generic.List<Ship>(player.designs);
            var familyDesigns = new List<Ship>();
            var familyPtrs = new HashSet<IntPtr>();
            foreach (Ship d in designs)
            {
                if (d == null)
                    continue;
                if (string.Equals(BaseOf(d), baseKey, StringComparison.OrdinalIgnoreCase))
                {
                    familyDesigns.Add(d);
                    familyPtrs.Add(d.Pointer);
                }
            }

            // Class ships: built ships whose design is in the family (fallback: base name match,
            // so generically-named ships of the class are still caught).
            var classShips = new List<Ship>();
            foreach (Ship s in player.GetFleetAll())
            {
                if (s == null || s.isDesign)
                    continue;
                bool inFamily = false;
                try
                {
                    Ship? design = s.design;
                    inFamily = design != null && familyPtrs.Contains(design.Pointer);
                }
                catch
                {
                }
                if (!inFamily)
                    inFamily = string.Equals(BaseOf(s), baseKey, StringComparison.OrdinalIgnoreCase);
                if (inFamily)
                    classShips.Add(s);
            }

            string className = pool[0];
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { className };
            int renamed = 0;

            // Design templates -> class name (each keeps its own refit suffix).
            foreach (Ship d in familyDesigns)
                renamed += Rename(d, className);

            // Lead ship: prefer the one already carrying the class base name, else the first.
            Ship? lead = null;
            foreach (Ship s in classShips)
            {
                if (string.Equals(BaseOf(s), baseKey, StringComparison.OrdinalIgnoreCase))
                {
                    lead = s;
                    break;
                }
            }
            if (lead == null && classShips.Count > 0)
                lead = classShips[0];
            if (lead != null)
                renamed += Rename(lead, className);

            // Remaining ships -> successive theme names.
            foreach (Ship s in classShips)
            {
                if (s == lead)
                    continue;
                string? next = NameThemeDatabase.PickNextUnused(pool, taken);
                if (next == null)
                    break; // pool exhausted
                renamed += Rename(s, next);
            }

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC rename class '{baseKey}' -> theme '{themeName}': {familyDesigns.Count} design(s), {classShips.Count} ship(s), {renamed} renamed.");

            // Class key changes only if a design template was renamed.
            return familyDesigns.Count > 0 && renamed > 0 ? ShipNameParts.BaseName(className, null) : string.Empty;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC class rename failed: {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    private static int Rename(Ship ship, string themedBase)
    {
        try
        {
            ship.SetShipName(themedBase + ShipNameParts.RefitSuffix(ship.name));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static string BaseOf(Ship s)
    {
        try { return ShipNameParts.BaseName(s.name, s.shipType); }
        catch { return string.Empty; }
    }
}
