using MelonLoader;
using UnityEngine;

namespace UADVanillaPlus.GameData;

// VP philosophy: balance-affecting features should be controlled in-game, not
// through loose config files. Balance changes default to VP's improved behavior
// while letting players opt back into vanilla rules from the UAD:VP menu.
internal static class ModSettings
{
    private const string PortStrikeBalancedKey = "uadvp_port_strike_balanced";
    private const string AiFleetCompositionModeKey = "uadvp_ai_fleet_composition_mode";
    private const string AdvancedAiBuilderEnabledKey = "uadvp_advanced_ai_builder_enabled";
    private const string BattleWeatherAlwaysSunnyKey = "uadvp_battle_weather_always_sunny";
    private const string BattleSpottingRangeModeKey = "uadvp_battle_spotting_range_mode";
    private const string BattleDamageModeKey = "uadvp_battle_damage_mode";
    private const string RealisticShellDamageModeKey = "uadvp_realistic_shell_damage_mode";
    private const string DesignAccuracyPenaltyModeKey = "uadvp_design_accuracy_penalty_mode";
    private const string MajorShipTorpedoesRestrictedKey = "uadvp_major_ship_torpedoes_restricted";
    private const string ObsoleteDesignRetentionEnabledKey = "uadvp_obsolete_design_retention_enabled";
    private const string SuperstructureRefitsEnabledKey = "uadvp_superstructure_refits_enabled";
    private const string ShipyardCapacityBalancedKey = "uadvp_shipyard_capacity_balanced";
    private const string MineWarfareDisabledKey = "uadvp_mine_warfare_disabled";
    private const string SubmarineWarfareDisabledKey = "uadvp_submarine_warfare_disabled";
    private const string CampaignMapWraparoundEnabledKey = "uadvp_campaign_map_wraparound_enabled";
    private const string EarlyCanalOpeningsEnabledKey = "uadvp_early_canal_openings_enabled";
    private const string TechnologySpreadModeKey = "uadvp_technology_spread_mode";
    private const string CampaignEndDateEnabledKey = "uadvp_campaign_end_date_enabled";
    private const string ExperimentalNationShipPaintsEnabledKey = "uadvp_experimental_nation_ship_paints_enabled";
    private const string BattleRuntimeDiagnosticsEnabledKey = "uadvp_battle_runtime_diagnostics_enabled";
    private const string NationShipPaintStringKeyPrefix = "uadvp_nation_ship_paint_";
    private const string OldPanamaCanalEarlyEnabledKey = "uadvp_panama_canal_early_enabled";

    private static bool? portStrikeBalanced;
    private static AiFleetCompositionMode? aiFleetCompositionMode;
    private static bool? advancedAiBuilderEnabled;
    private static bool? battleWeatherAlwaysSunny;
    private static BattleSpottingRangeMode? battleSpottingRangeMode;
    private static BattleDamageMode? battleDamageMode;
    private static RealisticShellDamageMode? realisticShellDamageMode;
    private static AccuracyPenaltyMode? designAccuracyPenaltyMode;
    private static bool? majorShipTorpedoesRestricted;
    private static bool? obsoleteDesignRetentionEnabled;
    private static bool? superstructureRefitsEnabled;
    private static bool? shipyardCapacityBalanced;
    private static bool? mineWarfareDisabled;
    private static bool? submarineWarfareDisabled;
    private static bool? campaignMapWraparoundEnabled;
    private static bool? earlyCanalOpeningsEnabled;
    private static TechnologySpreadMode? technologySpreadMode;
    private static bool? campaignEndDateEnabled;
    private static bool? experimentalNationShipPaintsEnabled;
    private static bool? battleRuntimeDiagnosticsEnabled;
    private static int nationShipPaintsRevision;

    internal enum AccuracyPenaltyMode
    {
        Div10 = 10,
        Div5 = 5,
        Div2 = 2,
        Vanilla = 1,
    }

    internal enum BattleSpottingRangeMode
    {
        Vanilla = 1,
        X3 = 3,
        X5 = 5,
        X10 = 10,
    }

    internal enum BattleDamageMode
    {
        Vanilla = 1,
        X2 = 2,
        X3 = 3,
        X5 = 5,
    }

    internal enum RealisticShellDamageMode
    {
        Vanilla = 0,
        Realistic = 1,
    }

    internal enum AiFleetCompositionMode
    {
        Vanilla = 0,
        Balanced = 1,
        Heavy = 2,
    }

    internal enum AiArmsRaceMode
    {
        Disabled = 0,
        Loose35 = 35,
        Standard60 = 60,
        Strict75 = 75,
    }

    internal enum TechnologySpreadMode
    {
        Vanilla = 0,
        Gradual = 1,
        Swift = 2,
        Unrestricted = 3,
        Historical = 4,
    }

    internal static bool PortStrikeBalanced
    {
        get => portStrikeBalanced ??= PlayerPrefs.GetInt(PortStrikeBalancedKey, 1) != 0;
        set
        {
            portStrikeBalanced = value;
            PlayerPrefs.SetInt(PortStrikeBalancedKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Port Strike mode {(value ? "Balanced" : "Vanilla")}.");
            LogCurrentSettings("after Port Strike change");
        }
    }

    internal static AiFleetCompositionMode AiFleetComposition
    {
        get => aiFleetCompositionMode ??= LoadAiFleetCompositionMode();
        set
        {
            aiFleetCompositionMode = value;
            PlayerPrefs.SetInt(AiFleetCompositionModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: AI Fleet Mix mode {AiFleetCompositionModeText(value)}.");
            LogCurrentSettings("after AI Fleet Mix change");
        }
    }

    internal static AiArmsRaceMode AiArmsRace
    {
        get => AiArmsRaceMode.Disabled;
        set
        {
            if (value != AiArmsRaceMode.Disabled)
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP option: AI Arms Race is retired for now and remains Disabled.");
        }
    }

    internal static bool AiArmsRaceEnabled
    {
        get => false;
        set
        {
            if (value)
                Melon<UADVanillaPlusMod>.Logger.Msg("UADVP option: AI Arms Race is retired for now and remains Disabled.");
        }
    }

    internal static bool AdvancedAiBuilderEnabled
    {
        get => advancedAiBuilderEnabled ??= PlayerPrefs.GetInt(AdvancedAiBuilderEnabledKey, 1) != 0;
        set
        {
            advancedAiBuilderEnabled = value;
            PlayerPrefs.SetInt(AdvancedAiBuilderEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Advanced AI Builder mode {AdvancedAiBuilderModeText(value)}.");
            LogCurrentSettings("after Advanced AI Builder change");
        }
    }

    internal static bool BattleWeatherAlwaysSunny
    {
        get => battleWeatherAlwaysSunny ??= PlayerPrefs.GetInt(BattleWeatherAlwaysSunnyKey, 1) != 0;
        set
        {
            battleWeatherAlwaysSunny = value;
            PlayerPrefs.SetInt(BattleWeatherAlwaysSunnyKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle Weather mode {(value ? "Always Sunny" : "Vanilla")}.");
            LogCurrentSettings("after Battle Weather change");
        }
    }

    internal static AccuracyPenaltyMode DesignAccuracyPenaltyMode
    {
        get => designAccuracyPenaltyMode ??= LoadAccuracyPenaltyMode();
        set
        {
            if (AccuracyPenaltyBalance.IsBattleOrLoading())
            {
                Melon<UADVanillaPlusMod>.Logger.Warning("UADVP option: Crew & Accuracy Balance cannot be changed while a battle is loading or active.");
                return;
            }

            designAccuracyPenaltyMode = value;
            PlayerPrefs.SetInt(DesignAccuracyPenaltyModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Crew & Accuracy Balance mode {AccuracyPenaltyModeText(value)}.");
            LogCurrentSettings("after Crew & Accuracy Balance change");
            AccuracyPenaltyBalance.TryReapplyLoadedStats(value);
        }
    }

    internal static bool MajorShipTorpedoesRestricted
    {
        get => majorShipTorpedoesRestricted ??= PlayerPrefs.GetInt(MajorShipTorpedoesRestrictedKey, 1) != 0;
        set
        {
            majorShipTorpedoesRestricted = value;
            PlayerPrefs.SetInt(MajorShipTorpedoesRestrictedKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: CA+ Torpedoes mode {(value ? "Disallowed" : "Vanilla")}.");
            LogCurrentSettings("after CA+ Torpedoes change");
        }
    }

    internal static BattleSpottingRangeMode BattleSpottingRange
    {
        get => battleSpottingRangeMode ??= LoadBattleSpottingRangeMode();
        set
        {
            battleSpottingRangeMode = value;
            PlayerPrefs.SetInt(BattleSpottingRangeModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle Spotting mode {BattleSpottingRangeModeText(value)}.");
            LogCurrentSettings("after Battle Spotting change");
        }
    }

    internal static BattleDamageMode BattleDamage
    {
        get => battleDamageMode ??= LoadBattleDamageMode();
        set
        {
            battleDamageMode = value;
            PlayerPrefs.SetInt(BattleDamageModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle Damage mode {BattleDamageModeText(value)}.");
            LogCurrentSettings("after Battle Damage change");
            BattleDamageBalance.ApplyCurrentSetting("option change");
        }
    }

    internal static RealisticShellDamageMode RealisticShellDamage
    {
        get => realisticShellDamageMode ??= LoadRealisticShellDamageMode();
        set
        {
            realisticShellDamageMode = value;
            PlayerPrefs.SetInt(RealisticShellDamageModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Realistic Shell Damage mode {RealisticShellDamageModeText(value)}.");
            LogCurrentSettings("after Realistic Shell Damage change");
            RealisticShellDamageBalance.ApplyCurrentSetting("option change");
        }
    }

    internal static bool ObsoleteDesignRetentionEnabled
    {
        get => obsoleteDesignRetentionEnabled ??= PlayerPrefs.GetInt(ObsoleteDesignRetentionEnabledKey, 0) != 0;
        set
        {
            obsoleteDesignRetentionEnabled = value;
            PlayerPrefs.SetInt(ObsoleteDesignRetentionEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Obsolete Tech & Hulls mode {(value ? "Retain" : "Vanilla")}.");
            LogCurrentSettings("after Obsolete Tech & Hulls change");
        }
    }

    internal static bool SuperstructureRefitsEnabled
    {
        get => superstructureRefitsEnabled ??= PlayerPrefs.GetInt(SuperstructureRefitsEnabledKey, 0) != 0;
        set
        {
            superstructureRefitsEnabled = value;
            PlayerPrefs.SetInt(SuperstructureRefitsEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Superstructure Compatibility mode {SuperstructureRefitsModeText(value)}.");
            LogCurrentSettings("after Superstructure Compatibility change");
        }
    }

    internal static bool ShipyardCapacityBalanced
    {
        get => shipyardCapacityBalanced ??= PlayerPrefs.GetInt(ShipyardCapacityBalancedKey, 1) != 0;
        set
        {
            shipyardCapacityBalanced = value;
            PlayerPrefs.SetInt(ShipyardCapacityBalancedKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Suspend Dock Overcapacity mode {(value ? "Automatic" : "Manual")}.");
            LogCurrentSettings("after Suspend Dock Overcapacity change");
        }
    }

    internal static bool MineWarfareDisabled
    {
        get => mineWarfareDisabled ??= PlayerPrefs.GetInt(MineWarfareDisabledKey, 0) != 0;
        set
        {
            mineWarfareDisabled = value;
            PlayerPrefs.SetInt(MineWarfareDisabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Mine Warfare mode {(value ? "Disabled" : "Enabled")}.");
            LogCurrentSettings("after Mine Warfare change");
        }
    }

    internal static bool SubmarineWarfareDisabled
    {
        get => submarineWarfareDisabled ??= PlayerPrefs.GetInt(SubmarineWarfareDisabledKey, 0) != 0;
        set
        {
            submarineWarfareDisabled = value;
            PlayerPrefs.SetInt(SubmarineWarfareDisabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Submarine Warfare mode {(value ? "Disabled" : "Enabled")}.");
            LogCurrentSettings("after Submarine Warfare change");
        }
    }

    internal static bool CampaignMapWraparoundEnabled
    {
        get => campaignMapWraparoundEnabled ??= PlayerPrefs.GetInt(CampaignMapWraparoundEnabledKey, 0) != 0;
        set
        {
            campaignMapWraparoundEnabled = value;
            PlayerPrefs.SetInt(CampaignMapWraparoundEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Map Geometry {(value ? "Disc World" : "Flat Earth")}.");
            LogCurrentSettings("after Map Geometry change");
        }
    }

    internal static bool EarlyCanalOpeningsEnabled
    {
        get => earlyCanalOpeningsEnabled ??= LoadEarlyCanalOpeningsEnabled();
        set
        {
            earlyCanalOpeningsEnabled = value;
            PlayerPrefs.SetInt(EarlyCanalOpeningsEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Canal Openings mode {(value ? "Early" : "Historical")}.");
            LogCurrentSettings("after Canal Openings change");
        }
    }

    internal static TechnologySpreadMode TechnologySpread
    {
        get => technologySpreadMode ??= LoadTechnologySpreadMode();
        set
        {
            technologySpreadMode = value;
            PlayerPrefs.SetInt(TechnologySpreadModeKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Technology Spread mode {TechnologySpreadModeText(value)}.");
            LogCurrentSettings("after Technology Spread change");
        }
    }

    internal static bool CampaignEndDateEnabled
    {
        get => campaignEndDateEnabled ??= PlayerPrefs.GetInt(CampaignEndDateEnabledKey, 1) != 0;
        set
        {
            campaignEndDateEnabled = value;
            PlayerPrefs.SetInt(CampaignEndDateEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Campaign End Date mode {CampaignEndDateModeText(value)}.");
            LogCurrentSettings("after Campaign End Date change");
        }
    }

    internal static bool ExperimentalNationShipPaintsEnabled
    {
        get => experimentalNationShipPaintsEnabled ??= PlayerPrefs.GetInt(ExperimentalNationShipPaintsEnabledKey, 0) != 0;
        set
        {
            experimentalNationShipPaintsEnabled = value;
            PlayerPrefs.SetInt(ExperimentalNationShipPaintsEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Experimental Nation Ship Paints mode {ExperimentalNationShipPaintsModeText(value)}.");
            LogCurrentSettings("after Experimental Nation Ship Paints change");
        }
    }

    // TODO release-disable: this temporary investigation diagnostic defaults on
    // so current balance test builds emit battle-exit runtime summaries.
    internal static bool BattleRuntimeDiagnosticsEnabled
    {
        get => battleRuntimeDiagnosticsEnabled ??= PlayerPrefs.GetInt(BattleRuntimeDiagnosticsEnabledKey, 1) != 0;
        set
        {
            battleRuntimeDiagnosticsEnabled = value;
            PlayerPrefs.SetInt(BattleRuntimeDiagnosticsEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle Runtime Diagnostics mode {BattleRuntimeDiagnosticsModeText(value)}.");
            LogCurrentSettings("after Battle Runtime Diagnostics change");
        }
    }

    internal static int NationShipPaintsRevision => nationShipPaintsRevision;

    internal static string NationShipPaintString(string nationKey)
        => PlayerPrefs.GetString(NationShipPaintPreferenceKey(nationKey), string.Empty);

    internal static bool SetNationShipPaintString(string nationKey, string value)
        => SetNationShipPaintString(nationKey, value, logChange: true);

    internal static bool SetNationShipPaintString(string nationKey, string value, bool logChange)
    {
        string preferenceKey = NationShipPaintPreferenceKey(nationKey);
        string storedValue = value ?? string.Empty;
        string currentValue = PlayerPrefs.GetString(preferenceKey, string.Empty);
        if (string.Equals(currentValue, storedValue, StringComparison.Ordinal))
            return false;

        PlayerPrefs.SetString(preferenceKey, storedValue);
        PlayerPrefs.Save();
        nationShipPaintsRevision++;
        if (logChange)
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP option: Nation Ship Paints updated {NormalizeNationPaintKey(nationKey)} paint string.");
        return true;
    }

    internal static bool DesignAccuracyPenaltiesBalanced
        => DesignAccuracyPenaltyMode != AccuracyPenaltyMode.Vanilla;

    internal static float AccuracyPenaltyDivisor(AccuracyPenaltyMode mode)
        => mode == AccuracyPenaltyMode.Vanilla ? 1f : (float)mode;

    internal static float BattleSpottingRangeMultiplier(BattleSpottingRangeMode mode)
        => mode == BattleSpottingRangeMode.Vanilla ? 1f : (float)mode;

    internal static float BattleDamageMultiplier(BattleDamageMode mode)
        => mode == BattleDamageMode.Vanilla ? 1f : (float)mode;

    internal static bool RealisticShellDamageEnabled
        => RealisticShellDamage == RealisticShellDamageMode.Realistic;

    internal static string AccuracyPenaltyModeText(AccuracyPenaltyMode mode)
        => mode == AccuracyPenaltyMode.Vanilla ? "Vanilla" : $"/{(int)mode}";

    internal static string BattleSpottingRangeModeText(BattleSpottingRangeMode mode)
        => mode == BattleSpottingRangeMode.Vanilla ? "Vanilla" : $"{(int)mode}x";

    internal static string BattleDamageModeText(BattleDamageMode mode)
        => mode == BattleDamageMode.Vanilla ? "Unchanged" : $"{(int)mode}x";

    internal static string RealisticShellDamageModeText(RealisticShellDamageMode mode)
        => mode == RealisticShellDamageMode.Realistic ? "Realistic" : "Vanilla";

    internal static string AiFleetCompositionModeText(AiFleetCompositionMode mode)
        => mode switch
        {
            AiFleetCompositionMode.Balanced => "Balanced",
            AiFleetCompositionMode.Heavy => "Heavy",
            _ => "Vanilla",
        };

    internal static float AiArmsRaceMinimumCompetitiveRatio
        => 0f;

    internal static string AiArmsRaceModeText(AiArmsRaceMode mode)
        => mode switch
        {
            AiArmsRaceMode.Loose35 => "35%",
            AiArmsRaceMode.Standard60 => "60%",
            AiArmsRaceMode.Strict75 => "75%",
            _ => "Disabled",
        };

    internal static string AiArmsRaceModeText(bool enabled)
        => AiArmsRaceModeText(enabled ? AiArmsRaceMode.Standard60 : AiArmsRaceMode.Disabled);

    internal static string AdvancedAiBuilderModeText(bool enabled)
        => enabled ? "Enhanced" : "Vanilla";

    internal static void LogCurrentSettings(string context)
    {
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP settings ({context}): {CurrentSettingsText()}.");
    }

    private static string CurrentSettingsText()
        => $"Battle Weather={BattleWeatherModeText(BattleWeatherAlwaysSunny)}; " +
           $"Battle Spotting={BattleSpottingRangeModeText(BattleSpottingRange)}; " +
           $"Battle Damage={BattleDamageModeText(BattleDamage)}; " +
           $"Realistic Shell Damage={RealisticShellDamageModeText(RealisticShellDamage)}; " +
           $"Crew & Accuracy Balance={AccuracyPenaltyModeText(DesignAccuracyPenaltyMode)}; " +
           $"Port Strike={PortStrikeModeText(PortStrikeBalanced)}; " +
           $"AI Fleet Mix={AiFleetCompositionModeText(AiFleetComposition)}; " +
           $"Advanced AI Builder={AdvancedAiBuilderModeText(AdvancedAiBuilderEnabled)}; " +
           $"Shared Designs={CampaignSharedDesignUsageSettings.CurrentModeText()}; " +
           $"Suspend Dock Overcapacity={ShipyardCapacityModeText(ShipyardCapacityBalanced)}; " +
           $"Canal Openings={CanalOpeningModeText(EarlyCanalOpeningsEnabled)}; " +
           $"Technology Spread={TechnologySpreadModeText(TechnologySpread)}; " +
           $"Campaign End Date={CampaignEndDateModeText(CampaignEndDateEnabled)}; " +
           $"Mine Warfare={MineWarfareModeText(MineWarfareDisabled)}; " +
           $"Submarine Warfare={SubmarineWarfareModeText(SubmarineWarfareDisabled)}; " +
           $"CA+ Torpedoes={MajorShipTorpedoesModeText(MajorShipTorpedoesRestricted)}; " +
           $"Obsolete Tech & Hulls={ObsoleteDesignRetentionModeText(ObsoleteDesignRetentionEnabled)}; " +
           $"Superstructure Compatibility={SuperstructureRefitsModeText(SuperstructureRefitsEnabled)}; " +
           $"Map Geometry={CampaignMapModeText(CampaignMapWraparoundEnabled)}; " +
           $"Experimental Nation Ship Paints={ExperimentalNationShipPaintsModeText(ExperimentalNationShipPaintsEnabled)}; " +
           $"Battle Runtime Diagnostics={BattleRuntimeDiagnosticsModeText(BattleRuntimeDiagnosticsEnabled)}";

    internal static string BattleWeatherModeText(bool alwaysSunny)
        => alwaysSunny ? "Always Sunny" : "Vanilla";

    internal static string PortStrikeModeText(bool balanced)
        => balanced ? "Balanced" : "Vanilla";

    internal static string ShipyardCapacityModeText(bool balanced)
        => balanced ? "Automatic" : "Manual";

    internal static string CanalOpeningModeText(bool early)
        => early ? "Early" : "Historical";

    internal static string MineWarfareModeText(bool disabled)
        => disabled ? "Disabled" : "Enabled";

    internal static string SubmarineWarfareModeText(bool disabled)
        => disabled ? "Disabled" : "Enabled";

    internal static string MajorShipTorpedoesModeText(bool restricted)
        => restricted ? "Disallowed" : "Vanilla";

    internal static string ObsoleteDesignRetentionModeText(bool enabled)
        => enabled ? "Retain" : "Vanilla";

    internal static string SuperstructureRefitsModeText(bool enabled)
        => enabled ? "Unrestricted" : "Vanilla";

    internal static string CampaignMapModeText(bool enabled)
        => enabled ? "Disc World" : "Flat Earth";

    internal static string ExperimentalNationShipPaintsModeText(bool enabled)
        => enabled ? "On" : "Off";

    internal static string BattleRuntimeDiagnosticsModeText(bool enabled)
        => enabled ? "On" : "Off";

    internal static string TechnologySpreadModeText(TechnologySpreadMode mode)
        => mode switch
        {
            TechnologySpreadMode.Gradual => "Gradual",
            TechnologySpreadMode.Swift => "Swift",
            TechnologySpreadMode.Unrestricted => "Unrestricted",
            TechnologySpreadMode.Historical => "Historical",
            _ => "Vanilla",
        };

    internal static string CampaignEndDateModeText(bool enabled)
        => enabled ? "Enabled" : "Disabled";

    private static AccuracyPenaltyMode LoadAccuracyPenaltyMode()
    {
        int stored = PlayerPrefs.GetInt(DesignAccuracyPenaltyModeKey, (int)AccuracyPenaltyMode.Div5);
        return Enum.IsDefined(typeof(AccuracyPenaltyMode), stored) ? (AccuracyPenaltyMode)stored : AccuracyPenaltyMode.Div5;
    }

    private static BattleSpottingRangeMode LoadBattleSpottingRangeMode()
    {
        int stored = PlayerPrefs.GetInt(BattleSpottingRangeModeKey, (int)BattleSpottingRangeMode.X3);
        return Enum.IsDefined(typeof(BattleSpottingRangeMode), stored)
            ? (BattleSpottingRangeMode)stored
            : BattleSpottingRangeMode.X3;
    }

    private static BattleDamageMode LoadBattleDamageMode()
    {
        int stored = PlayerPrefs.GetInt(BattleDamageModeKey, (int)BattleDamageMode.X3);
        return Enum.IsDefined(typeof(BattleDamageMode), stored)
            ? (BattleDamageMode)stored
            : BattleDamageMode.X3;
    }

    private static RealisticShellDamageMode LoadRealisticShellDamageMode()
    {
        int stored = PlayerPrefs.GetInt(RealisticShellDamageModeKey, (int)RealisticShellDamageMode.Realistic);
        return Enum.IsDefined(typeof(RealisticShellDamageMode), stored)
            ? (RealisticShellDamageMode)stored
            : RealisticShellDamageMode.Realistic;
    }

    private static AiFleetCompositionMode LoadAiFleetCompositionMode()
    {
        int stored = PlayerPrefs.GetInt(AiFleetCompositionModeKey, (int)AiFleetCompositionMode.Heavy);
        return Enum.IsDefined(typeof(AiFleetCompositionMode), stored)
            ? (AiFleetCompositionMode)stored
            : AiFleetCompositionMode.Heavy;
    }

    private static bool LoadEarlyCanalOpeningsEnabled()
    {
        if (PlayerPrefs.HasKey(EarlyCanalOpeningsEnabledKey))
            return PlayerPrefs.GetInt(EarlyCanalOpeningsEnabledKey, 0) != 0;

        return PlayerPrefs.GetInt(OldPanamaCanalEarlyEnabledKey, 0) != 0;
    }

    private static TechnologySpreadMode LoadTechnologySpreadMode()
    {
        int stored = PlayerPrefs.GetInt(TechnologySpreadModeKey, (int)TechnologySpreadMode.Vanilla);
        return Enum.IsDefined(typeof(TechnologySpreadMode), stored)
            ? (TechnologySpreadMode)stored
            : TechnologySpreadMode.Vanilla;
    }

    private static string NationShipPaintPreferenceKey(string nationKey)
        => NationShipPaintStringKeyPrefix + NormalizeNationPaintKey(nationKey);

    private static string NormalizeNationPaintKey(string nationKey)
    {
        if (string.IsNullOrWhiteSpace(nationKey))
            return "unknown";

        string trimmed = nationKey.Trim().ToLowerInvariant();
        char[] chars = new char[trimmed.Length];
        int count = 0;
        bool lastWasUnderscore = false;
        foreach (char c in trimmed)
        {
            bool isAllowed = char.IsLetterOrDigit(c);
            if (isAllowed)
            {
                chars[count++] = c;
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                chars[count++] = '_';
                lastWasUnderscore = true;
            }
        }

        string normalized = new(chars, 0, count);
        return normalized.Trim('_').Length == 0 ? "unknown" : normalized.Trim('_');
    }
}
