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
        // Account + instrument keys with a position open now (known or unknown start). Written by the fill worker after
        // every change, read by the sender (the reset raise rule), so it is a copy behind its own lock.
        private readonly object positionsGate = new object();
        private HashSet<string> openPositions = new HashSet<string>(StringComparer.Ordinal);
        public FillCoordinator(TradeBuilder builder, OpenTradesFile file, OutboxQueue queue)
        {
            Builder = builder; openFile = file; outbox = queue;
            var state = openFile.Load(); pending = state.PendingClosed;
            outbox.ImportInvalidations(state.Invalidated, state.BlockAll);
            DropInvalidated();
        }
        public void Apply(Fill fill)
        {
            string operation = Builder.Operation(fill);
            using (operation == "Update" || operation == "Remove" ? outbox.EnterMutation() : null)
            {
                if (operation == "Update" || operation == "Remove") { outbox.Correct(fill, pending); DropInvalidated(); }
                foreach (var trade in Builder.Process(fill)) if (!pending.Any(x => x.Key == trade.Key)) pending.Add(trade);
                // Before the outbox hand-off: the sender must see the position of a trade it is about to send.
                PublishPositions();
                if (pending.Count > 500) { pending.RemoveRange(0, pending.Count - 500); outbox.LogFull(); }
                dirty = true;
                RetryPersistence();
            }
        }
        private void DropInvalidated()
        { var keys = new HashSet<string>(outbox.Invalidated, StringComparer.Ordinal); pending.RemoveAll(x => outbox.BlockAll || keys.Contains(x.Key)); }
        public bool IsOpen(string positionKey)
        {
            if (positionKey == null) return false;
            lock (positionsGate) return openPositions.Contains(positionKey);
        }
        private void PublishPositions()
        {
            var keys = new HashSet<string>(Builder.Open.Where(x => x.Net != 0).Select(x => x.Key), StringComparer.Ordinal);
            lock (positionsGate) openPositions = keys;
        }
        public void Reset(string account) { Builder.OnReset(account); PublishPositions(); dirty = true; RetryPersistence(); }
        public void Disable(string account) { Builder.DropAccount(account); PublishPositions(); dirty = true; RetryPersistence(); }
        public void PruneDisabledAccounts(Func<string, bool> isEnabled)
        {
            foreach (string account in Builder.Open.Select(x => x.Account).Distinct().Where(x => !isEnabled(x)).ToList())
                Disable(account);
        }
        public void Resync(string account, IEnumerable<PositionSnapshot> positions, IEnumerable<string> executionIds)
        { Resync(account, new AccountSnapshot(positions, executionIds.Select(id => new ExecutionEvidence { ExecId = id }))); }
        public void Resync(string account, AccountSnapshot snapshot)
        {
            using (outbox.EnterMutation())
            {
                Builder.OnResync(account, snapshot); PublishPositions();
                var rejected = pending.Where(x => x.RequiresValidation && (x.Provenance == null || !x.Provenance.Complete || x.Provenance.Account == account) &&
                    (x.Provenance == null || !x.Provenance.VerifiedBy(account, snapshot))).Select(x => x.Key).ToList();
                if (rejected.Count != 0) outbox.InvalidateRecords(rejected);
                foreach (var trade in pending.Where(x => x.RequiresValidation && x.Provenance != null && x.Provenance.VerifiedBy(account, snapshot))) trade.RequiresValidation = false;
                outbox.ValidateRestored(account, snapshot); DropInvalidated(); dirty = true; RetryPersistence();
            }
        }
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
        { return new OpenTradesState { OpenTrades = new List<OpenTrade>(), PendingClosed = pending.ToList(),
            PendingProvenance = pending.Select(x => new PendingProvenance { Key = x.Key, Provenance = x.Provenance }).ToList(),
            Invalidated = outbox.Invalidated, BlockAll = outbox.BlockAll }; }
    }
}
