using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MintChipPlus.GameData;

// Auto-applies the player's preferred PER-SHIP-TYPE settings at battle start (ammo type,
// avoid torpedoes, avoid ships, auto group-leader) so they don't redo them every fight —
// BBs get different defaults than DDs/TBs. Runs from a GameManager.BattleStarted postfix,
// when ships + divisions are populated and mutable. Ammo is per-ship (by the ship's type);
// the division-level behaviors (avoid/auto-leader) are keyed by the division leader's type.
// "Leave" on any setting = don't touch it.
internal static class BattleStartDefaults
{
    // Surface ship types offered in the editor (Key = Ship.shipType.name, lowercase).
    internal static readonly (string Key, string Label)[] Types =
    {
        ("bb", "BB"), ("bc", "BC"), ("ca", "CA"), ("cl", "CL"), ("dd", "DD"), ("tb", "TB"),
    };

    private const string ConfigKey = "uadmc_battle_start_pertype";
    // type -> [ammo, avoidTorp, avoidShip, autoLeader, fireTorpedoes, formation] as enum ints
    private static Dictionary<string, int[]>? _cfg;

    private static int[] DefaultsFor(string type)
    {
        bool heavy = type is "bb" or "bc" or "ca";
        int ammo = (int)(heavy ? ModSettings.BattleAmmoMode.AP : ModSettings.BattleAmmoMode.HE);
        int on = (int)ModSettings.BattleToggle.On;
        int leave = (int)ModSettings.BattleToggle.Leave;
        int formLeave = (int)ModSettings.BattleFormation.Leave;
        // Torpedoes + formation default to Leave (don't touch) — opt-in per class.
        return new[] { ammo, on, on, on, leave, formLeave };
    }

    private static void EnsureLoaded()
    {
        if (_cfg != null)
            return;
        _cfg = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Types)
            _cfg[t.Key] = DefaultsFor(t.Key);

        try
        {
            string raw = PlayerPrefs.GetString(ConfigKey, string.Empty);
            foreach (string entry in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(entry))
                    continue;
                string[] kv = entry.Split('=');
                if (kv.Length != 2)
                    continue;
                string type = kv[0].Trim().ToLowerInvariant();
                string[] vals = kv[1].Split(',');
                if (vals.Length < 4)
                    continue;
                int[] arr = DefaultsFor(type); // start from defaults so older 4-value saves keep new fields
                for (int i = 0; i < arr.Length && i < vals.Length; i++)
                    int.TryParse(vals[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out arr[i]);
                _cfg[type] = arr;
            }
        }
        catch { }
    }

    private static int[] Cfg(string type)
    {
        EnsureLoaded();
        return _cfg!.TryGetValue(type.ToLowerInvariant(), out int[]? a) ? a : DefaultsFor(type);
    }

    private static void SetIdx(string type, int idx, int val)
    {
        EnsureLoaded();
        type = type.ToLowerInvariant();
        if (!_cfg!.TryGetValue(type, out int[]? a))
        {
            a = DefaultsFor(type);
            _cfg[type] = a;
        }
        a[idx] = val;
        Persist();
    }

    private static void Persist()
    {
        var sb = new StringBuilder();
        foreach (var kv in _cfg!)
        {
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(kv.Key).Append('=').Append(string.Join(",", kv.Value));
        }
        PlayerPrefs.SetString(ConfigKey, sb.ToString());
        PlayerPrefs.Save();
    }

    internal static ModSettings.BattleAmmoMode GetAmmo(string type) => (ModSettings.BattleAmmoMode)Cfg(type)[0];
    internal static ModSettings.BattleToggle GetAvoidTorp(string type) => (ModSettings.BattleToggle)Cfg(type)[1];
    internal static ModSettings.BattleToggle GetAvoidShip(string type) => (ModSettings.BattleToggle)Cfg(type)[2];
    internal static ModSettings.BattleToggle GetAutoLeader(string type) => (ModSettings.BattleToggle)Cfg(type)[3];
    internal static void SetAmmo(string type, ModSettings.BattleAmmoMode m) => SetIdx(type, 0, (int)m);
    internal static void SetAvoidTorp(string type, ModSettings.BattleToggle m) => SetIdx(type, 1, (int)m);
    internal static void SetAvoidShip(string type, ModSettings.BattleToggle m) => SetIdx(type, 2, (int)m);
    internal static void SetAutoLeader(string type, ModSettings.BattleToggle m) => SetIdx(type, 3, (int)m);
    internal static ModSettings.BattleToggle GetFireTorp(string type) => (ModSettings.BattleToggle)Cfg(type)[4];
    internal static ModSettings.BattleFormation GetFormation(string type) => (ModSettings.BattleFormation)Cfg(type)[5];
    internal static void SetFireTorp(string type, ModSettings.BattleToggle m) => SetIdx(type, 4, (int)m);
    internal static void SetFormation(string type, ModSettings.BattleFormation m) => SetIdx(type, 5, (int)m);

    // Divisions already given their defaults this battle (by pointer), so mid-battle splits
    // get them once without overwriting later manual changes.
    private static readonly HashSet<IntPtr> appliedDivisions = new();
    private static float lastReapply;

    // At battle start: apply to every player division and record each.
    internal static void Apply()
    {
        if (!ModSettings.BattleStartDefaultsEnabled)
            return;

        try
        {
            var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
            if (divisions == null)
                return;

            appliedDivisions.Clear();
            int divCount = 0, shipCount = 0;
            foreach (Division d in divisions)
            {
                if (d == null)
                    continue;
                shipCount += ApplyToDivision(d);
                try { appliedDivisions.Add(d.Pointer); } catch { }
                divCount++;
            }

            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC battle-start defaults applied (per-type): divisions={divCount} ships set={shipCount}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC battle-start defaults failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Mid-battle: a split creates a NEW division that reverts to vanilla orders, so re-apply
    // the per-type defaults to any player division we haven't seen yet. Driven from the Ui.Update
    // postfix; throttled. Only touches NEW divisions, so manual mid-battle changes are kept.
    internal static void ReapplyNewDivisions()
    {
        if (!ModSettings.BattleStartDefaultsEnabled)
            return;

        try
        {
            if (!GameManager.IsBattle)
            {
                if (appliedDivisions.Count > 0)
                    appliedDivisions.Clear();
                return;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - lastReapply < 1.5f)
                return;
            lastReapply = now;

            var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
            if (divisions == null)
                return;

            int newDivs = 0;
            foreach (Division d in divisions)
            {
                if (d == null)
                    continue;
                IntPtr ptr;
                try { ptr = d.Pointer; } catch { continue; }
                if (appliedDivisions.Contains(ptr))
                    continue;
                ApplyToDivision(d);
                appliedDivisions.Add(ptr);
                newDivs++;
            }

            if (newDivs > 0)
                Melon<MintChipPlusMod>.Logger.Msg($"UADMC battle-start defaults: re-applied to {newDivs} new division(s) (e.g. after a split).");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC battle-start reapply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Applies the per-type defaults to one division; returns the count of ships whose ammo was set.
    private static int ApplyToDivision(Division d)
    {
        int shipCount = 0;

        // Division-level behaviors, keyed by the division leader's ship type.
        string? leadType = TypeOf(SafeLeader(d));
        if (leadType != null)
        {
            ModSettings.BattleToggle torp = GetAvoidTorp(leadType);
            ModSettings.BattleToggle ship = GetAvoidShip(leadType);
            ModSettings.BattleToggle leader = GetAutoLeader(leadType);
            if (torp != ModSettings.BattleToggle.Leave)
            {
                try { if (d.CanAvoidTorpedoes()) d.AvoidTorpedoes(torp == ModSettings.BattleToggle.On); } catch { }
            }
            if (ship != ModSettings.BattleToggle.Leave)
            {
                try { d.AvoidCollisions(ship == ModSettings.BattleToggle.On); } catch { }
            }
            if (leader != ModSettings.BattleToggle.Leave)
            {
                try { d.autoChangeGroupLeader = leader == ModSettings.BattleToggle.On; } catch { }
            }
            ModSettings.BattleFormation form = GetFormation(leadType);
            if (form != ModSettings.BattleFormation.Leave)
            {
                try { d.formation = form == ModSettings.BattleFormation.Column ? Division.Formation.Column : Division.Formation.Line; } catch { }
            }
        }

        // Per-ship ammo + torpedo mode, keyed by each ship's own type.
        var ships = d.ships;
        if (ships == null)
            return shipCount;
        foreach (Ship s in ships)
        {
            if (s == null)
                continue;
            string? st = TypeOf(s);
            if (st == null)
                continue;
            Ship.ShellType? shell = GetAmmo(st) switch
            {
                ModSettings.BattleAmmoMode.Auto => Ship.ShellType.Auto,
                ModSettings.BattleAmmoMode.AP => Ship.ShellType.Ap,
                ModSettings.BattleAmmoMode.HE => Ship.ShellType.He,
                _ => (Ship.ShellType?)null,
            };
            if (shell.HasValue)
            {
                try { s.MainShellType = shell.Value; s.SecShellType = shell.Value; shipCount++; } catch { }
            }

            ModSettings.BattleToggle fire = GetFireTorp(st);
            if (fire != ModSettings.BattleToggle.Leave)
            {
                try { s.torpedoMode = fire == ModSettings.BattleToggle.On ? Ship.ShootMode.Normal : Ship.ShootMode.Off; } catch { }
            }
        }
        return shipCount;
    }

    private static Ship? SafeLeader(Division d) { try { return d.leader; } catch { return null; } }
    private static string? TypeOf(Ship? s) { try { return s?.shipType?.name?.ToLowerInvariant(); } catch { return null; } }
}
