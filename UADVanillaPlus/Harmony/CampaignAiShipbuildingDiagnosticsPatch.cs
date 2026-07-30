using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Temporary diagnostic only. This traces vanilla AI shipbuilding gates,
// candidate ratios, validation failures, and actual orders without mutating
// fleet/design behavior.
internal static class CampaignAiShipbuildingDiagnosticsPatch
{
    private const string LogPrefix = "UADVP ai-build";
    private static readonly bool DetailedDiagnosticsEnabled = false;
    private static readonly bool TurnSummaryEnabled = true;
    private static readonly string[] SurfaceTypes = { "BB", "BC", "CA", "CL", "DD", "TB" };
    private static readonly string[] FleetTypes = { "BB", "BC", "CA", "CL", "DD", "TB", "SS" };
    private static readonly Stack<BuildContext> ActiveSurfaceBuilds = new();
    private static readonly Stack<BuildContext> ActiveSubmarineBuilds = new();
    private static readonly Dictionary<string, AiBuildTurnSummary> PendingTurnSummaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedDesignSnapshotKeys = new(StringComparer.Ordinal);
    private static bool logDesignPowerSnapshots = true;
    private static bool suppressValidationAggregation;

    internal static BuildContext? BeginSurfaceBuild(Player? player, float tempPlayerCash)
    {
        if (!ShouldTrace(player))
            return null;

        BuildContext context = CreateContext(player!, tempPlayerCash, BuildKind.Surface);
        ActiveSurfaceBuilds.Push(context);

        try
        {
            context.BeforeShipIds = SnapshotShipIds(player!.fleetAll);
            EnsureTurnSummaryNation(context);
            if (DetailedDiagnosticsEnabled)
            {
                CampaignAiWeightedBuildTypePickerPatch.LogDisabledForVanilla(player, context.DateLabel);
                LogPlayerSummary(context);
                LogFleetMix(context);
                LogGateSummary(context);
                LogSurfaceTypeDiagnostics(context);
                LogDesignPowerSnapshot(context);
            }
        }
        catch (Exception ex)
        {
            Warn($"surface prefix failed for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }

        return context;
    }

    internal static void EndSurfaceBuild(BuildContext? context, Player? player)
    {
        if (context == null)
            return;

        try
        {
            LogNewSurfaceOrders(context, player);
            if (DetailedDiagnosticsEnabled)
            {
                LogVanillaValidationSummary(context);
                if (!context.ObservedOrder)
                    LogNoOrderSummary(context);
            }
        }
        catch (Exception ex)
        {
            Warn($"surface postfix failed for {context.Nation}. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PopContext(ActiveSurfaceBuilds, context);
        }
    }

    internal static BuildContext? BeginSubmarineBuild(Player? player, float tempPlayerCash)
    {
        if (!ShouldTrace(player))
            return null;

        BuildContext context = CreateContext(player!, tempPlayerCash, BuildKind.Submarine);
        ActiveSubmarineBuilds.Push(context);

        try
        {
            context.BeforeShipIds = SnapshotSubmarineIds(player!.submarinesAll);
            EnsureTurnSummaryNation(context);
            if (DetailedDiagnosticsEnabled)
                Log($"{context.Prefix} subs-start cash={Fmt(player.cash)} tempCash={Fmt(tempPlayerCash)} revenue={Fmt(context.Revenue)} income={Fmt(context.Income)} crew={context.CrewPool}/{Fmt(context.MinCrewPool)} stateBudget={Fmt(context.StateBudget)} fleetTons={Fmt(context.CurrentFleetTons)} aiShipbuilding={Fmt(context.AiShipbuilding)} phase={context.Phase}.");
        }
        catch (Exception ex)
        {
            Warn($"submarine prefix failed for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }

        return context;
    }

    internal static void EndSubmarineBuild(BuildContext? context, Player? player)
    {
        if (context == null)
            return;

        try
        {
            LogNewSubmarineOrders(context, player);
            if (DetailedDiagnosticsEnabled && !context.ObservedOrder)
                Log($"{context.Prefix} subs-none likelyGate={LikelyGate(context)}.");
        }
        catch (Exception ex)
        {
            Warn($"submarine postfix failed for {context.Nation}. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PopContext(ActiveSubmarineBuilds, context);
        }
    }

    internal static void RecordCanBuildValidation(Ship? design, int amount, string? reason, bool result)
    {
        if (!DetailedDiagnosticsEnabled || suppressValidationAggregation || ActiveSurfaceBuilds.Count == 0 || design == null)
            return;

        BuildContext context = ActiveSurfaceBuilds.Peek();
        try
        {
            Player? owner = design.player;
            if (owner == null || owner.Pointer != context.Player.Pointer)
                return;

            string type = NormalizeShipType(design.shipType);
            if (!SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                return;

            TypeDiagnostics diag = context.TypeDiagnosticsByType.GetValueOrDefault(type) ?? new TypeDiagnostics(type);
            context.TypeDiagnosticsByType[type] = diag;

            diag.VanillaValidationCalls++;
            if (result)
            {
                diag.VanillaBuildable++;
            }
            else
            {
                diag.VanillaFailures.Add(NormalizeReason(reason));
            }
        }
        catch (Exception ex)
        {
            Warn($"validation aggregation failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void RecordWeightedTypePredicate(
        Player? player,
        ShipType? shipType,
        bool vanillaResultBefore,
        bool vpResultAfter,
        string? selectedType,
        string decisionReason,
        bool fallback,
        bool suppressVanilla)
    {
        if (!DetailedDiagnosticsEnabled || ActiveSurfaceBuilds.Count == 0 || player == null)
            return;

        BuildContext context = ActiveSurfaceBuilds.Peek();
        try
        {
            if (player.Pointer != context.Player.Pointer)
                return;

            string type = NormalizeShipType(shipType);
            string selected = string.IsNullOrWhiteSpace(selectedType) ? "none" : selectedType!;
            context.WeightedPredicateCalls++;
            context.LastWeightedSelectedType = selected;
            context.LastWeightedDecisionReason = string.IsNullOrWhiteSpace(decisionReason) ? "unknown" : decisionReason;
            context.LastWeightedFallback = fallback;
            context.LastWeightedSuppressVanilla = suppressVanilla;

            if (vpResultAfter)
                context.WeightedPredicateAccepted++;
            else
                context.WeightedPredicateRejected++;

            if (string.Equals(type, selected, StringComparison.OrdinalIgnoreCase))
                context.WeightedSelectedSeen = true;

            if (context.WeightedPredicateDetails.Count < 10)
            {
                context.WeightedPredicateDetails.Add(
                    $"{type}:{BoolText(vanillaResultBefore)}->{BoolText(vpResultAfter)}");
            }
        }
        catch (Exception ex)
        {
            Warn($"weighted predicate aggregation failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static BuildAttemptState? BeginBuildShipsFromDesignCall(Ship? design, int amount, bool force, Player? overridePlayer)
    {
        if (ActiveSurfaceBuilds.Count == 0 || design == null)
            return null;

        BuildContext context = ActiveSurfaceBuilds.Peek();
        try
        {
            Player? owner = overridePlayer ?? design.player;
            if (owner == null || owner.Pointer != context.Player.Pointer)
                return null;

            List<ShipSnapshot> beforeFleet = SnapshotShipDetails(owner.fleetAll);
            List<ShipSnapshot> beforeDesigns = SnapshotShipDetails(owner.designs);
            if (DetailedDiagnosticsEnabled)
                LogBuildSourceFlags(context, owner, design, beforeFleet.Count, beforeDesigns.Count);

            return new BuildAttemptState(
                context,
                DesignLabel(design),
                NormalizeShipType(design.shipType),
                ShipLabel(design),
                amount,
                force,
                Safe(() => owner.cash, 0f),
                Safe(() => owner.crewPool, 0),
                Safe(() => owner.ShipTonnageUnderConstruction(), 0f),
                beforeFleet,
                beforeDesigns);
        }
        catch (Exception ex)
        {
            Warn($"build-call prefix failed. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static void EndBuildShipsFromDesignCall(BuildAttemptState? state, Ship? design, int amount, bool force, Player? overridePlayer)
    {
        if (state == null)
            return;

        BuildContext context = state.Context;
        try
        {
            Player? owner = overridePlayer ?? design?.player ?? context.Player;
            if (owner == null)
                return;

            List<Ship> afterShips = SafeShipList(owner.fleetAll);
            HashSet<string> afterShipIds = SnapshotShipIds(afterShips);
            int beforeCount = state.BeforeShipIds.Count;
            int afterCount = afterShipIds.Count;
            int createdCount = afterShipIds.Count(id => !state.BeforeShipIds.Contains(id));
            bool created = createdCount > 0;

            context.BuildShipsFromDesignCalls++;
            if (created)
                context.BuildShipsFromDesignSuccesses++;
            else
                context.BuildShipsFromDesignFailures++;

            string reason = created ? $"created:{createdCount}" : "no-new-ship";
            string detail =
                $"{state.Type}:{state.DesignName}:amount{amount}:force{force.ToString().ToLowerInvariant()}:before{beforeCount}:after{afterCount}:{reason}";
            if (context.BuildShipsFromDesignDetails.Count < 8)
                context.BuildShipsFromDesignDetails.Add(detail);

            float cashAfter = Safe(() => owner.cash, 0f);
            int crewAfter = Safe(() => owner.crewPool, 0);
            float buildingAfter = Safe(() => owner.ShipTonnageUnderConstruction(), 0f);
            if (DetailedDiagnosticsEnabled)
            {
                Log(
                    $"{context.Prefix} build-call type={state.Type} design={state.DesignLabel} amount={amount} force={force.ToString().ToLowerInvariant()} beforeShips={beforeCount} afterShips={afterCount} created={created.ToString().ToLowerInvariant()} reason={reason} cash={Fmt(state.CashBefore)}->{Fmt(cashAfter)} crew={state.CrewBefore}->{crewAfter} buildingTons={Fmt(state.BuildingTonsBefore)}->{Fmt(buildingAfter)}.");
            }

            foreach (Ship createdShip in afterShips.Where(ship => !state.BeforeShipIds.Contains(ShipIdentity(ship))))
            {
                if (DetailedDiagnosticsEnabled)
                    LogCreatedShipFlags(context, owner, createdShip, design);
                SanitizeCreatedShipClone(context, owner, createdShip, design);
                if (DetailedDiagnosticsEnabled)
                {
                    MajorShipTorpedoCleanup.Audit(createdShip, owner, "build-created", context.DateLabel);
                    MajorShipTorpedoCleanup.Audit(Safe(() => createdShip.design, null), owner, "build-created-linked-design", context.DateLabel);
                }
            }
        }
        catch (Exception ex)
        {
            Warn($"build-call postfix failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogBuildSourceFlags(BuildContext context, Player owner, Ship design, int beforeFleetCount, int beforeDesignCount)
    {
        try
        {
            Log(
                $"{context.Prefix} build-source-flags design={DesignLabel(design)} id={ShipId(design)} ptr={ShipPointerText(design)} isDesign={BoolText(ReadBoolMember(design, "isDesign"))} isRefitDesign={BoolText(ReadBoolMember(design, "isRefitDesign"))} isShared={BoolText(IsSharedDesignMarker(design))} sharedMarkers={SharedMarkerDetails(design)} inPlayerDesigns={BoolText(PlayerDesignsContain(owner, design))} beforeFleet={beforeFleetCount} beforeDesigns={beforeDesignCount} store={ShipStoreSummary(design)}.");
        }
        catch (Exception ex)
        {
            Warn($"build-source flag log failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogCreatedShipFlags(BuildContext context, Player owner, Ship ship, Ship? sourceDesign)
    {
        try
        {
            Ship? linkedDesign = Safe(() => ship.design, null);
            bool inPlayerDesigns = PlayerDesignsContain(owner, ship);
            bool designInPlayerDesigns = PlayerDesignsContain(owner, linkedDesign);
            Log(
                $"{context.Prefix} build-created-flags ship={ShipLabel(ship)} id={ShipId(ship)} ptr={ShipPointerText(ship)} type={NormalizeShipType(ship.shipType)} isDesign={BoolText(ReadBoolMember(ship, "isDesign"))} isRefitDesign={BoolText(ReadBoolMember(ship, "isRefitDesign"))} isShared={BoolText(IsSharedDesignMarker(ship))} sharedMarkers={SharedMarkerDetails(ship)} inPlayerDesigns={BoolText(inPlayerDesigns)} design={DesignLabel(linkedDesign)} designId={ShipId(linkedDesign)} designPtr={ShipPointerText(linkedDesign)} designIsShared={BoolText(IsSharedDesignMarker(linkedDesign))} designInPlayerDesigns={BoolText(designInPlayerDesigns)} sourceDesign={DesignLabel(sourceDesign)} sourceDesignPtr={ShipPointerText(sourceDesign)} sourceDesignIsShared={BoolText(IsSharedDesignMarker(sourceDesign))} store={ShipStoreSummary(ship)} designStore={ShipStoreSummary(linkedDesign)}.");
        }
        catch (Exception ex)
        {
            Warn($"created ship flag log failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SanitizeCreatedShipClone(BuildContext context, Player owner, Ship ship, Ship? sourceDesign)
    {
        try
        {
            List<string> cleared = new();
            List<string> failed = new();
            bool wasInPlayerDesigns = PlayerDesignsContain(owner, ship);
            string storeBefore = ShipStoreSummary(ship);

            Ship? linkedDesign = Safe(() => ship.design, null);
            if (ReadBoolMember(ship, "isDesign") && linkedDesign == null && sourceDesign != null)
            {
                ship.design = sourceDesign;
                if (!ReadBoolMember(ship, "isDesign"))
                    cleared.Add("isDesign");
                else
                    failed.Add("isDesign");
            }

            ClearRefitDesignMarkerIfTrue(ship, cleared, failed);
            ClearBoolFlagIfTrue(ship, "isSharedDesign", "isSharedDesign", cleared, failed);
            ClearBoolFlagIfTrue(ship, "IsSharedDesign", "IsSharedDesign", cleared, failed);
            ClearBoolFlagIfTrue(ship, "isShared", "isShared", cleared, failed);

            MajorShipTorpedoCleanupResult shipTorpedoCleanup = MajorShipTorpedoCleanup.Cleanup(ship, owner);
            MajorShipTorpedoCleanupResult designTorpedoCleanup = linkedDesign == null
                ? new MajorShipTorpedoCleanupResult { Reason = "missing-design" }
                : MajorShipTorpedoCleanup.Cleanup(linkedDesign, owner);
            bool removedFromDesigns = wasInPlayerDesigns && !PlayerDesignsContain(owner, ship);

            if (cleared.Count == 0 &&
                failed.Count == 0 &&
                !removedFromDesigns &&
                !shipTorpedoCleanup.Applied &&
                !designTorpedoCleanup.Applied)
            {
                return;
            }

            string clearedText = cleared.Count == 0 ? "none" : string.Join(",", cleared.Distinct(StringComparer.Ordinal));
            string failedText = failed.Count == 0 ? "none" : string.Join(",", failed.Distinct(StringComparer.Ordinal));
            if (DetailedDiagnosticsEnabled)
            {
                Log(
                    $"{context.Prefix} build-created-sanitized ship={ShipLabel(ship)} id={ShipId(ship)} ptr={ShipPointerText(ship)} cleared={clearedText} failed={failedText} removedFromDesigns={BoolText(removedFromDesigns)} isDesign={BoolText(ReadBoolMember(ship, "isDesign"))} isRefitDesign={BoolText(ReadBoolMember(ship, "isRefitDesign"))} isShared={BoolText(IsSharedDesignMarker(ship))} torpedoCleanup={TorpedoCleanupText(shipTorpedoCleanup)} designTorpedoCleanup={TorpedoCleanupText(designTorpedoCleanup)} storeBefore={storeBefore} storeAfter={ShipStoreSummary(ship)} designStillShared={BoolText(IsSharedDesignMarker(Safe(() => ship.design, null)))}.");
            }
        }
        catch (Exception ex)
        {
            Warn($"created ship sanitizer failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string TorpedoCleanupText(MajorShipTorpedoCleanupResult cleanup)
    {
        if (!cleanup.Applied)
            return $"skip:{cleanup.Reason}";

        return $"applied:reason={cleanup.Reason},removedTorps={cleanup.RemovedLaunchers},removedSupport={cleanup.RemovedSupportComponents},removedComponents={cleanup.RemovedComponentsText},live={cleanup.LivePartsBefore}->{cleanup.LivePartsAfter},cache={cleanup.CacheBefore}->{cleanup.CacheAfter},have={MajorShipTorpedoCleanup.BoolText(cleanup.HaveTorpedoesBefore)}->{MajorShipTorpedoCleanup.BoolText(cleanup.HaveTorpedoesAfter)},torpedoesAll={cleanup.TorpedoesAllBefore}->{cleanup.TorpedoesAllAfter},weaponCache={MajorShipTorpedoCleanup.BoolText(cleanup.WeaponCacheRefreshOk)},store={MajorShipTorpedoCleanup.TubeCountText(cleanup.StoreNameTubes)},reload={MajorShipTorpedoCleanup.TubeCountText(cleanup.ReloadTubes)},valid={cleanup.Valid}";
    }

    internal static T WithoutValidationAggregation<T>(Func<T> action)
    {
        bool previous = suppressValidationAggregation;
        suppressValidationAggregation = true;
        try
        {
            return action();
        }
        finally
        {
            suppressValidationAggregation = previous;
        }
    }

    internal static void FlushPendingAiBuildTurnSummariesAfterNextTurn()
    {
        if (!TurnSummaryEnabled || PendingTurnSummaries.Count == 0)
            return;

        List<AiBuildTurnSummary> summaries = PendingTurnSummaries.Values
            .OrderBy(summary => summary.TurnIndex)
            .ThenBy(summary => summary.DateLabel, StringComparer.Ordinal)
            .ToList();
        PendingTurnSummaries.Clear();

        foreach (AiBuildTurnSummary summary in summaries)
        {
            try
            {
                Log($"{LogPrefix} summary turn={summary.DateLabel} total={summary.Total} countries={summary.CountriesText()}");
            }
            catch (Exception ex)
            {
                Warn($"summary flush failed for {summary.DateLabel}. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void EnsureTurnSummaryNation(BuildContext context)
    {
        if (!TurnSummaryEnabled || context.InitialGeneration)
            return;

        AiBuildTurnSummary summary = GetTurnSummary(context);
        summary.EnsureNation(context.Nation);
    }

    private static void RecordTurnSummaryOrder(BuildContext context, string type)
    {
        if (!TurnSummaryEnabled || context.InitialGeneration)
            return;

        AiBuildTurnSummary summary = GetTurnSummary(context);
        summary.Add(context.Nation, NormalizeShipType(type));
    }

    private static AiBuildTurnSummary GetTurnSummary(BuildContext context)
    {
        string key = $"{context.Turn}|{context.DateLabel}";
        if (!PendingTurnSummaries.TryGetValue(key, out AiBuildTurnSummary? summary))
        {
            summary = new AiBuildTurnSummary(context.Turn, context.DateLabel);
            PendingTurnSummaries[key] = summary;
        }

        return summary;
    }

    private static BuildContext CreateContext(Player player, float tempPlayerCash, BuildKind kind)
    {
        CampaignController? campaign = CampaignController.Instance;
        AiPersonalities? personality = Safe(() => player.GetAiPersonality(), null);
        float currentFleetTons = Safe(() => player.TotalFleetTonnage(), 0f);
        float underConstructionTons = Safe(() => player.ShipTonnageUnderConstruction(), 0f);
        float capacityLimit = Safe(() => player.ShipbuildingCapacityLimit(), 0f);
        float overrideLimit = Safe(() => CampaignController.Param("override_shipybuilding_limit", 1f), 1f);
        float minCrewPool = Safe(() => CampaignController.Param("build_ships_min_crew_pool_amount", 0f), 0f);
        float aiShipGdpRatio = Safe(() => CampaignController.Param("ai_ship_gdp_ratio", 1f), 1f);
        float revenue = Safe(() => player.Revenue(), 0f);
        float income = Safe(() => player.Income(), 0f);
        float stateBudget = Safe(() => player.StateBudget(), 0f);
        int turn = Safe(() => campaign?.CurrentDate.turn ?? -1, -1);
        bool initialGeneration = turn <= 0;
        FleetMetricSnapshot fleetMetrics = FleetMetricSnapshot.For(player, stateBudget, aiShipGdpRatio);

        return new BuildContext(
            kind,
            player,
            tempPlayerCash,
            turn,
            CampaignDateLabel(campaign, turn),
            initialGeneration ? "generation" : "campaign",
            PlayerLabel(player),
            SafeString(() => player.data?.name),
            SafeString(() => player.data?.nameUi),
            SafeString(() => player.GetAiName()),
            SafeString(() => personality?.name),
            SafeString(() => personality?.aiTextName),
            Safe(() => personality?.aiShipbuilding ?? 0f, 0f),
            Safe(() => player.cash, 0f),
            revenue,
            income,
            stateBudget,
            Safe(() => player.crewPool, 0),
            minCrewPool,
            currentFleetTons,
            underConstructionTons,
            capacityLimit,
            overrideLimit,
            aiShipGdpRatio,
            fleetMetrics,
            initialGeneration);
    }

    private static bool ShouldTrace(Player? player)
    {
        if (player == null)
            return false;

        try
        {
            return player.isAi && !player.isMain;
        }
        catch
        {
            return false;
        }
    }

    private static void LogPlayerSummary(BuildContext context)
    {
        Log(
            $"{context.Prefix} start player={context.Nation} data={context.DataName} nameUi={context.DataNameUi} turn={context.DateLabel} turnIndex={context.Turn} phase={context.Phase} personality={context.PersonalityName}/{context.PersonalityText} aiName={context.AiName} aiShipbuilding={Fmt(context.AiShipbuilding)} aiFleetMix={ModSettings.AiFleetCompositionModeText(ModSettings.AiFleetComposition)} aiArmsRace={ModSettings.AiArmsRaceModeText(ModSettings.AiArmsRace)} cash={Fmt(context.Cash)} tempCash={Fmt(context.TempPlayerCash)} revenue={Fmt(context.Revenue)} income={Fmt(context.Income)} crew={context.CrewPool} minCrew={Fmt(context.MinCrewPool)} fleetTonsDiagnostic={Fmt(context.CurrentFleetTons)} fleetAllSurfaceTons={Fmt(context.FleetMetrics.FleetAllSurfaceTons)} vanillaFleetMetric={Fmt(context.FleetMetrics.VanillaFleetMetric)} vanillaFleetCapTons={Fmt(context.FleetMetrics.VanillaFleetCapTons)} vanillaFleetBudgetRatio={Fmt(context.FleetMetrics.VanillaFleetBudgetRatio)} fleetCount={context.FleetMetrics.FleetCount} fleetSurfaceCount={context.FleetMetrics.FleetSurfaceCount} fleetAllCount={context.FleetMetrics.FleetAllCount} fleetAllSurfaceCount={context.FleetMetrics.FleetAllSurfaceCount} buildingCount={context.FleetMetrics.BuildingCount} buildingTons={Fmt(context.UnderConstructionTons)} capacityLimit={Fmt(context.ShipbuildingCapacityLimit)} overrideShipybuildingLimit={Fmt(context.OverrideShipbuildingLimit)} stateBudget={Fmt(context.StateBudget)} aiShipGdpRatio={Fmt(context.AiShipGdpRatio)} isMajor={SafeBool(() => context.Player.isMajor)} isMedium={SafeBool(() => context.Player.isMedium)}.");
    }

    private static void LogGateSummary(BuildContext context)
    {
        bool cashGate = context.InitialGeneration
            ? context.Income >= context.Revenue * FleetGenerationMoneyMultiplier()
            : context.TempPlayerCash >= context.Revenue * Math.Max(0f, context.AiShipbuilding);
        bool crewGate = context.CrewPool >= context.MinCrewPool;
        bool queueGate = context.UnderConstructionTons < context.ShipbuildingCapacityLimit * Math.Max(0f, context.OverrideShipbuildingLimit);
        FleetMetricSnapshot currentFleetMetrics = FleetMetricSnapshot.For(context.Player, context.StateBudget, context.AiShipGdpRatio);
        float diagnosticFleetBudgetDenominator = Math.Max(0.0001f, context.CurrentFleetTons * Math.Max(0.0001f, context.AiShipGdpRatio));
        float diagnosticFleetBudgetRatio = context.StateBudget / diagnosticFleetBudgetDenominator;
        bool diagnosticFleetGdpGate = diagnosticFleetBudgetRatio >= 1f;
        bool fleetGdpGate = currentFleetMetrics.VanillaFleetGdpGate;

        context.CashGate = cashGate;
        context.CrewGate = crewGate;
        context.QueueGate = queueGate;
        context.FleetGdpGate = fleetGdpGate;
        context.DiagnosticFleetGdpGate = diagnosticFleetGdpGate;
        context.DiagnosticFleetBudgetRatio = diagnosticFleetBudgetRatio;
        context.FleetMetrics = currentFleetMetrics;

        Log($"{context.Prefix} gates cash={PassFail(cashGate)} crew={PassFail(crewGate)} queue={PassFail(queueGate)} fleetGDP={PassFail(fleetGdpGate)} vanillaFleetGDPGate={PassFail(currentFleetMetrics.VanillaFleetGdpGate)} vanillaFleetMetric={Fmt(currentFleetMetrics.VanillaFleetMetric)} vanillaFleetCapTons={Fmt(currentFleetMetrics.VanillaFleetCapTons)} vanillaFleetBudgetRatio={Fmt(currentFleetMetrics.VanillaFleetBudgetRatio)} fleetTonsDiagnostic={Fmt(context.CurrentFleetTons)} diagnosticFleetGDPGate={PassFail(diagnosticFleetGdpGate)} diagnosticFleetBudgetRatio={Fmt(diagnosticFleetBudgetRatio)} fleetAllSurfaceTons={Fmt(currentFleetMetrics.FleetAllSurfaceTons)} fleetCount={currentFleetMetrics.FleetCount} fleetSurfaceCount={currentFleetMetrics.FleetSurfaceCount} fleetAllCount={currentFleetMetrics.FleetAllCount} fleetAllSurfaceCount={currentFleetMetrics.FleetAllSurfaceCount} buildingCount={currentFleetMetrics.BuildingCount} likelyGate={LikelyGate(context)}.");
    }

    private static void LogFleetMix(BuildContext context)
    {
        Dictionary<string, FleetMix> mix = CreateEmptyFleetMix();

        foreach (Ship ship in SafeShipList(context.Player.fleetAll))
        {
            string type = NormalizeShipType(ship.shipType);
            if (!mix.TryGetValue(type, out FleetMix? entry))
                continue;

            entry.Count++;
            entry.Tons += Safe(() => ship.Tonnage(), 0f);
            if (Safe(() => ship.isBuilding, false))
            {
                entry.BuildingCount++;
                entry.BuildingTons += Safe(() => ship.Tonnage(), 0f);
            }
        }

        foreach (Submarine sub in SafeSubmarineList(context.Player.submarinesAll))
        {
            FleetMix entry = mix["SS"];
            entry.Count++;
            entry.Tons += Safe(() => sub.Tonnage(), 0f);
            if (Safe(() => sub.isBuilding, false))
            {
                entry.BuildingCount++;
                entry.BuildingTons += Safe(() => sub.Tonnage(), 0f);
            }
        }

        context.FleetMixByType = mix;
        int surfaceCount = SurfaceTypes.Sum(type => mix[type].Count);
        float surfaceTons = SurfaceTypes.Sum(type => mix[type].Tons);
        context.SurfaceCount = surfaceCount;
        context.SurfaceTons = surfaceTons;

        string details = string.Join(" ", FleetTypes.Select(type =>
        {
            FleetMix entry = mix[type];
            return $"{type}=c{entry.Count}/t{Fmt(entry.Tons)}/bc{entry.BuildingCount}/bt{Fmt(entry.BuildingTons)}";
        }));

        Log($"{context.Prefix} mix {details} surfaceCount={surfaceCount} surfaceTons={Fmt(surfaceTons)}.");
    }

    private static void LogSurfaceTypeDiagnostics(BuildContext context)
    {
        Dictionary<string, ShipType?> shipTypes = SurfaceTypes.ToDictionary(type => type, FindShipType, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, float> finalRatios = new(StringComparer.OrdinalIgnoreCase);
        foreach (string type in SurfaceTypes)
        {
            ShipType? shipType = shipTypes[type];
            float baseRatio = Safe(() => shipType?.buildRatio ?? 0f, 0f);
            finalRatios[type] = shipType == null ? 0f : Safe(() => context.Player.GetShipBuildRatio(shipType.name, baseRatio), baseRatio);
        }

        float desiredTotal = finalRatios.Values.Where(v => v > 0f).Sum();
        float currentSurfaceCount = Math.Max(1, context.SurfaceCount);
        float currentSurfaceTons = Math.Max(1f, context.SurfaceTons);

        foreach (string type in SurfaceTypes)
        {
            ShipType? shipType = shipTypes[type];
            FleetMix mix = context.FleetMixByType.TryGetValue(type, out FleetMix? value) ? value : new FleetMix();
            float baseRatio = Safe(() => shipType?.buildRatio ?? 0f, 0f);
            float finalRatio = finalRatios[type];
            float multiplier = baseRatio <= 0f ? 0f : finalRatio / baseRatio;
            float desiredShare = desiredTotal <= 0f ? 0f : finalRatio / desiredTotal;
            float currentCountShare = mix.Count / currentSurfaceCount;
            float currentTonnageShare = mix.Tons / currentSurfaceTons;
            List<Ship> designs = DesignsForType(context.Player, type);
            Ship? newestDesign = NewestDesign(designs);
            ValidationSummary validation = EstimateDesignAvailability(designs);
            Ship? newestBuildable = NewestDesign(validation.BuildableDesigns);
            bool hasAnyDesign = designs.Count > 0;
            bool hasBuildableDesign = validation.BuildableDesigns.Count > 0;
            bool countSatisfied = currentCountShare >= desiredShare && desiredShare > 0f;
            bool candidate = shipType != null && Safe(() => shipType.canBuild, false) && hasAnyDesign && hasBuildableDesign && !countSatisfied;
            string noReason = candidate
                ? "candidate"
                : CandidateNoReason(shipType, hasAnyDesign, hasBuildableDesign, countSatisfied);

            TypeDiagnostics diag = new(type)
            {
                Candidate = candidate,
                CountShareSatisfied = countSatisfied,
                HasAnyDesign = hasAnyDesign,
                HasBuildableDesign = hasBuildableDesign,
                NewestDesign = DesignLabel(newestDesign),
                NewestBuildableDesign = DesignLabel(newestBuildable),
                DesiredShare = desiredShare
            };
            diag.DiagnosticBuildable = validation.BuildableDesigns.Count;
            diag.DiagnosticFailures.AddRange(validation.Failures);
            context.TypeDiagnosticsByType[type] = diag;

            Log(
                $"{context.Prefix} type={type} baseRatio={Fmt(baseRatio)} personalityMult={Fmt(multiplier)} finalRatio={Fmt(finalRatio)} desiredShare={Pct(desiredShare)} countShare={Pct(currentCountShare)} tonShare={Pct(currentTonnageShare)} count={mix.Count} tons={Fmt(mix.Tons)} building={mix.BuildingCount}/{Fmt(mix.BuildingTons)} canBuildType={SafeBool(() => shipType?.canBuild ?? false)} hasDesign={hasAnyDesign} hasBuildable={hasBuildableDesign} newest={DesignLabel(newestDesign)} newestBuildable={DesignLabel(newestBuildable)} candidate={candidate} reason={noReason} availability=buildable:{validation.BuildableDesigns.Count},fail:{validation.FailureText}.");
        }
    }

    private static void LogDesignPowerSnapshot(BuildContext context)
    {
        if (!DetailedDiagnosticsEnabled || !logDesignPowerSnapshots)
            return;

        string key = $"{context.Player.Pointer.ToInt64()}:{context.Turn}";
        if (!LoggedDesignSnapshotKeys.Add(key))
            return;

        List<Ship> allDesigns = SafeShipList(context.Player.designs)
            .Where(design => !CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
            .ToList();
        List<Ship> surfaceDesigns = allDesigns
            .Where(design => SurfaceTypes.Contains(NormalizeShipType(design.shipType), StringComparer.OrdinalIgnoreCase))
            .OrderBy(design => NormalizeShipType(design.shipType))
            .ThenByDescending(design => Safe(() => DesignCreatedDate(design).turn, -1))
            .ThenBy(design => ShipLabel(design), StringComparer.Ordinal)
            .ToList();

        Dictionary<string, int> counts = SurfaceTypes.ToDictionary(type => type, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (Ship design in surfaceDesigns)
        {
            string type = NormalizeShipType(design.shipType);
            if (counts.ContainsKey(type))
                counts[type]++;
        }

        string countText = string.Join(" ", SurfaceTypes.Select(type => $"{type}={counts[type]}"));
        DesignBookPollutionSummary pollution = AnalyzeDesignBookPollution(context.Player, surfaceDesigns);
        Log(
            $"{context.Prefix} design-snapshot phase={context.Phase} personality={context.PersonalityName}/{context.PersonalityText} aiFleetMix={ModSettings.AiFleetCompositionModeText(ModSettings.AiFleetComposition)} aiArmsRace={ModSettings.AiArmsRaceModeText(ModSettings.AiArmsRace)} totalDesigns={allDesigns.Count} surfaceDesigns={surfaceDesigns.Count} counts {countText} pollution sharedDesignRows={pollution.SharedDesignRows} nameMatchDesigns={pollution.NameMatchDesigns} shipNameDesigns={pollution.ShipNameDesigns} sharedShipNameDesigns={pollution.SharedShipNameDesigns} duplicateFingerprintGroups={pollution.DuplicateFingerprintGroups} duplicateFingerprintRows={pollution.DuplicateFingerprintRows} examples={pollution.ExamplesText}.");

        if (pollution.HasSignals)
        {
            Log(
                $"UADVP design-book-pollution turn={context.DateLabel} nation={context.Nation} sharedDesignRows={pollution.SharedDesignRows} nameMatchDesigns={pollution.NameMatchDesigns} shipNameDesigns={pollution.ShipNameDesigns} sharedShipNameDesigns={pollution.SharedShipNameDesigns} duplicateFingerprintGroups={pollution.DuplicateFingerprintGroups} duplicateFingerprintRows={pollution.DuplicateFingerprintRows} examples={pollution.ExamplesText}.");
        }

        foreach (Ship design in surfaceDesigns)
            LogDesignPowerSnapshotLine(context, design);
    }

    private static void LogDesignPowerSnapshotLine(BuildContext context, Ship design)
    {
        string type = NormalizeShipType(design.shipType);
        GameDate created = DesignCreatedDate(design);
        DesignBuildCheck canBuild = CheckCanBuildDesign(design);
        EffectivePowerResult power = ShipEffectivePowerCalculator.Calculate(design);
        string armsRaceThreshold = ModSettings.AiArmsRaceEnabled
            ? AiDesignCompetitiveness.FormatRatio(AiDesignCompetitiveness.MinimumCompetitiveRatio)
            : "disabled";

        Log(
            $"{context.Prefix} design-power type={type} name={ShipLabel(design)} created={GameDateLabel(created, -1)} year={GameDateYear(created)} tons={MetricText(() => design.Tonnage())} cost={MetricText(() => design.Cost())} buildCostMonth={MetricText(() => design.BuildingCostPerMonth())} firepower={MetricText(() => design.GetFirepower())} powerProjection={MetricText(() => design.PowerProjection(true))} vanillaBasePower={ShipEffectivePowerCalculator.FormatCompactPower(power.VanillaBasePower)} basePower={ShipEffectivePowerCalculator.FormatCompactPower(power.BasePower)} adjustedPower={ShipEffectivePowerCalculator.FormatCompactPower(power.AdjustedPower)} vpWeapon={Fmt(power.VpWeaponPower)} vpGun={Fmt(power.AdjustedGunPower)} vpTorp={Fmt(power.AdjustedTorpedoPower)} armorAvg={Fmt(power.ArmorAverageMm)} vpArmor={Fmt(power.VpArmorScore)} typeArmor={Fmt(power.TypeArmorFactor)} armorFactor={Fmt(power.ArmorFactor)} weaponFactor={Fmt(power.WeaponFactor)} costFactor={Fmt(power.CostFactor)} speedFactor={Fmt(power.SpeedFactor)} q={Fmt(power.QualityMultiplier)} gun={Fmt(power.GunQualityMultiplier)} gunReload={Fmt(power.GunReloadFactor)} gunAcc={Fmt(power.GunIntrinsicAccuracyFactor)} shipAcc={Fmt(power.ShipAccuracyFactor)} gunRange={Fmt(power.GunRangeFactor)} torp={Fmt(power.TorpedoThreatFactor)} torpRange={Fmt(power.TorpedoRangeFactor)} torpSpeed={Fmt(power.TorpedoSpeedFactor)} torpReload={Fmt(power.TorpedoReloadFactor)} torpAcc={Fmt(power.TorpedoAccuracyFactor)} rawGunReload={Fmt(power.WeightedGunReloadScore)} rawGunRange={Fmt(power.WeightedGunRangeScore)} rawGunAcc={Fmt(power.WeightedGunAccuracyScore)} rawShipAcc={Fmt(power.ShipAccuracyScore)} rawTorpRange={Fmt(power.WeightedTorpedoRange)} rawTorpSpeed={Fmt(power.WeightedTorpedoSpeed)} rawTorpReload={Fmt(power.WeightedTorpedoReloadScore)} rawTorpAcc={Fmt(power.WeightedTorpedoAccuracyScore)} torpTubes={power.TorpedoTubeCount} benchmark={ShipEffectivePowerCalculator.FormatCompactPower(power.BenchmarkAdjustedPower)} competitive={ShipEffectivePowerCalculator.FormatRatio(power.CompetitivenessRatio)} threshold={armsRaceThreshold} canBuild={canBuild.CanBuild} reason={canBuild.Reason}.");

        if (power.TorpedoTubeCount > 0)
            MajorShipTorpedoCleanup.Audit(design, context.Player, "design-snapshot", context.DateLabel, logClean: true);
    }

    private static DesignBookPollutionSummary AnalyzeDesignBookPollution(Player player, List<Ship> surfaceDesigns)
    {
        try
        {
            List<Ship> fleetShips = SafeShipList(player.fleetAll);
            HashSet<string> fleetNameKeys = new(StringComparer.Ordinal);
            Dictionary<string, int> shipCountsByDesign = new(StringComparer.Ordinal);

            foreach (Ship ship in fleetShips)
            {
                string type = NormalizeShipType(ship.shipType);
                if (!SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                    continue;

                fleetNameKeys.Add(ShipNameKey(ship));

                Ship? design = Safe(() => ship.design, null);
                if (design != null)
                {
                    string designKey = ShipIdentity(design);
                    shipCountsByDesign[designKey] = shipCountsByDesign.TryGetValue(designKey, out int count) ? count + 1 : 1;
                }
            }

            int shipNameDesigns = 0;
            int nameMatchDesigns = 0;
            int sharedShipNameDesigns = 0;
            int sharedDesignRows = 0;
            List<string> examples = new();

            foreach (Ship design in surfaceDesigns)
            {
                if (IsSharedDesignMarker(design))
                    sharedDesignRows++;

                string designKey = ShipIdentity(design);
                int shipCount = shipCountsByDesign.TryGetValue(designKey, out int count) ? count : 0;
                if (!fleetNameKeys.Contains(ShipNameKey(design)))
                    continue;

                nameMatchDesigns++;
                if (shipCount == 0)
                {
                    shipNameDesigns++;
                    if (IsSharedDesignMarker(design))
                        sharedShipNameDesigns++;

                    if (examples.Count < 4)
                        examples.Add($"{NormalizeShipType(design.shipType)}:{ShipLabel(design)}");
                }
            }

            var duplicateGroups = surfaceDesigns
                .GroupBy(DesignFingerprint, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToList();
            int duplicateRows = duplicateGroups.Sum(group => group.Count());

            return new DesignBookPollutionSummary(
                sharedDesignRows,
                nameMatchDesigns,
                shipNameDesigns,
                sharedShipNameDesigns,
                duplicateGroups.Count,
                duplicateRows,
                examples.Count == 0 ? "none" : string.Join("|", examples));
        }
        catch (Exception ex)
        {
            Warn($"design-book pollution diagnostics failed. {ex.GetType().Name}: {ex.Message}");
            return DesignBookPollutionSummary.Empty;
        }
    }

    private static DesignBuildCheck CheckCanBuildDesign(Ship design)
    {
        return IsDesignAllowedByVp(design, out string reason)
            ? new DesignBuildCheck(true, reason)
            : new DesignBuildCheck(false, reason);
    }

    private static ValidationSummary EstimateDesignAvailability(List<Ship> designs)
    {
        ValidationSummary summary = new();

        foreach (Ship design in designs)
        {
            if (IsDesignAllowedByVp(design, out string reason))
                summary.BuildableDesigns.Add(design);
            else
                summary.Failures.Add(reason);
        }

        return summary;
    }

    private static bool IsDesignAllowedByVp(Ship design, out string reason)
    {
        reason = "none";
        if (CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
        {
            reason = "uadvpSmartRefitsAiRefitDesign";
            return false;
        }

        if (!ModSettings.AiArmsRaceEnabled)
            return true;

        if (AiDesignCompetitiveness.IsCompetitive(design, out _))
            return true;

        reason = "uadvpArmsRaceUncompetitive";
        return false;
    }

    private static void LogNewSurfaceOrders(BuildContext context, Player? player)
    {
        if (player == null)
            return;

        foreach (Ship ship in SafeShipList(player.fleetAll))
        {
            string id = ShipId(ship);
            if (context.BeforeShipIds.Contains(id))
                continue;

            context.ObservedOrder = true;
            string type = NormalizeShipType(ship.shipType);
            RecordTurnSummaryOrder(context, type);
            Ship? design = Safe(() => ship.design, null);
            if (DetailedDiagnosticsEnabled)
            {
                Log(
                    $"{context.Prefix} order turn={context.DateLabel} nation={context.Nation} selected={type} ship={ShipLabel(ship)} id={id} design={DesignLabel(design ?? ship)} tons={Fmt(Safe(() => ship.Tonnage(), 0f))} cost={Fmt(Safe(() => ship.Cost(), 0f))} dateCreated={GameDateLabel(ship.dateCreated, -1)} role={SafeString(() => ship.CurrentRole.ToString())} status={SafeString(() => ship.status.ToString())}.");
            }
        }
    }

    private static void LogNewSubmarineOrders(BuildContext context, Player? player)
    {
        if (player == null)
            return;

        foreach (Submarine sub in SafeSubmarineList(player.submarinesAll))
        {
            string id = VesselId(sub);
            if (context.BeforeShipIds.Contains(id))
                continue;

            context.ObservedOrder = true;
            RecordTurnSummaryOrder(context, "SS");
            if (DetailedDiagnosticsEnabled)
            {
                Log(
                    $"{context.Prefix} sub-order turn={context.DateLabel} nation={context.Nation} type={SubmarineTypeLabel(sub.Type)} id={id} tons={Fmt(Safe(() => sub.Tonnage(), 0f))} cost={Fmt(Safe(() => sub.Cost(), 0f))} status={SafeString(() => sub.status.ToString())}.");
            }
        }
    }

    private static void LogVanillaValidationSummary(BuildContext context)
    {
        foreach (string type in SurfaceTypes)
        {
            if (!context.TypeDiagnosticsByType.TryGetValue(type, out TypeDiagnostics? diag))
                continue;

            if (diag.VanillaValidationCalls <= 0)
                continue;

            string failures = diag.VanillaFailures.SummaryText();
            Log($"{context.Prefix} {type} vanilla-validation calls={diag.VanillaValidationCalls} buildable={diag.VanillaBuildable} fail={failures}.");
        }
    }

    private static void LogNoOrderSummary(BuildContext context)
    {
        string heavyCandidates = string.Join(
            ",",
            new[] { "BB", "BC", "CA" }.Select(type =>
            {
                bool candidate = context.TypeDiagnosticsByType.TryGetValue(type, out TypeDiagnostics? diag) && diag.Candidate;
                return $"{type}:{candidate.ToString().ToLowerInvariant()}";
            }));

        string heavyAvailability = string.Join(
            ",",
            new[] { "BB", "BC", "CA" }.Select(type =>
            {
                if (!context.TypeDiagnosticsByType.TryGetValue(type, out TypeDiagnostics? diag))
                    return $"{type}:none";
                if (!diag.HasAnyDesign)
                    return $"{type}:noDesign";
                if (!diag.HasBuildableDesign)
                    return $"{type}:invalid";
                if (diag.CountShareSatisfied)
                    return $"{type}:shareSatisfied";
                return $"{type}:candidate";
            }));

        Log($"{context.Prefix} none turn={context.DateLabel} nation={context.Nation} likelyGate={LikelyGate(context)} heavyCandidates={heavyCandidates} heavyState={heavyAvailability}.");
        if (string.Equals(LikelyGate(context), "candidate", StringComparison.Ordinal))
            LogCandidateNoOrderDetails(context);
    }

    private static void LogCandidateNoOrderDetails(BuildContext context)
    {
        string candidates = CandidateTypes(context);
        int validationCalls = TotalVanillaValidationCalls(context);
        string reason = CandidateNoOrderReason(context, candidates);
        string pickerDetails = context.WeightedPredicateDetails.Count == 0
            ? "none"
            : string.Join(",", context.WeightedPredicateDetails);
        string buildDetails = context.BuildShipsFromDesignDetails.Count == 0
            ? "none"
            : string.Join(",", context.BuildShipsFromDesignDetails);
        string postGates = CurrentGateText(context.Player, context);

        Log(
            $"{context.Prefix} candidate-no-order turn={context.DateLabel} nation={context.Nation} candidates={candidates} pickerCalls={context.WeightedPredicateCalls} pickerAccepted={context.WeightedPredicateAccepted} pickerRejected={context.WeightedPredicateRejected} pickerSelected={context.LastWeightedSelectedType} pickerReason={context.LastWeightedDecisionReason} selectedSeen={context.WeightedSelectedSeen.ToString().ToLowerInvariant()} fallback={context.LastWeightedFallback.ToString().ToLowerInvariant()} suppressVanilla={context.LastWeightedSuppressVanilla.ToString().ToLowerInvariant()} validationCalls={validationCalls} buildCalls={context.BuildShipsFromDesignCalls} buildCreated={context.BuildShipsFromDesignSuccesses} reason={reason} postGates={postGates} pickerDetails={pickerDetails} buildDetails={buildDetails}.");
    }

    private static string CandidateTypes(BuildContext context)
    {
        string[] candidates = SurfaceTypes
            .Where(type => context.TypeDiagnosticsByType.TryGetValue(type, out TypeDiagnostics? diag) && diag.Candidate)
            .ToArray();
        return candidates.Length == 0 ? "none" : string.Join(",", candidates);
    }

    private static int TotalVanillaValidationCalls(BuildContext context)
        => context.TypeDiagnosticsByType.Values.Sum(diag => diag.VanillaValidationCalls);

    private static string CandidateNoOrderReason(BuildContext context, string candidates)
    {
        if (!context.CashGate || !context.CrewGate || !context.QueueGate || !context.FleetGdpGate)
            return "broad-gate-blocked";
        if (string.Equals(candidates, "none", StringComparison.Ordinal))
            return "no-candidate-types";
        if (context.WeightedPredicateCalls == 0)
            return "vanilla-did-not-reach-type-predicate";
        if (context.LastWeightedSuppressVanilla)
            return "picker-suppressed-vanilla";
        if (string.Equals(context.LastWeightedSelectedType, "none", StringComparison.OrdinalIgnoreCase))
            return "picker-selected-none";
        if (!context.WeightedSelectedSeen || context.WeightedPredicateAccepted == 0)
            return "selected-type-not-used-by-vanilla";
        if (context.BuildShipsFromDesignCalls > 0 && context.BuildShipsFromDesignSuccesses == 0)
            return "build-call-produced-no-ship";
        if (!CurrentBroadGatesPass(context.Player, context))
            return "post-prefix-state-changed";
        return "unknown-candidate-no-order";
    }

    private static bool CurrentBroadGatesPass(Player player, BuildContext context)
    {
        bool cashGate = context.InitialGeneration
            ? Safe(() => player.Income(), 0f) >= Safe(() => player.Revenue(), 0f) * FleetGenerationMoneyMultiplier()
            : context.TempPlayerCash >= Safe(() => player.Revenue(), 0f) * Math.Max(0f, context.AiShipbuilding);
        bool crewGate = Safe(() => player.crewPool, 0) >= context.MinCrewPool;
        bool queueGate = Safe(() => player.ShipTonnageUnderConstruction(), 0f) < context.ShipbuildingCapacityLimit * Math.Max(0f, context.OverrideShipbuildingLimit);
        FleetMetricSnapshot fleetMetrics = FleetMetricSnapshot.For(player, Safe(() => player.StateBudget(), context.StateBudget), context.AiShipGdpRatio);
        bool fleetGdpGate = fleetMetrics.VanillaFleetGdpGate;
        return cashGate && crewGate && queueGate && fleetGdpGate;
    }

    private static string CurrentGateText(Player player, BuildContext context)
    {
        bool cashGate = context.InitialGeneration
            ? Safe(() => player.Income(), 0f) >= Safe(() => player.Revenue(), 0f) * FleetGenerationMoneyMultiplier()
            : context.TempPlayerCash >= Safe(() => player.Revenue(), 0f) * Math.Max(0f, context.AiShipbuilding);
        bool crewGate = Safe(() => player.crewPool, 0) >= context.MinCrewPool;
        bool queueGate = Safe(() => player.ShipTonnageUnderConstruction(), 0f) < context.ShipbuildingCapacityLimit * Math.Max(0f, context.OverrideShipbuildingLimit);
        float stateBudget = Safe(() => player.StateBudget(), 0f);
        float diagnosticFleetTons = Safe(() => player.TotalFleetTonnage(), context.CurrentFleetTons);
        float diagnosticFleetBudgetDenominator = Math.Max(0.0001f, diagnosticFleetTons * Math.Max(0.0001f, context.AiShipGdpRatio));
        float diagnosticFleetBudgetRatio = stateBudget / diagnosticFleetBudgetDenominator;
        bool diagnosticFleetGdpGate = diagnosticFleetBudgetRatio >= 1f;
        FleetMetricSnapshot fleetMetrics = FleetMetricSnapshot.For(player, stateBudget, context.AiShipGdpRatio);
        return $"cash={PassFail(cashGate)},crew={PassFail(crewGate)},queue={PassFail(queueGate)},fleetGDP={PassFail(fleetMetrics.VanillaFleetGdpGate)},vanillaFleetMetric={Fmt(fleetMetrics.VanillaFleetMetric)},vanillaFleetCapTons={Fmt(fleetMetrics.VanillaFleetCapTons)},vanillaFleetBudgetRatio={Fmt(fleetMetrics.VanillaFleetBudgetRatio)},fleetTonsDiagnostic={Fmt(diagnosticFleetTons)},diagnosticFleetGDP={PassFail(diagnosticFleetGdpGate)},diagnosticFleetBudgetRatio={Fmt(diagnosticFleetBudgetRatio)},fleetAllSurfaceTons={Fmt(fleetMetrics.FleetAllSurfaceTons)},fleetCount={fleetMetrics.FleetCount},fleetAllCount={fleetMetrics.FleetAllCount},buildingCount={fleetMetrics.BuildingCount}";
    }

    private static string CandidateNoReason(ShipType? shipType, bool hasDesign, bool hasBuildableDesign, bool countSatisfied)
    {
        if (shipType == null)
            return "unavailableType";
        if (!Safe(() => shipType.canBuild, false))
            return "unavailable";
        if (!hasDesign)
            return "noDesign";
        if (!hasBuildableDesign)
            return "noBuildableDesign";
        if (countSatisfied)
            return "countShareAlreadySatisfied";

        return "unknown";
    }

    private static string LikelyGate(BuildContext context)
    {
        if (!context.CashGate)
            return "cash";
        if (!context.CrewGate)
            return "crew";
        if (!context.QueueGate)
            return "queue";
        if (!context.FleetGdpGate)
            return "fleetGDP";
        return "candidate";
    }

    private static float FleetGenerationMoneyMultiplier()
    {
        float max = Safe(() => CampaignController.Param("fleet_generation_money_max", 1f), 1f);
        float min = Safe(() => CampaignController.Param("fleet_generation_money_min", 1f), 1f);
        return Safe(() => CampaignController.GetBaseYearMultiplier(max, min, true, -1), max);
    }

    private static List<Ship> DesignsForType(Player player, string type)
    {
        List<Ship> designs = new();
        foreach (Ship design in SafeShipList(player.designs))
        {
            if (CampaignSmartRefitPatch.IsBlockedAiRefitDesign(design))
                continue;

            if (string.Equals(NormalizeShipType(design.shipType), type, StringComparison.OrdinalIgnoreCase))
                designs.Add(design);
        }

        return designs;
    }

    private static Ship? NewestDesign(List<Ship> designs)
        => designs
            .OrderByDescending(design => Safe(() => design.dateCreated.turn, -1))
            .ThenByDescending(design => Safe(() => design.isRefitDesign ? design.dateCreatedRefit.turn : design.dateCreated.turn, -1))
            .FirstOrDefault();

    private static ShipType? FindShipType(string type)
    {
        try
        {
            var shipTypes = G.GameData?.shipTypes;
            if (shipTypes == null)
                return null;

            foreach (var pair in shipTypes)
            {
                ShipType? shipType = pair.Value;
                if (shipType == null)
                    continue;

                if (string.Equals(NormalizeShipType(shipType), type, StringComparison.OrdinalIgnoreCase))
                    return shipType;
            }
        }
        catch
        {
        }

        return null;
    }

    private static HashSet<string> SnapshotShipIds(Il2CppSystem.Collections.Generic.IEnumerable<Ship> ships)
        => SafeShipList(ships).Select(ShipIdentity).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> SnapshotShipIds(IEnumerable<Ship> ships)
        => ships.Where(ship => ship != null).Select(ShipIdentity).ToHashSet(StringComparer.Ordinal);

    private static List<ShipSnapshot> SnapshotShipDetails(Il2CppSystem.Collections.Generic.IEnumerable<Ship>? ships)
        => SafeShipList(ships).Select(ShipSnapshot.From).ToList();

    private static HashSet<string> SnapshotSubmarineIds(Il2CppSystem.Collections.Generic.IEnumerable<Submarine> submarines)
        => SafeSubmarineList(submarines).Select(VesselId).ToHashSet(StringComparer.Ordinal);

    private static List<Ship> SafeShipList(Il2CppSystem.Collections.Generic.IEnumerable<Ship>? ships)
    {
        List<Ship> result = new();
        if (ships == null)
            return result;

        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Ship>(ships);
            foreach (Ship ship in list)
            {
                if (ship != null)
                    result.Add(ship);
            }
        }
        catch
        {
            Warn("failed to enumerate ship list.");
        }

        return result;
    }

    private static List<Submarine> SafeSubmarineList(Il2CppSystem.Collections.Generic.IEnumerable<Submarine>? submarines)
    {
        List<Submarine> result = new();
        if (submarines == null)
            return result;

        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Submarine>(submarines);
            foreach (Submarine sub in list)
            {
                if (sub != null)
                    result.Add(sub);
            }
        }
        catch
        {
            Warn("failed to enumerate submarine list.");
        }

        return result;
    }

    private static Dictionary<string, FleetMix> CreateEmptyFleetMix()
    {
        Dictionary<string, FleetMix> mix = new(StringComparer.OrdinalIgnoreCase);
        foreach (string type in FleetTypes)
            mix[type] = new FleetMix();
        return mix;
    }

    private static string NormalizeShipType(ShipType? type)
    {
        string raw = SafeString(() => type?.name);
        if (string.Equals(raw, "<empty>", StringComparison.OrdinalIgnoreCase))
            raw = SafeString(() => type?.nameUi);
        return NormalizeShipType(raw);
    }

    private static string NormalizeShipType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNK";

        string compact = value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        return compact switch
        {
            "B" or "BB" or "BATTLESHIP" => "BB",
            "BC" or "BATTLECRUISER" => "BC",
            "CA" or "HEAVYCRUISER" => "CA",
            "CL" or "LIGHTCRUISER" => "CL",
            "DD" or "DESTROYER" => "DD",
            "TB" or "TORPEDOBOAT" => "TB",
            "SS" or "SUB" or "SUBMARINE" => "SS",
            _ => compact
        };
    }

    private static string ShipId(Ship? ship)
    {
        try
        {
            return ship?.id.ToString() ?? "<null>";
        }
        catch
        {
            return ship?.GetHashCode().ToString(CultureInfo.InvariantCulture) ?? "<null>";
        }
    }

    private static string ShipIdentity(Ship? ship)
    {
        if (ship == null)
            return "<null>";

        long pointer = Safe(() => ship.Pointer.ToInt64(), 0L);
        if (pointer != 0L)
            return $"ptr:{pointer.ToString(CultureInfo.InvariantCulture)}";

        return $"id:{ShipId(ship)}";
    }

    private static string ShipPointerText(Ship? ship)
    {
        long pointer = Safe(() => ship?.Pointer.ToInt64() ?? 0L, 0L);
        return pointer == 0L ? "<null>" : pointer.ToString(CultureInfo.InvariantCulture);
    }

    private static bool SameShipIdentity(Ship? left, Ship? right)
    {
        if (left == null || right == null)
            return false;

        return string.Equals(ShipIdentity(left), ShipIdentity(right), StringComparison.Ordinal);
    }

    private static string VesselId(VesselEntity? vessel)
    {
        try
        {
            return vessel?.id.ToString() ?? "<null>";
        }
        catch
        {
            return vessel?.GetHashCode().ToString(CultureInfo.InvariantCulture) ?? "<null>";
        }
    }

    private static string ShipLabel(Ship? ship)
    {
        if (ship == null)
            return "<null>";

        string name = SafeString(() => ship.Name(false, false, false, false, true));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => ship.vesselName);
    }

    private static string DesignLabel(Ship? ship)
    {
        if (ship == null)
            return "none";

        string type = NormalizeShipType(ship.shipType);
        string name = ShipLabel(ship);
        int year = GameDateYear(ship.isRefitDesign ? ship.dateCreatedRefit : ship.dateCreated);
        float tons = Safe(() => ship.Tonnage(), 0f);
        return $"{type}:{name}:{year}:{Fmt(tons)}t";
    }

    private static string ShipNameKey(Ship ship)
        => $"{NormalizeShipType(ship.shipType)}|{NormalizeNameKey(ShipLabel(ship))}";

    private static string NormalizeNameKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Trim().ToUpperInvariant();

    private static string DesignFingerprint(Ship design)
    {
        string type = NormalizeShipType(design.shipType);
        int year = GameDateYear(DesignCreatedDate(design));
        float tons = Safe(() => design.Tonnage(), 0f);
        float firepower = Safe(() => design.GetFirepower(), 0f);
        float tonBucket = (float)Math.Round(tons / 25f, MidpointRounding.AwayFromZero) * 25f;
        float firepowerBucket = (float)Math.Round(firepower / 50f, MidpointRounding.AwayFromZero) * 50f;
        return $"{type}:{year}:{Fmt(tonBucket)}t:fp{Fmt(firepowerBucket)}";
    }

    private static bool PlayerDesignsContain(Player? player, Ship? ship)
    {
        if (player == null || ship == null)
            return false;

        return SafeShipList(player.designs).Any(design => SameShipIdentity(design, ship));
    }

    private static bool IsSharedDesignMarker(Ship? ship)
        => ship != null && Safe(() => ReadBoolMember(ship, "isSharedDesign") ||
                                     ReadBoolMember(ship, "IsSharedDesign") ||
                                     ReadBoolMember(ship, "isShared"), false);

    private static string SharedMarkerDetails(Ship? ship)
    {
        if (ship == null)
            return "IsSharedDesign=<null>,isSharedDesign=<null>,isShared=<null>";

        return $"IsSharedDesign={OptionalBoolText(ReadOptionalBoolMember(ship, "IsSharedDesign"))},isSharedDesign={OptionalBoolText(ReadOptionalBoolMember(ship, "isSharedDesign"))},isShared={OptionalBoolText(ReadOptionalBoolMember(ship, "isShared"))}";
    }

    private static string ShipStoreSummary(Ship? ship)
    {
        if (ship == null)
            return "store=<null>";

        try
        {
            Ship.Store? store = ship.ToStore(false);
            return ShipStoreSummary(store);
        }
        catch (Exception ex)
        {
            return $"store=error:{ex.GetType().Name}";
        }
    }

    private static string ShipStoreSummary(Ship.Store? store)
    {
        if (store == null)
            return "store=<null>";

        return $"id={SafeString(() => store.id.ToString())},designId={SafeString(() => store.designId.ToString())},vessel={SafeString(() => store.vesselName)},isShared={Safe(() => store.isSharedDesign, false).ToString().ToLowerInvariant()},dateCreatedTurn={Safe(() => store.dateCreated.turn, -1)},dateFinishedTurn={Safe(() => store.dateFinished.turn, -1)},dateCreatedRefitTurn={Safe(() => store.dateCreatedRefit.turn, -1)}";
    }

    private static bool ReadBoolMember(object target, string memberName)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(target) is bool propertyValue)
            return propertyValue;

        FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(target) is bool fieldValue && fieldValue;
    }

    private static bool? ReadOptionalBoolMember(object target, string memberName)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(target) is bool propertyValue)
                return propertyValue;

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(target) is bool fieldValue)
                return fieldValue;
        }
        catch
        {
        }

        return null;
    }

    private static string OptionalBoolText(bool? value)
        => value.HasValue ? BoolText(value.Value) : "?";

    private static void ClearBoolFlagIfTrue(object target, string memberName, string label, List<string> cleared, List<string> failed)
    {
        bool wasSet = Safe(() => ReadBoolMember(target, memberName), false);
        if (!wasSet)
            return;

        if (TrySetBoolMember(target, memberName, false))
            cleared.Add(label);
        else
            failed.Add(label);
    }

    private static bool TrySetBoolMember(object target, string memberName, bool value)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? setter = property?.GetSetMethod(true);
            if (property != null && setter != null)
            {
                property.SetValue(target, value);
                return true;
            }

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void ClearRefitDesignMarkerIfTrue(Ship ship, List<string> cleared, List<string> failed)
    {
        if (!ReadBoolMember(ship, "isRefitDesign"))
            return;

        if (TrySetStringMember(ship, "refitDesignName", string.Empty) ||
            TrySetStringMember(ship, "<refitDesignName>k__BackingField", string.Empty))
        {
            if (!ReadBoolMember(ship, "isRefitDesign"))
                cleared.Add("isRefitDesign");
            else
                failed.Add("isRefitDesign");
        }
        else
        {
            failed.Add("isRefitDesign");
        }
    }

    private static bool TrySetStringMember(object target, string memberName, string value)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? setter = property?.GetSetMethod(true);
            if (property != null && setter != null)
            {
                property.SetValue(target, value);
                return true;
            }

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(string))
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string SubmarineTypeLabel(SubmarineType? type)
    {
        string label = SafeString(() => type?.nameUi);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        label = SafeString(() => type?.type);
        if (!string.Equals(label, "<empty>", StringComparison.Ordinal))
            return label;

        return SafeString(() => type?.name);
    }

    private static string PlayerLabel(Player? player)
    {
        if (player == null)
            return "<null>";

        string name = SafeString(() => player.Name(false));
        if (!string.Equals(name, "<empty>", StringComparison.Ordinal))
            return name;

        return SafeString(() => player.data?.name);
    }

    private static string CampaignDateLabel(CampaignController? campaign, int turn)
        => Safe(() => campaign?.CurrentDate.ToString(false) ?? (turn >= 0 ? $"turn-{turn}" : "unknown"), turn >= 0 ? $"turn-{turn}" : "unknown");

    private static string GameDateLabel(GameDate date, int turn)
        => Safe(() => date.ToString(false), turn >= 0 ? $"turn-{turn}" : "unknown");

    private static GameDate DesignCreatedDate(Ship design)
        => Safe(() => design.isRefitDesign ? design.dateCreatedRefit : design.dateCreated, design.dateCreated);

    private static int GameDateYear(GameDate date)
    {
        string text = Safe(() => date.ToString(true), string.Empty);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) ? year : 0;
    }

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();

    private static string PassFail(bool value)
        => value ? "pass" : "block";

    private static string BoolText(bool value)
        => value ? "true" : "false";

    private static string Pct(float value)
        => (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Fmt(float value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string MetricText(Func<float> read)
    {
        try
        {
            return Fmt(read());
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static string SafeBool(Func<bool> read)
    {
        try
        {
            return read().ToString();
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void PopContext(Stack<BuildContext> stack, BuildContext context)
    {
        if (stack.Count == 0)
            return;

        if (ReferenceEquals(stack.Peek(), context))
        {
            stack.Pop();
            return;
        }

        BuildContext[] remaining = stack.Where(item => !ReferenceEquals(item, context)).Reverse().ToArray();
        stack.Clear();
        foreach (BuildContext item in remaining)
            stack.Push(item);
    }

    private static void Log(string message)
        => Melon<UADVanillaPlusMod>.Logger.Msg(message);

    private static void Warn(string message)
        => Melon<UADVanillaPlusMod>.Logger.Warning($"{LogPrefix}: {message}");

    internal enum BuildKind
    {
        Surface,
        Submarine,
    }

    internal sealed class FleetMetricSnapshot
    {
        private FleetMetricSnapshot(
            float vanillaFleetMetric,
            float vanillaFleetCapTons,
            float vanillaFleetBudgetRatio,
            bool vanillaFleetGdpGate,
            float fleetAllSurfaceTons,
            int fleetCount,
            int fleetSurfaceCount,
            int fleetAllCount,
            int fleetAllSurfaceCount,
            int buildingCount)
        {
            VanillaFleetMetric = vanillaFleetMetric;
            VanillaFleetCapTons = vanillaFleetCapTons;
            VanillaFleetBudgetRatio = vanillaFleetBudgetRatio;
            VanillaFleetGdpGate = vanillaFleetGdpGate;
            FleetAllSurfaceTons = fleetAllSurfaceTons;
            FleetCount = fleetCount;
            FleetSurfaceCount = fleetSurfaceCount;
            FleetAllCount = fleetAllCount;
            FleetAllSurfaceCount = fleetAllSurfaceCount;
            BuildingCount = buildingCount;
        }

        internal float VanillaFleetMetric { get; }
        internal float VanillaFleetCapTons { get; }
        internal float VanillaFleetBudgetRatio { get; }
        internal bool VanillaFleetGdpGate { get; }
        internal float FleetAllSurfaceTons { get; }
        internal int FleetCount { get; }
        internal int FleetSurfaceCount { get; }
        internal int FleetAllCount { get; }
        internal int FleetAllSurfaceCount { get; }
        internal int BuildingCount { get; }

        internal static FleetMetricSnapshot For(Player player, float stateBudget, float aiShipGdpRatio)
        {
            List<Ship> fleet = SafeShipList(player.fleet);
            List<Ship> fleetAll = SafeShipList(player.fleetAll);
            float vanillaFleetMetric = 0f;
            int fleetSurfaceCount = 0;
            foreach (Ship ship in fleet)
            {
                if (SurfaceTypes.Contains(NormalizeShipType(ship.shipType), StringComparer.OrdinalIgnoreCase))
                    fleetSurfaceCount++;

                // Vanilla BuildNewShips sums player.fleet with b__198_5, which
                // dispatches Ship.Weight(false, false) rather than TotalFleetTonnage().
                vanillaFleetMetric += Safe(() => ship.Weight(false, false), 0f);
            }

            float fleetAllSurfaceTons = 0f;
            int fleetAllSurfaceCount = 0;
            int buildingCount = 0;
            foreach (Ship ship in fleetAll)
            {
                string type = NormalizeShipType(ship.shipType);
                if (SurfaceTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    fleetAllSurfaceCount++;
                    fleetAllSurfaceTons += Safe(() => ship.Tonnage(), 0f);
                }

                if (Safe(() => ship.isBuilding, false))
                    buildingCount++;
            }

            float ratioParam = Math.Max(0.0001f, aiShipGdpRatio);
            float vanillaFleetCapTons = stateBudget / ratioParam;
            float denominator = Math.Max(0.0001f, vanillaFleetMetric * ratioParam);
            float vanillaFleetBudgetRatio = stateBudget / denominator;
            bool vanillaFleetGdpGate = vanillaFleetBudgetRatio >= 1f;
            return new FleetMetricSnapshot(
                vanillaFleetMetric,
                vanillaFleetCapTons,
                vanillaFleetBudgetRatio,
                vanillaFleetGdpGate,
                fleetAllSurfaceTons,
                fleet.Count,
                fleetSurfaceCount,
                fleetAll.Count,
                fleetAllSurfaceCount,
                buildingCount);
        }
    }

    internal sealed class AiBuildTurnSummary
    {
        private readonly SortedDictionary<string, AiBuildNationSummary> nations = new(StringComparer.Ordinal);

        internal AiBuildTurnSummary(int turnIndex, string dateLabel)
        {
            TurnIndex = turnIndex;
            DateLabel = dateLabel;
        }

        internal int TurnIndex { get; }
        internal string DateLabel { get; }
        internal int Total => nations.Values.Sum(nation => nation.Total);

        internal void EnsureNation(string nation)
        {
            if (!nations.ContainsKey(nation))
                nations[nation] = new AiBuildNationSummary(nation);
        }

        internal void Add(string nation, string type)
        {
            EnsureNation(nation);
            nations[nation].Add(type);
        }

        internal string CountriesText()
            => nations.Count == 0
                ? "none"
                : string.Join(",", nations.Values.Select(nation => nation.ToSummaryText()));
    }

    internal sealed class AiBuildNationSummary
    {
        private readonly Dictionary<string, int> countsByType = new(StringComparer.OrdinalIgnoreCase);

        internal AiBuildNationSummary(string nation)
        {
            Nation = nation;
        }

        internal string Nation { get; }
        internal int Total => countsByType.Values.Sum();

        internal void Add(string type)
        {
            string normalizedType = string.IsNullOrWhiteSpace(type) ? "UNK" : type;
            countsByType[normalizedType] = countsByType.TryGetValue(normalizedType, out int current)
                ? current + 1
                : 1;
        }

        internal string ToSummaryText()
        {
            if (Total <= 0)
                return $"{Nation}:0";

            string details = string.Join(
                ",",
                countsByType
                    .OrderBy(pair => TypeSortIndex(pair.Key))
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
            return $"{Nation}:{Total}({details})";
        }

        private static int TypeSortIndex(string type)
        {
            for (int i = 0; i < FleetTypes.Length; i++)
            {
                if (string.Equals(FleetTypes[i], type, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return FleetTypes.Length;
        }
    }

    internal sealed class BuildContext
    {
        internal BuildContext(
            BuildKind kind,
            Player player,
            float tempPlayerCash,
            int turn,
            string dateLabel,
            string phase,
            string nation,
            string dataName,
            string dataNameUi,
            string aiName,
            string personalityName,
            string personalityText,
            float aiShipbuilding,
            float cash,
            float revenue,
            float income,
            float stateBudget,
            int crewPool,
            float minCrewPool,
            float currentFleetTons,
            float underConstructionTons,
            float shipbuildingCapacityLimit,
            float overrideShipbuildingLimit,
            float aiShipGdpRatio,
            FleetMetricSnapshot fleetMetrics,
            bool initialGeneration)
        {
            Kind = kind;
            Player = player;
            TempPlayerCash = tempPlayerCash;
            Turn = turn;
            DateLabel = dateLabel;
            Phase = phase;
            Nation = nation;
            DataName = dataName;
            DataNameUi = dataNameUi;
            AiName = aiName;
            PersonalityName = personalityName;
            PersonalityText = personalityText;
            AiShipbuilding = aiShipbuilding;
            Cash = cash;
            Revenue = revenue;
            Income = income;
            StateBudget = stateBudget;
            CrewPool = crewPool;
            MinCrewPool = minCrewPool;
            CurrentFleetTons = currentFleetTons;
            UnderConstructionTons = underConstructionTons;
            ShipbuildingCapacityLimit = shipbuildingCapacityLimit;
            OverrideShipbuildingLimit = overrideShipbuildingLimit;
            AiShipGdpRatio = aiShipGdpRatio;
            FleetMetrics = fleetMetrics;
            InitialGeneration = initialGeneration;
        }

        internal BuildKind Kind { get; }
        internal Player Player { get; }
        internal float TempPlayerCash { get; }
        internal int Turn { get; }
        internal string DateLabel { get; }
        internal string Phase { get; }
        internal string Nation { get; }
        internal string DataName { get; }
        internal string DataNameUi { get; }
        internal string AiName { get; }
        internal string PersonalityName { get; }
        internal string PersonalityText { get; }
        internal float AiShipbuilding { get; }
        internal float Cash { get; }
        internal float Revenue { get; }
        internal float Income { get; }
        internal float StateBudget { get; }
        internal int CrewPool { get; }
        internal float MinCrewPool { get; }
        internal float CurrentFleetTons { get; }
        internal float UnderConstructionTons { get; }
        internal float ShipbuildingCapacityLimit { get; }
        internal float OverrideShipbuildingLimit { get; }
        internal float AiShipGdpRatio { get; }
        internal FleetMetricSnapshot FleetMetrics { get; set; }
        internal bool InitialGeneration { get; }
        internal string Prefix => $"{LogPrefix} turn={DateLabel} nation={Nation} kind={Kind.ToString().ToLowerInvariant()}";
        internal HashSet<string> BeforeShipIds { get; set; } = new(StringComparer.Ordinal);
        internal Dictionary<string, FleetMix> FleetMixByType { get; set; } = CreateEmptyFleetMix();
        internal Dictionary<string, TypeDiagnostics> TypeDiagnosticsByType { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal bool CashGate { get; set; } = true;
        internal bool CrewGate { get; set; } = true;
        internal bool QueueGate { get; set; } = true;
        internal bool FleetGdpGate { get; set; } = true;
        internal bool DiagnosticFleetGdpGate { get; set; } = true;
        internal float DiagnosticFleetBudgetRatio { get; set; }
        internal bool ObservedOrder { get; set; }
        internal int SurfaceCount { get; set; }
        internal float SurfaceTons { get; set; }
        internal int WeightedPredicateCalls { get; set; }
        internal int WeightedPredicateAccepted { get; set; }
        internal int WeightedPredicateRejected { get; set; }
        internal bool WeightedSelectedSeen { get; set; }
        internal string LastWeightedSelectedType { get; set; } = "none";
        internal string LastWeightedDecisionReason { get; set; } = "none";
        internal bool LastWeightedFallback { get; set; }
        internal bool LastWeightedSuppressVanilla { get; set; }
        internal List<string> WeightedPredicateDetails { get; } = new();
        internal int BuildShipsFromDesignCalls { get; set; }
        internal int BuildShipsFromDesignSuccesses { get; set; }
        internal int BuildShipsFromDesignFailures { get; set; }
        internal List<string> BuildShipsFromDesignDetails { get; } = new();
    }

    internal sealed class BuildAttemptState
    {
        internal BuildAttemptState(
            BuildContext context,
            string designLabel,
            string type,
            string designName,
            int amount,
            bool force,
            float cashBefore,
            int crewBefore,
            float buildingTonsBefore,
            List<ShipSnapshot> beforeFleetSnapshot,
            List<ShipSnapshot> beforeDesignSnapshot)
        {
            Context = context;
            DesignLabel = designLabel;
            Type = type;
            DesignName = designName;
            Amount = amount;
            Force = force;
            CashBefore = cashBefore;
            CrewBefore = crewBefore;
            BuildingTonsBefore = buildingTonsBefore;
            BeforeFleetSnapshot = beforeFleetSnapshot;
            BeforeDesignSnapshot = beforeDesignSnapshot;
            BeforeShipIds = beforeFleetSnapshot.Select(ship => ship.Identity).ToHashSet(StringComparer.Ordinal);
            BeforeDesignIds = beforeDesignSnapshot.Select(ship => ship.Identity).ToHashSet(StringComparer.Ordinal);
        }

        internal BuildContext Context { get; }
        internal string DesignLabel { get; }
        internal string Type { get; }
        internal string DesignName { get; }
        internal int Amount { get; }
        internal bool Force { get; }
        internal float CashBefore { get; }
        internal int CrewBefore { get; }
        internal float BuildingTonsBefore { get; }
        internal List<ShipSnapshot> BeforeFleetSnapshot { get; }
        internal List<ShipSnapshot> BeforeDesignSnapshot { get; }
        internal HashSet<string> BeforeShipIds { get; }
        internal HashSet<string> BeforeDesignIds { get; }
    }

    internal sealed class ShipSnapshot
    {
        private ShipSnapshot(
            string identity,
            string id,
            string pointer,
            string type,
            string name,
            string designId,
            string designPointer,
            string designName)
        {
            Identity = identity;
            Id = id;
            Pointer = pointer;
            Type = type;
            Name = name;
            DesignId = designId;
            DesignPointer = designPointer;
            DesignName = designName;
        }

        internal string Identity { get; }
        internal string Id { get; }
        internal string Pointer { get; }
        internal string Type { get; }
        internal string Name { get; }
        internal string DesignId { get; }
        internal string DesignPointer { get; }
        internal string DesignName { get; }

        internal static ShipSnapshot From(Ship ship)
        {
            Ship? design = Safe(() => ship.design, null);
            return new ShipSnapshot(
                ShipIdentity(ship),
                ShipId(ship),
                ShipPointerText(ship),
                NormalizeShipType(ship.shipType),
                ShipLabel(ship),
                ShipId(design),
                ShipPointerText(design),
                ShipLabel(design));
        }
    }

    private sealed class DesignBookPollutionSummary
    {
        internal static readonly DesignBookPollutionSummary Empty = new(0, 0, 0, 0, 0, 0, "none");

        internal DesignBookPollutionSummary(
            int sharedDesignRows,
            int nameMatchDesigns,
            int shipNameDesigns,
            int sharedShipNameDesigns,
            int duplicateFingerprintGroups,
            int duplicateFingerprintRows,
            string examplesText)
        {
            SharedDesignRows = sharedDesignRows;
            NameMatchDesigns = nameMatchDesigns;
            ShipNameDesigns = shipNameDesigns;
            SharedShipNameDesigns = sharedShipNameDesigns;
            DuplicateFingerprintGroups = duplicateFingerprintGroups;
            DuplicateFingerprintRows = duplicateFingerprintRows;
            ExamplesText = examplesText;
        }

        internal int SharedDesignRows { get; }
        internal int NameMatchDesigns { get; }
        internal int ShipNameDesigns { get; }
        internal int SharedShipNameDesigns { get; }
        internal int DuplicateFingerprintGroups { get; }
        internal int DuplicateFingerprintRows { get; }
        internal string ExamplesText { get; }
        internal bool HasSignals => ShipNameDesigns > 0 || SharedShipNameDesigns > 0 || DuplicateFingerprintGroups > 0;
    }

    internal sealed class FleetMix
    {
        internal int Count { get; set; }
        internal float Tons { get; set; }
        internal int BuildingCount { get; set; }
        internal float BuildingTons { get; set; }
    }

    internal sealed class TypeDiagnostics
    {
        internal TypeDiagnostics(string type)
        {
            Type = type;
        }

        internal string Type { get; }
        internal bool Candidate { get; set; }
        internal bool CountShareSatisfied { get; set; }
        internal bool HasAnyDesign { get; set; }
        internal bool HasBuildableDesign { get; set; }
        internal string NewestDesign { get; set; } = "none";
        internal string NewestBuildableDesign { get; set; } = "none";
        internal float DesiredShare { get; set; }
        internal int DiagnosticBuildable { get; set; }
        internal FailureCounts DiagnosticFailures { get; } = new();
        internal int VanillaValidationCalls { get; set; }
        internal int VanillaBuildable { get; set; }
        internal FailureCounts VanillaFailures { get; } = new();
    }

    internal sealed class ValidationSummary
    {
        internal List<Ship> BuildableDesigns { get; } = new();
        internal FailureCounts Failures { get; } = new();
        internal string FailureText => Failures.SummaryText();
    }

    internal readonly record struct DesignBuildCheck(bool CanBuild, string Reason);

    internal sealed class FailureCounts
    {
        private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string reason)
        {
            reason = NormalizeReason(reason);
            counts[reason] = counts.TryGetValue(reason, out int current) ? current + 1 : 1;
        }

        internal void AddRange(FailureCounts other)
        {
            foreach (KeyValuePair<string, int> pair in other.counts)
                counts[pair.Key] = counts.TryGetValue(pair.Key, out int current) ? current + pair.Value : pair.Value;
        }

        internal string SummaryText()
            => counts.Count == 0
                ? "none"
                : string.Join(",", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
    }
}

[HarmonyPatch]
internal static class CampaignAiBuildNewShipsDiagnosticsPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "BuildNewShips", new[] { typeof(Player), typeof(float) })
           ?? throw new MissingMethodException(nameof(CampaignController), "BuildNewShips");

    [HarmonyPrefix]
    private static void Prefix(Player player, float tempPlayerCash, ref CampaignAiShipbuildingDiagnosticsPatch.BuildContext? __state)
        => __state = CampaignAiShipbuildingDiagnosticsPatch.BeginSurfaceBuild(player, tempPlayerCash);

    [HarmonyPostfix]
    private static void Postfix(Player player, CampaignAiShipbuildingDiagnosticsPatch.BuildContext? __state)
        => CampaignAiShipbuildingDiagnosticsPatch.EndSurfaceBuild(__state, player);
}

[HarmonyPatch]
internal static class CampaignAiBuildNewSubmarinesDiagnosticsPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(CampaignController), "BuildNewSubmarines", new[] { typeof(Player), typeof(float) })
           ?? throw new MissingMethodException(nameof(CampaignController), "BuildNewSubmarines");

    [HarmonyPrefix]
    private static void Prefix(Player player, float tempPlayerCash, ref CampaignAiShipbuildingDiagnosticsPatch.BuildContext? __state)
        => __state = CampaignAiShipbuildingDiagnosticsPatch.BeginSubmarineBuild(player, tempPlayerCash);

    [HarmonyPostfix]
    private static void Postfix(Player player, CampaignAiShipbuildingDiagnosticsPatch.BuildContext? __state)
        => CampaignAiShipbuildingDiagnosticsPatch.EndSubmarineBuild(__state, player);
}

[HarmonyPatch]
internal static class CampaignAiCanBuildShipsFromDesignDiagnosticsPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign), new[] { typeof(Ship), typeof(int), typeof(string).MakeByRefType() })
           ?? throw new MissingMethodException(nameof(PlayerController), nameof(PlayerController.CanBuildShipsFromDesign));

    [HarmonyPostfix]
    private static void Postfix(Ship design, int amount, ref string reason, bool __result)
        => CampaignAiShipbuildingDiagnosticsPatch.RecordCanBuildValidation(design, amount, reason, __result);
}

[HarmonyPatch]
internal static class CampaignAiBuildShipsFromDesignDiagnosticsPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(PlayerController), nameof(PlayerController.BuildShipsFromDesign), new[] { typeof(Ship), typeof(int), typeof(bool), typeof(Player) })
           ?? throw new MissingMethodException(nameof(PlayerController), nameof(PlayerController.BuildShipsFromDesign));

    [HarmonyPrefix]
    private static void Prefix(
        Ship design,
        int amount,
        bool force,
        Player overridePlayer,
        ref CampaignAiShipbuildingDiagnosticsPatch.BuildAttemptState? __state)
    {
        __state = CampaignAiShipbuildingDiagnosticsPatch.BeginBuildShipsFromDesignCall(design, amount, force, overridePlayer);
    }

    [HarmonyPostfix]
    private static void Postfix(
        Ship design,
        int amount,
        bool force,
        Player overridePlayer,
        CampaignAiShipbuildingDiagnosticsPatch.BuildAttemptState? __state)
    {
        CampaignAiShipbuildingDiagnosticsPatch.EndBuildShipsFromDesignCall(__state, design, amount, force, overridePlayer);
    }
}

[HarmonyPatch]
internal static class CampaignAiShipbuildingSummaryNextTurnCompletionPatch
{
    private static bool Prepare()
    {
        bool available = TargetMethod() != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP ai-build: NextTurn MoveNext target not found; summary flush will be skipped.");
        return available;
    }

    private static MethodBase? TargetMethod()
        => CampaignSharedDesignDiagnosticsPatch.NextTurnMoveNextTarget();

    [HarmonyPostfix]
    private static void Postfix(bool __result)
    {
        if (!__result)
            CampaignAiShipbuildingDiagnosticsPatch.FlushPendingAiBuildTurnSummariesAfterNextTurn();
    }
}
