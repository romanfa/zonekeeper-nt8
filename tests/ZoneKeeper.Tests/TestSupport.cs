using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;

namespace ZoneKeeper.Tests
{
    public sealed class MemoryFiles : IAtomicFileStore
    {
        private readonly object gate = new object();
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        public Func<string, bool> FailWrites;
        public Action<string> BeforeWrite;
        public int Writes;
        public bool Exists(string path) { lock (gate) return Data.ContainsKey(path); }
        public string Read(string path) { lock (gate) return Data[path]; }
        public void Write(string path, string text)
        {
            if (BeforeWrite != null) BeforeWrite(path);
            if (FailWrites != null && FailWrites(path)) throw new IOException("Disk unavailable");
            lock (gate) { Writes++; Data[path] = text; }
        }
    }
    public sealed class FakeProtector : ITokenProtector
    {
        public bool Fail;
        public byte[] Protect(byte[] plaintext)
        {
            if (Fail) throw new InvalidOperationException();
            byte[] result = new byte[plaintext.Length];
            for (int i = 0; i < result.Length; i++) result[i] = (byte)(plaintext[i] ^ 0x55);
            return result;
        }
        public byte[] Unprotect(byte[] encrypted) { return Protect(encrypted); }
    }
    public sealed class TestFolder : IDisposable
    {
        public string Path { get; private set; }
        public TestFolder() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zonekeeper-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
    public sealed class FakeApi : IAddonApi
    {
        public int Claims;
        public int Pings;
        public int Sends;
        public int Disconnects;
        public readonly List<int> BatchSizes = new List<int>();
        public readonly List<string> Tokens = new List<string>();
        public readonly Queue<ApiResult> SendAnswers = new Queue<ApiResult>();
        public readonly Queue<ApiResult> PingAnswers = new Queue<ApiResult>();
        public ApiResult ClaimResult = Fixtures.Claim();
        public ApiResult DisconnectResult = new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new DisconnectAnswer { Ok = true } };
        public Func<CancellationToken, Task<ApiResult>> SendOverride;
        public Task<ApiResult> ClaimAsync(string code, string name, CancellationToken cancellation)
        { Claims++; return Task.FromResult(ClaimResult); }
        public Task<ApiResult> PingAsync(string token, CancellationToken cancellation)
        { Pings++; Tokens.Add(token); return Task.FromResult(PingAnswers.Count == 0 ? Fixtures.Ping() : PingAnswers.Dequeue()); }
        public Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation)
        {
            Sends++; BatchSizes.Add(trades.Count); Tokens.Add(token);
            if (SendOverride != null) return SendOverride(cancellation);
            return Task.FromResult(SendAnswers.Count == 0 ? Fixtures.Sent(trades.Count) : SendAnswers.Dequeue());
        }
        public Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation)
        { Disconnects++; Tokens.Add(token); return Task.FromResult(DisconnectResult); }
        public void Dispose() { }
    }
    public sealed class FakeHttp : HttpMessageHandler
    {
        public new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return Send(request, cancellation); }
    }
    public static class Fixtures
    {
        public const long Time = 1791902100000L;
        public static string Token { get { return "zkd_" + new string('A', 43); } }
        public static Fill Fill(string id, bool buy, int qty, decimal price, int position, decimal commission = 0, string account = "Sim101", string instrument = "MNQ 12-26")
        {
            return new Fill { Op = "Add", Account = account, Instrument = instrument, ExecId = id, IsBuy = buy, Qty = qty, Price = price,
                PositionAfter = position, Commission = commission, TimeMs = Time, ArrivedMs = Time + 1000, PointValue = 2, Currency = "USD", Name = buy ? "Entry" : "Target1" };
        }
        public static TradeRecord Trade(string id = "exit", string account = "Sim101")
        {
            int sequence; if (!id.StartsWith("exit", StringComparison.Ordinal) || !int.TryParse(id.Substring(4), out sequence)) sequence = 0;
            var entry = Fill(id == "exit" ? "entry" : "entry:" + id, true, 2, 21034.25m, 2, 1.30m, account); entry.TimeMs += sequence * 600000L;
            var exit = Fill(id, false, 2, 21045.75m, 0, 1.30m, account); exit.TimeMs = entry.TimeMs + 420000;
            return new TradeRecord { Account = account, Instrument = "MNQ 12-26", Side = "long", Qty = 2, EntryTime = entry.TimeMs, ExitTime = exit.TimeMs,
                EntryPrice = 21034.25, ExitPrice = 21045.75, PnlCents = 4340, CommissionCents = 260, Currency = "USD", PointValue = 2,
                ExitExecId = id, EntryExecIds = new List<string> { entry.ExecId }, LocalDate = "2026-10-13", ExitName = "Target1",
                Provenance = new TradeProvenance { Account = account, Instrument = entry.Instrument,
                    Executions = new List<ExecutionEvidence> { ExecutionEvidence.From(entry), ExecutionEvidence.From(exit) } } };
        }
        public static AccountSnapshot Snapshot(IEnumerable<Fill> fills, params PositionSnapshot[] positions)
        { return new AccountSnapshot(positions, fills.Select(ExecutionEvidence.From)); }
        public static PositionSnapshot Position(int net, decimal price = 100)
        { return new PositionSnapshot { Instrument = "MNQ 12-26", Net = net, AveragePrice = price, TickSize = .25m, PointValue = 2, Currency = "USD" }; }
        public static void VerifyPersisted(OutboxQueue queue, MemoryFiles files, string path = "queue")
        {
            foreach (var account in files.Read(path).Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Select(Json.Read<OutboxLine>)
                .Where(x => x.Provenance != null).GroupBy(x => x.Provenance.Account))
                queue.ValidateRestored(account.Key, new AccountSnapshot(new PositionSnapshot[0], account.SelectMany(x => x.Provenance.Executions).OrderBy(x => x.TimeMs)));
        }
        public static ApiResult Claim()
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new ClaimAnswer { Token = Token, DeviceId = 7, EmailMasked = "r***@gmail.com" } }; }
        public static ApiResult Ping(long time = Time)
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new PingAnswer { Ok = true, DeviceId = 7, PlanStatus = "active", ServerTime = time } }; }
        public static ApiResult Sent(int count)
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TradesAnswer { Inserted = count, Duplicates = 0, Rejected = new List<RejectedTrade>() } }; }
        public static Settings Settings(MemoryFiles files, bool connected = false)
        {
            var settings = new Settings("settings", files, new FakeProtector(), delegate { });
            if (connected) settings.SetToken((ClaimAnswer)Claim().Body);
            return settings;
        }
    }
}
