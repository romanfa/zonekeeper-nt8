// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Accessed only by the serialized fill worker (or by a unit test).
    public sealed class TradeBuilder
    {
        private readonly Dictionary<string, OpenTrade> open = new Dictionary<string, OpenTrade>(StringComparer.Ordinal);
        private readonly HashSet<string> unknownLogged = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> seenOrder = new Queue<string>();
        private readonly TimeZoneInfo localZone;
        private readonly Action<string> log;
        public TradeBuilder(TimeZoneInfo zone, Action<string> writeLog)
        { localZone = zone ?? TimeZoneInfo.Local; log = writeLog ?? delegate { }; }
        public List<OpenTrade> Open { get { return open.Values.ToList(); } }
        public int SeenCount { get { return seen.Count; } }
        private bool Remember(string account, string id)
        {
            string key = Helpers.PairKey(account, id);
            if (!seen.Add(key)) return false;
            seenOrder.Enqueue(key);
            while (seenOrder.Count > 5000) seen.Remove(seenOrder.Dequeue());
            return true;
        }
        public List<TradeRecord> Process(Fill fill)
        {
            string operation = Operation(fill);
            if (operation == "Update") { OnUpdate(fill); return new List<TradeRecord>(); }
            if (operation == "Remove") { OnRemove(fill); return new List<TradeRecord>(); }
            if (operation != "Add") { log("Skipped an unknown fill operation."); return new List<TradeRecord>(); }
            return OnAdd(fill);
        }
        public string Operation(Fill fill)
        { return string.IsNullOrEmpty(fill.Op) ? (seen.Contains(Helpers.PairKey(fill.Account, fill.ExecId)) ? "Update" : "Add") : fill.Op; }
        public List<TradeRecord> OnAdd(Fill f)
        {
            var closed = new List<TradeRecord>();
            if (f.Qty <= 0 || string.IsNullOrEmpty(f.ExecId) || string.IsNullOrEmpty(f.Account) || string.IsNullOrEmpty(f.Instrument)) return closed;
            if (!Remember(f.Account, f.ExecId)) return closed;
            int signed = f.IsBuy ? f.Qty : -f.Qty;
            long before = (long)f.PositionAfter - signed;
            OpenTrade trade; open.TryGetValue(f.Key, out trade);
            if (!f.IsPlayback && f.ArrivedMs - f.TimeMs > 15 * 60000L)
            {
                // Replays update only the unknown position, without creating a trade or a log line.
                if (trade != null && !trade.UnknownStart) unknownLogged.Remove(f.Key);
                MoveUnknown(f, trade); return closed;
            }
            if (trade != null && !trade.UnknownStart && trade.Net != before)
                MakeUnknown(trade, "ZoneKeeper missed a fill of this trade. Not sent. Add it later by CSV.");
            if (before == 0)
            { Start(f, f.Qty, f.Commission); return closed; }
            if (trade == null || trade.UnknownStart)
            {
                LogUnknown(f.Key, "Position in " + f.Instrument + " was open before ZoneKeeper started. It is not sent. Add it later by CSV.");
                MoveUnknown(f, trade);
                if (f.PositionAfter != 0 && Math.Sign(f.PositionAfter) != Math.Sign(before))
                {
                    int remainder = (int)Math.Abs((long)f.PositionAfter);
                    Start(f, remainder, f.Commission * remainder / f.Qty);
                }
                return closed;
            }
            if (Math.Sign(signed) == Math.Sign(before))
            { trade.AddEntry(f, f.Qty, f.Commission); CheckEvidenceLimit(trade); return closed; }
            int closeQty = (int)Math.Min(f.Qty, Math.Abs(before));
            decimal closeCommission = f.Commission * closeQty / f.Qty;
            trade.AddExit(f, closeQty, closeCommission); CheckEvidenceLimit(trade);
            if (trade.Net != 0) return closed;
            Drop(trade.Key);
            if (trade.Sendable) closed.Add(trade.ToRecord(f, localZone));
            else if (!trade.UnknownStart) log("Not sent: currency " + (trade.Currency ?? "unknown") + " is not supported yet (" + trade.Account + " · " + trade.Instrument + ").");
            int rest = f.Qty - closeQty;
            if (rest > 0) Start(f, rest, f.Commission - closeCommission);
            return closed;
        }
        private void Start(Fill fill, int quantity, decimal commission)
        { unknownLogged.Remove(fill.Key); open[fill.Key] = OpenTrade.Start(fill, quantity, commission); }
        private void Drop(string key) { open.Remove(key); unknownLogged.Remove(key); }
        private void LogUnknown(string key, string message) { if (unknownLogged.Add(key)) log(message); }
        private void MakeUnknown(OpenTrade trade, string reason)
        { trade.UnknownStart = true; trade.Broken = true; LogUnknown(trade.Key, reason + " (" + trade.Account + " · " + trade.Instrument + ")"); }
        private void MoveUnknown(Fill fill, OpenTrade trade)
        {
            if (fill.PositionAfter == 0) { Drop(fill.Key); return; }
            if (trade == null) trade = new OpenTrade { Account = fill.Account, Instrument = fill.Instrument,
                Fills = new List<AppliedFill>(), EntryExecIds = new List<string>(), ExitNames = new List<string>() };
            trade.UnknownStart = true; trade.Net = fill.PositionAfter; trade.IsLong = trade.Net > 0;
            open[fill.Key] = trade;
        }
        private void CheckEvidenceLimit(OpenTrade trade)
        {
            if (trade.EvidenceLimitExceeded && !trade.UnknownStart)
                MakeUnknown(trade, "Not sent: this trade exceeded the local execution-evidence limit.");
        }
        private void ApplyCorrection(OpenTrade trade, AppliedFill applied, ExecutionEvidence corrected)
        {
            var comparison = corrected.Copy(); comparison.Commission = applied.SourceCommission;
            if (applied.Evidence == null || !applied.Evidence.Matches(comparison))
                MakeUnknown(trade, "ZoneKeeper missed a fill of this trade: an execution changed. Not sent. Add it later by CSV.");
            else
            {
                // A reversal fill belongs partly to the old trade and partly to the new one.
                trade.Commission += (corrected.Commission - applied.SourceCommission) * applied.AppliedQty / applied.SourceQty;
                applied.SourceCommission = corrected.Commission; applied.Evidence = corrected.Copy();
            }
        }
        public void OnUpdate(Fill f)
        {
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y != null && y.ExecId == f.ExecId)).ToList();
            if (affected.Count == 0) { log("A closed fill changed; affected waiting trades are invalidated. Already dispatched trades cannot be changed."); return; }
            foreach (var trade in affected.Where(x => !x.UnknownStart))
            {
                var applied = trade.Fills.First(x => x != null && x.ExecId == f.ExecId);
                ApplyCorrection(trade, applied, ExecutionEvidence.From(f));
            }
        }
        public void OnRemove(Fill f)
        {
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y != null && y.ExecId == f.ExecId)).ToList();
            if (affected.Count == 0) { log("A closed fill was removed; affected waiting trades are invalidated. Already dispatched trades cannot be changed."); return; }
            foreach (var trade in affected.Where(x => !x.UnknownStart))
                MakeUnknown(trade, "ZoneKeeper missed a fill of this trade: an execution was removed. Not sent. Add it later by CSV.");
        }
        public void OnReset(string account) { DropAccount(account); log(account + " was reset. Its open trades were dropped."); }
        public void DropAccount(string account)
        { foreach (var key in open.Where(x => x.Value.Account == account).Select(x => x.Key).ToList()) Drop(key); }
        public void OnResync(string account, IEnumerable<PositionSnapshot> positions, IEnumerable<string> executionIds)
        { OnResync(account, new AccountSnapshot(positions, executionIds.Select(id => new ExecutionEvidence { ExecId = id }))); }
        public void OnResync(string account, AccountSnapshot snapshot)
        {
            if (!snapshot.Consistent) { log("Account snapshot was inconsistent; positions were left unchanged (" + account + ")."); return; }
            var current = snapshot.Positions.Where(x => x.Net != 0).ToDictionary(x => Helpers.PairKey(account, x.Instrument), StringComparer.Ordinal);
            var evidence = snapshot.Executions;
            var byId = evidence.ToLookup(x => x.ExecId, StringComparer.Ordinal);
            foreach (var trade in open.Values.Where(x => x.Account == account).ToList())
            {
                PositionSnapshot position;
                if (!current.TryGetValue(trade.Key, out position))
                {
                    if (!trade.UnknownStart) log("Not sent: the account no longer has this open position (" + account + " · " + trade.Instrument + ").");
                    Drop(trade.Key); continue;
                }
                if (!trade.UnknownStart)
                {
                    var history = evidence.Where(x => x.Instrument == trade.Instrument).ToList();
                    var first = trade.Fills.FirstOrDefault(x => x.IsEntry);
                    int anchor = first == null ? -1 : history.FindIndex(x => x.ExecId == first.ExecId);
                    var ids = new HashSet<string>(trade.Fills.Select(x => x.ExecId), StringComparer.Ordinal);
                    bool unexplained = anchor >= 0 && history.Skip(anchor + 1).Any(x => !ids.Contains(x.ExecId));
                    decimal tolerance = Math.Max(.000000001m, Math.Abs(trade.PositionAveragePrice ?? 0) * .0000000000001m);
                    if (position.Net != trade.Net || position.PointValue != trade.PointValue || position.Currency != trade.Currency ||
                        !trade.PositionAveragePrice.HasValue || Math.Abs(position.AveragePrice - trade.PositionAveragePrice.Value) > tolerance || unexplained)
                        MakeUnknown(trade, "Not sent: the position or execution list changed; continuity of this trade is unknown.");
                    foreach (var applied in trade.Fills)
                    {
                        if (trade.UnknownStart) break;
                        // Missing history proves nothing; a visible amendment must agree with our raw evidence.
                        var corrected = byId[applied.ExecId].FirstOrDefault();
                        if (corrected != null) ApplyCorrection(trade, applied, corrected);
                    }
                }
                if (trade.UnknownStart) trade.Net = position.Net;
            }
            foreach (var position in current.Values)
            {
                string key = Helpers.PairKey(account, position.Instrument);
                if (open.ContainsKey(key)) continue;
                open[key] = new OpenTrade { Account = account, Instrument = position.Instrument, Net = position.Net, IsLong = position.Net > 0,
                    UnknownStart = true, Fills = new List<AppliedFill>(), EntryExecIds = new List<string>(), ExitNames = new List<string>() };
                LogUnknown(key, "Position in " + position.Instrument + " was open before ZoneKeeper started. It is not sent. Add it later by CSV.");
            }
        }
    }
}
