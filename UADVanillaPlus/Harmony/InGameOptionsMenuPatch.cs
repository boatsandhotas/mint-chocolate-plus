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
        Maneuvers,
        Campaign,
        CampaignII,
        SwitchNation,
        ShipDesign,
        Experimental,
        NationShipPaints,
    }

    private static int switchNationIndex;
    private static bool switchNationArmed;

    private const string ButtonName = "UADVP_OptionsButton";
    private const string PaintLauncherButtonName = "UADVP_PaintLauncherButton";
    private const string ThemeLauncherButtonName = "UADVP_ThemeLauncherButton";
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
    private const string SmartAiDesignsOptionName = "UADVP_Option_SmartAiDesigns";
    private const string SharedDesignsUsageOptionName = "UADVP_Option_SharedDesignsUsage";
    private const string SmartRefitsOptionName = "UADVP_Option_SmartRefits";
    private const string SeaTransportLossesOptionName = "UADVP_Option_SeaTransportLosses";
    private const string AiTaskForceStagingOptionName = "UADVP_Option_AiTaskForceStaging";
    private const string CampaignNavalMobilityOptionName = "UADVP_Option_CampaignNavalMobility";
    private const string TaskForceSustainmentOptionName = "UADVP_Option_TaskForceSustainment";
    private const string HullSpeedAdjustmentOptionName = "UADVP_Option_HullSpeedAdjustment";
    private const string HullWeightAdjustmentOptionName = "UADVP_Option_HullWeightAdjustment";
    private const string MajorShipTorpedoesOptionName = "UADVP_Option_MajorShipTorpedoes";
    private const string MultiYearShipyardRebuildOptionName = "UADVP_Option_MultiYearShipyardRebuild";
    private const string AiEconomyPrioritiesOptionName = "UADVP_Option_AiEconomyPriorities";
    private const string ShipResupplyOverrideOptionName = "UADVP_Option_ShipResupplyOverride";
    private const string ShipServiceRecordsOptionName = "UADVP_Option_ShipServiceRecords";
    private const string RebuildOverseasWeightOptionName = "UADVP_Option_RebuildOverseasWeight";
    private const string VanquishedSpoilsOptionName = "UADVP_Option_VanquishedSpoils";
    private const string VanquishedSpoilsShareOptionName = "UADVP_Option_VanquishedSpoilsShare";
    private const string NavalReinforcementOptionName = "UADVP_Option_NavalReinforcement";
    private const string ClassNamingThemesOptionName = "UADVP_Option_ClassNamingThemes";
    private const string ShipbuildingCapacityBoostOptionName = "UADVP_Option_ShipbuildingCapacityBoost";
    private const string SurrenderedShipCaptureOptionName = "UADVP_Option_SurrenderedShipCapture";
    private const string BattleStartDefaultsOptionName = "UADVP_Option_BattleStartDefaults";
    private const string BattleStartAmmoOptionName = "UADVP_Option_BattleStartAmmo";
    private const string BattleStartAvoidTorpOptionName = "UADVP_Option_BattleStartAvoidTorp";
    private const string BattleStartAvoidShipOptionName = "UADVP_Option_BattleStartAvoidShip";
    private const string BattleStartAutoLeaderOptionName = "UADVP_Option_BattleStartAutoLeader";
    private const string BattleStartFireTorpOptionName = "UADVP_Option_BattleStartFireTorp";
    private const string BattleStartFormationOptionName = "UADVP_Option_BattleStartFormation";
    private const string BattleSpeedSyncOptionName = "UADVP_Option_BattleSpeedSync";
    private const string BattleReverseMethodOptionName = "UADVP_Option_BattleReverseMethod";
    private const string FollowSteerDampingOptionName = "UADVP_Option_FollowSteerDamping";
    private const string ParallelStationOptionName = "UADVP_Option_ParallelStation";
    private const string SuperstructureRefitsOptionName = "UADVP_Option_SuperstructureRefits";
    private const string ShipyardCapacityOptionName = "UADVP_Option_ShipyardCapacity";
    private const string ForeignPortCapacityOptionName = "UADVP_Option_ForeignPortCapacity";
    private const string ArmyLogisticsOptionName = "UADVP_Option_ArmyLogistics";
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
    private static Button? themeLauncherButton;
    private static GameObject? themePanel;
    private static string themePanelClass = string.Empty;
    private static string themePanelNation = string.Empty;
    private static List<NameThemeDatabase.ThemeInfo> themePanelThemes = new();
    private static int themePanelThemeIndex;
    private static int battleDefaultsTypeIndex;
    // Battle typed course/speed inputs — a single draggable, always-on-top "helm" panel.
    private static Ui? currentUi;
    private static GameObject? battlePanel;
    private static RectTransform? battlePanelRect;
    private static Canvas? battlePanelCanvas;
    private static Text? battleSpeedText;
    private static Text? battleCourseText;
    private static InputField? battleSpeedInput;
    private static InputField? battleCourseInput;
    private static bool battlePanelPosLoaded;
    private static int speedDiagTick;
    // Confirmed in-game 0.5.243 (UADVP_SPEEDDIAG): Ship.SpeedMax(false) is in m/s; displayed knots =
    // m/s * 1.94384. e.g. SpeedMax(false)=19.29 -> 37.5 kn, matching the HUD "38" and the speed slider's
    // own max (375 == 37.5 * 10). SpeedMax(true) is a larger INTERNAL unit (56.38) that savedCurrent/
    // DesiredSpeed are expressed in (they peg exactly at SpeedMax(true)).
    private const float MetersPerSecToKnots = 1.94384f;
    private static string lastHelmVisSig = string.Empty;
    // Auto-clipboard: the last course/speed the player SET on a division (captured when a
    // stably-selected division's assigned order changes — works for typed or click-set orders).
    // Right-clicking a row pastes the remembered value onto the currently selected division.
    private static float clipboardCourse = float.NaN;
    private static float clipboardSpeed = float.NaN;
    private static IntPtr trackedDivPtr = IntPtr.Zero;
    private static float trackedCourse = float.NaN;
    private static float trackedSpeed = float.NaN;
    private const string BattlePanelXKey = "uadvp_battle_panel_x";
    private const string BattlePanelYKey = "uadvp_battle_panel_y";
    private static bool loggedThemeLauncherNull;
    private static bool loggedThemeLauncherShow;
    private static readonly Dictionary<PaintArea, Image> panelSwatches = new();
    private static readonly Dictionary<PaintArea, Image> panelClassSwatches = new();
    private static string panelDesignKey = string.Empty;
    private static string panelDesignName = string.Empty;
    private static string panelNationKey = string.Empty;

    private static GameObject? paintPicker;
    private static Image? pickerWheelImage;
    private static RectTransform? pickerWheelRect;
    private static Image? pickerWheelHandle;
    private static RectTransform? pickerWheelHandleRect;
    private static Slider? pickerValueSlider;
    private static Image? pickerPreviewFill;
    private static InputField? pickerHexInput;
    // Root of the user-presets row inside the picker; held so we can rebuild
    // just that row after Save / shift-click-delete without rebuilding the
    // whole picker (which would lose the current wheel/slider state).
    private static GameObject? pickerUserPresetsRow;
    private static Text? pickerValueText;
    private static DesignHullColorProofPatch.NationPaintUiInfo pickerNation;
    private static PaintArea pickerChannel;
    private static Color32 pickerOriginalChannelColor;
    // When pickerDesignKey is non-empty, picker writes to the per-class override for that
    // design Guid; otherwise it writes to the nation override (existing behavior).
    private static string pickerDesignKey = string.Empty;
    private static string pickerDesignName = string.Empty;
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
    internal static void UpdatePostfix(Ui __instance)
    {
        currentUi = __instance;
        if (!initialized && Time.realtimeSinceStartup >= nextRetryTime)
        {
            nextRetryTime = Time.realtimeSinceStartup + 1f;
            TrySetup();
        }

        RefreshLauncherButton();
        RefreshPaintLauncherButton();
        RefreshThemeLauncherButton();
        BattleControlProbe.SampleIfBattle();
        BattleStartDefaults.ReapplyNewDivisions();
        BattleSpeedSync.Tick();
        BattleTurn.TryHotkey(currentUi);
        FollowSteerProbe.Tick(currentUi);
        DesignStateProbe.Tick();
        FormationProbe.Tick();
        ParallelOrder.Tick(currentUi);
        TrySetupBattleInputs();
        RefreshBattleInputs();
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
            // Isolate each optional launcher so one failing setup never blocks the next.
            try { SetupPaintLauncherButton(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP paint launcher setup skipped. {ex.GetType().Name}: {ex.Message}"); }
            try { SetupThemeLauncherButton(); }
            catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP theme launcher setup skipped. {ex.GetType().Name}: {ex.Message}"); }
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
        AddSectionButton(sections.transform, Section.Maneuvers, "Maneuvers");
        AddSectionButton(sections.transform, Section.Campaign, "Campaign");
        AddSectionButton(sections.transform, Section.CampaignII, "Campaign II");
        AddSectionButton(sections.transform, Section.SwitchNation, "Switch Nation");
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
                AddSegmentedOption(
                    pane.transform,
                    SurrenderedShipCaptureOptionName,
                    "Capture Surrendered Ships",
                    "At campaign battle end the victory-points winner takes all surrendered ships — captures the loser's and recovers its own, towed to a winner port. Applies to you whether you win or lose. Vanilla leaves surrendered ships as losses.",
                    true,
                    ("On", ModSettings.SurrenderedShipCaptureEnabled, () => SetSurrenderedShipCaptureMode(true)),
                    ("Vanilla", !ModSettings.SurrenderedShipCaptureEnabled, () => SetSurrenderedShipCaptureMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    BattleSpeedSyncOptionName,
                    "Division Speed Sync",
                    "When a division leader has a manual speed order, its followers match that speed (capped at their own max) instead of running at full speed and circling back into line. Off keeps vanilla.",
                    true,
                    ("On", ModSettings.BattleSpeedSyncEnabled, () => SetBattleSpeedSyncMode(true)),
                    ("Off", !ModSettings.BattleSpeedSyncEnabled, () => SetBattleSpeedSyncMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    BattleStartDefaultsOptionName,
                    "Battle Start Defaults",
                    "Auto-apply your preferred per-class ship settings (below) when each battle begins, so you don't redo them every fight. Off keeps vanilla.",
                    true,
                    ("On", ModSettings.BattleStartDefaultsEnabled, () => SetBattleStartDefaultsMode(true)),
                    ("Off", !ModSettings.BattleStartDefaultsEnabled, () => SetBattleStartDefaultsMode(false)));
                if (ModSettings.BattleStartDefaultsEnabled)
                {
                    AddBattleDefaultsClassRow(pane.transform);
                    string bsType = CurrentBattleDefaultsType();
                    string bsLabel = BattleStartDefaults.Types[battleDefaultsTypeIndex].Label;
                    AddSegmentedOption(
                        pane.transform, BattleStartAmmoOptionName, $"  • {bsLabel} Ammo",
                        "Default shell type (main + secondary) for this class at battle start. Leave keeps each ship's current selection.",
                        true,
                        ("Leave", BattleStartDefaults.GetAmmo(bsType) == ModSettings.BattleAmmoMode.Leave, () => SetBattleStartAmmo(ModSettings.BattleAmmoMode.Leave)),
                        ("Auto", BattleStartDefaults.GetAmmo(bsType) == ModSettings.BattleAmmoMode.Auto, () => SetBattleStartAmmo(ModSettings.BattleAmmoMode.Auto)),
                        ("AP", BattleStartDefaults.GetAmmo(bsType) == ModSettings.BattleAmmoMode.AP, () => SetBattleStartAmmo(ModSettings.BattleAmmoMode.AP)),
                        ("HE", BattleStartDefaults.GetAmmo(bsType) == ModSettings.BattleAmmoMode.HE, () => SetBattleStartAmmo(ModSettings.BattleAmmoMode.HE)));
                    AddSegmentedOption(
                        pane.transform, BattleStartAvoidTorpOptionName, $"  • {bsLabel} Avoid Torpedoes",
                        "This class's divisions get this Avoid Torpedoes order at battle start. Leave keeps vanilla.",
                        true,
                        ("Leave", BattleStartDefaults.GetAvoidTorp(bsType) == ModSettings.BattleToggle.Leave, () => SetBattleStartAvoidTorp(ModSettings.BattleToggle.Leave)),
                        ("On", BattleStartDefaults.GetAvoidTorp(bsType) == ModSettings.BattleToggle.On, () => SetBattleStartAvoidTorp(ModSettings.BattleToggle.On)),
                        ("Off", BattleStartDefaults.GetAvoidTorp(bsType) == ModSettings.BattleToggle.Off, () => SetBattleStartAvoidTorp(ModSettings.BattleToggle.Off)));
                    AddSegmentedOption(
                        pane.transform, BattleStartAvoidShipOptionName, $"  • {bsLabel} Avoid Ships",
                        "This class's divisions get this Avoid Collisions order at battle start. Leave keeps vanilla.",
                        true,
                        ("Leave", BattleStartDefaults.GetAvoidShip(bsType) == ModSettings.BattleToggle.Leave, () => SetBattleStartAvoidShip(ModSettings.BattleToggle.Leave)),
                        ("On", BattleStartDefaults.GetAvoidShip(bsType) == ModSettings.BattleToggle.On, () => SetBattleStartAvoidShip(ModSettings.BattleToggle.On)),
                        ("Off", BattleStartDefaults.GetAvoidShip(bsType) == ModSettings.BattleToggle.Off, () => SetBattleStartAvoidShip(ModSettings.BattleToggle.Off)));
                    AddSegmentedOption(
                        pane.transform, BattleStartAutoLeaderOptionName, $"  • {bsLabel} Auto Group Leader",
                        "This class's divisions get this automatic group-leader change at battle start. Leave keeps vanilla.",
                        true,
                        ("Leave", BattleStartDefaults.GetAutoLeader(bsType) == ModSettings.BattleToggle.Leave, () => SetBattleStartAutoLeader(ModSettings.BattleToggle.Leave)),
                        ("On", BattleStartDefaults.GetAutoLeader(bsType) == ModSettings.BattleToggle.On, () => SetBattleStartAutoLeader(ModSettings.BattleToggle.On)),
                        ("Off", BattleStartDefaults.GetAutoLeader(bsType) == ModSettings.BattleToggle.Off, () => SetBattleStartAutoLeader(ModSettings.BattleToggle.Off)));
                    AddSegmentedOption(
                        pane.transform, BattleStartFireTorpOptionName, $"  • {bsLabel} Torpedoes",
                        "This class's ships get this torpedo firing mode at battle start (On = fire, Off = hold). Leave keeps vanilla.",
                        true,
                        ("Leave", BattleStartDefaults.GetFireTorp(bsType) == ModSettings.BattleToggle.Leave, () => SetBattleStartFireTorp(ModSettings.BattleToggle.Leave)),
                        ("On", BattleStartDefaults.GetFireTorp(bsType) == ModSettings.BattleToggle.On, () => SetBattleStartFireTorp(ModSettings.BattleToggle.On)),
                        ("Off", BattleStartDefaults.GetFireTorp(bsType) == ModSettings.BattleToggle.Off, () => SetBattleStartFireTorp(ModSettings.BattleToggle.Off)));
                    AddSegmentedOption(
                        pane.transform, BattleStartFormationOptionName, $"  • {bsLabel} Formation",
                        "This class's divisions get this formation at battle start. Leave keeps vanilla.",
                        true,
                        ("Leave", BattleStartDefaults.GetFormation(bsType) == ModSettings.BattleFormation.Leave, () => SetBattleStartFormation(ModSettings.BattleFormation.Leave)),
                        ("Column", BattleStartDefaults.GetFormation(bsType) == ModSettings.BattleFormation.Column, () => SetBattleStartFormation(ModSettings.BattleFormation.Column)),
                        ("Line", BattleStartDefaults.GetFormation(bsType) == ModSettings.BattleFormation.Line, () => SetBattleStartFormation(ModSettings.BattleFormation.Line)));
                }
                break;
            case Section.Maneuvers:
                AddSegmentedOption(
                    pane.transform,
                    BattleReverseMethodOptionName,
                    "Reverse-Course Method (R/T)",
                    "How the R (port) / T (starboard) hotkeys turn a selected division 180. 180 = single command, rear becomes lead. 90·90 = turn 90, swap the column once turning, then finish 90. Split = each ship breaks into its own division and pivots at the same instant (true simultaneous), then rejoins reversed. Rudder = direct hard-over (experimental). Split/Rudder fall back to 90·90 if the maneuver can't start.",
                    true,
                    ("180", ModSettings.BattleReverseMethod == ModSettings.BattleTurnMethod.Single180, () => SetBattleReverseMethod(ModSettings.BattleTurnMethod.Single180)),
                    ("90·90", ModSettings.BattleReverseMethod == ModSettings.BattleTurnMethod.NinetySwapNinety, () => SetBattleReverseMethod(ModSettings.BattleTurnMethod.NinetySwapNinety)),
                    ("Split", ModSettings.BattleReverseMethod == ModSettings.BattleTurnMethod.SplitRejoin, () => SetBattleReverseMethod(ModSettings.BattleTurnMethod.SplitRejoin)),
                    ("Rudder", ModSettings.BattleReverseMethod == ModSettings.BattleTurnMethod.Rudder, () => SetBattleReverseMethod(ModSettings.BattleTurnMethod.Rudder)));
                AddSegmentedOption(
                    pane.transform,
                    FollowSteerDampingOptionName,
                    "Follow Steering Damping (exp.)",
                    "Experimental: damps the per-frame yaw rate of division followers to reduce the S-pattern weave that fast, slow-rudder ships show while keeping station. Off keeps vanilla follow steering. Requires Battle Runtime Diagnostics on to log its effect.",
                    true,
                    ("On", ModSettings.FollowSteerDampingEnabled, () => SetFollowSteerDamping(true)),
                    ("Off", !ModSettings.FollowSteerDampingEnabled, () => SetFollowSteerDamping(false)));
                AddSegmentedOption(
                    pane.transform,
                    ParallelStationOptionName,
                    "Parallel Station",
                    "Where the Parallel order (order-bar button / Shift+P) places a division relative to its tagged anchor. Astern = behind and on the side away from the enemy (a trailing screen — e.g. DDs lurking for a torpedo run). Abreast = beside the anchor on the beam (parallel battle lines; chain divisions for 2–3 columns).",
                    true,
                    ("Astern", !ModSettings.ParallelStationAbreast, () => SetParallelStation(false)),
                    ("Abreast", ModSettings.ParallelStationAbreast, () => SetParallelStation(true)));
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
                    SeaTransportLossesOptionName,
                    "Sea Transport Losses",
                    "Active Forces ignores task forces merely transiting through a sea region when calculating abstract sea-zone transport losses. Vanilla counts the game's original area-vessel list.",
                    true,
                    ("Active Forces", ModSettings.SeaTransportLossesActiveForcesEnabled, () => SetSeaTransportLossesMode(true)),
                    ("Vanilla", !ModSettings.SeaTransportLossesActiveForcesEnabled, () => SetSeaTransportLossesMode(false)));
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
                    AiEconomyPrioritiesOptionName,
                    "AI Economy Priorities",
                    "Makes AI majors fund their economy sensibly: transport/merchant capacity up to 200% first, then technology, then crew training (reallocating their own naval budget, never overspending). Fixes AI nations starving their transport (and economy). Vanilla keeps the original AI budget split.",
                    true,
                    ("On", ModSettings.AiEconomyPrioritiesEnabled, () => SetAiEconomyPriorities(true)),
                    ("Vanilla", !ModSettings.AiEconomyPrioritiesEnabled, () => SetAiEconomyPriorities(false)));
                AddSegmentedOption(
                    pane.transform,
                    ShipResupplyOverrideOptionName,
                    "Ship Resupply Override",
                    "Debug: manually refuel and rearm your ships — for task forces stranded at sea that aren't replenishing. Off keeps vanilla supply.",
                    true,
                    ("On", ModSettings.ShipResupplyOverrideEnabled, () => SetShipResupplyOverride(true)),
                    ("Off", !ModSettings.ShipResupplyOverrideEnabled, () => SetShipResupplyOverride(false)));
                if (ModSettings.ShipResupplyOverrideEnabled)
                    AddActionButton(pane.transform, "Resupply My Fleet Now", () => ResupplyOverride.ResupplyAll(), 220f);
                AddSegmentedOption(
                    pane.transform,
                    ShipServiceRecordsOptionName,
                    "Ship Service Records",
                    "Records each of your ships' battle history — damage dealt/received, ships sunk and wrecked, survived/lost — per campaign. Data is captured now; the records viewer is coming.",
                    true,
                    ("On", ModSettings.ShipServiceRecordsEnabled, () => SetShipServiceRecords(true)),
                    ("Off", !ModSettings.ShipServiceRecordsEnabled, () => SetShipServiceRecords(false)));
                if (ModSettings.ShipServiceRecordsEnabled)
                    AddActionButton(pane.transform, "Open Ship Records (F10)", () => ShipRecordsViewer.Toggle(), 220f);
                AddSegmentedOption(
                    pane.transform,
                    MultiYearShipyardRebuildOptionName,
                    "Shipyard Rebuild on Conquest",
                    "On ties national shipbuilding capacity to territory: capturing a province takes its proportional share of the loser's shipyard and rebuilds it for the captor over a development-scaled few years. Vanilla leaves shipyard capacity unchanged when territory changes hands.",
                    true,
                    ("On", ModSettings.MultiYearShipyardRebuildEnabled, () => SetMultiYearShipyardRebuildMode(true)),
                    ("Vanilla", !ModSettings.MultiYearShipyardRebuildEnabled, () => SetMultiYearShipyardRebuildMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    RebuildOverseasWeightOptionName,
                    "Overseas Capacity Weight",
                    "How much overseas/colonial territory counts toward shipbuilding capacity versus home territory. Low makes colonies nearly irrelevant to shipbuilding; High makes them count nearly as much as the homeland.",
                    true,
                    ("Low", ModSettings.RebuildOverseasWeightLevel == ModSettings.LevelSetting.Low, () => SetRebuildOverseasWeight(ModSettings.LevelSetting.Low)),
                    ("Medium", ModSettings.RebuildOverseasWeightLevel == ModSettings.LevelSetting.Medium, () => SetRebuildOverseasWeight(ModSettings.LevelSetting.Medium)),
                    ("High", ModSettings.RebuildOverseasWeightLevel == ModSettings.LevelSetting.High, () => SetRebuildOverseasWeight(ModSettings.LevelSetting.High)));
                AddSegmentedOption(
                    pane.transform,
                    VanquishedSpoilsOptionName,
                    "Vanquished Spoils",
                    "On distributes a fully-conquered major's surviving fleet and a cash indemnity to the victors, instead of vanilla scrapping the fleet and stranding the treasury. Vanilla keeps the original behavior.",
                    true,
                    ("On", ModSettings.VanquishedSpoilsEnabled, () => SetVanquishedSpoilsMode(true)),
                    ("Vanilla", !ModSettings.VanquishedSpoilsEnabled, () => SetVanquishedSpoilsMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    VanquishedSpoilsShareOptionName,
                    "Vanquished Spoils Share",
                    "How much of a defeated nation's fleet and treasury the victors receive. Low scuttles more of the fleet and seizes less cash; High transfers more of both.",
                    true,
                    ("Low", ModSettings.VanquishedSpoilsShareLevel == ModSettings.LevelSetting.Low, () => SetVanquishedSpoilsShare(ModSettings.LevelSetting.Low)),
                    ("Medium", ModSettings.VanquishedSpoilsShareLevel == ModSettings.LevelSetting.Medium, () => SetVanquishedSpoilsShare(ModSettings.LevelSetting.Medium)),
                    ("High", ModSettings.VanquishedSpoilsShareLevel == ModSettings.LevelSetting.High, () => SetVanquishedSpoilsShare(ModSettings.LevelSetting.High)));
                AddSegmentedOption(
                    pane.transform,
                    NavalReinforcementOptionName,
                    "Naval Reinforcement",
                    "Reinforce with Navy: naval tonnage parked in a land battle's target waters adds army force to that battle (attacking OR defending your own coast). Higher settings add more force per ton — a large fleet commitment scales all the way up, no cap. Off disables it.",
                    true,
                    ("Off", ModSettings.NavalReinforcement == ModSettings.NavalReinforcementMode.Off, () => SetNavalReinforcement(ModSettings.NavalReinforcementMode.Off)),
                    ("Modest", ModSettings.NavalReinforcement == ModSettings.NavalReinforcementMode.Modest, () => SetNavalReinforcement(ModSettings.NavalReinforcementMode.Modest)),
                    ("Strong", ModSettings.NavalReinforcement == ModSettings.NavalReinforcementMode.Strong, () => SetNavalReinforcement(ModSettings.NavalReinforcementMode.Strong)),
                    ("Decisive", ModSettings.NavalReinforcement == ModSettings.NavalReinforcementMode.Decisive, () => SetNavalReinforcement(ModSettings.NavalReinforcementMode.Decisive)));
                AddSegmentedOption(
                    pane.transform,
                    ClassNamingThemesOptionName,
                    "Class Naming Themes",
                    "On shows a theme button in the ship constructor: assign a naming theme to a class and new ships of that class draw from that name pool (or a sequential <Class>-N scheme) instead of the generic per-nation list. Off hides the button and uses vanilla naming.",
                    true,
                    ("On", ModSettings.ClassNamingThemesEnabled, () => SetClassNamingThemesMode(true)),
                    ("Off", !ModSettings.ClassNamingThemesEnabled, () => SetClassNamingThemesMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    ShipbuildingCapacityBoostOptionName,
                    "Shipbuilding Capacity",
                    "Multiplies every nation's total shipbuilding capacity (the home-port-derived limit) so all players can build more tonnage at once. Vanilla keeps the game's limit.",
                    true,
                    ("Vanilla", ModSettings.ShipbuildingCapacityBoost == ModSettings.ShipbuildingCapacityBoostMode.Vanilla, () => SetShipbuildingCapacityBoost(ModSettings.ShipbuildingCapacityBoostMode.Vanilla)),
                    ("1.5x", ModSettings.ShipbuildingCapacityBoost == ModSettings.ShipbuildingCapacityBoostMode.Plus50, () => SetShipbuildingCapacityBoost(ModSettings.ShipbuildingCapacityBoostMode.Plus50)),
                    ("2x", ModSettings.ShipbuildingCapacityBoost == ModSettings.ShipbuildingCapacityBoostMode.Double, () => SetShipbuildingCapacityBoost(ModSettings.ShipbuildingCapacityBoostMode.Double)),
                    ("3x", ModSettings.ShipbuildingCapacityBoost == ModSettings.ShipbuildingCapacityBoostMode.Triple, () => SetShipbuildingCapacityBoost(ModSettings.ShipbuildingCapacityBoostMode.Triple)));
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
                    SmartAiDesignsOptionName,
                    "Smart AI Designs",
                    "Experimental replaces vanilla's random AI new-design fallback with one deterministic VP ship-design attempt after shared and predefined designs fail. Vanilla keeps the game's original random fallback.",
                    true,
                    ("Experimental", ModSettings.SmartAiDesignsEnabled, () => SetSmartAiDesignsMode(true)),
                    ("Vanilla", !ModSettings.SmartAiDesignsEnabled, () => SetSmartAiDesignsMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    SharedDesignsUsageOptionName,
                    "Shared Designs",
                    "Changes the active campaign shared-design setting for future AI designs. Only uses shared designs for future AI designs and blocks random AI fallback when no shared design is accepted. Existing designs are not altered.",
                    CampaignSharedDesignUsageSettings.HasActiveCampaign,
                    ("Off", CampaignSharedDesignUsageSettings.CurrentPolicy == CampaignSharedDesignUsageSettings.SharedDesignPolicy.Off, () => SetSharedDesignsUsageMode(CampaignSharedDesignUsageSettings.SharedDesignPolicy.Off)),
                    ("Selective", CampaignSharedDesignUsageSettings.CurrentPolicy == CampaignSharedDesignUsageSettings.SharedDesignPolicy.Selective, () => SetSharedDesignsUsageMode(CampaignSharedDesignUsageSettings.SharedDesignPolicy.Selective)),
                    ("Always", CampaignSharedDesignUsageSettings.CurrentPolicy == CampaignSharedDesignUsageSettings.SharedDesignPolicy.Always, () => SetSharedDesignsUsageMode(CampaignSharedDesignUsageSettings.SharedDesignPolicy.Always)),
                    ("Only", CampaignSharedDesignUsageSettings.CurrentPolicy == CampaignSharedDesignUsageSettings.SharedDesignPolicy.Only, () => SetSharedDesignsUsageMode(CampaignSharedDesignUsageSettings.SharedDesignPolicy.Only)));
                AddSegmentedOption(
                    pane.transform,
                    SmartRefitsOptionName,
                    "Smart Refits",
                    "Enhanced replaces vanilla AI random refits with VP's conservative refit pass and enables the player Smart Refit constructor button. Vanilla restores the game's original AI refit path and hides the VP button.",
                    true,
                    ("Enhanced", ModSettings.SmartRefitsEnabled, () => SetSmartRefitsMode(true)),
                    ("Vanilla", !ModSettings.SmartRefitsEnabled, () => SetSmartRefitsMode(false)));
                break;
            case Section.CampaignII:
                AddSegmentedOption(
                    pane.transform,
                    CampaignNavalMobilityOptionName,
                    "Campaign Naval Mobility",
                    "Controls campaign task-force movement and the supply-distance envelope. Extended makes task forces move about 2.7x vanilla per month. Vanilla restores the game's original monthly movement scale.",
                    true,
                    ("Extended", ModSettings.CampaignNavalMobility == ModSettings.CampaignNavalMobilityMode.Extended, () => SetCampaignNavalMobilityMode(ModSettings.CampaignNavalMobilityMode.Extended)),
                    ("Fast", ModSettings.CampaignNavalMobility == ModSettings.CampaignNavalMobilityMode.Fast, () => SetCampaignNavalMobilityMode(ModSettings.CampaignNavalMobilityMode.Fast)),
                    ("Improved", ModSettings.CampaignNavalMobility == ModSettings.CampaignNavalMobilityMode.Improved, () => SetCampaignNavalMobilityMode(ModSettings.CampaignNavalMobilityMode.Improved)),
                    ("Vanilla", ModSettings.CampaignNavalMobility == ModSettings.CampaignNavalMobilityMode.Vanilla, () => SetCampaignNavalMobilityMode(ModSettings.CampaignNavalMobilityMode.Vanilla)));
                AddSegmentedOption(
                    pane.transform,
                    TaskForceSustainmentOptionName,
                    "Task Force Sustainment",
                    "Full keeps campaign task forces supplied and tops off campaign fuel and ammunition at movement, maintenance, and battle boundaries. Vanilla keeps the game's original campaign supply, fuel, and ammunition attrition.",
                    true,
                    ("Full", ModSettings.TaskForceSustainmentFullEnabled, () => SetTaskForceSustainmentMode(true)),
                    ("Vanilla", !ModSettings.TaskForceSustainmentFullEnabled, () => SetTaskForceSustainmentMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    AiTaskForceStagingOptionName,
                    "AI Task Force Staging",
                    "Staging lets AI task forces heading to the same theater pause and rendezvous before battle generation. Vanilla keeps the game's original piecemeal task-force dispatch.",
                    true,
                    ("Staging", ModSettings.AiTaskForceStagingEnabled, () => SetAiTaskForceStagingMode(true)),
                    ("Vanilla", !ModSettings.AiTaskForceStagingEnabled, () => SetAiTaskForceStagingMode(false)));
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
                    ForeignPortCapacityOptionName,
                    "Foreign Port Capacity",
                    "50% lets controlled non-home ports contribute half of their normal port-capacity share to national shipbuilding capacity. Vanilla counts only home ports.",
                    true,
                    ("50%", ModSettings.ForeignPortCapacity == ModSettings.ForeignPortCapacityMode.Half, () => SetForeignPortCapacityMode(ModSettings.ForeignPortCapacityMode.Half)),
                    ("Vanilla", ModSettings.ForeignPortCapacity == ModSettings.ForeignPortCapacityMode.Vanilla, () => SetForeignPortCapacityMode(ModSettings.ForeignPortCapacityMode.Vanilla)));
                AddSegmentedOption(
                    pane.transform,
                    ArmyLogisticsOptionName,
                    "Army Logistics",
                    "Balanced bases army logistics on transport capacity and navy coverage of the national footprint. Vanilla keeps the game's budget/population formula and random non-major rolls.",
                    true,
                    ("Balanced", ModSettings.ArmyLogistics == ModSettings.ArmyLogisticsMode.Balanced, () => SetArmyLogisticsMode(ModSettings.ArmyLogisticsMode.Balanced)),
                    ("Vanilla", ModSettings.ArmyLogistics == ModSettings.ArmyLogisticsMode.Vanilla, () => SetArmyLogisticsMode(ModSettings.ArmyLogisticsMode.Vanilla)));
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
            case Section.SwitchNation:
            {
                var targets = PlayerSwap.SwitchTargets();
                if (targets.Count == 0)
                {
                    AddText(pane.transform, "Available on the campaign map only (not in battle), and only when other major nations exist.", 13, TextAnchor.MiddleLeft);
                    switchNationArmed = false;
                    break;
                }
                switchNationIndex = Mathf.Clamp(switchNationIndex, 0, targets.Count - 1);
                Player tgt = targets[switchNationIndex];
                string tgtName = PlayerSwap.NationLabel(tgt);
                string curName = PlayerSwap.NationLabel(PlayerSwap.CurrentHuman());
                int count = targets.Count;

                AddText(pane.transform,
                    $"Switch which nation you control. Your current nation ({curName}) is handed to the AI; you take over the selected nation and fight on as it. This SAVES and returns to the main menu — click Continue to resume as the new nation.",
                    12, TextAnchor.UpperLeft);

                GameObject row = new("UADVP_SwitchNationRow");
                row.transform.SetParent(pane.transform, false);
                Image bg = row.AddComponent<Image>();
                bg.color = RowBackground;
                HorizontalLayoutGroup hl = row.AddComponent<HorizontalLayoutGroup>();
                hl.padding = new RectOffset { left = 8, right = 8, top = 4, bottom = 4 };
                hl.spacing = 8f;
                hl.childAlignment = TextAnchor.MiddleLeft;
                hl.childControlHeight = true;
                hl.childControlWidth = true;
                hl.childForceExpandHeight = false;
                hl.childForceExpandWidth = false;
                AddLayout(row, minHeight: 34f, preferredHeight: 34f, flexibleWidth: 1f);
                Text rl = AddText(row.transform, "Nation", 13, TextAnchor.MiddleLeft);
                AddLayout(rl.gameObject, minWidth: 110f, flexibleWidth: 1f);
                AddActionButton(row.transform, "<", () => { switchNationIndex = (switchNationIndex - 1 + count) % count; switchNationArmed = false; RefreshMenu(); }, 56f);
                Text rc = AddText(row.transform, tgtName, 14, TextAnchor.MiddleCenter);
                AddLayout(rc.gameObject, minWidth: 150f);
                AddActionButton(row.transform, ">", () => { switchNationIndex = (switchNationIndex + 1) % count; switchNationArmed = false; RefreshMenu(); }, 56f);

                if (!switchNationArmed)
                {
                    AddActionButton(pane.transform, $"Become {tgtName}…", () => { switchNationArmed = true; RefreshMenu(); }, 220f);
                }
                else
                {
                    AddText(pane.transform, $"Confirm: become {tgtName}? Saves and drops to the main menu — then click Continue.", 13, TextAnchor.MiddleLeft);
                    AddActionButton(pane.transform, $"CONFIRM — Become {tgtName}", () => { switchNationArmed = false; PlayerSwap.SwitchTo(tgt); }, 250f);
                    AddActionButton(pane.transform, "Cancel", () => { switchNationArmed = false; RefreshMenu(); }, 120f);
                }
                break;
            }
            case Section.ShipDesign:
                AddSegmentedOption(
                    pane.transform,
                    HullSpeedAdjustmentOptionName,
                    "Hull Speed Adjustment",
                    "Adjusted lowers early TB/DD hull speed limits and delays one oversized early TB dual funnel until the historical small-funnel unlock. Vanilla restores the game's original hull speed and funnel availability.",
                    true,
                    ("Adjusted", ModSettings.HullSpeedAdjustmentEnabled, () => SetHullSpeedAdjustmentMode(true)),
                    ("Vanilla", !ModSettings.HullSpeedAdjustmentEnabled, () => SetHullSpeedAdjustmentMode(false)));
                AddSegmentedOption(
                    pane.transform,
                    HullWeightAdjustmentOptionName,
                    "Hull Weight Adjustment",
                    "Adjusted caps excessive hull mass ratios by ship class while preserving vanilla ratios below the cap. Vanilla restores the game's original hull mass ratios.",
                    true,
                    ("Adjusted", ModSettings.HullWeightAdjustmentEnabled, () => SetHullWeightAdjustmentMode(true)),
                    ("Vanilla", !ModSettings.HullWeightAdjustmentEnabled, () => SetHullWeightAdjustmentMode(false)));
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
                    "Flat Earth: vanilla one-map geometry. Disc World: wraps the map at the Pacific seam (neighboring copies, wider panning). Globe: renders the campaign as a 3D sphere skin over the flat sim (experimental — orbit with right-drag/scroll; border lines and great-circle movement are not represented).",
                    true,
                    ("Flat Earth", ModSettings.MapGeometry == ModSettings.MapGeometryMode.Flat, () => SetMapGeometryMode(ModSettings.MapGeometryMode.Flat)),
                    ("Disc World", ModSettings.MapGeometry == ModSettings.MapGeometryMode.Disc, () => SetMapGeometryMode(ModSettings.MapGeometryMode.Disc)),
                    ("Globe", ModSettings.MapGeometry == ModSettings.MapGeometryMode.Globe, () => SetMapGeometryMode(ModSettings.MapGeometryMode.Globe)));
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

        AddNationShipPaintsChannelHeader(parent);

        foreach (DesignHullColorProofPatch.NationPaintUiInfo nation in DesignHullColorProofPatch.NationPaintOptions())
            AddNationShipPaintRow(parent, nation);
    }

    // Column-header row above the nation swatch rows so the user can tell at a glance
    // which channel each swatch governs (Hull/Super/Turret/Deck/Bottom/Detail/Barrel/Trim)
    // without having to hover for the tooltip. Lead spacer + per-column widths mirror
    // AddNationShipPaintRow's label width (100) and swatch width (24) so the labels line
    // up with the swatches beneath them.
    private static void AddNationShipPaintsChannelHeader(Transform parent)
    {
        GameObject row = new("UADVP_NationShipPaintsHeader");
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = new Color(0f, 0f, 0f, 0f);
        rowImage.raycastTarget = false;
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.padding = new RectOffset { left = 8, right = 8, top = 2, bottom = 2 };
        rowLayout.spacing = 4f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 16f, preferredHeight: 16f, flexibleWidth: 1f);

        // Matches the 100-px nation-label column in AddNationShipPaintRow so the first
        // channel label sits directly above the first swatch.
        GameObject leadSpacer = new("Spacer");
        leadSpacer.transform.SetParent(row.transform, false);
        Image leadImage = leadSpacer.AddComponent<Image>();
        leadImage.color = new Color(0f, 0f, 0f, 0f);
        leadImage.raycastTarget = false;
        AddLayout(leadSpacer, minWidth: 100f, preferredWidth: 100f, flexibleWidth: 0f);

        foreach (PaintArea area in DesignHullColorProofPatch.AllPickerChannels)
        {
            Text label = AddText(row.transform, ShortChannelLabel(area), 9, TextAnchor.MiddleCenter);
            AddLayout(label.gameObject, minWidth: 24f, preferredWidth: 24f, minHeight: 14f, preferredHeight: 14f, flexibleWidth: 0f);
        }
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

    private static void SetMultiYearShipyardRebuildMode(bool enabled)
    {
        if (ModSettings.MultiYearShipyardRebuildEnabled != enabled)
            ModSettings.MultiYearShipyardRebuildEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSmartAiDesignsMode(bool enabled)
    {
        if (ModSettings.SmartAiDesignsEnabled != enabled)
            ModSettings.SmartAiDesignsEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetRebuildOverseasWeight(ModSettings.LevelSetting level)
    {
        if (ModSettings.RebuildOverseasWeightLevel != level)
            ModSettings.RebuildOverseasWeightLevel = level;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetVanquishedSpoilsMode(bool enabled)
    {
        if (ModSettings.VanquishedSpoilsEnabled != enabled)
            ModSettings.VanquishedSpoilsEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetNavalReinforcement(ModSettings.NavalReinforcementMode mode)
    {
        if (ModSettings.NavalReinforcement != mode)
            ModSettings.NavalReinforcement = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetVanquishedSpoilsShare(ModSettings.LevelSetting level)
    {
        if (ModSettings.VanquishedSpoilsShareLevel != level)
            ModSettings.VanquishedSpoilsShareLevel = level;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetClassNamingThemesMode(bool enabled)
    {
        if (ModSettings.ClassNamingThemesEnabled != enabled)
            ModSettings.ClassNamingThemesEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
        RefreshThemeLauncherButton();
    }

    private static void SetShipbuildingCapacityBoost(ModSettings.ShipbuildingCapacityBoostMode mode)
    {
        if (ModSettings.ShipbuildingCapacityBoost != mode)
            ModSettings.ShipbuildingCapacityBoost = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSurrenderedShipCaptureMode(bool enabled)
    {
        if (ModSettings.SurrenderedShipCaptureEnabled != enabled)
            ModSettings.SurrenderedShipCaptureEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetBattleStartDefaultsMode(bool enabled)
    {
        if (ModSettings.BattleStartDefaultsEnabled != enabled)
            ModSettings.BattleStartDefaultsEnabled = enabled;
        RefreshMenu();
        RefreshLauncherButton();
    }

    private static string CurrentBattleDefaultsType()
    {
        battleDefaultsTypeIndex = Mathf.Clamp(battleDefaultsTypeIndex, 0, BattleStartDefaults.Types.Length - 1);
        return BattleStartDefaults.Types[battleDefaultsTypeIndex].Key;
    }

    // The "Class" selector row for per-type battle-start defaults: < BB > cycling the type
    // that the rows below edit. Rebuilt each RefreshMenu so the rows reflect the chosen type.
    private static void AddBattleDefaultsClassRow(Transform pane)
    {
        battleDefaultsTypeIndex = Mathf.Clamp(battleDefaultsTypeIndex, 0, BattleStartDefaults.Types.Length - 1);
        int count = BattleStartDefaults.Types.Length;

        GameObject row = new("UADVP_BattleDefaultsClass");
        row.transform.SetParent(pane, false);
        Image bg = row.AddComponent<Image>();
        bg.color = RowBackground;
        HorizontalLayoutGroup hl = row.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset { left = 8, right = 8, top = 4, bottom = 4 };
        hl.spacing = 8f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlHeight = true;
        hl.childControlWidth = true;
        hl.childForceExpandHeight = false;
        hl.childForceExpandWidth = false;
        AddLayout(row, minHeight: 34f, preferredHeight: 34f, flexibleWidth: 1f);

        Text label = AddText(row.transform, "Class", 13, TextAnchor.MiddleLeft);
        AddLayout(label.gameObject, minWidth: 155f, flexibleWidth: 1f);
        AddActionButton(row.transform, "<", () => { battleDefaultsTypeIndex = (battleDefaultsTypeIndex - 1 + count) % count; RefreshMenu(); }, 56f);
        Text cur = AddText(row.transform, BattleStartDefaults.Types[battleDefaultsTypeIndex].Label, 14, TextAnchor.MiddleCenter);
        AddLayout(cur.gameObject, minWidth: 90f);
        AddActionButton(row.transform, ">", () => { battleDefaultsTypeIndex = (battleDefaultsTypeIndex + 1) % count; RefreshMenu(); }, 56f);
    }

    private static void SetBattleStartAmmo(ModSettings.BattleAmmoMode mode)
    {
        BattleStartDefaults.SetAmmo(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleStartAvoidTorp(ModSettings.BattleToggle mode)
    {
        BattleStartDefaults.SetAvoidTorp(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleStartAvoidShip(ModSettings.BattleToggle mode)
    {
        BattleStartDefaults.SetAvoidShip(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleStartAutoLeader(ModSettings.BattleToggle mode)
    {
        BattleStartDefaults.SetAutoLeader(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleStartFireTorp(ModSettings.BattleToggle mode)
    {
        BattleStartDefaults.SetFireTorp(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleStartFormation(ModSettings.BattleFormation mode)
    {
        BattleStartDefaults.SetFormation(CurrentBattleDefaultsType(), mode);
        RefreshMenu();
    }

    private static void SetBattleSpeedSyncMode(bool enabled)
    {
        ModSettings.BattleSpeedSyncEnabled = enabled;
        RefreshMenu();
    }

    private static void SetBattleReverseMethod(ModSettings.BattleTurnMethod mode)
    {
        if (ModSettings.BattleReverseMethod != mode)
            ModSettings.BattleReverseMethod = mode;
        RefreshMenu();
    }

    private static void SetFollowSteerDamping(bool enabled)
    {
        ModSettings.FollowSteerDampingEnabled = enabled;
        RefreshMenu();
    }

    private static void SetParallelStation(bool abreast)
    {
        ModSettings.ParallelStationAbreast = abreast;
        RefreshMenu();
    }

    private static void SetAiEconomyPriorities(bool enabled)
    {
        ModSettings.AiEconomyPrioritiesEnabled = enabled;
        RefreshMenu();
    }

    private static void SetShipResupplyOverride(bool enabled)
    {
        ModSettings.ShipResupplyOverrideEnabled = enabled;
        RefreshMenu();
    }

    private static void SetShipServiceRecords(bool enabled)
    {
        ModSettings.ShipServiceRecordsEnabled = enabled;
        RefreshMenu();
    }

    // ----- Battle "helm" panel: draggable, always-on-top typed course + speed, with paste -----

    private static void TrySetupBattleInputs()
    {
        if (currentUi == null)
            return;
        if (battlePanel != null)
            return; // already built (destroyed objects null out -> rebuild)
        try
        {
            UnityEngine.UI.Slider speedSlider = currentUi.divSpeedSlider;
            if (speedSlider == null)
                return;
            Transform root = TopCanvasOf(speedSlider.transform);

            battlePanel = new GameObject("UADVP_BattleHelmPanel");
            battlePanel.transform.SetParent(root, false);

            // Own canvas, sorted above the HUD so the divisions UI can't sink behind/over it.
            battlePanelCanvas = battlePanel.AddComponent<Canvas>();
            battlePanelCanvas.overrideSorting = true;
            battlePanelCanvas.sortingOrder = 5000;
            battlePanel.AddComponent<GraphicRaycaster>();

            Image bg = battlePanel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.8f);
            bg.raycastTarget = true;

            battlePanelRect = battlePanel.GetComponent<RectTransform>();
            battlePanelRect.anchorMin = new Vector2(0.5f, 0.5f);
            battlePanelRect.anchorMax = new Vector2(0.5f, 0.5f);
            battlePanelRect.pivot = new Vector2(0.5f, 0.5f);
            battlePanelRect.sizeDelta = new Vector2(250f, 128f);

            VerticalLayoutGroup vl = battlePanel.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset { left = 6, right = 6, top = 4, bottom = 6 };
            vl.spacing = 4f;
            vl.childAlignment = TextAnchor.UpperLeft;
            vl.childControlHeight = true;
            vl.childControlWidth = true;
            vl.childForceExpandHeight = false;
            vl.childForceExpandWidth = true;

            // Auto-size the panel height to however many rows it ends up with (speed, course, turn buttons)
            // so the dark background always covers them; width stays fixed at sizeDelta.x.
            ContentSizeFitter fitter = battlePanel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Header doubles as the drag handle.
            GameObject header = new("UADVP_BattleHelmHeader");
            header.transform.SetParent(battlePanel.transform, false);
            Image hbg = header.AddComponent<Image>();
            hbg.color = new Color(0.16f, 0.32f, 0.52f, 0.95f);
            hbg.raycastTarget = true;
            Text htext = AddText(header.transform, "HELM — drag • right-click a row = paste", 11, TextAnchor.MiddleCenter);
            htext.raycastTarget = false;
            AddLayout(header, minHeight: 20f, preferredHeight: 20f, flexibleWidth: 1f);
            AddDragHandler(header);

            BuildBattleRow("UADVP_BattleSpeedRow", "kn", true, out battleSpeedText, out battleSpeedInput);
            BuildBattleRow("UADVP_BattleCourseRow", "deg", false, out battleCourseText, out battleCourseInput);
            battleSpeedInput.onEndEdit.AddListener(new System.Action<string>(OnBattleSpeedEntered));
            battleCourseInput.onEndEdit.AddListener(new System.Action<string>(OnBattleCourseEntered));

            // Reverse-course buttons — same as the R (port) / T (starboard) hotkeys, honoring the
            // selected reverse method (180 / 90·90 / Split / Rudder).
            BuildBattleTurnRow();

            battlePanelPosLoaded = false;
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP battle helm panel created (draggable, paste-enabled).");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP battle inputs setup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Walk up to the top-most Canvas so our overlay parents at HUD root (won't be clipped/covered).
    private static Transform TopCanvasOf(Transform t)
    {
        Transform top = t;
        Transform? cur = t;
        while (cur != null)
        {
            if (cur.GetComponent<Canvas>() != null)
                top = cur;
            cur = cur.parent;
        }
        return top;
    }

    private static GameObject BuildBattleRow(string name, string placeholder, bool isSpeed, out Text current, out InputField input)
    {
        GameObject row = new(name);
        row.transform.SetParent(battlePanel!.transform, false);
        Image rbg = row.AddComponent<Image>();
        rbg.color = new Color(0f, 0f, 0f, 0.35f);
        rbg.raycastTarget = true; // so right-click lands on the row (paste)

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset { left = 6, right = 6, top = 2, bottom = 2 };
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 28f, preferredHeight: 28f, flexibleWidth: 1f);

        current = AddText(row.transform, "—", 13, TextAnchor.MiddleLeft);
        current.raycastTarget = false; // read-only label
        AddLayout(current.gameObject, minWidth: 168f, preferredWidth: 168f, preferredHeight: 22f);

        input = AddHexInput(row.transform, string.Empty, 52f);
        if (input.placeholder != null)
        {
            Text? ph = input.placeholder.TryCast<Text>();
            if (ph != null)
                ph.text = placeholder;
        }

        AddRightClickPaste(row, isSpeed);
        return row;
    }

    // A row of two reverse-course buttons (port / starboard) that mirror the R/T hotkeys.
    private static void BuildBattleTurnRow()
    {
        GameObject row = new("UADVP_BattleTurnRow");
        row.transform.SetParent(battlePanel!.transform, false);
        Image rbg = row.AddComponent<Image>();
        rbg.color = new Color(0f, 0f, 0f, 0.35f);
        rbg.raycastTarget = true;

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset { left = 6, right = 6, top = 2, bottom = 2 };
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        AddLayout(row, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        AddPanelButton(row, "<< Port (R)", () => GameData.BattleTurn.ReverseSelected(currentUi, false));
        AddPanelButton(row, "Stbd (T) >>", () => GameData.BattleTurn.ReverseSelected(currentUi, true));
    }

    // A lightweight button for the helm panel — its own Image (background) + a centered label, sized by
    // a nested layout group so the text reliably fills and centers. Avoids the heavy popup button prefab.
    private static Button AddPanelButton(GameObject row, string label, System.Action onClick)
    {
        GameObject go = new("UADVP_PanelBtn");
        go.transform.SetParent(row.transform, false);
        Image img = go.AddComponent<Image>();
        img.color = new Color(0.16f, 0.32f, 0.52f, 0.95f);
        img.raycastTarget = true;

        HorizontalLayoutGroup hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlHeight = true;
        hl.childControlWidth = true;
        hl.childForceExpandHeight = true;
        hl.childForceExpandWidth = true;

        Button btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(new System.Action(onClick));

        Text t = AddText(go.transform, label, 12, TextAnchor.MiddleCenter);
        t.raycastTarget = false;
        AddLayout(go, minHeight: 24f, preferredHeight: 24f, minWidth: 96f, flexibleWidth: 1f);
        return btn;
    }

    // ----- pointer plumbing for the panel (drag) and rows (right-click paste) -----

    private static void AddTrigger(GameObject go, UnityEngine.EventSystems.EventTriggerType type, System.Action<UnityEngine.EventSystems.BaseEventData> cb)
    {
        UnityEngine.EventSystems.EventTrigger trig = go.GetComponent<UnityEngine.EventSystems.EventTrigger>()
            ?? go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
        UnityEngine.EventSystems.EventTrigger.Entry entry = new();
        entry.eventID = type;
        entry.callback.AddListener(new System.Action<UnityEngine.EventSystems.BaseEventData>(cb));
        trig.triggers.Add(entry);
    }

    private static void AddDragHandler(GameObject handle)
    {
        AddTrigger(handle, UnityEngine.EventSystems.EventTriggerType.Drag, OnBattlePanelDrag);
        AddTrigger(handle, UnityEngine.EventSystems.EventTriggerType.EndDrag, _ => SaveBattlePanelPosition());
    }

    private static void OnBattlePanelDrag(UnityEngine.EventSystems.BaseEventData data)
    {
        try
        {
            if (battlePanelRect == null)
                return;
            var p = data.TryCast<UnityEngine.EventSystems.PointerEventData>();
            if (p == null)
                return;
            float scale = battlePanelCanvas != null && battlePanelCanvas.scaleFactor > 0f ? battlePanelCanvas.scaleFactor : 1f;
            battlePanelRect.anchoredPosition += p.delta / scale;
        }
        catch { }
    }

    private static void AddRightClickPaste(GameObject row, bool isSpeed)
    {
        AddTrigger(row, UnityEngine.EventSystems.EventTriggerType.PointerClick, data =>
        {
            try
            {
                var p = data.TryCast<UnityEngine.EventSystems.PointerEventData>();
                if (p == null || p.button != UnityEngine.EventSystems.PointerEventData.InputButton.Right)
                    return;
                if (isSpeed)
                {
                    if (!float.IsNaN(clipboardSpeed)) ApplySpeedToSelection(clipboardSpeed);
                }
                else
                {
                    if (!float.IsNaN(clipboardCourse)) ApplyCourseToSelection(clipboardCourse);
                }
            }
            catch { }
        });
    }

    private static void SaveBattlePanelPosition()
    {
        try
        {
            if (battlePanelRect == null)
                return;
            Vector2 ap = battlePanelRect.anchoredPosition;
            PlayerPrefs.SetFloat(BattlePanelXKey, ap.x);
            PlayerPrefs.SetFloat(BattlePanelYKey, ap.y);
            PlayerPrefs.Save();
        }
        catch { }
    }

    private static void LoadBattlePanelPosition()
    {
        try
        {
            if (battlePanelRect == null)
                return;
            float x = PlayerPrefs.GetFloat(BattlePanelXKey, 0f);
            float y = PlayerPrefs.GetFloat(BattlePanelYKey, -180f); // default: below screen center, clear of HUD
            battlePanelRect.anchoredPosition = new Vector2(x, y);
            battlePanelPosLoaded = true;
        }
        catch { }
    }

    private static void RefreshBattleInputs()
    {
        try
        {
            if (battlePanel == null)
                return;

            Ship? first = null;
            bool show = false;
            if (currentUi != null && GameManager.IsBattle)
            {
                // Only while the battle COMMAND phase is up. IsBattle stays true on the pre-battle
                // deployment/briefing and post-battle results screens, and the speed slider can be active
                // there too — so additionally gate on BattleManager's phase flags: IsBattleStart
                // (deployment/start phase) and IsBattleFinishing (results/finishing phase) must BOTH be
                // false. Active combat = neither. (UADVP_HELMVIS logs the flags on change to confirm.)
                UnityEngine.UI.Slider? sld = null;
                try { sld = currentUi.divSpeedSlider; } catch { }
                bool hudUp = sld != null && sld.gameObject.activeInHierarchy;
                bool starting = false, finishing = false;
                try
                {
                    var bm = BattleManager.Instance;
                    if (bm != null) { starting = bm.IsBattleStart; finishing = bm.IsBattleFinishing; }
                }
                catch { }
                var selected = currentUi.selectedShips;
                int selCount = selected != null ? selected.Count : 0;
                if (hudUp && !starting && !finishing && selCount > 0)
                {
                    first = selected![0];
                    show = first != null;
                }
                LogHelmVisibilityIfChanged(hudUp, starting, finishing, selCount, show);
            }

            if (battlePanel.activeSelf != show)
                battlePanel.SetActive(show);
            if (!show || first == null)
            {
                trackedDivPtr = IntPtr.Zero; // reset change tracking when nothing is selected
                return;
            }

            if (!battlePanelPosLoaded)
                LoadBattlePanelPosition();

            Division? div = BattleDiv(first);
            Ship lead = BattleLeader(div) ?? first;
            float curCourse = BattleF(() => lead.transform.eulerAngles.y);
            float assignedCourse = BattleAssignedCourse(div, curCourse);
            float assignedSpeed = BattleCommandedKnots(lead);

            // DIAGNOSTIC (throttled): dump everything needed to calibrate the raw<->displayed speed
            // conversion against the game's OWN readout (divSpeedText, the number on the speed bar).
            // With one sample where a known speed is set, compare game= to each candidate below to pin
            // the exact correct conversion for both the readout and the apply path. Enable via the
            // Experimental "Battle Runtime Diagnostics" option.
            if (ModSettings.BattleRuntimeDiagnosticsEnabled && (++speedDiagTick % 90) == 0)
            {
                Ship f = first;
                float curRaw = BattleF(() => f.savedCurrentSpeed);
                float desRaw = BattleF(() => f.savedDesiredSpeed);
                float engCustom = BattleF(() => f.engineCustomSpeed);
                float maxDisp = BattleF(() => f.SpeedMax());          // fakeMod=true  => displayed max
                float maxRaw = BattleF(() => f.SpeedMax(false));      // fakeMod=false => raw max
                float desDispSD = BattleF(() => f.SpeedDesired(true, true));   // game's own getter, displayed?
                float desRawSD = BattleF(() => f.SpeedDesired(true, false));   // game's own getter, raw
                float ratioCur = maxRaw > 0.01f ? curRaw * maxDisp / maxRaw : curRaw;
                float ratioDes = maxRaw > 0.01f ? desRaw * maxDisp / maxRaw : desRaw;
                float modCurTT = 0f, modCurTF = 0f, modDesTT = 0f;
                try { modCurTT = Ship.ModifySpeedShip(curRaw, true, true); } catch { }
                try { modCurTF = Ship.ModifySpeedShip(curRaw, true, false); } catch { }
                try { modDesTT = Ship.ModifySpeedShip(desRaw, true, true); } catch { }
                float slVal = float.NaN, slMin = float.NaN, slMax = float.NaN;
                try { var sld2 = currentUi != null ? currentUi.divSpeedSlider : null; if (sld2 != null) { slVal = sld2.value; slMin = sld2.minValue; slMax = sld2.maxValue; } } catch { }
                float velMps = 0f;
                try { Vector3 vv = f.velocity; vv.y = 0f; velMps = vv.magnitude; } catch { }
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP_SPEEDDIAG game={BattleGameSpeedText()} slider={slVal:0.###}[{slMin:0.##}..{slMax:0.##}] " +
                    $"vel={velMps:0.00}mps/{velMps * MetersPerSecToKnots:0.00}kn " +
                    $"curRaw={curRaw:0.00} desRaw={desRaw:0.00} engCustom={engCustom:0.00} maxDisp={maxDisp:0.00} maxRaw={maxRaw:0.00} " +
                    $"SpeedDesired(disp)={desDispSD:0.00} SpeedDesired(raw)={desRawSD:0.00} ratioCur={ratioCur:0.00} ratioDes={ratioDes:0.00} " +
                    $"modCurTT={modCurTT:0.00} modCurTF={modCurTF:0.00} modDesTT={modDesTT:0.00}");
            }

            // Auto-clipboard: capture when the assigned course/speed CHANGES while the same division
            // stays selected (i.e. the player adjusted it — right-click course order, slider drag, or
            // typed). Switching selection only re-baselines (no copy), so a paste keeps the value from
            // the division you adjusted, not the one you switch to.
            IntPtr divPtr = IntPtr.Zero;
            try { if (div != null) divPtr = div.Pointer; } catch { }
            if (divPtr != IntPtr.Zero && divPtr == trackedDivPtr)
            {
                if (!float.IsNaN(trackedCourse) && AngleDelta(assignedCourse, trackedCourse) > 1.0f)
                    clipboardCourse = assignedCourse;
                if (!float.IsNaN(trackedSpeed) && Mathf.Abs(assignedSpeed - trackedSpeed) > 0.2f)
                    clipboardSpeed = assignedSpeed;
            }
            trackedDivPtr = divPtr;
            trackedCourse = assignedCourse;
            trackedSpeed = assignedSpeed;

            if (battleSpeedText != null)
            {
                float curSpeed = CurrentKnots(lead);
                string clip = float.IsNaN(clipboardSpeed) ? string.Empty : $"  RC:{clipboardSpeed:0.0}";
                battleSpeedText.text = $"spd {curSpeed:0.0}/{assignedSpeed:0.0}kn{clip}";
            }
            if (battleCourseText != null)
            {
                string clip = float.IsNaN(clipboardCourse) ? string.Empty : $"  RC:{clipboardCourse:0}°";
                battleCourseText.text = $"crs {curCourse:0}/{assignedCourse:0}°{clip}";
            }
        }
        catch { }
    }

    // Shortest absolute angular difference in degrees (0..180).
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs((a - b) % 360f);
        return d > 180f ? 360f - d : d;
    }

    private static void OnBattleSpeedEntered(string value)
    {
        if (currentUi == null || !float.TryParse(value, out float knots))
            return;
        ApplySpeedToSelection(knots);
        if (battleSpeedInput != null)
            battleSpeedInput.text = string.Empty;
    }

    // Apply a knot speed to the selected division by driving the game's OWN division speed slider
    // (not SetEngineCustomSpeed): the slider is the throttle bar (so it moves) and the game commits the
    // order via OnSpeedSliderUp. Typed knots become a fraction of the division's max knots, mapped onto
    // the slider range. CRITICAL: max knots = SpeedMax(false) m/s * 1.94384 (=37.5 for the test ship),
    // NOT SpeedMax(true) (=56.38, an internal unit) — using the latter made every typed speed land ~0.66x
    // too slow. Reused by typed entry and by right-click paste.
    private static void ApplySpeedToSelection(float knots)
    {
        try
        {
            if (currentUi == null)
                return;
            if (knots < 0f)
                knots = 0f;
            var selected = currentUi.selectedShips;
            if (selected == null || selected.Count == 0)
                return;
            Ship? first = selected[0];
            if (first == null)
                return;

            Ship lead = BattleLeader(BattleDiv(first)) ?? first;
            float maxKn = BattleF(() => lead.SpeedMax(false)) * MetersPerSecToKnots;
            float frac = maxKn > 0f ? Mathf.Clamp01(knots / maxKn) : 0f;
            float before = BattleF(() => first.savedCurrentSpeed);

            UnityEngine.UI.Slider? slider = null;
            try { slider = currentUi.divSpeedSlider; } catch { }
            float sMin = 0f, sMax = 1f, sVal = frac;
            bool applied = false;
            if (slider != null)
            {
                try { sMin = slider.minValue; sMax = slider.maxValue; } catch { }
                sVal = Mathf.Lerp(sMin, sMax, frac);
                try { slider.value = sVal; applied = true; } catch { }
                try { currentUi.OnSpeedSliderUp(); } catch { }
            }

            if (!applied)
            {
                foreach (Ship ship in selected)
                {
                    if (ship == null) continue;
                    float max = BattleF(() => ship.SpeedMax());
                    float v = max > 0f ? Mathf.Min(knots, max) : knots;
                    try { ship.SetEngineCustomSpeed(v); } catch { }
                }
            }

            // Capture the resulting state so the log shows what the game DID with our slider value: the
            // raw desired speed it now holds, its own displayed desired (SpeedDesired fakeMod), the value
            // the slider settled on, and the game's own speed readout. Comparing these to `typed` tells us
            // whether a typed/pasted speed lands where intended. (These may lag a frame; the periodic
            // UADVP_SPEEDDIAG line shows the fully-settled values.)
            float afterDesRaw = BattleF(() => first.savedDesiredSpeed);
            float afterDesDisp = BattleF(() => first.SpeedDesired(true, true));
            float afterSliderVal = slider != null ? BattleF(() => slider.value) : float.NaN;
            string gameTxt = BattleGameSpeedText();

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP battle speed: typed={knots:0.0}kn maxKn={maxKn:0.0} frac={frac:0.00} slider[{sMin:0.##}..{sMax:0.##}]=set{sVal:0.###}->now{afterSliderVal:0.###} " +
                $"curBefore={before:0.0} desRawAfter={afterDesRaw:0.00} desDispAfter={afterDesDisp:0.00} game={gameTxt} appliedViaSlider={applied}");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP battle speed apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // One-shot (per state change) trace of the helm-panel visibility gate, so a log read confirms the
    // BattleManager phase flags actually distinguish deploy / combat / results. Gated on diagnostics.
    private static void LogHelmVisibilityIfChanged(bool hudUp, bool starting, bool finishing, int selCount, bool show)
    {
        if (!ModSettings.BattleRuntimeDiagnosticsEnabled)
            return;
        string sig = $"{hudUp}|{starting}|{finishing}|{selCount > 0}|{show}";
        if (sig == lastHelmVisSig)
            return;
        lastHelmVisSig = sig;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP_HELMVIS hudUp={hudUp} battleStart={starting} battleFinishing={finishing} selCount={selCount} -> show={show}");
    }

    // The game's own division-speed readout text (the number shown on the speed bar) — the ground truth
    // for calibrating raw<->displayed knots. divSpeedText is the primary line, divSpeedText2 the secondary.
    private static string BattleGameSpeedText()
    {
        try
        {
            if (currentUi == null)
                return "?";
            string a = "?";
            string b = "?";
            try { var t = currentUi.divSpeedText; if (t != null) a = t.text; } catch { }
            try { var t = currentUi.divSpeedText2; if (t != null) b = t.text; } catch { }
            return $"\"{a}\"|\"{b}\"";
        }
        catch { return "?"; }
    }

    private static void OnBattleCourseEntered(string value)
    {
        if (currentUi == null || !float.TryParse(value, out float course))
            return;
        ApplyCourseToSelection(course);
        if (battleCourseInput != null)
            battleCourseInput.text = string.Empty;
    }

    // Steer each selected division onto an absolute compass course. Reused by typed entry and paste.
    private static void ApplyCourseToSelection(float course)
    {
        try
        {
            if (currentUi == null)
                return;
            course = ((course % 360f) + 360f) % 360f;
            var selected = currentUi.selectedShips;
            if (selected == null)
                return;
            var seen = new HashSet<IntPtr>();
            int n = 0;
            foreach (Ship ship in selected)
            {
                if (ship == null)
                    continue;
                Division? d = BattleDiv(ship);
                if (d == null)
                    continue;
                IntPtr ptr;
                try { ptr = d.Pointer; } catch { continue; }
                if (!seen.Add(ptr))
                    continue;
                Ship lead = BattleLeader(d) ?? ship;
                float yaw = BattleF(() => lead.transform.eulerAngles.y);
                Vector3 forward;
                try { forward = lead.transform.forward; } catch { continue; }
                Vector3 dir = Quaternion.AngleAxis(course - yaw, Vector3.up) * forward;
                try { d.MoveDir(dir, true); n++; } catch { }
            }
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP battle course: {course:0}° -> {n} division(s).");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP battle course apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Division? BattleDiv(Ship s) { try { return s.division; } catch { return null; } }
    private static Ship? BattleLeader(Division? d) { try { return d?.leader; } catch { return null; } }
    private static float BattleF(Func<float> f) { try { return f(); } catch { return 0f; } }

    // Current ACTUAL speed in knots, taken from the hull's physical velocity (m/s) rather than the opaque
    // savedCurrentSpeed field (whose internal unit kept mis-scaling the readout). World velocity shares
    // the same m/s scale that SpeedMax(false) uses (confirmed: SpeedMax(false) 19.29 m/s == 37.5 kn ==
    // throttle), so knots = |velocity_horizontal| * 1.94384. Horizontal magnitude only (ignore sink/rise).
    private static float CurrentKnots(Ship s)
    {
        try
        {
            Vector3 v = s.velocity;
            v.y = 0f;
            return v.magnitude * MetersPerSecToKnots;
        }
        catch { return 0f; }
    }

    // Assigned/desired speed in DISPLAYED knots (what the game shows on the speed bar).
    private static float BattleSetSpeed(Ship s)
    {
        return DisplaySpeed(s, BattleF(() => s.savedDesiredSpeed));
    }

    // The selected division's COMMANDED speed in knots, read straight off the game's own speed slider so
    // it matches the HUD speed bar exactly. We deliberately do NOT use savedDesiredSpeed here: for a
    // follower that's station-keeping it swings between ~0 and max every second (that produced the junk
    // readout). slider fraction * division max knots; falls back to the converted desired if no slider.
    private static float BattleCommandedKnots(Ship lead)
    {
        try
        {
            var sld = currentUi != null ? currentUi.divSpeedSlider : null;
            if (sld != null)
            {
                float min = sld.minValue, max = sld.maxValue, val = sld.value;
                float maxKn = BattleF(() => lead.SpeedMax(false)) * MetersPerSecToKnots;
                if (max > min && maxKn > 0f)
                    return Mathf.Clamp01((val - min) / (max - min)) * maxKn;
            }
        }
        catch { }
        return BattleSetSpeed(lead);
    }

    // Convert a savedCurrent/DesiredSpeed value to the DISPLAYED knots the game shows. Those fields are
    // in SpeedMax(true) INTERNAL units (they peg exactly at SpeedMax(true)), while the displayed max is
    // SpeedMax(false) m/s * 1.94384 knots. So knots = raw * maxKnots / SpeedMax(true). (Confirmed in-game
    // 0.5.243: SpeedMax(false)=19.29 m/s -> 37.5 kn == HUD "38" == sliderMax/10; savedDesiredSpeed pegged
    // at SpeedMax(true)=56.38.) The earlier SpeedMax(true)/SpeedMax(false) ratio was inverted AND in the
    // wrong units, inflating the readout ~2.9x.
    private static float DisplaySpeed(Ship s, float raw)
    {
        try
        {
            float maxInternal = s.SpeedMax();                           // SpeedMax(true): same units as raw
            float maxKnots = s.SpeedMax(false) * MetersPerSecToKnots;   // displayed max, in knots
            if (maxInternal > 0.01f)
                return raw * (maxKnots / maxInternal);
        }
        catch { }
        return raw;
    }

    // Assigned course (degrees) from the division's ordered move direction; falls back to the
    // current heading when no direction is set. Atan2(x,z) matches the eulerAngles.y convention.
    private static float BattleAssignedCourse(Division? d, float fallbackDeg)
    {
        try
        {
            if (d == null)
                return fallbackDeg;
            Vector3 dir = d.MovingDirection();
            if (dir.sqrMagnitude < 0.0001f)
                return fallbackDeg;
            float deg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return (deg % 360f + 360f) % 360f;
        }
        catch { return fallbackDeg; }
    }

    private static void SetSharedDesignsUsageMode(CampaignSharedDesignUsageSettings.SharedDesignPolicy mode)
    {
        CampaignSharedDesignUsageSettings.TrySetMode(mode);
        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSmartRefitsMode(bool enabled)
    {
        if (ModSettings.SmartRefitsEnabled != enabled)
            ModSettings.SmartRefitsEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetSeaTransportLossesMode(bool enabled)
    {
        if (ModSettings.SeaTransportLossesActiveForcesEnabled != enabled)
            ModSettings.SeaTransportLossesActiveForcesEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetCampaignNavalMobilityMode(ModSettings.CampaignNavalMobilityMode mode)
    {
        if (ModSettings.CampaignNavalMobility != mode)
            ModSettings.CampaignNavalMobility = mode;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetTaskForceSustainmentMode(bool enabled)
    {
        if (ModSettings.TaskForceSustainmentFullEnabled != enabled)
        {
            ModSettings.TaskForceSustainmentFullEnabled = enabled;
            CampaignTaskForceSustainmentPatch.ApplyAllActive("option-change");
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetAiTaskForceStagingMode(bool enabled)
    {
        if (ModSettings.AiTaskForceStagingEnabled != enabled)
            ModSettings.AiTaskForceStagingEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetHullSpeedAdjustmentMode(bool enabled)
    {
        if (ModSettings.HullSpeedAdjustmentEnabled != enabled)
            ModSettings.HullSpeedAdjustmentEnabled = enabled;

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetHullWeightAdjustmentMode(bool enabled)
    {
        if (ModSettings.HullWeightAdjustmentEnabled != enabled)
            ModSettings.HullWeightAdjustmentEnabled = enabled;

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

    private static void SetMapGeometryMode(ModSettings.MapGeometryMode mode)
    {
        if (ModSettings.MapGeometry != mode)
        {
            ModSettings.MapGeometry = mode;
            // Apply both: each builds for its mode and tears down otherwise (mutually exclusive).
            CampaignMapWrapVisualPatch.ApplyCurrentSetting();
            CampaignGlobeVisualPatch.ApplyCurrentSetting();
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetForeignPortCapacityMode(ModSettings.ForeignPortCapacityMode mode)
    {
        if (ModSettings.ForeignPortCapacity != mode)
        {
            ModSettings.ForeignPortCapacity = mode;
            RefreshCampaignCostUi("Foreign Port Capacity mode change");
        }

        RefreshMenu();
        RefreshLauncherButton();
    }

    private static void SetArmyLogisticsMode(ModSettings.ArmyLogisticsMode mode)
    {
        if (ModSettings.ArmyLogistics != mode)
        {
            ModSettings.ArmyLogistics = mode;
            RefreshCampaignCostUi("Army Logistics mode change");
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

    private static void SetNationShipPaintString(DesignHullColorProofPatch.NationPaintUiInfo nation, string value)
    {
        if (ModSettings.SetNationShipPaintString(nation.Key, value ?? string.Empty))
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange($"{nation.Label} string changed");

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
        => ModSettings.BattleWeatherAlwaysSunny || ModSettings.BattleSpottingRange != ModSettings.BattleSpottingRangeMode.Vanilla || ModSettings.BattleDamage != ModSettings.BattleDamageMode.Vanilla || ModSettings.RealisticShellDamageEnabled || ModSettings.DesignAccuracyPenaltiesBalanced || ModSettings.PortStrikeBalanced || ModSettings.SeaTransportLossesActiveForcesEnabled || ModSettings.AiFleetComposition != ModSettings.AiFleetCompositionMode.Vanilla || ModSettings.AdvancedAiBuilderEnabled || ModSettings.SmartAiDesignsEnabled || ModSettings.SmartRefitsEnabled || ModSettings.CampaignNavalMobility != ModSettings.CampaignNavalMobilityMode.Vanilla || ModSettings.TaskForceSustainmentFullEnabled || ModSettings.AiTaskForceStagingEnabled || ModSettings.HullSpeedAdjustmentEnabled || ModSettings.HullWeightAdjustmentEnabled || ModSettings.MajorShipTorpedoesRestricted || ModSettings.SuperstructureRefitsEnabled || ModSettings.ShipyardCapacityBalanced || ModSettings.ForeignPortCapacity != ModSettings.ForeignPortCapacityMode.Vanilla || ModSettings.ArmyLogistics != ModSettings.ArmyLogisticsMode.Vanilla || ModSettings.EarlyCanalOpeningsEnabled || ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Vanilla || !ModSettings.CampaignEndDateEnabled || ModSettings.MineWarfareDisabled || ModSettings.SubmarineWarfareDisabled || ModSettings.CampaignMapWraparoundEnabled || ModSettings.ExperimentalNationShipPaintsEnabled;

    private static void AddLauncherTooltip(GameObject buttonObject)
        => AddTooltip(
            buttonObject,
            LauncherTooltipText,
            () => launcherButton != null && launcherButton.interactable);

    private static string LauncherTooltipText()
        => $"UAD:VP Options\nBattle Weather: {BattleWeatherModeText(ModSettings.BattleWeatherAlwaysSunny)}\nBattle Spotting: {BattleSpottingRangeModeText(ModSettings.BattleSpottingRange)}\nBattle Damage: {BattleDamageModeText(ModSettings.BattleDamage)}\nRealistic Shell Damage: {RealisticShellDamageModeText(ModSettings.RealisticShellDamage)}\nCrew & Accuracy Balance: {DesignAccuracyPenaltiesModeText(ModSettings.DesignAccuracyPenaltyMode)}\nPort Strike: {PortStrikeModeText(ModSettings.PortStrikeBalanced)}\nSea Transport Losses: {SeaTransportLossesModeText(ModSettings.SeaTransportLossesActiveForcesEnabled)}\nAI Fleet Mix: {AiFleetCompositionModeText(ModSettings.AiFleetComposition)}\nAdvanced AI Builder: {AdvancedAiBuilderModeText(ModSettings.AdvancedAiBuilderEnabled)}\nSmart AI Designs: {SmartAiDesignsModeText(ModSettings.SmartAiDesignsEnabled)}\nShared Designs: {CampaignSharedDesignUsageSettings.CurrentModeText()}\nSmart Refits: {SmartRefitsModeText(ModSettings.SmartRefitsEnabled)}\nCampaign Naval Mobility: {CampaignNavalMobilityModeText(ModSettings.CampaignNavalMobility)}\nTask Force Sustainment: {TaskForceSustainmentModeText(ModSettings.TaskForceSustainmentFullEnabled)}\nAI Task Force Staging: {AiTaskForceStagingModeText(ModSettings.AiTaskForceStagingEnabled)}\nSuspend Dock Overcapacity: {ShipyardCapacityModeText(ModSettings.ShipyardCapacityBalanced)}\nForeign Port Capacity: {ForeignPortCapacityModeText(ModSettings.ForeignPortCapacity)}\nArmy Logistics: {ArmyLogisticsModeText(ModSettings.ArmyLogistics)}\nCanal Openings: {CanalOpeningModeText(ModSettings.EarlyCanalOpeningsEnabled)}\nTechnology Spread: {TechnologySpreadModeText(ModSettings.TechnologySpread)}\nCampaign End Date: {CampaignEndDateModeText(ModSettings.CampaignEndDateEnabled)}\nMine Warfare: {MineWarfareModeText(ModSettings.MineWarfareDisabled)}\nSubmarine Warfare: {SubmarineWarfareModeText(ModSettings.SubmarineWarfareDisabled)}\nHull Speed Adjustment: {HullSpeedAdjustmentModeText(ModSettings.HullSpeedAdjustmentEnabled)}\nHull Weight Adjustment: {HullWeightAdjustmentModeText(ModSettings.HullWeightAdjustmentEnabled)}\nCA+ Torpedoes: {MajorShipTorpedoesModeText(ModSettings.MajorShipTorpedoesRestricted)}\nSuperstructure Compatibility: {SuperstructureRefitsModeText(ModSettings.SuperstructureRefitsEnabled)}\nMap Geometry: {CampaignMapWraparoundModeText(ModSettings.CampaignMapWraparoundEnabled)}\nExperimental Nation Ship Paints: {ExperimentalNationShipPaintsModeText(ModSettings.ExperimentalNationShipPaintsEnabled)}\nBattle Runtime Diagnostics: {BattleRuntimeDiagnosticsModeText(ModSettings.BattleRuntimeDiagnosticsEnabled)}";

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
            Section.Maneuvers => "Battle Maneuvers",
            Section.Campaign => "Campaign",
            Section.CampaignII => "Campaign II",
            Section.SwitchNation => "Switch Nation",
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

    private static string SmartAiDesignsModeText(bool enabled)
        => ModSettings.SmartAiDesignsModeText(enabled);

    private static string SmartRefitsModeText(bool enabled)
        => ModSettings.SmartRefitsModeText(enabled);

    private static string SeaTransportLossesModeText(bool enabled)
        => ModSettings.SeaTransportLossesModeText(enabled);

    private static string AiTaskForceStagingModeText(bool enabled)
        => ModSettings.AiTaskForceStagingModeText(enabled);

    private static string CampaignNavalMobilityModeText(ModSettings.CampaignNavalMobilityMode mode)
        => ModSettings.CampaignNavalMobilityModeText(mode);

    private static string TaskForceSustainmentModeText(bool enabled)
        => ModSettings.TaskForceSustainmentModeText(enabled);

    private static string HullSpeedAdjustmentModeText(bool enabled)
        => ModSettings.HullSpeedAdjustmentModeText(enabled);

    private static string HullWeightAdjustmentModeText(bool enabled)
        => ModSettings.HullWeightAdjustmentModeText(enabled);

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

    private static string SuperstructureRefitsModeText(bool enabled)
        => ModSettings.SuperstructureRefitsModeText(enabled);

    private static string ShipyardCapacityModeText(bool balanced)
        => balanced ? "Automatic" : "Manual";

    private static string ForeignPortCapacityModeText(ModSettings.ForeignPortCapacityMode mode)
        => ModSettings.ForeignPortCapacityModeText(mode);

    private static string ArmyLogisticsModeText(ModSettings.ArmyLogisticsMode mode)
        => ModSettings.ArmyLogisticsModeText(mode);

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

    // ----- Phase 2: class naming theme launcher + picker panel (constructor) -----

    private static void SetupThemeLauncherButton()
    {
        GameObject? options = FindPath("Global/Ui/UiMain/Common/Options");
        GameObject? helpButton = FindPath("Global/Ui/UiMain/Common/Options/Help");
        if (options == null || helpButton == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP theme launcher: Options/Help not found at setup.");
            return;
        }

        GameObject buttonObject = options.transform.Find(ThemeLauncherButtonName)?.gameObject ?? UnityEngine.Object.Instantiate(helpButton);
        buttonObject.transform.SetParent(options.transform, false);
        buttonObject.name = ThemeLauncherButtonName;
        buttonObject.SetActive(false);
        MatchButtonSizing(buttonObject, helpButton);
        RemoveTooltipHandlers(buttonObject);
        AddTooltip(buttonObject, () => "UAD:VP Class Naming Themes" + ThemeTooltipSuffix());

        // Distinct icon so the button doesn't read as a second Help "?".
        Transform? imageChild = buttonObject.transform.Find("Image");
        if (imageChild != null && imageChild.TryGetComponent(out Image themeImage))
        {
            Sprite? sprite = Resources.Load<Sprite>("tabs/fleet") ?? Resources.Load<Sprite>("tabs/tech");
            if (sprite != null)
            {
                themeImage.sprite = sprite.TryCast<Sprite>();
                themeImage.preserveAspect = true;
                themeImage.color = Color.white;
            }
            ScaleLauncherIcon(imageChild);
        }

        Outline outline = buttonObject.GetComponent<Outline>() ?? buttonObject.AddComponent<Outline>();
        outline.effectDistance = new Vector2(1f, 1f);
        outline.effectColor = new Color(0.4f, 0.8f, 1f, 1f);

        themeLauncherButton = buttonObject.GetComponent<Button>();
        if (themeLauncherButton != null)
        {
            themeLauncherButton.onClick.RemoveAllListeners();
            themeLauncherButton.onClick.AddListener(new System.Action(ToggleThemePanel));
        }
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP theme launcher button added (button={(themeLauncherButton != null)}).");
    }

    private static string ThemeTooltipSuffix()
    {
        if (DesignHullColorProofPatch.TryResolveCurrentConstructorDesign(out _, out string name) && !string.IsNullOrWhiteSpace(name))
            return $"\nEditing class: {name}";
        return "\nOpen a ship in the constructor to set its class theme.";
    }

    private static void RefreshThemeLauncherButton()
    {
        if (themeLauncherButton == null)
        {
            if (!loggedThemeLauncherNull)
            {
                loggedThemeLauncherNull = true;
                Melon<UADVanillaPlusMod>.Logger.Warning("UADVP theme launcher: button is null (not created).");
            }
            return;
        }

        bool show = initialized && ModSettings.ClassNamingThemesEnabled && GameManager.IsConstructor;
        GameObject go = themeLauncherButton.gameObject;
        if (go.activeSelf != show)
            go.SetActive(show);
        if (show && !loggedThemeLauncherShow)
        {
            loggedThemeLauncherShow = true;
            RectTransform? r = go.GetComponent<RectTransform>();
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP theme launcher shown: active={go.activeSelf} parent={go.transform.parent?.name} sibling={go.transform.GetSiblingIndex()} pos={(r != null ? r.anchoredPosition.ToString() : "?")}.");
        }
        if (!show)
            CloseThemePanel();
    }

    private static void ToggleThemePanel()
    {
        if (themePanel != null)
            CloseThemePanel();
        else
            OpenThemePanel();
    }

    private static void CloseThemePanel()
    {
        if (themePanel != null)
        {
            UnityEngine.Object.Destroy(themePanel);
            themePanel = null;
        }
    }

    private static void OpenThemePanel()
    {
        CloseThemePanel();

        if (!DesignHullColorProofPatch.TryResolveCurrentConstructorDesign(out _, out string designName) || string.IsNullOrWhiteSpace(designName))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP naming themes: no design open in the constructor.");
            return;
        }

        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupRoot == null)
            return;

        // Normalize to the game's real base-class name so this matches the build-time
        // key (ShipGenerateRandomNameThemePatch). (Type-prefix stripping needs the ship's
        // ShipType, added with the P2 ship-aware constructor resolution; null is fine for
        // the common no-prefix case.)
        string baseKey = ShipNameParts.BaseName(designName, null);
        themePanelClass = string.IsNullOrEmpty(baseKey) ? designName : baseKey;
        themePanelNation = ModCampaignState.MainPlayerNation();
        themePanelThemes = NameThemeDatabase.GetAvailableThemes(themePanelNation, 99999);
        themePanelThemeIndex = 0;

        ClassThemeAssignments.Choice? choice = ClassThemeAssignments.Get(themePanelClass);
        if (choice != null && choice.Mode == ClassThemeAssignments.Mode.ThemePool && !string.IsNullOrEmpty(choice.ThemeName))
        {
            int idx = themePanelThemes.FindIndex(t => string.Equals(t.ThemeName, choice.ThemeName, StringComparison.Ordinal));
            if (idx >= 0)
                themePanelThemeIndex = idx;
        }

        themePanel = new GameObject("UADVP_ClassThemePanel");
        themePanel.transform.SetParent(popupRoot.transform, false);
        Image bg = themePanel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.9f);
        bg.raycastTarget = true;
        Button bgButton = themePanel.AddComponent<Button>();
        bgButton.targetGraphic = bg;

        RectTransform rect = themePanel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-18f, -90f);
        rect.sizeDelta = new Vector2(560f, 408f);

        VerticalLayoutGroup layout = themePanel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = 12, right = 12, top = 10, bottom = 10 };
        layout.spacing = 6f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        BuildThemePanelContent();
        themePanel.transform.SetAsLastSibling();
    }

    private static void BuildThemePanelContent()
    {
        if (themePanel == null)
            return;
        ClearChildren(themePanel);

        ClassThemeAssignments.Choice? choice = ClassThemeAssignments.Get(themePanelClass);
        ClassThemeAssignments.Mode mode = choice?.Mode ?? ClassThemeAssignments.Mode.Off;

        // Tall (scrollable theme list) for Theme mode, compact otherwise.
        bool themeList = mode == ClassThemeAssignments.Mode.ThemePool && themePanelThemes.Count > 0;
        RectTransform panelRect = themePanel.GetComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(560f, themeList ? 408f : 150f);

        GameObject header = new("Header");
        header.transform.SetParent(themePanel.transform, false);
        Image hi = header.AddComponent<Image>();
        hi.color = new Color(0f, 0f, 0f, 0f);
        hi.raycastTarget = false;
        HorizontalLayoutGroup hl = header.AddComponent<HorizontalLayoutGroup>();
        hl.spacing = 8f;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlHeight = true;
        hl.childControlWidth = true;
        hl.childForceExpandHeight = false;
        hl.childForceExpandWidth = false;
        AddLayout(header, minHeight: 22f, preferredHeight: 22f, flexibleWidth: 1f);
        Text title = AddText(header.transform, $"Naming: {themePanelClass}", 13, TextAnchor.MiddleLeft);
        AddLayout(title.gameObject, flexibleWidth: 1f);
        AddActionButton(header.transform, "Close", CloseThemePanel, 56f);

        AddSegmentedOption(
            themePanel.transform,
            "UADVP_ThemeMode",
            "Mode",
            "Vanilla uses the game's default names. Theme draws names from a chosen pool. Sequential names ships <Class>-1, -2, ...",
            true,
            ("Vanilla", mode == ClassThemeAssignments.Mode.Off, () => SetThemeMode(ClassThemeAssignments.Mode.Off)),
            ("Theme", mode == ClassThemeAssignments.Mode.ThemePool, () => SetThemeMode(ClassThemeAssignments.Mode.ThemePool)),
            ("Sequential", mode == ClassThemeAssignments.Mode.Sequential, () => SetThemeMode(ClassThemeAssignments.Mode.Sequential)));

        if (mode == ClassThemeAssignments.Mode.ThemePool)
        {
            if (themePanelThemes.Count == 0)
            {
                AddText(themePanel.transform, $"No themes for '{(themePanelNation.Length == 0 ? "(no nation)" : themePanelNation)}'.", 12, TextAnchor.MiddleLeft);
            }
            else
            {
                themePanelThemeIndex = Mathf.Clamp(themePanelThemeIndex, 0, themePanelThemes.Count - 1);
                BuildThemeScrollList(themePanel.transform);

                NameThemeDatabase.ThemeInfo cur = themePanelThemes[themePanelThemeIndex];
                List<string> preview = NameThemeDatabase.GetNamesForTheme(cur.ThemeName, themePanelNation);
                string sample = preview.Count == 0 ? "(no names)" : string.Join(", ", preview.Take(8));
                AddText(themePanel.transform, $"{cur.ThemeName}: {sample}", 12, TextAnchor.MiddleLeft);

                // Selecting a theme already applies to FUTURE builds; this renames the class
                // + its existing ships now (lead takes the theme's first name, rest follow).
                AddActionButton(themePanel.transform, "Rename class + ships now", ApplyThemeRenameNow, 240f);
            }
        }
        else if (mode == ClassThemeAssignments.Mode.Sequential)
        {
            AddText(themePanel.transform, $"New ships: {themePanelClass}-1, {themePanelClass}-2, ...", 12, TextAnchor.MiddleLeft);
        }
    }

    // Scrollable list of available themes (selected highlighted). Replaces the cycler so
    // every theme is reachable without paging. No game prefab — a hand-built ScrollRect.
    private static void BuildThemeScrollList(Transform parent)
    {
        GameObject scrollGo = new("UADVP_ThemeScroll");
        scrollGo.transform.SetParent(parent, false);
        Image scrollBg = scrollGo.AddComponent<Image>();
        scrollBg.color = new Color(0f, 0f, 0f, 0.25f);
        ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;
        AddLayout(scrollGo, minHeight: 224f, preferredHeight: 224f, flexibleWidth: 1f);

        GameObject viewport = new("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        Image vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0.01f);
        viewport.AddComponent<RectMask2D>();
        RectTransform vpRect = viewport.GetComponent<RectTransform>();
        vpRect.anchorMin = Vector2.zero;
        vpRect.anchorMax = Vector2.one;
        vpRect.pivot = new Vector2(0f, 1f);
        vpRect.offsetMin = Vector2.zero;
        vpRect.offsetMax = Vector2.zero;

        GameObject content = new("Content");
        content.transform.SetParent(viewport.transform, false);
        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f;
        vlg.childControlHeight = true;
        vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;
        ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        RectTransform cRect = content.GetComponent<RectTransform>();
        cRect.anchorMin = new Vector2(0f, 1f);
        cRect.anchorMax = new Vector2(1f, 1f);
        cRect.pivot = new Vector2(0.5f, 1f);
        cRect.offsetMin = Vector2.zero;
        cRect.offsetMax = Vector2.zero;

        scroll.viewport = vpRect;
        scroll.content = cRect;

        for (int i = 0; i < themePanelThemes.Count; i++)
        {
            int index = i;
            NameThemeDatabase.ThemeInfo info = themePanelThemes[i];
            Button button = AddActionButton(content.transform, $"{info.ThemeName}  ({info.NameCount})", () => SelectTheme(index), 360f);
            Image image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
            image.color = i == themePanelThemeIndex ? SelectedGold : SegmentIdle;
        }
    }

    private static void SetThemeMode(ClassThemeAssignments.Mode mode)
    {
        ClassThemeAssignments.Choice choice = ClassThemeAssignments.Get(themePanelClass) ?? new ClassThemeAssignments.Choice();
        choice.Mode = mode;
        if (mode == ClassThemeAssignments.Mode.ThemePool && string.IsNullOrEmpty(choice.ThemeName) && themePanelThemes.Count > 0)
            choice.ThemeName = themePanelThemes[Mathf.Clamp(themePanelThemeIndex, 0, themePanelThemes.Count - 1)].ThemeName;
        ClassThemeAssignments.Set(themePanelClass, choice);
        BuildThemePanelContent();
    }

    private static void SelectTheme(int index)
    {
        if (index < 0 || index >= themePanelThemes.Count)
            return;
        themePanelThemeIndex = index;
        ClassThemeAssignments.Choice choice = ClassThemeAssignments.Get(themePanelClass) ?? new ClassThemeAssignments.Choice();
        choice.Mode = ClassThemeAssignments.Mode.ThemePool;
        choice.ThemeName = themePanelThemes[index].ThemeName;
        ClassThemeAssignments.Set(themePanelClass, choice);
        BuildThemePanelContent();
    }

    // Family-wide rename of the current class to the selected theme. Renaming the class
    // template changes its base name, so re-key the theme assignment so future builds of the
    // (now renamed) class stay themed. Closes the panel afterward since the class identity changed.
    private static void ApplyThemeRenameNow()
    {
        if (themePanelThemes.Count == 0)
            return;
        NameThemeDatabase.ThemeInfo cur = themePanelThemes[Mathf.Clamp(themePanelThemeIndex, 0, themePanelThemes.Count - 1)];
        string oldKey = themePanelClass;
        string newKey = ShipNaming.RenameClassToTheme(oldKey, cur.ThemeName, themePanelNation);

        if (!string.IsNullOrEmpty(newKey) && !string.Equals(newKey, oldKey, StringComparison.OrdinalIgnoreCase))
        {
            ClassThemeAssignments.Set(oldKey, new ClassThemeAssignments.Choice { Mode = ClassThemeAssignments.Mode.Off });
            ClassThemeAssignments.Set(newKey, new ClassThemeAssignments.Choice
            {
                Mode = ClassThemeAssignments.Mode.ThemePool,
                ThemeName = cur.ThemeName,
            });
        }

        CloseThemePanel();
    }

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

        bool hasDesign = DesignHullColorProofPatch.TryResolveCurrentConstructorDesign(out string designKey, out string designName);

        // Nation row shows the pure nation colors (so the user can see what's stored
        // for the country independent of any per-class override). Class row shows the
        // layered effective colors (so the user sees what the class actually looks like
        // in-game once both layers are applied).
        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(nation.Key, out Dictionary<PaintArea, Color32> nationColors))
            return;
        Dictionary<PaintArea, Color32> classLayeredColors = nationColors;
        if (hasDesign)
            DesignHullColorProofPatch.TryResolveLayeredPaintColors(nation.Key, designKey, out classLayeredColors);

        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupRoot == null)
            return;

        panelNationKey = nation.Key;
        panelDesignKey = hasDesign ? designKey : string.Empty;
        panelDesignName = hasDesign ? designName : string.Empty;
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
        // Dual-row when a design context resolves; collapse to a single nation row
        // otherwise. Width grows with the channel count (8 swatches + label + buttons).
        panelRect.sizeDelta = new Vector2(hasDesign ? 600f : 350f, hasDesign ? 172f : 130f);

        VerticalLayoutGroup layout = constructorPaintPanel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = 12, right = 12, top = 10, bottom = 10 };
        layout.spacing = 6f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        // Header row: title + close.
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

        string titleSuffix = hasDesign && !string.IsNullOrWhiteSpace(panelDesignName)
            ? $" — {panelDesignName}"
            : string.Empty;
        Text titleText = AddText(headerRow.transform, $"Ship Paints{titleSuffix}", 13, TextAnchor.MiddleLeft);
        AddLayout(titleText.gameObject, flexibleWidth: 1f);
        AddActionButton(headerRow.transform, "Close", CloseConstructorPaintPanel, width: 56f);

        // Channel header row: small text labels aligned over each swatch column so the
        // user can identify channels without hovering.
        BuildPanelChannelHeader(constructorPaintPanel.transform);

        // Nation row: shows the pure nation paint values.
        BuildPanelTargetRow(
            parent: constructorPaintPanel.transform,
            label: $"{nation.Label}:",
            colors: nationColors,
            swatchStorage: panelSwatches,
            onSwatchClick: (channel, color) => OpenPaintPicker(nation, channel),
            actionButtons: new[] { ("Reset", new Action(() => ResetNationShipPaintString(nation))) });

        // Class row: only shown when we have a design context. Swatches show layered
        // colors (so the user sees the effective look); clicking opens the picker pointed
        // at the design override for that channel.
        if (hasDesign)
        {
            BuildPanelTargetRow(
                parent: constructorPaintPanel.transform,
                label: TruncateClassLabel(panelDesignName),
                colors: classLayeredColors,
                swatchStorage: panelClassSwatches,
                onSwatchClick: (channel, color) => OpenPaintPicker(nation, channel, panelDesignKey, panelDesignName),
                actionButtons: new[]
                {
                    // Demote ↓: clear all class overrides — class falls back to nation.
                    ("Demote ↓", new Action(() => ResetDesignPaintString(panelDesignKey, panelDesignName))),
                    // Promote ↑: copy class overrides up into nation (class still overrides).
                    ("Promote ↑", new Action(() => PromoteDesignToNation(panelDesignKey, panelDesignName, nation))),
                    // Swap ↕: exchange the nation and class paint strings atomically.
                    ("Swap ↕", new Action(() => SwapDesignAndNation(panelDesignKey, panelDesignName, nation))),
                });
        }
        else
        {
            panelClassSwatches.Clear();
        }

        constructorPaintPanel.transform.SetAsLastSibling();
        Melon<UADVanillaPlusMod>.Logger.Msg(
            hasDesign
                ? $"UADVP ship paints panel opened for {nation.Label}, class {panelDesignName} ({panelDesignKey})."
                : $"UADVP ship paints panel opened for {nation.Label} (no design context)."
        );
    }

    private static void BuildPanelChannelHeader(Transform parent)
    {
        GameObject row = new("UADVP_PanelChannelHeader");
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = new Color(0f, 0f, 0f, 0f);
        rowImage.raycastTarget = false;
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 16f, preferredHeight: 16f, flexibleWidth: 1f);

        // Aligns with the row-label width (92) in BuildPanelTargetRow.
        GameObject leadSpacer = new("Spacer");
        leadSpacer.transform.SetParent(row.transform, false);
        Image leadImage = leadSpacer.AddComponent<Image>();
        leadImage.color = new Color(0f, 0f, 0f, 0f);
        leadImage.raycastTarget = false;
        AddLayout(leadSpacer, minWidth: 92f, preferredWidth: 92f, flexibleWidth: 0f);

        foreach (PaintArea area in DesignHullColorProofPatch.AllPickerChannels)
        {
            Text label = AddText(row.transform, ShortChannelLabel(area), 9, TextAnchor.MiddleCenter);
            AddLayout(label.gameObject, minWidth: 26f, preferredWidth: 26f, minHeight: 14f, preferredHeight: 14f, flexibleWidth: 0f);
        }
    }

    // Short labels (max 6 chars) so the channel-header row above the swatches stays
    // readable at 9pt with 26-px column width.
    private static string ShortChannelLabel(PaintArea channel)
        => channel switch
        {
            PaintArea.HullSide => "Hull",
            PaintArea.Superstructure => "Super",
            PaintArea.Gun => "Turret",
            PaintArea.Barbette => "Barb",
            PaintArea.Deck => "Deck",
            PaintArea.Bottom => "Bottom",
            PaintArea.Roof => "Detail",
            PaintArea.Barrel => "Barrel",
            PaintArea.Banner => "Trim",
            _ => "?",
        };

    private static string TruncateClassLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "This Class:";
        if (name.Length <= 22)
            return $"{name}:";
        return $"{name.Substring(0, 21).TrimEnd()}…:";
    }

    private static void BuildPanelTargetRow(
        Transform parent,
        string label,
        Dictionary<PaintArea, Color32> colors,
        Dictionary<PaintArea, Image> swatchStorage,
        Action<PaintArea, Color32> onSwatchClick,
        (string Label, Action OnPress)[] actionButtons)
    {
        GameObject row = new("UADVP_PanelTargetRow");
        row.transform.SetParent(parent, false);
        Image rowImage = row.AddComponent<Image>();
        rowImage.color = new Color(0f, 0f, 0f, 0f);
        rowImage.raycastTarget = false;
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(row, minHeight: 30f, preferredHeight: 30f, flexibleWidth: 1f);

        Text labelText = AddText(row.transform, label, 12, TextAnchor.MiddleLeft);
        AddLayout(labelText.gameObject, minWidth: 92f, preferredWidth: 92f, flexibleWidth: 0f);

        swatchStorage.Clear();
        foreach (PaintArea area in DesignHullColorProofPatch.AllPickerChannels)
        {
            if (!colors.TryGetValue(area, out Color32 channelColor))
                continue;
            Image fill = AddRawPanelSwatch(row.transform, area, channelColor, onSwatchClick);
            swatchStorage[area] = fill;
        }

        GameObject spacer = new("Spacer");
        spacer.transform.SetParent(row.transform, false);
        Image spacerImage = spacer.AddComponent<Image>();
        spacerImage.color = new Color(0f, 0f, 0f, 0f);
        spacerImage.raycastTarget = false;
        AddLayout(spacer, minWidth: 4f, flexibleWidth: 1f);

        foreach ((string actionLabel, Action onPress) in actionButtons)
            AddActionButton(row.transform, actionLabel, onPress, width: actionLabel.Length > 6 ? 72f : 56f);
    }

    private static Image AddRawPanelSwatch(Transform parent, PaintArea channel, Color32 color, Action<PaintArea, Color32> onClick)
    {
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
        button.onClick.AddListener(new System.Action(() => onClick(channel, color)));

        AddTooltip(swatchObject, $"{ChannelLabel(channel)}\nCurrent: {HexFor(color)}\nClick to pick a color.");
        return fill;
    }

    private static void ResetDesignPaintString(string designKey, string designName)
    {
        if (string.IsNullOrEmpty(designKey))
            return;

        ClosePaintPicker();
        if (ModSettings.SetDesignShipPaintString(designKey, string.Empty))
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange($"design {designName} reset");

        // Rebuild the panel since both target rows may have changed appearance.
        if (constructorPaintPanel != null)
        {
            CloseConstructorPaintPanel();
            OpenConstructorPaintPanel();
        }
    }

    private static void PromoteDesignToNation(string designKey, string designName, DesignHullColorProofPatch.NationPaintUiInfo nation)
    {
        if (string.IsNullOrEmpty(designKey))
            return;

        ClosePaintPicker();
        bool changed = DesignHullColorProofPatch.PromoteDesignToNation(designKey, nation.Key);
        if (changed)
        {
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange($"promote design {designName} to {nation.Label}");
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP ship paints: promoted design {designName} ({designKey}) overrides to nation {nation.Label}.");
        }
        else
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP ship paints: no per-class overrides to promote for {designName}.");
        }

        if (constructorPaintPanel != null)
        {
            CloseConstructorPaintPanel();
            OpenConstructorPaintPanel();
        }
    }

    // Atomically exchanges the nation paint string with the design paint string —
    // after the swap, the nation displays what the class had as overrides, and the
    // class layer holds what the nation had. Useful for "I want my whole nation to
    // look like this class, and this one class to look like the old nation."
    private static void SwapDesignAndNation(string designKey, string designName, DesignHullColorProofPatch.NationPaintUiInfo nation)
    {
        if (string.IsNullOrEmpty(designKey))
            return;

        ClosePaintPicker();

        string previousNationString = ModSettings.NationShipPaintString(nation.Key);
        string previousDesignString = ModSettings.DesignShipPaintString(designKey);

        if (string.Equals(previousNationString, previousDesignString, StringComparison.OrdinalIgnoreCase))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP ship paints: swap is a no-op — {nation.Label} and {designName} share identical paint strings.");
            return;
        }

        // Suppress per-set log spam; emit one summary line below.
        ModSettings.SetNationShipPaintString(nation.Key, previousDesignString, logChange: false);
        ModSettings.SetDesignShipPaintString(designKey, previousNationString, logChange: false);
        DesignHullColorProofPatch.ApplyNationPaintSettingsChange(
            $"swap nation {nation.Label} ↔ design {designName}");

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP ship paints: swapped nation {nation.Label} ↔ design {designName} ({designKey}).");

        if (constructorPaintPanel != null)
        {
            CloseConstructorPaintPanel();
            OpenConstructorPaintPanel();
        }
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
        // Nation row shows the pure nation colors (what's stored for the country).
        // Class row shows the layered effective colors (what the class actually looks
        // like once both layers are applied).
        if (DesignHullColorProofPatch.TryResolveAllNationPaintColors(panelNationKey, out Dictionary<PaintArea, Color32> nationOnly))
        {
            foreach (KeyValuePair<PaintArea, Image> entry in panelSwatches)
            {
                if (nationOnly.TryGetValue(entry.Key, out Color32 c))
                    entry.Value.color = c;
            }
        }

        if (panelClassSwatches.Count > 0
            && DesignHullColorProofPatch.TryResolveLayeredPaintColors(panelNationKey, panelDesignKey, out Dictionary<PaintArea, Color32> layered))
        {
            foreach (KeyValuePair<PaintArea, Image> entry in panelClassSwatches)
            {
                if (layered.TryGetValue(entry.Key, out Color32 c))
                    entry.Value.color = c;
            }
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
        panelClassSwatches.Clear();
        panelNationKey = string.Empty;
        panelDesignKey = string.Empty;
        panelDesignName = string.Empty;
    }

    private static void OpenPaintPicker(DesignHullColorProofPatch.NationPaintUiInfo nation, PaintArea channel)
        => OpenPaintPicker(nation, channel, string.Empty, string.Empty);

    private static void OpenPaintPicker(DesignHullColorProofPatch.NationPaintUiInfo nation, PaintArea channel, string designKey, string designName)
    {
        ClosePaintPicker();

        // When editing a design override, show the effective layered colors. The user
        // sees the same color they see on the ship, and edits go into the design slot.
        bool editingDesign = !string.IsNullOrEmpty(designKey);
        bool resolved = editingDesign
            ? DesignHullColorProofPatch.TryResolveLayeredPaintColors(nation.Key, designKey, out Dictionary<PaintArea, Color32> colors)
            : DesignHullColorProofPatch.TryResolveAllNationPaintColors(nation.Key, out colors);
        if (!resolved)
            return;

        GameObject? popupRoot = FindPath("Global/Ui/UiMain/Popup");
        if (popupRoot == null)
            return;

        if (!colors.TryGetValue(channel, out Color32 current))
            current = new Color32(128, 128, 128, 255);

        pickerNation = nation;
        pickerChannel = channel;
        pickerOriginalChannelColor = current;
        pickerDesignKey = editingDesign ? designKey : string.Empty;
        pickerDesignName = editingDesign ? designName : string.Empty;

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
        windowRect.sizeDelta = new Vector2(260f, 432f);

        VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = 14, right = 14, top = 12, bottom = 12 };
        layout.spacing = 8f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        string pickerTitle = editingDesign
            ? $"{designName} — {ChannelLabel(channel)}  (class)"
            : $"{nation.Label} — {ChannelLabel(channel)}  (nation)";
        AddText(window.transform, pickerTitle, 14, TextAnchor.MiddleLeft);
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

        // Naval preset row. Colors chosen to cover the common channel intents:
        // hull greys (battleship/haze), trim accent (gold), bold accent (navy
        // blue), deck/metal defaults that match the painter's built-ins (teak
        // / gunmetal / anti-fouling red), and a WW2-camo accent (olive drab).
        AddPaintPickerPresetRow(window.transform);

        // Custom user-defined presets row. Save button captures the current
        // picker color; shift-click any custom swatch to delete it.
        AddPaintPickerUserPresetRow(window.transform);

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
        Melon<UADVanillaPlusMod>.Logger.Msg(
            editingDesign
                ? $"UADVP paint picker opened for class {designName} ({designKey}) / {ChannelLabel(channel)} at {HexFor(current)}."
                : $"UADVP paint picker opened for nation {nation.Label} / {ChannelLabel(channel)} at {HexFor(current)}.");
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
        if (!string.IsNullOrEmpty(pickerDesignKey))
        {
            // Class target: only the overridden channels go into the design's paint string.
            if (!DesignHullColorProofPatch.TryResolveDesignPaintOverrides(pickerDesignKey, out Dictionary<PaintArea, Color32> designColors))
                return;
            designColors[pickerChannel] = picked;
            string serialized = DesignHullColorProofPatch.BuildNationPaintString(designColors);
            if (ModSettings.SetDesignShipPaintString(pickerDesignKey, serialized, logChange: false))
                DesignHullColorProofPatch.ApplyNationPaintSettingsChange("live picker drag (class)");
            return;
        }

        // Nation target.
        if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> colors))
            return;

        colors[pickerChannel] = picked;
        string serialized2 = DesignHullColorProofPatch.BuildNationPaintString(colors);
        if (ModSettings.SetNationShipPaintString(pickerNation.Key, serialized2, logChange: false))
            DesignHullColorProofPatch.ApplyNationPaintSettingsChange("live picker drag (nation)");
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
        bool editingDesign = !string.IsNullOrEmpty(pickerDesignKey);
        Dictionary<PaintArea, Color32> effective;
        if (editingDesign
            ? DesignHullColorProofPatch.TryResolveLayeredPaintColors(pickerNation.Key, pickerDesignKey, out effective)
            : DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out effective))
        {
            if (effective.TryGetValue(pickerChannel, out Color32 channelColor))
            {
                string targetLabel = editingDesign ? $"class {pickerDesignName}" : $"nation {pickerNation.Label}";
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP option: Ship Paints applied {targetLabel} / {ChannelLabel(pickerChannel)} = {HexFor(channelColor)}.");
            }
        }

        ClosePaintPicker();
        if (menu != null && menu.activeInHierarchy)
            RefreshMenu();
        RefreshLauncherButton();
    }

    private static void CancelPaintPicker()
    {
        if (!string.IsNullOrEmpty(pickerDesignKey))
        {
            if (DesignHullColorProofPatch.TryResolveDesignPaintOverrides(pickerDesignKey, out Dictionary<PaintArea, Color32> designColors))
            {
                designColors[pickerChannel] = pickerOriginalChannelColor;
                string serialized = DesignHullColorProofPatch.BuildNationPaintString(designColors);
                if (ModSettings.SetDesignShipPaintString(pickerDesignKey, serialized, logChange: false))
                    DesignHullColorProofPatch.ApplyNationPaintSettingsChange("picker cancel revert (class)");
            }
        }
        else if (DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out Dictionary<PaintArea, Color32> colors))
        {
            colors[pickerChannel] = pickerOriginalChannelColor;
            string serialized = DesignHullColorProofPatch.BuildNationPaintString(colors);
            if (ModSettings.SetNationShipPaintString(pickerNation.Key, serialized, logChange: false))
                DesignHullColorProofPatch.ApplyNationPaintSettingsChange("picker cancel revert (nation)");
        }

        ClosePaintPicker();
        if (menu != null && menu.activeInHierarchy)
            RefreshMenu();
        RefreshLauncherButton();
    }

    private static void LoadPaintPickerChannelDefault()
    {
        // For class target: "Default" snaps the channel back to the nation's effective
        // color (i.e. clears the per-class override visually). For nation target:
        // "Default" snaps to the built-in nation scheme value.
        Dictionary<PaintArea, Color32> defaults;
        if (!string.IsNullOrEmpty(pickerDesignKey))
        {
            if (!DesignHullColorProofPatch.TryResolveAllNationPaintColors(pickerNation.Key, out defaults))
                return;
        }
        else if (!DesignHullColorProofPatch.TryGetAllDefaultNationPaintColors(pickerNation.Key, out defaults))
        {
            return;
        }
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
        pickerUserPresetsRow = null;
        pickerValueText = null;
        pickerWheelDragging = false;
        pickerDesignKey = string.Empty;
        pickerDesignName = string.Empty;
    }

    private static string ChannelLabel(PaintArea channel)
        => channel switch
        {
            PaintArea.HullSide => "Hull",
            PaintArea.Superstructure => "Super",
            PaintArea.Gun => "Turrets",
            PaintArea.Barbette => "Barbette",
            PaintArea.Deck => "Deck",
            PaintArea.Bottom => "Bottom",
            // Roof is the internal name (token: roofing/roof); user-facing label is
            // "Details" because in practice the channel catches deck-fitting details.
            PaintArea.Roof => "Details",
            PaintArea.Barrel => "Barrel",
            PaintArea.Banner => "Trim",
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

        SetPaintPickerToColor32(color);
    }

    // Converts an RGB color into the picker's HSV state, preserving hue/saturation
    // collapse on pure-black so the wheel handle and value slider end up in the
    // expected positions. Used by the hex-input handler and preset-swatch buttons.
    private static void SetPaintPickerToColor32(Color32 color)
    {
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

    private static void AddPaintPickerPresetRow(Transform parent)
    {
        GameObject presetsRow = new("UADVP_PaintPickerPresets");
        presetsRow.transform.SetParent(parent, false);
        Image presetsRowImage = presetsRow.AddComponent<Image>();
        presetsRowImage.color = new Color(0f, 0f, 0f, 0f);
        presetsRowImage.raycastTarget = false;
        HorizontalLayoutGroup presetsLayout = presetsRow.AddComponent<HorizontalLayoutGroup>();
        // Tight spacing so 8 swatches fit inside the 260-wide picker window.
        // childControlWidth MUST be true so the HLG honors each swatch's
        // LayoutElement.preferredWidth (24); otherwise it uses the swatch's
        // native RectTransform (~100) and the row blows off-screen.
        presetsLayout.spacing = 4f;
        presetsLayout.childAlignment = TextAnchor.MiddleLeft;
        presetsLayout.childControlHeight = true;
        presetsLayout.childControlWidth = true;
        presetsLayout.childForceExpandHeight = false;
        presetsLayout.childForceExpandWidth = false;
        AddLayout(presetsRow, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);

        // (name, hex, tooltip). Hex strings are authoritative — converted to
        // Color32 inline so the swatch and the picker write the same value.
        (string Name, byte R, byte G, byte B, string Tooltip)[] presets =
        {
            ("BattleshipGrey",   0x7C, 0x86, 0x8D, "Battleship grey (#7C868D)"),
            ("HazeGrey",         0xA8, 0xB0, 0xB8, "Haze grey (#A8B0B8)"),
            ("NavyBlue",         0x1B, 0x28, 0x45, "Navy blue (#1B2845)"),
            ("NavyGold",         0xB8, 0x86, 0x0B, "Darker gold (#B8860B)"),
            ("OliveDrab",        0x5B, 0x61, 0x49, "Olive drab (#5B6149)"),
            ("Teak",             0xD4, 0xA6, 0x6B, "Teak deck (#D4A66B) — default deck"),
            ("Gunmetal",         0x52, 0x52, 0x57, "Gunmetal (#525257) — default metal"),
            ("AntiFoulingRed",   0x73, 0x1A, 0x1A, "Anti-fouling red (#731A1A) — default bottom"),
        };

        foreach ((string name, byte r, byte g, byte b, string tooltip) in presets)
        {
            Color32 color = new(r, g, b, 255);
            AddPickerQuickSwatch(presetsRow.transform, name, color, () => SetPaintPickerToColor32(color), tooltip);
        }
    }

    // User-defined preset row: shows saved custom colors plus a trailing
    // "+ Save" button that captures the picker's current color. Shift-click on
    // any saved swatch removes it. The row Transform is cached so we can
    // rebuild just this row after save/delete without re-opening the picker.
    private static void AddPaintPickerUserPresetRow(Transform parent)
    {
        pickerUserPresetsRow = new GameObject("UADVP_PaintPickerUserPresets");
        pickerUserPresetsRow.transform.SetParent(parent, false);
        Image rowImage = pickerUserPresetsRow.AddComponent<Image>();
        rowImage.color = new Color(0f, 0f, 0f, 0f);
        rowImage.raycastTarget = false;
        HorizontalLayoutGroup rowLayout = pickerUserPresetsRow.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlHeight = true;
        // Honor each swatch's LayoutElement preferredWidth — same fix as the
        // default presets row, otherwise children blow up to native rect width.
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = false;
        AddLayout(pickerUserPresetsRow, minHeight: 26f, preferredHeight: 26f, flexibleWidth: 1f);

        PopulatePaintPickerUserPresetRow();
    }

    private static void PopulatePaintPickerUserPresetRow()
    {
        if (pickerUserPresetsRow == null)
            return;

        // Destroy any existing swatches/buttons so we can rebuild from the
        // current saved preset list. DestroyImmediate so the HLG re-flows
        // synchronously before we add the new children.
        Transform rowTransform = pickerUserPresetsRow.transform;
        for (int i = rowTransform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(rowTransform.GetChild(i).gameObject);

        List<Color32> presets = ModSettings.UserPaintPresets();
        for (int i = 0; i < presets.Count; i++)
        {
            int presetIndex = i;
            Color32 color = presets[i];
            string tooltip = $"Custom preset {HexFor(color)}\nClick to apply. Shift-click to delete.";
            AddPickerQuickSwatch(rowTransform, $"UserPreset_{i}", color,
                () => OnUserPaintPresetClicked(presetIndex), tooltip);
        }

        // Save button is always present. Tooltip shows current cap usage.
        AddActionButton(rowTransform, "+ Save",
            SaveCurrentPaintPickerColorAsPreset, width: 60f);
    }

    private static void OnUserPaintPresetClicked(int index)
    {
        // Shift-click deletes; plain click applies.
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            if (ModSettings.RemoveUserPaintPresetAt(index))
                PopulatePaintPickerUserPresetRow();
            return;
        }

        List<Color32> presets = ModSettings.UserPaintPresets();
        if (index < 0 || index >= presets.Count)
            return;
        SetPaintPickerToColor32(presets[index]);
    }

    private static void SaveCurrentPaintPickerColorAsPreset()
    {
        Color rgb = Color.HSVToRGB(pickerCurrentH, pickerCurrentS, pickerCurrentV);
        Color32 color = new(
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.g * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(rgb.b * 255f), 0, 255),
            byte.MaxValue);
        if (ModSettings.AddUserPaintPreset(color))
            PopulatePaintPickerUserPresetRow();
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
