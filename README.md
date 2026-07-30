# UAD Mint Chip Plus

UAD Mint Chip Plus (`UAD:MC`) is a lightweight mod for Ultimate Admiral: Dreadnoughts that keeps the base game feel while adding small quality-of-life improvements.

Current version: `0.6.34`

## Philosophy

- No performance degradation compared to vanilla.
- No config files: installation should stay as simple as one DLL in the `Mods` folder.
- Quality-of-life changes are always enabled.
- Most balance changes default to improved behavior and can be turned off individually from the in-game UAD:MC options menu; sharper or experimental options may default to vanilla.
- Balance changes are intended to make the game feel more fair: fewer extreme edge cases, fewer unrealistic exploits, and fewer outcomes where the player or AI gets punished by hidden or overly swingy mechanics.

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) for Ultimate Admiral: Dreadnoughts.
2. Download the latest `MintChipPlus.dll` from the repository releases page.
3. Copy `MintChipPlus.dll` into your game `Mods` folder.

Typical Steam install path:

```text
...\Steam\steamapps\common\Ultimate Admiral Dreadnoughts\Mods
```

Start the game normally after copying the DLL. If the mod loads, `UAD:MC` and the version number will appear in the game's version text.

## Features

### Campaign

**QoL:**

- **Campaign maintenance indicators**: show dock expansion status and transport capacity directly in the campaign country info panel.
- **Politics capacity details**: show each nation's transport capacity, dockyard size, and shipbuilding capacity in the Politics screen row.
- **Task force tonnage indicators**: fill campaign-map task-force icons by battle tonnage, with 100,000 tons and above shown as a full stack.
- **Task force return shortcut**: add a `Return to <origin port>` button to task-force popups for one-click orders back to port.
- **Fleet tab multi-select Change Port**: raise the Fleet tab's Change Port group limit so a whole multi-selected group of ships can be sent to one port in a single order.
- **Campaign battle auto-resolve odds**: show the player's vanilla auto-resolve win chance in the battle popup.
- **Campaign map port ship counts**: darken and bold ports with player or AI vessels, lightly mute empty ports, and show counts beside occupied port names on the world map.
- **Campaign active fleet port count**: show how many active vessels are currently in port.
- **Campaign construction summaries**: split own builds, foreign contracts, and commissioning ships in the existing build counts.
- **Campaign technology indicator**: show a compact estimate for the next expected research discovery.
- **Research standing markers**: show colored badges spelling out whether each research category is ahead of, behind, or tied with the leading major nation.
- **Direct diplomacy politics actions**: add Declare War and Force Peace buttons with confirmation to campaign politics rows, with Force Peace using the vanilla reparation flow when war victory points produce a clear winner.
- **In-game options menu**: control UAD:MC balance options from the top-right game UI.
- **Shared design usage control**: change the active campaign's Shared Designs mode after campaign start so future AI design needs can use `Off`, `Selective`, `Always`, or MC-only `Only` without starting a new campaign. `Only` blocks random AI fallback when no shared design is accepted.
- **Shared design browser stability**: guard the main-menu Shared Designs browser so a community design that references parts the current game data can no longer grade fails to render gracefully instead of freezing the game, letting players scroll past or back out.
- **Shared design import adaptation**: in Advanced AI Builder's Enhanced mode, let AI shared-design imports safely downgrade armor quality, torpedo size, radio, rangefinder, steering, auxiliary-engine, and drive-shaft components, ignore safe stat-only tech baggage, and trim range or speed for slight tonnage near misses before final build validation.

**Balance:**

- **Port Strike balance**: scales transport losses from undefended port strikes by attacker tonnage instead of allowing small raiders to destroy large transport groups.
- **Sea transport loss scope**: default-on `Active Forces` mode makes sea-zone transport losses ignore task forces that are only moving through a region instead of counting them as settled local raiders; `Vanilla` restores the game's original area-vessel list.
- **AI Fleet Mix**: optional `Vanilla`, `Balanced`, and `Heavy` modes adjust AI surface-ship construction weights and, outside vanilla mode, favor the most under-target valid surface types. This defaults to `Heavy`.
- **Advanced AI Builder**: default-on `Enhanced` mode lets MC help AI design books with shared-design blueprint adaptation, missing-type recovery, and stale-design refreshes; `Vanilla` keeps the game's original design-generation cadence and exact shared-design checks.
- **Smart AI Designs**: experimental, default-off mode replaces vanilla's random AI new-design fallback with one deterministic MC attempt after shared and predefined designs fail.
- **Smart Refits**: default-on `Enhanced` mode replaces vanilla AI random refits with MC's conservative refit pass and adds a player-only `Smart Refit` constructor button; `Vanilla` restores the game's original AI refit path and hides the MC button.
- **Campaign naval mobility**: Adds an in-game campaign movement preset that defaults to faster task-force movement and a wider matching supply envelope, with a vanilla preset available from the UAD:MC options menu.
- **Task Force Sustainment**: default-on `Full` mode keeps campaign task forces supplied and tops off campaign fuel and ammunition at movement, maintenance, and battle boundaries; `Vanilla` restores the game's original campaign supply, fuel, and ammunition attrition.
- **AI task-force staging**: default-on `Staging` mode helps AI task forces headed to the same theater pause and rendezvous before battle generation instead of arriving as isolated piecemeal fights; `Vanilla` keeps the game's original dispatch.
- **Suspend Dock Overcapacity**: automatically delays lower-priority repairs, builds, and refits when monthly dock work exceeds shipyard capacity; manual mode keeps vanilla overcapacity handling.
- **Foreign port shipbuilding capacity**: controlled non-home ports can contribute 50% of their normal port-capacity share to national shipbuilding capacity, with a `Vanilla` toggle in the UAD:MC options menu.
- **Army Logistics balance**: default-on `Balanced` mode bases army logistics on transport capacity plus navy-rating and fleet-tonnage coverage of national ports and provinces; `Vanilla` restores the game's budget/population formula and random non-major logistics rolls.
- **Canal openings**: optional setting to open the Panama and Kiel canals from 1890 when a campaign map loads, matching early-campaign canals such as Suez; historical mode keeps vanilla's 1914 and 1895 opening years.
- **Technology Spread**: optional `Gradual`, `Swift`, and `Unrestricted` modes that help major nations catch up faster in research categories where they trail the current leader. `Historical` grants every major nation all normal technologies by historical year and disables research spending while leaving repeatable end-techs vanilla. This defaults to vanilla.
- **Campaign End Date**: optional setting to disable vanilla's forced 1965 retirement so campaigns can continue past the normal end date. This defaults to enabled.
- **Mine Warfare**: optional setting to disable minefield damage in existing campaigns and hide mine and minesweeping equipment from the ship designer. This defaults to enabled.
- **Submarine Warfare**: optional setting to disable submarine construction and submarine campaign battles while leaving existing submarines in saved campaigns untouched. This defaults to enabled.

### Design

**QoL:**

- **Design ship counts**: show active, building, and unavailable ships for each design.
- **Auto Design Lite**: adds a separate designer Auto Design button that auto-places parts on the current hull without running vanilla's full spec-changing ship generator.
- **Generate armor button**: adds a constructor action that creates a balanced armor layout from the current main guns, with a type-based fallback when no main guns are mounted.
- **Smart new-hull defaults**: when the player picks a new hull, start from a freshly named max-range, max-tonnage, optimal-speed design with veteran crew, spacious quarters, best-available propulsion/protection components, and sensible armament defaults after the first gun is placed.
- **Single-attempt auto design**: the designer Auto Design button now makes one generation attempt and keeps the result visible if it fails.
- **Designs tab country viewer**: browse major AI nations' ship designs from the campaign Designs tab.
- **Design power column**: show a compact MC effective-combat-power score in the campaign Designs tab.
- **Refit design names**: use compact `Class (year)` names for player and AI refit designs, with same-year conflicts written as `Class (yearb)`, `Class (yearc)`, and so on.
- **British late-hull tower availability**: correct missing campaign compatibility between the Battlecruiser VI, G3, and N3 hull families and their matching late British main and secondary towers.

**Balance:**

- **Hull Speed Adjustment**: default-on `Adjusted` mode lowers early TB hull speeds to 26 knots, pre-1908 destroyers to 29 knots, and delays one oversized early TB dual funnel until the small-funnel unlock; `Vanilla` restores original hull speed and funnel availability.
- **Hull Weight Adjustment**: default-on `Adjusted` mode caps extreme hull mass ratios by ship class, softens double/triple-bottom penalties, removes direct citadel weight penalties, removes armor-quality weight reductions, removes a hidden anti-torpedo material-weight bucket, flattens crew weight, lightens early torpedo-boat towers, and gates oversized early TB towers to later tech; `Vanilla` restores original hull weights and protection-tech effects.
- **CA+ torpedo restriction**: optionally disallow torpedo launchers on heavy cruisers, battlecruisers, and battleships.
- **Obsolete tech and hull retention**: optional player-only setting to keep already researched obsolete hulls and components available in ship design while AI design availability stays vanilla. This defaults to vanilla.
- **Superstructure Compatibility**: optional player-only `Unrestricted` mode that lets researched main towers, secondary towers, and funnels be used beyond their vanilla hull-family compatibility. Tech, country, ship class, mount, and placement checks still apply.

### Battle

**QoL:**

- **Battle speed quality-of-life**: keep the player's selected battle speed available when the game tries to slow simulation speed near enemies.
- **Space pause toggle**: use Space in battle to pause and resume to the previous battle speed.
- **Weapon target range readout**: show the current target range under each battle weapon-row aim/status line.
- **Battle division AI control**: add an `AI` division-order toggle with `6` hotkey support in battle so selected friendly divisions can be handed back to AI control, with manual right-click orders returning them to player control.
- **Battle reverse-course hotkeys**: press `R` to reverse every selected division to port, or `T` to starboard, with the maneuver selectable in the Battle options (Reverse-Course Method): *180* (reorder + a single ~179° swing), *90·90* (turn 90°, swap the column once the turn is initiated, finish 90°), *Split* (each ship breaks into its own division and pivots at the same instant for a true simultaneous start, then rejoins reversed), or *Rudder* (direct hard-over, experimental). Split/Rudder fall back to 90·90 if they can't start.

**Balance:**

- **Battle spotting range**: optional `Vanilla`, `3x`, `5x`, and `10x` modes multiply spotter-side spotting range for both player and AI ships. This defaults to `3x`.
- **Battle Damage**: optional `Unchanged`, `2x`, `3x`, and `5x` modes multiply vanilla global gun and torpedo section-damage params for punchier battles. This defaults to `3x`.
- **Realistic Shell Damage**: optional cubic gun damage curve anchored at vanilla 12-inch shell damage, reducing excessive small-gun structure damage and very-large-gun spikes while leaving shell weight, penetration, reload, range, and the Battle Damage multiplier untouched. This defaults to `Realistic`.
- **Crew & accuracy balance**: lets players flatten extreme smoke/stability/instability accuracy penalties, crew-training Accuracy/Aiming/Reload/Damage Control swings, and damage-state accuracy penalties from damaged fire control, damaged conning towers, flooding instability, and damage instability.
- **Battle weather balance**: optionally force daytime, clear skies, calm wind, and calm seas instead of random bad-weather rolls.

### Experimental

- **Map Geometry**: optional `Disc World` seamless visual wrap-around for the campaign map at the Pacific edge, including source map material detail on side maps, clickable port/task-force/mission marker copies, wrapped task-force route visuals, and wrapped-map movement clicks/destinations. The `Flat Earth` setting keeps vanilla map geometry and remains the default.
- **Experimental Nation Ship Paints**: optional nation-themed ship paint schemes for designer previews and battles, with editable per-nation paint strings for hull, superstructure, and gun colors. This is disabled by default so vanilla ship materials remain unchanged unless players opt in.

## Known Issues

- Map Geometry's `Disc World` mode is still experimental. Map surface/material details, labels, political overlays, grid visuals, wrapped port/task-force/mission marker clicks, task-force route visuals, and wrapped-map movement destination clicks wrap. Country/state border-line rendering on side maps is still under diagnostic investigation, and some less-common map interactions and marker types may still use vanilla map behavior.
- Experimental Nation Ship Paints is still being tuned for visual consistency and battle-load performance. Runtime texture recoloring remains disabled; the option currently uses material color clones only when explicitly enabled.
- Superstructure Compatibility's `Unrestricted` mode is intentionally conservative, but newly exposed tower and funnel combinations may still need class-group tightening or a denylist after broader campaign testing.

## Building And Running From Source

Clone the repository and build the solution with .NET 6:

```powershell
dotnet build .\MintChipPlus.sln -c Release
```

If the project cannot find your game install automatically, set `UAD_PATH` to the Ultimate Admiral: Dreadnoughts install folder:

```powershell
$env:UAD_PATH='C:\Program Files (x86)\Steam\steamapps\common\Ultimate Admiral Dreadnoughts\'
dotnet build .\MintChipPlus.sln -c Release
```

The built DLL will be here:

```text
MintChipPlus\bin\Release\net6.0\MintChipPlus.dll
```

To build and copy directly into the game's `Mods` folder:

```powershell
dotnet build .\MintChipPlus.sln -c Release /p:DeployOnBuild=true
```

## Borrowing Code

Other Ultimate Admiral: Dreadnoughts modders are welcome to borrow, adapt, or learn from this code for their own mods. Credit is appreciated when copying larger pieces, but the main goal is to make useful modding patterns easier to share.

## Modding Notes

Features are split into focused Harmony patches where possible so individual ideas can be traced without importing the whole mod. If a small helper or patch saves you time in another project, feel free to use it.

## Thanks

UAD Mint Chip Plus is inspired by the work of the [Tweaks and Fixes / UAD Realism DIP team](https://github.com/DukeDagor/UADRealismDIP), especially as a reference for how Ultimate Admiral: Dreadnoughts modding works.
