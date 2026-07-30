using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;

namespace MintChipPlus.GameData;

// Per-ship career "service records": each ship accumulates a history of the battles it fought, with
// absolute damage dealt/received, ships sunk (finisher) and wrecked (most damage), and whether it
// survived. Captured at battle end by BattleShipRecorder (via Ui.RegisterTakenDamage) and persisted
// per-campaign in ModCampaignState, keyed by Ship.id so it survives across battles/saves. The viewer
// is a separate (future) surface; this is the data layer.
//
// Serialization is a control-char-delimited blob (no JSON dep, collision-proof vs ship/battle names):
//   records separated by RS(); record = id US name US type US entriesBlob;
//   entries separated by GS(); entry = date FS dealt FS received FS kills FS wrecks FS sunk(0/1).
internal static class ShipServiceRecords
{
    private const string Feature = "ship_records";
    private const char VS = '', VF = ''; // victim list (within an entry) + victim fields
    private const char RS = '', US = '', GS = '', FS = '';

    internal struct BattleResult
    {
        public string Id, Name, Type, Date;
        public float Dealt, Received;
        public int Kills, Wrecks;
        public bool Sunk;
        public List<VictimHit>? Victims;
    }

    internal sealed class Entry
    {
        public string Date = "";
        public float Dealt, Received;
        public int Kills, Wrecks;
        public bool Sunk;
        public readonly List<VictimHit> Victims = new();
    }

    // One enemy ship this ship hit in a battle: its type + tonnage, damage dealt to it, sank/wrecked.
    internal sealed class VictimHit
    {
        public string Name = "";
        public string Type = "";
        public float Tonnage;
        public float Damage;
        public bool Sank, Wrecked;
    }

    internal sealed class Record
    {
        public string Id = "", Name = "", Type = "";
        public readonly List<Entry> Battles = new();
    }

    // Append one battle's results for the player's participating ships, then persist.
    internal static void RecordBattle(List<BattleResult> results, string dateLabel)
    {
        try
        {
            if (results == null || results.Count == 0)
                return;
            Dictionary<string, Record> recs = Load();
            int newEntries = 0;
            foreach (BattleResult r in results)
            {
                if (string.IsNullOrEmpty(r.Id))
                    continue;
                if (!recs.TryGetValue(r.Id, out Record? rec) || rec == null)
                    recs[r.Id] = rec = new Record { Id = r.Id };
                rec.Name = r.Name ?? rec.Name;
                rec.Type = r.Type ?? rec.Type;
                var entry = new Entry
                {
                    Date = string.IsNullOrEmpty(r.Date) ? dateLabel : r.Date,
                    Dealt = r.Dealt,
                    Received = r.Received,
                    Kills = r.Kills,
                    Wrecks = r.Wrecks,
                    Sunk = r.Sunk,
                };
                if (r.Victims != null)
                    entry.Victims.AddRange(r.Victims);
                rec.Battles.Add(entry);
                newEntries++;
            }
            Store(recs);
            Melon<MintChipPlusMod>.Logger.Msg(
                $"UADMC ship records: logged {newEntries} ship-battle entr(ies) ({recs.Count} ship(s) tracked) for {dateLabel}.");
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC ship records: record failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Read API for the (future) viewer.
    internal static Dictionary<string, Record> Load()
    {
        var recs = new Dictionary<string, Record>(StringComparer.Ordinal);
        try
        {
            string blob = ModCampaignState.Load(Feature);
            if (string.IsNullOrEmpty(blob))
                return recs;
            foreach (string recStr in blob.Split(RS))
            {
                if (string.IsNullOrEmpty(recStr))
                    continue;
                string[] parts = recStr.Split(US);
                if (parts.Length < 4)
                    continue;
                var rec = new Record { Id = parts[0], Name = parts[1], Type = parts[2] };
                if (parts[3].Length > 0)
                {
                    foreach (string eStr in parts[3].Split(GS))
                    {
                        if (string.IsNullOrEmpty(eStr))
                            continue;
                        string[] f = eStr.Split(FS);
                        if (f.Length < 6)
                            continue;
                        var entry = new Entry
                        {
                            Date = f[0],
                            Dealt = ParseF(f[1]),
                            Received = ParseF(f[2]),
                            Kills = ParseI(f[3]),
                            Wrecks = ParseI(f[4]),
                            Sunk = f[5] == "1",
                        };
                        if (f.Length >= 7 && f[6].Length > 0)
                        {
                            foreach (string vStr in f[6].Split(VS))
                            {
                                if (string.IsNullOrEmpty(vStr)) continue;
                                string[] vf = vStr.Split(VF);
                                if (vf.Length < 3) continue;
                                int flags = ParseI(vf[2]);
                                entry.Victims.Add(new VictimHit
                                {
                                    Name = vf[0], Damage = ParseF(vf[1]),
                                    Sank = (flags & 1) != 0, Wrecked = (flags & 2) != 0,
                                    Type = vf.Length > 3 ? vf[3] : "",
                                    Tonnage = vf.Length > 4 ? ParseF(vf[4]) : 0f,
                                });
                            }
                        }
                        rec.Battles.Add(entry);
                    }
                }
                if (!string.IsNullOrEmpty(rec.Id))
                    recs[rec.Id] = rec;
            }
        }
        catch (Exception ex)
        {
            Melon<MintChipPlusMod>.Logger.Warning($"UADMC ship records: load failed — {ex.GetType().Name}: {ex.Message}");
        }
        return recs;
    }

    private static void Store(Dictionary<string, Record> recs)
    {
        var sb = new StringBuilder();
        bool firstRec = true;
        foreach (Record rec in recs.Values)
        {
            if (!firstRec) sb.Append(RS);
            firstRec = false;
            sb.Append(Clean(rec.Id)).Append(US).Append(Clean(rec.Name)).Append(US).Append(Clean(rec.Type)).Append(US);
            bool firstE = true;
            foreach (Entry e in rec.Battles)
            {
                if (!firstE) sb.Append(GS);
                firstE = false;
                sb.Append(Clean(e.Date)).Append(FS)
                  .Append(e.Dealt.ToString("0.###", CultureInfo.InvariantCulture)).Append(FS)
                  .Append(e.Received.ToString("0.###", CultureInfo.InvariantCulture)).Append(FS)
                  .Append(e.Kills.ToString(CultureInfo.InvariantCulture)).Append(FS)
                  .Append(e.Wrecks.ToString(CultureInfo.InvariantCulture)).Append(FS)
                  .Append(e.Sunk ? "1" : "0").Append(FS);
                bool firstV = true;
                foreach (VictimHit v in e.Victims)
                {
                    if (!firstV) sb.Append(VS);
                    firstV = false;
                    sb.Append(Clean(v.Name)).Append(VF)
                      .Append(v.Damage.ToString("0.###", CultureInfo.InvariantCulture)).Append(VF)
                      .Append(((v.Sank ? 1 : 0) | (v.Wrecked ? 2 : 0)).ToString(CultureInfo.InvariantCulture)).Append(VF)
                      .Append(Clean(v.Type)).Append(VF)
                      .Append(v.Tonnage.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
        }
        ModCampaignState.Save(Feature, sb.ToString());
    }

    // Strip the delimiter control chars from any user-facing string before serializing.
    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace(RS, ' ').Replace(US, ' ').Replace(GS, ' ').Replace(FS, ' ').Replace(VS, ' ').Replace(VF, ' ');
    }

    private static float ParseF(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    private static int ParseI(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
