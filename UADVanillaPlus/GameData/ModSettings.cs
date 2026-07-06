using System;
using System.Collections.Generic;
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
    private const string BattleReverseMethodKey = "uadvp_battle_reverse_method";
    private const string FollowSteerDampingEnabledKey = "uadvp_follow_steer_damping_enabled";
    private const string ParallelStationAbreastKey = "uadvp_parallel_station_abreast";
    private const string AiEconomyPrioritiesKey = "uadvp_ai_economy_priorities";
    private const string ShipResupplyOverrideKey = "uadvp_ship_resupply_override";
    private const string ShipServiceRecordsKey = "uadvp_ship_service_records";
    private const string SuperstructureRefitsEnabledKey = "uadvp_superstructure_refits_enabled";
    private const string ShipyardCapacityBalancedKey = "uadvp_shipyard_capacity_balanced";
    private const string MultiYearShipyardRebuildKey = "uadvp_multiyear_shipyard_rebuild";
    private const string VanquishedSpoilsKey = "uadvp_vanquished_spoils";
    private const string RebuildOverseasWeightKey = "uadvp_rebuild_overseas_weight";
    private const string VanquishedSpoilsShareKey = "uadvp_vanquished_spoils_share";
    private const string NavalReinforcementKey = "uadvp_naval_reinforcement";
    private const string ClassNamingThemesKey = "uadvp_class_naming_themes";
    private const string MineWarfareDisabledKey = "uadvp_mine_warfare_disabled";
    private const string SubmarineWarfareDisabledKey = "uadvp_submarine_warfare_disabled";
    private const string CampaignMapWraparoundEnabledKey = "uadvp_campaign_map_wraparound_enabled";
    private const string EarlyCanalOpeningsEnabledKey = "uadvp_early_canal_openings_enabled";
    private const string TechnologySpreadModeKey = "uadvp_technology_spread_mode";
    private const string CampaignEndDateEnabledKey = "uadvp_campaign_end_date_enabled";
    private const string ExperimentalNationShipPaintsEnabledKey = "uadvp_experimental_nation_ship_paints_enabled";
    private const string BattleRuntimeDiagnosticsEnabledKey = "uadvp_battle_runtime_diagnostics_enabled";
    private const string NationShipPaintStringKeyPrefix = "uadvp_nation_ship_paint_";
    private const string DesignShipPaintStringKeyPrefix = "uadvp_design_ship_paint_";
    private const string UserPaintPresetsKey = "uadvp_user_paint_presets";
    // Cap chosen so the user-presets row fits in the 260-wide picker window:
    //   6 swatches × 24 + 6 gaps × 4 + "+ Save" button (60) = 228 ≤ 232 usable.
    private const int MaxUserPaintPresets = 6;
    private const string OldPanamaCanalEarlyEnabledKey = "uadvp_panama_canal_early_enabled";

    private static bool? portStrikeBalanced;
    private static AiFleetCompositionMode? aiFleetCompositionMode;
    private static bool? advancedAiBuilderEnabled;
    private static bool? battleWeatherAlwaysSunny;
    private static BattleSpottingRangeMode? battleSpottingRangeMode;
    private static BattleDamageMode? battleDamageMode;
    private static RealisticShellDamageMode? realisticShellDamageMode;
    private static AccuracyPenaltyMode? designAccuracyPenaltyMode;
    private static BattleTurnMethod? battleReverseMethod;
    private static bool? followSteerDampingEnabled;
    private static bool? parallelStationAbreast;
    private static bool? aiEconomyPrioritiesEnabled;
    private static bool? shipResupplyOverrideEnabled;
    private static bool? shipServiceRecordsEnabled;
    private static bool? majorShipTorpedoesRestricted;
    private static bool? superstructureRefitsEnabled;
    private static bool? shipyardCapacityBalanced;
    private static bool? multiYearShipyardRebuild;
    private static bool? vanquishedSpoils;
    private static bool? classNamingThemes;
    private static LevelSetting? rebuildOverseasWeightLevel;
    private static LevelSetting? vanquishedSpoilsShareLevel;
    private static NavalReinforcementMode? navalReinforcement;
    private static bool? mineWarfareDisabled;
    private static bool? submarineWarfareDisabled;
    internal enum MapGeometryMode { Flat = 0, Disc = 1, Globe = 2 }
    private const string MapGeometryKey = "uadvp_map_geometry_mode";
    private static MapGeometryMode? mapGeometry;
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

    // Strategy the R/T battle reverse-course hotkeys use to turn a division 180.
    internal enum BattleTurnMethod
    {
        Single180 = 1,         // immediate reorder + single MoveDir(~179 to the chosen side)
        NinetySwapNinety = 2,  // MoveDir(90); swap column once the turn is initiated; finish MoveDir(180)
        SplitRejoin = 3,       // split each ship to its own division, turn together, rejoin reversed once initiated
        Rudder = 4,            // direct hard-over rudder per ship (experimental)
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

    // Shared Low/Medium/High level for VP weighting options.
    internal enum LevelSetting
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    internal enum AiArmsRaceMode
    {
        Disabled = 0,
        Loose35 = 35,
        Standard60 = 60,
        Strict75 = 75,
    }

    // "Reinforce with Navy": how much army force naval tonnage in a land battle's target waters adds.
    // Off disables the feature; the others scale the per-ton force and the maximum boost cap.
    internal enum NavalReinforcementMode
    {
        Off = 0,
        Modest = 1,   // gentle nudge, caps around +50%
        Strong = 2,   // a full naval commitment can roughly double the attack (~+100%)
        Decisive = 3, // bring the fleet and you basically win (~+250%)
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

    internal static BattleTurnMethod BattleReverseMethod
    {
        get => battleReverseMethod ??= (BattleTurnMethod)PlayerPrefs.GetInt(BattleReverseMethodKey, (int)BattleTurnMethod.NinetySwapNinety);
        set
        {
            battleReverseMethod = value;
            PlayerPrefs.SetInt(BattleReverseMethodKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle Reverse-Course method {BattleTurnMethodText(value)}.");
        }
    }

    internal static string BattleTurnMethodText(BattleTurnMethod mode) => mode switch
    {
        BattleTurnMethod.Single180 => "Single 180",
        BattleTurnMethod.NinetySwapNinety => "90 / swap / 90",
        BattleTurnMethod.SplitRejoin => "Split & rejoin",
        BattleTurnMethod.Rudder => "Rudder",
        _ => mode.ToString(),
    };

    // Experimental: damp the per-frame yaw rate of division FOLLOWERS to kill the S-pattern weave
    // that fast / slow-rudder ships show while station-keeping. Off = vanilla follow steering.
    internal static bool FollowSteerDampingEnabled
    {
        get => followSteerDampingEnabled ??= PlayerPrefs.GetInt(FollowSteerDampingEnabledKey, 0) != 0;
        set
        {
            followSteerDampingEnabled = value;
            PlayerPrefs.SetInt(FollowSteerDampingEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Follow Steering Damping {(value ? "On" : "Off")}.");
        }
    }

    // Offset preset for the station-keeping "Parallel" order: false = Astern (behind + disengaged
    // side, a trailing screen), true = Abreast (beside the anchor on the beam, for parallel battle
    // lines). (True within-division abreast formation isn't achievable — the native follow-steer
    // bypasses managed patches — so abreast is delivered as this cross-division station-keep offset.)
    internal static bool ParallelStationAbreast
    {
        get => parallelStationAbreast ??= PlayerPrefs.GetInt(ParallelStationAbreastKey, 0) != 0;
        set
        {
            parallelStationAbreast = value;
            PlayerPrefs.SetInt(ParallelStationAbreastKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Parallel Station {(value ? "Abreast" : "Astern")}.");
        }
    }

    // When on, AI majors fund their economy by the priority ladder: transport capacity up to 200%,
    // then technology, then crew training (reallocating their own naval budget). Off = vanilla.
    internal static bool AiEconomyPrioritiesEnabled
    {
        get => aiEconomyPrioritiesEnabled ??= PlayerPrefs.GetInt(AiEconomyPrioritiesKey, 1) != 0;
        set
        {
            aiEconomyPrioritiesEnabled = value;
            PlayerPrefs.SetInt(AiEconomyPrioritiesKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: AI Economy Priorities {(value ? "On" : "Vanilla")}.");
        }
    }

    // Records each ship's battle history (damage dealt/received, kills, wrecks, survived) per campaign.
    internal static bool ShipServiceRecordsEnabled
    {
        get => shipServiceRecordsEnabled ??= PlayerPrefs.GetInt(ShipServiceRecordsKey, 1) != 0;
        set
        {
            shipServiceRecordsEnabled = value;
            PlayerPrefs.SetInt(ShipServiceRecordsKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Ship Service Records {(value ? "On" : "Off")}.");
        }
    }

    // Debug override: enables manual refuel/rearm of the player's ships. Off (vanilla) by default.
    internal static bool ShipResupplyOverrideEnabled
    {
        get => shipResupplyOverrideEnabled ??= PlayerPrefs.GetInt(ShipResupplyOverrideKey, 0) != 0;
        set
        {
            shipResupplyOverrideEnabled = value;
            PlayerPrefs.SetInt(ShipResupplyOverrideKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Ship Resupply Override {(value ? "On" : "Off")}.");
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

    // Balance: tie national shipyard (build) capacity to territory — on conquest the
    // loser instantly loses a captured province's proportional share of its shipyard
    // and the captor rebuilds it over a development-scaled number of years. Default on.
    internal static bool MultiYearShipyardRebuildEnabled
    {
        get => multiYearShipyardRebuild ??= PlayerPrefs.GetInt(MultiYearShipyardRebuildKey, 1) != 0;
        set
        {
            multiYearShipyardRebuild = value;
            PlayerPrefs.SetInt(MultiYearShipyardRebuildKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Multi-year shipyard rebuild {(value ? "On" : "Vanilla")}.");
            LogCurrentSettings("after Multi-year shipyard rebuild change");
        }
    }

    // Balance: on full conquest of a major, distribute its fleet + a cash indemnity to
    // the victors (instead of vanilla scrapping the fleet and stranding the treasury).
    // Default on.
    internal static bool VanquishedSpoilsEnabled
    {
        get => vanquishedSpoils ??= PlayerPrefs.GetInt(VanquishedSpoilsKey, 1) != 0;
        set
        {
            vanquishedSpoils = value;
            PlayerPrefs.SetInt(VanquishedSpoilsKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Vanquished spoils {(value ? "On" : "Vanilla")}.");
            LogCurrentSettings("after Vanquished spoils change");
        }
    }

    // Weighting: how much overseas/colonial territory counts toward shipbuilding
    // capacity relative to home territory (1.0). Low=0.1, Medium=0.25, High=0.5.
    internal static LevelSetting RebuildOverseasWeightLevel
    {
        get => rebuildOverseasWeightLevel ??= LoadLevel(RebuildOverseasWeightKey, LevelSetting.Medium);
        set
        {
            rebuildOverseasWeightLevel = value;
            PlayerPrefs.SetInt(RebuildOverseasWeightKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Overseas capacity weight {LevelText(value)}.");
            LogCurrentSettings("after Overseas capacity weight change");
        }
    }

    internal static double RebuildOverseasWeight => RebuildOverseasWeightLevel switch
    {
        LevelSetting.Low => 0.1,
        LevelSetting.High => 0.5,
        _ => 0.25,
    };

    // Weighting: how generous vanquished spoils are — Low keeps less (more scuttled,
    // smaller indemnity), High keeps more.
    internal static LevelSetting VanquishedSpoilsShareLevel
    {
        get => vanquishedSpoilsShareLevel ??= LoadLevel(VanquishedSpoilsShareKey, LevelSetting.Medium);
        set
        {
            vanquishedSpoilsShareLevel = value;
            PlayerPrefs.SetInt(VanquishedSpoilsShareKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Vanquished spoils share {LevelText(value)}.");
            LogCurrentSettings("after Vanquished spoils share change");
        }
    }

    internal static double VanquishedScuttleFraction => VanquishedSpoilsShareLevel switch
    {
        LevelSetting.Low => 0.6,
        LevelSetting.High => 0.2,
        _ => 0.4,
    };

    internal static double VanquishedCashSeizeFraction => VanquishedSpoilsShareLevel switch
    {
        LevelSetting.Low => 0.25,
        LevelSetting.High => 0.75,
        _ => 0.5,
    };

    // "Reinforce with Navy" strength. Off disables it. Default Strong.
    internal static NavalReinforcementMode NavalReinforcement
    {
        get
        {
            if (navalReinforcement == null)
            {
                int stored = PlayerPrefs.GetInt(NavalReinforcementKey, (int)NavalReinforcementMode.Strong);
                navalReinforcement = Enum.IsDefined(typeof(NavalReinforcementMode), stored)
                    ? (NavalReinforcementMode)stored : NavalReinforcementMode.Strong;
            }
            return navalReinforcement.Value;
        }
        set
        {
            navalReinforcement = value;
            PlayerPrefs.SetInt(NavalReinforcementKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Naval Reinforcement {value}.");
        }
    }

    internal static bool NavalReinforcementEnabled => NavalReinforcement != NavalReinforcementMode.Off;

    // Projected force (ArmyForceForProvince units) added per ton of shipping parked at the coast — no cap. A
    // strong province defends with ~a few hundred thousand projected force, so ~1/ton means a full-fleet
    // commitment (hundreds of thousands of tons) can overcome it; a small fleet only tips a weak province.
    internal static float NavalReinforcementForcePerTon => NavalReinforcement switch
    {
        NavalReinforcementMode.Modest => 0.5f,
        NavalReinforcementMode.Decisive => 2.0f,
        _ => 1.0f, // Strong
    };

    // QoL: let the player assign a naming theme per ship class; new ships of that class
    // draw from the theme pool instead of the generic per-nation list. Default on (no
    // effect until a class is given a theme in the constructor).
    internal static bool ClassNamingThemesEnabled
    {
        get => classNamingThemes ??= PlayerPrefs.GetInt(ClassNamingThemesKey, 1) != 0;
        set
        {
            classNamingThemes = value;
            PlayerPrefs.SetInt(ClassNamingThemesKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Class naming themes {(value ? "On" : "Off")}.");
            LogCurrentSettings("after Class naming themes change");
        }
    }

    // Balance: raise total shipbuilding capacity for ALL players. Vanilla's home-port-derived
    // limit is restrictively low (a single 40k-ton design eats a third of a ~120k cap), so
    // multiply Player.ShipbuildingCapacityLimit. Default 2x; tunable or vanilla in options.
    private const string ShipbuildingCapacityBoostKey = "uadvp_shipbuilding_capacity_boost";
    private static ShipbuildingCapacityBoostMode? shipbuildingCapacityBoost;

    internal enum ShipbuildingCapacityBoostMode
    {
        Vanilla = 100,
        Plus50 = 150,
        Double = 200,
        Triple = 300,
    }

    internal static ShipbuildingCapacityBoostMode ShipbuildingCapacityBoost
    {
        get => shipbuildingCapacityBoost ??= LoadShipbuildingCapacityBoost();
        set
        {
            shipbuildingCapacityBoost = value;
            PlayerPrefs.SetInt(ShipbuildingCapacityBoostKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Shipbuilding Capacity {ShipbuildingCapacityBoostText(value)}.");
            LogCurrentSettings("after Shipbuilding Capacity change");
        }
    }

    internal static float ShipbuildingCapacityBoostMultiplier => (int)ShipbuildingCapacityBoost / 100f;

    internal static string ShipbuildingCapacityBoostText(ShipbuildingCapacityBoostMode mode)
        => mode == ShipbuildingCapacityBoostMode.Vanilla ? "Vanilla" : $"{(int)mode / 100f:0.##}x";

    private static ShipbuildingCapacityBoostMode LoadShipbuildingCapacityBoost()
    {
        int stored = PlayerPrefs.GetInt(ShipbuildingCapacityBoostKey, (int)ShipbuildingCapacityBoostMode.Double);
        return Enum.IsDefined(typeof(ShipbuildingCapacityBoostMode), stored)
            ? (ShipbuildingCapacityBoostMode)stored
            : ShipbuildingCapacityBoostMode.Double;
    }

    // Balance: at campaign battle end the VP-winner takes all surrendered ships (captures
    // the loser's, recovers its own). Default on; vanilla leaves surrendered ships as losses.
    private const string SurrenderedShipCaptureKey = "uadvp_surrendered_ship_capture";
    private static bool? surrenderedShipCapture;

    internal static bool SurrenderedShipCaptureEnabled
    {
        get => surrenderedShipCapture ??= PlayerPrefs.GetInt(SurrenderedShipCaptureKey, 1) != 0;
        set
        {
            surrenderedShipCapture = value;
            PlayerPrefs.SetInt(SurrenderedShipCaptureKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Surrendered ship capture {(value ? "On" : "Vanilla")}.");
            LogCurrentSettings("after Surrendered ship capture change");
        }
    }

    // QoL: auto-apply the player's preferred PER-SHIP-TYPE settings at battle start so they
    // don't redo them every fight (BBs want different ammo/behavior than DDs/TBs). The master
    // toggle lives here; the per-type values live in GameData/BattleStartDefaults. Leave = don't
    // touch that setting (vanilla).
    internal enum BattleAmmoMode { Leave = 0, Auto = 1, AP = 2, HE = 3 }
    internal enum BattleToggle { Leave = 0, On = 1, Off = 2 }
    internal enum BattleFormation { Leave = 0, Column = 1, Line = 2 }

    private const string BattleStartEnabledKey = "uadvp_battle_start_defaults";
    private static bool? battleStartDefaults;

    internal static bool BattleStartDefaultsEnabled
    {
        get => battleStartDefaults ??= PlayerPrefs.GetInt(BattleStartEnabledKey, 0) != 0;
        set
        {
            battleStartDefaults = value;
            PlayerPrefs.SetInt(BattleStartEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Battle start defaults {(value ? "On" : "Off")}.");
        }
    }

    private const string BattleSpeedSyncKey = "uadvp_battle_speed_sync";
    private static bool? battleSpeedSync;

    internal static bool BattleSpeedSyncEnabled
    {
        get => battleSpeedSync ??= PlayerPrefs.GetInt(BattleSpeedSyncKey, 1) != 0; // default On
        set
        {
            battleSpeedSync = value;
            PlayerPrefs.SetInt(BattleSpeedSyncKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Division speed-sync {(value ? "On" : "Off")}.");
        }
    }

    internal static string LevelText(LevelSetting level) => level switch
    {
        LevelSetting.Low => "Low",
        LevelSetting.High => "High",
        _ => "Medium",
    };

    private static LevelSetting LoadLevel(string key, LevelSetting fallback)
    {
        int stored = PlayerPrefs.GetInt(key, (int)fallback);
        return Enum.IsDefined(typeof(LevelSetting), stored) ? (LevelSetting)stored : fallback;
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

    // Tri-state campaign map geometry: Flat (vanilla), Disc (Pacific-seam wrap), Globe (3D sphere skin).
    internal static MapGeometryMode MapGeometry
    {
        get => mapGeometry ??= LoadMapGeometry();
        set
        {
            mapGeometry = value;
            PlayerPrefs.SetInt(MapGeometryKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Map Geometry {value}.");
            LogCurrentSettings("after Map Geometry change");
        }
    }

    private static MapGeometryMode LoadMapGeometry()
    {
        if (PlayerPrefs.HasKey(MapGeometryKey))
            return (MapGeometryMode)PlayerPrefs.GetInt(MapGeometryKey);
        // Migrate the previous Disc/Flat bool key.
        return PlayerPrefs.GetInt(CampaignMapWraparoundEnabledKey, 0) != 0 ? MapGeometryMode.Disc : MapGeometryMode.Flat;
    }

    // Back-compat shims so existing Disc reads (CampaignMapWrapVisualPatch) keep compiling unchanged.
    internal static bool CampaignMapWraparoundEnabled => MapGeometry == MapGeometryMode.Disc;
    internal static bool CampaignGlobeEnabled => MapGeometry == MapGeometryMode.Globe;

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

    // Per-class paint storage keyed by the design Ship's Guid string. Layered on top of
    // nation paints at resolve time so a design only needs to store the channels it
    // explicitly customizes.
    internal static string DesignShipPaintString(string designKey)
        => string.IsNullOrEmpty(designKey)
            ? string.Empty
            : PlayerPrefs.GetString(DesignShipPaintPreferenceKey(designKey), string.Empty);

    internal static bool SetDesignShipPaintString(string designKey, string value)
        => SetDesignShipPaintString(designKey, value, logChange: true);

    internal static bool SetDesignShipPaintString(string designKey, string value, bool logChange)
    {
        if (string.IsNullOrEmpty(designKey))
            return false;
        string preferenceKey = DesignShipPaintPreferenceKey(designKey);
        string storedValue = value ?? string.Empty;
        string currentValue = PlayerPrefs.GetString(preferenceKey, string.Empty);
        if (string.Equals(currentValue, storedValue, StringComparison.Ordinal))
            return false;

        PlayerPrefs.SetString(preferenceKey, storedValue);
        PlayerPrefs.Save();
        nationShipPaintsRevision++;
        if (logChange)
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP option: Design Ship Paints updated for design {designKey}.");
        return true;
    }

    // User-defined color presets shown in the picker, persisted across sessions.
    // Stored as a semicolon-delimited list of #RRGGBB hex strings. Capped at
    // MaxUserPaintPresets entries; saving past the cap drops the oldest.
    internal static List<Color32> UserPaintPresets()
    {
        string raw = PlayerPrefs.GetString(UserPaintPresetsKey, string.Empty);
        List<Color32> result = new();
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        foreach (string token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseHexColor(token, out Color32 color))
                result.Add(color);
        }
        return result;
    }

    internal static bool AddUserPaintPreset(Color32 color)
    {
        List<Color32> presets = UserPaintPresets();
        // Skip duplicates so the row doesn't accumulate identical swatches.
        foreach (Color32 existing in presets)
        {
            if (existing.r == color.r && existing.g == color.g && existing.b == color.b)
                return false;
        }
        presets.Add(color);
        // Drop oldest when we exceed the cap so the most recent saves win.
        while (presets.Count > MaxUserPaintPresets)
            presets.RemoveAt(0);
        return PersistUserPaintPresets(presets, $"saved {HexFor(color)}");
    }

    internal static bool RemoveUserPaintPresetAt(int index)
    {
        List<Color32> presets = UserPaintPresets();
        if (index < 0 || index >= presets.Count)
            return false;
        Color32 removed = presets[index];
        presets.RemoveAt(index);
        return PersistUserPaintPresets(presets, $"removed {HexFor(removed)}");
    }

    private static bool PersistUserPaintPresets(List<Color32> presets, string changeDescription)
    {
        string serialized = string.Join(";", presets.ConvertAll(HexFor));
        string current = PlayerPrefs.GetString(UserPaintPresetsKey, string.Empty);
        if (string.Equals(current, serialized, StringComparison.Ordinal))
            return false;
        PlayerPrefs.SetString(UserPaintPresetsKey, serialized);
        PlayerPrefs.Save();
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: user paint presets {changeDescription} ({presets.Count}/{MaxUserPaintPresets}).");
        return true;
    }

    private static bool TryParseHexColor(string value, out Color32 color)
    {
        color = default;
        string hex = (value ?? string.Empty).Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
            hex = hex[1..];
        if (hex.Length != 6)
            return false;
        try
        {
            color = new Color32(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                byte.MaxValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string HexFor(Color32 color)
        => $"#{color.r:X2}{color.g:X2}{color.b:X2}";

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
           $"Superstructure Compatibility={SuperstructureRefitsModeText(SuperstructureRefitsEnabled)}; " +
           $"Map Geometry={CampaignMapModeText(MapGeometry)}; " +
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

    internal static string SuperstructureRefitsModeText(bool enabled)
        => enabled ? "Unrestricted" : "Vanilla";

    internal static string CampaignMapModeText(MapGeometryMode mode)
        => mode switch { MapGeometryMode.Disc => "Disc World", MapGeometryMode.Globe => "Globe", _ => "Flat Earth" };

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

    private static string DesignShipPaintPreferenceKey(string designKey)
        => DesignShipPaintStringKeyPrefix + designKey;

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

    // ===== Buy Ships from an Allied Major (default OFF) =====
    private const string AllyShipPurchaseKey = "uadvp_ally_ship_purchase";
    private static bool? allyShipPurchase;

    internal static bool AllyShipPurchaseEnabled
    {
        get => allyShipPurchase ??= PlayerPrefs.GetInt(AllyShipPurchaseKey, 0) != 0;
        set
        {
            allyShipPurchase = value;
            PlayerPrefs.SetInt(AllyShipPurchaseKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Ally Ship Purchase {(value ? "On" : "Off")}.");
        }
    }

    // Premium band the ally charges over the design's build cost (Low/Med/High); each order's roll is
    // further scaled up by the seller's dock pressure. Medium ~= +50%..+120%.
    private const string AllyPremiumLevelKey = "uadvp_ally_premium_level";
    private static LevelSetting? allyPremiumLevel;

    internal static LevelSetting AllyPremiumLevel
    {
        get => allyPremiumLevel ??= LoadLevel(AllyPremiumLevelKey, LevelSetting.Medium);
        set
        {
            allyPremiumLevel = value;
            PlayerPrefs.SetInt(AllyPremiumLevelKey, (int)value);
            PlayerPrefs.Save();
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Ally Build Premium {LevelText(value)}.");
        }
    }

    internal static double AllyPremiumMinFraction => AllyPremiumLevel switch
    {
        LevelSetting.Low => 0.30,
        LevelSetting.High => 0.70,
        _ => 0.50,
    };

    internal static double AllyPremiumMaxFraction => AllyPremiumLevel switch
    {
        LevelSetting.Low => 0.80,
        LevelSetting.High => 1.60,
        _ => 1.20,
    };
}
