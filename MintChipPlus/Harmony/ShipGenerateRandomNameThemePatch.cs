using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Phase 2 Stage 2: apply class naming themes. Ship.GenerateRandomName is the single
// chokepoint (static; fires per ship build with the design class name + nation). When
// the human player's design class has a theme assigned, override the generated name.
[HarmonyPatch(typeof(Ship), nameof(Ship.GenerateRandomName))]
internal static class ShipGenerateRandomNameThemePatch
{
    [HarmonyPostfix]
    private static void Postfix(
        bool isDesign,
        ShipType shipType,
        PlayerData playerData,
        string className,
        Il2CppSystem.Collections.Generic.HashSet<string> existingNames,
        ref string __result)
    {
        try
        {
            if (isDesign)
                return; // name actual ships, not design templates
            if (!ModSettings.ClassNamingThemesEnabled)
                return;
            if (!ModCampaignState.IsMainPlayer(playerData))
                return; // human player only

            // Key by the game's real base-class name (refit suffix / clone counter /
            // type prefix stripped) so all refits of a class share the theme.
            string key = ShipNameParts.BaseName(className, shipType);
            ClassThemeAssignments.Choice? choice = ClassThemeAssignments.Get(key);
            if (choice == null || choice.Mode == ClassThemeAssignments.Mode.Off)
                return;

            if (choice.Mode == ClassThemeAssignments.Mode.Sequential)
            {
                __result = $"{key}-{choice.SeqNext + 1}";
                ClassThemeAssignments.BumpSeq(key);
                return;
            }

            // ThemePool mode.
            string nation = SafeStr(() => playerData?.name).ToLowerInvariant();
            List<string> names = NameThemeDatabase.GetNamesForTheme(choice.ThemeName, nation);
            if (names == null || names.Count == 0)
                return;
            NameThemeDatabase.Shuffle(names);

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingNames != null)
            {
                try
                {
                    foreach (string n in existingNames)
                        if (!string.IsNullOrEmpty(n))
                            taken.Add(n);
                }
                catch
                {
                }
            }
            if (!string.IsNullOrEmpty(__result))
                taken.Add(__result);

            string? picked = NameThemeDatabase.PickNextUnused(names, taken);
            if (!string.IsNullOrWhiteSpace(picked))
                __result = picked;
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC naming theme apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafeStr(Func<string?> f)
    {
        try { return f() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
