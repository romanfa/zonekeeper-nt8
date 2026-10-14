// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Accessed only by the serialized fill worker (or by a unit test).
    public sealed class TradeBuilder
    {
        private const string RestartNotSent = "Open trade from before the restart could not be checked. Not sent. You can add it later by CSV.";
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
                t.AwaitingContinuity = !t.AwaitingFlat;
                if (t.ExecutionCheckpoint != null) histories[t.Key] = t.ExecutionCheckpoint.Where(x => x != null).Take(ExecutionEvidence.MaximumCount).Select(x => x.Copy()).ToList();
                foreach (var fill in t.Fills) if (fill != null && !string.IsNullOrEmpty(fill.ExecId)) Remember(t.Account, fill.ExecId);
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
            if (string.IsNullOrEmpty(f.ExecId) || string.IsNullOrEmpty(f.Account) || string.IsNullOrEmpty(f.Instrument)) return closed;
            OpenTrade trade; open.TryGetValue(f.Key, out trade);
            if (f.Qty < 0 || (f.Qty == 0 && (trade == null || !trade.AwaitingFlat))) return closed;
            if (!Remember(f.Account, f.ExecId)) return closed;
            if (!f.IsPlayback && f.ArrivedMs - f.TimeMs > 15 * 60000L)
            { log("Skipped an old fill (from before ZoneKeeper started)."); return closed; }
            if (trade != null && trade.AwaitingFlat)
            {
                // Only a new accepted Add reports a newly observed position; corrections are historical.
                if (f.PositionAfter == 0) open.Remove(f.Key);
                else trade.Net = f.PositionAfter;
                if (f.Qty > 0) Track(f);
                return closed;
            }
            Track(f);
            int signed = f.IsBuy ? f.Qty : -f.Qty;
            if (trade != null && trade.AwaitingContinuity && Math.Sign(signed) != Math.Sign(trade.Net) && f.Qty >= Math.Abs(trade.Net))
            {
                // A close/reversal ahead of startup proof cannot send or create a proved remainder.
                RejectRestored(trade);
                if (f.PositionAfter == 0) open.Remove(f.Key);
                else trade.Net = f.PositionAfter;
                return closed;
            }
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
            if (!trade.Broken)
            {
                trade.Broken = true;
                if (!trade.AwaitingContinuity && !trade.AwaitingFlat)
                    log("ZoneKeeper missed a fill of this trade (" + trade.Account + " · " + trade.Instrument + ").");
            }
        }
        private void Check(OpenTrade trade, int position)
        {
            if (trade.Net != position) MarkBroken(trade);
        }
        private void DropBrokenFlat(OpenTrade trade, Fill fill)
        {
            if (!trade.AwaitingFlat && trade.Broken && fill.PositionAfter == 0) { open.Remove(trade.Key); NotSent(trade); }
        }
        private void NotSent(OpenTrade trade)
        {
            if (trade.AwaitingFlat) return;
            if (trade.AwaitingContinuity) { RejectRestored(trade); return; }
            string why = trade.UnknownStart ? "the start or continuity of this trade could not be verified" :
                trade.Broken ? "ZoneKeeper missed a fill of this trade" : "currency " + (trade.Currency ?? "unknown") + " is not supported yet";
            log("Not sent: " + why + " (" + trade.Account + " · " + trade.Instrument + ").");
        }
        private void RejectRestored(OpenTrade trade)
        {
            log(RestartNotSent);
            trade.AwaitingFlat = true; trade.AwaitingContinuity = false; trade.UnknownStart = true;
        }
        public void OnUpdate(Fill f)
        {
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y != null && y.ExecId == f.ExecId)).ToList();
            if (affected.Count == 0)
            {
                OpenTrade current;
                if (!open.TryGetValue(f.Key, out current) || !current.AwaitingFlat)
                    log("A closed fill changed; affected waiting trades are invalidated. Already dispatched trades cannot be changed.");
                return;
            }
            foreach (var trade in affected)
            {
                if (trade.AwaitingFlat) continue;
                var applied = trade.Fills.First(x => x != null && x.ExecId == f.ExecId);
                var evidence = ExecutionEvidence.From(f);
                evidence.Commission = applied.SourceCommission;
                if (applied.Evidence == null || !applied.Evidence.Matches(evidence))
                {
                    if (trade.AwaitingContinuity) RejectRestored(trade);
                    else MarkBroken(trade);
                }
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
            var affected = open.Values.Where(x => x.Account == f.Account && x.Fills.Any(y => y != null && y.ExecId == f.ExecId)).ToList();
            if (affected.Count != 0)
                foreach (var trade in affected)
                {
                    if (trade.AwaitingFlat) continue;
                    if (trade.AwaitingContinuity) RejectRestored(trade);
                    else MarkBroken(trade);
                    DropBrokenFlat(trade, f);
                }
            else
            {
                OpenTrade current;
                if (!open.TryGetValue(f.Key, out current) || !current.AwaitingFlat)
                    log("A closed fill was removed; affected waiting trades are invalidated. Already dispatched trades cannot be changed.");
            }
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
        private static bool CheckpointMatches(OpenTrade trade, List<ExecutionEvidence> actual, bool allowLivePrefix)
        {
            var expected = trade.ExecutionCheckpoint;
            if (expected == null || expected.Count == 0 || expected.Count > ExecutionEvidence.MaximumCount ||
                actual.Count > ExecutionEvidence.MaximumCount || expected.Any(x => x == null || !x.Complete)) return false;
            int prefix = actual.Count - expected.Count;
            if (prefix < 0) return false;
            if (prefix != 0)
            {
                // Only a fully observed live checkpoint starting from flat can own older leading history.
                // A restored/unknown/broken trade still needs the entire unchanged checkpoint.
                var first = expected[0];
                if (!allowLivePrefix || !trade.Sendable || first.PositionAfter != (first.IsBuy ? first.Qty : -first.Qty)) return false;
            }
            return expected.Zip(actual.Skip(prefix), (x, y) => x.Matches(y)).All(x => x);
        }
        private static bool RestoredFillsMatch(OpenTrade trade, List<ExecutionEvidence> actual)
        {
            if (trade.UnknownStart || trade.Broken || trade.EvidenceLimitExceeded || trade.Fills.Count == 0 ||
                trade.Fills.Count > ExecutionEvidence.MaximumCount || actual.Any(x => !x.Complete) ||
                actual.Select(x => x.ExecId).Distinct(StringComparer.Ordinal).Count() != actual.Count) return false;
            var byId = actual.ToDictionary(x => x.ExecId, StringComparer.Ordinal);
            long entries = 0, exits = 0; decimal entryValue = 0, exitValue = 0;
            try
            {
                foreach (var fill in trade.Fills)
                {
                    ExecutionEvidence found;
                    var saved = fill == null ? null : fill.Evidence;
                    // API entry IDs are capped and sanitized. Proof uses every raw entry and exit ID instead.
                    if (saved == null || !saved.Complete || string.IsNullOrEmpty(fill.ExecId) ||
                        fill.ExecId != saved.ExecId || saved.Instrument != trade.Instrument ||
                        fill.SourceQty != saved.Qty || fill.AppliedQty <= 0 || fill.AppliedQty > fill.SourceQty ||
                        fill.Price != saved.Price || fill.SourceCommission != saved.Commission || fill.IsBuy != saved.IsBuy ||
                        fill.IsEntry != (fill.IsBuy == trade.IsLong) || !byId.TryGetValue(fill.ExecId, out found) || !saved.Matches(found)) return false;
                    if (fill.IsEntry) { entries += fill.AppliedQty; entryValue += fill.AppliedQty * fill.Price; }
                    else { exits += fill.AppliedQty; exitValue += fill.AppliedQty * fill.Price; }
                }
            }
            catch (OverflowException) { return false; }
            return entries == trade.EntryQty && exits == trade.ExitQty &&
                (trade.IsLong ? entries - exits : exits - entries) == trade.Net &&
                entryValue == trade.EntryValue && exitValue == trade.ExitValue;
        }
        public void OnResync(string account, IEnumerable<PositionSnapshot> positions, IEnumerable<string> executionIds)
        {
            OnResync(account, new AccountSnapshot(positions, executionIds.Select(id => new ExecutionEvidence { ExecId = id })));
        }
        public void OnResync(string account, AccountSnapshot snapshot)
        {
            var positions = snapshot.Positions; var evidence = snapshot.Executions;
            if (!snapshot.Consistent) log("Not sent: account snapshot was inconsistent; execution continuity could not be proved (" + account + ").");
            if (snapshot.Consistent) foreach (var x in evidence) if (!string.IsNullOrEmpty(x.ExecId)) Remember(account, x.ExecId);
            var current = positions.Where(x => x.Net != 0).ToDictionary(x => Helpers.PairKey(account, x.Instrument), StringComparer.Ordinal);
            foreach (var p in current.Values.Where(x => x.TickSize <= 0))
                log("Not sent: instrument tick size is missing or nonpositive (" + account + " · " + p.Instrument + ").");
            foreach (var t in open.Values.Where(x => x.Account == account).ToList())
            {
                PositionSnapshot p;
                bool hasPosition = current.TryGetValue(t.Key, out p);
                if (t.AwaitingFlat)
                {
                    // A missing position only proves flat when the whole snapshot is consistent.
                    if (snapshot.Consistent && !hasPosition) open.Remove(t.Key);
                    else if (hasPosition) t.Net = p.Net;
                    continue;
                }
                bool restored = t.AwaitingContinuity;
                var actual = evidence.Where(x => x.Instrument == t.Instrument).ToList();
                if (actual.Count > ExecutionEvidence.MaximumCount)
                    log("Not sent: execution history exceeded the local evidence limit (" + account + " · " + t.Instrument + ").");
                bool continuous = snapshot.Consistent && CheckpointMatches(t, actual, !restored) &&
                    (!restored || RestoredFillsMatch(t, actual));
                if (hasPosition && p.Net == t.Net && continuous && t.PositionAveragePrice.HasValue &&
                    p.TickSize > 0 && Math.Abs(p.AveragePrice - t.PositionAveragePrice.Value) <= p.TickSize * .001m &&
                    p.PointValue == t.PointValue && p.Currency == t.Currency)
                {
                    // Adopt proved leading history, so a later restart still uses exact full-history equality.
                    t.ExecutionCheckpoint = actual.Select(x => x.Copy()).ToList();
                    t.AwaitingContinuity = false;
                    log("Resumed an open trade: " + account + " · " + t.Instrument + " · " + (t.IsLong ? "long " : "short ") + Math.Abs(t.Net) + ".");
                }
                else if (restored)
                {
                    RejectRestored(t);
                    if (snapshot.Consistent && !hasPosition) open.Remove(t.Key);
                    else if (hasPosition) t.Net = p.Net;
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
                var unknown = OpenTrade.Start(f, f.Qty, 0); unknown.UnknownStart = true; unknown.AwaitingFlat = true;
                unknown.EntryExecIds.Clear(); unknown.Fills.Clear(); open[key] = unknown;
            }
        }
    }
}
