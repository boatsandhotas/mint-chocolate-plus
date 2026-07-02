# UAD:VP 0.5.271 — Release Notes (changes since 0.5.104)

This is a large release. The headline additions are a full nation ship-paint system, player-launched naval and land invasions with diplomatic consequences, buying ships from allies, per-class ship naming themes, submarine/patrol auto-assignment panels, capturing surrendered ships, persistent ship service records, and a territory-linked shipbuilding-capacity overhaul. Alongside these are AI economy improvements, several stability fixes, and a batch of removed/retired features.

Unless noted, features apply to the human player only and leave AI code paths on native behavior.

---

## Campaign

### Naval & land invasions (new)
- **Launch naval invasions from an enemy-port popup.** A new *Launch Invasion* button on any enemy or minor-power port popup lets you order a seaborne invasion of that province yourself instead of waiting on the AI. Hidden for your own ports and where an invasion is already underway; shown greyed-out with a reason when you have under 25,000 tons of shipping in that sea area. Routed through vanilla's own conquest-event creation so save/load and AI awareness stay native; your task forces in the area supply the required tonnage. *(On)*
- **Launch land invasions from the province popup.** A *Launch Land Invasion* button lets you attack an adjacent enemy territory overland. Appears only when one of your provinces shares a confirmed mutual land border with the target (islands/non-bordering provinces are blocked), and re-enables after a failed assault. Creates a native `ProvinceBattle` (with its on-map arrow/flag) so the campaign, save/load, and AI treat it as vanilla-spawned. *(On)*
- **Enemy ports and province names now respond to clicks.** Vanilla ignores clicks on enemy/minor ports and territory labels; VP force-opens the full port window and province popup for them. This is what surfaces the invasion buttons, and as a side effect lets you open info popups for territory you don't own. *(On)*
- **Province popup pins in place when clicked.** The province popup is a cursor-following hover tooltip in vanilla, which made its invasion button impossible to click. Clicking a province name now latches the popup at a fixed position and adds an explicit Close button; plain hover still behaves as a normal tooltip, and stale pins self-heal if dismissed another way. *(On)*
- **Invasion diplomacy: war declaration, ally-betrayal penalty, and third-party blowback (with a preview).** Invading in peacetime auto-declares war (on the defender, or on the major protecting a minor) and applies tiered attitude penalties to every other major based on their stake in the target — allied with the defender, bordering the invaded province/country, holding territory in the same sea area, down to a flat general-aggression hit — each jittered +/-10% and scaled by current relations. Betraying a sworn ally cancels the alliance and adds a severe extra reputation hit. Justified counter-invasions (already at war, they invaded you, or one side occupies the other's claimed land) waive all third-party penalties. The confirmation dialog previews the war declaration, ally-betrayal warning, and each major's grouped penalty before you commit. *(On)*

### Surrendered-ship capture & vanquished spoils (new)
- **Capture surrendered ships at battle end.** The victory-point winner now takes every surrendered, non-sunk ship — capturing the loser's surrendered vessels and recovering its own — instead of vanilla writing them all off as losses. The transfer is deferred until the campaign reconciles on World re-entry (with a loading-screen fallback) so it sticks, and kept ships are scrubbed from vanilla's post-battle loss pass. *(On; "Surrendered ship capture: On / Vanilla" toggle in options.)*
- **Vanquished-nation spoils on full conquest.** When an AI major is fully eliminated, its surviving fleet and a cash indemnity are distributed to the victors (majors holding its former territory, weighted by port capacity, plus a bonus to the "finisher" who took its last provinces), with only a fraction scuttled — instead of vanilla destroying the whole fleet and stranding the treasury. This removes the perverse incentive to leave a beaten nation a one-province rump so you can annex its assets. Majors only, never the human player. *(On; "Vanquished spoils share" Low/Medium/High option — Medium default: 40% of the fleet scuttled, 50% of cash seized.)*

### Shipbuilding capacity & port QoL
- **Shipyard capacity now follows conquest (multi-year rebuild).** Capturing a province from another major instantly strips the loser of that province's proportional share of its shipyard; the captor rebuilds that capacity gradually over a development-scaled number of years (roughly 0.5-6, faster for large/developed captures). Re-capturing a province mid-rebuild transfers only the portion developed so far — the undeveloped remainder is lost. A companion **Overseas Capacity Weight** option (Low/Medium/High, Medium default) sets how much colonial territory counts toward capacity versus the homeland. *(On; vanilla left capacity unchanged on territory transfer.)*
- **Higher total shipbuilding capacity for all players.** Multiplies the total shipbuilding-capacity limit for every player (human and AI) so a single large design no longer eats a huge fraction of a restrictively low vanilla cap. Applies uniformly across UI, build gating, overcapacity penalties, and AI scrap targets. *(On; Vanilla / 1.5x / 2x / 3x in options, 2x default.)*
- **Send a whole multi-selected group to one port in one click.** Raises vanilla's small per-click limit in the Fleet tab's Change Port flow so an entire multi-selected group can be ordered to a single port at once. *(Always on.)*

### Submarine & patrol auto-assignment (new)
- **Minelayer Port Goals panel (F8).** Set a default per-port minelayer composition (count of each sub variant) plus per-port overrides or a "skip this port" marker. *Rebalance now* places only idle/unassigned minelayer subs into quota-deficit ports, leaving assigned and in-transit subs untouched. A per-variant *Build N + assign* action builds the missing subs (with a shipyard-capacity-over-budget warning) and homes them. Goals persist per campaign. *(On)*
- **Patrol / Foreign Stations panel (F7).** See your controlled regions ranked by tonnage deficit (required defense minus current), multi-select your in-port or building ships (by ship, shift-click, or whole class), then *Send* them to a chosen region/port or *Auto-distribute* the selection so each ship goes to the currently-neediest region and the load spreads out. Contract hulls being built for other nations are excluded. *(On)*

### Ship service records (new)
- **Persistent per-ship career battle log.** Every player ship keeps a career service record across the whole campaign. Each battle records absolute damage dealt and received, ships sunk (finishing blow) and wrecked (most damage to a ship that went down), whether it survived, and a per-enemy breakdown (each foe's class, tonnage, and damage taken). Records are keyed by ship id so they keep accumulating across refits and saves. *(On)*
- **Ship Service Records viewer (F10 panel).** A draggable, always-on-top campaign-only panel: a sortable, scrollable ship list on the left (sort by tonnage sunk, kills, battles fought, name, or class); the selected ship's career totals on the right, including tonnage sunk/damaged broken down by enemy type, plus a newest-first battle history where each row expands to reveal the specific enemy ships sunk, wrecked, or hit that battle. *(Gated behind the Ship Service Records option, On by default.)*

### Other campaign tools
- **Switch Nation (defect to another major mid-campaign).** A new *Switch Nation* tab in the in-game options lets you hand your current nation to the AI and take over any other major, continuing the same campaign as them. Pick a target with `<`/`>`, arm and confirm; the mod flips `isMain`/`isAi` on both nations, carries your campaign-id GUID onto the new nation, saves, and drops to the main menu to Continue as the new nation. Blocked during battle and when no other majors exist.
- **Bundled ship-name theme database.** An embedded name-theme database (`ship_names.csv` baked into the DLL, no loose config) supplies the selectable naming themes: per-country national fleets for roughly ten nations, universal themes usable by everyone (e.g. Greek Gods, optionally culture-gated to owners), and conquered-territory themes that let you name a class from another nation's pool once you hold its territory. National themes shadow same-named universal ones, and available themes are filtered to the class.

---

## AI

- **AI majors fund transport, then tech, then crew training.** AI majors now redistribute their already-chosen naval budget by a priority ladder — merchant/transport capacity up to 200%, then technology, then crew training — instead of vanilla starving transport (observed around 8% when ~200% is healthy). It only reallocates the existing total (never raises spend, bankruptcy-safe) and applies to AI majors only, never the human. *(On)*

---

## Battle

- **Division speed-sync: followers hold the leader's manual speed.** When you set a manual speed on a player division's leader, every follower is forced to that same speed (capped to its own max) instead of racing ahead and looping/doing 360s to fall back into line; releasing the leader's manual speed returns followers to normal formation speed. Player divisions only, AI-controlled ships skipped. *(On)*
- **Reverse-course hotkeys (R = port, T = starboard).** Press R to turn every selected division 180 degrees to port, T to starboard. The maneuver is chosen in options: Single 180, "90 / swap / 90" (default), "Split & rejoin" (temporarily splits each follower into its own division so all ships start the turn on the same frame, then rejoins them reversed), or Rudder (experimental hard-over per ship, falls back to 90/swap/90 if it can't start). Column order is reversed and the group UI refreshed so the new lead ship shows in front. Also backs the HELM panel's on-screen Port/Stbd buttons. *(Player-initiated, always available.)*
- **Parallel station-keeping order (Shift+P / new PAR button).** Select a division, press Shift+P or click the new PAR button in the order bar, then click an anchor division — the selected division continuously runs parallel to that anchor, offset either astern and toward the disengaged (away-from-enemy) side (good for a DD torpedo lurk or a trailing line) or on its beam (abreast, via an options toggle; astern default). Any genuine new order, manual move, follow/scout/screen order, or retreat cancels it.

---

## Constructor / UI

### Class naming themes (new)
- **Assign a name theme per ship class in the constructor.** A new *Theme* button lets you assign a naming theme to a ship's class. New ships of the class then draw names from the chosen theme's pool (deconflicted against names already in use) instead of the generic per-nation list, or from a sequential "`<Class>`-N" numbering scheme. Choices save per campaign, keyed by the class's real refit base-name so all refits share the theme, and apply to the human player only. *(Controlled by a "Class Naming Themes" On/Off option that defaults On but has no effect until you assign a theme; Off also hides the button and reverts to vanilla naming.)*
- **Assigning a theme retroactively renames the whole class family.** Choosing a theme immediately renames the entire class family: all matching design templates and the lead ship take the theme's first name, and remaining ships take successive theme names, each keeping its own refit-date suffix (e.g. "(Jul. 1904)") so refit variants stay dated. Human player only; the class assignment is re-keyed to the new lead name so future builds stay themed.

### Fleet UI fixes / QoL
- **Reworked ship "Pwr" power rating (throw-weight model).** Replaces vanilla's misranking `EstimatePower` as the displayed base power with a throw-weight model: per-salvo shell damage (scaling roughly with caliber cubed) times a hull-size/armor survivability term, so capital ships outrank light fast-firing cruisers and "fewer, bigger guns" raises power. Only Belt and Deck armor are read (other zones carry garbage sentinels on light hulls). Native influence/PowerProjection is left untouched. *(On, no in-game toggle.)*

---

## Experimental (opt-in, off by default unless noted)

### Nation ship paints
- **Experimental Nation Ship Paints mode.** An opt-in mode (default **Off**) that recolors ships with nation-themed paint schemes in the constructor preview and in battle. Ten built-in national base looks (USA, UK, Germany, France, Russia, Japan, Italy, Austria-Hungary, Spain, China) tint hull, superstructure, and guns while preserving texture detail; turning it Off restores the game's original ship materials.
- **Ship Paints settings pane with per-nation swatches.** While paint mode is on, a *Ship Paints* tab lists every supported nation as a row of clickable per-channel color swatches with a channel-label header and a per-nation Reset. Hidden when the toggle is Off.
- **Eight per-channel paint targets.** Hull, Superstructure, Turret (guns/barbettes), Deck, Bottom (below waterline), Detail (roof/metal fittings), Barrel, and Trim — classified per-material so one part can carry several separately tinted surfaces.
- **Constructor paint panel: separate Nation vs per-Class colors.** A paint launcher button in the constructor opens a panel with two swatch rows — nation-wide colors and per-class (this design) colors layered on top — so a class can be painted differently from the rest of the fleet. Per-class overrides are stored per design GUID; only the channels a class customizes replace the nation values.
- **Promote / Demote / Swap between class and nation paint.** The class row adds Promote (copy this class's overrides up into the nation so future classes inherit them), Demote (clear class overrides, falling back to the nation scheme), and Swap (exchange the nation and class paint strings).
- **Color picker.** Clicking any swatch opens a live-preview picker: a draggable hue/saturation wheel, brightness/value slider, typed `#RRGGBB` hex input, black/white quick swatches, a row of naval preset colors, and up to six saveable custom presets (Save to capture, shift-click a custom swatch to delete). Edits preview on the ship immediately.

### Buy ships from an allied major (new)
- **Buy Ship from an ally.** With the feature enabled, viewing an allied major's ship designs in the campaign fleet design viewer adds a *Buy Ship* button that opens a quote dialog: pick how many hulls (capped by your cash for the deposit and the ally's willingness) and see per-hull price, build time, and the deposit/balance split. The ally builds the hulls in their own dock at a premium over the design's build cost (band configurable Low/Med/High, default Medium ~+50%..+120%, positioned by the seller's dock pressure); you pay a 30% deposit up front and the balance as each hull commissions, at which point ownership transfers to you. Orders and purchased-class records persist across save/reload. *(Off by default.)*
- **Incoming ally-built ships in the Fleet tab.** Ships an ally is building for you appear as rows pinned to the top of the Fleet tab, styled like vanilla's "building for another nation" rows (yellow "Building x%, Nm" / "Fitting out" status, the ally in the Sold column, total price in Cost). Each row has a Select Port control for delivery (you're also prompted right after ordering to set the port for the batch), and clicking a row lets you cancel the order (deposit forfeit, ally keeps the hull). These seller-owned hulls don't count against your own shipbuilding capacity. *(Off)*
- **Purchased ship classes are refit-only.** A class bought from an ally may be refitted but never queued as a new build in your own yards; the build gate is forced to fail for the purchased design and its entire refit lineage ("Purchased from ally — refit only; buy more from your ally to build new."). To get more hulls you must reorder from the ally. *(Off)*
- **Alliance-break disposition for outstanding orders.** If your alliance with a seller ends mid-build: breaking into war lets the ally seize the in-progress hull and forfeits your deposit; a peaceful split honors the contract so the ship still delivers on completion. Already-delivered ships are unaffected. *(Off)*

### Battle experimentals
- **Auto-apply preferred per-ship-type settings at battle start.** Applies your saved per-type preferences each fight: ammo (AP/HE/Auto) and fire-torpedoes per ship, plus avoid-torpedoes, avoid-ships, auto group-leader, and Line/Column formation keyed by the division leader's type. Heavies (BB/BC/CA) default to AP, others to HE; "Leave" keeps vanilla. Re-applies to new divisions from a mid-battle split without overwriting manual changes. *(Off — opt-in preference.)*
- **Follow-steering damping to reduce the follower "weave."** Optional per-follower yaw damping that soft-clips only the excess turn rate above a threshold to kill the S-pattern weave a fast hull with a slow rudder shows while station-keeping. Leaders are untouched; ships far off-station (rejoining) and ships actively dodging torpedoes are exempted so they keep full rudder, and it never fights a commanded reverse maneuver. *(Off)*

### Other experimentals
- **Globe campaign-map mode (3D sphere skin).** The Map Geometry option becomes a three-way selector (Flat Earth / Disc World / Globe). Globe mode renders the campaign map as a true 3D political sphere built from the game's own country meshes re-projected by lat/long, orbited with right-drag/scroll, while the underlying simulation stays flat. No external assets. Border lines and great-circle movement are not represented. *(Off — Flat default.)*
- **Ship Resupply Override (top up stranded fleets).** Force-refuels and rearms all your ships on demand via the game's own resupply routines with unlimited free capacity, for task forces stranded at sea. Adds a "Ship Resupply Override" On/Off toggle plus a "Resupply My Fleet Now" button in options. *(Off)*

---

## Fixes

- **Per-turn weight/cost crash from null gun caliber data.** Stability guard preventing a null `turretPartData` in a ship's gun-caliber list from throwing a `NullReferenceException` in `GunData.BaseWeight` and aborting the campaign's per-turn weight/cost update for the entire fleet. At the `Ship.CWeight` chokepoint it asks vanilla to reconcile calibers, drops any still-bad entries, and logs the offending ship. *(Always on, no toggle.)*
- **Shared Designs browser freeze on broken designs.** Stops the main-menu Shared Designs browser from freezing when a community design references parts the current game data can no longer grade (e.g. a missing gun grade). Finalizers turn the hard freeze into a graceful skip so the broken design just fails to render; the constructor-exception swallow is scoped to the shared-design browser only. *(Always on.)*
- **Keep my fleet refittable: player refits no longer vanish from the Design tab.** When one of your ships' designs went obsolete, vanilla's cull erased the template and any refit cloned from it was born erased, so the new refit never appeared in the Ship Design tab. VP now skips the obsolescence cull for the main human player and un-erases the in-flight refit clone so vanilla commits the refit normally. Human player only; AI design pipelines untouched. *(Always on, narrowly scoped to avoid the save corruption the old bulk un-eraser caused.)*
- **No more duplicate ship rows in the port popup.** Fixes the port popup listing some ships twice (a vessel that is both stationed and part of a task force in the port) by hiding exact-duplicate rows and keeping the first. The port tonnage total was already correct. *(Always on.)*

---

## Removed

- **"Obsolete Tech & Hulls (Retain)" feature and its refit-rescue workaround.** Deleted the old opt-in Retain feature (design with already-researched but "obsolete" tech/hulls) and its `ObsoleteRefitRescuePatch` save-recommit workaround, and removed the associated menu toggle. Superseded by the narrower, main-player-only refit-persistence fix; the rescue workaround's re-add-to-designs approach risked GetStore save corruption.
- **`CalcFollowing` true-abreast formation override.** Removed — in IL2CPP the native follow-steer calls `CalcFollowing` internally and bypasses the managed Harmony patch, so ships never moved to the clean-abreast station. Abreast is now delivered instead as an offset preset of the Parallel station-keeping order.
