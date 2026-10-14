// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // The fill worker is the sole caller. This checkpoint also covers the handoff from open state to outbox.
    public sealed class FillCoordinator
    {
        public TradeBuilder Builder { get; private set; }
        private readonly OpenTradesFile openFile;
        private readonly OutboxQueue outbox;
        private readonly List<TradeRecord> pending;
        private bool dirty = true;
        public FillCoordinator(TradeBuilder builder, OpenTradesFile file, OutboxQueue queue)
        {
            Builder = builder; openFile = file; outbox = queue;
            var state = openFile.Load(); Builder.Restore(state.OpenTrades); pending = state.PendingClosed;
        }
        public void Apply(Fill fill)
        {
            foreach (var trade in Builder.Process(fill)) if (!pending.Any(x => x.Key == trade.Key)) pending.Add(trade);
            dirty = true;
            RetryPersistence();
        }
        public void Reset(string account) { Builder.OnReset(account); dirty = true; RetryPersistence(); }
        public void Disable(string account) { Builder.DropAccount(account); dirty = true; RetryPersistence(); }
        public void PruneDisabledAccounts(Func<string, bool> isEnabled)
        {
            foreach (string account in Builder.Open.Select(x => x.Account).Distinct().Where(x => !isEnabled(x)).ToList())
                Disable(account);
        }
        public void Resync(string account, IEnumerable<PositionSnapshot> positions, IEnumerable<string> executionIds)
        { Builder.OnResync(account, positions, executionIds); dirty = true; RetryPersistence(); }
        public bool RetryPersistence()
        {
            if (!dirty && !outbox.IsDirty) return true;
            // Save the handoff checkpoint first. If only the outbox write fails, restart can recover this record.
            bool saved = openFile.Save(Snapshot());
            foreach (var trade in pending.ToList()) if (outbox.Add(trade)) pending.Remove(trade);
            if (!outbox.TryFlush()) return false;
            // Add may have succeeded for records retained by a previous failed attempt.
            foreach (var trade in pending.ToList()) if (outbox.Add(trade)) pending.Remove(trade);
            bool complete = openFile.Save(Snapshot()) && saved;
            dirty = !complete; return complete;
        }
        public OpenTradesState Snapshot()
        { return new OpenTradesState { OpenTrades = Builder.Open, PendingClosed = pending.ToList() }; }
    }
}
