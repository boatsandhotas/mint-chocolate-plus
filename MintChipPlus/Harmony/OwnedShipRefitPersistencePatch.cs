using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// "Keep my fleet refittable." When an owned ship's design goes obsolete, the vanilla obsolescence cull
// (CampaignController.DeleteOldDesigns) marks the design template VesselEntity.Status.Erased (7) and drops
// it from player.designs. Refitting that ship then clones an already-erased design, so the refit clone is
// born erased and never commits to player.designs -> the new refit never appears in the Ship Design tab.
//
// Targeted, save-conscious fix for the MAIN human player only (this is NOT the old "use obsolete hulls in
// new designs" feature, and NOT the bulk un-eraser that corrupted saves by re-adding fleet-only ghosts to
// player.designs):
//   1. Skip DeleteOldDesigns for the main player so their design templates stop being auto-erased.
//   2. When the player refits a ship, flip ONLY the in-flight refit clone from Status.Erased (7) back to
//      Status.None (0) so vanilla commits the new refit normally. The source/base design is left obsolete
//      (status 7) on purpose -- it's the old template and the player wants it to stay obsolete; only the
//      clone (the design that actually commits to player.designs) needs to be live. We never call
//      player.designs.Add ourselves -- vanilla's own commit adds the new refit -- which avoids the GetStore
//      serialization corruption the old re-adding un-eraser hit. Manual deletes use a different path
//      (Ui.ConDeleteShip -> CampaignController.DeleteDesign) and are unaffected by the cull skip.
internal static class OwnedShipRefitPersistence
{
    private static bool loggedCullSkip;
    private static int uneraseLogCount;

    private static bool IsMainHumanPlayer(Player? player)
    {
        try { return player != null && player.isMain && !player.isAi; }
        catch { return false; }
    }

    private static bool IsMainHumanDesign(Ship? design)
    {
        if (design == null) return false;
        try { return IsMainHumanPlayer(design.player); }
        catch { return false; }
    }

    // Returns true if the obsolescence cull should be skipped for this player (the main human player).
    internal static bool ShouldSkipMainPlayerCull(Player? player)
    {
        if (!IsMainHumanPlayer(player))
            return false;

        if (!loggedCullSkip)
        {
            loggedCullSkip = true;
            Melon<MintChipPlusMod>.Logger.Msg(
                "UADMC owned-ship refit: skipping obsolescence cull (DeleteOldDesigns) for the main player so owned designs are not auto-erased (status 7). Manual deletes are unaffected.");
        }

        return true;
    }

    // Flip a single main-player design/refit template from Erased (7) back to None (0) so it -- and any
    // refit cloned from it -- can commit. Only touches design templates, never real ships or AI designs,
    // and never re-adds to player.designs (vanilla's own commit handles that).
    internal static void UneraseIfNeeded(Ship? design, string context)
    {
        if (design == null) return;
        try
        {
            if (design.status != VesselEntity.Status.Erased) return;

            bool isTemplate;
            try { isTemplate = design.isDesign || design.isRefitDesign; } catch { isTemplate = false; }
            if (!isTemplate || !IsMainHumanDesign(design)) return;

            design.status = VesselEntity.Status.None;

            if (uneraseLogCount < 60)
            {
                uneraseLogCount++;
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC owned-ship refit: un-erased design '{SafeName(design)}' (status Erased->None) at {context} so the refit can persist.");
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC owned-ship refit: un-erase failed at {context}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Un-erase every erased clone currently parked in Ui.newRefitShips (the in-flight refit working copies).
    internal static void UneraseRefitClones(string context)
    {
        try
        {
            Ui? ui = G.ui;
            if (ui == null) return;

            Il2CppSystem.Collections.Generic.HashSet<Ship>? clones = ui.newRefitShips;
            if (clones == null) return;

            foreach (Ship clone in clones)
                UneraseIfNeeded(clone, context);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning(
                $"UADMC owned-ship refit: refit-clone scan failed at {context}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafeName(Ship? s) { try { return s?.name ?? "?"; } catch { return "?"; } }
}

// 1. Skip the obsolescence cull for the main player so owned design templates stop being erased.
[HarmonyPatch]
internal static class OwnedShipRefitCullSkipPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<MintChipPlusMod>.Logger.Warning("UADMC owned-ship refit: DeleteOldDesigns not found; cull-skip disabled.");
        return available;
    }

    private static MethodBase? TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "DeleteOldDesigns", new[] { typeof(Player) });

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player player)
        => !OwnedShipRefitPersistence.ShouldSkipMainPlayerCull(player);
}

// 2a. On refit entry, un-erase only the in-flight refit clone(s) so the new refit can commit. We
// deliberately do NOT touch the source/base design -- it stays obsolete (status 7). Only the clone (the
// new refit design that actually gets committed to player.designs) needs to be non-erased.
[HarmonyPatch(typeof(Ui), nameof(Ui.RefitShip))]
internal static class OwnedShipRefitBeginUneraserPatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => OwnedShipRefitPersistence.UneraseRefitClones("refit start");
}

// 2b. Right before the refit save commits, un-erase any erased clone so vanilla commits it normally.
[HarmonyPatch(typeof(Ui), nameof(Ui.SaveDesignOnExitFromConstructor))]
internal static class OwnedShipRefitSaveUneraserPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            Ui? ui = G.ui;
            if (ui == null || !ui.isConstructorRefitMode) return;
        }
        catch { return; }

        OwnedShipRefitPersistence.UneraseRefitClones("refit save");
    }
}
