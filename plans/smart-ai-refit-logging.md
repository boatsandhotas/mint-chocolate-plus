# Smart AI Refit Logging Reference

## Normal Logging

Normal builds keep AI Smart Refit output to one compact summary line per
campaign turn:

- `UADVP smart refits ai summary turn=... entries=N started=N continued=N rejected=N skipped=N protected=N deleteGuards=N skipReasons=... totalMs=N maxMs=N details=...`
- If Smart Refits are enabled and no AI Smart Refit event was recorded during
  the turn, the turn-end hook emits `UADVP smart refits ai summary turn=... entries=0`.
- Details are capped at eight entries, then summarized with `+N more`.

The summary counters mean:

- `started`: a new AI Smart Refit design was accepted and campaign refits were
  started from it.
- `continued`: an existing AI Smart Refit design started more campaign refits.
- `rejected`: a candidate failed creation, service validation, buildability, or
  refit-start checks.
- `skipped`: candidate/refit work was skipped for normal cadence, budget,
  continuation coverage, already-assigned refits, or similar reasons.
- `protected`: a referenced live refit design was intentionally preserved while
  constructor visuals were cleaned up.
- `deleteGuards`: VP blocked deletion of a live-referenced AI refit design.

`referenced-live-refit` protection is expected. It means a campaign ship still
points at that refit design, so VP must not delete it.

## Verbose Diagnostics

To restore the old detailed AI Smart Refit breadcrumbs for a focused
investigation, change:

- `UADVanillaPlus/Harmony/CampaignSmartRefitPatch.cs`
  - `VerboseAiRefitDiagnostics => true`
  - Re-enables `UADVP smart refits ai-debug` state dumps, candidate-skip,
    probe, cleanup, protected-cleanup, delete-guard, and per-event AI logs.
- `UADVanillaPlus/Services/SmartRefitService.cs`
  - `VerboseAiRefitDiagnostics => true`
  - Re-enables service-validation waiver logs such as
    `buildability-obsolete-allowed` and `buildability-waiver-allowed`.

Keep the verbose switches off for normal releases. The compact turn summary is
intended to answer whether AI Smart Refit work happened, whether it succeeded,
why it skipped or rejected, and whether protected/delete-guard activity is
normal without flooding `Latest.log`.
