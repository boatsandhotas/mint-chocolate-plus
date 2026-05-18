using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using PaintArea = UADVanillaPlus.Harmony.DesignHullColorProofPatch.PaintArea;
using UADVanillaPlus.GameData;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Patch intent: expose VP balance controls in a vanilla-like settings panel.
// Options are grouped by gameplay area so future toggles and multi-choice
// controls can be added without turning the menu into a flat debug list.
[HarmonyPatch(typeof(Ui))]
internal static class InGameOptionsMenuPatch
{
    private enum Section
    {
        Battle,
        Campaign,
        ShipDesign,
        Experimental,
        NationShipPaints,
    }

    private const string ButtonName = "UADVP_OptionsButton";
    private const string PaintLauncherButtonName = "UADVP_PaintLauncherButton";
    private const string MenuName = "UADVP Options";
    private const string ContentName = "UADVP_OptionsContent";
    private const string BattleWeatherOptionName = "UADVP_Option_BattleWeather";
    private const string BattleSpottingRangeOptionName = "UADVP_Option_BattleSpottingRange";
    private const string BattleDamageOptionName = "UADVP_Option_BattleDamage";
    private const string RealisticShellDamageOptionName = "UADVP_Option_RealisticShellDamage";
    private const string DesignAccuracyPenaltiesOptionName = "UADVP_Option_DesignAccuracyPenalties";
    private const string PortStrikeOptionName = "UADVP_Option_PortStrike";
    private const string AiFleetCompositionOptionName = "UADVP_Option_AiFleetComposition";
    private const string AdvancedAiBuilderOptionName = "UADVP_Option_AdvancedAiBuilder";
    private const string SharedDesignsUsageOptionName = "UADVP_Option_SharedDesignsUsage";
    private const string MajorShipTorpedoesOptionName = "UADVP_Option_MajorShipTorpedoes";
    private const string ObsoleteDesignRetentionOptionName = "UADVP_Option_ObsoleteDesignRetention";
    private const string SuperstructureRefitsOptionName = "UADVP_Option_SuperstructureRefits";
    private const string ShipyardCapacityOptionName = "UADVP_Option_ShipyardCapacity";
    private const string CampaignMapWraparoundOptionName = "UADVP_Option_CampaignMapWraparound";
    private const string CanalOpeningsOptionName = "UADVP_Option_CanalOpenings";
    private const string TechnologySpreadOptionName = "UADVP_Option_TechnologySpread";
    private const string CampaignEndDateOptionName = "UADVP_Option_CampaignEndDate";
    private const string MineWarfareOptionName = "UADVP_Option_MineWarfare";
    private const string SubmarineWarfareOptionName = "UADVP_Option_SubmarineWarfare";
    private const string ExperimentalNationShipPaintsOptionName = "UADVP_Option_ExperimentalNationShipPaints";
    private const string BattleRuntimeDiagnosticsOptionName = "UADVP_Option_BattleRuntimeDiagnostics";
    private const string NationShipPaintsSectionName = "UADVP_Option_NationShipPaints";

    private static readonly Color Background = new(0f, 0f, 0f, 0.94f);
    private static readonly Color RowBackground = new(0.09f, 0.09f, 0.09f, 0.96f);
    private static readonly Color SelectedGold = new(0.58f, 0.44f, 0.2f, 0.95f);
    private static readonly Color SegmentIdle = new(0.28f, 0.27f, 0.2f, 0.9f);
    private static readonly Color SegmentDisabled = new(0.12f, 0.12f, 0.1f, 0.82f);
    private static readonly Color SwatchBorder = new(0.78f, 0.78f, 0.72f, 1f);
    private static readonly Color PickerBackdrop = new(0f, 0f, 0f, 0.001f);
    private static readonly Color SliderTrack = new(0.38f, 0.38f, 0.38f, 1f);
    private static readonly Color SliderFill = new(0.78f, 0.6f, 0.28f, 1f);
    private static readonly Color SliderHandle = new(0.98f, 0.97f, 0.92f, 1f);
    private static readonly System.Reflection.MethodInfo? RefreshFinancesWindow = AccessTools.Method(typeof(CampaignFinancesWindow), "Refresh");
    private static readonly System.Reflection.MethodInfo? RefreshConstructorParts = AccessTools.Method(typeof(Ui), "RefreshParts");

    private static Button? launcherButton;
    private static Image? launcherImage;
    private static Outline? launcherOutline;
    private static GameObject? menu;
    private static GameObject? contentRoot;
    private static Section selectedSection = Section.Battle;
    private static bool initialized;
    private static float nextRetryTime;

    private static Button? paintLauncherButton;
    private static Image? paintLauncherImage;
    private static Outline? paintLauncherOutline;
    private static Sprite? paintIconSprite;

    private static GameObject? constructorPaintPanel;
    private static readonly Dictionary<PaintArea, Image> panelSwatches = new();
    private static string panelNationKey = string.Empty;

    private static GameObject? paintPicker;
    private static Image? pickerWheelImage;
    private static RectTransform? pickerWheelRect;
    private static Image? pickerWheelHandle;
    private static RectTransform? pickerWheelHandleRect;
    private static Slider? pickerValueSlider;
    private static Image? pickerPreviewFill;
    private static InputField? pickerHexInput;
    private static Text? pickerValueText;
    private static DesignHullColorProofPatch.NationPaintUiInfo pickerNation;
    private static PaintArea pickerChannel;
    private static Color32 pickerOriginalChannelColor;
    private static float pickerCurrentH;
    private static float pickerCurrentS;
    private static float pickerCurrentV;
    private static bool pickerWheelDragging;
    private static Sprite? colorWheelSprite;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Ui.Start))]
    internal static void StartPostfix()
    {
        TrySetup();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Ui.Update))]
    internal static void UpdatePostfix()
    {
        if (!initialized && Time.realtimeSinceStartup >= nextRetryTime)
        {
            nextRetryTime = Time.realtimeSinceStartup + 1f;
            TrySetup();
        }

        RefreshLauncherButton();
        RefreshPaintLauncherButton();
        RefreshConstructorPaintPanel();
        UpdatePaintPickerWheelDrag();
    }

    private static void TrySetup()
    {
        if (initialized)
            return;

        try
        {
            SetupLauncherButton();
            SetupPaintLauncherButton();
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP options menu setup skipped. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SetupLauncherButton()
    {
        GameObject? options = FindPath("Global/Ui/UiMain/Common/Options");
        GameObject? helpButton = FindPath("Global/Ui/UiMain/Common/Options/Help");
        if (options == null || helpButton == null)
            return;

        GameObject buttonObject = options.transform.Find(ButtonName)?.gameObject ?? UnityEngine.Object.Instantiate(helpButton);
        buttonObject.transform.SetParent(options.transform, false);
        buttonObject.name = ButtonName;
        buttonObject.SetActive(true);
        MatchButtonSizing(buttonObject, helpButton);
        RemoveTooltipHandlers(buttonObject);
        AddLauncherTooltip(buttonObject);

        Transform? imageChild = buttonObject.transform.Find("Image");
        if (imageChild != null && imageChild.TryGetComponent(out Image image))
        {
            launcherImage = image;
            Sprite? sprite = Resources.Load<Sprite>("tabs/tech") ?? Resources.Load<Sprite>("tabs/fleet");
            if (sprite != null)
            {
                launcherImage.sprite = sprite.TryCast<Sprite>();
                launcherImage.preserveAspect = true;
            }

            ScaleLauncherIcon(imageChild);
        }

        launcherOutline = buttonObject.GetComponent<Outline>() ?? buttonObject.AddComponent<Outline>();
        launcherOutline.effectDistance = new Vector2(1f, 1f);

        launcherButton = buttonObject.GetComponent<Button>();
        if (launcherButton != null)
        {
            launcherButton.onClick.RemoveAllListeners();
            launcherButton.onClick.AddListener(new System.Action(OpenMenu));
        }

        initialized = true;
        RefreshLauncherButton();
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP options menu button added.");
    }

    private static void OpenMenu()
    {
        if (menu != null)
        {
            menu.transform.SetAsLastSibling();
            menu.SetActive(true);
            RefreshMenu();
            if (launcherButton != null)
                launcherButton.interactable = false;
            return;
        }

        GameObject? popupTemplate = FindPath("Global/Ui/UiMain/Popup/PopupMenu");
        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupTemplate == null || popupRoot == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP options menu skipped. Popup template not found.");
            return;
        }

        menu = UnityEngine.Object.Instantiate(popupTemplate);
        menu.transform.SetParent(popupRoot.transform, false);
        menu.name = MenuName;
        menu.transform.localScale = Vector3.one;
        menu.transform.localPosition = Vector3.zero;

        RectTransform? rootRect = menu.GetComponent<RectTransform>();
        if (rootRect != null)
        {
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
        }

        ConfigureBackdrop(menu);

        GameObject? window = Child(menu, "Window");
        if (window == null)
        {
            UnityEngine.Object.Destroy(menu);
            menu = null;
            return;
        }

        BuildSettingsWindow(window);
        menu.transform.SetAsLastSibling();
        menu.SetActive(true);
        if (launcherButton != null)
            launcherButton.interactable = false;
    }

    private static void BuildSettingsWindow(GameObject window)
    {
        ClearChildren(window);
        ConfigureWindow(window);
        NormalizeSelectedSection();

        contentRoot = new GameObject(ContentName);
        contentRoot.transform.SetParent(window.transform, false);
        RectTransform contentRect = contentRoot.AddComponent<RectTransform>();
        contentRect.anchorMin = Vector2.zero;
        contentRect.anchorMax = Vector2.one;
        contentRect.offsetMin = new Vector2(32f, 28f);
        contentRect.offsetMax = new Vector2(-32f, -28f);

        VerticalLayoutGroup contentLayout = contentRoot.AddComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 18f;
        contentLayout.childControlHeight = true;
        contentLayout.childControlWidth = true;
        contentLayout.childForceExpandHeight = false;
        contentLayout.childForceExpandWidth = true;

        Text title = AddText(contentRoot.transform, "UAD:VP Options", 18, TextAnchor.MiddleLeft);
        title.name = "UADVP_OptionsTitle";
        AddLayout(title.gameObject, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 1f);

        GameObject body = new("UADVP_OptionsBody");
        body.transform.SetParent(contentRoot.transform, false);
        HorizontalLayoutGroup bodyLayout = body.AddComponent<HorizontalLayoutGroup>();
        bodyLayout.spacing = 12f;
        bodyLayout.childControlHeight = true;
        bodyLayout.childControlWidth = true;
        bodyLayout.childForceExpandHeight = true;
        bodyLayout.childForceExpandWidth = true;
        AddLayout(body, minHeight: 205f, flexibleHeight: 1f, flexibleWidth: 1f);

        BuildSectionList(body.transform);
        BuildSectionPane(body.transform);

        GameObject footer = new("UADVP_OptionsFooter");
        footer.transform.SetParent(contentRoot.transform, false);
        HorizontalLayoutGroup footerLayout = footer.AddComponent<HorizontalLayoutGroup>();
        footerLayout.childAlignment = TextAnchor.MiddleRight;
        footerLayout.childControlWidth = false;
        footerLayout.childControlHeight = true;
        footerLayout.childForceExpandWidth = true;
        footerLayout.spacing = 10f;
        AddLayout(footer, minHeight: 26f, flexibleWidth: 1f);
        AddActionButton(footer.transform, "Close", CloseMenu, width: 105f);
    }

    private static void BuildSectionList(Transform parent)
    {
        GameObject sections = new("UADVP_OptionsSections");
        sections.transform.SetParent(parent, false);
        VerticalLayoutGroup layout = sections.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        AddLayout(sections, minWidth: 122f, preferredWidth: 122f, flexibleHeight: 1f);

        AddSectionButton(sections.transform, Section.Battle, "Battle");
        AddSectionButton(sections.transform, Section.Campaign, "Campaign");
        AddSectionButton(sections.transform, Section.ShipDesign, "Ship Design");
        AddSectionButton(sections.transform, Section.Experimental, "Experimental");
        if (ModSettings.ExperimentalNationShipPaintsEnabled)
            AddSectionButton(sections.transform, Section.NationShipPaints, "Ship Paints");
    }

    private static void BuildSectionPane(Transform parent)
    {
        GameObject pane = new("UADVP_OptionsPane");
        pane.transform.SetParent(parent, false);
        Image paneImage = pane.AddComponent<Image>();
        paneImage.color = new Color(0f, 0f, 0f, 0.12f);
        VerticalLayoutGroup layout = pane.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 6f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        AddLayout(pane, flexibleWidth: 1f, flexibleHeight: 1f);

        AddText(pane.transform, SectionTitle(selectedSection), 15, TextAnchor.MiddleLeft);

        switch (selectedSection)
        {
            case Section.Battle:
                AddSegmentedOption(
                    pane.transform,
                    BattleWeatherOptionName,
                    "Battle Weather",
                    "Always Sunny forces battles to start in daytime fair weather. Vanilla keeps the game's random battle time and weather rolls.",
                    true,
                    ("Always Sunny", ModSettings.BattleWeatherAlwaysSunny, () => SetBattleWeatherMode(true)),
                    ("Vanilla", !ModSettings.BattleWeatherAlwaysSunny, () => SetBattleWeatherMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    BattleSpottingRangeOptionName,
                    "Battle Spotting",
                    "Multiplies the spotter-side spotting range contribution for player and AI ships in battle. Target visibility, weather, smoke, and firing reveal behavior stay vanilla.",
                    true,
                    ("Vanilla", ModSettings.BattleSpottingRange == ModSettings.BattleSpottingRangeMode.Vanilla, () => SetBattleSpottingRangeMode(ModSettings.BattleSpottingRangeMode.Vanilla)),
                    ("3x", ModSettings.BattleSpottingRange == ModSettings.BattleSpottingRangeMode.X3, () => SetBattleSpottingRangeMode(ModSettings.BattleSpottingRangeMode.X3)),
                    ("5x", ModSettings.BattleSpottingRange == ModSettings.BattleSpottingRangeMode.X5, () => SetBattleSpottingRangeMode(ModSettings.BattleSpottingRangeMode.X5)),
                    ("10x", ModSettings.BattleSpottingRange == ModSettings.BattleSpottingRangeMode.X10, () => SetBattleSpottingRangeMode(ModSettings.BattleSpottingRangeMode.X10)));
                AddSegmentedOption(
                    pane.transform,
                    BattleDamageOptionName,
                    "Battle Damage",
                    "Multiplies vanilla's global gun and torpedo section-damage params once when game data is loaded. Unchanged restores vanilla damage values; higher modes are deliberately blunt and affect player and AI firepower.",
                    true,
                    ("Unchanged", ModSettings.BattleDamage == ModSettings.BattleDamageMode.Vanilla, () => SetBattleDamageMode(ModSettings.BattleDamageMode.Vanilla)),
                    ("2x", ModSettings.BattleDamage == ModSettings.BattleDamageMode.X2, () => SetBattleDamageMode(ModSettings.BattleDamageMode.X2)),
                    ("3x", ModSettings.BattleDamage == ModSettings.BattleDamageMode.X3, () => SetBattleDamageMode(ModSettings.BattleDamageMode.X3)),
                    ("5x", ModSettings.BattleDamage == ModSettings.BattleDamageMode.X5, () => SetBattleDamageMode(ModSettings.BattleDamageMode.X5)));
                AddSegmentedOption(
                    pane.transform,
                    RealisticShellDamageOptionName,
                    "Realistic Shell Damage",
                    "Realistic makes gun direct damage scale cubically with caliber, anchored at vanilla 12-inch shell damage. This reduces excessive small-gun structure damage and trims very-large-gun spikes. Battle Damage remains a separate global multiplier.",
                    true,
                    ("Realistic", ModSettings.RealisticShellDamage == ModSettings.RealisticShellDamageMode.Realistic, () => SetRealisticShellDamageMode(ModSettings.RealisticShellDamageMode.Realistic)),
                    ("Vanilla", ModSettings.RealisticShellDamage == ModSettings.RealisticShellDamageMode.Vanilla, () => SetRealisticShellDamageMode(ModSettings.RealisticShellDamageMode.Vanilla)));
                AddSegmentedOption(
                    pane.transform,
                    DesignAccuracyPenaltiesOptionName,
                    "Crew & Accuracy Balance",
                    "Flattens extreme crew, design, and damage-state accuracy swings, including damaged fire control/conning tower and flooding/damage instability. Does not change base damage or positioning. Changing this option is disabled while a battle is loading or active.",
                    CanChangeAccuracyPenalties(),
                    ("/10", ModSettings.DesignAccuracyPenaltyMode == ModSettings.AccuracyPenaltyMode.Div10, () => SetDesignAccuracyPenaltiesMode(ModSettings.AccuracyPenaltyMode.Div10)),
                    ("/5", ModSettings.DesignAccuracyPenaltyMode == ModSettings.AccuracyPenaltyMode.Div5, () => SetDesignAccuracyPenaltiesMode(ModSettings.AccuracyPenaltyMode.Div5)),
                    ("/2", ModSettings.DesignAccuracyPenaltyMode == ModSettings.AccuracyPenaltyMode.Div2, () => SetDesignAccuracyPenaltiesMode(ModSettings.AccuracyPenaltyMode.Div2)),
                    ("Vanilla", ModSettings.DesignAccuracyPenaltyMode == ModSettings.AccuracyPenaltyMode.Vanilla, () => SetDesignAccuracyPenaltiesMode(ModSettings.AccuracyPenaltyMode.Vanilla)));
                break;
            case Section.Campaign:
                AddSegmentedOption(
                    pane.transform,
                    PortStrikeOptionName,
                    "Port Strike",
                    "Balanced scales port strike transport losses to the attacking force instead of letting tiny raids destroy excessive transport capacity.",
                    true,
                    ("Balanced", ModSettings.PortStrikeBalanced, () => SetPortStrikeMode(true)),
                    ("Vanilla", !ModSettings.PortStrikeBalanced, () => SetPortStrikeMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    AiFleetCompositionOptionName,
                    "AI Fleet Mix",
                    "Adjusts AI surface-ship construction weights. Vanilla keeps the game's original light-ship-heavy ratios. Balanced gives BB, BC, CA, CL, DD, and TB equal weight. Heavy favors capital and cruiser fleets while reducing destroyer and torpedo-boat pressure.",
                    true,
                    ("Vanilla", ModSettings.AiFleetComposition == ModSettings.AiFleetCompositionMode.Vanilla, () => SetAiFleetCompositionMode(ModSettings.AiFleetCompositionMode.Vanilla)),
                    ("Balanced", ModSettings.AiFleetComposition == ModSettings.AiFleetCompositionMode.Balanced, () => SetAiFleetCompositionMode(ModSettings.AiFleetCompositionMode.Balanced)),
                    ("Heavy", ModSettings.AiFleetComposition == ModSettings.AiFleetCompositionMode.Heavy, () => SetAiFleetCompositionMode(ModSettings.AiFleetCompositionMode.Heavy)));
                AddSegmentedOption(
                    pane.transform,
                    AdvancedAiBuilderOptionName,
                    "Advanced AI Builder",
                    "Enhanced lets VP help AI design books with shared-design blueprint adaptation, missing-type recovery, and stale-design refreshes. Vanilla keeps the game's original design-generation cadence and exact shared-design checks.",
                    true,
                    ("Enhanced", ModSettings.AdvancedAiBuilderEnabled, () => SetAdvancedAiBuilderMode(true)),
                    ("Vanilla", !ModSettings.AdvancedAiBuilderEnabled, () => SetAdvancedAiBuilderMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    SharedDesignsUsageOptionName,
                    "Shared Designs",
                    "Changes the active campaign shared-design setting for future AI designs. Existing designs are not altered, and the selected mode persists when the campaign is saved.",
                    CampaignSharedDesignUsageSettings.HasActiveCampaign,
                    ("Off", CampaignSharedDesignUsageSettings.CurrentMode == CampaignController.SharedDesignUsage.Off, () => SetSharedDesignsUsageMode(CampaignController.SharedDesignUsage.Off)),
                    ("Selective", CampaignSharedDesignUsageSettings.CurrentMode == CampaignController.SharedDesignUsage.Selective, () => SetSharedDesignsUsageMode(CampaignController.SharedDesignUsage.Selective)),
                    ("Always", CampaignSharedDesignUsageSettings.CurrentMode == CampaignController.SharedDesignUsage.Always, () => SetSharedDesignsUsageMode(CampaignController.SharedDesignUsage.Always)));
                AddSegmentedOption(
                    pane.transform,
                    ShipyardCapacityOptionName,
                    "Suspend Dock Overcapacity",
                    "Automatic temporarily suspends lower-priority repairs, builds, and refits during the monthly advance when dock work exceeds shipyard capacity. Manual keeps vanilla behavior, where players must manage overcapacity themselves and the game applies its global over-capacity time penalty.",
                    true,
                    ("Automatic", ModSettings.ShipyardCapacityBalanced, () => SetShipyardCapacityMode(true)),
                    ("Manual", !ModSettings.ShipyardCapacityBalanced, () => SetShipyardCapacityMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    CanalOpeningsOptionName,
                    "Canal Openings",
                    "Early opens the Panama and Kiel canals from 1890 when a campaign map loads, like Suez and the other early canals. Historical keeps vanilla's 1914 and 1895 opening years.",
                    true,
                    ("Early", ModSettings.EarlyCanalOpeningsEnabled, () => SetCanalOpeningsMode(true)),
                    ("Historical", !ModSettings.EarlyCanalOpeningsEnabled, () => SetCanalOpeningsMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    TechnologySpreadOptionName,
                    "Technology Spread",
                    "Gradual, Swift, and Unrestricted multiply vanilla research speed for major nations that trail the current leader in a category. Historical grants every major nation normal technologies by historical year and sets research budgets to zero. Repeatable end-techs remain vanilla.",
                    true,
                    ("Vanilla", ModSettings.TechnologySpread == ModSettings.TechnologySpreadMode.Vanilla, () => SetTechnologySpreadMode(ModSettings.TechnologySpreadMode.Vanilla)),
                    ("Gradual", ModSettings.TechnologySpread == ModSettings.TechnologySpreadMode.Gradual, () => SetTechnologySpreadMode(ModSettings.TechnologySpreadMode.Gradual)),
                    ("Swift", ModSettings.TechnologySpread == ModSettings.TechnologySpreadMode.Swift, () => SetTechnologySpreadMode(ModSettings.TechnologySpreadMode.Swift)),
                    ("Unrestricted", ModSettings.TechnologySpread == ModSettings.TechnologySpreadMode.Unrestricted, () => SetTechnologySpreadMode(ModSettings.TechnologySpreadMode.Unrestricted)),
                    ("Historical", ModSettings.TechnologySpread == ModSettings.TechnologySpreadMode.Historical, () => SetTechnologySpreadMode(ModSettings.TechnologySpreadMode.Historical)));
                AddSegmentedOption(
                    pane.transform,
                    CampaignEndDateOptionName,
                    "Campaign End Date",
                    "Enabled keeps vanilla's forced retirement at 1965. Disabled suppresses the vanilla retirement popup and finish call so campaigns can continue past 1965; other campaign-ending conditions still apply.",
                    true,
                    ("Disabled", !ModSettings.CampaignEndDateEnabled, () => SetCampaignEndDateMode(false)),
                    ("Enabled", ModSettings.CampaignEndDateEnabled, () => SetCampaignEndDateMode(true)));
                AddSegmentedOption(
                    pane.transform,
                    MineWarfareOptionName,
                    "Mine Warfare",
                    "Disabled prevents minefield damage and hides mine and minesweeping equipment from the ship designer. Enabled keeps the game's normal minefields and mine equipment.",
                    true,
                    ("Disabled", ModSettings.MineWarfareDisabled, () => SetMineWarfareMode(true)),
                    ("Enabled", !ModSettings.MineWarfareDisabled, () => SetMineWarfareMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    SubmarineWarfareOptionName,
                    "Submarine Warfare",
                    "Disabled prevents submarine construction and submarine campaign battles while leaving existing submarines in saved campaigns untouched. Enabled keeps the game's normal submarine warfare.",
                    true,
                    ("Disabled", ModSettings.SubmarineWarfareDisabled, () => SetSubmarineWarfareMode(true)),
                    ("Enabled", !ModSettings.SubmarineWarfareDisabled, () => SetSubmarineWarfareMode(false)));
                break;
            case Section.ShipDesign:
                AddSegmentedOption(
                    pane.transform,
                    MajorShipTorpedoesOptionName,
                    "CA+ Torpedoes",
                    "Disallowed prevents heavy cruisers and larger ships from mounting torpedoes. This nudges designs toward more plausible fleet roles and avoids oversized torpedo platforms.",
                    true,
                    ("Disallowed", ModSettings.MajorShipTorpedoesRestricted, () => SetMajorShipTorpedoesMode(true)),
                    ("Vanilla", !ModSettings.MajorShipTorpedoesRestricted, () => SetMajorShipTorpedoesMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    ObsoleteDesignRetentionOptionName,
                    "Obsolete Tech & Hulls",
                    "Retain keeps already researched obsolete hulls and components available for player ship designs. Vanilla hides older options as newer equivalents become available. AI design availability remains vanilla.",
                    true,
                    ("Retain", ModSettings.ObsoleteDesignRetentionEnabled, () => SetObsoleteDesignRetentionMode(true)),
                    ("Vanilla", !ModSettings.ObsoleteDesignRetentionEnabled, () => SetObsoleteDesignRetentionMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    SuperstructureRefitsOptionName,
                    "Superstructure Compatibility",
                    "Unrestricted lets researched main towers, secondary towers, and funnels be used beyond their vanilla hull-family compatibility. Tech, country, ship class, mount, and placement checks still apply.",
                    true,
                    ("Unrestricted", ModSettings.SuperstructureRefitsEnabled, () => SetSuperstructureRefitsMode(true)),
                    ("Vanilla", !ModSettings.SuperstructureRefitsEnabled, () => SetSuperstructureRefitsMode(false)));
                break;
            case Section.Experimental:
                AddSegmentedOption(
                    pane.transform,
                    CampaignMapWraparoundOptionName,
                    "Map Geometry",
                    "Disc World enables the experimental campaign-map wrap illusion at the Pacific edge: neighboring map copies, wider horizontal panning, and wrapped marker and movement interactions. Flat Earth keeps vanilla one-map geometry and bounds.",
                    true,
                    ("Disc World", ModSettings.CampaignMapWraparoundEnabled, () => SetCampaignMapWraparoundMode(true)),
                    ("Flat Earth", !ModSettings.CampaignMapWraparoundEnabled, () => SetCampaignMapWraparoundMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    ExperimentalNationShipPaintsOptionName,
                    "Experimental Nation Ship Paints",
                    "On applies experimental nation-themed ship paint schemes in designer previews and battles. Visual style and battle-load performance are still being tuned. Off keeps the game's original ship materials.",
                    true,
                    ("Off", !ModSettings.ExperimentalNationShipPaintsEnabled, () => SetExperimentalNationShipPaintsMode(false)),
                    ("On", ModSettings.ExperimentalNationShipPaintsEnabled, () => SetExperimentalNationShipPaintsMode(true)));
                AddSegmentedOption(
                    pane.transform,
                    BattleRuntimeDiagnosticsOptionName,
                    "Battle Runtime Diagnostics",
                    "On logs temporary battle-exit summaries for aim uptime, target churn, maneuvering, and weapon output. This is enabled for current balance investigation builds and should be disabled before release.",
                    true,
                    ("On", ModSettings.BattleRuntimeDiagnosticsEnabled, () => SetBattleRuntimeDiagnosticsMode(true)),
                    ("Off", !ModSettings.BattleRuntimeDiagnosticsEnabled, () => SetBattleRuntimeDiagnosticsMode(false)));
                break;
            case Section.NationShipPaints:
                BuildNationShipPaintsPane(pane.transform);
                break;
        }
    }

    private static void BuildNationShipPaintsPane(Transform parent)
    {
        DesignHullColorProofPatch.RefreshNationPaintSettingsCache("options menu");
        AddText(parent, "Click a swatch to pick a color for that ship area. Reset clears the override and restores the built-in scheme.", 12, TextAnchor.MiddleLeft);

        foreach (DesignHullColorProofPatch.NationPaintUiInfo nation in DesignHullColorProofPatch.NationPaintOptions())
            AddNationShipPaintRow(parent, nation);
    }

    private static void AddNationShipPaintRow(Transform parent, DesignHullColorProofPatch.NationPaintUiInfo nation)
    {
        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(nation.Key, out Dictionary<PaintArea, Color32> colors))
            return;

        GameObject row = new($"{NationShipPaintsSectionName}_{nation.Key}");
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = RowBackground;

        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.padding = new RectOffset { left = 8, right = 8, top = 2, bottom = 2 };
        rowLayout.spacing = 4f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        Text label = AddText(row.transform, $"{nation.Label}:", 12, TextAnchor.MiddleLeft);
        AddLayout(label.gameObject, minWidth: 100f, preferredWidth: 100f, flexibleWidth: 0f);

        // 14 swatches across all channels — picker UI is intentionally sloppy/wide during
        // experimentation. Tooltip on each swatch identifies its channel.
        foreach (PaintArea area in DesignHullColorProofPatch.AllPickerChannels)
        {
            if (!colors.TryGetValue(area, out Color32 channelColor))
                continue;
            AddPaintSwatch(row.transform, nation, area, channelColor);
        }

        GameObject spacer = new("UADVP_PaintRowSpacer");
        spacer.transform.SetParent(row.transform, false);
        Image spacerImage = spacer.AddComponent<Image>();
        spacerImage.color = new Color(0f, 0f, 0f, 0f);
        spacerImage.raycastTarget = false;
        AddLayout(spacer, minWidth: 4f, flexibleWidth: 1f);

        AddActionButton(row.transform, "Reset", () => ResetNationShipPaintString(nation), width: 56f);
    }

    private static void AddPaintSwatch(Transform parent, DesignHullColorProofPatch.NationPaintUiInfo nation, PaintArea channel, Color32 color)
    {
        // Compact swatch (no inline label) — channel name shows via tooltip. Keeps the
        // row width manageable now that we expose all 14 channels per nation.
        GameObject swatchObject = new($"UADVP_Swatch_{nation.Key}_{channel}");
        swatchObject.transform.SetParent(parent, false);
        Image border = swatchObject.AddComponent<Image>();
        border.color = SwatchBorder;
        AddLayout(swatchObject, minWidth: 24f, preferredWidth: 24f, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 0f);

        GameObject swatchFill = new("Fill");
        swatchFill.transform.SetParent(swatchObject.transform, false);
        Image fill = swatchFill.AddComponent<Image>();
        fill.color = color;
        fill.raycastTarget = false;
        RectTransform fillRect = swatchFill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(1f, 1f);
        fillRect.offsetMax = new Vector2(-1f, -1f);

        Button button = swatchObject.AddComponent<Button>();
        button.targetGraphic = border;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(() => OpenPaintPicker(nation, channel)));

        AddTooltip(swatchObject, $"{nation.Label} — {ChannelLabel(channel)}\nCurrent: {HexFor(color)}\nClick to pick a color.");
    }

    private static void AddSegmentedOption(Transform parent, string name, string label, string tooltip, bool interactable, params (string Label, bool Selected, Action OnPress)[] segments)
    {
        GameObject row = new(name);
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = RowBackground;
        AddTooltip(row, $"{label}\n{tooltip}");
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.padding = new RectOffset
        {
            left = 8,
            right = 8,
            top = 4,
            bottom = 4,
        };
        rowLayout.spacing = 8f;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 34f, preferredHeight: 34f, flexibleWidth: 1f);

        Text labelText = AddText(row.transform, label, 13, TextAnchor.MiddleLeft);
        AddLayout(labelText.gameObject, minWidth: 155f, flexibleWidth: 1f);

        foreach (var segment in segments)
            AddSegmentButton(row.transform, segment.Label, segment.Selected, segment.OnPress, segments.Length > 2 ? 92f : 112f, interactable, $"{label}: {segment.Label}\n{tooltip}");
    }

    private static void AddSectionButton(Transform parent, Section section, string label)
    {
        Button button = AddActionButton(parent, label, () => SelectSection(section), width: 102f);
        Image image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
        image.color = selectedSection == section ? SelectedGold : SegmentIdle;
    }

    private static void AddSegmentButton(Transform parent, string label, bool selected, Action onPress, float width, bool interactable, string tooltip)
    {
        Button button = AddActionButton(parent, label, onPress, width);
        button.interactable = interactable;
        AddTooltip(button.gameObject, tooltip);
        Image image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
        image.color = selected && interactable ? SelectedGold : SegmentDisabled;
    }

    private static Button AddActionButton(Transform parent, string label, Action onPress, float width)
    {
        GameObject? buttonTemplate = FindPath("Global/Ui/UiMain/Popup/PopupMenu/Window/ButtonBase");
        GameObject buttonObject = buttonTemplate != null ? UnityEngine.Object.Instantiate(buttonTemplate) : new GameObject($"UADVP_Button_{label}");
        buttonObject.transform.SetParent(parent, false);
        buttonObject.name = $"UADVP_Button_{label.Replace(" ", string.Empty)}";
        buttonObject.SetActive(true);
        buttonObject.transform.localPosition = Vector3.zero;
        buttonObject.transform.localScale = Vector3.one;
        // The vanilla popup button prefab carries tall menu geometry; clamp both
        // layout and rect height so compact option rows do not balloon vertically.
        AddLayout(buttonObject, minWidth: width, preferredWidth: width, minHeight: 26f, preferredHeight: 26f, flexibleHeight: 0f);
        RectTransform? buttonRect = buttonObject.GetComponent<RectTransform>();
        if (buttonRect != null)
            buttonRect.sizeDelta = new Vector2(buttonRect.sizeDelta.x, 26f);

        Button button = buttonObject.GetComponent<Button>() ?? buttonObject.AddComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(onPress));

        Image image = buttonObject.GetComponent<Image>() ?? buttonObject.AddComponent<Image>();
        button.targetGraphic = image;
        SetMenuButtonText(buttonObject, label);
        return button;
    }

    private static Text AddText(Transform parent, string text, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = new($"UADVP_Text_{text.Replace(" ", string.Empty).Replace(":", string.Empty)}");
        textObject.transform.SetParent(parent, false);
        Text uiText = textObject.AddComponent<Text>();
        uiText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        uiText.fontSize = fontSize;
        uiText.color = Color.white;
        uiText.alignment = alignment;
        uiText.text = text;
        uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
        uiText.verticalOverflow = VerticalWrapMode.Overflow;
        AddLayout(textObject, minHeight: fontSize + 6f, preferredHeight: fontSize + 6f, flexibleWidth: 1f);
        return uiText;
    }

    private static void SelectSection(Section section)
    {
        if (section == Section.NationShipPaints && !ModSettings.ExperimentalNationShipPaintsEnabled)
            section = Section.Experimental;

        selectedSection = section;
        RefreshMenu();
    }

    private static void SetBattleWeatherMode(bool alwaysSunny)
    {
        if (ModSettings.BattleWeatherAlwaysSunny != alwaysSunny)
            ModSettings.BattleWeatherAlwaysSunny = alwaysSunny;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetBattleSpottingRangeMode(ModSettings.BattleSpottingRangeMode mode)
    {
        if (ModSettings.BattleSpottingRange != mode)
            ModSettings.BattleSpottingRange = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetBattleDamageMode(ModSettings.BattleDamageMode mode)
    {
        if (ModSettings.BattleDamage != mode)
            ModSettings.BattleDamage = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetRealisticShellDamageMode(ModSettings.RealisticShellDamageMode mode)
    {
        if (ModSettings.RealisticShellDamage != mode)
            ModSettings.RealisticShellDamage = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetDesignAccuracyPenaltiesMode(ModSettings.AccuracyPenaltyMode mode)
    {
        if (!CanChangeAccuracyPenalties())
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP option: Crew & Accuracy Balance cannot be changed while a battle is loading or active.");
            RefreshMenu();
            RefreshLauncherButton();
            return;
        }

        if (ModSettings.DesignAccuracyPenaltyMode != mode)
            ModSettings.DesignAccuracyPenaltyMode = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static bool CanChangeAccuracyPenalties()
        => !AccuracyPenaltyBalance.IsBattleOrLoading();

    private static void SetPortStrikeMode(bool balanced)
    {
        if (ModSettings.PortStrikeBalanced != balanced)
            ModSettings.PortStrikeBalanced = balanced;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetAiFleetCompositionMode(ModSettings.AiFleetCompositionMode mode)
    {
        if (ModSettings.AiFleetComposition != mode)
        {
            ModSettings.AiFleetComposition = mode;
            CampaignAiFleetCompositionPatch.ApplyCurrentSetting("options menu");
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetAdvancedAiBuilderMode(bool enabled)
    {
        if (ModSettings.AdvancedAiBuilderEnabled != enabled)
            ModSettings.AdvancedAiBuilderEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSharedDesignsUsageMode(CampaignController.SharedDesignUsage mode)
    {
        CampaignSharedDesignUsageSettings.TrySetMode(mode);
        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetMajorShipTorpedoesMode(bool restricted)
    {
        if (ModSettings.MajorShipTorpedoesRestricted != restricted)
            ModSettings.MajorShipTorpedoesRestricted = restricted;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetObsoleteDesignRetentionMode(bool enabled)
    {
        if (ModSettings.ObsoleteDesignRetentionEnabled != enabled)
        {
            ModSettings.ObsoleteDesignRetentionEnabled = enabled;
            RefreshConstructorAvailabilityUi();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSuperstructureRefitsMode(bool enabled)
    {
        if (ModSettings.SuperstructureRefitsEnabled != enabled)
        {
            ModSettings.SuperstructureRefitsEnabled = enabled;
            RefreshConstructorAvailabilityUi("Superstructure Compatibility");
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetShipyardCapacityMode(bool balanced)
    {
        if (ModSettings.ShipyardCapacityBalanced != balanced)
        {
            ModSettings.ShipyardCapacityBalanced = balanced;
            RefreshCampaignCostUi();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetCampaignMapWraparoundMode(bool enabled)
    {
        if (ModSettings.CampaignMapWraparoundEnabled != enabled)
        {
            ModSettings.CampaignMapWraparoundEnabled = enabled;
            CampaignMapWrapVisualPatch.ApplyCurrentSetting();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetExperimentalNationShipPaintsMode(bool enabled)
    {
        if (ModSettings.ExperimentalNationShipPaintsEnabled != enabled)
        {
            ModSettings.ExperimentalNationShipPaintsEnabled = enabled;
            DesignHullColorProofPatch.ApplyCurrentSetting();
        }

        if (enabled)
            selectedSection = Section.NationShipPaints;
        else if (selectedSection == Section.NationShipPaints)
            selectedSection = Section.Experimental;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetBattleRuntimeDiagnosticsMode(bool enabled)
    {
        if (ModSettings.BattleRuntimeDiagnosticsEnabled != enabled)
            ModSettings.BattleRuntimeDiagnosticsEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void ResetNationShipPaintString(DesignHullColorProofPatch.NationPaintUiInfo nation)
    {
        ClosePaintPicker();
        if (ModSettings.SetNationShipPaintString(nation.Key, string.Empty))
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange($"{nation.Label} reset");

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetCanalOpeningsMode(bool early)
    {
        if (ModSettings.EarlyCanalOpeningsEnabled != early)
        {
            ModSettings.EarlyCanalOpeningsEnabled = early;
            CampaignCanalOpeningPatch.ApplyCurrentSetting();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetTechnologySpreadMode(ModSettings.TechnologySpreadMode mode)
    {
        if (ModSettings.TechnologySpread != mode)
        {
            ModSettings.TechnologySpread = mode;

            if (mode == ModSettings.TechnologySpreadMode.Historical)
            {
                CampaignHistoricalResearchPatch.ApplyCurrentSetting("option change");
                RefreshCampaignCostUi("Technology Spread mode change");
            }
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetCampaignEndDateMode(bool enabled)
    {
        if (ModSettings.CampaignEndDateEnabled != enabled)
            ModSettings.CampaignEndDateEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetMineWarfareMode(bool disabled)
    {
        if (ModSettings.MineWarfareDisabled != disabled)
        {
            ModSettings.MineWarfareDisabled = disabled;
            RefreshConstructorAvailabilityUi("Mine Warfare");
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSubmarineWarfareMode(bool disabled)
    {
        if (ModSettings.SubmarineWarfareDisabled != disabled)
        {
            ModSettings.SubmarineWarfareDisabled = disabled;
            RefreshSubmarineWarfareUi();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void RefreshConstructorAvailabilityUi()
        => RefreshConstructorAvailabilityUi("Obsolete Tech & Hulls");

    private static void RefreshConstructorAvailabilityUi(string optionName)
    {
        try
        {
            if (!GameManager.IsConstructor)
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP option: stored {optionName} mode change; constructor UI is not active.");
                return;
            }

            Ui? ui = G.ui;
            if (ui == null || PlayerController.Instance == null)
                return;

            try { ui.RefreshConstructorInfo(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: constructor info refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try { RefreshConstructorParts?.Invoke(ui, Array.Empty<object>()); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: constructor parts refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: refreshed constructor availability UI after {optionName} mode change.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP option: constructor availability refresh skipped. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RefreshSubmarineWarfareUi()
    {
        try
        {
            Ui? ui = G.ui;
            if (ui == null || PlayerController.Instance == null || CampaignController.Instance?.CampaignData == null)
                return;

            try { ui.CountryInfo?.Refresh(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: submarine warfare country-info refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try { ui.SubmarineWindow?.Refresh(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: submarine warfare window refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP option: refreshed submarine warfare UI after mode change.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP option: submarine warfare UI refresh skipped. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RefreshCampaignCostUi(string reason = "Suspend Dock Overcapacity mode change")
    {
        try
        {
            Ui? ui = G.ui;
            if (ui == null || PlayerController.Instance == null || CampaignController.Instance?.CampaignData == null)
                return;

            try { ui.CountryInfo?.Refresh(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: country-info cost refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                if (ui.FinancesWindow != null)
                    RefreshFinancesWindow?.Invoke(ui.FinancesWindow, Array.Empty<object>());
            }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: finances cost refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try { ui.FleetWindow?.Refresh(false); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: fleet cost refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try { ui.SubmarineWindow?.Refresh(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: submarine cost refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            try { ui.RefreshCampaignUI(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP option: campaign UI refresh failed. {ex.GetType().Name}: {ex.Message}"); }

            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: refreshed campaign cost UI after {reason}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP option: campaign cost UI refresh skipped. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RefreshMenu()
    {
        if (contentRoot == null)
            return;

        GameObject? window = Child(menu, "Window");
        if (window == null)
            return;

        BuildSettingsWindow(window);
    }

    private static void NormalizeSelectedSection()
    {
        if (selectedSection == Section.NationShipPaints && !ModSettings.ExperimentalNationShipPaintsEnabled)
            selectedSection = Section.Experimental;
    }

    private static void RefreshLauncherButton()
    {
        if (launcherButton == null)
            return;

        launcherButton.interactable = menu == null || !menu.activeInHierarchy;

        if (launcherImage != null)
            launcherImage.color = Color.white;

        if (launcherOutline != null)
            launcherOutline.effectColor = AnyBalanceOptionEnabled() ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
    }

    private static bool AnyBalanceOptionEnabled()
        => ModSettings.BattleWeatherAlwaysSunny || ModSettings.BattleSpottingRange != ModSettings.BattleSpottingRangeMode.Vanilla || ModSettings.BattleDamage != ModSettings.BattleDamageMode.Vanilla || ModSettings.RealisticShellDamageEnabled || ModSettings.DesignAccuracyPenaltiesBalanced || ModSettings.PortStrikeBalanced || ModSettings.AiFleetComposition != ModSettings.AiFleetCompositionMode.Vanilla || ModSettings.AdvancedAiBuilderEnabled || ModSettings.MajorShipTorpedoesRestricted || ModSettings.ObsoleteDesignRetentionEnabled || ModSettings.SuperstructureRefitsEnabled || ModSettings.ShipyardCapacityBalanced || ModSettings.EarlyCanalOpeningsEnabled || ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Vanilla || !ModSettings.CampaignEndDateEnabled || ModSettings.MineWarfareDisabled || ModSettings.SubmarineWarfareDisabled || ModSettings.CampaignMapWraparoundEnabled || ModSettings.ExperimentalNationShipPaintsEnabled;

    private static void AddLauncherTooltip(GameObject buttonObject)
        => AddTooltip(
            buttonObject,
            LauncherTooltipText,
            () => launcherButton != null && launcherButton.interactable);

    private static string LauncherTooltipText()
        => $"UAD:VP Options\nBattle Weather: {BattleWeatherModeText(ModSettings.BattleWeatherAlwaysSunny)}\nBattle Spotting: {BattleSpottingRangeModeText(ModSettings.BattleSpottingRange)}\nBattle Damage: {BattleDamageModeText(ModSettings.BattleDamage)}\nRealistic Shell Damage: {RealisticShellDamageModeText(ModSettings.RealisticShellDamage)}\nCrew & Accuracy Balance: {DesignAccuracyPenaltiesModeText(ModSettings.DesignAccuracyPenaltyMode)}\nPort Strike: {PortStrikeModeText(ModSettings.PortStrikeBalanced)}\nAI Fleet Mix: {AiFleetCompositionModeText(ModSettings.AiFleetComposition)}\nAdvanced AI Builder: {AdvancedAiBuilderModeText(ModSettings.AdvancedAiBuilderEnabled)}\nShared Designs: {CampaignSharedDesignUsageSettings.CurrentModeText()}\nSuspend Dock Overcapacity: {ShipyardCapacityModeText(ModSettings.ShipyardCapacityBalanced)}\nCanal Openings: {CanalOpeningModeText(ModSettings.EarlyCanalOpeningsEnabled)}\nTechnology Spread: {TechnologySpreadModeText(ModSettings.TechnologySpread)}\nCampaign End Date: {CampaignEndDateModeText(ModSettings.CampaignEndDateEnabled)}\nMine Warfare: {MineWarfareModeText(ModSettings.MineWarfareDisabled)}\nSubmarine Warfare: {SubmarineWarfareModeText(ModSettings.SubmarineWarfareDisabled)}\nCA+ Torpedoes: {MajorShipTorpedoesModeText(ModSettings.MajorShipTorpedoesRestricted)}\nObsolete Tech & Hulls: {ObsoleteDesignRetentionModeText(ModSettings.ObsoleteDesignRetentionEnabled)}\nSuperstructure Compatibility: {SuperstructureRefitsModeText(ModSettings.SuperstructureRefitsEnabled)}\nMap Geometry: {CampaignMapWraparoundModeText(ModSettings.CampaignMapWraparoundEnabled)}\nExperimental Nation Ship Paints: {ExperimentalNationShipPaintsModeText(ModSettings.ExperimentalNationShipPaintsEnabled)}\nBattle Runtime Diagnostics: {BattleRuntimeDiagnosticsModeText(ModSettings.BattleRuntimeDiagnosticsEnabled)}";

    private static void AddTooltip(GameObject target, string text, Func<bool>? canShow = null)
        => AddTooltip(target, () => text, canShow);

    private static void AddTooltip(GameObject target, Func<string> textFactory, Func<bool>? canShow = null)
    {
        RemoveTooltipHandlers(target);
        OnEnter onEnter = target.AddComponent<OnEnter>();
        onEnter.action = new System.Action(() =>
        {
            if (G.ui == null || canShow?.Invoke() == false)
                return;

            G.ui.ShowTooltip(textFactory(), target);
        });

        OnLeave onLeave = target.AddComponent<OnLeave>();
        onLeave.action = new System.Action(() =>
        {
            try { G.ui?.HideTooltip(); }
            catch { }
        });
    }

    private static void ConfigureBackdrop(GameObject root)
    {
        GameObject? bg = Child(root, "Bg");
        if (bg == null)
            return;

        bg.transform.SetAsFirstSibling();
        RectTransform? bgRect = bg.GetComponent<RectTransform>();
        if (bgRect != null)
        {
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
        }

        Image bgImage = bg.GetComponent<Image>() ?? bg.AddComponent<Image>();
        bgImage.color = new Color(0f, 0f, 0f, 0.6f);
        bgImage.raycastTarget = true;
    }

    private static void ConfigureWindow(GameObject window)
    {
        Image image = window.GetComponent<Image>() ?? window.AddComponent<Image>();
        image.color = Background;

        RectTransform? rect = window.GetComponent<RectTransform>();
        if (rect == null)
            return;

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(980f, 560f);
    }

    private static void MatchButtonSizing(GameObject target, GameObject template)
    {
        target.transform.localScale = template.transform.localScale;

        RectTransform? targetRect = target.GetComponent<RectTransform>();
        RectTransform? templateRect = template.GetComponent<RectTransform>();
        if (targetRect != null && templateRect != null)
        {
            targetRect.anchorMin = templateRect.anchorMin;
            targetRect.anchorMax = templateRect.anchorMax;
            targetRect.pivot = templateRect.pivot;
            targetRect.sizeDelta = templateRect.sizeDelta;
            targetRect.localScale = templateRect.localScale;
        }

        LayoutElement? targetLayout = target.GetComponent<LayoutElement>();
        LayoutElement? templateLayout = template.GetComponent<LayoutElement>();
        if (targetLayout != null && templateLayout != null)
        {
            targetLayout.minWidth = templateLayout.minWidth;
            targetLayout.minHeight = templateLayout.minHeight;
            targetLayout.preferredWidth = templateLayout.preferredWidth;
            targetLayout.preferredHeight = templateLayout.preferredHeight;
            targetLayout.flexibleWidth = templateLayout.flexibleWidth;
            targetLayout.flexibleHeight = templateLayout.flexibleHeight;
        }

        Transform? targetImage = target.transform.Find("Image");
        Transform? templateImage = template.transform.Find("Image");
        if (targetImage == null || templateImage == null)
            return;

        RectTransform? targetImageRect = targetImage.GetComponent<RectTransform>();
        RectTransform? templateImageRect = templateImage.GetComponent<RectTransform>();
        if (targetImageRect == null || templateImageRect == null)
            return;

        targetImageRect.anchorMin = templateImageRect.anchorMin;
        targetImageRect.anchorMax = templateImageRect.anchorMax;
        targetImageRect.pivot = templateImageRect.pivot;
        targetImageRect.sizeDelta = templateImageRect.sizeDelta;
        targetImageRect.localScale = templateImageRect.localScale;
    }

    private static void ScaleLauncherIcon(Transform imageChild)
    {
        RectTransform? rect = imageChild.GetComponent<RectTransform>();
        if (rect != null)
            rect.localScale *= 0.67f;
        else
            imageChild.localScale *= 0.67f;
    }

    private static LayoutElement AddLayout(
        GameObject target,
        float minWidth = -1f,
        float preferredWidth = -1f,
        float minHeight = -1f,
        float preferredHeight = -1f,
        float flexibleWidth = -1f,
        float flexibleHeight = -1f)
    {
        LayoutElement layout = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
        if (minWidth >= 0f)
            layout.minWidth = minWidth;
        if (preferredWidth >= 0f)
            layout.preferredWidth = preferredWidth;
        if (minHeight >= 0f)
            layout.minHeight = minHeight;
        if (preferredHeight >= 0f)
            layout.preferredHeight = preferredHeight;
        if (flexibleWidth >= 0f)
            layout.flexibleWidth = flexibleWidth;
        if (flexibleHeight >= 0f)
            layout.flexibleHeight = flexibleHeight;
        return layout;
    }

    private static void ClearChildren(GameObject target)
    {
        for (int i = target.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(target.transform.GetChild(i).gameObject);
    }

    private static string SectionTitle(Section section)
        => section switch
        {
            Section.Battle => "Battle",
            Section.Campaign => "Campaign",
            Section.ShipDesign => "Ship Design",
            Section.Experimental => "Experimental",
            Section.NationShipPaints => "Nation Ship Paints",
            _ => "Options",
        };

    private static string BattleWeatherModeText(bool alwaysSunny)
        => alwaysSunny ? "Always Sunny" : "Vanilla";

    private static string PortStrikeModeText(bool balanced)
        => balanced ? "Balanced" : "Vanilla";

    private static string AiFleetCompositionModeText(ModSettings.AiFleetCompositionMode mode)
        => ModSettings.AiFleetCompositionModeText(mode);

    private static string AdvancedAiBuilderModeText(bool enabled)
        => ModSettings.AdvancedAiBuilderModeText(enabled);

    private static string DesignAccuracyPenaltiesModeText(ModSettings.AccuracyPenaltyMode mode)
        => ModSettings.AccuracyPenaltyModeText(mode);

    private static string BattleSpottingRangeModeText(ModSettings.BattleSpottingRangeMode mode)
        => ModSettings.BattleSpottingRangeModeText(mode);

    private static string BattleDamageModeText(ModSettings.BattleDamageMode mode)
        => ModSettings.BattleDamageModeText(mode);

    private static string RealisticShellDamageModeText(ModSettings.RealisticShellDamageMode mode)
        => ModSettings.RealisticShellDamageModeText(mode);

    private static string MajorShipTorpedoesModeText(bool restricted)
        => restricted ? "Disallowed" : "Vanilla";

    private static string ObsoleteDesignRetentionModeText(bool enabled)
        => enabled ? "Retain" : "Vanilla";

    private static string SuperstructureRefitsModeText(bool enabled)
        => ModSettings.SuperstructureRefitsModeText(enabled);

    private static string ShipyardCapacityModeText(bool balanced)
        => balanced ? "Automatic" : "Manual";

    private static string CanalOpeningModeText(bool early)
        => ModSettings.CanalOpeningModeText(early);

    private static string TechnologySpreadModeText(ModSettings.TechnologySpreadMode mode)
        => ModSettings.TechnologySpreadModeText(mode);

    private static string CampaignEndDateModeText(bool enabled)
        => ModSettings.CampaignEndDateModeText(enabled);

    private static string MineWarfareModeText(bool disabled)
        => ModSettings.MineWarfareModeText(disabled);

    private static string SubmarineWarfareModeText(bool disabled)
        => ModSettings.SubmarineWarfareModeText(disabled);

    private static string CampaignMapWraparoundModeText(bool enabled)
        => enabled ? "Disc World" : "Flat Earth";

    private static string ExperimentalNationShipPaintsModeText(bool enabled)
        => ModSettings.ExperimentalNationShipPaintsModeText(enabled);

    private static string BattleRuntimeDiagnosticsModeText(bool enabled)
        => ModSettings.BattleRuntimeDiagnosticsModeText(enabled);

    private static void SetMenuButtonText(GameObject buttonObject, string text)
    {
        TMP_Text? tmp = Child(buttonObject, "Text (TMP)")?.GetComponent<TMP_Text>() ?? buttonObject.GetComponentInChildren<TMP_Text>();
        if (tmp != null)
        {
            RemoveComponent<LocalizeText>(tmp.gameObject);
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 13f;
            tmp.enableWordWrapping = false;
            return;
        }

        Text? uiText = buttonObject.GetComponentInChildren<Text>();
        if (uiText != null)
        {
            uiText.text = text;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.fontSize = 12;
        }
    }

    private static void CloseMenu()
    {
        ClosePaintPicker();
        if (menu != null)
            menu.SetActive(false);

        if (launcherButton != null)
            launcherButton.interactable = true;
    }

    private static GameObject? FindPath(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        GameObject? current = GameObject.Find(parts[0]);
        for (int i = 1; current != null && i < parts.Length; i++)
            current = Child(current, parts[i]);

        return current;
    }

    private static GameObject? Child(GameObject? parent, string name)
    {
        Transform? child = parent == null ? null : parent.transform.Find(name);
        return child == null ? null : child.gameObject;
    }

    private static void RemoveTooltipHandlers(GameObject target)
    {
        RemoveComponent<OnEnter>(target);
        RemoveComponent<OnLeave>(target);
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }

    // ---- Paint launcher + constructor panel + HSV color picker ----

    private static void SetupPaintLauncherButton()
    {
        GameObject? options = FindPath("Global/Ui/UiMain/Common/Options");
        GameObject? helpButton = FindPath("Global/Ui/UiMain/Common/Options/Help");
        if (options == null || helpButton == null)
            return;

        GameObject buttonObject = options.transform.Find(PaintLauncherButtonName)?.gameObject ?? UnityEngine.Object.Instantiate(helpButton);
        buttonObject.transform.SetParent(options.transform, false);
        buttonObject.name = PaintLauncherButtonName;
        buttonObject.SetActive(false);
        MatchButtonSizing(buttonObject, helpButton);
        RemoveTooltipHandlers(buttonObject);
        AddTooltip(buttonObject, () => $"UAD:VP Ship Paints{ConstructorPaintTooltipSuffix()}");

        Transform? imageChild = buttonObject.transform.Find("Image");
        if (imageChild != null && imageChild.TryGetComponent(out Image image))
        {
            paintLauncherImage = image;
            paintLauncherImage.sprite = EnsurePaintIconSprite();
            paintLauncherImage.preserveAspect = true;
            paintLauncherImage.color = Color.white;
            ScaleLauncherIcon(imageChild);
        }

        paintLauncherOutline = buttonObject.GetComponent<Outline>() ?? buttonObject.AddComponent<Outline>();
        paintLauncherOutline.effectDistance = new Vector2(1f, 1f);

        paintLauncherButton = buttonObject.GetComponent<Button>();
        if (paintLauncherButton != null)
        {
            paintLauncherButton.onClick.RemoveAllListeners();
            paintLauncherButton.onClick.AddListener(new System.Action(ToggleConstructorPaintPanel));
        }

        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP ship paints launcher button added.");
    }

    private static bool ShouldShowPaintLauncher()
        => initialized && ModSettings.ExperimentalNationShipPaintsEnabled && GameManager.IsConstructor;

    private static string ConstructorPaintTooltipSuffix()
    {
        if (!DesignHullColorProofPatch.TryResolveCurrentConstructorNation(out DesignHullColorProofPatch.NationPaintUiInfo info))
            return "\nThis nation has no built-in paint scheme.";
        return $"\nEditing: {info.Label}";
    }

    private static void RefreshPaintLauncherButton()
    {
        if (paintLauncherButton == null)
            return;

        GameObject buttonObject = paintLauncherButton.gameObject;
        bool show = ShouldShowPaintLauncher();
        if (buttonObject.activeSelf != show)
            buttonObject.SetActive(show);

        if (!show)
        {
            CloseConstructorPaintPanel();
            return;
        }

        paintLauncherButton.interactable = true;
        if (paintLauncherImage != null)
            paintLauncherImage.color = Color.white;
        if (paintLauncherOutline != null)
        {
            bool hasNation = DesignHullColorProofPatch.TryResolveCurrentConstructorNation(out _);
            paintLauncherOutline.effectColor = hasNation ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
        }
    }

    private static void ToggleConstructorPaintPanel()
    {
        if (constructorPaintPanel != null)
        {
            CloseConstructorPaintPanel();
            return;
        }
        OpenConstructorPaintPanel();
    }

    private static void OpenConstructorPaintPanel()
    {
        ClosePaintPicker();

        if (!DesignHullColorProofPatch.TryResolveCurrentConstructorNation(out DesignHullColorProofPatch.NationPaintUiInfo nation))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP ship paints: no matching nation paint scheme for the current player.");
            return;
        }

        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(nation.Key, out Dictionary<PaintArea, Color32> colors))
            return;

        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupRoot == null)
            return;

        panelNationKey = nation.Key;
        constructorPaintPanel = new GameObject("UADVP_ConstructorPaintPanel");
        constructorPaintPanel.transform.SetParent(popupRoot.transform, false);

        Image panelBg = constructorPaintPanel.AddComponent<Image>();
        panelBg.color = new Color(0f, 0f, 0f, 0.9f);
        panelBg.raycastTarget = true;
        Button panelBgButton = constructorPaintPanel.AddComponent<Button>();
        panelBgButton.targetGraphic = panelBg;

        RectTransform panelRect = constructorPaintPanel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.anchoredPosition = new Vector2(-18f, -90f);
        panelRect.sizeDelta = new Vector2(280f, 110f);

        VerticalLayoutGroup layout = constructorPaintPanel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = 12, right = 12, top = 10, bottom = 10 };
        layout.spacing = 8f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        GameObject headerRow = new("Header");
        headerRow.transform.SetParent(constructorPaintPanel.transform, false);
        Image headerImage = headerRow.AddComponent<Image>();
        headerImage.color = new Color(0f, 0f, 0f, 0f);
        headerImage.raycastTarget = false;
        HorizontalLayoutGroup headerLayout = headerRow.AddComponent<HorizontalLayoutGroup>();
        headerLayout.spacing = 8f;
        headerLayout.childAlignment = TextAnchor.MiddleLeft;
        headerLayout.childControlHeight = true;
        headerLayout.childControlWidth = true;
        headerLayout.childForceExpandHeight = false;
        headerLayout.childForceExpandWidth = false;
        AddLayout(headerRow, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);

        Text titleText = AddText(headerRow.transform, $"Ship Paints — {nation.Label}", 13, TextAnchor.MiddleLeft);
        AddLayout(titleText.gameObject, flexibleWidth: 1f);

        AddActionButton(headerRow.transform, "Reset", () => ResetNationShipPaintString(nation), width: 56f);
        AddActionButton(headerRow.transform, "Close", CloseConstructorPaintPanel, width: 56f);

        GameObject swatchRow = new("Swatches");
        swatchRow.transform.SetParent(constructorPaintPanel.transform, false);
        Image swatchRowImage = swatchRow.AddComponent<Image>();
        swatchRowImage.color = new Color(0f, 0f, 0f, 0f);
        swatchRowImage.raycastTarget = false;
        HorizontalLayoutGroup swatchLayout = swatchRow.AddComponent<HorizontalLayoutGroup>();
        swatchLayout.spacing = 4f;
        swatchLayout.childAlignment = TextAnchor.MiddleLeft;
        swatchLayout.childControlHeight = true;
        swatchLayout.childControlWidth = true;
        swatchLayout.childForceExpandHeight = false;
        swatchLayout.childForceExpandWidth = false;
        AddLayout(swatchRow, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        panelSwatches.Clear();
        foreach (PaintArea area in DesignHullColorProofPatch.AllPickerChannels)
        {
            if (!colors.TryGetValue(area, out Color32 channelColor))
                continue;
            Image swatchImage = AddPanelSwatch(swatchRow.transform, nation, area, channelColor);
            panelSwatches[area] = swatchImage;
        }

        constructorPaintPanel.transform.SetAsLastSibling();
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP ship paints panel opened for {nation.Label}.");
    }

    private static Image AddPanelSwatch(Transform parent, DesignHullColorProofPatch.NationPaintUiInfo nation, PaintArea channel, Color32 color)
    {
        // Compact, label-less swatch — tooltip identifies the channel.
        GameObject swatchObject = new($"UADVP_PanelSwatch_{channel}");
        swatchObject.transform.SetParent(parent, false);
        Image border = swatchObject.AddComponent<Image>();
        border.color = SwatchBorder;
        AddLayout(swatchObject, minWidth: 26f, preferredWidth: 26f, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 0f);

        GameObject fillObject = new("Fill");
        fillObject.transform.SetParent(swatchObject.transform, false);
        Image fill = fillObject.AddComponent<Image>();
        fill.color = color;
        fill.raycastTarget = false;
        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(1f, 1f);
        fillRect.offsetMax = new Vector2(-1f, -1f);

        Button button = swatchObject.AddComponent<Button>();
        button.targetGraphic = border;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(() => OpenPaintPicker(nation, channel)));

        AddTooltip(swatchObject, $"{nation.Label} — {ChannelLabel(channel)}\nCurrent: {HexFor(color)}\nClick to pick a color.");
        return fill;
    }

    private static void RefreshConstructorPaintPanel()
    {
        if (constructorPaintPanel == null)
            return;
        if (!ShouldShowPaintLauncher())
        {
            CloseConstructorPaintPanel();
            return;
        }
        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(panelNationKey, out Dictionary<PaintArea, Color32> colors))
            return;
        foreach (KeyValuePair<PaintArea, Image> entry in panelSwatches)
        {
            if (colors.TryGetValue(entry.Key, out Color32 c))
                entry.Value.color = c;
        }
    }

    private static void CloseConstructorPaintPanel()
    {
        // Only act if the panel actually exists; otherwise we would tear down a picker
        // that was opened from the regular options menu (this is called every Update).
        if (constructorPaintPanel == null)
            return;

        ClosePaintPicker();
        UnityEngine.Object.Destroy(constructorPaintPanel);
        constructorPaintPanel = null;
        panelSwatches.Clear();
        panelNationKey = string.Empty;
    }

    private static void OpenPaintPicker(DesignHullColorProofPatch.NationPaintUiInfo nation, PaintArea channel)
    {
        ClosePaintPicker();

        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(nation.Key, out Dictionary<PaintArea, Color32> colors))
            return;

        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupRoot == null)
            return;

        if (!colors.TryGetValue(channel, out Color32 current))
            current = new Color32(128, 128, 128, 255);

        pickerNation = nation;
        pickerChannel = channel;
        pickerOriginalChannelColor = current;

        // Seed HSV state from the channel's current RGB color.
        Color.RGBToHSV((Color)current, out pickerCurrentH, out pickerCurrentS, out pickerCurrentV);
        if (pickerCurrentV <= 0.001f)
        {
            pickerCurrentH = 0f;
            pickerCurrentS = 0f;
        }

        paintPicker = new GameObject("UADVP_PaintPicker");
        paintPicker.transform.SetParent(popupRoot.transform, false);

        Image backdropImage = paintPicker.AddComponent<Image>();
        backdropImage.color = PickerBackdrop;
        backdropImage.raycastTarget = true;
        RectTransform rootRect = paintPicker.GetComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        Button backdropButton = paintPicker.AddComponent<Button>();
        backdropButton.targetGraphic = backdropImage;
        backdropButton.onClick.AddListener(new System.Action(CancelPaintPicker));

        GameObject window = new("Window");
        window.transform.SetParent(paintPicker.transform, false);
        Image windowImage = window.AddComponent<Image>();
        windowImage.color = Background;
        windowImage.raycastTarget = true;
        Button windowButton = window.AddComponent<Button>();
        windowButton.targetGraphic = windowImage;
        RectTransform windowRect = window.GetComponent<RectTransform>();
        windowRect.anchorMin = new Vector2(1f, 0f);
        windowRect.anchorMax = new Vector2(1f, 0f);
        windowRect.pivot = new Vector2(1f, 0f);
        windowRect.anchoredPosition = new Vector2(-24f, 24f);
        windowRect.sizeDelta = new Vector2(260f, 360f);

        VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = 14, right = 14, top = 12, bottom = 12 };
        layout.spacing = 8f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        AddText(window.transform, $"{nation.Label} — {ChannelLabel(channel)}", 14, TextAnchor.MiddleLeft);
        AddText(window.transform, "Drag the wheel; tweak brightness below. Live.", 10, TextAnchor.MiddleLeft);

        AddColorWheel(window.transform);
        AddValueSlider(window.transform);

        GameObject previewRow = new("UADVP_PaintPreviewRow");
        previewRow.transform.SetParent(window.transform, false);
        Image previewRowImage = previewRow.AddComponent<Image>();
        previewRowImage.color = new Color(0f, 0f, 0f, 0f);
        previewRowImage.raycastTarget = false;
        HorizontalLayoutGroup previewLayout = previewRow.AddComponent<HorizontalLayoutGroup>();
        previewLayout.spacing = 10f;
        previewLayout.childAlignment = TextAnchor.MiddleLeft;
        previewLayout.childControlHeight = true;
        previewLayout.childControlWidth = true;
        previewLayout.childForceExpandHeight = false;
        previewLayout.childForceExpandWidth = false;
        AddLayout(previewRow, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        GameObject previewBox = new("Preview");
        previewBox.transform.SetParent(previewRow.transform, false);
        Image previewBorder = previewBox.AddComponent<Image>();
        previewBorder.color = SwatchBorder;
        AddLayout(previewBox, minWidth: 50f, preferredWidth: 50f, minHeight: 26f, preferredHeight: 26f);

        GameObject previewFillObject = new("Fill");
        previewFillObject.transform.SetParent(previewBox.transform, false);
        pickerPreviewFill = previewFillObject.AddComponent<Image>();
        pickerPreviewFill.color = current;
        pickerPreviewFill.raycastTarget = false;
        RectTransform previewFillRect = previewFillObject.GetComponent<RectTransform>();
        previewFillRect.anchorMin = Vector2.zero;
        previewFillRect.anchorMax = Vector2.one;
        previewFillRect.offsetMin = new Vector2(1f, 1f);
        previewFillRect.offsetMax = new Vector2(-1f, -1f);

        pickerHexInput = AddHexInput(previewRow.transform, HexFor(current), 86f);
        pickerHexInput.onEndEdit.AddListener(new System.Action<string>(OnPaintPickerHexEntered));

        AddPickerQuickSwatch(previewRow.transform, "BlackSwatch", new Color32(0, 0, 0, 255), () => SetPaintPickerHSV(0f, 0f, 0f), "Pure black (#000000)");
        AddPickerQuickSwatch(previewRow.transform, "WhiteSwatch", new Color32(255, 255, 255, 255), () => SetPaintPickerHSV(0f, 0f, 1f), "Pure white (#FFFFFF)");

        GameObject buttonsRow = new("UADVP_PaintPickerButtons");
        buttonsRow.transform.SetParent(window.transform, false);
        Image buttonsRowImage = buttonsRow.AddComponent<Image>();
        buttonsRowImage.color = new Color(0f, 0f, 0f, 0f);
        buttonsRowImage.raycastTarget = false;
        HorizontalLayoutGroup buttonsLayout = buttonsRow.AddComponent<HorizontalLayoutGroup>();
        buttonsLayout.spacing = 6f;
        buttonsLayout.childAlignment = TextAnchor.MiddleRight;
        buttonsLayout.childControlHeight = true;
        // Must control width or the popup-button template's native rect width wins,
        // ignoring the LayoutElement.preferredWidth we set in AddActionButton.
        buttonsLayout.childControlWidth = true;
        buttonsLayout.childForceExpandWidth = false;
        AddLayout(buttonsRow, minHeight: 28f, preferredHeight: 28f, flexibleWidth: 1f);

        AddActionButton(buttonsRow.transform, "Reset", LoadPaintPickerChannelDefault, width: 60f);
        AddActionButton(buttonsRow.transform, "Cancel", CancelPaintPicker, width: 60f);
        AddActionButton(buttonsRow.transform, "Done", ApplyPaintPicker, width: 60f);

        UpdatePickerVisualsFromHSV(commitLive: false);
        PositionWheelHandle();
        paintPicker.transform.SetAsLastSibling();
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP paint picker opened for {nation.Label} / {ChannelLabel(channel)} at {HexFor(current)}.");
    }

    private static void AddColorWheel(Transform parent)
    {
        // The picker's outer VerticalLayoutGroup expands child width, which would stretch the
        // wheel rect wider than the 180-px disc the sprite actually renders (preserveAspect) —
        // breaking both drag hit-testing and handle placement. So wrap in a centered row whose
        // children keep their LayoutElement width.
        GameObject centerRow = new("WheelCenterRow");
        centerRow.transform.SetParent(parent, false);
        Image centerImage = centerRow.AddComponent<Image>();
        centerImage.color = new Color(0f, 0f, 0f, 0f);
        centerImage.raycastTarget = false;
        HorizontalLayoutGroup centerLayout = centerRow.AddComponent<HorizontalLayoutGroup>();
        centerLayout.spacing = 0f;
        centerLayout.childAlignment = TextAnchor.MiddleCenter;
        centerLayout.childControlHeight = false;
        centerLayout.childControlWidth = false;
        centerLayout.childForceExpandHeight = false;
        centerLayout.childForceExpandWidth = false;
        AddLayout(centerRow, minHeight: 180f, preferredHeight: 180f, flexibleWidth: 1f);

        GameObject wheelContainer = new("ColorWheel");
        wheelContainer.transform.SetParent(centerRow.transform, false);
        pickerWheelImage = wheelContainer.AddComponent<Image>();
        pickerWheelImage.sprite = EnsureColorWheelSprite();
        pickerWheelImage.preserveAspect = true;
        pickerWheelImage.raycastTarget = true;
        AddLayout(wheelContainer, minWidth: 180f, preferredWidth: 180f, minHeight: 180f, preferredHeight: 180f, flexibleWidth: 0f);
        pickerWheelRect = wheelContainer.GetComponent<RectTransform>();
        pickerWheelRect.sizeDelta = new Vector2(180f, 180f);

        GameObject handle = new("WheelHandle");
        handle.transform.SetParent(wheelContainer.transform, false);
        Image handleImage = handle.AddComponent<Image>();
        handleImage.color = new Color(1f, 1f, 1f, 1f);
        handleImage.raycastTarget = false;
        pickerWheelHandle = handleImage;
        pickerWheelHandleRect = handle.GetComponent<RectTransform>();
        pickerWheelHandleRect.anchorMin = new Vector2(0.5f, 0.5f);
        pickerWheelHandleRect.anchorMax = new Vector2(0.5f, 0.5f);
        pickerWheelHandleRect.pivot = new Vector2(0.5f, 0.5f);
        pickerWheelHandleRect.sizeDelta = new Vector2(10f, 10f);

        Outline handleOutline = handle.AddComponent<Outline>();
        handleOutline.effectColor = new Color(0f, 0f, 0f, 1f);
        handleOutline.effectDistance = new Vector2(1f, 1f);
    }

    private static void AddValueSlider(Transform parent)
    {
        byte initial = (byte)Mathf.Clamp(Mathf.RoundToInt(pickerCurrentV * 255f), 0, 255);
        pickerValueSlider = AddColorSlider(parent, "V", initial, out pickerValueText);
        pickerValueSlider.onValueChanged.AddListener(new System.Action<float>(v =>
        {
            pickerCurrentV = Mathf.Clamp01(v / 255f);
            UpdatePickerVisualsFromHSV(commitLive: true);
        }));
    }

    private static Slider AddColorSlider(Transform parent, string channelLabel, byte initialValue, out Text valueText)
    {
        GameObject row = new($"UADVP_PaintSlider_{channelLabel}");
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = new Color(0f, 0f, 0f, 0f);
        rowImage.raycastTarget = false;
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);

        Text channelText = AddText(row.transform, channelLabel, 12, TextAnchor.MiddleLeft);
        AddLayout(channelText.gameObject, minWidth: 16f, preferredWidth: 16f, flexibleWidth: 0f);

        GameObject sliderObject = new("Slider");
        sliderObject.transform.SetParent(row.transform, false);
        Image sliderRaycast = sliderObject.AddComponent<Image>();
        sliderRaycast.color = new Color(0f, 0f, 0f, 0f);
        sliderRaycast.raycastTarget = true;
        AddLayout(sliderObject, minWidth: 150f, preferredWidth: 150f, minHeight: 20f, preferredHeight: 20f, flexibleWidth: 1f);

        GameObject background = new("Background");
        background.transform.SetParent(sliderObject.transform, false);
        Image backgroundImage = background.AddComponent<Image>();
        backgroundImage.color = SliderTrack;
        backgroundImage.raycastTarget = false;
        RectTransform backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0f, 0.25f);
        backgroundRect.anchorMax = new Vector2(1f, 0.75f);
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        GameObject fillArea = new("Fill Area");
        fillArea.transform.SetParent(sliderObject.transform, false);
        Image fillAreaImage = fillArea.AddComponent<Image>();
        fillAreaImage.color = new Color(0f, 0f, 0f, 0f);
        fillAreaImage.raycastTarget = false;
        RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.25f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.75f);
        fillAreaRect.offsetMin = new Vector2(8f, 0f);
        fillAreaRect.offsetMax = new Vector2(-8f, 0f);

        GameObject fillObject = new("Fill");
        fillObject.transform.SetParent(fillArea.transform, false);
        Image fillImage = fillObject.AddComponent<Image>();
        fillImage.color = SliderFill;
        fillImage.raycastTarget = false;
        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        GameObject handleArea = new("Handle Slide Area");
        handleArea.transform.SetParent(sliderObject.transform, false);
        Image handleAreaImage = handleArea.AddComponent<Image>();
        handleAreaImage.color = new Color(0f, 0f, 0f, 0f);
        handleAreaImage.raycastTarget = false;
        RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = new Vector2(8f, 0f);
        handleAreaRect.offsetMax = new Vector2(-8f, 0f);

        GameObject handleObject = new("Handle");
        handleObject.transform.SetParent(handleArea.transform, false);
        Image handleImage = handleObject.AddComponent<Image>();
        handleImage.color = SliderHandle;
        handleImage.raycastTarget = true;
        RectTransform handleRect = handleObject.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(14f, 20f);

        Slider slider = sliderObject.AddComponent<Slider>();
        slider.targetGraphic = handleImage;
        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 255f;
        slider.wholeNumbers = true;
        slider.value = initialValue;

        valueText = AddText(row.transform, "100%", 11, TextAnchor.MiddleRight);
        AddLayout(valueText.gameObject, minWidth: 38f, preferredWidth: 38f, flexibleWidth: 0f);

        return slider;
    }

    private static void PositionWheelHandle()
    {
        if (pickerWheelHandleRect == null || pickerWheelRect == null)
            return;

        // Use min(width, height) so an aspect-preserved sprite in a non-square rect still
        // matches the visible disc.
        float radius = Mathf.Min(pickerWheelRect.rect.width, pickerWheelRect.rect.height) * 0.5f;
        float angle = pickerCurrentH * (2f * Mathf.PI);
        float x = Mathf.Cos(angle) * pickerCurrentS * radius;
        float y = Mathf.Sin(angle) * pickerCurrentS * radius;
        pickerWheelHandleRect.anchoredPosition = new Vector2(x, y);
    }

    private static void UpdatePickerVisualsFromHSV(bool commitLive)
    {
        Color rgb = Color.HSVToRGB(pickerCurrentH, pickerCurrentS, pickerCurrentV);
        Color32 color = new(
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.g * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.b * 255f), 0, 255),
            byte.MaxValue);

        if (pickerPreviewFill != null)
            pickerPreviewFill.color = color;
        if (pickerHexInput != null && !pickerHexInput.isFocused)
            pickerHexInput.SetTextWithoutNotify(HexFor(color));
        if (pickerValueText != null)
            pickerValueText.text = Mathf.RoundToInt(pickerCurrentV * 100f) + "%";
        if (pickerWheelHandle != null)
        {
            float brightness = (color.r + color.g + color.b) / (3f * 255f);
            pickerWheelHandle.color = brightness > 0.55f ? new Color(0f, 0f, 0f, 1f) : new Color(1f, 1f, 1f, 1f);
        }

        PositionWheelHandle();

        if (commitLive)
            CommitPaintPickerColor(color);
    }

    private static void CommitPaintPickerColor(Color32 picked)
    {
        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> colors))
            return;

        colors[pickerChannel] = picked;
        string serialized = DesignHullColorProofPatch.BuildNationPaintString(colors);
        if (ModSettings.SetNationShipPaintString(pickerNation.Key, serialized, logChange: false))
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange("live picker drag");
    }

    private static void UpdatePaintPickerWheelDrag()
    {
        if (paintPicker == null || pickerWheelImage == null || pickerWheelRect == null)
        {
            pickerWheelDragging = false;
            return;
        }

        bool pressed = UnityEngine.Input.GetMouseButton(0);
        if (!pressed)
        {
            pickerWheelDragging = false;
            return;
        }

        Vector2 screenPoint = UnityEngine.Input.mousePosition;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(pickerWheelRect, screenPoint, null, out Vector2 local))
            return;

        float radius = Mathf.Min(pickerWheelRect.rect.width, pickerWheelRect.rect.height) * 0.5f;
        if (radius <= 0f)
            return;

        float dx = local.x / radius;
        float dy = local.y / radius;
        float distance = Mathf.Sqrt((dx * dx) + (dy * dy));

        if (!pickerWheelDragging)
        {
            // Only start a drag if the press landed inside the disc; once dragging, follow the cursor outside.
            if (distance > 1f)
                return;
            pickerWheelDragging = true;
        }

        float clamped = Mathf.Min(distance, 1f);
        float angle = Mathf.Atan2(dy, dx);
        float hue = angle / (2f * Mathf.PI);
        if (hue < 0f)
            hue += 1f;

        pickerCurrentH = hue;
        pickerCurrentS = clamped;
        UpdatePickerVisualsFromHSV(commitLive: true);
    }

    private static void ApplyPaintPicker()
    {
        if (DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> colors)
            && colors.TryGetValue(pickerChannel, out Color32 channelColor))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP option: Nation Ship Paints applied {pickerNation.Label} {ChannelLabel(pickerChannel)} = {HexFor(channelColor)}.");
        }

        ClosePaintPicker();
        if (menu != null && menu.activeInHierarchy)
            RefreshMenu();
        RefreshLauncherButton();
    }

    private static void CancelPaintPicker()
    {
        if (DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> colors))
        {
            colors[pickerChannel] = pickerOriginalChannelColor;
            string serialized = DesignHullColorProofPatch.BuildNationPaintString(colors);
            if (ModSettings.SetNationShipPaintString(pickerNation.Key, serialized, logChange: false))
                DesignHullColorProofPatch.ApplyNationPaintSettingsChange("picker cancel revert");
        }

        ClosePaintPicker();
        if (menu != null && menu.activeInHierarchy)
            RefreshMenu();
        RefreshLauncherButton();
    }

    private static void LoadPaintPickerChannelDefault()
    {
        if (!DesignHullColorProofPatch.TryGetAllDefaultNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> defaults))
            return;
        if (!defaults.TryGetValue(pickerChannel, out Color32 fallback))
            return;

        Color.RGBToHSV((Color)fallback, out pickerCurrentH, out pickerCurrentS, out pickerCurrentV);
        if (pickerCurrentV <= 0.001f)
        {
            pickerCurrentH = 0f;
            pickerCurrentS = 0f;
        }

        if (pickerValueSlider != null)
            pickerValueSlider.SetValueWithoutNotify(Mathf.Clamp(Mathf.RoundToInt(pickerCurrentV * 255f), 0, 255));

        UpdatePickerVisualsFromHSV(commitLive: true);
    }

    private static void ClosePaintPicker()
    {
        if (paintPicker != null)
        {
            UnityEngine.Object.Destroy(paintPicker);
            paintPicker = null;
        }

        pickerWheelImage = null;
        pickerWheelRect = null;
        pickerWheelHandle = null;
        pickerWheelHandleRect = null;
        pickerValueSlider = null;
        pickerPreviewFill = null;
        pickerHexInput = null;
        pickerValueText = null;
        pickerWheelDragging = false;
    }

    private static string ChannelLabel(PaintArea channel)
        => channel switch
        {
            PaintArea.HullSide => "Hull",
            PaintArea.Superstructure => "Super",
            PaintArea.Gun => "Guns",
            PaintArea.Barbette => "Barbette",
            PaintArea.Deck => "Deck",
            PaintArea.Bottom => "Bottom",
            // Roof is the internal name (token: roofing/roof); user-facing label is
            // "Details" because in practice the channel catches deck-fitting details.
            PaintArea.Roof => "Details",
            PaintArea.Barrel => "Barrel",
            _ => "Hull",
        };

    private static string HexFor(Color32 color)
        => $"#{color.r:X2}{color.g:X2}{color.b:X2}";

    private static void SetPaintPickerHSV(float h, float s, float v)
    {
        pickerCurrentH = h;
        pickerCurrentS = s;
        pickerCurrentV = v;
        if (pickerValueSlider != null)
            pickerValueSlider.SetValueWithoutNotify(Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255));
        UpdatePickerVisualsFromHSV(commitLive: true);
    }

    private static void OnPaintPickerHexEntered(string raw)
    {
        if (!TryParseHexInput(raw, out Color32 color))
        {
            // Invalid input — restore the field to the actual current hex.
            if (pickerHexInput != null)
            {
                Color rgb = Color.HSVToRGB(pickerCurrentH, pickerCurrentS, pickerCurrentV);
                Color32 currentColor = new(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.r * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.g * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.b * 255f), 0, 255),
                    byte.MaxValue);
                pickerHexInput.SetTextWithoutNotify(HexFor(currentColor));
            }
            return;
        }

        Color.RGBToHSV((Color)color, out float h, out float s, out float v);
        if (v <= 0.001f)
        {
            h = 0f;
            s = 0f;
        }
        SetPaintPickerHSV(h, s, v);
    }

    private static bool TryParseHexInput(string value, out Color32 color)
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

    private static InputField AddHexInput(Transform parent, string initialText, float width)
    {
        GameObject fieldObject = new("UADVP_HexInput");
        fieldObject.transform.SetParent(parent, false);
        Image image = fieldObject.AddComponent<Image>();
        image.color = new Color(0.04f, 0.04f, 0.04f, 0.95f);
        AddLayout(fieldObject, minWidth: width, preferredWidth: width, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 0f);

        InputField input = fieldObject.AddComponent<InputField>();
        input.targetGraphic = image;
        input.lineType = InputField.LineType.SingleLine;
        input.contentType = InputField.ContentType.Standard;
        input.characterValidation = InputField.CharacterValidation.None;
        input.characterLimit = 7;
        input.selectionColor = new Color(0.6f, 0.52f, 0.25f, 0.65f);

        Text text = AddInputText(fieldObject.transform, "Text", Color.white, initialText, 12);
        Text placeholder = AddInputText(fieldObject.transform, "Placeholder", new Color(0.72f, 0.72f, 0.68f, 0.72f), "#RRGGBB", 12);
        input.textComponent = text;
        input.placeholder = placeholder;
        input.text = initialText;
        return input;
    }

    private static Text AddInputText(Transform parent, string name, Color color, string text, int fontSize)
    {
        GameObject textObject = new(name);
        textObject.transform.SetParent(parent, false);
        Text uiText = textObject.AddComponent<Text>();
        uiText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        uiText.fontSize = fontSize;
        uiText.color = color;
        uiText.alignment = TextAnchor.MiddleLeft;
        uiText.text = text;
        uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
        uiText.verticalOverflow = VerticalWrapMode.Overflow;
        uiText.raycastTarget = false;

        RectTransform? rect = textObject.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 1f);
            rect.offsetMax = new Vector2(-6f, -1f);
        }
        return uiText;
    }

    private static Button AddPickerQuickSwatch(Transform parent, string name, Color32 color, Action onClick, string tooltip)
    {
        GameObject swatchObject = new(name);
        swatchObject.transform.SetParent(parent, false);
        Image border = swatchObject.AddComponent<Image>();
        border.color = SwatchBorder;
        AddLayout(swatchObject, minWidth: 24f, preferredWidth: 24f, minHeight: 24f, preferredHeight: 24f, flexibleWidth: 0f);

        GameObject fillObject = new("Fill");
        fillObject.transform.SetParent(swatchObject.transform, false);
        Image fill = fillObject.AddComponent<Image>();
        fill.color = color;
        fill.raycastTarget = false;
        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(1f, 1f);
        fillRect.offsetMax = new Vector2(-1f, -1f);

        Button button = swatchObject.AddComponent<Button>();
        button.targetGraphic = border;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(new System.Action(onClick));

        AddTooltip(swatchObject, tooltip);
        return button;
    }

    private static Sprite EnsurePaintIconSprite()
    {
        if (paintIconSprite != null)
            return paintIconSprite;

        const int size = 16;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            name = "UADVP_PaintIcon",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        Color32 red = new(220, 70, 70, 255);
        Color32 yellow = new(232, 198, 64, 255);
        Color32 green = new(78, 188, 96, 255);
        Color32 blue = new(72, 128, 224, 255);
        Color32 border = new(36, 36, 36, 255);

        Color32[] pixels = new Color32[size * size];
        int half = size / 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (x == 0 || y == 0 || x == size - 1 || y == size - 1)
                {
                    pixels[(y * size) + x] = border;
                    continue;
                }
                bool top = y >= half;
                bool right = x >= half;
                pixels[(y * size) + x] = (top, right) switch
                {
                    (true, false) => red,
                    (true, true) => yellow,
                    (false, false) => blue,
                    _ => green,
                };
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        paintIconSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        paintIconSprite.name = "UADVP_PaintIconSprite";
        return paintIconSprite;
    }

    private static Sprite EnsureColorWheelSprite()
    {
        if (colorWheelSprite != null)
            return colorWheelSprite;

        const int size = 180;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            name = "UADVP_ColorWheel",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };

        Color32[] pixels = new Color32[size * size];
        float center = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center) / center;
                float dy = (y - center) / center;
                float r = Mathf.Sqrt((dx * dx) + (dy * dy));
                int idx = (y * size) + x;
                if (r > 1f)
                {
                    pixels[idx] = new Color32(0, 0, 0, 0);
                    continue;
                }

                float angle = Mathf.Atan2(dy, dx);
                float h = angle / (2f * Mathf.PI);
                if (h < 0f) h += 1f;

                Color c = Color.HSVToRGB(h, r, 1f);
                byte a = r > 0.97f
                    ? (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(255f, 0f, (r - 0.97f) / 0.03f)), 0, 255)
                    : (byte)255;
                pixels[idx] = new Color32(
                    (byte)Mathf.RoundToInt(c.r * 255f),
                    (byte)Mathf.RoundToInt(c.g * 255f),
                    (byte)Mathf.RoundToInt(c.b * 255f),
                    a);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        colorWheelSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        colorWheelSprite.name = "UADVP_ColorWheelSprite";
        return colorWheelSprite;
    }
}
