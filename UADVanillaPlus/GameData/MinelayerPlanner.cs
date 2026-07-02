using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// Shared minelayer-distribution planner (faithful port of the save tool's AssignSubmarines,
// goal-driven). Computes, for the human player, each port's current minelayer composition vs the
// goal target, and a plan that places ONLY idle (unassigned) minelayers into quota-deficit ports.
// Already-assigned and in-transit subs are never touched. Apply() performs the save tool's write
// (sub.VesselBuildingSelectedLocation = port). Also surfaces per-variant SubmarineType + idle lists
// so the panel can build-the-missing and assign them. Used by the goals panel.
internal static class MinelayerPlanner
{
    internal sealed class Move
    {
        public Submarine Sub = null!;
        public string Name = "?";
        public string Variant = "";
        public string ToPort = "";
        public PortElement ToElement = null!;
    }

    internal sealed class PortRow
    {
        public string PortId = "";        // stable id (goal key)
        public string Name = "";          // display name (e.g. "Dublin")
        public PortElement Element = null!;
        public string Region = "?";       // region (area) label this port belongs to
        public IntPtr RegionKey;          // area pointer, for grouping
        public readonly Dictionary<string, int> Current = new(StringComparer.Ordinal); // variant -> assigned count
    }

    internal sealed class Plan
    {
        public bool HasHuman;
        public Player Human = null!;
        public readonly List<string> Variants = new();                 // variants present or targeted (sorted)
        public readonly List<PortRow> Ports = new();                   // in stable port order
        public readonly Dictionary<string, PortRow> PortById = new(StringComparer.Ordinal);
        public readonly List<Move> Moves = new();
        public readonly Dictionary<string, int> Shortfall = new(StringComparer.Ordinal);   // variant -> deficit slots unfilled by idle (= to build)
        public readonly Dictionary<string, List<Submarine>> IdleSubs = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> IdleByVariant = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> AssignedByVariant = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> DeployedByVariant = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SubmarineType> TypeByVariant = new(StringComparer.Ordinal);
        public readonly Dictionary<string, float> TonnageByVariant = new(StringComparer.Ordinal); // representative per-sub tonnage
        public float ShipyardCapacity;          // human.shipyard
        public float CurrentBuildingTonnage;    // Σ tonnage of vessels (ships+subs) currently under construction
        public int IdleTotal;
        public int AssignedTotal;
        public int DeployedTotal;
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    // Turn a snake_case region id ("se_asia") into a display label ("Se Asia").
    internal static string Prettify(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "(region)";
        var sb = new System.Text.StringBuilder(id.Length);
        bool cap = true;
        foreach (char c in id)
        {
            if (c == '_' || c == '-') { sb.Append(' '); cap = true; }
            else if (cap) { sb.Append(char.ToUpperInvariant(c)); cap = false; }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    internal static Plan Compute(MinelayerGoals.Goal goal)
    {
        var plan = new Plan();
        var data = CampaignController.Instance?.CampaignData;
        if (data == null)
            return plan;

        Player? human = null;
        var players = Safe(() => data.Players, null);
        if (players != null)
            foreach (Player p in players)
                if (p != null && Safe(() => p.isMain, false)) { human = p; break; }
        if (human == null)
            return plan;
        plan.HasHuman = true;
        plan.Human = human;

        // ----- player ports in stable order -----
        var byPlayer = Safe(() => data.ProvincesByPlayer, null);
        if (byPlayer != null)
        {
            foreach (var kvp in byPlayer)
            {
                var provs = kvp.Value;
                if (provs == null) continue;
                foreach (Province pr in provs)
                {
                    if (pr == null) continue;
                    if (!Safe(() => pr.ControllerPlayer?.isMain ?? false, false)) continue;
                    string region = Prettify(Safe(() => pr.AreaId, "") ?? ""); // the region/area name, NOT a port
                    var ports = Safe(() => pr.Ports, null);
                    if (ports == null) continue;
                    foreach (PortElement pe in ports)
                    {
                        if (pe == null) continue;
                        string? pid = Safe(() => pe.Id, null);
                        if (pid == null || plan.PortById.ContainsKey(pid)) continue;
                        string pname = Safe(() => pe.Name, null) ?? "";
                        var row = new PortRow { PortId = pid, Name = string.IsNullOrWhiteSpace(pname) ? pid : pname, Element = pe, Region = region };
                        plan.PortById[pid] = row;
                        plan.Ports.Add(row);
                    }
                }
            }
        }

        // ----- classify minelayers (save tool order + runtime in-transit guard) -----
        var variantSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in goal.Default.Keys) variantSet.Add(k);

        var subs = Safe(() => data.GetSubmarines, null);
        if (subs != null)
        {
            foreach (Submarine sub in subs)
            {
                if (sub == null) continue;
                if (!Safe(() => sub.player?.isMain ?? false, false)) continue;
                if (Safe(() => sub.isSunk || sub.isScrapped, false)) continue;
                int mines = Safe(() => sub.Type?.mines ?? 0, 0);
                if (mines <= 0) continue;

                string variant = Safe(() => sub.Type?.nameUi, null) ?? ("mines" + mines);
                variantSet.Add(variant);
                if (!plan.TypeByVariant.ContainsKey(variant))
                {
                    var t = Safe(() => sub.Type, null);
                    if (t != null) plan.TypeByVariant[variant] = t;
                    plan.TonnageByVariant[variant] = Safe(() => sub.Tonnage(), 0f);
                }
                if (Safe(() => sub.isBuilding, false))
                    plan.CurrentBuildingTonnage += Safe(() => sub.Tonnage(), 0f);

                string? portId = Safe(() => sub.PortLocation?.Id, null);
                string? buildId = Safe(() => sub.VesselBuildingSelectedLocation?.Id, null);
                string? prevId = Safe(() => sub.PrevPortLocation?.Id, null);
                bool atSea = Safe(() => sub.location != null, false);
                bool inTransit = Safe(() => sub.IsSailing, false)
                              || Safe(() => sub.SailingTo != null, false)
                              || Safe(() => sub.SeaGroupId.ToString() != "00000000-0000-0000-0000-000000000000", false);

                string? coverPort = portId ?? buildId;
                if (coverPort != null)
                {
                    plan.AssignedTotal++;
                    Inc(plan.AssignedByVariant, variant);
                    if (plan.PortById.TryGetValue(coverPort, out var row))
                    {
                        row.Current.TryGetValue(variant, out int c);
                        row.Current[variant] = c + 1;
                    }
                }
                else if (prevId != null || atSea || inTransit)
                {
                    plan.DeployedTotal++;
                    Inc(plan.DeployedByVariant, variant);
                }
                else
                {
                    plan.IdleTotal++;
                    if (!plan.IdleSubs.TryGetValue(variant, out var list))
                        plan.IdleSubs[variant] = list = new List<Submarine>();
                    list.Add(sub);
                }
            }
        }

        foreach (var kv in plan.IdleSubs) plan.IdleByVariant[kv.Key] = kv.Value.Count;

        // Shipyard load: capacity + tonnage of surface ships currently under construction
        // (building subs were summed in the loop above). Subs count against shipyard per the user.
        plan.ShipyardCapacity = Safe(() => human.shipyard, 0f);
        try
        {
            foreach (Ship s in human.GetFleetAll())
            {
                if (s == null) continue;
                if (Safe(() => s.isBuilding, false))
                    plan.CurrentBuildingTonnage += Safe(() => s.tonnage, 0f);
            }
        }
        catch { }

        // Also offer minelayer types the player CAN build but hasn't yet, so you can set a goal for any
        // buildable design (not just ones you already own). Master catalog G.GameData.submarines filtered
        // to minelayers (mines>0) that pass PlayerController.CanBuildSubmarineForType for the human.
        try
        {
            var catalog = Safe(() => G.GameData?.submarines, null);
            var pc = PlayerController.Instance;
            if (catalog != null && pc != null)
            {
                foreach (var kv in catalog)
                {
                    SubmarineType t = kv.Value;
                    if (t == null || Safe(() => t.mines, 0) <= 0) continue; // minelayers only
                    bool buildable;
                    try { buildable = pc.CanBuildSubmarineForType(t, human, out string _); } catch { buildable = false; }
                    if (!buildable) continue;
                    string variant = Safe(() => t.nameUi, null) ?? "";
                    if (variant.Length == 0) continue;
                    variantSet.Add(variant);
                    if (!plan.TypeByVariant.ContainsKey(variant))
                        plan.TypeByVariant[variant] = t;
                }
            }
        }
        catch { }

        plan.Variants.AddRange(variantSet);
        plan.Variants.Sort(StringComparer.Ordinal);

        // ----- per-variant: place idle subs into goal-deficit ports -----
        foreach (string variant in plan.Variants)
        {
            plan.IdleSubs.TryGetValue(variant, out var pool);
            int pi = 0;
            int filledSlots = 0, totalSlots = 0;

            foreach (PortRow row in plan.Ports)
            {
                int target = goal.EffectiveTarget(row.PortId, variant);
                row.Current.TryGetValue(variant, out int have);
                for (int i = have; i < target; i++)
                {
                    totalSlots++;
                    if (pool != null && pi < pool.Count)
                    {
                        Submarine s = pool[pi++];
                        plan.Moves.Add(new Move
                        {
                            Sub = s,
                            Name = Safe(() => s.Name(true), "?") ?? "?",
                            Variant = variant,
                            ToPort = row.PortId,
                            ToElement = row.Element,
                        });
                        filledSlots++;
                    }
                }
            }

            int shortfall = totalSlots - filledSlots;
            if (shortfall > 0)
                plan.Shortfall[variant] = shortfall;
        }

        return plan;
    }

    private static void Inc(Dictionary<string, int> d, string k)
    {
        d.TryGetValue(k, out int v);
        d[k] = v + 1;
    }

    // Apply via the shared assignment core: building subs get the field write, built subs are moved.
    internal static int Apply(List<Move> moves)
    {
        int applied = 0;
        if (moves == null) return 0;
        foreach (Move m in moves)
            if (VesselAssignment.AssignToPort(m.Sub, m.ToElement)) applied++;
        return applied;
    }
}
