using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;

namespace MintChipPlus.GameData;

// Per-campaign minelayer port-composition goal:
//   - Default: a per-port composition (variant nameUi -> count) applied to every port.
//   - Overrides: portId -> (variant -> count). A port WITH an override uses it exclusively;
//     an override that is present-but-empty means "skip this port" (target 0 for all variants).
// Variants are only targeted when set > 0; unlocking a new sub class does NOT auto-target it.
// Persisted via ModCampaignState (control-char blob, no JSON dep), mirroring ShipServiceRecords.
// Flat record format (records split by RS, fields by US):
//   "D" US variant US count                 -> a default entry
//   "O" US portId [US variant US count]     -> an override entry (no trailing pair = skip marker)
internal static class MinelayerGoals
{
    private const string Feature = "minelayer_goals";
    private const char RS = '', US = '';

    internal sealed class Goal
    {
        public readonly Dictionary<string, int> Default = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, int>> Overrides = new(StringComparer.Ordinal);

        internal int EffectiveTarget(string portId, string variant)
        {
            if (Overrides.TryGetValue(portId, out var ov))
                return ov.TryGetValue(variant, out int n) ? n : 0;
            return Default.TryGetValue(variant, out int d) ? d : 0;
        }

        internal bool HasOverride(string portId) => Overrides.ContainsKey(portId);

        internal void SetDefault(string variant, int count)
        {
            if (count <= 0) Default.Remove(variant);
            else Default[variant] = count;
        }

        // Ensure an override map exists for a port (empty = "skip"). Pass remove=true to clear it
        // (port reverts to using the Default).
        internal Dictionary<string, int> EnsureOverride(string portId)
        {
            if (!Overrides.TryGetValue(portId, out var ov))
                Overrides[portId] = ov = new Dictionary<string, int>(StringComparer.Ordinal);
            return ov;
        }

        internal void ClearOverride(string portId) => Overrides.Remove(portId);

        internal void SetOverride(string portId, string variant, int count)
        {
            var ov = EnsureOverride(portId);
            if (count <= 0) ov.Remove(variant);
            else ov[variant] = count;
        }
    }

    internal static Goal Load()
    {
        var g = new Goal();
        try
        {
            string blob = ModCampaignState.Load(Feature);
            if (string.IsNullOrEmpty(blob))
                return g;

            foreach (string rec in blob.Split(RS))
            {
                if (string.IsNullOrEmpty(rec))
                    continue;
                string[] f = rec.Split(US);
                if (f.Length == 0)
                    continue;

                if (f[0] == "D" && f.Length >= 3)
                {
                    g.Default[f[1]] = ParseI(f[2]);
                }
                else if (f[0] == "O" && f.Length >= 2)
                {
                    var ov = g.EnsureOverride(f[1]); // create even if empty (skip marker)
                    if (f.Length >= 4)
                        ov[f[2]] = ParseI(f[3]);
                }
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC minelayer goals: load failed — {ex.GetType().Name}: {ex.Message}");
        }
        return g;
    }

    internal static void Save(Goal g)
    {
        try
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in g.Default)
                Append(sb, ref first, "D" + US + Clean(kv.Key) + US + kv.Value.ToString(CultureInfo.InvariantCulture));

            foreach (var port in g.Overrides)
            {
                if (port.Value.Count == 0)
                {
                    Append(sb, ref first, "O" + US + Clean(port.Key)); // skip marker
                    continue;
                }
                foreach (var kv in port.Value)
                    Append(sb, ref first, "O" + US + Clean(port.Key) + US + Clean(kv.Key) + US + kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            ModCampaignState.Save(Feature, sb.ToString());
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC minelayer goals: save failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Append(StringBuilder sb, ref bool first, string rec)
    {
        if (!first) sb.Append(RS);
        first = false;
        sb.Append(rec);
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace(RS, ' ').Replace(US, ' ');
    }

    private static int ParseI(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
