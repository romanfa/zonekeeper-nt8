// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    [DataContract]
    public sealed class OpenTradesState
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "open_trades")] public List<OpenTrade> OpenTrades { get; set; }
        // A closed trade remains here until its outbox write succeeds, so an IO retry cannot lose the only copy.
        [DataMember(Name = "pending_closed")] public List<TradeRecord> PendingClosed { get; set; }
        public OpenTradesState() { Version = 1; OpenTrades = new List<OpenTrade>(); PendingClosed = new List<TradeRecord>(); }
    }
    public sealed class OpenTradesFile
    {
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly Action<string> log;
        public OpenTradesFile(string filePath, IAtomicFileStore store, Action<string> writeLog)
        { path = filePath; files = store; log = writeLog ?? delegate { }; }
        public OpenTradesState Load()
        {
            try
            {
                if (!files.Exists(path)) return new OpenTradesState();
                OpenTradesState state;
                if (Json.TryRead(files.Read(path), out state) && state.Version == 1 && state.OpenTrades != null && state.PendingClosed != null) return state;
                log("Could not read saved open trades. Positions will be treated as unknown starts."); return new OpenTradesState();
            }
            catch (Exception) { log(UiText.SaveFailed); throw; }
        }
        public bool Save(OpenTradesState state)
        {
            try { files.Write(path, Json.Write(state)); return true; }
            catch (Exception) { log(UiText.SaveFailed); return false; }
        }
    }
}
