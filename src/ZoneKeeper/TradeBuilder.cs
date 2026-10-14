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
            string operation = fill.Op;
            if (string.IsNullOrEmpty(operation)) operation = seen.Contains(Helpers.PairKey(fill.Account, fill.ExecId)) ? "Update" : "Add";
            if (operation == "Update") { OnUpdate(fill); return new List<TradeRecord>(); }
            if (operation == "Remove") { OnRemove(fill); return new List<TradeRecord>(); }
            if (operation != "Add") { log("Skipped an unknown fill operation."); return new List<TradeRecord>(); }
            return OnAdd(fill);
        }
        public List<TradeRecord> OnAdd(Fill f)
        {
            var closed = new List<TradeRecord>();
            if (f.Qty <= 0 || string.IsNullOrEmpty(f.ExecId) || string.IsNullOrEmpty(f.Account) || string.IsNullOrEmpty(f.Instrument)) return closed;
            if (!Remember(f.Account, f.ExecId)) return closed;
            if (!f.IsPlayback && f.ArrivedMs - f.TimeMs > 15 * 60000L)
            { log("Skipped an old fill (from before ZoneKeeper started)."); return closed; }
            OpenTrade trade; open.TryGetValue(f.Key, out trade);
            int signed = f.IsBuy ? f.Qty : -f.Qty;
            if (trade == null)
            {
                trade = OpenTrade.Start(f, f.Qty, f.Commission); open[f.Key] = trade;
                Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed;
            }
            if (Math.Sign(signed) == Math.Sign(trade.Net))
            {
                trade.AddEntry(f, f.Qty, f.Commission); Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed;
            }
            int closeQty = Math.Min(f.Qty, Math.Abs(trade.Net));
            decimal closeCommission = f.Commission * closeQty / f.Qty;
            trade.AddExit(f, closeQty, closeCommission);
            if (trade.Net != 0) { Check(trade, f.PositionAfter); DropBrokenFlat(trade, f); return closed; }
            open.Remove(f.Key);
            int rest = f.Qty - closeQty;
            if (Math.Abs((long)f.PositionAfter) != rest) MarkBroken(trade);
            if (trade.Sendable) closed.Add(trade.ToRecord(f, localZone));
            else NotSent(trade);
            if (rest > 0)
            {
                var next = OpenTrade.Start(f, rest, f.Commission - closeCommission); open[f.Key] = next;
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
            if (Math.Abs((long)trade.Net) != Math.Abs((long)position)) MarkBroken(trade);
        }
        private void DropBrokenFlat(OpenTrade trade, Fill fill)
        {
            if (trade.Broken && fill.PositionAfter == 0) { open.Remove(trade.Key); NotSent(trade); }
        }
        private void NotSent(OpenTrade trade)
        {
            string why = trade.UnknownStart ? "this trade started before ZoneKeeper was running" :
                trade.Broken ? "ZoneKeeper missed a fill of this trade" : "currency " + (trade.Currency ?? "unknown") + " is not supported yet";
            log("Not sent: " + why + " (" + trade.Account + " · " + trade.Instrument + ").");
        }
        public void OnUpdate(Fill f)
        {
            OpenTrade trade; AppliedFill applied;
            if (!open.TryGetValue(f.Key, out trade) || (applied = trade.Fills.FirstOrDefault(x => x.ExecId == f.ExecId)) == null)
            { log("A fill changed after its trade closed; saved trades cannot be changed."); return; }
            if (f.Qty != applied.SourceQty || f.Price != applied.Price || f.IsBuy != applied.IsBuy) MarkBroken(trade);
            else
            {
                // A reversal fill belongs partly to the old trade and partly to the new one.
                trade.Commission += (f.Commission - applied.SourceCommission) * applied.AppliedQty / applied.SourceQty;
                applied.SourceCommission = f.Commission;
            }
            DropBrokenFlat(trade, f);
        }
        public void OnRemove(Fill f)
        {
            OpenTrade trade;
            if (open.TryGetValue(f.Key, out trade) && trade.Fills.Any(x => x.ExecId == f.ExecId))
            { MarkBroken(trade); DropBrokenFlat(trade, f); }
            else log("A fill was removed after its trade closed; saved trades cannot be changed.");
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
            foreach (string id in executionIds) if (!string.IsNullOrEmpty(id)) Remember(account, id);
            var current = positions.Where(x => x.Net != 0).ToDictionary(x => Helpers.PairKey(account, x.Instrument), StringComparer.Ordinal);
            foreach (var t in open.Values.Where(x => x.Account == account).ToList())
            {
                PositionSnapshot p;
                if (current.TryGetValue(t.Key, out p) && p.Net == t.Net)
                    log("Resumed an open trade: " + account + " · " + t.Instrument + " · " + (t.IsLong ? "long " : "short ") + Math.Abs(t.Net) + ".");
                else
                { open.Remove(t.Key); log("Not sent: the position changed while ZoneKeeper did not see it (" + account + " · " + t.Instrument + ")."); }
            }
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
