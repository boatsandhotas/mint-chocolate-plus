using System;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Debug/override: force-refuel and rearm the player's ships. Campaign task forces stranded at sea (or
// otherwise not replenishing) can be topped up on demand. Uses the game's own resupply routines
// (CampaignController.ReplenishFuel / ReplenishAmmo, static) with a huge free-capacity so they fill to
// the ship's actual need, bypassing the normal supply throttle. Gated behind a settings toggle.
internal static class ResupplyOverride
{
    private const float HugeCapacity = 1_000_000_000f;

    private static Player? HumanPlayer() => PlayerSwap.CurrentHuman();

    // Refuel + rearm one ship. Returns true if either call ran without throwing.
    internal static bool ResupplyShip(Player player, Ship ship)
    {
        if (player == null || ship == null)
            return false;
        bool ok = false;
        try { CampaignController.ReplenishFuel(player, ship, HugeCapacity, 1f); ok = true; }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP resupply: fuel failed — {ex.GetType().Name}: {ex.Message}"); }
        try { CampaignController.ReplenishAmmo(player, ship, HugeCapacity, 1f); ok = true; }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP resupply: ammo failed — {ex.GetType().Name}: {ex.Message}"); }
        return ok;
    }

    // Refuel + rearm every ship in the human player's fleet. Returns the count resupplied.
    internal static int ResupplyAll()
    {
        int n = 0;
        try
        {
            Player? player = HumanPlayer();
            if (player == null)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning("UADVP resupply: no human player found.");
                return 0;
            }
            // fleetAll is an Il2Cpp IEnumerable<Ship> that won't foreach directly; materialize to a List.
            var ships = new Il2CppSystem.Collections.Generic.List<Ship>(player.fleetAll);
            foreach (Ship ship in ships)
            {
                if (ship == null)
                    continue;
                if (ResupplyShip(player, ship))
                    n++;
            }
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP resupply: refueled + rearmed {n} ship(s).");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP resupply all failed: {ex.GetType().Name}: {ex.Message}");
        }
        return n;
    }
}
