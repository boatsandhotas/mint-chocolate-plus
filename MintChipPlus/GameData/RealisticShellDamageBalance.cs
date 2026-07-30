using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UadGameData = Il2Cpp.GameData;

namespace MintChipPlus.GameData;

// Balance intent: adjust the loaded gun damage curve once so vanilla battle
// code keeps reading normal GunData fields. This avoids per-shell hooks while
// making direct shell damage scale with shell volume instead of the vanilla
// small-gun and super-heavy-gun spikes.
internal static class RealisticShellDamageBalance
{
    private const float AnchorCaliberInches = 12f;
    private const float AnchorDamageMod = 4.4f;
    private const float ChangeEpsilon = 0.00001f;

    private static readonly Dictionary<string, float> OriginalDamageMods = new(StringComparer.Ordinal);
    private static readonly int[] SampleCalibers = { 1, 3, 6, 12, 18, 21 };

    private static ModSettings.RealisticShellDamageMode? lastAppliedMode;
    private static string? lastAppliedSummary;
    private static bool loggedMissingGameData;
    private static bool loggedDeferredInBattle;

    internal static bool IsBattleOrLoading()
        => GameManager.IsBattle || GameManager.IsLoadingBattle;

    internal static void ApplyCurrentSetting(
        string context = "manual",
        UadGameData? gameDataOverride = null,
        bool allowDuringBattleTransition = false)
    {
        if (!allowDuringBattleTransition && IsBattleOrLoading())
        {
            if (!loggedDeferredInBattle)
            {
                loggedDeferredInBattle = true;
                Melon<MintChipPlusMod>.Logger.Warning(
                    "UADMC realistic shell damage: live reapply deferred because a battle is loading or active.");
            }

            return;
        }

        UadGameData? gameData = gameDataOverride ?? G.GameData;
        if (gameData?.guns == null)
        {
            if (!loggedMissingGameData)
            {
                loggedMissingGameData = true;
                Melon<MintChipPlusMod>.Logger.Msg("UADMC realistic shell damage: option stored; gun data is not loaded yet.");
            }

            return;
        }

        loggedMissingGameData = false;
        loggedDeferredInBattle = false;

        CaptureOriginalDamageMods(gameData);

        ModSettings.RealisticShellDamageMode mode = ModSettings.RealisticShellDamage;
        int changed = 0;
        int seen = 0;
        Dictionary<int, string> samples = new();

        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, GunData> entry in gameData.guns)
        {
            string key = entry.Key ?? string.Empty;
            GunData gun = entry.Value;
            if (gun == null || string.IsNullOrWhiteSpace(key))
                continue;

            if (!OriginalDamageMods.TryGetValue(key, out float original))
                continue;

            seen++;
            float current = gun.damageMod;
            float target = mode == ModSettings.RealisticShellDamageMode.Realistic
                ? RealisticDamageMod(gun.caliberInch, original)
                : original;

            if (Math.Abs(current - target) > ChangeEpsilon)
                changed++;

            gun.damageMod = target;
            AddSample(samples, gun.caliberInch, original, target);
        }

        string sampleText = samples.Count > 0
            ? string.Join(", ", SampleCalibers.Where(samples.ContainsKey).Select(caliber => samples[caliber]))
            : "samples=none";
        string summary = $"mode={ModSettings.RealisticShellDamageModeText(mode)} changed={changed}/{seen} guns; {sampleText}";
        if (lastAppliedMode == mode && string.Equals(lastAppliedSummary, summary, StringComparison.Ordinal))
            return;

        lastAppliedMode = mode;
        lastAppliedSummary = summary;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC realistic shell damage: applied {ModSettings.RealisticShellDamageModeText(mode)} during {context}; {summary}.");
    }

    private static void CaptureOriginalDamageMods(UadGameData gameData)
    {
        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, GunData> entry in gameData.guns)
        {
            string key = entry.Key ?? string.Empty;
            GunData gun = entry.Value;
            if (gun == null || string.IsNullOrWhiteSpace(key) || OriginalDamageMods.ContainsKey(key))
                continue;

            OriginalDamageMods[key] = gun.damageMod;
        }
    }

    private static float RealisticDamageMod(float caliberInches, float fallback)
        => caliberInches > 0f
            ? AnchorDamageMod * MathF.Pow(caliberInches / AnchorCaliberInches, 3f)
            : fallback;

    private static void AddSample(Dictionary<int, string> samples, float caliberInches, float original, float target)
    {
        foreach (int sampleCaliber in SampleCalibers)
        {
            if (samples.ContainsKey(sampleCaliber) || Math.Abs(caliberInches - sampleCaliber) > 0.05f)
                continue;

            samples[sampleCaliber] = $"{sampleCaliber}in {Fmt(original)}->{Fmt(target)}";
            return;
        }
    }

    private static string Fmt(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);
}
