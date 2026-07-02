using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// "Buy ships from an allied major" — the ally builds the class in THEIR dock via
// BuildShipsFromDesign(force:true, overridePlayer:seller); we stamp a ForSaleTo contract on each
// hull, charge the buyer a deposit + balance (random +50..120% premium scaled by the seller's dock
// pressure), and deliver ownership once the hull finishes building + commissioning. A purchased
// class becomes refit-only (ForeignPurchaseBuildRestrictionPatch). HEAVY probe logging of cash +
// dock-pressure deltas on BOTH players settles the load-bearing unknown (who BuildShipsFromDesign
// actually charges/occupies). Prefix: UADVP_ALLYBUY.
internal static class AlliedShipPurchase
{
    private const float DepositFraction = 0.30f;
    private static readonly System.Random Rng = new System.Random(12345);
    private static readonly HashSet<string> HonorLogged = new(StringComparer.Ordinal);

    private static void Log(string m) => Melon<UADVanillaPlusMod>.Logger.Msg("UADVP_ALLYBUY " + m);
    private static T Safe<T>(Func<T> f, T fb) { try { return f(); } catch { return fb; } }

    // A price quote: computed once so the confirmation dialog shows exactly what gets charged.
    internal struct Quote { public float Cost, Pressure, PremiumFraction, Price, Deposit; public int BuildMonths; }

    internal static Quote GetQuote(Player seller, Ship design)
    {
        var q = new Quote();
        if (seller == null || design == null) return q;
        q.Cost = Safe(() => design.Cost(), 0f);
        float limit = Safe(() => seller.ShipbuildingCapacityLimit(), 1f);
        float underCon = Safe(() => seller.ShipTonnageUnderConstruction(), 0f);
        q.Pressure = limit > 1f ? underCon / limit : 0f;
        // Premium stays WITHIN the band [Min..Max] (e.g. +50%..+120%); dock pressure only POSITIONS the
        // roll within it (busy yard leans toward the cap), it never multiplies past the cap.
        double min = ModSettings.AllyPremiumMinFraction, max = ModSettings.AllyPremiumMaxFraction;
        double pf = q.Pressure < 0f ? 0.0 : (q.Pressure > 1f ? 1.0 : q.Pressure);
        double position = Rng.NextDouble() * 0.6 + pf * 0.4;
        if (position < 0.0) position = 0.0; else if (position > 1.0) position = 1.0;
        q.PremiumFraction = (float)(min + position * (max - min));
        q.Price = q.Cost * (1f + q.PremiumFraction);
        q.Deposit = q.Price * DepositFraction;
        // Build time = base months × the SELLER's over-capacity time penalty (the design is owned by
        // the human viewer, so design.BuildingTime(true) would apply OUR penalty, not theirs).
        q.BuildMonths = Safe(() =>
        {
            int baseMonths = design.BuildingTime(false);
            float pen = seller.TimePenalty();
            if (pen < 1f) pen = 1f;
            int m = (int)System.Math.Round(baseMonths * pen);
            return m < 1 ? 1 : m;
        }, 0);
        return q;
    }

    // How many of this class the ally is willing to build for you: a random base appetite plus however
    // many fit in their FREE dock capacity. A yard at/above capacity is LESS willing (base appetite
    // only) but never zero — they'll suspend their own ships to fit a few.
    internal static int WillingnessCap(Player seller, Ship design)
    {
        if (seller == null || design == null) return 1;
        float limit = Safe(() => seller.ShipbuildingCapacityLimit(), 0f);
        float under = Safe(() => seller.ShipTonnageUnderConstruction(), 0f);
        float free = limit - under;
        float ton = Safe(() => design.Tonnage(), 0f);
        int slots = ton > 1f && free > 0f ? (int)System.Math.Floor(free / ton) : 0; // hulls that fit in free capacity
        int baseWilling = 2 + Rng.Next(0, 7);                                        // 2..8 base appetite (never 0)
        return System.Math.Max(1, baseWilling + slots);
    }

    // ----- 1. PLACE ORDER -----  (returns the created orders, so the caller can set their delivery port)
    internal static List<AllyPurchaseState.Order> PlaceOrder(Player seller, Ship design, int amount, Quote? quote = null)
    {
        if (seller == null || design == null || amount <= 0) return new();
        Player buyer = PlayerController.Instance;
        if (buyer == null) { Log("no buyer (PlayerController.Instance null)"); return new(); }

        Quote q = quote ?? GetQuote(seller, design);
        float cost = q.Cost, premiumFraction = q.PremiumFraction, price = q.Price, deposit = q.Deposit, pressure = q.Pressure;
        string designKey = Safe(() => design.id.ToString(), "") ?? "";
        string designName = SafeName(design);
        int turn = Safe(() => CampaignController.Instance.CurrentDate.turn, 0);

        // --- PROBE SNAPSHOT (before) ---
        float bCash0 = Safe(() => buyer.cash, 0f), sCash0 = Safe(() => seller.cash, 0f);
        float bTon0 = Safe(() => buyer.ShipTonnageUnderConstruction(), 0f), sTon0 = Safe(() => seller.ShipTonnageUnderConstruction(), 0f);

        Il2CppSystem.Collections.Generic.List<Ship> hulls;
        try { hulls = PlayerController.Instance.BuildShipsFromDesign(design, amount, force: true, overridePlayer: seller); }
        catch (Exception ex) { Log($"BuildShipsFromDesign threw: {ex.GetType().Name}: {ex.Message}"); return new(); }
        int built = hulls?.Count ?? 0;

        // --- PROBE SNAPSHOT (after) — the load-bearing capacity-attribution log ---
        Log($"PROBE order: design=\"{designName}\" seller={Safe(() => seller.data?.name, "?")} amount={amount} built={built} " +
            $"buyerCash={bCash0:0} sellerCash={sCash0:0} buyerCashD={Safe(() => buyer.cash, 0f) - bCash0:0} sellerCashD={Safe(() => seller.cash, 0f) - sCash0:0} " +
            $"buyerTonD={Safe(() => buyer.ShipTonnageUnderConstruction(), 0f) - bTon0:0} sellerTonD={Safe(() => seller.ShipTonnageUnderConstruction(), 0f) - sTon0:0} " +
            $"cost={cost:0} pressure={pressure:0.00} premium={premiumFraction:0.00} price={price:0} deposit={deposit:0}");
        if (deposit > bCash0)
            Log($"NOTE: deposit {deposit:0} exceeds buyer cash {bCash0:0} — this order overdraws (check whether Cost() is over-scaled vs the cash economy).");

        if (built == 0 || hulls == null) return new();

        var created = new List<AllyPurchaseState.Order>();
        foreach (Ship h in hulls)
        {
            if (h == null) continue;
            string hullId = Safe(() => h.id.ToString(), "") ?? "";
            bool ok = Safe(() =>
            {
                h.ForSaleTo = buyer.data;        // PlayerData buyer (top-level Ship.ForSaleTo)
                h.SaleProfit = price - cost;     // float premium
                return true;
            }, false);
            Log($"order hull id={hullId} owner={Safe(() => h.player?.data?.name, "?")} forSaleTo={Safe(() => h.ForSaleTo?.name, "?")} isBuilding={Safe(() => h.isBuilding, false)} stamp={ok}");

            // charge the deposit; record the order
            Safe(() => { buyer.cash = (float)(buyer.cash - deposit); return true; }, false);
            var ord = new AllyPurchaseState.Order
            {
                Seller = Safe(() => seller.data?.name, "") ?? "",
                DesignKey = designKey,
                DesignName = designName,
                HullId = hullId,
                Deposit = deposit,
                Balance = price - deposit,
                StartTurn = turn,
                BuildMonths = q.BuildMonths,
            };
            AllyPurchaseState.AddOrder(ord);
            created.Add(ord);
        }
        Log($"ordered {built}x \"{designName}\" from {Safe(() => seller.data?.name, "?")}; deposit {deposit:0}/hull charged, balance {price - deposit:0}/hull on delivery.");
        return created;
    }

    // ----- 2. DELIVER COMPLETED — call from OnNewTurn. Idempotent. -----
    internal static void DeliverCompleted()
    {
        var cc = CampaignController.Instance;
        Player buyer = PlayerController.Instance;
        var data = cc?.CampaignData;
        if (cc == null || buyer == null || data?.Players == null) return;

        var taken = new Il2CppSystem.Collections.Generic.List<Ship>();
        foreach (Player seller in data.Players)
        {
            if (seller == null || Safe(() => seller.Pointer == buyer.Pointer, false)) continue;

            var fleet = new List<Ship>();
            try { foreach (Ship s in seller.GetFleetAll()) if (s != null) fleet.Add(s); } catch { continue; }

            foreach (Ship ship in fleet)
            {
                bool mine = Safe(() => ship.ForSaleTo != null && ship.ForSaleTo.Pointer == buyer.data.Pointer, false);
                bool done = Safe(() => !ship.isBuilding && !ship.isCommissioning, false);
                if (!mine || !done) continue;

                string hullId = Safe(() => ship.id.ToString(), "") ?? "";
                AllyPurchaseState.Order? order = FindOrderByHull(hullId);
                float bCash0 = Safe(() => buyer.cash, 0f), sCash0 = Safe(() => seller.cash, 0f);

                PortElement? destPort = order != null ? FindBuyerPort(order.DestPort) : null; // player-chosen delivery port
                int before = taken.Count;
                try { cc.TransferShipToNewOwner(buyer, ship.id, ref taken, destPort, true); }
                catch (Exception ex) { Log($"deliver threw: {ex.GetType().Name}: {ex.Message}"); continue; }
                if (taken.Count <= before) { Log($"deliver: transfer did not take for hull {hullId}"); continue; }

                Ship? moved = Safe(() => taken[taken.Count - 1], null);
                if (moved != null)
                {
                    Ship m = moved;
                    Safe(() => { m.ForSaleTo = null; return true; }, false);
                    AdoptDesign(m, buyer, seller, order?.DesignKey ?? ""); // clone the class template once so it's refittable
                }

                // settle: charge buyer the balance, credit seller the full price (deposit + balance)
                if (order != null)
                {
                    float total = order.Deposit + order.Balance;
                    Safe(() => { buyer.cash = (float)(buyer.cash - order.Balance); return true; }, false);
                    Safe(() => { seller.cash = (float)(seller.cash + total); return true; }, false);
                    AllyPurchaseState.AddRestricted(order.DesignKey);   // class becomes refit-only
                    AllyPurchaseState.RemoveOrder(order);
                }
                Log($"DELIVERED hull {hullId} \"{order?.DesignName ?? "?"}\" {Safe(() => seller.data?.name, "?")}->{Safe(() => buyer.data?.name, "?")} " +
                    $"buyerCashD={Safe(() => buyer.cash, 0f) - bCash0:0} sellerCashD={Safe(() => seller.cash, 0f) - sCash0:0} (restricted now: build-no/refit-yes)");
            }
        }
    }

    // ----- 3. ALLIANCE-BREAK DISPOSITION — call from OnNewTurn. -----
    // Delivered = already yours. Open order + alliance ended: war => seize (hull stays seller, deposit
    // forfeit); peaceful => honor (leave the order; it still delivers on completion).
    internal static void ProcessBreaks()
    {
        Player? human = ExtraGameData.MainPlayer();
        var data = CampaignController.Instance?.CampaignData;
        if (human == null || data?.Players == null) return;

        // snapshot the orders (we may mutate the list on seize)
        var orders = new List<AllyPurchaseState.Order>(AllyPurchaseState.Current.Orders);
        foreach (AllyPurchaseState.Order o in orders)
        {
            Player? seller = FindPlayer(o.Seller);
            if (seller == null) continue;
            if (CampaignInvasionActions.IsAllied(human, seller)) continue; // still allied — nothing to do

            if (AllySales.AllianceBrokeIntoWar(seller))
            {
                // seize: the seller keeps the in-build hull; clear our contract so we don't deliver it.
                Ship? hull = FindHull(seller, o.HullId);
                if (hull != null) Safe(() => { hull.ForSaleTo = null; return true; }, false);
                AllyPurchaseState.RemoveOrder(o);
                Log($"SEIZED: {o.Seller} broke the alliance into war — hull \"{o.DesignName}\" kept by {o.Seller}, deposit {o.Deposit:0} forfeit.");
            }
            else if (HonorLogged.Add(o.HullId))
            {
                Log($"alliance with {o.Seller} ended peacefully — contract for \"{o.DesignName}\" honored; will still deliver on completion.");
            }
        }
    }

    // classKey (the CLASS-level design id, e.g. the order's DesignKey) -> the buyer-owned clone, cached
    // for the session so N hulls of a class reuse ONE clone reliably (not one clone per ship).
    private static readonly Dictionary<string, Ship> CloneCache = new(StringComparer.Ordinal);

    // Make the bought class refittable. The delivered hull's design is still the SELLER's, and the refit
    // editor clones ship.design (which must be buyer-owned). So clone the class's DESIGN-SHIP TEMPLATE
    // (NOT each hull's design) ONCE per class, repoint the hull to it, and gate the clone id. Deduped by
    // classKey via a session cache + the persisted map.
    private static void AdoptDesign(Ship moved, Player buyer, Player? seller, string classKey)
    {
        if (string.IsNullOrEmpty(classKey)) classKey = Safe(() => moved.design.id.ToString(), "") ?? "";
        if (classKey.Length == 0) { Log("adopt: no class key"); return; }

        Ship? clone = ResolveExistingClone(buyer, classKey);
        if (clone == null)
        {
            // Clone the DESIGN TEMPLATE (the class design ship), not the built hull's per-ship design.
            Ship? template = (seller != null ? FindSellerDesign(seller, classKey) : null) ?? Safe(() => moved.design, null);
            if (template == null) { Log($"adopt: no template design for classKey={classKey}"); return; }
            try { clone = PlayerController.Instance.CloneDesign(template, buyer); }
            catch (Exception ex) { Log($"CloneDesign threw: {ex.GetType().Name}: {ex.Message}"); return; }
            if (clone == null) { Log("adopt: CloneDesign returned null"); return; }
            string newId = Safe(() => clone.id.ToString(), "") ?? "";
            AllyPurchaseState.SetDesignClone(classKey, newId);
            AllyPurchaseState.AddRestricted(newId);   // gate the clone (build-no/refit-yes)
            CloneCache[classKey] = clone;
            Log($"adopt PROBE: classKey={classKey} templateOwner={Safe(() => template.player?.data?.name, "?")} cloneId={newId} cloneOwner={Safe(() => clone.player?.data?.name, "?")} " +
                $"inDesignsAll={IsBuyerDesign(buyer, newId)} templTon={Safe(() => template.Tonnage(), 0f):0} cloneTon={Safe(() => clone.Tonnage(), 0f):0}");
        }

        Ship c = clone;
        CloneCache[classKey] = c;
        Safe(() => { moved.design = c; return true; }, false);
    }

    private static Ship? ResolveExistingClone(Player buyer, string classKey)
    {
        if (CloneCache.TryGetValue(classKey, out Ship? cached) && cached != null) return cached;
        string? existing = AllyPurchaseState.GetDesignClone(classKey);
        if (!string.IsNullOrEmpty(existing))
        {
            Ship? c = FindBuyerDesign(buyer, existing);
            if (c != null) { CloneCache[classKey] = c; return c; }
        }
        return null;
    }

    private static Ship? FindSellerDesign(Player seller, string designId)
    {
        if (seller == null || string.IsNullOrEmpty(designId)) return null;
        foreach (Ship d in SafeDesigns(seller))
            if (string.Equals(Safe(() => d.id.ToString(), ""), designId, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    // Retroactively adopt designs for already-delivered purchased ships whose design is still the
    // seller's (bought before this fix existed). Idempotent + cheap: skips already buyer-owned designs.
    internal static void AdoptOwnedPurchasedDesigns()
    {
        Player buyer = PlayerController.Instance;
        if (buyer == null) return;

        var fleet = new List<Ship>();
        try { foreach (Ship s in buyer.GetFleetAll()) if (s != null) fleet.Add(s); } catch { return; }
        int adopted = 0;
        foreach (Ship s in fleet)
        {
            Ship? d = Safe(() => s.design, null);
            if (d == null) continue;
            // A ship you OWN whose DESIGN belongs to someone else = a bought/foreign hull whose design
            // was never copied to your book (the game's warship-trade delivers it WITHOUT a design copy).
            // Clone it so it's refittable. (Captured ships already get a design copy from the game, so
            // their design.player == you and they're skipped here.) NOT gated on the restricted set —
            // the game can deliver a bought hull before our own delivery path ever tags it.
            bool foreign = Safe(() => d.player != null && d.player.Pointer != buyer.Pointer, false);
            if (!foreign) continue;
            string did = Safe(() => d.id.ToString(), "") ?? "";
            if (did.Length == 0) continue;
            AdoptDesign(s, buyer, Safe(() => d.player, null), did);
            adopted++;
        }
        if (adopted > 0) Log($"retroactive adopt: {adopted} owned ship(s) with a foreign design cloned to your book (refit-enabled)");
    }

    // Keep ordered ships building: the AI seller (especially an over-capacity dock) may SUSPEND the
    // forced hulls so they never progress. Un-pause them each turn and log progress + paused state so
    // we can confirm whether un-pausing alone gets them building, or they're capacity-starved (0%).
    internal static void EnsureOrderedBuildsRunning()
    {
        if (AllyPurchaseState.Current.Orders.Count == 0) return;
        int turn = Safe(() => CampaignController.Instance.CurrentDate.turn, 0);
        foreach (AllyPurchaseState.Order o in new List<AllyPurchaseState.Order>(AllyPurchaseState.Current.Orders))
        {
            Ship? hull = TryResolveOrderHull(o);
            if (hull == null)
            {
                // Hull is gone from the seller's fleet — the game's warship-trade delivered it (the
                // retroactive-adopt sweep will clone its design). Clear the stale order to stop the spam.
                Log($"build watch: \"{o.DesignName}\" hull {o.HullId} gone from {o.Seller}'s fleet — clearing stale order (delivered by game)");
                AllyPurchaseState.RemoveOrder(o);
                continue;
            }
            bool paused = Safe(() => hull.isBuildingPaused, false);
            if (paused) Safe(() => { hull.isBuildingPaused = false; return true; }, false);

            // Drive the build in PARALLEL on the quoted schedule (the offload premise): the ally's dock
            // builds serially/one-at-a-time, so put a FLOOR on progress = elapsed/quotedMonths. Using
            // max(actual, scheduled) means if the ally builds one faster, we don't interfere.
            int months = o.BuildMonths > 0 ? o.BuildMonths : Safe(() => hull.BuildingTime(true), 24);
            if (months < 1) months = 1;
            int elapsed = turn - o.StartTurn; if (elapsed < 0) elapsed = 0;
            float scheduled = (float)elapsed / months * 100f;
            if (scheduled > 100f) scheduled = 100f;
            float actual = Safe(() => hull.buildingProgress, 0f);
            if (scheduled > actual) Safe(() => { hull.buildingProgress = scheduled; return true; }, false);

            float prog = Safe(() => hull.buildingProgress, -1f);
            bool building = Safe(() => hull.isBuilding, false);
            bool commissioning = Safe(() => hull.isCommissioning, false);
            Log($"build watch: \"{o.DesignName}\" prog={prog:0.#}% (sched={scheduled:0.#}%) wasPaused={paused} building={building} commissioning={commissioning} elapsed={elapsed}/{months}mo turn={turn} seller={o.Seller}");
        }
    }

    private static Ship? FindBuyerDesign(Player buyer, string designId)
    {
        if (string.IsNullOrEmpty(designId)) return null;
        foreach (Ship d in SafeDesigns(buyer))
            if (string.Equals(Safe(() => d.id.ToString(), ""), designId, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    private static bool IsBuyerDesign(Player buyer, string designId) => FindBuyerDesign(buyer, designId) != null;

    private static List<Ship> SafeDesigns(Player p)
    {
        var result = new List<Ship>();
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<Ship>(p.designsAll);
            foreach (Ship s in list) if (s != null) result.Add(s);
        }
        catch { }
        return result;
    }

    // ----- helpers -----
    private static AllyPurchaseState.Order? FindOrderByHull(string hullId)
    {
        if (string.IsNullOrEmpty(hullId)) return null;
        foreach (AllyPurchaseState.Order o in AllyPurchaseState.Current.Orders)
            if (string.Equals(o.HullId, hullId, StringComparison.OrdinalIgnoreCase)) return o;
        return null;
    }

    private static Player? FindPlayer(string nation)
    {
        var data = CampaignController.Instance?.CampaignData;
        if (data?.Players == null || string.IsNullOrEmpty(nation)) return null;
        foreach (Player p in data.Players)
            if (p != null && string.Equals(Safe(() => p.data?.name, ""), nation, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private static Ship? FindHull(Player seller, string hullId)
    {
        if (seller == null || string.IsNullOrEmpty(hullId)) return null;
        try { foreach (Ship s in seller.GetFleetAll()) if (s != null && string.Equals(Safe(() => s.id.ToString(), ""), hullId, StringComparison.OrdinalIgnoreCase)) return s; }
        catch { }
        return null;
    }

    // Resolve the live hull GameObject for an order (seller's in-build fleet). Used by the fleet-tab
    // "incoming purchases" view to read live build progress.
    internal static Ship? TryResolveOrderHull(AllyPurchaseState.Order o)
    {
        if (o == null) return null;
        Player? seller = FindPlayer(o.Seller);
        return seller == null ? null : FindHull(seller, o.HullId);
    }

    // The human player's controlled ports, largest capacity first (delivery-port choices).
    internal static List<PortElement> BuyerPorts()
    {
        var result = new List<PortElement>();
        var byPlayer = Safe(() => CampaignController.Instance?.CampaignData?.ProvincesByPlayer, null);
        if (byPlayer == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var kvp in byPlayer)
            {
                var provs = kvp.Value;
                if (provs == null) continue;
                foreach (Province pr in provs)
                {
                    if (pr == null || !Safe(() => pr.ControllerPlayer?.isMain ?? false, false)) continue;
                    var ports = Safe(() => pr.Ports, null);
                    if (ports == null) continue;
                    foreach (PortElement pe in ports)
                    {
                        if (pe == null) continue;
                        string? pid = Safe(() => pe.Id, null);
                        if (pid == null || !seen.Add(pid)) continue;
                        result.Add(pe);
                    }
                }
            }
        }
        catch { }
        result.Sort((a, b) => Safe(() => b.GetPortCapacityWithoutDamage(), 0).CompareTo(Safe(() => a.GetPortCapacityWithoutDamage(), 0)));
        return result;
    }

    internal static PortElement? FindBuyerPort(string portId)
    {
        if (string.IsNullOrEmpty(portId)) return null;
        foreach (PortElement pe in BuyerPorts())
            if (string.Equals(Safe(() => pe.Id, ""), portId, StringComparison.OrdinalIgnoreCase)) return pe;
        return null;
    }

    // Player-initiated cancel: forfeit the deposit (penalty); the seller keeps the partial hull.
    internal static void CancelOrder(AllyPurchaseState.Order o)
    {
        if (o == null) return;
        Ship? hull = TryResolveOrderHull(o);
        if (hull != null) Safe(() => { hull.ForSaleTo = null; return true; }, false); // stop delivering; seller keeps it
        AllyPurchaseState.RemoveOrder(o);
        Log($"CANCELLED order \"{o.DesignName}\" from {o.Seller} — deposit {o.Deposit:0} forfeit (penalty); {o.Seller} keeps the hull.");
    }

    private static string SafeName(Ship design)
        => Safe(() => design.design != null ? design.design.Name(false, false, false, false, true) : design.Name(false, false, false, false, true), null)
           ?? (Safe(() => design.shipType?.name, "?") ?? "?");
}
