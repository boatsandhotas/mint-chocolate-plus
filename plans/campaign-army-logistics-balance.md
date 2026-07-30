# Campaign Army Logistics Balance

## Context

Vanilla major-nation army logistics is meant to represent whether a country can
support land operations through economy, transports, and naval reach. The intent
is reasonable, but the current formula double-dips on empire size and army
burden. A large country already pays for a larger army through yearly army
budget and the resulting pressure on naval funds. Vanilla then also pushes that
same burden directly into the army logistics score.

The most visible bad outcome from the current campaign inspection was not just
that large empires could have lower logistics. It was that small or shattered
major powers can look too healthy because their denominator is tiny, while a
normal-sized state with weak GDP can collapse to single-digit logistics. Spain
at 10 percent while one-province Japan sat at 47 percent is formula-consistent
but fails the campaign-story smell test.

A later live screenshot showed the United States at 26 percent logistics despite
745,896 tons of fleet and 136 percent transport capacity. The visible reason was
that vanilla reported only 21 percent Navy Power Rating for the US while China
showed 44 percent and 200 percent transport capacity. This means VP should not
use raw `Player.NavyPowerRating()` as the only navy-support input. It should
blend effective power rating with fleet tonnage coverage so a large but
apparently low-projection navy is not treated the same as no navy at all.

## Vanilla Evidence

Primary methods:

- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\Player.txt`
  - `Player.LogisticsFactor()` around line 21618
  - `Player.LogisticsFactorFinal()` around line 21907
  - `Player.StateBudget()` around line 15700
  - `Player.YearlyArmyBudget()` around line 20800
  - `Player.TotalPowerProjection()` around line 21045
  - `Player.NavyPowerRating()` around line 21515
  - `Player.GetTotalPopulationWithColony()` around line 20661
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignController.txt`
  - `CampaignController.GetBaseYearMultiplier(...)` around line 7292
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignFinancesWindow.txt`
  - `CampaignFinancesWindow.GetArmyLogistics(Player)` calls `Player.LogisticsFactorFinal()`
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\ProvinceBattleManager.txt`
  - land battles call `Player.LogisticsFactorFinal()` repeatedly for strength and losses
- `E:\Codex\cpp2il_uad_isil\IsilDump\Assembly-CSharp\CampaignConquestEvent.txt`
  - conquest progress calls `Player.LogisticsFactorFinal()`

Major-nation vanilla shape:

    LogisticsFactor =
      clamp((StateBudget - YearlyArmyBudget) / pow(totalPopulationWithColonies + 1, 0.725))
      * yearScale(max=0.00001, min=0.001)
      * lerp(0.25, 4, TransportCapacity * 0.5)
      * lerp(2, 0.25, YearlyArmyBudget / (StateBudget * 0.5 + 1))
      * navyPowerFactor

    LogisticsFactorFinal =
      clamp(lerp(1, 100, clamp01(LogisticsFactor / yearScale(max=50000, min=10000))), 1, 100)

Medium and minor/small nations do not use the major formula. They roll each
time `LogisticsFactorFinal()` is called:

    Medium:
      clamp(Random.Range(0.55, 1) * yearScale(max=79, min=29), 1, 100)

    Minor/small:
      clamp(Random.Range(0.73, 1) * yearScale(max=50, min=12), 1, 100)

That random-per-call behavior can make non-majors look too strong and unstable
from one UI/campaign call to another.

## Agreed VP Design

Replace army logistics with a more legible coverage model:

    Army Logistics = transport coverage * navy coverage

Do not include:

- `StateBudget - YearlyArmyBudget`
- a second army-budget pressure multiplier
- a direct `population^0.725` denominator
- random per-call medium/minor rolls

Rationale:

- Army size and army budget already matter because they consume state resources.
- Transport capacity already has economy-scaled cost through vanilla transport
  capacity income/expense logic.
- Bigger empires should still need bigger navies, but that requirement should
  come from empire footprint, not from reusing army budget and population burden.
- Player agency should be clear: protect/fund transports and maintain enough
  effective navy power for the national footprint.

## Immediate Next Step

Do not jump directly to the final replacement formula. First add a diagnostic
pass that logs the hidden inputs behind logistics and navy rating for the visible
major powers in the current campaign. The screenshots show that `NavyPowerRating`
can diverge sharply from visible fleet tonnage, so VP needs evidence about
projection, deployed status, and footprint before locking constants.

Diagnostic target:

    player label
    vanilla LogisticsFactorFinal()
    TransportCapacity
    NavyPowerRating()
    TotalPowerProjection()
    TotalFleetTonnage()
    counted fleet ship count
    counted in-port / at-sea / other status counts
    active task force count if easy to read safely
    province count
    home/foreign port counts
    projection per 100k tons
    projection per port
    projection per province

This can be a temporary or option-gated log tied to country-info refresh,
campaign load, or a once-per-player campaign sample. Keep it compact and
deduplicated. The goal is to calibrate Britain, Germany, France, US, Italy,
Austria-Hungary, Spain, Japan, China, and the Soviet Union from the same save
state before implementing the balanced formula.

## Balanced Formula Direction

Patch `Player.LogisticsFactorFinal()` so the same number drives UI, province
battles, and conquest progress.

For major nations:

    transportCoverage = clamp01(player.TransportCapacity)

    requiredNavyRating =
      base requirement
      + home port requirement
      + foreign/non-home port requirement
      + non-home province footprint requirement

    powerCoverage = clamp01(player.NavyPowerRating() / requiredNavyRating)

    requiredFleetTonnage =
      base tonnage requirement
      + home port tonnage requirement
      + foreign/non-home port tonnage requirement
      + non-home province tonnage requirement

    tonnageCoverage = clamp01(player.TotalFleetTonnage() / requiredFleetTonnage)

    navyCoverage = clamp01(powerCoverage * powerCoverageWeight + tonnageCoverage * tonnageCoverageWeight)

    result = clamp(round-or-float(100 * transportCoverage * navyCoverage), 1, 100)

Suggested first-pass constants, subject to tuning after screenshots/logs:

    baseRequiredNavyRating = 20
    homePortWeight = 0.5
    foreignPortWeight = 1.0
    nonHomeProvinceWeight = 0.35
    minRequiredNavyRating = 20
    maxRequiredNavyRating = 100
    baseRequiredFleetTonnage = 100000
    homePortTonnageWeight = 3000
    foreignPortTonnageWeight = 6000
    nonHomeProvinceTonnageWeight = 4000
    powerCoverageWeight = 0.5
    tonnageCoverageWeight = 0.5

The exact constants should be treated as tuning knobs. The design goal is:

- compact majors with a decent navy can reach useful logistics
- sprawling empires need meaningfully higher navy power
- large fleets should help even when vanilla Navy Power Rating looks oddly low,
  but raw tonnage alone should not grant perfect logistics
- one-province or tiny-footprint powers cannot get world-class logistics from a
  tiny denominator
- transport capacity below 100 percent hurts immediately
- transport capacity above 100 percent does not inflate logistics past the navy
  coverage cap

For medium and minor/small nations:

    mediumResult = min(majorStyleCoverageResult, 50)
    minorResult = min(majorStyleCoverageResult, 25 or 30)

Recommended first pass:

    medium cap: 50
    minor/small cap: 25

Avoid vanilla random rolls. If a non-major has missing data or a zero footprint,
use a conservative deterministic fallback under its cap rather than rolling.

## Implementation Handoff

Add a Campaign option:

    Army Logistics: Balanced / Vanilla

Default should be `Balanced`, matching VP's usual balance-feature policy.
`Vanilla` should return the original `Player.LogisticsFactorFinal()` behavior.

Likely files:

- `E:\Codex\UADVanillaPlus\UADVanillaPlus\GameData\ModSettings.cs`
  - add an `ArmyLogisticsMode` enum with `Vanilla = 0`, `Balanced = 1`
  - add a `uadvp_army_logistics_mode` PlayerPrefs key
  - default to `Balanced`
  - add mode text, current-settings text, and option logging
- `E:\Codex\UADVanillaPlus\UADVanillaPlus\Harmony\InGameOptionsMenuPatch.cs`
  - add an option row in the Campaign section
  - recommended tooltip: "Balanced bases army logistics on transport capacity and navy coverage of the national footprint. Vanilla keeps the game's budget/population formula and random non-major rolls."
  - call `RefreshCampaignCostUi("Army Logistics mode change")` after changing the mode so visible country/finance text refreshes
  - include the option in launcher tooltip and balance-option detection
- `E:\Codex\UADVanillaPlus\UADVanillaPlus\Harmony\CampaignArmyLogisticsBalancePatch.cs`
  - patch `[HarmonyPatch(typeof(Player), nameof(Player.LogisticsFactorFinal))]`
  - use a prefix:
        if mode is Vanilla, return true
        otherwise compute `__result` and return false
  - keep all calculation code in a small helper class or private methods
  - use safe loops over Il2Cpp lists; avoid LINQ on Il2Cpp collections in hot-ish paths
  - log one compact confirmation per player/fingerprint, including player label,
    major/medium/minor, transport capacity, navy rating, required navy rating,
    total fleet tonnage, required fleet tonnage, footprint counts, cap, and result

Implementation cautions:

- Do not patch only the UI getter. `LogisticsFactorFinal()` is used by actual
  land-war and conquest logic.
- Do not call `LogisticsFactorFinal()` from inside the balanced calculation, or
  the patch will recurse.
- Use `Player.NavyPowerRating()` rather than naval prestige. The country-info UI
  already displays `NavyPowerRating()` as "Navy's Power Rating".
- Do not use `Player.NavyPowerRating()` alone. The live US screenshot showed
  745,896 tons but only 21 percent Navy Power Rating, so a balanced formula
  should blend rating coverage with `Player.TotalFleetTonnage()` coverage.
- For footprint, count controlled provinces and ports through `player.provinces`,
  `player.homeProvinces`, and `player.provincesWithPort`. Foreign/non-home ports
  should matter more than home ports.
- Clamp defensive fallback values. A null player, missing campaign data, or
  broken list should produce a conservative value, not an exception.

Verification:

1. Build with the repo's standard Release command after the builder bumps version
   per `AGENTS.md`.
2. Copy the DLL to the game Mods folder.
3. Launch the current campaign and open country info / finances.
4. Confirm `Latest.log` has the new option log and one-line logistics sample logs.
5. Compare visible values for France, US, Spain, Japan, Italy, and Austria-Hungary:
   - France/high-power majors should remain strong but not automatically magic
   - US should be driven by TR capacity and required navy coverage, not army budget
   - Spain should no longer collapse solely from budget/population double-dip
   - Japan/minors should be hard-capped and not roll into great-power logistics
6. Toggle `Army Logistics` to Vanilla and confirm the old values return.
