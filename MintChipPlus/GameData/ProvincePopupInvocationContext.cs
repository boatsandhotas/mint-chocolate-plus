namespace MintChipPlus.GameData;

// Click-context tracking for the province popup. Two layers:
//
//  1. ClickInvocationDepth — set by the OnPointerClick Prefix/Postfix
//     around vanilla's click handler. If the Harmony hook on
//     MapTextMeshLabel.OnPointerClick fires (which we've seen NOT happen
//     in some game builds), this is the cleanest signal that the current
//     Show call is click-driven.
//
//  2. PinnedProvinceId — a latched id of the most recently click-shown
//     province. Once latched, the popup behaves as click-anchored for
//     that province until the user explicitly dismisses it (Close button)
//     or clicks a DIFFERENT province (which re-latches). This solves the
//     "popup only visible while mouse is held" failure mode that came
//     from polling Input.GetMouseButton each Show frame: we latch on the
//     first frame the mouse is down over a label and stay latched even
//     after release.
internal static class ProvincePopupInvocationContext
{
    private static int clickInvocationDepth;

    internal static bool IsActiveClickInvocation => clickInvocationDepth > 0;

    internal static void BeginClick() => clickInvocationDepth++;
    internal static void EndClick()
    {
        if (clickInvocationDepth > 0) clickInvocationDepth--;
    }

    // Province id that has been click-pinned by the user. Empty string =
    // nothing pinned, popup follows vanilla tooltip behaviour.
    internal static string PinnedProvinceId { get; private set; } = string.Empty;

    internal static bool IsPinned => !string.IsNullOrEmpty(PinnedProvinceId);

    internal static bool IsPinnedTo(string provinceId)
        => !string.IsNullOrEmpty(provinceId) && PinnedProvinceId == provinceId;

    internal static void PinTo(string provinceId)
    {
        if (string.IsNullOrEmpty(provinceId)) return;
        PinnedProvinceId = provinceId;
    }

    internal static void Unpin() => PinnedProvinceId = string.Empty;
}
