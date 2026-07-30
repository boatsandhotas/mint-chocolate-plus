using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.Harmony;

// Patch intent: log design-side accuracy at the moment a campaign battle is
// accepted, before the battle scene and live Ship.HitChance code are involved.
// This keeps the diagnostic near "battle start" while avoiding the unsafe
// combat hit-chance detour that caused CoreCLR access violations.
[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.AcceptBattle))]
internal static class BattleStartAccuracyBreakdownPatch
{
    private static readonly HashSet<string> LoggedBattles = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedCustomPendingContexts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedCustomProbeKeys = new(StringComparer.Ordinal);
    private static string? loggedCustomBattleSignature;
    private static bool pendingCustomBattle;
    private static string? pendingCustomBattleContext;

    [HarmonyPrefix]
    private static void PrefixAcceptBattle(CampaignBattle battle, bool autoResolve)
    {
        if (battle == null || autoResolve || !LoggedBattles.Add(battle.Id.ToString()))
            return;

        try
        {
            CampaignBattle acceptedBattle = battle;
            LogBattleSide("attacker", acceptedBattle.AttackerShips);
            LogBattleSide("defender", acceptedBattle.DefenderShips);
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC battle-start accuracy breakdown failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void LogCustomBattleOnBattleState(string trigger = "unknown")
    {
        if (!IsCustomBattleState())
            return;

        try
        {
            List<Ship> ships = RuntimeBattleShips();
            string signature = CustomBattleSignature(ships);
            LogCustomBattleProbe(trigger, true, ships.Count, signature);
            if (string.IsNullOrWhiteSpace(signature) || string.Equals(signature, loggedCustomBattleSignature, StringComparison.Ordinal))
                return;

            loggedCustomBattleSignature = signature;
            pendingCustomBattle = false;
            SplitCustomBattleShips(ships, out List<Ship> attacker, out List<Ship> defender);
            LogBattleSide("attacker", attacker, "custom");
            LogBattleSide("defender", defender, "custom");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC custom battle-start accuracy breakdown failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ResetCustomBattleSummary()
    {
        loggedCustomBattleSignature = null;
        pendingCustomBattle = false;
        pendingCustomBattleContext = null;
        LoggedCustomPendingContexts.Clear();
        LoggedCustomProbeKeys.Clear();
    }

    internal static void MarkPendingCustomBattle(string context)
    {
        pendingCustomBattle = true;
        pendingCustomBattleContext = context;
        if (LoggedCustomPendingContexts.Add(context))
            Melon<MintChipPlusMod>.Logger.Msg($"UADMC custom battle-start accuracy pending: context={context}.");
    }

    private static void LogCustomBattleProbe(string trigger, bool isCustom, int shipCount, string signature)
    {
        string compactSignature = CompactSignature(signature);
        string key = $"{trigger}|{isCustom}|{shipCount}|{compactSignature}";
        if (!LoggedCustomProbeKeys.Add(key))
            return;

        Melon<MintChipPlusMod>.Logger.Msg(
            $"UADMC custom battle-start accuracy probe: trigger={trigger} custom={isCustom} ships={shipCount} signature={compactSignature}.");
    }

    private static string CompactSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return "none";

        string sanitized = signature.Replace(' ', '_');
        return sanitized.Length <= 160 ? sanitized : sanitized[..160] + "...";
    }

    private static void LogBattleSide(string side, Il2CppSystem.Collections.Generic.List<Ship>? ships)
    {
        AccuracySideSummary summary = new(side);
        if (ships == null)
        {
            Melon<MintChipPlusMod>.Logger.Msg(summary.Format());
            return;
        }

        foreach (Ship ship in ships)
        {
            if (ship != null)
                summary.Add(ship);
        }

        Melon<MintChipPlusMod>.Logger.Msg(summary.Format());
    }

    private static void LogBattleSide(string side, IEnumerable<Ship> ships, string mode)
    {
        AccuracySideSummary summary = new(side, mode);
        foreach (Ship ship in ships)
        {
            if (ship != null)
                summary.Add(ship);
        }

        Melon<MintChipPlusMod>.Logger.Msg(summary.Format());
    }

    private static bool IsCustomBattleState()
    {
        if (pendingCustomBattle)
            return true;

        try
        {
            CampaignBattle? currentBattle = BattleManager.Instance?.CurrentBattle;
            if (currentBattle != null && currentBattle.IsCampaignBattle)
                return false;
        }
        catch
        {
        }

        try
        {
            if (BattleManager.Instance?.CurrentCustomBattle != null)
                return true;
        }
        catch
        {
        }

        try
        {
            return GameManager.IsCustomBattle;
        }
        catch
        {
            return false;
        }
    }

    private static List<Ship> RuntimeBattleShips()
    {
        List<Ship> ships = new();
        try
        {
            if (Ship.AllShips == null)
                return ships;

            foreach (Ship ship in Ship.AllShips)
            {
                if (ship == null ||
                    SafeBool(() => ship.isDesign) ||
                    SafeBool(() => ship.isErased) ||
                    SafeBool(() => ship.isSunk))
                {
                    continue;
                }

                ships.Add(ship);
            }
        }
        catch
        {
            ships.Clear();
        }

        return ships;
    }

    private static void SplitCustomBattleShips(List<Ship> ships, out List<Ship> attacker, out List<Ship> defender)
    {
        attacker = new List<Ship>();
        defender = new List<Ship>();
        if (ships.Count == 0)
            return;

        Player? attackerPlayer = ships
            .Select(SafePlayer)
            .Where(player => player != null)
            .Distinct()
            .OrderByDescending(IsMainPlayer)
            .ThenBy(PlayerLabel, StringComparer.Ordinal)
            .FirstOrDefault();

        if (attackerPlayer == null)
        {
            attacker.Add(ships[0]);
            defender.AddRange(ships.Skip(1));
            return;
        }

        foreach (Ship ship in ships)
        {
            Player? player = SafePlayer(ship);
            if (ReferenceEquals(player, attackerPlayer) || SamePlayer(player, attackerPlayer))
                attacker.Add(ship);
            else
                defender.Add(ship);
        }
    }

    private static string CustomBattleSignature(List<Ship> ships)
    {
        if (ships.Count == 0)
            return $"empty:{pendingCustomBattleContext ?? "custom"}";

        return string.Join(
            "|",
            ships
                .Select(ship => $"{SafePlayerName(ship)}:{SafeShipType(ship)}:{SafeShipName(ship)}:{SafeShipId(ship)}")
                .OrderBy(static item => item, StringComparer.Ordinal));
    }

    private sealed class AccuracySideSummary
    {
        private readonly string side;
        private readonly string? mode;
        private readonly EffectRange accuracy = new();
        private readonly EffectRange accuracyLong = new();
        private readonly EffectRange accuracyWaves = new();
        private readonly EffectRange accuracyCruise = new();
        private int shipCount;
        private int towerCount;
        private int funnelCount;
        private float accuracySum;
        private float accuracyLongSum;
        private float accuracyWavesSum;
        private float accuracyCruiseSum;
        private float stabilitySum;
        private float beamSum;
        private float draughtSum;
        private float overweightSum;

        internal AccuracySideSummary(string side, string? mode = null)
        {
            this.side = side;
            this.mode = mode;
        }

        internal void Add(Ship ship)
        {
            shipCount++;
            string shipLabel = SafeShipLabel(ship);

            AddEffect(accuracy, ref accuracySum, SafeStatEffect(ship, "accuracy"), shipLabel);
            AddEffect(accuracyLong, ref accuracyLongSum, SafeStatEffect(ship, "accuracy_long"), shipLabel);
            AddEffect(accuracyWaves, ref accuracyWavesSum, SafeStatEffect(ship, "accuracy_waves"), shipLabel);
            AddEffect(accuracyCruise, ref accuracyCruiseSum, SafeStatEffect(ship, "accuracy_cruise"), shipLabel);

            stabilitySum += SafeStat(ship, "stability");
            beamSum += SafeStat(ship, "beam");
            draughtSum += SafeStat(ship, "draught");
            overweightSum += SafeStat(ship, "overweight");

            CountAccuracyParts(ship, out int towers, out int funnels);
            towerCount += towers;
            funnelCount += funnels;
        }

        internal string Format()
        {
            string prefix = string.IsNullOrWhiteSpace(mode)
                ? $"UADMC battle-start accuracy summary: side={side}"
                : $"UADMC battle-start accuracy summary: mode={mode} side={side}";

            if (shipCount == 0)
                return $"{prefix}, ships=0.";

            return $"{prefix}, ships={shipCount}; " +
                $"accuracy avg={FormatPercentFromOne(Average(accuracySum))} worst={accuracy.MinText()} best={accuracy.MaxText()}; " +
                $"long avg={FormatPercentFromOne(Average(accuracyLongSum))}; " +
                $"waves avg={FormatPercentFromOne(Average(accuracyWavesSum))} peak={accuracyWaves.MaxText()}; " +
                $"cruise avg={FormatPercentFromOne(Average(accuracyCruiseSum))} worst={accuracyCruise.MinText()}; " +
                $"hull avg stability={FormatFloat(Average(stabilitySum))}, beam={FormatFloat(Average(beamSum))}, draught={FormatFloat(Average(draughtSum))}, overweight={FormatFloat(Average(overweightSum))}; " +
                $"avg towers={FormatFloat((float)towerCount / shipCount)}, funnels={FormatFloat((float)funnelCount / shipCount)}.";
        }

        private void AddEffect(EffectRange range, ref float sum, float value, string shipLabel)
        {
            sum += value;
            range.Add(value, shipLabel);
        }

        private float Average(float sum)
            => sum / shipCount;
    }

    private sealed class EffectRange
    {
        private bool hasValue;
        private float min;
        private float max;
        private string minShip = string.Empty;
        private string maxShip = string.Empty;

        internal void Add(float value, string shipLabel)
        {
            if (!hasValue)
            {
                hasValue = true;
                min = value;
                max = value;
                minShip = shipLabel;
                maxShip = shipLabel;
                return;
            }

            if (value < min)
            {
                min = value;
                minShip = shipLabel;
            }

            if (value > max)
            {
                max = value;
                maxShip = shipLabel;
            }
        }

        internal string MinText()
            => hasValue ? $"{FormatPercentFromOne(min)} {minShip}" : "n/a";

        internal string MaxText()
            => hasValue ? $"{FormatPercentFromOne(max)} {maxShip}" : "n/a";
    }

    private static float SafeStat(Ship ship, string stat)
    {
        try
        {
            if (G.GameData?.stats == null || ship.stats == null || !G.GameData.stats.TryGetValue(stat, out StatData statData))
                return 1f;

            return ship.stats.TryGetValue(statData, out Ship.StatValue statValue) ? statValue.total : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    private static float SafeStatEffect(Ship ship, string effect)
    {
        try
        {
            return ship.StatEffect(effect);
        }
        catch
        {
            return 1f;
        }
    }

    private static void CountAccuracyParts(Ship ship, out int towerCount, out int funnelCount)
    {
        towerCount = 0;
        funnelCount = 0;

        try
        {
            Il2CppSystem.Collections.Generic.List<Part>? parts = ship.parts;
            if (parts == null)
                return;

            foreach (Part part in parts)
            {
                PartData? data = part?.data;
                if (data == null)
                    continue;

                if (data.isFunnel)
                    funnelCount++;
                if (data.isTowerAny)
                    towerCount++;
            }
        }
        catch
        {
            towerCount = 0;
            funnelCount = 0;
        }
    }

    private static string SafeShipLabel(Ship ship)
        => $"{SafeShipType(ship)} {SafeShipName(ship)}";

    private static string SafeShipName(Ship ship)
    {
        try
        {
            return ship.Name(false, false, false, false, true);
        }
        catch
        {
            return "<ship>";
        }
    }

    private static string SafePlayerName(Ship ship)
    {
        try
        {
            return ship.player?.Name(false) ?? "<player>";
        }
        catch
        {
            return "<player>";
        }
    }

    private static Player? SafePlayer(Ship ship)
    {
        try
        {
            return ship.player;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMainPlayer(Player? player)
    {
        try
        {
            return player?.isMain ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePlayer(Player? left, Player? right)
    {
        if (left == null || right == null)
            return false;

        try
        {
            return left.Pointer == right.Pointer;
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static string PlayerLabel(Player? player)
    {
        try
        {
            return player?.Name(false) ?? "<player>";
        }
        catch
        {
            return "<player>";
        }
    }

    private static string SafeShipId(Ship ship)
    {
        try
        {
            return ship.id.ToString();
        }
        catch
        {
            return "<id>";
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static string SafeShipType(Ship ship)
    {
        try
        {
            return ship.shipType?.nameUi ?? ship.shipType?.name ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string FormatPercentFromOne(float value)
        => $"{(value - 1f) * 100f:+0.#;-0.#;0}%";

    private static string FormatFloat(float value)
        => value.ToString("0.###");
}

[HarmonyPatch(typeof(BattleManager), nameof(BattleManager.StartCustomBattle))]
internal static class BattleStartAccuracyCustomBattleStartPatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => BattleStartAccuracyBreakdownPatch.MarkPendingCustomBattle("StartCustomBattle");
}

[HarmonyPatch(typeof(BattleManager), "PreInitCustomBattle")]
internal static class BattleStartAccuracyCustomBattlePreInitPatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => BattleStartAccuracyBreakdownPatch.MarkPendingCustomBattle("PreInitCustomBattle");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ToCustomBattle))]
internal static class BattleStartAccuracyCustomBattleToCustomPatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => BattleStartAccuracyBreakdownPatch.MarkPendingCustomBattle("ToCustomBattle");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ToCustomBattleFromSave))]
internal static class BattleStartAccuracyCustomBattleFromSavePatch
{
    [HarmonyPrefix]
    private static void Prefix()
        => BattleStartAccuracyBreakdownPatch.MarkPendingCustomBattle("ToCustomBattleFromSave");
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnEnterState))]
internal static class BattleStartAccuracyCustomBattleEnterStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleStartAccuracyBreakdownPatch.LogCustomBattleOnBattleState("OnEnterState");
        else if (state == GameManager.GameState.CustomBattleSetup)
            BattleStartAccuracyBreakdownPatch.ResetCustomBattleSummary();
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeState), new[] { typeof(GameManager.GameState), typeof(bool) })]
internal static class BattleStartAccuracyCustomBattleChangeStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState newState)
    {
        if (newState == GameManager.GameState.Battle)
            BattleStartAccuracyBreakdownPatch.LogCustomBattleOnBattleState("ChangeState");
        else if (newState == GameManager.GameState.CustomBattleSetup)
            BattleStartAccuracyBreakdownPatch.ResetCustomBattleSummary();
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnLeaveState))]
internal static class BattleStartAccuracyCustomBattleLeaveStatePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameManager.GameState state)
    {
        if (state == GameManager.GameState.Battle)
            BattleStartAccuracyBreakdownPatch.ResetCustomBattleSummary();
    }
}
