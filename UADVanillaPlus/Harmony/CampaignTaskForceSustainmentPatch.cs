using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Campaign-only sustainment balance. This keeps strategic task forces supplied
// and replenished without touching battle firing, reload, or ammo-spend logic.
internal static class CampaignTaskForceSustainmentPatch
{
    private const string LogPrefix = "UADVP TaskForceSustainment";
    private const float FullFuel = 100f;
    private const float FullEpsilon = 0.01f;
    private const float FreeCapacity = 1_000_000f;
    private const float ReplenishModifier = 1f;

    private static readonly MethodInfo? ReplenishFuelMethod =
        AccessTools.Method(typeof(CampaignController), "ReplenishFuel", new[] { typeof(Player), typeof(VesselEntity), typeof(float), typeof(float) });
    private static readonly MethodInfo? ReplenishAmmoMethod =
        AccessTools.Method(typeof(CampaignController), "ReplenishAmmo", new[] { typeof(Player), typeof(Ship), typeof(float), typeof(float) });
    private static readonly HashSet<string> WarnedFallbacks = new(StringComparer.Ordinal);
    private static string lastLoggedSummary = string.Empty;

    internal static void ApplyAllActive(string reason)
    {
        if (!ModSettings.TaskForceSustainmentFullEnabled)
            return;

        try
        {
            SustainmentSummary summary = new();
            var taskForces = CampaignController.Instance?.CampaignData?.TaskForces;
            if (taskForces != null)
            {
                foreach (CampaignController.TaskForce group in taskForces)
                    ApplyGroup(group, ref summary);
            }

            LogSummary(reason, summary, forceIfEmpty: string.Equals(reason, "option-change", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: apply-all failed reason={LogToken(reason)}; leaving vanilla task force state intact. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ApplyGroupActive(CampaignController.TaskForce? group, string reason)
    {
        if (!ModSettings.TaskForceSustainmentFullEnabled || group == null)
            return;

        try
        {
            SustainmentSummary summary = new();
            ApplyGroup(group, ref summary);
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"group:{reason}:{ex.GetType().Name}:{ex.Message}",
                $"{LogPrefix}: group sustainment failed reason={LogToken(reason)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ApplyPlayerActive(Player? player, string reason)
    {
        if (!ModSettings.TaskForceSustainmentFullEnabled)
            return;

        try
        {
            SustainmentSummary summary = new();
            var taskForces = CampaignController.Instance?.CampaignData?.TaskForces;
            if (taskForces != null)
            {
                foreach (CampaignController.TaskForce group in taskForces)
                {
                    if (player == null || SamePlayer(Safe(() => group.Controller, null), player))
                        ApplyGroup(group, ref summary);
                }
            }

            LogSummary(reason, summary);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: player sustainment failed reason={LogToken(reason)} player={LogToken(PlayerLabel(player))}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ApplyBattleActive(CampaignBattle? battle, string reason)
    {
        if (!ModSettings.TaskForceSustainmentFullEnabled || battle == null)
            return;

        try
        {
            SustainmentSummary summary = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            ApplyBattleShipList(battle.AttackerShips, ref summary, seen);
            ApplyBattleShipList(battle.DefenderShips, ref summary, seen);
            ApplyBattleShipList(battle.ShipsAdditionalAttacker, ref summary, seen);
            ApplyBattleShipList(battle.ShipsAdditionalDefender, ref summary, seen);
            ApplyBattleShipList(battle.ActualShips, ref summary, seen);
            LogSummary(reason, summary);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"{LogPrefix}: battle sustainment failed reason={LogToken(reason)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyGroup(CampaignController.TaskForce? group, ref SustainmentSummary summary)
    {
        if (group == null)
            return;

        summary.Groups++;
        if (!Safe(() => group.HaveSupply, true))
        {
            group.HaveSupply = true;
            summary.SupplyRestored++;
        }

        Player? owner = Safe(() => group.Controller, null);
        var vessels = Safe(() => group.Vessels, null);
        if (vessels == null)
            return;

        foreach (VesselEntity vessel in vessels)
            ApplyVessel(owner, vessel, ref summary);
    }

    private static void ApplyBattleShipList(Il2CppSystem.Collections.Generic.List<Ship>? ships, ref SustainmentSummary summary, HashSet<string> seen)
    {
        if (ships == null)
            return;

        foreach (Ship ship in ships)
        {
            if (ship == null)
                continue;

            string id = ShipId(ship);
            if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                continue;

            ApplyVessel(Safe(() => ship.player, null), ship, ref summary);
        }
    }

    private static void ApplyVessel(Player? owner, VesselEntity? vessel, ref SustainmentSummary summary)
    {
        if (vessel == null)
            return;

        summary.Vessels++;
        ApplyFuel(owner ?? Safe(() => vessel.player, null), vessel, ref summary);

        Ship? ship = Safe(() => vessel.TryCast<Ship>(), null);
        if (ship == null)
            return;

        summary.Ships++;
        ApplyAmmo(owner ?? Safe(() => ship.player, null), ship, ref summary);
    }

    private static void ApplyFuel(Player? owner, VesselEntity vessel, ref SustainmentSummary summary)
    {
        float before = Safe(() => vessel.Fuel, FullFuel);
        if (before >= FullFuel - FullEpsilon)
            return;

        bool usedVanilla = false;
        if (owner != null && ReplenishFuelMethod != null)
        {
            try
            {
                ReplenishFuelMethod.Invoke(null, new object[] { owner, vessel, FreeCapacity, ReplenishModifier });
                usedVanilla = true;
            }
            catch (Exception ex)
            {
                WarnOnce(
                    $"fuel-invoke:{ex.GetType().Name}:{ex.Message}",
                    $"{LogPrefix}: ReplenishFuel invoke failed; direct fuel fill fallback may be used. {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            WarnOnce("fuel-method-missing", $"{LogPrefix}: ReplenishFuel unavailable; direct fuel fill fallback will be used.");
        }

        float after = Safe(() => vessel.Fuel, before);
        if (after < FullFuel - FullEpsilon)
        {
            vessel.Fuel = FullFuel;
            summary.Fallbacks++;
            WarnOnce(
                "fuel-direct-fill",
                $"{LogPrefix}: direct fuel fill fallback used after vanilla replenish path left a vessel below full.");
        }

        summary.FuelRestored++;
        if (usedVanilla)
            summary.VanillaFuelCalls++;
    }

    private static void ApplyAmmo(Player? owner, Ship ship, ref SustainmentSummary summary)
    {
        if (!NeedsAmmo(ship))
            return;

        bool usedVanilla = false;
        if (owner != null && ReplenishAmmoMethod != null)
        {
            try
            {
                ReplenishAmmoMethod.Invoke(null, new object[] { owner, ship, FreeCapacity, ReplenishModifier });
                usedVanilla = true;
            }
            catch (Exception ex)
            {
                WarnOnce(
                    $"ammo-invoke:{ex.GetType().Name}:{ex.Message}",
                    $"{LogPrefix}: ReplenishAmmo invoke failed; direct ammo fill fallback may be used. {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            WarnOnce("ammo-method-missing", $"{LogPrefix}: ReplenishAmmo unavailable; direct ammo fill fallback will be used.");
        }

        if (NeedsAmmo(ship))
        {
            DirectFillAmmo(ship);
            summary.Fallbacks++;
            WarnOnce(
                "ammo-direct-fill",
                $"{LogPrefix}: direct ammo fill fallback used after vanilla replenish path left a ship below full.");
        }

        summary.AmmoRestored++;
        if (usedVanilla)
            summary.VanillaAmmoCalls++;
    }

    private static bool NeedsAmmo(Ship ship)
    {
        try
        {
            var ammoByPart = ship.ammo;
            if (ammoByPart == null)
                return false;

            foreach (var entry in ammoByPart)
            {
                var ammo = entry.Value;
                if (ammo != null && (ammo.AP < ammo.MaxAP || ammo.HE < ammo.MaxHE))
                    return true;
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"ammo-read:{ex.GetType().Name}:{ex.Message}",
                $"{LogPrefix}: ammo state read failed; skipping direct ammo inspection for one ship. {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static void DirectFillAmmo(Ship ship)
    {
        try
        {
            var ammoByPart = ship.ammo;
            if (ammoByPart == null)
                return;

            foreach (var entry in ammoByPart)
            {
                var ammo = entry.Value;
                if (ammo == null)
                    continue;

                if (ammo.AP < ammo.MaxAP)
                    ammo.AP = ammo.MaxAP;
                if (ammo.HE < ammo.MaxHE)
                    ammo.HE = ammo.MaxHE;
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"ammo-direct-failed:{ex.GetType().Name}:{ex.Message}",
                $"{LogPrefix}: direct ammo fill failed for one ship. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogSummary(string reason, SustainmentSummary summary, bool forceIfEmpty = false)
    {
        if (!forceIfEmpty && !summary.AnyChange)
            return;

        string message =
            $"{LogPrefix}: applied reason={LogToken(reason)} groups={summary.Groups} vessels={summary.Vessels} ships={summary.Ships} " +
            $"supply={summary.SupplyRestored} fuel={summary.FuelRestored} ammo={summary.AmmoRestored} " +
            $"vanillaFuel={summary.VanillaFuelCalls} vanillaAmmo={summary.VanillaAmmoCalls} fallbacks={summary.Fallbacks}.";
        if (string.Equals(message, lastLoggedSummary, StringComparison.Ordinal))
            return;

        lastLoggedSummary = message;
        Melon<UADVanillaPlusMod>.Logger.Msg(message);
    }

    private static bool SamePlayer(Player? a, Player? b)
    {
        if (a == null || b == null)
            return false;

        try
        {
            if (a.Pointer == b.Pointer)
                return true;
        }
        catch { }

        try { return a.data == b.data; }
        catch { return false; }
    }

    private static string PlayerLabel(Player? player)
    {
        if (player == null)
            return "unknown";

        try { return player.data?.name ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string ShipId(Ship ship)
    {
        try { return ship.id.ToString(); }
        catch { return string.Empty; }
    }

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace(' ', '_').Replace('\n', '_').Replace('\r', '_');

    private static void WarnOnce(string key, string message)
    {
        if (WarnedFallbacks.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning(message);
    }

    private static T Safe<T>(Func<T> func, T fallback)
    {
        try { return func(); }
        catch { return fallback; }
    }

    private struct SustainmentSummary
    {
        internal int Groups;
        internal int Vessels;
        internal int Ships;
        internal int SupplyRestored;
        internal int FuelRestored;
        internal int AmmoRestored;
        internal int VanillaFuelCalls;
        internal int VanillaAmmoCalls;
        internal int Fallbacks;

        internal readonly bool AnyChange => SupplyRestored > 0 || FuelRestored > 0 || AmmoRestored > 0 || Fallbacks > 0;
    }
}

[HarmonyPatch(typeof(CampaignController.TaskForce), nameof(CampaignController.TaskForce.CheckSupplyPortDistance))]
internal static class CampaignTaskForceSustainmentSupplyPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignController.TaskForce __instance)
        => CampaignTaskForceSustainmentPatch.ApplyGroupActive(__instance, "check-supply");
}

[HarmonyPatch(typeof(CampaignController.TaskForce), nameof(CampaignController.TaskForce.FuelConsumption))]
internal static class CampaignTaskForceSustainmentFuelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CampaignController.TaskForce __instance)
    {
        if (!ModSettings.TaskForceSustainmentFullEnabled)
            return true;

        CampaignTaskForceSustainmentPatch.ApplyGroupActive(__instance, "fuel-consumption");
        return false;
    }
}

[HarmonyPatch(typeof(CampaignController), "ReplenishFuelAndAmmoTaskForces")]
internal static class CampaignTaskForceSustainmentMaintenancePatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player)
        => CampaignTaskForceSustainmentPatch.ApplyPlayerActive(player, "end-turn");
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.AcceptBattle))]
internal static class CampaignTaskForceSustainmentAcceptBattlePatch
{
    [HarmonyPrefix]
    private static void Prefix(CampaignBattle battle)
        => CampaignTaskForceSustainmentPatch.ApplyBattleActive(battle, "battle-accept");
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.FinishCombat))]
internal static class CampaignTaskForceSustainmentFinishCombatPatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignBattle battle)
    {
        CampaignTaskForceSustainmentPatch.ApplyBattleActive(battle, "battle-finish");
        CampaignTaskForceSustainmentPatch.ApplyAllActive("battle-finish-taskforces");
    }
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.CompleteBattle), new[] { typeof(CampaignBattle), typeof(bool), typeof(bool) })]
internal static class CampaignTaskForceSustainmentCompleteBattlePatch
{
    [HarmonyPostfix]
    private static void Postfix(CampaignBattle battle)
    {
        CampaignTaskForceSustainmentPatch.ApplyBattleActive(battle, "battle-complete");
        CampaignTaskForceSustainmentPatch.ApplyAllActive("battle-complete-taskforces");
    }
}
