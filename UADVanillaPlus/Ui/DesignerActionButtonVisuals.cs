using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.UserInterface;

internal enum DesignerActionButtonVisual
{
    PartsOnly,
    ArmorFill,
    SmartRefit
}

internal static class DesignerActionButtonVisuals
{
    private const string IconName = "UADVP_ActionIcon";
    private const int IconTextureSize = 64;

    private static Sprite? partsIcon;
    private static Sprite? armorIcon;
    private static Sprite? smartRefitIcon;

    internal static void Apply(GameObject? buttonObject, DesignerActionButtonVisual visual)
    {
        if (buttonObject == null)
            return;

        VisualStyle style = StyleFor(visual);
        Button? button = buttonObject.GetComponent<Button>() ?? buttonObject.GetComponentInChildren<Button>(true);
        Image? background = ResolveBackground(buttonObject, button);
        Graphic? targetGraphic = button?.targetGraphic;

        if (background != null)
        {
            background.color = style.Normal;
            background.raycastTarget = true;
            if (button != null && button.targetGraphic == null)
                button.targetGraphic = background;
        }

        if (button != null)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = style.Normal;
            colors.highlightedColor = style.Highlighted;
            colors.pressedColor = style.Pressed;
            colors.selectedColor = style.Highlighted;
            colors.disabledColor = style.Disabled;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            button.colors = colors;
        }

        HideInheritedImages(buttonObject, background, targetGraphic);
        SetTextVisible(buttonObject, style.ShowText);

        if (style.Icon == null)
        {
            ClearOwnedVisuals(buttonObject);
            return;
        }

        Image icon = EnsureIcon(buttonObject);
        icon.gameObject.SetActive(true);
        icon.sprite = style.Icon;
        icon.color = style.IconColor;
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        RectTransform iconRect = icon.GetComponent<RectTransform>() ?? icon.gameObject.AddComponent<RectTransform>();
        float size = IconSize(buttonObject);
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.sizeDelta = new Vector2(size, size);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.localScale = Vector3.one;
        iconRect.localRotation = Quaternion.identity;

        try { icon.transform.SetAsLastSibling(); }
        catch { }
    }

    internal static void ClearOwnedVisuals(GameObject? buttonObject)
    {
        GameObject? iconObject = FindDirectChild(buttonObject, IconName);
        if (iconObject == null)
            return;

        Image? icon = iconObject.GetComponent<Image>();
        if (icon != null)
            icon.sprite = null;

        iconObject.SetActive(false);
    }

    private static VisualStyle StyleFor(DesignerActionButtonVisual visual)
    {
        return visual switch
        {
            DesignerActionButtonVisual.PartsOnly => new VisualStyle(
                new Color(0.12f, 0.48f, 0.26f, 0.95f),
                new Color(0.18f, 0.64f, 0.34f, 1f),
                new Color(0.08f, 0.34f, 0.18f, 1f),
                new Color(0.07f, 0.22f, 0.13f, 0.72f),
                Color.white,
                PartsIcon(),
                false),
            DesignerActionButtonVisual.ArmorFill => new VisualStyle(
                new Color(0.86f, 0.62f, 0.10f, 0.96f),
                new Color(1.00f, 0.76f, 0.18f, 1f),
                new Color(0.66f, 0.45f, 0.06f, 1f),
                new Color(0.40f, 0.29f, 0.08f, 0.72f),
                new Color(0.08f, 0.075f, 0.06f, 1f),
                ArmorIcon(),
                false),
            _ => new VisualStyle(
                new Color(0.78f, 0.30f, 0.30f, 0.96f),
                new Color(0.95f, 0.42f, 0.42f, 1f),
                new Color(0.58f, 0.18f, 0.18f, 1f),
                new Color(0.40f, 0.16f, 0.16f, 0.72f),
                Color.white,
                SmartRefitIcon(),
                false)
        };
    }

    private static Image? ResolveBackground(GameObject buttonObject, Button? button)
    {
        Image? background = button?.targetGraphic?.TryCast<Image>();
        if (background != null)
            return background;

        background = buttonObject.GetComponent<Image>();
        if (background != null)
        {
            if (button != null)
                button.targetGraphic = background;
            return background;
        }

        background = buttonObject.AddComponent<Image>();
        if (button != null)
            button.targetGraphic = background;

        return background;
    }

    private static void HideInheritedImages(GameObject buttonObject, Image? background, Graphic? targetGraphic)
    {
        foreach (Image image in buttonObject.GetComponentsInChildren<Image>(true))
        {
            if (image == null)
                continue;

            GameObject imageObject = image.gameObject;
            if (image == background ||
                image == targetGraphic ||
                imageObject == buttonObject ||
                imageObject.name.StartsWith("UADVP_", StringComparison.Ordinal))
            {
                continue;
            }

            image.enabled = false;
            image.raycastTarget = false;
        }
    }

    private static void SetTextVisible(GameObject buttonObject, bool visible)
    {
        foreach (TMP_Text text in buttonObject.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text != null)
                text.enabled = visible;
        }

        foreach (Text text in buttonObject.GetComponentsInChildren<Text>(true))
        {
            if (text != null)
                text.enabled = visible;
        }
    }

    private static Image EnsureIcon(GameObject buttonObject)
    {
        GameObject? iconObject = FindDirectChild(buttonObject, IconName);
        if (iconObject == null)
        {
            iconObject = new GameObject(IconName);
            iconObject.AddComponent<RectTransform>();
            iconObject.transform.SetParent(buttonObject.transform, false);
        }

        return iconObject.GetComponent<Image>() ?? iconObject.AddComponent<Image>();
    }

    private static GameObject? FindDirectChild(GameObject? root, string name)
    {
        Transform? transform = root?.transform;
        if (transform == null)
            return null;

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform? child = transform.GetChild(i);
            if (child != null && child.gameObject.name == name)
                return child.gameObject;
        }

        return null;
    }

    private static float IconSize(GameObject buttonObject)
    {
        RectTransform? rect = buttonObject.GetComponent<RectTransform>();
        float height = rect != null ? rect.rect.height : 0f;
        return height > 10f ? Mathf.Clamp(height * 0.7f, 28f, 40f) : 34f;
    }

    private static Sprite PartsIcon()
    {
        if (partsIcon != null)
            return partsIcon;

        Texture2D texture = NewIconTexture("UADVP_PartsOnlyIcon");
        Color32[] pixels = TransparentPixels();
        Color32 white = new(255, 255, 255, 255);
        Color32 shadow = new(20, 38, 28, 170);

        FillRect(pixels, 15, 18, 20, 16, shadow);
        FillRect(pixels, 29, 13, 20, 16, shadow);
        FillRect(pixels, 30, 35, 20, 16, shadow);
        FillRect(pixels, 12, 15, 20, 16, white);
        FillRect(pixels, 26, 10, 20, 16, white);
        FillRect(pixels, 27, 32, 20, 16, white);
        FillRect(pixels, 47, 18, 4, 18, white);
        FillRect(pixels, 40, 25, 18, 4, white);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        partsIcon = CreateSprite(texture);
        return partsIcon;
    }

    private static Sprite ArmorIcon()
    {
        if (armorIcon != null)
            return armorIcon;

        Texture2D texture = NewIconTexture("UADVP_ArmorFillIcon");
        Color32[] pixels = TransparentPixels();
        Color32 dark = new(18, 18, 17, 255);
        Color32 highlight = new(48, 43, 32, 230);

        for (int y = 8; y <= 52; y++)
        {
            float progress = Mathf.Clamp01((y - 8f) / 44f);
            int halfWidth = progress < 0.45f
                ? 19
                : Mathf.RoundToInt(Mathf.Lerp(19f, 3f, (progress - 0.45f) / 0.55f));
            int center = 32;
            FillRect(pixels, center - halfWidth, y, halfWidth * 2 + 1, 1, dark);
        }

        FillRect(pixels, 27, 16, 5, 25, highlight);
        FillRect(pixels, 43, 16, 4, 17, new Color32(0, 0, 0, 0));
        FillRect(pixels, 37, 22, 16, 4, dark);
        FillRect(pixels, 43, 16, 4, 16, dark);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        armorIcon = CreateSprite(texture);
        return armorIcon;
    }

    private static Sprite SmartRefitIcon()
    {
        if (smartRefitIcon != null)
            return smartRefitIcon;

        Texture2D texture = NewIconTexture("UADVP_SmartRefitIcon");
        Color32[] pixels = TransparentPixels();
        Color32 white = new(255, 255, 255, 255);
        Color32 shadow = new(70, 18, 18, 155);

        FillRect(pixels, 12, 34, 21, 14, shadow);
        FillRect(pixels, 10, 32, 21, 14, white);
        FillRect(pixels, 15, 17, 5, 20, shadow);
        FillRect(pixels, 13, 15, 5, 20, white);
        FillRect(pixels, 9, 15, 13, 5, white);
        FillRect(pixels, 35, 34, 5, 14, shadow);
        FillRect(pixels, 33, 32, 5, 14, white);
        FillRect(pixels, 27, 32, 17, 5, white);

        FillRect(pixels, 45, 16, 5, 28, white);
        FillRect(pixels, 38, 21, 19, 5, white);
        FillRect(pixels, 41, 17, 13, 5, white);
        FillRect(pixels, 44, 13, 7, 5, white);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        smartRefitIcon = CreateSprite(texture);
        return smartRefitIcon;
    }

    private static Texture2D NewIconTexture(string name)
        => new(IconTextureSize, IconTextureSize, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

    private static Color32[] TransparentPixels()
    {
        Color32[] pixels = new Color32[IconTextureSize * IconTextureSize];
        Color32 clear = new(0, 0, 0, 0);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;
        return pixels;
    }

    private static void FillRect(Color32[] pixels, int x, int y, int width, int height, Color32 color)
    {
        int minX = Mathf.Clamp(x, 0, IconTextureSize - 1);
        int minY = Mathf.Clamp(y, 0, IconTextureSize - 1);
        int maxX = Mathf.Clamp(x + width - 1, 0, IconTextureSize - 1);
        int maxY = Mathf.Clamp(y + height - 1, 0, IconTextureSize - 1);
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
                pixels[(py * IconTextureSize) + px] = color;
        }
    }

    private static Sprite CreateSprite(Texture2D texture)
        => Sprite.Create(
            texture,
            new Rect(0f, 0f, IconTextureSize, IconTextureSize),
            new Vector2(0.5f, 0.5f),
            100f);

    private readonly record struct VisualStyle(
        Color Normal,
        Color Highlighted,
        Color Pressed,
        Color Disabled,
        Color IconColor,
        Sprite? Icon,
        bool ShowText);
}
