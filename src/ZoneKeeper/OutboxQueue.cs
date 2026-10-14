// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    [DataContract]
    public sealed class OutboxLine
    {
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "queued_at")] public long QueuedAt { get; set; }
        [DataMember(Name = "trade")] public TradeRecord Trade { get; set; }
        [DataMember(Name = "provenance")] public TradeProvenance Provenance { get; set; }
        public string Identity { get { return Trade.Key; } }
    }
    [DataContract]
    public sealed class InvalidationState
    {
        [DataMember] public List<string> Keys { get; set; }
        [DataMember] public bool BlockAll { get; set; }
    }
    public sealed class OutboxQueue
    {
        private readonly object gate = new object();
        // Shared by the background fill worker and sender. Never held by UI snapshots.
        private readonly SemaphoreSlim dispatch = new SemaphoreSlim(1, 1);
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly Action<string> log;
        private readonly List<OutboxLine> rows = new List<OutboxLine>();
        private readonly HashSet<string> newlyAdded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<TradeRecord> notifications = new Queue<TradeRecord>();
        private bool dirty;
        private bool journalDirty;
        private bool blockAll;
        private readonly HashSet<string> invalidated = new HashSet<string>(StringComparer.Ordinal);
        public List<string> Invalidated { get { lock (gate) return invalidated.ToList(); } }
        public bool BlockAll { get { lock (gate) return blockAll; } }
        public void LogFull() { log(UiText.QueueFull); }
        private sealed class Release : IDisposable
        {
            private SemaphoreSlim semaphore;
            public Release(SemaphoreSlim value) { semaphore = value; }
            public void Dispose() { var value = Interlocked.Exchange(ref semaphore, null); if (value != null) value.Release(); }
        }
        public IDisposable EnterMutation() { dispatch.Wait(); return new Release(dispatch); }
        public async Task<IDisposable> EnterSendAsync(CancellationToken cancellation)
        { await dispatch.WaitAsync(cancellation).ConfigureAwait(false); return new Release(dispatch); }
        public event Action<TradeRecord> DurableAdded;
        public event Action ReadyToSend;
        public OutboxQueue(string filePath, IAtomicFileStore store, Action<string> writeLog)
        {
            path = filePath; files = store; log = writeLog ?? delegate { };
            Load();
        }
        public int Count { get { lock (gate) return rows.Count; } }
        public bool IsDirty { get { lock (gate) return dirty || journalDirty; } }
        private void Load()
        {
            try
            {
                string journalPath = path + ".invalidations";
                if (files.Exists(journalPath))
                {
                    InvalidationState state;
                    if (!Json.TryRead(files.Read(journalPath), out state) || state.Keys == null)
                    { blockAll = true; journalDirty = true; log("Could not verify waiting-trade invalidations. Waiting trades will not be sent."); }
                    else { blockAll = state.BlockAll; foreach (var key in state.Keys) invalidated.Add(key); }
                }
                if (!files.Exists(path)) return;
                int malformed = 0;
                foreach (string text in files.Read(path).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    OutboxLine row;
                    if (!Json.TryRead(text, out row) || row.Trade == null || string.IsNullOrEmpty(row.Trade.Account) || string.IsNullOrEmpty(row.Trade.ExitExecId))
                    { malformed++; continue; }
                    row.Trade.Provenance = row.Provenance; row.Trade.RequiresValidation = true;
                    if (blockAll || invalidated.Contains(row.Identity)) { dirty = true; continue; }
                    if (!rows.Any(x => x.Identity == row.Identity)) rows.Add(row);
                }
                if (malformed != 0) log("Skipped " + malformed + " damaged waiting-list lines.");
                if (rows.Count > 500) { rows.RemoveRange(0, rows.Count - 500); dirty = true; log(UiText.QueueFull); }
            }
            catch (Exception) { log(UiText.SaveFailed); throw; } // do not overwrite an unreadable existing queue
        }
        public bool Add(TradeRecord trade)
        {
            bool result;
            lock (gate)
            {
                if (blockAll || invalidated.Contains(trade.Key)) result = true;
                else if (rows.Any(x => x.Identity == trade.Key)) result = (!dirty && !journalDirty) || FlushLocked();
                else
                {
                    rows.Add(new OutboxLine { Key = trade.Account + "|" + trade.ExitExecId, QueuedAt = Helpers.NowMs(), Trade = trade, Provenance = trade.Provenance });
                    newlyAdded.Add(trade.Key); dirty = true;
                    if (rows.Count > 500)
                    { newlyAdded.Remove(rows[0].Identity); rows.RemoveAt(0); log(UiText.QueueFull); }
                    result = FlushLocked();
                }
            }
            PublishAdded(); return result;
        }
        public bool TryFlush()
        { bool result; lock (gate) result = (!dirty && !journalDirty) || FlushLocked(); PublishAdded(); return result; }
        private void SaveRows(List<OutboxLine> next)
        {
            files.Write(path, string.Join("\n", next.Select(x => Json.Write(x))) + (next.Count == 0 ? "" : "\n"));
        }
        private bool FlushLocked()
        {
            if (!SaveJournalLocked()) return false;
            try { SaveRows(rows); }
            catch (Exception) { log(UiText.SaveFailed); return false; }
            dirty = false;
            var added = rows.Where(x => newlyAdded.Contains(x.Identity)).Select(x => x.Trade).ToList();
            newlyAdded.Clear();
            foreach (var trade in added) notifications.Enqueue(trade);
            return true;
        }
        private void PublishAdded()
        {
            List<TradeRecord> added;
            lock (gate) { added = notifications.ToList(); notifications.Clear(); }
            foreach (var trade in added)
            {
                var handler = DurableAdded;
                if (handler != null) foreach (Action<TradeRecord> subscriber in handler.GetInvocationList())
                    try { subscriber(trade); } catch (Exception) { }
            }
        }
        public List<OutboxLine> Peek(int count)
        {
            lock (gate) return dirty || journalDirty || blockAll ? new List<OutboxLine>() : rows.Where(x => !x.Trade.RequiresValidation).Take(count).ToList();
        }
        private bool SaveJournalLocked()
        {
            if (!journalDirty) return true;
            try { files.Write(path + ".invalidations", Json.Write(new InvalidationState { Keys = invalidated.ToList(), BlockAll = blockAll })); }
            catch (Exception) { log(UiText.SaveFailed); return false; }
            journalDirty = false; return true;
        }
        public void ImportInvalidations(IEnumerable<string> identities, bool all)
        {
            lock (gate)
            {
                foreach (string key in identities ?? new string[0]) if (invalidated.Add(key)) journalDirty = true;
                if (all && !blockAll) { blockAll = true; journalDirty = true; }
                DropInvalidatedLocked();
            }
        }
        private void DropInvalidatedLocked()
        {
            foreach (var row in rows.Where(x => blockAll || invalidated.Contains(x.Identity)).ToList())
            { rows.Remove(row); newlyAdded.Remove(row.Identity); dirty = true; }
            if (invalidated.Count > ExecutionEvidence.MaximumCount)
            { invalidated.Clear(); blockAll = true; journalDirty = true; rows.Clear(); newlyAdded.Clear(); dirty = true; log("Waiting-trade safety history is full. Trades will not be sent."); }
        }
        public void InvalidateRecords(IEnumerable<string> identities)
        {
            lock (gate)
            {
                foreach (var key in identities) if (invalidated.Add(key)) journalDirty = true;
                // Persist the tombstone before rewriting the queue. A failed rewrite cannot revive it.
                SaveJournalLocked(); DropInvalidatedLocked();
                if (dirty) FlushLocked();
            }
        }
        public void Correct(Fill fill, IEnumerable<TradeRecord> pending)
        {
            List<string> affected;
            lock (gate) affected = rows.Where(x => x.Provenance == null || x.Provenance.AffectedBy(fill)).Select(x => x.Identity)
                .Concat(pending.Where(x => x.Provenance == null || x.Provenance.AffectedBy(fill)).Select(x => x.Key)).Distinct(StringComparer.Ordinal).ToList();
            if (affected.Count == 0) { log("Execution correction: no affected waiting trade. Already dispatched trades cannot be changed."); return; }
            log("Not sent: execution " + fill.Op + " invalidated " + affected.Count + " waiting trade(s).");
            InvalidateRecords(affected);
        }
        public void ValidateRestored(string account, AccountSnapshot snapshot)
        {
            List<string> rejected = new List<string>();
            bool ready = false;
            lock (gate)
            {
                foreach (var row in rows.Where(x => x.Trade.RequiresValidation && (x.Provenance == null || !x.Provenance.Complete || x.Provenance.Account == account)))
                {
                    if (row.Provenance != null && row.Provenance.VerifiedBy(account, snapshot)) { row.Trade.RequiresValidation = false; ready = true; }
                    else rejected.Add(row.Identity);
                }
            }
            if (rejected.Count != 0)
            { log("Not sent: restored waiting trades lack unchanged execution history."); InvalidateRecords(rejected); }
            var handler = ReadyToSend; if (ready && handler != null) handler();
        }
        public bool Remove(IEnumerable<string> identities)
        {
            lock (gate)
            {
                if (dirty && !FlushLocked()) return false;
                var keys = new HashSet<string>(identities, StringComparer.Ordinal);
                var next = rows.Where(x => !keys.Contains(x.Identity)).ToList();
                try { SaveRows(next); }
                catch (Exception) { log(UiText.SaveFailed); return false; }
                rows.Clear(); rows.AddRange(next); return true;
            }
        }
    }
}
