using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Validation + execution helpers for player-launched naval and land invasions.
// Both flows share the same diplomatic backbone (force war on defender if at
// peace, then apply third-party blowback via InvasionDiplomaticConsequences)
// and call vanilla's own creators so save/load and AI awareness stay on
// vanilla code paths.
//
// Naval invasions  → CampaignController.CreateConquestEvent + CampaignConquestEvent
// Land invasions   → ProvinceBattleManager.AddBattle + new ProvinceBattle
internal static class CampaignInvasionActions
{
    private const int DefaultRequiredTonnage = 25000;
    private const int MinAttackerAreaTonnageForNaval = 25000;

    // Status returned by the Check* methods. HardBlocked = hide the button
    // entirely (target doesn't make sense at all — own port, conquest
    // already in progress). SoftBlocked = show button but disabled with
    // the reason in the popup (target is plausible but a current condition
    // prevents launch — insufficient tonnage, no land border, etc.).
    internal enum InvasionTargetStatus { Allowed, SoftBlocked, HardBlocked }

    // -- Naval --------------------------------------------------------------

    internal static bool CanLaunchNavalInvasion(
        Player? attacker,
        PortElement? target,
        out Player? defender,
        out Player? majorAlly,
        out Province? province,
        out string reason)
    {
        return CheckNavalInvasion(attacker, target, out defender, out majorAlly, out province, out reason)
            == InvasionTargetStatus.Allowed;
    }

    internal static InvasionTargetStatus CheckNavalInvasion(
        Player? attacker,
        PortElement? target,
        out Player? defender,
        out Player? majorAlly,
        out Province? province,
        out string reason)
    {
        defender = null;
        majorAlly = null;
        province = null;
        reason = string.Empty;

        if (GameManager.IsBattle || CampaignController.Instance?.CampaignData == null)
        {
            reason = "Invasions can only be launched from the campaign map.";
            return InvasionTargetStatus.HardBlocked;
        }
        if (attacker == null) { reason = "No attacking player."; return InvasionTargetStatus.HardBlocked; }
        if (target == null)   { reason = "No target port.";      return InvasionTargetStatus.HardBlocked; }

        province = target.CurrentProvince;
        if (province == null) { reason = "Target port has no province."; return InvasionTargetStatus.HardBlocked; }

        defender = province.ControllerPlayer;
        if (defender == null)   { reason = "Target province has no controller.";    return InvasionTargetStatus.HardBlocked; }
        if (defender == attacker) { reason = "Cannot invade your own port.";        return InvasionTargetStatus.HardBlocked; }

        majorAlly = ResolveMajorAlly(defender, province) ?? defender;

        // Block only if another NAVAL invasion is already in progress at
        // this province (vanilla picks its own port within the province,
        // so check province-wide). A concurrent LAND battle is fine —
        // naval + land are complementary attack vectors and vanilla
        // handles them through separate systems.
        if (CountConquestEventsAtProvince(province) > 0)
        {
            reason = "A naval invasion is already in progress in this province.";
            return InvasionTargetStatus.HardBlocked;
        }

        // Tonnage check is SOFT-blocked: target is valid, the player just
        // needs to bring ships into the area before the button activates.
        try
        {
            Area? area = province.CurrentArea;
            if (area != null)
            {
                float currentTonnage = CampaignController.Instance.AreaCurrentTonnage(area, attacker);
                if (currentTonnage < MinAttackerAreaTonnageForNaval)
                {
                    reason = $"You need at least {MinAttackerAreaTonnageForNaval:N0} tons of shipping in " +
                             $"the {area.Id} area to launch a naval invasion (you have {currentTonnage:N0}).";
                    return InvasionTargetStatus.SoftBlocked;
                }
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP naval-invasion: AreaCurrentTonnage check threw, allowing invasion. {ex.GetType().Name}: {ex.Message}");
        }

        return InvasionTargetStatus.Allowed;
    }

    internal static bool LaunchNavalInvasion(Player attacker, PortElement target)
    {
        if (!CanLaunchNavalInvasion(attacker, target,
                out Player? defender, out Player? majorAlly, out Province? province, out string reason))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP naval-invasion: refused. {reason}");
            return false;
        }

        // Break any alliance with the defender FIRST so the war-forcing
        // step below isn't operating on a stale Relation flagged as allied.
        BreakAllianceIfPresent(attacker, defender!, "naval-invasion");

        bool wasAlreadyAtWar = ResolveWarStateAndForceIfNeeded(
            attacker, defender!, majorAlly!, "naval-invasion");

        // FIRST: try vanilla's own naval-invasion entry point. CheckNavalInvasions
        // is the AI's per-turn invasion-decision method — if vanilla runs its
        // own creation logic, the conquest event lands in SpecialEvents with
        // vanilla-computed tonnage, fully canonical. If vanilla doesn't act
        // (conditions not met from its POV — peacetime AI guards, etc.), we
        // fall back to a manual CreateConquestEvent call with our heuristic.
        //
        // Province-level (not port-level) detection: vanilla picks ITS own
        // port within the target province, which may differ from the one
        // the player clicked. Count province-wide so we don't create a
        // duplicate.
        int conquestCountBefore = CountConquestEventsAtProvince(province!);
        bool vanillaCreated = TryVanillaNavalInvasion(attacker, defender!, province!, conquestCountBefore);

        if (!vanillaCreated)
        {
            int taskForceTonnage = ComputeRequiredTonnage(defender!, province!);
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP naval-invasion: vanilla path didn't create event; calling CreateConquestEvent manually " +
                $"attacker={attacker.Name(false)} defender={defender!.Name(false)} " +
                $"majorAlly={majorAlly!.Name(false)} province={province!.Id} port={target.Id} " +
                $"requiredTonnage={taskForceTonnage} priorWar={wasAlreadyAtWar}.");

            try
            {
                CampaignController.Instance.CreateConquestEvent(
                    attacker, defender, majorAlly, taskForceTonnage, province);
            }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP naval-invasion: CreateConquestEvent threw. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Either path: try the refresh + apply auto-battle flags to the new event.
        RefreshConquestEventTonnage(attacker, target);

        ApplyConsequencesIfPeacetime(attacker, defender!, province!, wasAlreadyAtWar, "naval-invasion");
        TryRefreshUi();
        return true;
    }

    // Seeds the required-tonnage value via vanilla tonnage methods. AreaRequiredTonnage
    // has an IL2CPP-internal NRE in some campaign states, so we try a cascade:
    //   1. NeededTonnage(area, defender, forShips=false) — alt vanilla method
    //   2. AreaExpectedTonnage(area, defender)            — alt vanilla method
    //   3. AreaCurrentTonnage(area, defender)             — actual defender presence
    //   4. DefaultRequiredTonnage                          — hard fallback
    // After CreateConquestEvent runs, RefreshConquestEventTonnage calls vanilla's
    // CheckEventRequiredTonnageFor on the resulting event so the final
    // RequiredTonnage matches whatever vanilla would compute for that event.
    private static int ComputeRequiredTonnage(Player defender, Province province)
    {
        Area? area = province.CurrentArea;
        CampaignController? cc = CampaignController.Instance;
        if (area == null || cc == null) return DefaultRequiredTonnage;

        int? result = TryTonnage("NeededTonnage", () => cc.NeededTonnage(area, defender, false), area);
        if (result.HasValue) return result.Value;

        result = TryTonnage("AreaExpectedTonnage", () => cc.AreaExpectedTonnage(area, defender), area);
        if (result.HasValue) return result.Value;

        result = TryTonnage("AreaRequiredTonnage", () => cc.AreaRequiredTonnage(area, defender), area);
        if (result.HasValue) return result.Value;

        result = TryTonnage("AreaCurrentTonnage(defender)", () => cc.AreaCurrentTonnage(area, defender), area);
        if (result.HasValue) return result.Value;

        // Heuristic fallback: use the province's port-size value as the
        // required tonnage. The CSV's port column is tonnage-denominated
        // (Cuba=75000, Kingston=7000, Vancouver=…). Bigger ports = harder
        // to overcome. Better than a flat 25k for any province with a port.
        try
        {
            float portSize = province.Port;
            if (portSize > 0f)
            {
                int rounded = (int)Math.Round(portSize);
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP naval-invasion: province.Port heuristic ({province.Id}) = {portSize:0} → using {rounded} tons.");
                return rounded;
            }
        }
        catch { }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP naval-invasion: all vanilla tonnage methods unavailable; using default {DefaultRequiredTonnage} tons.");
        return DefaultRequiredTonnage;
    }

    // Attempt vanilla's CheckNavalInvasions path. Returns true if a new
    // conquest event landed in SpecialEvents at our target port between the
    // before and after counts. CheckNavalInvasions encapsulates vanilla's
    // full invasion decision logic — including tonnage computation — so
    // when it does create the event, the result has canonical vanilla
    // values for RequiredTonnage and the rest of the event state.
    //
    // CheckNavalInvasions(owner, enemy, province) — best inference of arg
    // semantics: owner is the *defender* (province controller), enemy is
    // the attacker considering the invasion. Tries both orderings if the
    // first doesn't fire.
    private static bool TryVanillaNavalInvasion(Player attacker, Player defender, Province province, int conquestCountBefore)
    {
        CampaignController? cc = CampaignController.Instance;
        if (cc == null) return false;

        // Attempt 1: (owner=defender, enemy=attacker) — vanilla AI's perspective:
        // "defender is the area owner, attacker is the threatening enemy".
        if (TryCheckNavalInvasions(cc, defender, attacker, province, "defender-as-owner")
            && CountConquestEventsAtProvince(province) > conquestCountBefore)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                "UADVP naval-invasion: vanilla CheckNavalInvasions(owner=defender, enemy=attacker) created the event.");
            return true;
        }

        // Attempt 2: (owner=attacker, enemy=defender) — opposite ordering.
        if (TryCheckNavalInvasions(cc, attacker, defender, province, "attacker-as-owner")
            && CountConquestEventsAtProvince(province) > conquestCountBefore)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                "UADVP naval-invasion: vanilla CheckNavalInvasions(owner=attacker, enemy=defender) created the event.");
            return true;
        }

        return false;
    }

    private static bool TryCheckNavalInvasions(CampaignController cc, Player owner, Player enemy, Province province, string label)
    {
        try
        {
            cc.CheckNavalInvasions(owner, enemy, province);
            return true;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP naval-invasion: CheckNavalInvasions({label}) threw. {ex.GetType().Name}: {ex.Message?.Split('\n')[0]}");
            return false;
        }
    }

    private static int CountConquestEventsAtProvince(Province province)
    {
        try
        {
            var events = CampaignController.Instance?.CampaignData?.SpecialEvents;
            if (events == null) return 0;
            int count = 0;
            foreach (BaseCampaignSpecialEvent evt in events)
            {
                CampaignConquestEvent? c = evt?.TryCast<CampaignConquestEvent>();
                if (c != null && c.EnemyProvince == province) count++;
            }
            return count;
        }
        catch { return 0; }
    }

    private static void TryRefreshTonnage(CampaignConquestEvent evt, PlayerData playerData, string label)
    {
        try
        {
            CampaignController.Instance?.CheckEventRequiredTonnageFor(playerData, evt);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP naval-invasion: CheckEventRequiredTonnageFor({label}) threw. {ex.GetType().Name}: {ex.Message?.Split('\n')[0]}");
        }
    }

    private static int? TryTonnage(string label, Func<float> getter, Area area)
    {
        try
        {
            float t = getter();
            if (t > 0f)
            {
                int rounded = (int)Math.Round(t);
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP naval-invasion: {label}({area.Id}) = {t:0} → using {rounded} tons.");
                return rounded;
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP naval-invasion: {label} threw, trying next. {ex.GetType().Name}: {ex.Message?.Split('\n')[0]}");
        }
        return null;
    }

    // Finds the conquest event we just created and runs vanilla's tonnage
    // refresh on it. Looks for our exact (attacker, port) tuple in
    // CampaignData.SpecialEvents. Tries the refresh with BOTH attacker and
    // defender player data because vanilla's intent for the playerData arg
    // isn't documented — one of the two perspectives might trigger an
    // actual recompute. Silent if the event isn't found.
    private static void RefreshConquestEventTonnage(Player attacker, PortElement port)
    {
        try
        {
            var events = CampaignController.Instance?.CampaignData?.SpecialEvents;
            if (events == null) return;

            CampaignConquestEvent? found = null;
            foreach (BaseCampaignSpecialEvent evt in events)
            {
                CampaignConquestEvent? c = evt?.TryCast<CampaignConquestEvent>();
                if (c == null) continue;
                if (c.EnemyPort == port) { found = c; }  // last match wins
            }
            if (found == null) return;

            int before = found.RequiredTonnage;
            TryRefreshTonnage(found, attacker.data, "attacker");

            // If the attacker perspective didn't move the needle, try the
            // defender (the event's MajorAlly PlayerData).
            if (found.RequiredTonnage == before && found.MajorAlly != null)
                TryRefreshTonnage(found, found.MajorAlly, "defender/majorAlly");

            int after = found.RequiredTonnage;
            if (before != after)
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP naval-invasion: CheckEventRequiredTonnageFor refined requiredTonnage {before} → {after}.");

            // NOTE: Earlier this code force-set GenerateBattleWhenEnterRadius
            // and CheckBattle to true. Removed because forcing CheckBattle
            // bypasses a vanilla guard — when CheckEventProgress runs with
            // CheckBattle=true before vanilla's setup is complete, it can
            // call CreateMinorNationFleet on a minor list with null entries
            // and crash. Vanilla flips CheckBattle on its own when ready
            // (forces in radius, etc.). GenerateBattleWhenEnterRadius is
            // already True by vanilla default on a freshly created event,
            // so we just trust vanilla here.
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP naval-invasion: tonnage refresh failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -- Land ---------------------------------------------------------------

    internal static bool CanLaunchLandInvasion(
        Player? attacker,
        Province? target,
        out Player? defender,
        out Province? attackerProvince,
        out string reason)
    {
        return CheckLandInvasion(attacker, target, out defender, out attackerProvince, out reason)
            == InvasionTargetStatus.Allowed;
    }

    internal static InvasionTargetStatus CheckLandInvasion(
        Player? attacker,
        Province? target,
        out Player? defender,
        out Province? attackerProvince,
        out string reason)
    {
        defender = null;
        attackerProvince = null;
        reason = string.Empty;

        if (GameManager.IsBattle || CampaignController.Instance?.CampaignData == null)
        {
            reason = "Invasions can only be launched from the campaign map.";
            return InvasionTargetStatus.HardBlocked;
        }
        if (attacker == null) { reason = "No attacking player."; return InvasionTargetStatus.HardBlocked; }
        if (target == null)   { reason = "No target province.";  return InvasionTargetStatus.HardBlocked; }

        defender = target.ControllerPlayer;
        if (defender == null)     { reason = "Target province has no controller.";   return InvasionTargetStatus.HardBlocked; }
        if (defender == attacker) { reason = "Cannot invade your own territory.";    return InvasionTargetStatus.HardBlocked; }

        // No-land-border is HARD: clicking an island province is conceptually
        // not a land-invasion target at all, no amount of player action makes
        // it valid.
        attackerProvince = FindBorderingAttackerProvince(attacker, target);
        if (attackerProvince == null)
        {
            reason = "You have no province sharing a land border with this territory.";
            return InvasionTargetStatus.HardBlocked;
        }

        // Block only if vanilla considers this exact (attacker/defender/
        // attackerProvince/target) battle already to be in progress. Using
        // vanilla's HasEqualBattle gives us natural re-invasion-after-failure:
        // when a battle resolves via vanilla's ResilientDefense → EndBattle →
        // RemoveBattle path, HasEqualBattle returns false again and the next
        // attempt is allowed. (Province.BattleStatus was sticky on at least
        // some failure paths, which left re-attempts permanently blocked.)
        //
        // Pass the fully-formed probe (matching the shape used at launch
        // time) so HasEqualBattle compares apples to apples — a probe with
        // a null AttackerProvince produced false positives for some defenders.
        if (HasExistingProvinceBattle(attacker, defender, attackerProvince, target))
        {
            reason = "A land battle against this defender is already in progress here.";
            return InvasionTargetStatus.HardBlocked;
        }

        return InvasionTargetStatus.Allowed;
    }

    internal static bool LaunchLandInvasion(Player attacker, Province target)
    {
        if (!CanLaunchLandInvasion(attacker, target,
                out Player? defender, out Province? attackerProvince, out string reason))
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP land-invasion: refused. {reason}");
            return false;
        }

        // For land invasions we also resolve the major-ally so we can drive
        // war against the protecting major when invading a minor.
        Player majorAlly = ResolveMajorAlly(defender!, target) ?? defender!;

        // Break any alliance with the defender FIRST so the war-forcing step
        // operates on a relation that's already been demoted from "allied".
        BreakAllianceIfPresent(attacker, defender!, "land-invasion");

        bool wasAlreadyAtWar = ResolveWarStateAndForceIfNeeded(
            attacker, defender!, majorAlly, "land-invasion");

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP land-invasion: creating ProvinceBattle attacker={attacker.Name(false)} " +
            $"defender={defender!.Name(false)} majorAlly={majorAlly.Name(false)} " +
            $"from={attackerProvince!.Id} to={target.Id} priorWar={wasAlreadyAtWar}.");

        try
        {
            ProvinceBattle battle = new ProvinceBattle
            {
                Attacker = attacker,
                Defender = defender,
                AttackerProvince = attackerProvince,
                DefenderProvince = target,
                Advance = 0f,
                // Initialize the per-player army-force dictionary so vanilla's
                // per-turn ProvinceBattleManager iteration doesn't NRE on a
                // null dict. Vanilla AI-created battles have this populated
                // by their own init path; we need to match that contract.
                PlayerArmyForce = new Il2CppSystem.Collections.Generic.Dictionary<Player, float>(),
                AttackerKills = 0,
                DefenderKills = 0,
                AttackerLosses = 0,
                DefenderLosses = 0,
                RedAdvanceTurns = 0,
            };

            // Pre-check vanilla's notion of a same-attacker conflict. Log
            // both flags. If vanilla refuses duplicates internally based on
            // SameAttacker, we want to know up front instead of having
            // AddBattle silently fail.
            bool hasEqual = false, sameAttacker = false;
            try { hasEqual = ProvinceBattleManager.HasEqualBattle(battle); } catch { }
            try { sameAttacker = ProvinceBattleManager.SameAttacker(battle); } catch { }
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP land-invasion: pre-AddBattle vanilla checks — HasEqualBattle={hasEqual} SameAttacker={sameAttacker}.");

            if (hasEqual)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP land-invasion: vanilla HasEqualBattle says this attacker/defender pair " +
                    $"already has an equivalent battle; aborting to avoid duplication.");
                return false;
            }

            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP land-invasion: calling ProvinceBattleManager.AddBattle…");
            try
            {
                ProvinceBattleManager.AddBattle(battle);
            }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP land-invasion: AddBattle threw. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP land-invasion: AddBattle returned; calling AddArrow…");

            // AddBattle only enrols the data record; the Flag/Line/Line2/End
            // GameObjects (the on-map arrow + flag) are created by AddArrow.
            // Without this call, MapUI.LateUpdate → UpdateFlagScale NRE's on
            // the null battle.Flag.
            try
            {
                ProvinceBattleManager.AddArrow(battle);
            }
            catch (Exception ex)
            {
                Melon<UADVanillaPlusMod>.Logger.Warning(
                    $"UADVP land-invasion: AddArrow failed; removing the just-added battle to avoid " +
                    $"a LateUpdate crash. {ex.GetType().Name}: {ex.Message}");
                try { ProvinceBattleManager.RemoveBattle(battle); }
                catch { }
                return false;
            }
            Melon<UADVanillaPlusMod>.Logger.Msg("UADVP land-invasion: AddArrow returned; running diplomatic consequences…");
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP land-invasion: ProvinceBattleManager.AddBattle threw. {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        ApplyConsequencesIfPeacetime(attacker, defender, target, wasAlreadyAtWar, "land-invasion");
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP land-invasion: consequences applied; calling TryRefreshUi…");
        TryRefreshUi();
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP land-invasion: COMPLETED attacker={attacker.Name(false)} target={target.Id}.");
        return true;
    }

    // -- Shared helpers -----------------------------------------------------

    // Returns whether the attacker was already at war with the defender (or
    // the minor's major-ally protector). When at peace, this forces war —
    // against the major-ally protector if the defender is an unprotected
    // minor with no direct relation to the attacker; against the defender
    // otherwise.
    private static bool ResolveWarStateAndForceIfNeeded(
        Player attacker, Player defender, Player majorAlly, string source)
    {
        var relations = CampaignController.Instance?.CampaignData?.Relations;
        if (relations == null) return false;

        Relation? defenderRel = RelationExt.Between(relations, attacker, defender);
        Relation? backerRel = (majorAlly != defender)
            ? RelationExt.Between(relations, attacker, majorAlly)
            : null;

        bool defenderAtWar = defenderRel != null && defenderRel.isWar;
        bool backerAtWar = backerRel != null && backerRel.isWar;
        bool alreadyAtWar = defenderAtWar || backerAtWar;

        if (alreadyAtWar)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP {source}: skipping war trigger; already at war " +
                $"(defenderAtWar={defenderAtWar}, backerAtWar={backerAtWar}).");
            return true;
        }

        // Prefer to force war against the major-ally (the real geopolitical
        // actor) when the defender is a minor with a protector. Falls back to
        // forcing war against the defender if there's no protector or the
        // protector relation is missing too.
        if (backerRel != null)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP {source}: minor {defender.Name(false)} is backed by {majorAlly.Name(false)}; " +
                "forcing war against the backer.");
            ForceWarWithDefender(attacker, majorAlly, backerRel, source);
            return false;
        }

        if (defenderRel != null)
        {
            ForceWarWithDefender(attacker, defender, defenderRel, source);
            return false;
        }

        // No relation with defender and no major-ally relation either. This is
        // an unprotected minor — invade without a formal war declaration.
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP {source}: {defender.Name(false)} is an unprotected minor with no relation entry; " +
            "proceeding without war declaration.");
        return false;
    }

    private static bool ForceWarWithDefender(Player attacker, Player defender, Relation? relation, string source)
    {
        if (relation == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP {source}: cannot force war; no relation between {attacker.Name(false)} and {defender.Name(false)}.");
            return false;
        }

        float beforeAttitude = relation.attitude;
        bool beforeWar = relation.isWar;

        try
        {
            CampaignController.Instance.AdjustAttitude(
                relation,
                -200f,
                true,        // canFullyAdjust
                false,       // init
                $"UADVP {source} declares war on defender",
                true,        // raiseEvents
                true,        // force
                false);      // fromCommonEnemy

            ActionsManager.ChoosenAction = ActionsManager.ActionType.IncreaseTension;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP {source}: AdjustAttitude failed forcing war on {defender.Name(false)}. " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP {source}: forced war {attacker.Name(false)} → {defender.Name(false)}; " +
            $"attitude {beforeAttitude:0.0}→{relation.attitude:0.0}, war {beforeWar}→{relation.isWar}.");

        return relation.isWar;
    }

    private static void ApplyConsequencesIfPeacetime(
        Player attacker, Player defender, Province province, bool wasAlreadyAtWar, string source)
    {
        if (wasAlreadyAtWar) return;
        try
        {
            InvasionDiplomaticConsequences.ApplyForInvasion(attacker, defender, province);
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP {source}: diplomatic consequences threw. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // True iff there's a Relation between attacker and defender with
    // isAlliance set. Covers both major-major and major-minor alliances —
    // vanilla uses the same Relation flag for either.
    internal static bool IsAllied(Player? attacker, Player? defender)
    {
        if (attacker == null || defender == null) return false;
        try
        {
            var relations = CampaignController.Instance?.CampaignData?.Relations;
            if (relations == null) return false;
            Relation? rel = RelationExt.Between(relations, attacker, defender);
            return rel != null && rel.isAlliance;
        }
        catch { return false; }
    }

    // Breaks the alliance between attacker and defender (clears isAlliance
    // on the Relation) and applies an extra reputation penalty against the
    // attacker — invading a sworn ally is far worse than invading a non-allied
    // neutral. Returns true if an alliance was actually broken (false if there
    // was no alliance to break).
    internal static bool BreakAllianceIfPresent(Player attacker, Player defender, string source)
    {
        var relations = CampaignController.Instance?.CampaignData?.Relations;
        if (relations == null) return false;

        Relation? rel = RelationExt.Between(relations, attacker, defender);
        if (rel == null || !rel.isAlliance) return false;

        float beforeAttitude = rel.attitude;
        try
        {
            rel.isAlliance = false;
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP {source}: clearing isAlliance threw. {ex.GetType().Name}: {ex.Message}");
        }

        // Extra reputation hit for attacking an ally — large, on top of the
        // war declaration's own attitude swing. -75 is enough to nuke the
        // relationship even if attitude was near +100 before.
        try
        {
            CampaignController.Instance?.AdjustAttitude(
                rel,
                -75f,
                true,        // canFullyAdjust
                false,       // init
                $"UADVP {source}: invaded sworn ally",
                true,        // raiseEvents
                true,        // force
                false);      // fromCommonEnemy
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP {source}: alliance-break attitude hit threw. {ex.GetType().Name}: {ex.Message}");
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP {source}: BROKE ALLIANCE with {defender.Name(false)}; " +
            $"isAlliance→false, attitude {beforeAttitude:0.0}→{rel.attitude:0.0}.");
        return true;
    }

    private static void TryRefreshUi()
    {
        try { G.ui.RefreshCampaignUI(); }
        catch { /* refresh failures are cosmetic only */ }
    }

    // Walks the major nations and returns the one whose homeProvincesMinor list
    // covers this province. Falls back to the defender itself when the defender
    // is already a major, which matches vanilla's data shape (MajorAlly == self).
    private static Player? ResolveMajorAlly(Player defender, Province province)
    {
        if (defender.isMajor) return defender;

        if (CampaignController.Instance?.CampaignData?.Players == null) return null;

        foreach (Player candidate in CampaignController.Instance.CampaignData.Players)
        {
            if (candidate == null || !candidate.isMajor) continue;
            Il2CppSystem.Collections.Generic.List<Province>? minorHome = candidate.homeProvincesMinor;
            if (minorHome == null) continue;
            foreach (Province p in minorHome)
                if (p == province) return candidate;
        }

        return null;
    }

    // Returns one of the attacker's provinces that shares a *mutual* land
    // border with the target. Requires both directions of adjacency to match
    // (target.NeighbourProvinces contains mine AND mine.NeighbourProvinces
    // contains target) — guards against asymmetric runtime adjacency data
    // that could include sea-reachable provinces in one direction only.
    private static Province? FindBorderingAttackerProvince(Player attacker, Province target)
    {
        Il2CppSystem.Collections.Generic.List<Province>? mine = attacker.provinces;
        if (mine == null) return null;

        Il2CppSystem.Collections.Generic.List<Province>? targetNeighbours = target.NeighbourProvinces;
        if (targetNeighbours == null || targetNeighbours.Count == 0)
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP land-invasion: {target.Id} has no land neighbours (likely an isolated island); blocking.");
            return null;
        }

        foreach (Province p in mine)
        {
            if (p == null) continue;
            bool targetListsMe = false;
            foreach (Province n in targetNeighbours)
                if (n == p) { targetListsMe = true; break; }
            if (!targetListsMe) continue;

            // Confirm the reverse: my province also lists target as a neighbour.
            Il2CppSystem.Collections.Generic.List<Province>? myNeighbours = p.NeighbourProvinces;
            if (myNeighbours == null) continue;
            foreach (Province mn in myNeighbours)
                if (mn == target)
                {
                    Melon<UADVanillaPlusMod>.Logger.Msg(
                        $"UADVP land-invasion: mutual land border confirmed {p.Id} ↔ {target.Id}.");
                    return p;
                }
        }

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP land-invasion: no mutual land border between attacker and {target.Id}; blocking.");
        return null;
    }

    // Cache so we don't log "still in progress" every hover frame for the
    // same (attacker, defender, province) triple.
    private static (Player? a, Player? d, Province? p, bool result) _hasBattleLastResult;

    private static bool HasExistingProvinceBattle(
        Player attacker, Player defender, Province? attackerProvince, Province target)
    {
        bool result;
        try
        {
            ProvinceBattle probe = new ProvinceBattle
            {
                Attacker = attacker,
                Defender = defender,
                AttackerProvince = attackerProvince!,
                DefenderProvince = target,
            };
            result = ProvinceBattleManager.HasEqualBattle(probe);
        }
        catch (Exception ex)
        {
            // If vanilla's check throws, fail OPEN — better to let the user
            // click and have launch-time validation catch it than to silently
            // hide the button. Log so we know when this path triggers.
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP land-invasion: HasEqualBattle threw for " +
                $"{attacker.Name(false)}→{defender.Name(false)}@{target.Id} " +
                $"({ex.GetType().Name}); treating as no-existing-battle.");
            return false;
        }

        if (result &&
            (_hasBattleLastResult.a != attacker
             || _hasBattleLastResult.d != defender
             || _hasBattleLastResult.p != target
             || !_hasBattleLastResult.result))
        {
            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP land-invasion: HasEqualBattle=TRUE for " +
                $"{attacker.Name(false)}→{defender.Name(false)}@{target.Id}; " +
                $"button will be hidden until the battle resolves.");
        }
        _hasBattleLastResult = (attacker, defender, target, result);
        return result;
    }

}
