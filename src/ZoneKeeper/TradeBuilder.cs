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
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> seenOrder = new Queue<string>();
        private readonly Dictionary<string, List<ExecutionEvidence>> histories = new Dictionary<string, List<ExecutionEvidence>>(StringComparer.Ordinal);
        private readonly TimeZoneInfo localZone;
        private readonly Action<string> log;
        public TradeBuilder(TimeZoneInfo zone, Action<string> writeLog)
        {
            localZone = zone ?? TimeZoneInfo.Local; log = writeLog ?? delegate { };
        }
        public List<OpenTrade> Open { get { return open.Values.ToList(); } }
        public int SeenCount { get { return seen.Count; } }
        public void Restore(IEnumerable<OpenTrade> trades)
        {
            foreach (var t in trades)
            {
                if (t == null || string.IsNullOrEmpty(t.Account) || string.IsNullOrEmpty(t.Instrument) || t.Net == 0 ||
                    t.EntryQty <= 0 || t.PointValue <= 0 || t.Fills == null || t.EntryExecIds == null || t.ExitNames == null)
                { log("Skipped invalid saved open trade."); continue; }
                open[t.Key] = t;
                t.AwaitingContinuity = true;
                if (t.ExecutionCheckpoint != null) histories[t.Key] = t.ExecutionCheckpoint.Where(x => x != null).Take(ExecutionEvidence.MaximumCount).Select(x => x.Copy()).ToList();
                foreach (var fill in t.Fills) Remember(t.Account, fill.ExecId);
            }
        }
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
        private void Track(Fill fill)
        {
            List<ExecutionEvidence> history;
            if (!histories.TryGetValue(fill.Key, out history)) histories[fill.Key] = history = new List<ExecutionEvidence>();
            int existing = history.FindIndex(x => x.ExecId == fill.ExecId);
            if (existing >= 0) history[existing] = ExecutionEvidence.From(fill);
            else history.Add(ExecutionEvidence.From(fill));
            if (history.Count > ExecutionEvidence.MaximumCount) history.RemoveRange(0, history.Count - ExecutionEvidence.MaximumCount);
        }
        private void Checkpoint(OpenTrade trade)
        {
            if (trade.EvidenceLimitExceeded && !trade.Broken)
            { trade.Broken = true; log("Not sent: this trade exceeded the local execution-evidence limit (" + trade.Account + " · " + trade.Instrument + ")."); }
            List<ExecutionEvidence> history;
            trade.ExecutionCheckpoint = histories.TryGetValue(trade.Key, out history) ? history.Select(x => x.Copy()).ToList() : null;
        }
        public List<TradeRecord> OnAdd(Fill f)
        {
            var closed = new List<TradeRecord>();
            if (f.Qty <= 0 || string.IsNullOrEmpty(f.ExecId) || string.IsNullOrEmpty(f.Account) || string.IsNullOrEmpty(f.Instrument)) return closed;
            if (!Remember(f.Account, f.ExecId)) return closed;
            if (!f.IsPlayback && f.ArrivedMs - f.TimeMs > 15 * 60000L)
            { log("Skipped an old fill (from before ZoneKeeper started)."); return closed; }
            Track(f);
            OpenTrade trade; open.TryGetValue(f.Key, out trade);
            int signed = f.IsBuy ? f.Qty : -f.Qty;
            if (trade == null)
            {
                trade = OpenTrade.Start(f, f.Qty, f.Commission); Checkpoint(trade); open[f.Key] = trade;
                Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed;
            }
            if (Math.Sign(signed) == Math.Sign(trade.Net))
            {
                trade.AddEntry(f, f.Qty, f.Commission); Checkpoint(trade); Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed;
            }
            int closeQty = Math.Min(f.Qty, Math.Abs(trade.Net));
            decimal closeCommission = f.Commission * closeQty / f.Qty;
            trade.AddExit(f, closeQty, closeCommission);
            Checkpoint(trade);
            if (trade.Net != 0) { Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed; }
            open.Remove(f.Key);
            int rest = f.Qty - closeQty;
            int expectedRemainder = f.IsBuy ? rest : -rest;
            bool mismatch = f.PositionAfter != expectedRemainder;
            if (mismatch) MarkBroken(trade);
            if (trade.Sendable) closed.Add(trade.ToRecord(f, localZone));
            else NotSent(trade);
            if (rest > 0)
            {
                var next = OpenTrade.Start(f, rest, f.Commission - closeCommission); open[f.Key] = next;
                Checkpoint(next);
                if (mismatch || trade.Broken || trade.AwaitingContinuity) MarkBroken(next);
                Check(next, f.PositionAfter); DropBrokenFlat(next, f);
            }
            return closed;
        }
        private void MarkBroken(OpenTrade trade)
        {
            if (!trade.Broken) { trade.Broken = true; log("ZoneKeeper missed a fill of this trade (" + trade.Account + " · " + trade.Instrument + ")."); }
        }
        private void Check(OpenTrade trade, int position)
        {
            if (trade.Net != position) MarkBroken(trade);
        }
        private void DropBrokenFlat(OpenTrade trade, Fill fill)
        {
            if (trade.Broken && fill.PositionAfter == 0) { open.Remove(trade.Key); NotSent(trade); }
        }
        private void NotSent(OpenTrade trade)
        {
            string why = trade.UnknownStart || trade.AwaitingContinuity ? "the start or continuity of this trade could not be verified" :
                trade.Broken ? "ZoneKeeper missed a fill of this trade" : "currency " + (trade.Currency ?? "unknown") + " is not supported yet";
            log("Not sent: " + why + " (" + trade.Account + " · " + trade.Instrument + ").");
        }
        public void OnUpdate(Fill f)
        {
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y.ExecId == f.ExecId)).ToList();
            if (affected.Count == 0)
            { log("A closed fill changed; affected waiting trades are invalidated. Already dispatched trades cannot be changed."); return; }
            foreach (var trade in affected)
            {
                var applied = trade.Fills.First(x => x.ExecId == f.ExecId);
                var evidence = ExecutionEvidence.From(f);
                evidence.Commission = applied.SourceCommission;
                if (applied.Evidence == null || !applied.Evidence.Matches(evidence)) MarkBroken(trade);
                else
                {
                    // A reversal fill belongs partly to the old trade and partly to the new one.
                    trade.Commission += (f.Commission - applied.SourceCommission) * applied.AppliedQty / applied.SourceQty;
                    applied.SourceCommission = f.Commission;
                    applied.Evidence = ExecutionEvidence.From(f);
                }
                Track(f); Checkpoint(trade);
                DropBrokenFlat(trade, f);
            }
        }
        public void OnRemove(Fill f)
        {
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y.ExecId == f.ExecId)).ToList();
            if (affected.Count != 0)
                foreach (var trade in affected) { MarkBroken(trade); DropBrokenFlat(trade, f); }
            else log("A closed fill was removed; affected waiting trades are invalidated. Already dispatched trades cannot be changed.");
            List<ExecutionEvidence> history;
            if (histories.TryGetValue(f.Key, out history)) history.RemoveAll(x => x.ExecId == f.ExecId);
        }
        public void OnReset(string account)
        {
            DropAccount(account); log(account + " was reset. Its open trades were dropped.");
        }
        public void DropAccount(string account)
        {
            foreach (var key in open.Where(x => x.Value.Account == account).Select(x => x.Key).ToList()) open.Remove(key);
        }
        public void OnResync(string account, IEnumerable<PositionSnapshot> positions, IEnumerable<string> executionIds)
        {
            OnResync(account, new AccountSnapshot(positions, executionIds.Select(id => new ExecutionEvidence { ExecId = id })));
        }
        public void OnResync(string account, AccountSnapshot snapshot)
        {
            var positions = snapshot.Positions; var evidence = snapshot.Executions;
            if (snapshot.Consistent) foreach (var x in evidence) if (!string.IsNullOrEmpty(x.ExecId)) Remember(account, x.ExecId);
            var current = positions.Where(x => x.Net != 0).ToDictionary(x => Helpers.PairKey(account, x.Instrument), StringComparer.Ordinal);
            foreach (var t in open.Values.Where(x => x.Account == account).ToList())
            {
                PositionSnapshot p;
                var expected = t.ExecutionCheckpoint;
                var actual = evidence.Where(x => x.Instrument == t.Instrument).ToList();
                bool continuous = snapshot.Consistent && expected != null && expected.Count > 0 && expected.Count <= ExecutionEvidence.MaximumCount &&
                    actual.Count == expected.Count && expected.Zip(actual, (x, y) => x != null && x.Complete && x.Matches(y)).All(x => x);
                if (current.TryGetValue(t.Key, out p) && p.Net == t.Net && t.PositionAveragePrice.HasValue &&
                    p.AveragePrice == t.PositionAveragePrice.Value && p.PointValue == t.PointValue && p.Currency == t.Currency && continuous)
                {
                    t.AwaitingContinuity = false;
                    log("Resumed an open trade: " + account + " · " + t.Instrument + " · " + (t.IsLong ? "long " : "short ") + Math.Abs(t.Net) + ".");
                }
                else
                { open.Remove(t.Key); log("Not sent: execution continuity could not be proved (" + account + " · " + t.Instrument + ")."); }
            }
            foreach (var group in evidence.Where(x => x.Complete).GroupBy(x => x.Instrument))
                histories[Helpers.PairKey(account, group.Key)] = group.Take(ExecutionEvidence.MaximumCount).Select(x => x.Copy()).ToList();
            foreach (var p in current.Values)
            {
                string key = Helpers.PairKey(account, p.Instrument);
                if (open.ContainsKey(key)) continue;
                var f = new Fill { Account = account, Instrument = p.Instrument, ExecId = "unknown-start", Qty = Math.Abs(p.Net),
                    IsBuy = p.Net > 0, Price = p.AveragePrice, PointValue = p.PointValue, Currency = p.Currency, TimeMs = Helpers.NowMs() };
                var unknown = OpenTrade.Start(f, f.Qty, 0); unknown.UnknownStart = true; unknown.EntryExecIds.Clear(); unknown.Fills.Clear(); open[key] = unknown;
            }
        }
    }
}
