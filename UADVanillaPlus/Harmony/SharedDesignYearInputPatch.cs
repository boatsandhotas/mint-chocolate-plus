using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Patch intent: let players type an exact shared-design year instead of using
// vanilla's coarse year selector. This is UI-only; vanilla still owns the
// actual shared-design refresh and filtering.
internal static class SharedDesignYearInputPatch
{
    private const string LogPrefix = "UADVP shared-design year input";
    private const string InputName = "UADVP_SharedDesignYearInput";
    private const string FlagGridName = "UADVP_SharedDesignFlagGrid";
    private const int FlagGridColumns = 4;
    private const float FlagButtonWidth = 36f;
    private const float FlagButtonHeight = 24f;
    private const float FlagGridSpacing = 4f;
    private const int MinYear = 1890;
    private const int MaxYear = 1960;

    private const string NationYearPath =
        "Global/Ui/UiMain/Constructor/Left/Scroll View/Viewport/Cont/NationAndYearSelection";
    private const string ChooseCountryPath =
        "Global/Ui/UiMain/Constructor/Left/Scroll View/Viewport/Cont/NationAndYearSelection/ChooseCountry";
    private const string ChooseYearPath =
        "Global/Ui/UiMain/Constructor/Left/Scroll View/Viewport/Cont/NationAndYearSelection/ChooseYear";
    private const string ShipNameTemplatePath =
        "Global/Ui/UiMain/Constructor/Left/Scroll View/Viewport/Cont/FoldShipSettings/ShipSettings/ShipName";

    private static GameObject? vanillaChooseYear;
    private static GameObject? vanillaChooseCountry;
    private static GameObject? flagGridObject;
    private static GameObject? yearInputObject;
    private static GameObject? yearEditObject;
    private static GameObject? yearStaticObject;
    private static GameObject? yearBackgroundObject;
    private static InputField? yearInputField;
    private static Text? yearStaticText;
    private static TMP_Text? yearStaticTmpText;
    private static bool suppressInputEvents;
    private static bool loggedAttached;
    private static bool loggedMissingTemplate;
    private static bool loggedFlagGridAttached;
    private static bool loggedMissingFlagGridSource;
    private static int lastLiveCampaignYear;
    private static string? flagGridSignature;

    internal static void RefreshConstructorUi()
    {
        try
        {
            if (!GameManager.IsSharedDesignConstructor)
            {
                RestoreVanillaSelector();
                return;
            }

            GameObject? nationYear = FindPath(NationYearPath);
            GameObject? chooseCountry = FindPath(ChooseCountryPath);
            GameObject? chooseYear = FindPath(ChooseYearPath);
            GameObject? template = FindPath(ShipNameTemplatePath);

            if (nationYear == null)
            {
                LogMissingTemplateOnce(nationYear, template);
                return;
            }

            vanillaChooseCountry = chooseCountry ?? vanillaChooseCountry;
            vanillaChooseYear = chooseYear ?? vanillaChooseYear;

            if (template != null)
            {
                if (vanillaChooseYear != null)
                    vanillaChooseYear.SetActive(false);

                GameObject input = EnsureYearInput(nationYear, template);
                input.SetActive(true);
                SyncYearText(CurrentSharedDesignYear());
            }
            else
            {
                LogMissingTemplateOnce(nationYear, template);
            }

            EnsureFlagGrid(nationYear, chooseCountry);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: UI refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void SyncYearText(int year)
    {
        if (year <= 0)
            year = CurrentSharedDesignYear();

        suppressInputEvents = true;
        try
        {
            string text = year.ToString(CultureInfo.InvariantCulture);
            if (yearInputField != null)
                yearInputField.text = text;
            SetText(yearStaticObject, text);
            if (yearStaticText != null)
                yearStaticText.text = text;
            if (yearStaticTmpText != null)
                yearStaticTmpText.text = text;
        }
        finally
        {
            suppressInputEvents = false;
        }
    }

    internal static void CaptureCampaignYear(CampaignController? campaign, string source)
    {
        int year = Safe(() => campaign!.CurrentDate.AsDate().Year, 0);
        if (year < MinYear || year > MaxYear)
            return;

        if (lastLiveCampaignYear == year)
            return;

        lastLiveCampaignYear = year;
        Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: captured campaign year={year} source={source}.");
    }

    internal static void CaptureActiveCampaignYear(string source)
    {
        if (!Safe(() => GameManager.IsCampaign, false))
            return;

        CaptureCampaignYear(CampaignController.Instance, source);
    }

    internal static int ApplyPreferredInitialSharedDesignYear(int vanillaYear)
    {
        int preferredYear = PreferredInitialSharedDesignYear(vanillaYear);
        int currentYear = CurrentSharedDesignYear();
        if (preferredYear == currentYear)
            return preferredYear;

        PlayerData? nation = CurrentSharedDesignPlayer();
        if (nation == null || GameManager.Instance == null || G.ui == null)
            return preferredYear;

        try
        {
            G.ui.sharedDesignYear = preferredYear;
            GameManager.Instance.RefreshSharedDesign(preferredYear, nation);
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"{LogPrefix}: defaulted shared-design year {currentYear}->{preferredYear} source={(IsCachedCampaignYearValid() ? "last-campaign" : "vanilla")}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to default shared-design year to {preferredYear}: {ex.GetType().Name}: {ex.Message}");
        }

        return preferredYear;
    }

    internal static int PreserveCurrentYearForNewDesign(int vanillaYear, bool log)
    {
        int currentYear = CurrentSharedDesignYear();
        int preservedYear = currentYear >= MinYear && currentYear <= MaxYear
            ? currentYear
            : Math.Clamp(vanillaYear > 0 ? vanillaYear : MinYear, MinYear, MaxYear);

        try
        {
            if (G.ui != null)
                G.ui.sharedDesignYear = preservedYear;

            if (log)
                Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: preserved shared-design year={preservedYear} for new design.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: failed to preserve shared-design year={preservedYear} for new design: {ex.GetType().Name}: {ex.Message}");
        }

        return preservedYear;
    }

    private static GameObject EnsureYearInput(GameObject nationYear, GameObject template)
    {
        Transform? existing = nationYear.transform.Find(InputName);
        GameObject input = existing != null ? existing.gameObject : UnityEngine.Object.Instantiate(template);

        input.transform.SetParent(nationYear.transform, false);
        input.name = InputName;
        input.transform.localScale = Vector3.one;

        if (vanillaChooseYear != null)
            input.transform.SetSiblingIndex(vanillaChooseYear.transform.GetSiblingIndex());

        LayoutElement? layout = input.GetComponent<LayoutElement>();
        if (layout != null)
            layout.preferredHeight = 40f;

        ConfigureInput(input);
        yearInputObject = input;

        if (!loggedAttached)
        {
            loggedAttached = true;
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: attached direct year entry field ({MinYear}-{MaxYear}).");
        }

        return input;
    }

    private static void EnsureFlagGrid(GameObject nationYear, GameObject? chooseCountry)
    {
        List<PlayerData> nations = SharedDesignNations();
        if (nations.Count == 0)
        {
            SetCountryArrowButtonsActive(true);
            if (flagGridObject != null)
                flagGridObject.SetActive(false);
            LogMissingFlagGridSourceOnce();
            return;
        }

        GameObject grid = EnsureFlagGridObject(nationYear, chooseCountry);
        grid.SetActive(true);
        SetCountryArrowButtonsActive(false);

        int year = CurrentSharedDesignYear();
        string signature = BuildFlagGridSignature(nations, year);
        if (!string.Equals(flagGridSignature, signature, StringComparison.Ordinal) ||
            grid.transform.childCount != nations.Count)
        {
            ClearChildren(grid);
            ConfigureFlagGridLayout(grid, nations.Count);

            foreach (PlayerData nation in nations)
                AddFlagButton(grid, nation, year);

            flagGridSignature = signature;
        }

        RefreshFlagGridSelection();
        UpdateCountryDisplay(CurrentSharedDesignPlayer(), year);

        if (!loggedFlagGridAttached)
        {
            loggedFlagGridAttached = true;
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: attached nation flag grid nations={nations.Count} columns={FlagGridColumns}.");
        }
    }

    private static GameObject EnsureFlagGridObject(GameObject nationYear, GameObject? chooseCountry)
    {
        Transform? existing = nationYear.transform.Find(FlagGridName);
        GameObject grid = existing != null ? existing.gameObject : new GameObject(FlagGridName);

        if (grid.GetComponent<RectTransform>() == null)
            grid.AddComponent<RectTransform>();

        grid.transform.SetParent(nationYear.transform, false);
        grid.name = FlagGridName;
        grid.transform.localScale = Vector3.one;

        if (chooseCountry != null)
            grid.transform.SetSiblingIndex(chooseCountry.transform.GetSiblingIndex() + 1);

        flagGridObject = grid;
        return grid;
    }

    private static void ConfigureFlagGridLayout(GameObject grid, int nationCount)
    {
        GridLayoutGroup layout = grid.GetComponent<GridLayoutGroup>() ?? grid.AddComponent<GridLayoutGroup>();
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = FlagGridColumns;
        layout.cellSize = new Vector2(FlagButtonWidth, FlagButtonHeight);
        layout.spacing = new Vector2(FlagGridSpacing, FlagGridSpacing);
        layout.childAlignment = TextAnchor.UpperLeft;

        int rows = Math.Max(1, Mathf.CeilToInt(nationCount / (float)FlagGridColumns));
        float preferredHeight = rows * FlagButtonHeight + Math.Max(0, rows - 1) * FlagGridSpacing;
        float preferredWidth = FlagGridColumns * FlagButtonWidth + Math.Max(0, FlagGridColumns - 1) * FlagGridSpacing;

        LayoutElement element = grid.GetComponent<LayoutElement>() ?? grid.AddComponent<LayoutElement>();
        element.minWidth = preferredWidth;
        element.preferredWidth = preferredWidth;
        element.minHeight = preferredHeight;
        element.preferredHeight = preferredHeight;
        element.flexibleHeight = 0f;
    }

    private static void AddFlagButton(GameObject grid, PlayerData nation, int year)
    {
        GameObject buttonObject = new($"UADVP_SharedDesignFlag_{NationKey(nation)}");
        buttonObject.AddComponent<RectTransform>();
        buttonObject.transform.SetParent(grid.transform, false);
        buttonObject.transform.localScale = Vector3.one;

        LayoutElement layout = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();
        layout.minWidth = FlagButtonWidth;
        layout.preferredWidth = FlagButtonWidth;
        layout.minHeight = FlagButtonHeight;
        layout.preferredHeight = FlagButtonHeight;

        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.sizeDelta = new Vector2(FlagButtonWidth, FlagButtonHeight);

        Image background = buttonObject.AddComponent<Image>();
        background.color = FlagButtonColor(IsCurrentNation(nation));

        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = background;
        PlayerData capturedNation = nation;
        button.onClick.AddListener(new Action(() => SelectSharedDesignNation(capturedNation)));

        GameObject flagObject = new("Flag");
        flagObject.AddComponent<RectTransform>();
        flagObject.transform.SetParent(buttonObject.transform, false);
        flagObject.transform.localScale = Vector3.one;

        RectTransform flagRect = flagObject.GetComponent<RectTransform>();
        flagRect.anchorMin = Vector2.zero;
        flagRect.anchorMax = Vector2.one;
        flagRect.offsetMin = new Vector2(3f, 2f);
        flagRect.offsetMax = new Vector2(-3f, -2f);

        Image flagImage = flagObject.AddComponent<Image>();
        flagImage.sprite = NationFlag(nation, year);
        flagImage.preserveAspect = true;
        flagImage.raycastTarget = false;
        flagImage.color = Color.white;

        SetTooltip(buttonObject, NationDisplayName(nation, year));
    }

    private static void SelectSharedDesignNation(PlayerData nation)
    {
        if (nation == null || !GameManager.IsSharedDesignConstructor)
            return;

        int year = CurrentSharedDesignYear();
        try
        {
            G.ui.sharedDesignPlayer = nation;
            UpdateCountryDisplay(nation, year);
            RefreshFlagGridSelection();
            GameManager.Instance.RefreshSharedDesign(year, nation);
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: refreshed shared designs for nation={NationKey(nation)} year={year}.");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: failed to refresh nation={NationKey(nation)} year={year}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RefreshFlagGridSelection()
    {
        if (flagGridObject == null)
            return;

        PlayerData? current = CurrentSharedDesignPlayer();
        for (int i = 0; i < flagGridObject.transform.childCount; i++)
        {
            Transform child = flagGridObject.transform.GetChild(i);
            Image? background = child.gameObject.GetComponent<Image>();
            if (background == null)
                continue;

            string key = child.gameObject.name.Replace("UADVP_SharedDesignFlag_", string.Empty, StringComparison.Ordinal);
            background.color = FlagButtonColor(current != null && string.Equals(key, NationKey(current), StringComparison.Ordinal));
        }
    }

    private static void UpdateCountryDisplay(PlayerData? nation, int year)
    {
        if (nation == null)
            return;

        GameObject? chooseCountry = vanillaChooseCountry ?? FindPath(ChooseCountryPath);
        if (chooseCountry == null)
            return;

        SetText(FindChildPath(chooseCountry, "Name"), NationDisplayName(nation, year));

        Image? flagImage = FindChildPath(chooseCountry, "Flag")?.GetComponent<Image>();
        if (flagImage != null)
        {
            flagImage.sprite = NationFlag(nation, year);
            flagImage.preserveAspect = true;
            flagImage.color = Color.white;
        }
    }

    private static List<PlayerData> SharedDesignNations()
    {
        List<PlayerData> nations = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        AppendNations(G.GameData?.playersMajor, nations, seen, requireEnabled: true);
        if (nations.Count == 0)
            AppendNations(G.GameData?.playersMajor, nations, seen, requireEnabled: false);
        if (nations.Count == 0)
            AppendNations(G.GameData?.players, nations, seen, requireEnabled: true);

        return nations
            .OrderBy(nation => Safe(() => nation.order, 0f))
            .ThenBy(nation => NationDisplayName(nation, CurrentSharedDesignYear()), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendNations(
        Il2CppSystem.Collections.Generic.Dictionary<string, PlayerData>? source,
        List<PlayerData> nations,
        HashSet<string> seen,
        bool requireEnabled)
    {
        if (source == null)
            return;

        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, PlayerData> entry in source)
        {
            PlayerData nation = entry.Value;
            if (nation == null)
                continue;

            if (requireEnabled &&
                (!Safe(() => nation.enabled, true) || !Safe(() => nation.enabledForCampaign, true)))
            {
                continue;
            }

            string key = !string.IsNullOrWhiteSpace(entry.Key) ? entry.Key : NationKey(nation);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            nations.Add(nation);
        }
    }

    private static string BuildFlagGridSignature(List<PlayerData> nations, int year)
        => $"{year}:{string.Join("|", nations.Select(NationKey))}";

    private static PlayerData? CurrentSharedDesignPlayer()
    {
        try { return G.ui?.sharedDesignPlayer; }
        catch { return null; }
    }

    private static bool IsCurrentNation(PlayerData nation)
    {
        PlayerData? current = CurrentSharedDesignPlayer();
        return current != null && string.Equals(NationKey(current), NationKey(nation), StringComparison.Ordinal);
    }

    private static string NationKey(PlayerData nation)
        => SafeString(() => nation.name);

    private static string NationDisplayName(PlayerData nation, int year)
    {
        string displayName = SafeString(() => Player.GetNameUI(null, nation, year));
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName;

        displayName = SafeString(() => nation.nameUi);
        return !string.IsNullOrWhiteSpace(displayName) ? displayName : NationKey(nation);
    }

    private static Sprite? NationFlag(PlayerData nation, int year)
        => Safe(() => Player.Flag(nation, true, null, year), null)
           ?? Safe(() => Player.Flag(nation, false, null, year), null);

    private static Color FlagButtonColor(bool selected)
        => selected ? new Color(0.96f, 0.78f, 0.28f, 0.9f) : new Color(1f, 1f, 1f, 0.18f);

    private static void SetCountryArrowButtonsActive(bool active)
    {
        GameObject? chooseCountry = vanillaChooseCountry ?? FindPath(ChooseCountryPath);
        if (chooseCountry == null)
            return;

        FindChildPath(chooseCountry, "MoveLeft")?.SetActive(active);
        FindChildPath(chooseCountry, "MoveRight")?.SetActive(active);
    }

    private static void ClearChildren(GameObject target)
    {
        for (int i = target.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(target.transform.GetChild(i).gameObject);
    }

    private static void SetTooltip(GameObject target, string text)
    {
        RemoveComponent<OnEnter>(target);
        RemoveComponent<OnLeave>(target);

        OnEnter onEnter = target.AddComponent<OnEnter>();
        onEnter.action = new Action(() =>
        {
            try { G.ui?.ShowTooltip(text, target); }
            catch { }
        });

        OnLeave onLeave = target.AddComponent<OnLeave>();
        onLeave.action = new Action(() =>
        {
            try { G.ui?.HideTooltip(); }
            catch { }
        });
    }

    private static void ConfigureInput(GameObject input)
    {
        GameObject? editName = FindChildPath(input, "EditName");
        yearBackgroundObject = FindChildPath(input, "EditName/Bg");
        yearEditObject = FindChildPath(input, "EditName/Edit");
        yearStaticObject = FindChildPath(input, "EditName/Static");

        if (editName != null)
        {
            LayoutElement? editLayout = editName.GetComponent<LayoutElement>();
            if (editLayout != null)
                editLayout.preferredHeight = 40f;

            Button button = editName.GetComponent<Button>() ?? editName.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(new Action(BeginEdit));
        }

        if (yearEditObject != null)
        {
            RemoveComponent<CheckShipName>(yearEditObject);
            RemoveComponent<LocalizeText>(yearEditObject);
            yearInputField = yearEditObject.GetComponent<InputField>();
            if (yearInputField != null)
            {
                yearInputField.onEndEdit.RemoveAllListeners();
                yearInputField.onEndEdit.AddListener(new Action<string>(ApplyEnteredYear));
                yearInputField.contentType = InputField.ContentType.IntegerNumber;
                yearInputField.characterValidation = InputField.CharacterValidation.Integer;
                yearInputField.characterLimit = 4;
            }

            GameObject? placeholder = FindChildPath(yearEditObject, "Placeholder");
            SetText(placeholder, "Year");
        }

        if (yearStaticObject != null)
        {
            Transform? header = yearStaticObject.transform.Find("Header");
            if (header != null)
                header.gameObject.SetActive(false);

            GameObject? staticTextObject = FindChildPath(yearStaticObject, "Text");
            yearStaticText = staticTextObject != null ? staticTextObject.GetComponent<Text>() : null;
            yearStaticTmpText = staticTextObject != null ? staticTextObject.GetComponent<TMP_Text>() : null;
        }

        EndEditVisualState();
    }

    private static void BeginEdit()
    {
        if (yearInputField == null)
            return;

        if (yearBackgroundObject != null)
            yearBackgroundObject.SetActive(true);
        if (yearEditObject != null)
            yearEditObject.SetActive(true);
        if (yearStaticObject != null)
            yearStaticObject.SetActive(false);

        yearInputField.ActivateInputField();
    }

    private static void ApplyEnteredYear(string value)
    {
        if (suppressInputEvents)
            return;

        EndEditVisualState();

        int currentYear = CurrentSharedDesignYear();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedYear) ||
            parsedYear < MinYear ||
            parsedYear > MaxYear)
        {
            SyncYearText(currentYear);
            return;
        }

        if (parsedYear == currentYear)
        {
            SyncYearText(currentYear);
            return;
        }

        try
        {
            G.ui.sharedDesignYear = parsedYear;
            GameManager.Instance.RefreshSharedDesign(parsedYear, G.ui.sharedDesignPlayer);
            SyncYearText(parsedYear);
            Melon<UADVanillaPlusMod>.Logger.Msg($"{LogPrefix}: refreshed shared designs for year={parsedYear}.");
        }
        catch (Exception ex)
        {
            SyncYearText(currentYear);
            Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: failed to refresh year={parsedYear}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void EndEditVisualState()
    {
        if (yearBackgroundObject != null)
            yearBackgroundObject.SetActive(false);
        if (yearEditObject != null)
            yearEditObject.SetActive(false);
        if (yearStaticObject != null)
            yearStaticObject.SetActive(true);

        try
        {
            yearInputField?.DeactivateInputField();
        }
        catch
        {
        }
    }

    internal static bool TryGetCurrentSharedDesignYear(out int year)
    {
        year = 0;
        try
        {
            int current = G.ui?.sharedDesignYear ?? 0;
            if (current >= MinYear && current <= MaxYear)
            {
                year = current;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static int CurrentSharedDesignYear()
        => TryGetCurrentSharedDesignYear(out int year) ? year : MinYear;

    private static int PreferredInitialSharedDesignYear(int vanillaYear)
    {
        if (IsCachedCampaignYearValid())
            return lastLiveCampaignYear;

        return Math.Clamp(vanillaYear > 0 ? vanillaYear : MinYear, MinYear, MaxYear);
    }

    private static bool IsCachedCampaignYearValid()
        => lastLiveCampaignYear >= MinYear && lastLiveCampaignYear <= MaxYear;

    private static void RestoreVanillaSelector()
    {
        if (vanillaChooseYear != null)
            vanillaChooseYear.SetActive(true);

        if (yearInputObject != null)
            yearInputObject.SetActive(false);

        if (flagGridObject != null)
            flagGridObject.SetActive(false);

        SetCountryArrowButtonsActive(true);
    }

    private static void LogMissingTemplateOnce(GameObject? nationYear, GameObject? template)
    {
        if (loggedMissingTemplate)
            return;

        loggedMissingTemplate = true;
        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: skipped UI attach; nationYear={(nationYear != null ? "found" : "missing")}, ship-name template={(template != null ? "found" : "missing")}.");
    }

    private static void LogMissingFlagGridSourceOnce()
    {
        if (loggedMissingFlagGridSource)
            return;

        loggedMissingFlagGridSource = true;
        Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: skipped nation flag grid; no shared-design nations found.");
    }

    private static GameObject? FindPath(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        GameObject? current = GameObject.Find(parts[0]);
        for (int i = 1; current != null && i < parts.Length; i++)
            current = FindChild(current, parts[i]);

        return current;
    }

    private static GameObject? FindChildPath(GameObject parent, string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        GameObject? current = parent;
        for (int i = 0; current != null && i < parts.Length; i++)
            current = FindChild(current, parts[i]);

        return current;
    }

    private static GameObject? FindChild(GameObject parent, string name)
    {
        Transform? child = parent.transform.Find(name);
        return child == null ? null : child.gameObject;
    }

    private static void SetText(GameObject? target, string text)
    {
        if (target == null)
            return;

        Text? uiText = target.GetComponent<Text>();
        if (uiText != null)
        {
            RemoveComponent<LocalizeText>(target);
            uiText.text = text;
        }

        TMP_Text? tmpText = target.GetComponent<TMP_Text>();
        if (tmpText != null)
        {
            RemoveComponent<LocalizeText>(target);
            tmpText.text = text;
        }
    }

    private static void RemoveComponent<T>(GameObject target) where T : Component
    {
        T? component = target.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.Destroy(component);
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private static string SafeString(Func<string?> action)
    {
        try { return action() ?? string.Empty; }
        catch { return string.Empty; }
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.ConstructorUI))]
internal static class SharedDesignYearInputConstructorUiPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        SharedDesignYearInputPatch.RefreshConstructorUi();
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ToSharedDesignsConstructor), new[] { typeof(int), typeof(PlayerData), typeof(bool) })]
internal static class SharedDesignYearInputEnterConstructorPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref int year, bool forceCreateNew)
    {
        if (!forceCreateNew)
            return;

        year = SharedDesignYearInputPatch.PreserveCurrentYearForNewDesign(year, log: true);
    }

    [HarmonyPostfix]
    private static void Postfix(int year, bool forceCreateNew)
    {
        int displayYear = forceCreateNew
            ? SharedDesignYearInputPatch.PreserveCurrentYearForNewDesign(year, log: false)
            : SharedDesignYearInputPatch.ApplyPreferredInitialSharedDesignYear(year);

        SharedDesignYearInputPatch.SyncYearText(displayYear);
        SharedDesignYearInputPatch.RefreshConstructorUi();
    }
}

[HarmonyPatch(typeof(CampaignController), "OnNewTurn")]
internal static class SharedDesignYearInputCampaignYearNewTurnPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance)
        => SharedDesignYearInputPatch.CaptureCampaignYear(__instance, "new-turn");
}

[HarmonyPatch(typeof(CampaignController), nameof(CampaignController.OnLoadingScreenHide))]
internal static class SharedDesignYearInputCampaignYearLoadPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignController __instance)
        => SharedDesignYearInputPatch.CaptureCampaignYear(__instance, "campaign-load");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ToMainMenu))]
internal static class SharedDesignYearInputToMainMenuPatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => SharedDesignYearInputPatch.CaptureActiveCampaignYear("to-main-menu");
}
