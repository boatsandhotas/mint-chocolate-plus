# Smart AI Design Generation Reference

## Status

Implemented and proven in campaign samples. The original plan remains below as
the architecture reference, but the live implementation now uses a strict
VP-owned Smart AI pipeline with up to three same-candidate inner attempts,
Smart Parts-only placement, part-dependent post-placement defaults, armor fill,
strict validation, and compact turn/nation summaries.

Recent validation evidence before the logging cleanup:

- Pre-restart long sample: 31 Smart AI designs, 31 accepted, 0 final rejects, 1
  useful internal retry. Italian TB `Aldebaran` missed required torpedoes on
  attempt 1 and accepted on attempt 2.
- Post-restart sample: 30 Smart AI designs, 30 accepted, 0 retries, 0 rejects,
  0 accepted overweight designs.
- Later fresh truncated sample: 7 Smart AI designs, 7 accepted, 0 retries, 0
  rejects, 0 accepted overweight designs.
- Old bad signatures stayed gone: no vanilla tail states `11/12/13/14`, no
  `callback=true:true`, no accepted overweight designs, and no final
  smart-design discarded/rejected summaries.
- Known benign behavior: TBs have no armor profile room, so armor summaries
  with `spent=0`, `profileB=0`, or `no-profile-room` can be expected for them.

## Diagnostics Cleanup / Re-enable Recipe

Normal builds intentionally keep Smart AI design logs compact. To re-enable the
heavy temporary diagnostics for a future investigation:

- `UADVanillaPlus/Services/SmartAiDesignService.cs`
  - Change `VerboseDiagnostics => true`.
  - `Finish(...)` is where compact retry/reject/detail logging is emitted.
  - `RunVanillaRandomGeneratorForSmartAi(...)` gates the
    `after-defaultsPost-before-armor` snapshot and passes
    `traceWeight: VerboseDiagnostics` into
    `DesignNewHullDefaultsPatch.ApplySmartAiPartDependentDefaults(...)`.
  - `RunSmartAiPartsAtGeneratorState(...)` passes `VerboseDiagnostics` into
    `DesignAutoDesignLitePatch.TryRunPartsOnlyForAi(...)`.
  - `RecordUnmatched(...)` and `RecordFallbackClaim(...)` are the generator
    state-machine matching diagnostics.
- `UADVanillaPlus/Harmony/DesignNewHullDefaultsPatch.cs`
  - `ApplySmartAiPartDependentDefaults(...)` is the post-parts defaults seam.
    With tracing enabled it logs component-by-component weight breadcrumbs for
    armament, equipment, and torpedo defaults only.
- `UADVanillaPlus/Harmony/DesignAutoDesignLitePatch.cs`
  - `LogDiagnosticSnapshot(...)` owns the heavy component/part/gun-row
    snapshots used by both UI Smart Place Parts and AI parts-only placement.
- `UADVanillaPlus/Harmony/CampaignAiDesignGenerationDiagnosticsPatch.cs`
  - Change `VerboseDiagnostics => true` to restore the broad `[AI DesignGen]`
    start/result/gate diagnostics. Normal mode logs only created designs and
    unusual random failures/erases.
- `UADVanillaPlus/Harmony/CampaignSmartAiDesignPatch.cs`
  - `CampaignSmartAiDesignSummaryNextTurnCompletionPatch` flushes
    `SmartAiDesignService` summaries when the `NextTurn` state machine
    completes.

## Normal Logging

Normal builds emit one compact Smart AI design line per campaign turn:

- `UADVP smart AI designs summary turn=... entries=N accepted=N rejected=N retries=N totalMs=N designs=...`
- Each design entry includes nation, type, design name, hull display/id,
  accepted or rejected attempt count, weight/capacity, elapsed milliseconds, and
  retry first-failure text when applicable.
- If Smart AI Designs is enabled and no Smart AI design attempt happened that
  turn, the turn-end hook emits `UADVP smart AI designs summary turn=... entries=0`.
- Heavy per-attempt payloads, generator state-machine details, component weight
  traces, and `DesignAutoDesignLitePatch.LogDiagnosticSnapshot(... "smart-ai" ...)`
  output are gated by `VerboseDiagnostics => false`.
- Known benign behavior remains: TBs have no armor profile room, so armor
  summaries with `spent=0`, `profileB=0`, or `no-profile-room` can be expected
  when verbose diagnostics are re-enabled.

Useful bad-pattern log searches:

- `result=rejected`
- `summary.*rejected=[1-9]`
- `attemptsUsed=[2-9]`
- `acceptedAttempt=[2-9]`
- `stoppedAfterSmartParts=false`
- `states=.*11/12/13/14`
- `callback=true:true`

This is the proposed direction for a future VP feature that replaces the most expensive and least controlled part of vanilla AI random ship design while keeping vanilla campaign selection, shared-design checks, and build validation as the authority.

## Goal

When an AI nation needs a fresh generated surface-ship design after shared/predefined design checks have failed or been skipped, VP should make one deterministic smart-design attempt instead of letting vanilla run a broad random retry loop.

The desired behavior:

1. Let the existing campaign flow decide that an AI design is needed.
2. Let vanilla/shared-design logic run first.
3. If the AI reaches the new-design fallback, let vanilla choose the ship type and hull.
4. Apply VP's practical new-hull defaults/components.
5. Run parts placement only.
6. Validate the resulting design with strict vanilla validation.
7. Apply armor fill logic.
8. Validate again with strict vanilla validation.
9. Compare the candidate against the AI nation's existing same-type top designs.
10. Save/keep the design only if it passes all gates.
11. Do only one attempt.
12. Summarize all AI smart-design attempts at the end of the turn, including success or the rejection reason.

## Configuration

This should be player-configurable.

Recommended first shape:

- Add a campaign option named `Smart AI Designs` or similar.
- Gate it behind the existing `Advanced AI Builder` family conceptually, but keep it as a separate setting because it is more invasive than shared-design adaptation.
- Initial development default should probably be off or experimental until it has several clean next-turn logs.
- Once proven stable, it can be considered for default-on `Enhanced` behavior.
- `Vanilla`/off mode must leave the current vanilla random design path unchanged.

This option is separate from `Smart Refits`. Smart AI Designs creates new AI design rows. Smart Refits modifies refit candidates or existing ships/designs.

Likely files for a builder pass:

- `UADVanillaPlus/GameData/ModSettings.cs`
- `UADVanillaPlus/Harmony/InGameOptionsMenuPatch.cs`
- New AI design service or patch file under `UADVanillaPlus/Harmony/` or `UADVanillaPlus/Services/`
- Existing helpers from `DesignNewHullDefaultsPatch`, `DesignAutoDesignLitePatch`, `DesignAutoArmorPatch`, `CampaignAiDesignGenerationDiagnosticsPatch`, `CampaignAiDesignRosterPrunePatch`, and `ShipEffectivePowerCalculator`

## Candidate Pipeline

The feature should run only for AI major/minor campaign design generation, not for the human designer.

Proposed first-pass flow:

1. Enter only after shared/predefined design checks are complete and the AI is about to create a fresh random design.
2. Use the current vanilla-selected `Player`, `ShipType`, and hull-backed temporary `Ship`.
3. Apply deterministic ship setup:
   - max legal tonnage for the hull/player/ship type,
   - optimal hull speed, clamped to legal type speed,
   - high range,
   - reasonable crew/quarters defaults if applicable to design rows,
   - best available propulsion/protection components.
4. Run parts-only placement through the `Ship.AddRandomPartsNew(...)` seam used by Auto Design Lite.
5. Recalculate weight/cost.
6. Run strict vanilla validation.
7. If valid, apply armor fill.
8. Recalculate weight/cost again.
9. Run strict vanilla validation again.
10. Run same-type quality/top-3 gate.
11. Accept the candidate or erase/rollback it cleanly.

The key principle is not to call the UI button path. The AI path should reuse the core logic behind player UI helpers, not `G.ui`, constructor refreshes, tooltips, overlays, or main-player guards.

## Strict Vanilla Validation

Do not use Smart Refit's waiver-aware validation for this feature.

Smart Refit deliberately grandfathers some refit/inherited-part states and has special allowances for refit design edge cases. New AI designs should not inherit those allowances.

For the first implementation, the validation gate should be strict:

1. `ship.CalcWeightAndCost(true, true)` or the local equivalent.
2. `ship.IsValid(false)` must pass.
3. `ship.IsValidCostReqParts(out reason, out notPassed, out badParts)` must pass.
4. `ship.IsValidCostWeightBarbette(out reason, out errorBarbettePart)` must pass.
5. `PlayerController.CanBuildShipsFromDesign(ship, 1, out reason)` must pass.

Run this set:

- after parts placement and before armor fill,
- after armor fill and final recalculation,
- before keeping/saving the design row.

Reject on any vanilla failure. Log the normalized reason and stage.

## Top-3 Quality Gate

The first quality gate should be conservative and same-type only.

Suggested rule:

- Build the current AI nation's same-type design roster.
- Rank buildable same-type designs with the existing `CampaignAiDesignRosterPrunePatch`/`ShipEffectivePowerCalculator` scoring.
- If fewer than 3 buildable same-type designs exist, a valid new candidate can pass.
- If 3 or more buildable same-type designs exist, the candidate should pass only if it is not meaningfully worse than the current third-best design.

The exact tolerance is still open. A first pass could use one of these:

- candidate adjusted power must be at least 90 percent of the third-best same-type adjusted power,
- or candidate must rank inside the same-type top 3,
- or candidate may pass if it improves freshness while staying within a small power tolerance.

This gate should avoid resurrecting the old global `AI Arms Race` behavior. It is local to the AI nation's own same-type design book.

## Logging And Turn Summary

The feature needs compact logs because next-turn runs are expensive to reproduce.

Per attempt, log:

- turn,
- nation,
- type,
- hull id/name,
- whether shared-design fallback had already failed,
- component/default summary,
- parts placement result,
- validation stage and reason,
- armor fill result,
- final validation result,
- adjusted power/top-3 comparison,
- accepted/rejected result,
- elapsed milliseconds.

At the end of the turn, summarize all Smart AI Design attempts:

- total attempts,
- accepted count,
- rejected count,
- rejection reasons grouped by stage,
- examples by nation/type/design.

Current implementation flushes Smart AI summaries from the `NextTurn` state-machine completion hook, matching the AI build and shared-design gap summary cadence.

## Risks And Guardrails

- Avoid broad direct hooks on `Ship.GenerateRandomShip(...)` delegate/callback signatures. Prior designer work showed this boundary can be unstable.
- Prefer the existing coroutine/state-machine seams and the private `Ship.AddRandomPartsNew(...)` reflection wrapper shape already proven by Auto Design Lite.
- Do not touch human designer behavior.
- Do not let this option affect shared-design import validation except through existing Advanced AI Builder behavior.
- Do not accept designs through Smart Refit refit waivers.
- Keep the attempt count to one for the first version.
- If the smart attempt fails, fall back behavior should be decided explicitly:
  - safest initial test mode: reject and allow vanilla random generation to proceed only when the option is off,
  - stricter replacement mode: reject and do not create a design that turn,
  - diagnostic mode may log and then let vanilla proceed, but that would not be a true replacement.

## Open Questions

- Exact hook point: whether to replace the random fallback before `Ship.GenerateRandomShip(...)` starts, or intercept the state machine once the temporary ship/hull exists.
- Ownership/commit path: whether accepted designs should rely on vanilla's existing temporary ship becoming a campaign design row or require explicit normalization.
- Fallback policy after smart failure: skip design for that turn or let vanilla random generation continue.
- Default setting: off/experimental at first versus on as part of Advanced AI Builder Enhanced.
- Whether max range/spacious quarters should be used for AI new designs exactly as in the human hull defaults, or tuned separately for AI economy.
- Whether quality tolerance should compare against top 3 buildable same-type designs, all same-type designs, or buildable non-refit designs only.
