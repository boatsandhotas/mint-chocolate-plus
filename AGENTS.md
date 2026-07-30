# UAD Vanilla Plus Agent Notes

## Working Rules

- The default VP workflow is split between thinker sessions and one master builder session. Thinker sessions iterate on ideas, inspect vanilla/decompiled/log evidence, update plan/research docs when the user wants durable notes, and generate copy/paste handoff blobs. The master builder session owns source-code implementation, version bumps, builds, live DLL copies, commits, pushes, and releases.
- If a session has not been explicitly promoted to the master builder role, treat it as a thinker/reviewer session: do not modify source code, bump versions, build, copy DLLs, commit, push, or publish. Thinker sessions may update `plans/`, `AGENTS.md`, README notes, and other docs only when the user asks for durable documentation or repo guidance.
- Ambiguous implementation language is not builder promotion. In thinker/reviewer sessions, requests phrased as "can we modify/fix/add", "could we", "what would it take", or "can you look at" mean investigate, compare against vanilla/decompiled evidence, and return a recommendation or paste-ready builder handoff unless the user explicitly says this session is the builder session or directly asks to edit/build/copy here.
- Before continuing an existing multi-session investigation, check `plans/` for reusable research docs, plan docs, and any explicitly saved handoff notes. In particular, campaign map wrap-around follow-up work should start with `plans/campaign-map-wrap.md`.
- Use `plans/` as the repo home for useful cross-session docs: vanilla-flow maps, formula notes, investigation summaries, design plans, and other findings that future VP work should reuse. When an investigation produces reusable knowledge, add or update a focused doc there once the user wants it recorded.
- During ongoing thinker-session investigations, keep the relevant `plans/` reference doc current as ideas settle or logs disprove earlier assumptions, rather than waiting for the end of a long thread. Short incremental notes are preferred when they prevent context from being lost.
- When the user asks for a handoff note, handoff blob, builder-session summary, or says they want something they can copy/paste, provide it directly in the chat as one self-contained fenced Markdown block that can be copied as a single unit. Put the whole handoff inside that one outer block, including title, context, implementation direction, exact paths, relevant vanilla evidence, and verification steps. Use a `text` fence for the outer block. Never include nested triple-backtick fences inside the handoff; they break copy/paste. If the handoff needs code or command examples, format them as indented text inside the same outer block or keep them inline. Before sending, sanity-check that the handoff response has exactly one opening fence and one closing fence. Do not create or update a `plans/` file for copy/paste handoffs unless the user explicitly asks to record it as a file or durable repo note.
- Before investigating or changing battle generation, custom battle loading, saved battle payloads, or campaign battle execution, read `plans/vanilla-battle-flow.md` first for the vanilla flow and data-contract map.
- Before making UI layout changes, including adding buttons, toggles, text, labels, overlays, or popup/menu controls, read `plans/ui-layout-guide.md` first and follow its decision tree for choosing between native button cloning, existing-label text injection, child overlays, layout-group rows, or explicitly reserved floating panels.
- Before every build, bump the current patch version. Update `UADVanillaPlus/ModInfo.cs` `MelonVersion`, `UADVanillaPlus/Properties/AssemblyInfo.cs` assembly/file versions, and `README.md`'s current version together. Do not add a separate build-number suffix.
- Keep the in-game overlay version and MelonLoader metadata consistent through `ModInfo.DisplayText`.
- After a successful build, always try the built-DLL copy immediately. Copy directly to the game `Mods` folder without first checking whether the game is running; if the DLL is locked, let the copy fail and report that. Never close, kill, restart, or otherwise interrupt the running game, Steam, MelonLoader, Unity crash handler, or related processes to unlock the DLL unless the user explicitly asks you to do that after the lock is reported.
- When the user asks to merge, push, update GitHub, or otherwise publish completed work, commit locally, fast-forward/merge the work to `master`, and push `master` unless they explicitly limit it to local-only or ask for a different branch.
- The GitHub CLI may be installed outside `PATH`; use its full path (e.g. `<WORKSPACE>\tools\gh\...\gh.exe`) when `gh` is not on `PATH`.
- When creating or updating GitHub release notes, summarize the major player-facing highlights since the previous public release/tag, not only the final commit. Use the tag range, such as `previous_tag..new_tag`, and group the notes by user-facing area when helpful.
- Do not stop at a feature branch or PR branch for normal completed work. Use feature branches only as temporary work branches or when the user explicitly asks for a PR-style flow.
- Keep feature ports modular. Each QoL port or gameplay change should ideally live in its own source file under a clear folder, with only small shared helpers in `GameData` or similar common areas.
- Do not add loose config files for player-facing balance options. Balance-affecting features should be controlled through the in-game VP options menu, with shared option state living behind a typed helper in `GameData`.
- Keep QoL changes always enabled, while balance changes default to improved/on and can be toggled individually back to vanilla in-game.
- Port only the requested behavior from TAF/DIP. Avoid pulling unrelated config systems, UI rewrites, fleet tab changes, data edits, or gameplay logic as hidden dependencies.
- Prefer VP names for new UI objects and logs, such as `UADVP_...`, rather than carrying over `TAF_...` names.
- Update `README.md` when adding major player-facing features, installation changes, or source-build workflow changes. Keep README consumer-friendly: describe the main feature value, not every implementation detail or internal versioning rule.
- Order README feature bullets by user value/impact within each subsection, not by implementation chronology. Use judgment: frequently checked, high-friction, or high-consequence gameplay improvements should appear before smaller conveniences.
- In README feature lists, bold the feature name before the colon, such as `**Campaign maintenance indicators**: ...`.
- Never commit or push to `master` unless the user asks for a commit, push, merge, or GitHub update. Once they do, `master` is the default target in this repo.
- For development work, truth-seek against the available game disassembly before guessing how UAD works. The workspace has both skeleton/diffable and fuller IL views available at `<WORKSPACE>\cpp2il_uad_diffable` and `<WORKSPACE>\cpp2il_uad_isil`; inspect the relevant game classes/methods there when behavior or signatures are uncertain.
- Prefer narrow Harmony prefixes/postfixes/transpilers when they keep a VP feature simple, but keep full-method replacement in the toolbox. UAD is effectively a frozen/unmaintained game, so future game-update drift is not the main risk; the main risks are technical integration, Il2Cpp/runtime boundaries, performance, and over-copying proprietary decompiled code. If a vanilla method has a clean boundary and layered pre/post hooks would be brittle, overly invasive, or hard to reason about, it is acceptable to skip the original method and run a VP-owned implementation instead. Use decompiled sources as a behavioral reference, verify live Il2Cpp names/signatures before wiring the target, and avoid wholesale copying of large proprietary decompiled blocks into the repo.
- Before adding Harmony diagnostics to Il2Cpp methods with unusual signatures, verify the exact decompiled signature and likely marshaling behavior up front. Be especially careful around `ref`/`out` parameters, value-type structs, Il2Cpp collection fields, nested generic types, and UI hot paths; prefer safer surrounding hooks or read-only postfixes when the direct hook shape is uncertain.
- Be performance-conscious by default. One of VP's goals is to avoid TAF/DIP-style overhead, so watch for hot paths, broad polling, expensive UI rebuilds, repeated reflection, allocations in frequent hooks, and large data scans. Push back when a requested idea is likely to hurt performance, and prefer designs that cache, narrow scope, or hook less frequently.
- Every feature should include lightweight confirmation logging that makes it possible to verify from `Latest.log` that the feature is active and has applied its main effect. Keep these lines compact, clearly prefixed, and tied to meaningful events such as startup, option changes, first application, fallback, or failure.
- Be liberal with temporary logs and timings while developing or debugging. UAD behavior is often unclear from source alone and reruns are expensive, so optimize for enough upfront evidence to diagnose from `Latest.log`, then remove, condense, or gate noisy traces before leaving the feature in normal builds.
- When a player-visible rough edge is accepted as "good enough for now", document it in `README.md` under Known Issues so future sessions do not rediscover it from scratch.
- For campaign UI text patches, assume vanilla may rewrite labels through multiple paths after the obvious `CampaignCountryInfoUI.Refresh` call. Prefer native getter postfixes or final-pass repair hooks over one-shot text writes, and keep any watchdog narrow, cached, and visible-instance scoped. For campaign maintenance indicators specifically, render inside vanilla's existing multi-line `ShipbuildingCapacity` label and force the country-info layout rebuild; do not append a second line to `ShipyardSize` or position a floating TMP clone, because those approaches can push or overlap neighboring rows.

## Build

Use the workspace-local .NET home so builds do not write to user-profile locations:

```powershell
$env:DOTNET_CLI_HOME='<WORKSPACE>\.dotnet_home'
$env:NUGET_PACKAGES='<WORKSPACE>\.nuget\packages'
<WORKSPACE>\dotnet\dotnet.exe build <WORKSPACE>\UADVanillaPlus\UADVanillaPlus.sln -c Release /p:RestoreConfigFile=<WORKSPACE>\UADVanillaPlus\NuGet.Config
```

Copy the built DLL directly after a successful build. Do not run a process check first; if the game has the DLL locked, let the copy fail and report that. Do not close or kill the game or any related process to unlock the DLL unless the user explicitly asks for that after seeing the lock:

```powershell
Copy-Item -LiteralPath '<WORKSPACE>\UADVanillaPlus\UADVanillaPlus\bin\Release\net6.0\UADVanillaPlus.dll' -Destination '<UAD_INSTALL>\Mods\UADVanillaPlus.dll' -Force
```

## Crash Dumps And Native Debugging

When UAD crashes without a managed exception in `Latest.log`, check Windows crash artifacts before guessing. WER reports may only contain `Report.wer`; real dumps are often under `%LOCALAPPDATA%\CrashDumps`. Copy useful UAD dumps into `<WORKSPACE>\UADCrashDumps\dumps` so analysis stays in the workspace.

Workspace-local crash tooling installed on 2026-05-14:

- .NET SDK 8: `<WORKSPACE>\.tools\dotnet-sdk\dotnet.exe`
- `dotnet-dump`: `<WORKSPACE>\.tools\dotnet-tools\dotnet-dump.exe`
- Windows Debugging Tools `cdb`: `<WORKSPACE>\.tools\WindowsKits\Debuggers\x64\cdb.exe`
- Sysinternals ProcDump: `<WORKSPACE>\.tools\Procdump\procdump64.exe`

Per-user WER full dumps are enabled for `Ultimate Admiral Dreadnoughts.exe` under `HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Ultimate Admiral Dreadnoughts.exe`:

- `DumpFolder`: `<WORKSPACE>\UADCrashDumps\LocalDumps`
- `DumpCount`: `5`
- `DumpType`: `2` (full user-mode dump)

The next native crash should create a larger `.dmp` in that folder. Full dumps can be large; delete old files from `<WORKSPACE>\UADCrashDumps\LocalDumps` after they are no longer useful.

Use `dotnet-dump` first for managed/interop clues. It needs the workspace .NET root on PATH because the system `dotnet` may only have an older runtime:

```powershell
$env:DOTNET_ROOT='<WORKSPACE>\.tools\dotnet-sdk'
$env:PATH='<WORKSPACE>\.tools\dotnet-sdk;<WORKSPACE>\.tools\dotnet-tools;' + $env:PATH
& '<WORKSPACE>\.tools\dotnet-tools\dotnet-dump.exe' analyze '<WORKSPACE>\UADCrashDumps\dumps\<dump-file>.dmp' -c "threads" -c "clrthreads" -c "clrstack -all" -c "pe" -c "exit"
```

Use `cdb` when native frames matter. Public symbols are limited for game binaries, but even export-level stacks can show whether the fault crosses `coreclr`, `GameAssembly.dll`, MelonLoader's `version.dll` shim, and `UnityPlayer`:

```powershell
& '<WORKSPACE>\.tools\WindowsKits\Debuggers\x64\cdb.exe' -y 'srv*<WORKSPACE>\.tools\symbols*https://msdl.microsoft.com/download/symbols' -z '<WORKSPACE>\UADCrashDumps\dumps\<dump-file>.dmp' -c ".ecxr; r; k; lm m coreclr; lm m GameAssembly; lm m version; q"
```

If the next repro needs a richer dump than WER gives, attach ProcDump before triggering the crash. Prefer attaching by PID:

```powershell
$p = Get-Process | Where-Object { $_.ProcessName -eq 'Ultimate Admiral Dreadnoughts' } | Select-Object -First 1
& '<WORKSPACE>\.tools\Procdump\procdump64.exe' -accepteula -ma -e 1 $p.Id '<WORKSPACE>\UADCrashDumps'
```

For the 2026-05-14 battle-start crash, `dotnet-dump` showed `System.ExecutionEngineException` at `Il2CppInterop.Runtime.IL2CPP.il2cpp_value_box`, and `cdb` showed the native path crossing `coreclr -> GameAssembly.dll -> version.dll -> UnityPlayer`. Treat that shape as an Il2CppInterop/Harmony/native boundary problem, not a normal catchable C# exception.

## Current Feature Layout

- `plans/`: cross-session implementation notes, reusable investigation docs, vanilla-flow maps, formula notes, and next-step plans. Read the relevant file before resuming a planned feature or related investigation.
- `Harmony/UiVersionTextPatch.cs`: version text overlay only.
- `Harmony/CampaignFleetWindowDesignViewerPatch.cs`: Designs tab country viewer and design ship-count display only.
- `Harmony/CampaignConstructionStatusPatch.cs`: campaign construction summary/count display plus campaign maintenance indicators only.
- `Harmony/CampaignCountryInfoFinalRefreshPatch.cs`: final-pass campaign country-info decoration after broad vanilla UI refreshes only.
- `Harmony/CampaignCountryInfoWatchdogPatch.cs`: narrow campaign country-info repair pass for vanilla tab/popup rewrites only; keep checks cheap and scoped to visible country-info instances.
- `Harmony/CampaignPortShipCountPatch.cs`: campaign world-map port-name active vessel counts only.
- `Harmony/CampaignActiveFleetStatusPatch.cs`: campaign Active Fleet in-port count display only.
- `Harmony/CampaignTechnologyStatusPatch.cs`: campaign country-info technology timing indicator only.
- `Harmony/InGameOptionsMenuPatch.cs`: top-right UAD:VP in-game options menu only.
- `Harmony/CampaignPoliticsDeclareWarPatch.cs`: politics row Declare War and Force Peace buttons only.
- `Harmony/CampaignMapWrapVisualPatch.cs`: experimental campaign map visual wrap-around only; off by default from the UAD:VP Experimental options section.
- `Harmony/BattleTimeSpeedLimitPatch.cs`: battle simulation speed limit QoL only.
- `Harmony/BattleWeatherBalancePatch.cs`: battle weather/daytime balance option only.
- `Harmony/BattleAccuracyPenaltyBalancePatch.cs` and `GameData/AccuracyPenaltyBalance.cs`: battle design-side accuracy penalty balance option only; rewrites selected `StatData.effect` strings before vanilla `PostProcess` parses them, avoiding combat-time overhead and loaded-dictionary mutation.
- `Harmony/BattleStartAccuracyBreakdownPatch.cs`: battle-accept design accuracy diagnostic logging only.
- `Harmony/Il2CppInteropExceptionPatch.cs`: compatibility/debug logging for Il2Cpp trampoline exceptions only.
- `Harmony/PortStrikeBalancePatch.cs`: port strike transport-loss balance option only.
- `Harmony/DesignTorpedoRestrictionPatch.cs`: CA+ torpedo availability balance option only.
- `GameData/CampaignDiplomacyActions.cs`: small diplomacy validation/action helpers for campaign politics patches.
- `GameData/ExtraGameData.cs`: small campaign/player lookup helpers.
- `GameData/ModSettings.cs`: typed in-game option state only; feature patches should read options from here instead of parsing files.
- `GameData/PlayerExtensions.cs`: small player fleet enumeration helpers.

## High-Level Design

- `UADVanillaPlusMod.cs` is the MelonLoader entrypoint. It should stay small: patch registration, startup logging, and lifecycle hooks only.
- `ModInfo.cs` is the single source of truth for mod identity, SemVer, and displayed version text. Do not duplicate version strings in patches.
- `Harmony/` owns behavior changes implemented through Harmony patches. Each feature should get its own patch file named after the game surface it changes, such as `CampaignFleetWindowDesignViewerPatch.cs`.
- `GameData/` owns small read/query helpers around UAD campaign objects. Keep these helpers generic and side-effect free so multiple feature patches can share them safely.
- Campaign country-info additions are currently split between native text producers and final repair hooks because some vanilla tab/popup paths repaint labels after normal refresh. Campaign maintenance indicators use vanilla's `ShipbuildingCapacity` multi-line text field (`maintenance`, `Shipbuilding Capacity:`, then used/capacity) instead of modifying `ShipyardSize` layout height or using a positioned overlay.
- Future folders should follow responsibility, not chronology. For example, put reusable UI construction helpers under `Ui/`, data import/export helpers under `Data/`, and ship/designer calculations under `ShipDesign/` if those areas become real modules.
- Feature patches should explain their intent in comments near the class or non-obvious methods: what behavior changes, why VP wants it, and what vanilla behavior is being protected.
