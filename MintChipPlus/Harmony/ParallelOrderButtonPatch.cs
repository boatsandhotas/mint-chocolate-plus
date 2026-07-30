using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// Order-bar button for the Parallel station-keeping order. Reuses the proven order-button machinery
// from BattleDivisionAiControlPatch (find the order-button cluster, clone a mode button, position,
// strip inherited handlers) rather than duplicating it. The button enters the native "click an anchor
// division" targeting (ParallelOrder.BeginTargeting); the click is captured in ParallelOrderCapturePatch.
// Sits just after the AI control button so the two MC buttons don't overlap. Refreshed from the same
// Ui.RefreshShipControls postfix as the AI button.
internal static class ParallelOrderButton
{
    private const string ButtonName = "UADMC_OrderParallel";
    private const string AiButtonName = "UADMC_OrderAiControl";
    private const float ButtonSpacing = 4f;
    private static bool loggedAdded;

    internal static void Refresh(Ui ui)
    {
        if (ui == null)
            return;
        try
        {
            List<(string SourceName, GameObject Root)> roots = BattleDivisionAiControlPatch.CandidateOrderRoots(ui)
                .GroupBy(r => r.Root.Pointer)
                .Select(g => g.First())
                .ToList();
            if (roots.Count == 0)
                return;

            BattleDivisionAiControlPatch.OrderButtonContext? ctx = BattleDivisionAiControlPatch.ResolveOrderButtonContext(roots);
            if (ctx == null)
            {
                Hide(roots);
                return;
            }

            Button? button = Ensure(ctx, roots);
            if (button == null)
            {
                Hide(roots);
                return;
            }

            BattleDivisionAiControlPatch.PositionAiControlButton(button, ctx);
            PlaceAfterAiButton(button, ctx.Parent);

            bool canAssign = ParallelOrder.CanAssign(ui);
            button.gameObject.SetActive(canAssign);
            button.interactable = canAssign;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(new System.Action(() => ParallelOrder.BeginTargeting(ui, "button")));
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC parallel button failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Button? Ensure(BattleDivisionAiControlPatch.OrderButtonContext ctx, List<(string SourceName, GameObject Root)> roots)
    {
        Button? existing = FindExisting(roots);
        if (existing != null)
        {
            if (existing.transform.parent != ctx.Parent)
                existing.transform.SetParent(ctx.Parent, false);
            return existing;
        }

        GameObject go = UnityEngine.Object.Instantiate(ctx.Template.gameObject, ctx.Parent);
        go.name = ButtonName;
        go.SetActive(false);
        BattleDivisionAiControlPatch.TryRemoveInheritedPointerHandlers(go);
        BattleDivisionAiControlPatch.TryRemoveInheritedHotkeyIndicators(go);

        Button? button = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
        if (button == null)
            return null;
        button.onClick.RemoveAllListeners();
        BattleDivisionAiControlPatch.SetButtonText(go, "PAR");

        if (!loggedAdded)
        {
            loggedAdded = true;
            Melon<MintChipPlusMod>.Logger.Msg("UADMC parallel order: added Parallel order button to battle division orders.");
        }
        return button;
    }

    private static List<Button> FindAll(IEnumerable<(string SourceName, GameObject Root)> roots)
    {
        List<Button> buttons = new();
        HashSet<IntPtr> seen = new();
        foreach ((_, GameObject root) in roots)
        {
            if (root == null)
                continue;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject.name != ButtonName)
                    continue;
                Button? b = t.GetComponent<Button>() ?? t.gameObject.GetComponentInChildren<Button>(true);
                if (b == null || b.Pointer == IntPtr.Zero || !seen.Add(b.Pointer))
                    continue;
                buttons.Add(b);
            }
        }
        return buttons;
    }

    private static Button? FindExisting(IEnumerable<(string SourceName, GameObject Root)> roots)
        => FindAll(roots).FirstOrDefault();

    private static void Hide(IEnumerable<(string SourceName, GameObject Root)> roots)
    {
        foreach (Button b in FindAll(roots))
            b.gameObject.SetActive(false);
    }

    // Nudge our button to sit just right of the AI button when both are in the same parent, so they
    // don't land on the same spot.
    private static void PlaceAfterAiButton(Button button, Transform parent)
    {
        try
        {
            Transform? ai = null;
            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.gameObject.name == AiButtonName) { ai = t; break; }
            }
            if (ai == null)
                return;

            button.transform.SetSiblingIndex(ai.GetSiblingIndex() + 1);

            // If the parent uses a layout group, sibling order is enough.
            GameObject po = parent.gameObject;
            if (po.GetComponent<HorizontalLayoutGroup>() != null ||
                po.GetComponent<VerticalLayoutGroup>() != null ||
                po.GetComponent<GridLayoutGroup>() != null)
                return;

            RectTransform? aiRect = ai.GetComponent<RectTransform>();
            RectTransform? myRect = button.GetComponent<RectTransform>();
            if (aiRect == null || myRect == null)
                return;
            float aiW = aiRect.rect.width;
            float myW = myRect.rect.width;
            float aiRight = aiRect.anchoredPosition.x + aiW * (1f - aiRect.pivot.x);
            myRect.anchorMin = aiRect.anchorMin;
            myRect.anchorMax = aiRect.anchorMax;
            myRect.pivot = aiRect.pivot;
            myRect.anchoredPosition = new Vector2(aiRight + ButtonSpacing + myW * myRect.pivot.x, aiRect.anchoredPosition.y);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(Ui), "RefreshShipControls")]
internal static class ParallelOrderButtonRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix(Ui __instance) => ParallelOrderButton.Refresh(__instance);
}
