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
        public int Catalogs;
        public int Todays;
        public int Resets;
        public int Audios;
        public readonly List<string> CatalogETags = new List<string>();
        public readonly List<string> TodayDates = new List<string>();
        public readonly List<ResetBody> ResetBodies = new List<ResetBody>();
        public readonly List<string> AudioIds = new List<string>();
        public readonly Queue<ApiResult> CatalogAnswers = new Queue<ApiResult>();
        public readonly Queue<ApiResult> TodayAnswers = new Queue<ApiResult>();
        public readonly Queue<ApiResult> ResetAnswers = new Queue<ApiResult>();
        public readonly Queue<ApiResult> AudioAnswers = new Queue<ApiResult>();
        // Bytes the fake audio call writes into the target when the answer is Ok (default 3 MB).
        public int AudioBytes = 3 * 1024 * 1024;
        public readonly List<int> BatchSizes = new List<int>();
        // The calls in order: "ping", "trades", "reset", "today", "catalog", "audio", "claim", "disconnect".
        public readonly List<string> Order = new List<string>();
        public readonly List<string> Tokens = new List<string>();
        public readonly Queue<ApiResult> SendAnswers = new Queue<ApiResult>();
        public readonly Queue<ApiResult> PingAnswers = new Queue<ApiResult>();
        public ApiResult ClaimResult = Fixtures.Claim();
        public ApiResult DisconnectResult = new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new DisconnectAnswer { Ok = true } };
        public Func<CancellationToken, Task<ApiResult>> SendOverride;
        public Task<ApiResult> ClaimAsync(string code, string name, CancellationToken cancellation)
        { Claims++; Order.Add("claim"); return Task.FromResult(ClaimResult); }
        public Task<ApiResult> PingAsync(string token, CancellationToken cancellation)
        { Pings++; Order.Add("ping"); Tokens.Add(token); return Task.FromResult(PingAnswers.Count == 0 ? Fixtures.Ping() : PingAnswers.Dequeue()); }
        public Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation)
        {
            Sends++; Order.Add("trades"); BatchSizes.Add(trades.Count); Tokens.Add(token);
            if (SendOverride != null) return SendOverride(cancellation);
            return Task.FromResult(SendAnswers.Count == 0 ? Fixtures.Sent(trades.Count) : SendAnswers.Dequeue());
        }
        public Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation)
        { Disconnects++; Order.Add("disconnect"); Tokens.Add(token); return Task.FromResult(DisconnectResult); }
        public Task<ApiResult> CatalogAsync(string token, string etag, CancellationToken cancellation)
        { Catalogs++; Order.Add("catalog"); Tokens.Add(token); CatalogETags.Add(etag); return Task.FromResult(CatalogAnswers.Count == 0 ? Fixtures.CatalogOk() : CatalogAnswers.Dequeue()); }
        public Task<ApiResult> TodayAsync(string token, string date, CancellationToken cancellation)
        { Todays++; Order.Add("today"); Tokens.Add(token); TodayDates.Add(date); return Task.FromResult(TodayAnswers.Count == 0 ? Fixtures.TodayOk() : TodayAnswers.Dequeue()); }
        public Task<ApiResult> SaveResetAsync(string token, ResetBody body, CancellationToken cancellation)
        { Resets++; Order.Add("reset"); Tokens.Add(token); ResetBodies.Add(body); return Task.FromResult(ResetAnswers.Count == 0 ? Fixtures.ResetOk(body.TradeId) : ResetAnswers.Dequeue()); }
        public Task<ApiResult> DownloadAudioAsync(string token, string id, Stream target, IProgress<int> percent, CancellationToken cancellation)
        {
            Audios++; Order.Add("audio"); Tokens.Add(token); AudioIds.Add(id);
            var answer = AudioAnswers.Count == 0 ? new ApiResult { Kind = ApiKind.Ok, Status = 200, ContentLength = AudioBytes } : AudioAnswers.Dequeue();
            if (answer.Kind == ApiKind.Ok) { target.Write(new byte[AudioBytes], 0, AudioBytes); if (percent != null) percent.Report(100); }
            return Task.FromResult(answer);
        }
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
        public static ApiResult CatalogOk(string etag = "\"0123456789abcdef\"")
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, ETag = etag, Body = Json.Read<Catalog>(CatalogJson) }; }
        public static ApiResult TodayOk(params TodayTrade[] trades)
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TodayAnswer { Date = "2026-10-19", ServerTime = Time, Trades = trades.ToList() } }; }
        public static ApiResult ResetOk(long tradeId, long entryId = 640)
        { return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new ResetAnswer { Entry = new ResetEntry { Id = entryId, TradeId = tradeId } } }; }
        public static TodayTrade TodayTrade(long id, long pnlCents = 21250, long exitMs = Time, long? entryId = null, string currency = "USD")
        { return new TodayTrade { Id = id, Account = "Sim101", Instrument = "ES 12-26", Side = "short", Qty = 2, ExitTime = exitMs, PnlCents = pnlCents, Currency = currency, EntryId = entryId }; }
        public static Catalog Catalog() { return Json.Read<Catalog>(CatalogJson); }
        public static string Marks(double end = 153.09) { return "{\"seconds\":153,\"marks\":{\"steps\":[0,9.97,17.04,57.2,76.01,114.94,123.88],\"breaths\":[2.65,15.0,27.28],\"end\":" + end.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"; }
        private static string Thought(string key, string text, string a, string b, string c)
        {
            return "{\"key\":\"" + key + "\",\"text\":\"" + text + "\",\"lines\":[" +
                "{\"id\":\"" + key + ".1\",\"text\":\"Line one of " + key + ".\",\"audio\":\"" + a + "\"}," +
                "{\"id\":\"" + key + ".2\",\"text\":\"Line two of " + key + ".\",\"audio\":\"" + b + "\"}," +
                "{\"id\":\"" + key + ".3\",\"text\":\"Line three of " + key + ".\",\"audio\":\"" + c + "\"}]}";
        }
        // The shape of GET /api/nt/catalog (plan 31 section 2.2) with the 3 trade triggers, one thought each + Something else.
        public static readonly string CatalogJson =
            "{\"version\":\"0123456789abcdef\",\"rules\":{\"trade_max\":3,\"red_line\":7}," +
            "\"tilts\":[" + string.Join(",", new[] { "Calm", "Still thinking", "A small pull", "Tightening up", "Pushing for a trade", "Hard to think", "A strong urge", "Unplanned trades", "Anger or fear", "Overwhelmed or numb" }
                .Select((name, i) => "{\"n\":" + (i + 1) + ",\"name\":\"" + name + "\",\"feel\":\"Feel " + (i + 1) + ".\",\"action\":\"Act " + (i + 1) + ".\",\"band\":\"" + (i < 3 ? "g" : i < 6 ? "o" : "r") + "\"}")) + "]," +
            "\"triggers\":[" +
            "{\"key\":\"stop_loss\",\"label\":\"Stop loss\",\"question\":\"What is your head saying?\",\"thoughts\":[" + Thought("make_it_back", "I have to make it back now", "b1", "b21", "b22") + "],\"other\":" + Thought("other", "Something else", "b11", "b29", "b30") + "}," +
            "{\"key\":\"winning_trade\",\"label\":\"Winning trade\",\"question\":\"What is your head saying?\",\"thoughts\":[" + Thought("on_fire", "I'm on fire. Size up", "b7", "b39", "b40") + "],\"other\":" + Thought("other", "Something else", "b13", "b45", "b46") + "}," +
            "{\"key\":\"break_even\",\"label\":\"Break-even trade\",\"question\":\"What happened after your break-even exit?\",\"thoughts\":[" + Thought("be_target", "It hit break-even, then reached my target", "b14", "b47", "b48") + "],\"other\":" + Thought("other", "Something else", "b16", "b51", "b52") + "}]," +
            "\"parts\":[{\"name\":\"STOP\",\"sec\":5,\"todo\":\"Hands off the mouse. Mouse hand on the chest.\",\"say\":null,\"kind\":null}," +
            "{\"name\":\"RATE\",\"sec\":10,\"todo\":\"Match your thoughts, body, and urge to a number from 1 to 10.\",\"say\":null,\"kind\":\"rate\"}," +
            "{\"name\":\"BREATHE\",\"sec\":40,\"todo\":\"3 rounds: in 4 through the nose, out 8.\",\"say\":null,\"kind\":\"breathe\"}," +
            "{\"name\":\"LET GO\",\"sec\":25,\"todo\":\"Eyes off the chart.\",\"say\":\"Win or loss, it is the same now.\",\"kind\":null}," +
            "{\"name\":\"REPROGRAM LOGIC\",\"sec\":15,\"todo\":\"Read your Reprogram Logic slowly.\",\"say\":null,\"kind\":\"line\"}," +
            "{\"name\":\"ANCHOR\",\"sec\":8,\"todo\":\"Mouse hand on the chest, a long breath out.\",\"say\":null,\"kind\":null}," +
            "{\"name\":\"BACK\",\"sec\":18,\"todo\":\"Your tilt again comes next: step 5.\",\"say\":\"This next trade is my first trade.\",\"kind\":null}]," +
            "\"after\":{\"green\":\"Check your setup checklist and wait for the next A+.\",\"orange\":\"Reset again, or stand up 2–5 min.\"," +
            "\"orange_twice\":\"Second reset and still 4 or higher: today is done. Stop for today.\",\"red\":\"Red line: tilt 7 or higher. Stop for today.\"}," +
            "\"audios\":{" + string.Join(",", new[] { "b1", "b21", "b22", "b11", "b29", "b30", "b7", "b39", "b40", "b13", "b45", "b46", "b14", "b47", "b48", "b16", "b51", "b52" }.Select(id => "\"" + id + "\":" + Marks())) + "}}";
        public static Settings Settings(MemoryFiles files, bool connected = false)
        {
            var settings = new Settings("settings", files, new FakeProtector(), delegate { });
            if (connected) settings.SetToken((ClaimAnswer)Claim().Body);
            return settings;
        }
    }
}
