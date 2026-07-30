# Vanilla Campaign Task-Force AI

Last updated: 2026-06-07

## Scope

This is a working reference for how Ultimate Admiral: Dreadnoughts appears to manage campaign task forces for AI nations.

The goal is to keep future VP investigation grounded in vanilla code paths before proposing changes. This note is intentionally separate from implementation planning. Update it as we decode more of the IL branches, inspect live logs, or prove assumptions wrong.

## Short Take

AI task-force behavior appears to be a rule-based campaign allocation system, not a deep operational admiral AI.

The broad model is:

1. Maintain the AI navy.
2. Assign ship roles.
3. Calculate where areas are under-covered by tonnage or power.
4. Move available ships and submarines toward those area needs.
5. Tick task-force movement, supply, fuel, mines, repair, and battle state.
6. Let mission generation convert nearby/enemy task-force contact into battles.

Important distinction: `AiManageFleet(...)` is mostly fleet maintenance and construction setup. The area/task-force movement pressure lives later in `MoveVessels(...)`, `MovePlayerVessels(...)`, `NeededTonnage(...)`, and related area-tonnage methods.

## Evidence Sources

Primary vanilla source references:

- `E:\Codex\cpp2il_uad_diffable\DiffableCs\Assembly-CSharp\CampaignController.cs`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType__AiManageFleet_d__201.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType__MoveVessels_d__203.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType__ProcessTaskForces_d__173.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType_Data.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType___c.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType___c__DisplayClass210_0.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController_NestedType_TaskForce.txt`
- `E:\Codex\cpp2il_uad_diffable\DiffableCs\Assembly-CSharp\CampaignShipsMovementManager.cs`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignShipsMovementManager.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\MissionGenerator.txt`
- `E:\Codex\cpp2il_uad_diffable\DiffableCs\Assembly-CSharp\MissionGenerator.cs`
- `E:\Codex\cpp2il_uad_diffable\DiffableCs\Assembly-CSharp\VesselEntity.cs`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\BattleManager.txt`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\MapUI.txt`

Related VP reference docs:

- `plans/vanilla-ai-shipbuilding-designs.md`
- `plans/vanilla-campaign-icon-painting.md`
- `plans/vanilla-battle-flow.md`
- `plans/reddit-user-suggestions.md`

Decompiler caveat: the diffable C# files preserve class shape, field names, method names, and signatures, but most method bodies are empty. The ISIL dumps preserve call flow and field offsets, but some calls are native addresses or virtual dispatch. Treat this as good structural evidence, not yet a perfect readable C# reconstruction.

## Main Objects

### `CampaignController.TaskForce`

`TaskForce` is the campaign-level moving group. It contains:

- `Vessels`
- `Id`
- `Controller`
- `From`, `To`, `OriginPort`
- `FromPosition`, `ToPosition`, `WorldPos`
- `Path`, `FullPath`, `CheckFullPath`
- `CurrentPositionIndex`, `NextPositionUsed`
- `DestinationNearestPort`, `NearestPort`, `NearestFriendlyPort`
- `HaveSupply`, `SqrDistanceToNearestFriendlyPort`
- `GroupMovingToBattle`, `GroupMovingFromBattle`, `BattleId`, `PendingBattle`
- `RoleIndex`, `LastTurnRoleSelected`
- `Repair`
- `EnemiesOnPath`
- `PathInvalid`, `RebuildRoute`, `CheckForRecalculate`
- `GroupCreateTurn`, `DamagedWithMinesTurn`, `WasMove`
- `NavmeshObstacles`

Useful methods include:

- `BattleTonnage()`
- `GetVesselType()`
- `GetZoneRadius(...)`
- `AverageRange()`
- `CheckShips()`
- `CheckSupplyPortDistance()`
- `RecalculatePath()`
- `Move()`
- `MoveNextDestionation(...)`
- `FuelConsumption(...)`
- `CheckMinefieldOnPath()`
- `GetTaskForceMinefieldRadius()`
- `GetMinesweepingCapacity()`

### `CampaignController.Data`

The campaign data store owns task-force collections:

- `TaskForces`
- `TaskForceById`
- `TaskForceByPlayer`

It also exposes helpers for adding, removing, finding, and merging task forces:

- `AddTaskForce(...)`
- `RemoveTaskForce(...)`
- `GetPlayerTaskForces(...)`
- `GetPlayersTaskForces(...)`
- `GetAllianceTaskForces(...)`
- `GetTaskForceInsideRadius(...)`
- `MergeShipGroups(...)`
- `RemoveVesselFromTaskForce(...)`

## Turn-Level Flow

### 1. AI fleet maintenance

`CampaignController.AiManageFleet(Player player, bool prewarming = false)` performs the high-level AI fleet upkeep path.

The inspected coroutine calls:

1. `ScrapOldAiShips(player)`
2. if not prewarming:
   - `SetAiShipRoles(player)`
   - `DeleteOldDesigns(player)`
3. design maintenance:
   - `GenerateRandomDesigns(player, prewarming)`, or
   - `GetPredefinedDesign(player, prewarming)`
4. `player.CashAndIncome()`
5. `BuildNewShips(player, tempPlayerCash)`
6. `BuildNewSubmarines(player, tempPlayerCash)`

This path connects task-force behavior to ship roles and fleet composition, but it is not itself the final movement planner.

### 2. AI vessel movement

`CampaignController.MoveVessels(Player player)` wraps movement refresh work:

1. `CampaignMap.UpdateTaskForcePositions()`
2. `MovePlayerVessels(player, VesselType.Ship)`
3. `MovePlayerVessels(player, VesselType.Submarine)`
4. `CampaignMap.ClearTaskForcePositions()`

`VesselEntity.VesselType` is only:

- `Ship = 0`
- `Submarine = 1`

So surface and submarine allocation are handled as parallel passes through the same movement selector.

### 3. Area need scoring

The movement selector relies on area pressure methods:

- `NeededTonnage(Area area, Player player, bool forShips = true)`
- `AreaCurrentTonnage(Area area, Player player)`
- `AreaExpectedTonnage(Area area, Player player)`
- `AreaRequiredTonnage(Area area, Player player)`
- `AreaRequiredTonnageForSubmarines(Area area, Player player)`
- `PowerProjectionInAreaForPlayer(...)`
- `PowerProjectionInAreaForPlayerTension(...)`
- `RaidPowerAvgInAreaForPlayer(...)`
- `RaidPowerAvgSubInAreaForPlayer(...)`
- `EscortPowerAvgInAreaForPlayer(...)`

Current understanding:

- `AreaCurrentTonnage(...)` sums currently present vessels in an area.
- `AreaExpectedTonnage(...)` looks at ships already assigned toward an area.
- `AreaRequiredTonnage(...)` and `AreaRequiredTonnageForSubmarines(...)` estimate area demand from provinces, player/enemy/alliance context, and existing vessel pressure.
- `NeededTonnage(...)` compares actual/expected coverage against required coverage.

The inspected shipbuilding note already found that `NeededTonnage(...)` does not directly drive ship construction. It appears to feed later campaign movement/task-force allocation instead.

### 4. Task-force movement mechanics

`CampaignShipsMovementManager` is the movement execution layer:

- `RequestPath(...)`
- `DistancePerTurn(...)`
- `MoveVessels(...)`
- `MoveTaskForce(...)`
- `MoveGroup(...)`

`TaskForce.RecalculatePath()` calls path request/simplification logic and updates path arrays. `TaskForce.Move()` advances the group and calls minefield and fuel-consumption logic during movement.

This suggests that AI choice and movement execution are separated:

- `MovePlayerVessels(...)` decides which vessels or groups should move.
- `CampaignShipsMovementManager` handles pathing and group creation/update.
- `TaskForce` handles ongoing path state and per-turn consequences.

### 5. Task-force processing

`CampaignController.ProcessTaskForces()` is the existing-task-force maintenance pass.

From visible calls and task-force methods, it appears to handle:

- invalid/sunk/scrapped vessel cleanup through `TaskForce.CheckShips()`
- route/path progression and recalculation
- arrival/port state
- supply and nearest-port checks
- minefield interactions
- fuel/ammo/repair consequences
- battle movement flags and pending battle bookkeeping
- task-force removal/merge/update cases

This method is large and not fully decoded yet. Treat this section as a structural map, not a branch-complete rewrite.

### 5a. Automatic disbanding/removal

Yes, vanilla task forces can be removed/disbanded. This appears to be shared campaign lifecycle logic rather than a separate AI-only strategic choice.

Known removal paths:

- `Data.RemoveTaskForce(TaskForce group, bool returnFromSea = false)` removes the group from `Data.TaskForces` and `TaskForceByPlayer`, optionally returns the vessels from sea, and calls `MapUI.OnShipsGroupRemoved(group)`.
- `Data.RemoveVesselFromTaskForce(VesselEntity vessel)` removes one vessel from its current group; if the group becomes empty, it calls `RemoveTaskForce(...)`.
- `ProcessTaskForces()` calls `Data.RemoveTaskForce(...)` during task-force lifecycle handling. The exact branch still needs a readable reconstruction, but it appears connected to port/arrival/finished-state cleanup.
- `Data.MergeShipGroups(...)` removes the source group after transferring its vessels into the destination group.
- `BattleManager.RemoveTaskForceGroup(...)` removes task-force groups associated with battle cleanup.
- `MapUI.RefreshMovingGroups()` has a defensive cleanup path for invalid groups or groups whose vessels are sunk/scrapped/invalid.
- `CheckTaskForcceGroups(...)` can remove task forces when a player/nation is disabled, using `returnFromSea = true`.

Practical implication: if VP adds persistent or player-managed task-force composition, it must either cooperate with these shared removal paths or deliberately guard specific groups from vanilla cleanup.

### 5b. Automatic merging

Yes, vanilla task forces can combine. The implementation is `Data.MergeShipGroups(TaskForce mergeTo, TaskForce group)`.

Observed merge behavior:

- The source group's vessels are transferred into the destination group's `Vessels` list.
- Each transferred vessel has its stored task-force/group id and location state updated to match the destination group.
- The old/source task force is removed with `Data.RemoveTaskForce(group, false)`.

`ProcessTaskForces()` appears to be the automatic merge caller. Current decoded conditions:

- groups must belong to the same controller/nation
- groups must have the same vessel type; surface ships merge with surface ships, submarines with submarines
- groups must be close enough on the campaign map, with a type-specific distance threshold
- surface-ship groups must stay under the controller's `ship_crew_group_limit`, exposed through `Player.GetShipsMaxCrewForTaskForce()`
- submarine groups must stay under the controller's `submarine_group_limit`, exposed through `Player.GetSubsMaxGroupSize()`

If nearby same-owner groups are too large to merge, `ProcessTaskForces()` appears to push or offset one group to a nearby sampled map position instead of merging it.

Practical implication: "combine task forces" exists, but it is an automatic proximity/capacity cleanup behavior, not evidence of an AI plan that intentionally gathers named fleets for a strategic objective.

### 6. Mission generation

`MissionGenerator` turns task-force presence into battles.

Relevant methods include:

- `TryGenerateTaskForceBattle(Player a, Player b)`
- `TryMoveShipGroupToAnotherPosition(TaskForce group)`
- `CanUseThisArea(...)`
- `FilterShipsForBattle(...)`
- `FindValidPortForSubsAttackTransport(...)`
- `AddSubmarineBattle(...)`

The task-force battle path checks or uses:

- task-force zone radius
- task-force vessel type
- movement destination/next destination
- recon eligibility
- ship HP/ammo/fleet-type/distance filters
- actual/reinforcement ship selection
- battle reservation and battle creation

Practical implication: movement creates campaign contact opportunities, but `MissionGenerator` is where contact becomes an actual battle object.

## Current Working Model

The current best model is:

1. AI fleet maintenance ensures ships, designs, and broad roles exist.
2. Area scoring computes where the nation wants more surface or submarine coverage.
3. `MovePlayerVessels(...)` selects available vessels or existing groups to satisfy those area deficits.
4. `CampaignShipsMovementManager` creates/moves the task-force group through the map.
5. `TaskForce.Move()` and `ProcessTaskForces()` maintain the group as it travels.
6. `MissionGenerator` checks task-force overlap/proximity and emits battles if battle filters pass.

This is probably why AI task-force behavior can feel semi-random even when it is not purely random. The AI is likely satisfying area-pressure math and letting battle generation fall out of position/proximity, rather than pursuing a named enemy group with a persistent intercept plan.

## Piecemeal AI Dispatch Finding

Observed player-facing symptom:

- An AI country at war with the player can send several small task forces toward the player's region instead of one consolidated combat group.
- Those small groups can then be intercepted and defeated one by one.

Likely vanilla reason:

- `MovePlayerVessels(...)` is trying to satisfy area need, not build a coherent theater fleet.
- If it cannot simply redirect an existing suitable group, it falls back to available in-port vessels.
- The fallback filters vessels by type, in-port status, normal/usable state, and port presence.
- It groups candidate vessels by `PortElement`.
- It then selects batches from those port groups and sends each batch to a random point inside the needed area with `CampaignShipsMovementManager.MoveVessels(...)`.
- Surface-ship batches are capped by `SelectBestVesselsByMaxCrew(...)` using `Player.GetShipsMaxCrewForTaskForce()`.
- Submarine batches are capped by `Player.GetSubsMaxGroupSize()` and a submarine movement-distance check.
- Automatic merging only happens later if same-controller same-type groups happen to get close enough and remain under the cap.

This explains the France-vs-US style failure mode: France may be correctly identifying a US-region area need, but dispatching eligible ships from multiple French ports as separate batches. Different origins, random destination points, and staggered arrival mean the groups may never merge before battle generation exposes them as separate small fights.

### Fix Direction

The fix should improve AI consolidation without creating unlimited doom-stacks.

Recommended first approach:

- Add an AI-only consolidation pass for task forces headed to the same or nearby destination area.
- Respect vanilla caps:
  - surface ships: combined crew <= `ship_crew_group_limit`
  - submarines: combined count <= `submarine_group_limit`
- Only merge safe candidates:
  - same controller/nation
  - same vessel type
  - neither group pending/in battle/moving from battle
  - not repairing/returning because of battle damage
  - close enough now, or clearly converging on the same destination/area
- Prefer using existing `Data.MergeShipGroups(...)` when groups are already close enough.
- For groups not physically close, avoid teleport-merging; a real rendezvous/staging route is safer but larger.

Potential implementation tiers:

1. Destination-aware nearby merge: widen or supplement vanilla's automatic merge for AI groups in the same theater before battle generation. This is the least invasive and most likely to reduce the "tiny fights" symptom.
2. Stateless outbound rendezvous/staging: each campaign turn, re-detect AI groups headed toward the same theater, pick a temporary staging group/point, redirect compatible stragglers toward it, and merge when they physically meet. This avoids custom save persistence if every decision can be recomputed from current task-force state and vanilla routes.
3. Full `MovePlayerVessels(...)` replacement: build consolidated batches before dispatch. This is the cleanest behavior model but highest-risk because the vanilla method is large and handles many campaign edge cases.

Current recommendation: try tier 1 first, with compact logging that reports controller, type, destination/theater, source group count, merged group count, and cap reason when a candidate is skipped.

### Stateless Staging Variant

The best "proper" behavior is probably a rendezvous system, but it does not necessarily need a custom save-file contract.

Possible model:

1. Hook once per campaign turn after vanilla AI movement has created/updated task forces, but before task-force battle generation if possible.
2. For each AI player at war, group active task forces by:
   - controller/nation
   - vessel type
   - destination theater or approximate destination area
3. For each cluster, choose a lead group:
   - largest battle tonnage or highest combined crew
   - already closest to the target theater
   - not pending/in battle, not moving from battle, not repairing
4. For compatible straggler groups, choose one of two behaviors:
   - if already close enough, merge now with `Data.MergeShipGroups(...)`
   - if not close, redirect the straggler toward the lead group's current position, lead destination, or a safe staging point near the origin side
5. When groups naturally become close enough on a later turn, merge them under the normal surface/sub caps.
6. If the situation changes after reload, battle, or route disruption, simply recompute the cluster next turn.

This works without custom persistence if the only lasting state is vanilla task-force movement state. VP does not need to remember "this group is part of convoy plan X"; it can infer that again from current controller/type/destination/war context.

Open design choices:

- Theater key: use destination area if it is recoverable; otherwise derive one from `To`, `ToPosition`, path endpoint, or nearest province/area.
- Staging point: use the lead group, a friendly port near the theater route, or a map point between source ports and destination.
- Lead behavior: if the lead keeps moving normally, other groups may never catch it. A stop-and-replan anchor may be better than a hidden movement pause: stop one selected group near the rendezvous point, let stragglers converge, and let vanilla AI movement reconsider the situation next turn.
- Patience: avoid repeatedly redirecting groups every turn; a lightweight cooldown may be useful, but that either needs a transient runtime dictionary or a derivable rule such as "only adjust groups created this turn or groups still far from target."
- Minimum value: only stage if the cluster has enough combined tonnage/crew to matter, to avoid wasting time coordinating tiny detachments.
- Battle safety: do not stage groups that are already near hostile contact, pending battle, returning from battle, or low fuel/supply.

Risk: if the hook runs after mission generation, staging will not prevent the current turn's small battles. If it runs too early, vanilla movement may overwrite VP's route. Turn-order decoding is the next required proof before choosing the hook.

### Stop-And-Replan Anchor Refinement

A likely better staging algorithm is:

1. Detect AI task-force clusters that are trying to reach the same theater.
2. Pick an anchor group. Good candidates are:
   - largest or most valuable group
   - slowest group, so others can actually catch it
   - not too close to hostile contact or battle generation range
   - not pending/in battle, returning, repairing, or low fuel/supply
3. Stop the anchor by routing it to its current sea position if vanilla/pathing accepts that as a valid at-sea destination.
4. Reroute compatible stragglers toward the anchor/staging point.
5. When stragglers catch the anchor, merge into the anchor with `Data.MergeShipGroups(...)`.
6. On the next AI movement pass, let vanilla re-evaluate the merged group's best destination from current campaign state.

This may be more vanilla-friendly than a hidden movement-skip hold. It intentionally does not preserve the original destination. Instead, it lets the AI pause long enough to consolidate, then lets normal area-pressure movement decide whether the merged force should continue toward the same theater, move somewhere else, or stop because the situation changed.

This also preserves the stateless/no-save-file design. VP does not need to remember the original destination because it is deliberately discarded. The only durable state is normal vanilla task-force routing.

Implementation note: prefer the cleanest possible stop, likely `Move.Position(anchor.CurrentPosition())`. Task forces do not normally vanish just because they arrive at a map destination, so this should probably be fine. If pathfinding or route lifecycle code refuses a zero-distance route, use a tiny nearby sampled legal point instead. The fallback point should be close enough to behave like a stop while still giving vanilla a normal route to process.

Alternative: a transient movement-skip hold set could preserve the original route, but it would be more of a hidden VP order and may interact awkwardly with fuel, mine, supply, and arrival bookkeeping.

### Working Plan For A Builder Pass

No code has been written yet. The current preferred implementation plan is:

1. Add an AI-only campaign task-force staging coordinator.
2. Run it after vanilla has created/updated AI task-force movement for the turn, and before task-force battle generation if the turn order allows.
3. Cluster active AI task forces by controller, vessel type, and destination theater.
4. Only process clusters with at least two compatible groups and enough combined strength to matter.
5. Pick an anchor group, probably favoring slowest/largest/safest rather than simply closest to the final target.
6. Stop the anchor at its current position.
7. Redirect compatible stragglers toward the anchor.
8. Merge groups that become close enough, preserving vanilla surface/sub caps.
9. Recompute everything next turn instead of storing custom operation state.

Expected player-facing effect: AI nations should be less likely to feed the player tiny sequential task-force battles when several groups are clearly being sent to the same region.

Main validation questions:

- Does an exact current-position stop produce a stable at-sea task force route?
- If not, does a tiny nearby legal staging point behave cleanly?
- Does the chosen hook run early enough to prevent small battles on the same turn?
- Does vanilla movement naturally reassign the stopped/merged group on the next turn?

## Confidence

High confidence:

- `TaskForce` is the campaign moving-group object and stores path, port, supply, battle, repair, and vessel state.
- AI fleet maintenance calls `SetAiShipRoles(...)` before movement-related logic.
- `MoveVessels(...)` runs separate movement passes for surface ships and submarines.
- `NeededTonnage(...)` and the area-tonnage methods are part of the movement/task-force pressure model.
- `CampaignShipsMovementManager` owns path requests and task-force movement execution.
- `MissionGenerator.TryGenerateTaskForceBattle(...)` owns task-force battle creation.
- Vanilla automatically merges nearby same-controller same-type task forces when combined group size remains under the relevant tech cap.
- Vanilla removes task forces through shared lifecycle cleanup, empty-group cleanup, merge cleanup, battle cleanup, and invalid-group cleanup.
- `MovePlayerVessels(...)` can create separate task-force batches from separate source ports and send them toward random points inside an area.
- The piecemeal AI attack symptom is plausibly caused by area-need dispatch plus late/opportunistic merge behavior.

Medium confidence:

- `MovePlayerVessels(...)` ranks areas by unmet tonnage/power need, then picks available vessels or groups to cover those areas.
- AI task forces are more area-allocation behavior than target-specific pursuit behavior.
- `ProcessTaskForces(...)` is the main pass for arrival, cleanup, supply, mine, repair, and pending battle state.
- Surface merge capacity is based on combined crew total, capped by `ship_crew_group_limit`.
- Submarine merge capacity is based on combined vessel count, capped by `submarine_group_limit`.
- A nearby/destination-aware AI consolidation pass can reduce small sequential battles without replacing the whole movement planner.
- A stateless staging pass may avoid custom save persistence by recomputing group clusters from current task-force state each campaign turn.
- Stop-and-replan anchoring may be more vanilla-friendly than movement-skip anchoring if routing to a local staging point is valid.
- A current-position stop is probably cleaner than a tiny staging move if vanilla accepts it.

Low confidence / still needs decoding:

- Exact area ranking formula inside `MovePlayerVessels(...)`.
- Exact vessel selection order when several ports or candidate ships can satisfy the same area.
- Exact role meanings assigned by `SetAiShipRoles(...)`.
- Exact distance constants for automatic surface/submarine task-force merging.
- Exact `ProcessTaskForces()` branch that removes groups during arrival/finished-state handling.
- Exact turn-order seam where consolidation should run before task-force battle generation.
- Whether destination area/theater can be recovered reliably from task-force state after vanilla movement redirects a group.
- Whether a task force can be routed to its exact current sea position as a clean "stop", or whether pathing requires a tiny nearby legal point.
- Whether a Harmony prefix on `TaskForce.Move()` is a safe enough fallback seam for one-turn AI hold anchors.
- Whether AI and player task forces diverge in any meaningful logic after group creation.
- How special events and naval invasions override normal area-pressure movement.

## Related VP Ideas And Risks

### Permanent or locked task forces

Before implementing persistent task-force composition, decode:

- `CampaignController.Data.MergeShipGroups(...)`
- `CampaignController.Data.RemoveVesselFromTaskForce(...)`
- `CampaignController.Data.RemoveTaskForce(...)`
- `MovePlayerVessels(...)`
- `ProcessTaskForces(...)`

Risk: locking composition may fight vanilla's automatic merge/split/cleanup logic, especially for AI nations.

### Intercept enemy task force

A targeted intercept command probably needs new behavior, because the visible vanilla model looks area-driven, not target-driven.

Likely seams:

- player-side UI command on a selected `TaskForce`
- stored target task-force id
- route refresh when the target moves
- guardrails when the target disappears, enters battle, returns to port, crosses blocked sea, or leaves visibility

Risk: battle generation may already move groups away or reserve ships, so an intercept order must coexist with `MissionGenerator`.

### Auto-return damage thresholds

Relevant seams:

- `TaskForce.Repair`
- `TaskForce.PendingBattle`
- `TaskForce.GroupMovingFromBattle`
- `TaskForce.NearestFriendlyPort`
- `TaskForce.CheckSupplyPortDistance()`
- `ProcessTaskForces()`

Risk: player and AI task forces may share the same post-battle cleanup and repair paths. Gate any player convenience carefully if AI behavior should remain vanilla.

## Next Investigation Checklist

- Decode `MovePlayerVessels(...)` enough to write readable pseudo-code.
- Decode `SetAiShipRoles(...)` enough to map `RoleIndex`/role choices to movement behavior.
- Decode exact automatic merge distance constants for surface and submarine groups.
- Decode the exact `ProcessTaskForces()` arrival/finished-state branch that calls `RemoveTaskForce(...)`.
- Decode turn order around `MoveVessels(...)`, `ProcessTaskForces()`, and `MissionGenerator.TryGenerateTaskForceBattle(...)` to pick the safest consolidation hook.
- Inspect whether `TaskForce.To`, `ToPosition`, destination area, or path endpoint gives the most stable "same theater" key.
- Inspect whether `CampaignShipsMovementManager.MoveGroup(...)` can safely redirect an AI task force toward a staging point without breaking supply, return, battle, or route state.
- Inspect whether `MoveGroup(anchor, Move.Position(anchor.CurrentPosition()), MoveSettings.Empty)` behaves as a clean at-sea stop.
- If exact current-position routing is rejected, test a tiny nearby legal staging point.
- Inspect whether `TaskForce.Move()` can be skipped for selected AI anchor groups for one turn without breaking `ProcessTaskForces()` assumptions as a fallback design.
- Inspect `ProcessTaskForces(...)` around arrival, battle flags, and repair/supply decisions.
- Compare the vanilla movement model against `UADRealismDIP` only after the vanilla baseline is clearer.
- Use live `Latest.log` instrumentation only after choosing a specific question; broad task-force logging could get noisy fast.

## Update Log

- 2026-06-07: Initial working summary created from high-level vanilla source/ISIL inspection.
- 2026-06-07: Added automatic task-force removal and merge findings from `RemoveTaskForce(...)`, `MergeShipGroups(...)`, and `ProcessTaskForces()`.
- 2026-06-07: Added piecemeal AI dispatch finding and recommended AI-only consolidation direction.
- 2026-06-07: Added stateless staging/rendezvous variant to avoid custom save persistence.
- 2026-06-07: Revised anchor design toward stop-and-replan staging, using normal vanilla routing to pause the anchor and let AI movement adapt next turn.
- 2026-06-07: Added concrete no-persistence builder plan and made exact current-position stop the preferred anchor behavior, with tiny nearby staging as fallback.
