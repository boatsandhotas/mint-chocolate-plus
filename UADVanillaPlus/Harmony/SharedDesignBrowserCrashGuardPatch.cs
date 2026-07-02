using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.Harmony;

// Stability guard for the main-menu "Shared Designs" browser.
//
// Community shared designs can reference parts the current game data can no
// longer grade (observed live: "no gun grade for gun: gun_16_x1"). Vanilla
// GameManager.RefreshSharedDesign builds a preview Ship from the shared-design
// store; Part.GetModelNameScale then indexes a gun-grade dictionary with the
// missing grade and throws KeyNotFoundException. That abort leaves the
// Constructor half-built, so Ui.UpdateConstructor dereferences a null every
// frame forever and the game appears frozen (VP's Il2Cpp trampoline handler only
// logs the per-frame NRE instead of letting it hard-crash, which is why the log
// fills with identical Ui.UpdateConstructor stacks).
//
// These finalizers turn the hard freeze into a graceful skip: a broken shared
// design simply fails to render and the player can scroll past it or back out.
// The UpdateConstructor swallow is gated to the shared-design browser so the
// normal campaign/designer constructor keeps vanilla error behavior.
internal static class SharedDesignBrowserCrashGuard
{
    private static readonly HashSet<string> LoggedRefresh = new(StringComparer.Ordinal);
    private static readonly object LogLock = new();
    private static bool loggedUpdateConstructor;

    internal static void NoteRefreshFailure(int year, PlayerData? nation, Exception ex)
    {
        try
        {
            string key = year.ToString() + ":" + (nation == null ? "<null>" : "<set>");
            lock (LogLock)
            {
                if (!LoggedRefresh.Add(key))
                    return;
                if (LoggedRefresh.Count > 256)
                    LoggedRefresh.Clear();
            }

            Melon<UADVanillaPlusMod>.Logger.Warning(
                "UADVP shared-design browser guard: RefreshSharedDesign failed for year=" + year +
                " (" + ex.GetType().Name + ": " + ex.Message +
                "); skipping this design so the Shared Designs screen stays responsive.");
        }
        catch
        {
        }
    }

    internal static void NoteUpdateConstructorFailure(Exception ex)
    {
        try
        {
            lock (LogLock)
            {
                if (loggedUpdateConstructor)
                    return;
                loggedUpdateConstructor = true;
            }

            Melon<UADVanillaPlusMod>.Logger.Warning(
                "UADVP shared-design browser guard: swallowed Ui.UpdateConstructor exception (" +
                ex.GetType().Name + ": " + ex.Message +
                ") to prevent a per-frame freeze on a broken shared design; further occurrences suppressed.");
        }
        catch
        {
        }
    }

    internal static bool InSharedDesignBrowser()
    {
        try
        {
            return GameManager.IsSharedDesignConstructor;
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.RefreshSharedDesign))]
internal static class SharedDesignRefreshCrashGuardPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, int year, PlayerData nation)
    {
        if (__exception == null)
            return null;

        SharedDesignBrowserCrashGuard.NoteRefreshFailure(year, nation, __exception);
        return null; // swallow: a single bad shared design must not abort the browser
    }
}

[HarmonyPatch(typeof(Ui), nameof(Ui.UpdateConstructor))]
internal static class SharedDesignUpdateConstructorCrashGuardPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception == null)
            return null;

        if (!SharedDesignBrowserCrashGuard.InSharedDesignBrowser())
            return __exception; // preserve vanilla behavior outside the shared-design browser

        SharedDesignBrowserCrashGuard.NoteUpdateConstructorFailure(__exception);
        return null; // swallow: stop the per-frame freeze so the player can navigate away
    }
}
