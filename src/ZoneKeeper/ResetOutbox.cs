// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    [DataContract]
    public sealed class ResetLine
    {
        [DataMember(Name = "request_id")] public string RequestId { get; set; }
        [DataMember(Name = "trade_id")] public long TradeId { get; set; }
        // When the trade closed (for the log line only; not sent).
        [DataMember(Name = "exit_time")] public long ExitTime { get; set; }
        [DataMember(Name = "queued_at")] public long QueuedAt { get; set; }
        [DataMember(Name = "body")] public ResetBody Body { get; set; }
    }

    // Finished resets waiting to be sent (resets.jsonl). Written and flushed before the panel closes.
    public sealed class ResetOutbox
    {
        public const int Limit = 100;
        private readonly object gate = new object();
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly Action<string> log;
        private readonly List<ResetLine> rows = new List<ResetLine>();
        public ResetOutbox(string filePath, IAtomicFileStore store, Action<string> writeLog)
        {
            path = filePath; files = store; log = writeLog ?? delegate { };
            Load();
        }
        private void Load()
        {
            try
            {
                if (!files.Exists(path)) return;
                int damaged = 0;
                foreach (string text in files.Read(path).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    ResetLine row;
                    if (!Json.TryRead(text, out row) || row.Body == null || string.IsNullOrEmpty(row.RequestId) || row.TradeId <= 0) { damaged++; continue; }
                    if (!rows.Any(x => x.RequestId == row.RequestId)) rows.Add(row);
                }
                if (damaged != 0) log("Skipped " + damaged + " damaged reset lines.");
                if (rows.Count > Limit) { rows.RemoveRange(0, rows.Count - Limit); log(UiText.ResetQueueFull); Write(); }
            }
            catch (Exception) { log(UiText.SaveFailed); }
        }
        public int Count { get { lock (gate) return rows.Count; } }
        public List<long> PendingTradeIds { get { lock (gate) return rows.Select(x => x.TradeId).Distinct().ToList(); } }
        // True only when the line is on disk.
        public bool Add(ResetBody body, long exitMs, long nowMs)
        {
            if (body == null || string.IsNullOrEmpty(body.RequestId) || body.TradeId <= 0) return false;
            lock (gate)
            {
                if (rows.Any(x => x.RequestId == body.RequestId)) return true;
                rows.Add(new ResetLine { RequestId = body.RequestId, TradeId = body.TradeId, ExitTime = exitMs, QueuedAt = nowMs, Body = body });
                if (rows.Count > Limit) { rows.RemoveAt(0); log(UiText.ResetQueueFull); }
                if (Write()) return true;
                rows.RemoveAll(x => x.RequestId == body.RequestId); return false;
            }
        }
        public ResetLine Peek() { lock (gate) return rows.Count == 0 ? null : rows[0]; }
        // The line leaves memory only when the file was written; a failed write means "try again later".
        public bool Remove(string requestId)
        {
            lock (gate)
            {
                var next = rows.Where(x => x.RequestId != requestId).ToList();
                if (next.Count == rows.Count) return true;
                if (!Write(next)) return false;
                rows.Clear(); rows.AddRange(next); return true;
            }
        }
        private bool Write() { return Write(rows); }
        private bool Write(List<ResetLine> lines)
        {
            try { files.Write(path, string.Join("\n", lines.Select(x => Json.Write(x))) + (lines.Count == 0 ? "" : "\n")); return true; }
            catch (Exception) { log(UiText.SaveFailed); return false; }
        }
    }
}
