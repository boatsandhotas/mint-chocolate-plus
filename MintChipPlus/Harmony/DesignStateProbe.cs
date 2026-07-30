using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using MintChipPlus.GameData;

namespace MintChipPlus.Harmony;

// TEMPORARY diagnostic: dump the real runtime state of the MAIN player's designs so we can see why a
// refit renders red/disabled while its base builds — without guessing which gate is responsible.
// Logs (once per design name) the flags that actually drive red/struck/disabled: isErased, isDesign,
// isRefitDesign, IsValid, plus hull/type. Driven from the existing Ui.Update postfix. Remove once the
// obsolete-refit issue is resolved.
internal static class DesignStateProbe
{
    private static readonly HashSet<string> Logged = new();
    private static float lastScan = -999f;
    private static bool loggedScan;

    internal static void Tick()
    {
        try
        {
            Player? player = ExtraGameData.MainPlayer();
            if (player == null)
                return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - lastScan < 2f)
                return;
            lastScan = now;

            // Enumerate designs the way the design viewer does (the IEnumerable is not directly
            // foreach-able / TryCast-able; copying it into a fresh Il2Cpp list works).
            Il2CppSystem.Collections.Generic.List<Ship> designs;
            try { designs = new Il2CppSystem.Collections.Generic.List<Ship>(player.designs); }
            catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC_DESIGNPROBE designs-enum failed: {ex.GetType().Name}: {ex.Message}"); return; }

            if (!loggedScan)
            {
                loggedScan = true;
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC_DESIGNPROBE scanning {designs.Count} design(s) for {Safe(() => player.data?.name, "?")}.");
            }

            var inDesigns = new HashSet<IntPtr>();
            foreach (Ship d in designs)
            {
                if (d == null) continue;
                try { inDesigns.Add(d.Pointer); } catch { }
                Report(d, "inDesigns");
            }

            // Also scan designs referenced by ACTUAL FLEET SHIPS — that's how a culled-but-still-
            // sailed design (like the red Huazhou refit) is found; it's no longer in player.designs.
            try
            {
                foreach (Ship ship in player.GetFleetAll())
                {
                    if (ship == null) continue;
                    Ship? dz = null;
                    try { dz = ship.design; } catch { }
                    if (dz == null) continue;
                    bool listed;
                    try { listed = inDesigns.Contains(dz.Pointer); } catch { listed = false; }
                    if (listed) continue; // already reported above
                    Report(dz, "fleetOnly");
                }
            }
            catch (Exception ex) { Melon<MintChipPlusMod>.Logger.Warning($"UADMC_DESIGNPROBE fleet-scan failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC_DESIGNPROBE failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Report(Ship d, string src)
    {
        if (d == null) return;
        string name = Safe(() => d.name, "?") ?? "?";
        if (!Logged.Add(src + "|" + name))
            return;
        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC_DESIGNPROBE [{src}] name='{name}' erased={B(() => d.isErased)} design={B(() => d.isDesign)} refit={B(() => d.isRefitDesign)} valid={B(() => d.IsValid(false))} hull={Safe(() => d.hull?.data?.name, "?")} type={Safe(() => d.shipType?.name, "?")}");
    }

    private static string B(Func<bool> read) { try { return read() ? "T" : "F"; } catch { return "?"; } }
    private static string? Safe(Func<string?> read, string fallback) { try { return read() ?? fallback; } catch { return fallback; } }
}
