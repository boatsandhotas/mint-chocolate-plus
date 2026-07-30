using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

internal static class CampaignGeneratedDesignSanitizer
{
    internal static bool SanitizeAiGeneratedDesign(Ship? ship, Player? player, string source, string turnLabel)
    {
        MajorShipTorpedoCleanupResult cleanup = MajorShipTorpedoCleanup.Cleanup(ship, player);
        if (!cleanup.Applied)
        {
            MajorShipTorpedoCleanup.Audit(ship, player, $"{source}-design-post-cleanup", turnLabel);
            return false;
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP AI DesignGen sanitized random design turn={MajorShipTorpedoCleanup.LogToken(turnLabel)} nation={AiDesignCompetitiveness.PlayerLabel(player)} type={AiDesignCompetitiveness.NormalizeShipType(ship?.shipType)} design={MajorShipTorpedoCleanup.LogToken(AiDesignCompetitiveness.ShipLabel(ship))} source={MajorShipTorpedoCleanup.LogToken(source)} reason={MajorShipTorpedoCleanup.LogToken(cleanup.Reason)} removedTorps={cleanup.RemovedLaunchers} livePartsBefore={cleanup.LivePartsBefore} livePartsAfter={cleanup.LivePartsAfter} cacheBefore={cleanup.CacheBefore} cacheAfter={cleanup.CacheAfter} haveTorpedoesBefore={MajorShipTorpedoCleanup.BoolText(cleanup.HaveTorpedoesBefore)} haveTorpedoesAfter={MajorShipTorpedoCleanup.BoolText(cleanup.HaveTorpedoesAfter)} torpedoesAllBefore={cleanup.TorpedoesAllBefore} torpedoesAllAfter={cleanup.TorpedoesAllAfter} weaponCacheRefresh={MajorShipTorpedoCleanup.BoolText(cleanup.WeaponCacheRefreshOk)} removedByRemovePart={cleanup.RemovedByRemovePart} removedStaleCache={cleanup.RemovedStaleCache} removedSupport={cleanup.RemovedSupportComponents} removedComponents={cleanup.RemovedComponentsText} valid={cleanup.Valid} recalc={MajorShipTorpedoCleanup.BoolText(cleanup.RecalcOk)} tonsBefore={MajorShipTorpedoCleanup.Fmt(cleanup.TonsBefore)} tonsAfter={MajorShipTorpedoCleanup.Fmt(cleanup.TonsAfter)} storeNameTubes={MajorShipTorpedoCleanup.TubeCountText(cleanup.StoreNameTubes)} reloadTubes={MajorShipTorpedoCleanup.TubeCountText(cleanup.ReloadTubes)}.");

        MajorShipTorpedoCleanup.Audit(ship, player, $"{source}-design-post-cleanup", turnLabel);
        return true;
    }
}
