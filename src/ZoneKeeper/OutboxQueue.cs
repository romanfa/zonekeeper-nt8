// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    [DataContract]
    public sealed class OutboxLine
    {
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "queued_at")] public long QueuedAt { get; set; }
        [DataMember(Name = "trade")] public TradeRecord Trade { get; set; }
        public string Identity { get { return Trade.Key; } }
    }
    public sealed class OutboxQueue
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly Action<string> log;
        private readonly List<OutboxLine> rows = new List<OutboxLine>();
        private readonly HashSet<string> newlyAdded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<TradeRecord> notifications = new Queue<TradeRecord>();
        private bool dirty;
        public event Action<TradeRecord> DurableAdded;
        public OutboxQueue(string filePath, IAtomicFileStore store, Action<string> writeLog)
        {
            path = filePath; files = store; log = writeLog ?? delegate { };
            Load();
        }
        public int Count { get { lock (gate) return rows.Count; } }
        public bool IsDirty { get { lock (gate) return dirty; } }
        private void Load()
        {
            try
            {
                if (!files.Exists(path)) return;
                int malformed = 0;
                foreach (string text in files.Read(path).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    OutboxLine row;
                    if (!Json.TryRead(text, out row) || row.Trade == null || string.IsNullOrEmpty(row.Trade.Account) || string.IsNullOrEmpty(row.Trade.ExitExecId))
                    { malformed++; continue; }
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
                if (rows.Any(x => x.Identity == trade.Key)) result = !dirty || FlushLocked();
                else
                {
                    rows.Add(new OutboxLine { Key = trade.Account + "|" + trade.ExitExecId, QueuedAt = Helpers.NowMs(), Trade = trade });
                    newlyAdded.Add(trade.Key); dirty = true;
                    if (rows.Count > 500)
                    { newlyAdded.Remove(rows[0].Identity); rows.RemoveAt(0); log(UiText.QueueFull); }
                    result = FlushLocked();
                }
            }
            PublishAdded(); return result;
        }
        public bool TryFlush()
        { bool result; lock (gate) result = !dirty || FlushLocked(); PublishAdded(); return result; }
        private void SaveRows(List<OutboxLine> next)
        {
            files.Write(path, string.Join("\n", next.Select(x => Json.Write(x))) + (next.Count == 0 ? "" : "\n"));
        }
        private bool FlushLocked()
        {
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
            lock (gate) return dirty ? new List<OutboxLine>() : rows.Take(count).ToList();
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
