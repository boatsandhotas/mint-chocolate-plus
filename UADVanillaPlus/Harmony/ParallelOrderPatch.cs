using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// "Parallel" battle order (behavior-first, hotkey-driven for now — order-bar button to follow once the
// feel is confirmed). A selected division is told to run PARALLEL to a tagged anchor division, offset
// BEHIND it and AWAY from the enemy (DDs lurking out of fire for a torpedo run; or a trailing line).
//
// Tagging reuses the game's native "click a target division" flow (confirmed by probe: the targeting
// click routes through Division.SetScreenDivision, self=ordered division, arg=clicked anchor):
//   1. Press the hotkey -> set Ui.SelectedCommand = Screen + parallelPending (enters targeting mode).
//   2. Player clicks the anchor division -> native calls Division.SetScreenDivision(self, anchor);
//      our prefix captures self->anchor as a Parallel link and RETURNS FALSE to suppress the real
//      screen assignment.
// Each frame, for every linked division, we re-issue Division.MoveTo a point offset from the anchor's
// leader so it closes to station and matches course. A reentrancy guard keeps our own MoveTo from
// tripping the cancel-on-new-order logic. Any genuine new order, or right-click move, cancels it.
internal static class ParallelOrder
{
    // Offsets in world units (follow distance ~330; ships span a few hundred). Tunable.
    // The station is a MOVING point (it tracks the anchor), so MoveTo-ing it each frame already makes
    // the follower run parallel — no "lookahead" needed (an earlier lookahead overpowered the behind
    // offset and put the target AHEAD of the anchor).
    private const float BehindDistance = 800f;     // Astern mode: how far behind the anchor's leader
    private const float LateralDistance = 800f;    // Astern mode: how far to the disengaged side
    private const float AbreastDistance = 900f;    // Abreast mode: how far to the side (on the beam)
    private const float SampleSeconds = 2f;

    private sealed class Link { public Division Follower = null!; public Division Anchor = null!; }

    // Keyed by follower division pointer.
    private static readonly Dictionary<IntPtr, Link> Links = new();
    private static bool parallelPending;
    private static bool drivingParallel;     // reentrancy guard around our own MoveTo
    private static float lastSample;

    internal static bool IsLinked(IntPtr followerPtr) => Links.ContainsKey(followerPtr);

    // Called every frame from the Ui.Update postfix.
    internal static void Tick(Ui? ui)
    {
        try
        {
            if (ui == null || !GameManager.IsBattle)
            {
                if (Links.Count > 0) Links.Clear();
                parallelPending = false;
                return;
            }

            // Clear a stuck pending if the native targeting was cancelled (no anchor clicked).
            if (parallelPending)
            {
                try { if (ui.SelectedCommand != Ui.DivisionCommand.Screen) parallelPending = false; } catch { }
            }

            HandleHotkey(ui);
            DriveLinks();
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP parallel order failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void HandleHotkey(Ui ui)
    {
        // Shift+P (P alone / number keys collide with vanilla battle binds — e.g. 7 = main shell type).
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (!shift || !Input.GetKeyDown(KeyCode.P))
            return;
        if (!CanHandleBattleHotkeys() || IsPointerOverUi())
            return;
        BeginTargeting(ui, "Shift+P");
    }

    // Enter "click an anchor division" targeting for the Parallel order. Used by the hotkey and the
    // order-bar button. Reuses the game's native Screen-targeting flow; the click is intercepted in
    // ParallelOrderCapturePatch.
    internal static void BeginTargeting(Ui? ui, string via)
    {
        if (ui == null || !GameManager.IsBattle)
            return;
        if (!HasSelectedPlayerDivision(ui))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP parallel order: select a division first, then click an anchor division.");
            return;
        }
        try { ui.SelectedCommand = Ui.DivisionCommand.Screen; } catch { }
        parallelPending = true;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP parallel order ({via}): targeting — click the anchor division to run parallel to.");
    }

    // True while a player division is selected (so the button knows whether to show as usable).
    internal static bool CanAssign(Ui? ui) => ui != null && GameManager.IsBattle && HasSelectedPlayerDivision(ui);

    // Called from the SetScreenDivision prefix when targeting was started for Parallel.
    internal static bool TryCapture(Division ordered, Division anchor)
    {
        if (!parallelPending)
            return false;
        parallelPending = false;
        try { if (G.ui != null) G.ui.SelectedCommand = Ui.DivisionCommand.None; } catch { }

        if (ordered == null || anchor == null)
            return true; // consume anyway (we entered parallel mode)
        IntPtr fp, ap;
        try { fp = ordered.Pointer; ap = anchor.Pointer; } catch { return true; }
        if (fp == ap)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP parallel order: anchor must be a different division.");
            return true;
        }

        Links[fp] = new Link { Follower = ordered, Anchor = anchor };
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP parallel order: '{DivName(ordered)}' now running parallel to '{DivName(anchor)}'.");
        return true;
    }

    internal static void Cancel(Division? d, string reason)
    {
        if (d == null || drivingParallel) return;
        try
        {
            if (Links.Remove(d.Pointer))
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP parallel order: '{DivName(d)}' parallel cancelled ({reason}).");
        }
        catch { }
    }

    private static void DriveLinks()
    {
        if (Links.Count == 0)
            return;

        bool sample = false;
        try { float now = Time.realtimeSinceStartup; if (now - lastSample >= SampleSeconds) { lastSample = now; sample = true; } } catch { }

        var dead = new List<IntPtr>();
        foreach (var kv in Links)
        {
            Link link = kv.Value;
            Ship? fLead = SafeLead(link.Follower);
            Ship? aLead = SafeLead(link.Anchor);
            if (fLead == null || aLead == null)
            {
                dead.Add(kv.Key);
                continue;
            }

            Vector3 aPos, aFwd, aRight;
            try
            {
                aPos = aLead.transform.position;
                aFwd = aLead.transform.forward; aFwd.y = 0f; aFwd = aFwd.sqrMagnitude < 0.0001f ? Vector3.forward : aFwd.normalized;
                aRight = aLead.transform.right; aRight.y = 0f; aRight = aRight.sqrMagnitude < 0.0001f ? Vector3.right : aRight.normalized;
            }
            catch { dead.Add(kv.Key); continue; }

            Vector3 awaySide = AwayFromEnemySide(aLead, aPos, aRight);
            bool abreast = false;
            try { abreast = ModSettings.ParallelStationAbreast; } catch { }
            // Astern = behind + disengaged side (trailing screen). Abreast = beside on the beam, same
            // fore/aft (parallel battle lines).
            Vector3 target = abreast
                ? aPos + awaySide * AbreastDistance
                : aPos - aFwd * BehindDistance + awaySide * LateralDistance;

            drivingParallel = true;
            try { link.Follower.MoveTo(target); } catch { } finally { drivingParallel = false; }

            if (sample)
            {
                Vector3 fp = Pos(fLead);
                float dist = Vector3.Distance(new Vector3(fp.x, 0, fp.z), new Vector3(target.x, 0, target.z));
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP_PARALLELLOG '{DivName(link.Follower)}'->'{DivName(link.Anchor)}' anchorHdg={Hdg(aFwd)} anchorPos=({aPos.x:0},{aPos.z:0}) tgt=({target.x:0},{target.z:0}) folPos=({fp.x:0},{fp.z:0}) dist={dist:0}");
            }
        }

        foreach (IntPtr k in dead)
            Links.Remove(k);
    }

    // Perpendicular side of the anchor's course pointing away from the enemy fleet centroid.
    private static Vector3 AwayFromEnemySide(Ship anchorLead, Vector3 aPos, Vector3 aRight)
    {
        try
        {
            var enemies = DivisionsManager.Instance?.GetEnemiesFor(anchorLead);
            if (enemies != null && enemies.Count > 0)
            {
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (Ship e in enemies)
                {
                    if (e == null) continue;
                    try { sum += e.transform.position; n++; } catch { }
                }
                if (n > 0)
                {
                    Vector3 centroid = sum / n;
                    Vector3 awayVec = aPos - centroid; awayVec.y = 0f;
                    return Vector3.Dot(aRight, awayVec) >= 0f ? aRight : -aRight;
                }
            }
        }
        catch { }
        return aRight; // no enemies known -> default starboard
    }

    // ---- helpers ----

    private static bool HasSelectedPlayerDivision(Ui ui)
    {
        try
        {
            var sel = ui.selectedShips;
            if (sel == null) return false;
            foreach (Ship s in sel)
                if (s != null && SafeDiv(s) != null) return true;
        }
        catch { }
        return false;
    }

    private static bool CanHandleBattleHotkeys()
    {
        try { return GameManager.CanHandleKeyboardInput() && !Util.FocusIsInInputField(); }
        catch { return false; }
    }

    private static bool IsPointerOverUi()
    {
        try { EventSystem? es = EventSystem.current; return es != null && es.IsPointerOverGameObject(); }
        catch { return false; }
    }

    private static Division? SafeDiv(Ship s) { try { return s.division; } catch { return null; } }
    private static Ship? SafeLead(Division d) { try { return d.leader; } catch { return null; } }
    private static Vector3 Pos(Ship s) { try { return s.transform.position; } catch { return Vector3.zero; } }
    private static string Hdg(Vector3 d) { float a = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; a = (a % 360f + 360f) % 360f; return a.ToString("0"); }
    private static string DivName(Division? d)
    {
        if (d == null) return "?";
        try { Ship? l = d.leader; return l == null ? "?" : (string.IsNullOrWhiteSpace(l.vesselName) ? l.name : l.vesselName); }
        catch { return "?"; }
    }
}

// Capture the targeting click for Parallel (suppress the real screen assignment), and cancel a
// division's parallel order when it gets any genuine new order.
[HarmonyPatch(typeof(Division))]
internal static class ParallelOrderCapturePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Division.SetScreenDivision), typeof(Division))]
    private static bool SetScreenDivisionPrefix(Division __instance, Division screen)
    {
        // If we're tagging an anchor for Parallel, consume this call.
        if (ParallelOrder.TryCapture(__instance, screen))
            return false;
        // Otherwise a real screen order cancels any parallel link on this division.
        ParallelOrder.Cancel(__instance, "screen order");
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Division.MoveDir), typeof(Vector3), typeof(bool))]
    private static void MoveDirPostfix(Division __instance) => ParallelOrder.Cancel(__instance, "manual course");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Division.MoveTo), typeof(Vector3))]
    private static void MoveToPostfix(Division __instance) => ParallelOrder.Cancel(__instance, "manual move");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Division.SetFollow), typeof(Ship))]
    private static void SetFollowPostfix(Division __instance) => ParallelOrder.Cancel(__instance, "follow order");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Division.SetScoutDivision), typeof(Division))]
    private static void SetScoutDivisionPostfix(Division __instance) => ParallelOrder.Cancel(__instance, "scout order");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Division.Retreat), typeof(bool))]
    private static void RetreatPostfix(Division __instance) => ParallelOrder.Cancel(__instance, "retreat");
}
