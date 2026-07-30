using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace MintChipPlus.GameData;

// At campaign battle end the MC-winner takes ALL surrendered ships (capture the loser's,
// recover its own). The transfer CANNOT be done in the CompleteBattle postfix: that runs in
// the battle-summary phase ~16s before the campaign reconciles on World re-entry, and
// vanilla's post-battle loss pass then undoes the transfer (confirmed in-game — captured
// ships briefly appear then vanish; the Sink lists are even empty at that point).
//
// So: SNAPSHOT the surrendered ships' Guids + the victor at battle end, then REINSTATE them
// to the victor AFTER reconciliation (GameManager.OnEnterState(World), with an
// OnLoadingScreenHide fallback). We re-resolve each live ship by Guid via the Guid-keyed
// CampaignController.TransferShipToNewOwner — never holding a live Ship reference across the
// Battle->World transition (the wrapper can be disposed). "Winner takes all surrendered
// ships" => every surrendered, non-sunk ship is reassigned to the victor.
internal static class SurrenderedShipCapture
{
    private sealed class Pending
    {
        internal Il2CppSystem.Guid Id;
        internal PlayerData Victor = null!;
        internal string Name = "?";
    }

    private static readonly List<Pending> _pending = new();

    // Ids (string form) of ships we are keeping for the victor this battle. Used to scrub
    // them out of the post-battle loss pass so vanilla doesn't reap the surrendered ships
    // after we transfer them. Persists from battle end until the next battle.
    private static readonly HashSet<string> _protect = new(StringComparer.OrdinalIgnoreCase);

    // Battle-end snapshot only — no transfer here (too early to persist).
    internal static void OnCompleteBattle(CampaignBattle battle)
    {
        if (!ModSettings.SurrenderedShipCaptureEnabled)
            return;
        try
        {
            var cc = CampaignController.Instance;
            if (cc == null || battle == null)
                return;
            _protect.Clear();

            Player? victor = ResolveVictor(battle);
            if (victor == null)
                return; // draw / indeterminate -> leave to vanilla

            PlayerData? victorData = SafeData(victor);
            if (victorData == null)
                return;

            var sunk = new HashSet<IntPtr>();
            Collect(battle.SunkShips, sunk);

            int queued = QueueSurrendered(battle.AttackerShips, sunk, victorData)
                       + QueueSurrendered(battle.DefenderShips, sunk, victorData);

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC surrendered capture: victor={PlayerName(victor)} queued {queued} surrendered ship(s) for post-battle reinstatement.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC surrendered capture (queue) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // After the campaign has reconciled (World re-entry / loading-screen hide): re-resolve
    // each snapshotted ship by Guid and assign it to the victor. One-shot — drains the queue.
    internal static void ReinstatePending()
    {
        if (_pending.Count == 0)
            return;

        var batch = new List<Pending>(_pending);
        _pending.Clear();

        try
        {
            var cc = CampaignController.Instance;
            if (cc == null)
                return;

            var taken = new Il2CppSystem.Collections.Generic.List<Ship>();
            int ok = 0;
            foreach (Pending p in batch)
            {
                Player? victor = ResolvePlayer(cc, p.Victor);
                if (victor == null)
                {
                    Melon<MintChipPlusMod>.Logger.Warning($"UADMC capture reinstate '{p.Name}': victor not resolvable, skipped.");
                    continue;
                }

                int before = taken.Count;
                try { cc.TransferShipToNewOwner(victor, p.Id, ref taken, null, true); }
                catch (Exception ex)
                {
                    Melon<MintChipPlusMod>.Logger.Warning($"UADMC capture reinstate '{p.Name}' threw: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                bool resolved = taken.Count > before;
                string extra = string.Empty;
                if (resolved)
                {
                    ok++;
                    try
                    {
                        Ship moved = taken[taken.Count - 1];
                        moved.IsSurrendered = false;
                        extra = $" isDead={moved.isDead}";
                    }
                    catch { }
                }
                Melon<MintChipPlusMod>.Logger.Msg(
                    $"UADMC capture reinstate '{p.Name}' -> {PlayerDataName(p.Victor)} resolved={resolved}{extra}.");
            }

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC capture reinstate: {ok}/{batch.Count} surrendered ship(s) transferred to the victor.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC capture reinstate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Called right before vanilla's post-battle loss pass: remove our kept ships from the
    // battle's loss lists (populated by now) so the pass doesn't reap them.
    internal static void ProtectFromLossPass(CampaignBattle battle)
    {
        if (_protect.Count == 0 || battle == null)
            return;
        try
        {
            int removed = ScrubList(battle.AttackerShipsSink) + ScrubList(battle.DefenderShipsSink) + ScrubList(battle.SunkShips);
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC loss-pass protect: removed {removed} kept ship(s) from battle loss lists (protected={_protect.Count}).");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC loss-pass protect failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int ScrubList(Il2CppSystem.Collections.Generic.List<Ship>? list)
    {
        if (list == null)
            return 0;
        var toRemove = new List<Ship>();
        foreach (Ship s in list)
        {
            if (s == null)
                continue;
            string id;
            try { id = s.id.ToString(); } catch { continue; }
            if (_protect.Contains(id))
                toRemove.Add(s);
        }
        int removed = 0;
        foreach (Ship s in toRemove)
        {
            try { if (list.Remove(s)) removed++; } catch { }
        }
        return removed;
    }

    // Diagnostic: log the human player's vessel count around the candidate deleters so we
    // can see which one removes the captured ships (only fires after a capture battle).
    internal static void LogLossPass(string tag)
    {
        if (_protect.Count == 0)
            return;
        Melon<MintChipPlusMod>.Logger.Msg($"UADMC loss-pass {tag}: mainVessels={MainVesselCount()} protected={_protect.Count}.");
    }

    private static int MainVesselCount()
    {
        try
        {
            Player? main = ModCampaignState.MainPlayerOrNull();
            var vbp = CampaignController.Instance?.CampaignData?.VesselsByPlayer;
            if (main?.data == null || vbp == null)
                return -1;
            return vbp.TryGetValue(main.data, out var list) ? list.Count : -1;
        }
        catch { return -1; }
    }

    private static int QueueSurrendered(Il2CppSystem.Collections.Generic.List<Ship>? ships, HashSet<IntPtr> sunk, PlayerData victorData)
    {
        if (ships == null)
            return 0;
        int n = 0;
        foreach (Ship s in ships)
        {
            if (s == null)
                continue;
            bool surrendered = false;
            try { surrendered = s.IsSurrendered; } catch { }
            if (!surrendered)
                continue;
            if (sunk.Contains(s.Pointer))
                continue;

            Il2CppSystem.Guid id;
            try { id = s.id; } catch { continue; }
            string name = "?";
            try { name = s.name ?? "?"; } catch { }

            _pending.Add(new Pending { Id = id, Victor = victorData, Name = name });
            try { _protect.Add(id.ToString()); } catch { }
            n++;
        }
        return n;
    }

    private static Player? ResolveVictor(CampaignBattle battle)
    {
        Player? victor = SafeP(() => battle.Victor);
        if (victor != null)
            return victor;
        float vpA = SafeFloat(() => battle.VictoryPointsAttacker);
        float vpD = SafeFloat(() => battle.VictoryPointsDefender);
        if (vpA == vpD)
            return null;
        return SafeP(() => vpA > vpD ? battle.Attacker : battle.Defender);
    }

    private static Player? ResolvePlayer(CampaignController cc, PlayerData data)
    {
        try
        {
            if (data == null)
                return null;
            var players = cc.CampaignData?.Players;
            if (players == null)
                return null;
            foreach (Player p in players)
                if (p != null && p.data != null && p.data.Pointer == data.Pointer)
                    return p;
        }
        catch { }
        return null;
    }

    private static void Collect(Il2CppSystem.Collections.Generic.List<Ship>? ships, HashSet<IntPtr> set)
    {
        if (ships == null)
            return;
        foreach (Ship s in ships)
            if (s != null)
                set.Add(s.Pointer);
    }

    private static PlayerData? SafeData(Player p) { try { return p.data; } catch { return null; } }
    private static string PlayerName(Player? p) { try { return p?.data?.name ?? "(null)"; } catch { return "?"; } }
    private static string PlayerDataName(PlayerData? d) { try { return d?.name ?? "(null)"; } catch { return "?"; } }
    private static Player? SafeP(Func<Player?> f) { try { return f(); } catch { return null; } }
    private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
}
