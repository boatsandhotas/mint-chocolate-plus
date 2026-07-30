using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MintChipPlus.Harmony;

// Shared-design libraries can contain hundreds of rows. Vanilla renders a 3D
// ship thumbnail for each row during ConstructorUI rebuilds, so MC returns a
// cheap placeholder only inside that shared-design list rebuild scope.
internal static class SharedDesignConstructorPreviewBypassPatch
{
    private const int PlaceholderWidth = 64;
    private const int PlaceholderHeight = 32;

    private static bool suppressSharedDesignListPreviews;
    private static bool loggedFirstBypass;
    private static Texture2D? placeholderTexture;

    internal static void BeginSharedDesignListRebuild()
    {
        suppressSharedDesignListPreviews = true;
    }

    internal static void EndSharedDesignListRebuild()
    {
        suppressSharedDesignListPreviews = false;
    }

    internal static bool ShouldBypass()
    {
        return suppressSharedDesignListPreviews && GameManager.IsSharedDesignConstructor;
    }

    internal static Texture2D PlaceholderTexture()
    {
        if (placeholderTexture != null)
            return placeholderTexture;

        Texture2D texture = new(PlaceholderWidth, PlaceholderHeight, TextureFormat.RGBA32, false)
        {
            name = "UADMC_SharedDesignPreviewPlaceholder",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color32[] pixels = new Color32[PlaceholderWidth * PlaceholderHeight];
        for (int y = 0; y < PlaceholderHeight; y++)
        {
            for (int x = 0; x < PlaceholderWidth; x++)
            {
                bool border = x == 0 || y == 0 || x == PlaceholderWidth - 1 || y == PlaceholderHeight - 1;
                bool stripe = y == PlaceholderHeight / 2 || (x > 8 && x < PlaceholderWidth - 8 && y > PlaceholderHeight / 2 + 4 && y < PlaceholderHeight / 2 + 7);
                pixels[(y * PlaceholderWidth) + x] = border
                    ? new Color32(88, 96, 104, 150)
                    : stripe
                        ? new Color32(120, 130, 138, 110)
                        : new Color32(42, 46, 52, 70);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        placeholderTexture = texture;
        return texture;
    }

    internal static void LogFirstBypass()
    {
        if (loggedFirstBypass)
            return;

        loggedFirstBypass = true;
        Melon<MintChipPlusMod>.Logger.Msg(
            "UADMC shared-design UI: bypassing row preview rendering during shared-design constructor list rebuild.");
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.ConstructorUI))]
internal static class SharedDesignConstructorPreviewScopePatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (GameManager.IsSharedDesignConstructor)
            SharedDesignConstructorPreviewBypassPatch.BeginSharedDesignListRebuild();
    }

    [HarmonyFinalizer]
    private static void Finalizer()
    {
        SharedDesignConstructorPreviewBypassPatch.EndSharedDesignListRebuild();
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.GetShipPreviewTex), new[] { typeof(Ship), typeof(bool) })]
internal static class SharedDesignConstructorPreviewTexturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Texture2D __result)
    {
        if (!SharedDesignConstructorPreviewBypassPatch.ShouldBypass())
            return true;

        __result = SharedDesignConstructorPreviewBypassPatch.PlaceholderTexture();
        SharedDesignConstructorPreviewBypassPatch.LogFirstBypass();
        return false;
    }
}
