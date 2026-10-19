using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // Add-on 0.2.0: the reset window opens by itself after a closed trade (plan note 29).
    public sealed class ResetTests
    {
        private const string Base = Helpers.LiveBaseUrl;
        private const string NewBody = "{\"inserted\":1,\"duplicates\":1,\"rejected\":[{\"index\":2,\"error\":\"bad\"}],\"ids\":[{\"index\":0,\"id\":812},{\"index\":1,\"id\":640}]}";

        // The 0.1.x answer shape, to prove that old add-ons ignore the new "ids" field.
        [DataContract] private sealed class OldTradesAnswer
        {
            [DataMember(Name = "inserted")] public int Inserted { get; set; }
            [DataMember(Name = "duplicates")] public int Duplicates { get; set; }
            [DataMember(Name = "rejected")] public List<RejectedTrade> Rejected { get; set; }
        }

        private sealed class Scope : IDisposable
        {
            public readonly TestFolder Folder = new TestFolder();
            public readonly MemoryFiles Files = new MemoryFiles();
            public readonly FakeApi Api = new FakeApi();
            public readonly OutboxQueue Queue;
            public readonly Settings Settings;
            public readonly SessionCoordinator Session;
            public readonly EventLog Log;
            public readonly List<long> Opened = new List<long>();
            public int KeyRemoved;
            public Scope(bool connected = true, long runStartedMs = 0)
            {
                Settings = Fixtures.Settings(Files, connected); Queue = new OutboxQueue("queue", Files, delegate { }); Log = new EventLog(Folder.Path);
                Session = new SessionCoordinator(Settings, Queue, Api, Log) { RunStartedMs = runStartedMs };
                Queue.DurableAdded += delegate { Session.NotifyQueued(); };
                Session.ResetWanted += id => Opened.Add(id);
                Session.KeyRemoved += () => KeyRemoved++;
            }
            public Task Step(long now) { return Session.ProcessAsync(now, CancellationToken.None); }
            // Ping once, then send the given trades in one batch at "now" with the given answer.
            public async Task Send(long now, ApiResult answer, params TradeRecord[] trades)
            {
                await Step(now);
                if (answer != null) Api.SendAnswers.Enqueue(answer);
                foreach (var trade in trades) Queue.Add(trade);
                await Step(now);
            }
            public void Dispose() { Folder.Dispose(); }
        }

        private static ApiResult Saved(params long[] ids)
        {
            return new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TradesAnswer { Inserted = ids.Length, Duplicates = 0, Rejected = new List<RejectedTrade>(),
                Ids = ids.Select((id, index) => new SavedTrade { Index = index, Id = id }).ToList() } };
        }
        private static TradeRecord TradeAt(string id, long exitMs) { var trade = Fixtures.Trade(id); trade.ExitTime = exitMs; trade.EntryTime = exitMs - 60000; return trade; }
        private static List<TradeRecord> Batch(params long[] exits) { return exits.Select((x, i) => TradeAt("exit" + i, x)).ToList(); }
        private static TradesAnswer Ids(params long[] ids)
        { return new TradesAnswer { Rejected = new List<RejectedTrade>(), Ids = ids.Select((id, index) => new SavedTrade { Index = index, Id = id }).ToList() }; }

        // R1 + R2
        [Fact]
        public void R1_TradesAnswerReadsIdsAndOldAnswersHaveNone()
        {
            var answer = Json.Read<TradesAnswer>(NewBody);
            Assert.Equal(2, answer.Ids.Count); Assert.Equal(0, answer.Ids[0].Index); Assert.Equal(812, answer.Ids[0].Id); Assert.Equal(640, answer.Ids[1].Id);
            Assert.Null(Json.Read<TradesAnswer>("{\"inserted\":1,\"duplicates\":0,\"rejected\":[]}").Ids);
        }
        [Fact]
        public void R2_OldAnswerShapeIgnoresTheNewIdsField()
        {
            var old = Json.Read<OldTradesAnswer>(NewBody);
            Assert.Equal(1, old.Inserted); Assert.Equal(1, old.Duplicates); Assert.Single(old.Rejected); Assert.Equal(2, old.Rejected[0].Index);
        }
        [Fact]
        public async Task R2_RealHttpAnswerWithIdsIsAGoodSendAndOpensTheNewestTrade()
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles(); var settings = Fixtures.Settings(files, true); var queue = new OutboxQueue("queue", files, delegate { });
                var handler = new FakeHttp();
                handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    request.RequestUri.AbsolutePath == "/api/nt/trades" ? "{\"inserted\":2,\"duplicates\":0,\"rejected\":[],\"ids\":[{\"index\":0,\"id\":811},{\"index\":1,\"id\":812}]}"
                    : Json.Write((PingAnswer)Fixtures.Ping().Body)) });
                using (var client = new ApiClient(Base, handler, delegate { }))
                {
                    var session = new SessionCoordinator(settings, queue, client, new EventLog(folder.Path));
                    var opened = new List<long>(); session.ResetWanted += id => opened.Add(id);
                    queue.DurableAdded += delegate { session.NotifyQueued(); };
                    long now = Fixtures.Time + 2000000;
                    await session.ProcessAsync(now, CancellationToken.None);
                    queue.Add(TradeAt("exit1", now - 120000)); queue.Add(TradeAt("exit2", now - 60000));
                    await session.ProcessAsync(now, CancellationToken.None);
                    Assert.Equal(0, queue.Count); Assert.Equal(ZkStatus.Connected, session.Snapshot().Status);
                    Assert.Equal(new long[] { 812 }, opened);
                }
            }
        }

        // R3
        [Fact]
        public void R3_PickTradeTakesTheNewestFreshTrade()
        {
            long now = Fixtures.Time;
            var batch = Batch(now - 60000, now - 5000, now - 120000);
            Assert.Equal(641, ResetRules.PickTrade(batch, Ids(640, 641, 642), now, 0, new List<long>()).Id);
            // Same exit time: the later item in the batch wins.
            Assert.Equal(2, ResetRules.PickTrade(Batch(now - 1000, now - 1000, now - 1000), Ids(1, 2, 3), now, 0, null).Index);
        }
        [Fact]
        public void R3_TradeClosedElevenMinutesAgoIsNotOpened()
        {
            long now = Fixtures.Time;
            Assert.Null(ResetRules.PickTrade(Batch(now - 11 * 60000), Ids(812), now, 0, null));
            Assert.Null(ResetRules.PickTrade(Batch(now - ResetRules.FreshMs - 1), Ids(812), now, 0, null));
            Assert.Equal(812, ResetRules.PickTrade(Batch(now - ResetRules.FreshMs), Ids(812), now, 0, null).Id);
            // A stale newest trade does not hide an older-in-batch fresh one.
            Assert.Equal(7, ResetRules.PickTrade(Batch(now - 30000, now - 11 * 60000), Ids(7, 8), now, 0, null).Id);
        }
        [Fact]
        public void R3_PickTradeIgnoresBadOpenedRejectedAndPreRestartItems()
        {
            long now = Fixtures.Time;
            var batch = Batch(now - 1000, now - 2000);
            Assert.Null(ResetRules.PickTrade(batch, new TradesAnswer { Rejected = new List<RejectedTrade>(), Ids = null }, now, 0, null));
            var bad = new TradesAnswer { Rejected = new List<RejectedTrade>(), Ids = new List<SavedTrade> { null, new SavedTrade { Index = -1, Id = 5 }, new SavedTrade { Index = 2, Id = 6 }, new SavedTrade { Index = 0, Id = 0 }, new SavedTrade { Index = 0, Id = -3 } } };
            Assert.Null(ResetRules.PickTrade(batch, bad, now, 0, null));
            Assert.Equal(9, ResetRules.PickTrade(batch, Ids(8, 9), now, 0, new List<long> { 8 }).Id);
            var rejected = Ids(8, 9); rejected.Rejected.Add(new RejectedTrade { Index = 0, Error = "bad" });
            Assert.Equal(9, ResetRules.PickTrade(batch, rejected, now, 0, null).Id);
            // Restart rule: a trade that closed before this NinjaTrader run started never opens.
            Assert.Null(ResetRules.PickTrade(batch, Ids(8, 9), now, now - 500, null));
            Assert.Equal(8, ResetRules.PickTrade(batch, Ids(8, 9), now, now - 1000, null).Id);
            Assert.Null(ResetRules.PickTrade(null, Ids(8), now, 0, null));
            Assert.Null(ResetRules.PickTrade(batch, null, now, 0, null));
        }

        // R4
        [Fact]
        public async Task R4_ApiClientPostsHandoffWithBearerAndNoBodyAndMasksTheKey()
        {
            using (var folder = new TestFolder())
            {
                var log = new EventLog(folder.Path); var handler = new FakeHttp();
                string key = Fixtures.HandoffKey;
                handler.Send = async (request, cancellation) => {
                    Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/api/nt/handoff", request.RequestUri.AbsolutePath);
                    Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.Equal(Fixtures.Token, request.Headers.Authorization.Parameter);
                    Assert.Null(request.Content);
                    await Task.Yield();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"handoff\":\"" + key + "\",\"expires_in\":60}") };
                };
                using (var client = new ApiClient(Base, handler, log.ProtectSecret))
                {
                    var result = await client.HandoffAsync(Fixtures.Token, CancellationToken.None);
                    Assert.Equal(ApiKind.Ok, result.Kind); Assert.Equal(key, ((HandoffAnswer)result.Body).Handoff); Assert.Equal(60, ((HandoffAnswer)result.Body).ExpiresIn);
                }
                log.Write("Opening https://getzonekeeper.com/auth/addon?h=" + key);
                Assert.DoesNotContain(key, log.Recent[0]); Assert.Contains("[redacted]", log.Recent[0]);
            }
        }
        [Fact]
        public async Task R4_HandoffOkReturnsTheKeyAndNeedsAToken()
        {
            using (var s = new Scope())
            {
                var result = await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None);
                Assert.Equal(HandoffStatus.Ok, result.Status); Assert.Equal(Fixtures.HandoffKey, result.Handoff); Assert.Equal(1, s.Api.Handoffs);
            }
            using (var s = new Scope(connected: false))
            {
                Assert.Equal(HandoffStatus.NotConnected, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                Assert.Equal(0, s.Api.Handoffs);
            }
        }
        [Fact]
        public async Task R4_Handoff401ForgetsTheKeyAndClosesTheWindow()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotConnected, Status = 401, Error = "not_connected" });
                Assert.Equal(HandoffStatus.NotConnected, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                Assert.Null(s.Settings.Token); Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status);
                Assert.Equal(UiText.Disconnected, s.Session.Snapshot().Message); Assert.Equal(1, s.KeyRemoved);
            }
        }
        [Fact]
        public async Task R4_Handoff403NotPaidPausesAndKeepsTheKey()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403, Error = "not_paid" });
                Assert.Equal(HandoffStatus.NotPaid, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                Assert.Equal(ZkStatus.Paused, s.Session.Snapshot().Status); Assert.NotNull(s.Settings.Token); Assert.Equal(0, s.KeyRemoved);
            }
        }
        [Fact]
        public async Task R4_Handoff404IsUnsupportedAndLogsOnce()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                for (int i = 0; i < 2; i++)
                {
                    s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Status = 404 });
                    Assert.Equal(HandoffStatus.Unsupported, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                }
                Assert.Equal(1, s.Log.Recent.Count(x => x.EndsWith(UiText.ResetUnsupported, StringComparison.Ordinal)));
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
            }
        }
        [Fact]
        public async Task R4_HandoffNoAnswerOr429NeverChangesTradeSending()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                long next = s.Session.Snapshot().NextTryMs;
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.TooMany, Status = 429, RetryAfter = TimeSpan.FromSeconds(90) });
                var busy = await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None);
                Assert.Equal(HandoffStatus.Offline, busy.Status); Assert.Equal(TimeSpan.FromSeconds(90), busy.RetryAfter);
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Status = 503 });
                Assert.Equal(HandoffStatus.Offline, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Error = "No answer from getzonekeeper.com." });
                Assert.Equal(HandoffStatus.Offline, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status); Assert.Equal(next, s.Session.Snapshot().NextTryMs);
                // A trade still goes out at once.
                s.Queue.Add(Fixtures.Trade()); await s.Step(Fixtures.Time);
                Assert.Equal(1, s.Api.Sends); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Theory]
        [InlineData("short")]
        [InlineData(null)]
        [InlineData("zkd_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [InlineData("hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh!")]
        public async Task R4_HandoffWithABadKeyIsTreatedAsNoAnswer(string key)
        {
            using (var s = new Scope())
            {
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new HandoffAnswer { Handoff = key, ExpiresIn = 60 } });
                Assert.Equal(HandoffStatus.Offline, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
                s.Api.HandoffAnswers.Enqueue(new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = null });
                Assert.Equal(HandoffStatus.Offline, (await s.Session.HandoffAsync(Fixtures.Time, CancellationToken.None)).Status);
            }
        }
        [Fact]
        public async Task R4_ApiClientBadHandoffJsonIsOffline()
        {
            var handler = new FakeHttp();
            handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });
            using (var client = new ApiClient(Base, handler, delegate { }))
                Assert.Equal(ApiKind.Offline, (await client.HandoffAsync(Fixtures.Token, CancellationToken.None)).Kind);
        }

        // R5
        [Fact]
        public void R5_LoginRequestIsAPostWithTheKeyInHeaderAndBodyNotInTheUrl()
        {
            string key = "abc_DEF-" + new string('x', 35);
            var login = ResetRules.LoginRequest(Base, key, 812);
            Assert.Equal("POST", login.Method);
            Assert.Equal("https://getzonekeeper.com/auth/addon?trade=812", login.Url);
            Assert.DoesNotContain(key, login.Url); Assert.DoesNotContain("h=", login.Url);
            Assert.Equal("Content-Type: application/x-www-form-urlencoded\r\nX-ZK-Handoff: " + key, login.Headers);
            Assert.Equal("h=" + key, login.Body);
            Assert.Equal("https://getzonekeeper.com/auth/addon", ResetRules.LoginRequest(Base, key, 0).Url);
            Assert.Equal("https://test.example.com:8443/auth/addon", ResetRules.LoginRequest("https://test.example.com:8443", key, 0).Url);
            Assert.Null(ResetRules.LoginRequest(Base, "bad key", 812));
            Assert.Null(ResetRules.LoginRequest(Base, key + "\r\nX-Evil: 1", 812));
            Assert.Null(ResetRules.LoginRequest("http://getzonekeeper.com", key, 812));
        }
        [Fact]
        public void R5_SiteChecks()
        {
            Assert.True(ResetRules.IsSiteUrl(Base, "https://getzonekeeper.com/app/?trade=1#today"));
            Assert.True(ResetRules.IsSiteUrl(Base, "https://GETZONEKEEPER.com/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "https://evil.example.com/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "https://getzonekeeper.com.evil.example.com/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "http://getzonekeeper.com/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "https://getzonekeeper.com:8443/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "https://user:pw@getzonekeeper.com/"));
            Assert.False(ResetRules.IsSiteUrl(Base, "not a url"));
            Assert.False(ResetRules.IsSiteUrl(Base, null));
            Assert.True(ResetRules.IsLoginPage(Base, "https://getzonekeeper.com/login/?state=expired"));
            Assert.True(ResetRules.IsLoginPage(Base, "https://getzonekeeper.com/login"));
            Assert.False(ResetRules.IsLoginPage(Base, "https://getzonekeeper.com/loginhelp/"));
            Assert.False(ResetRules.IsLoginPage(Base, "https://evil.example.com/login/"));
        }
        [Fact]
        public void R5_NavigationKeepsSitePagesAndSendsOtherClicksToTheNormalBrowser()
        {
            Assert.Equal(NavigationChoice.Allow, ResetRules.Navigation(Base, "https://getzonekeeper.com/app/#today", false));
            Assert.Equal(NavigationChoice.LoginPage, ResetRules.Navigation(Base, "https://getzonekeeper.com/login/?state=expired", false));
            Assert.Equal(NavigationChoice.OpenOutside, ResetRules.Navigation(Base, "https://www.youtube.com/@getzonekeeper", true));
            Assert.Equal(NavigationChoice.Block, ResetRules.Navigation(Base, "https://www.youtube.com/@getzonekeeper", false));
            Assert.Equal(NavigationChoice.Block, ResetRules.Navigation(Base, "http://example.com/", true));
            Assert.Equal(NavigationChoice.Block, ResetRules.Navigation(Base, "javascript:alert(1)", true));
            Assert.Equal(NavigationChoice.Block, ResetRules.Navigation(Base, "file:///C:/Windows/", true));
            Assert.Equal(NavigationChoice.Block, ResetRules.Navigation(Base, "https://user@example.com/", true));
        }

        // R6
        [Fact]
        public void R6_WebMessages()
        {
            Assert.Equal("{\"type\":\"trade\",\"id\":812}", ResetRules.TradeMessage(812));
            Assert.True(ResetRules.IsReady("{\"type\":\"ready\"}"));
            foreach (string junk in new[] { "{\"type\":\"trade\"}", "\"ready\"", "junk", "", null, "{}", "[1,2]", "{\"type\":5}" })
                Assert.False(ResetRules.IsReady(junk));
        }

        // R7
        [Fact]
        public async Task R7_GoodSendWithIdsRaisesResetWantedOnceAndLogsIt()
        {
            using (var s = new Scope())
            {
                var trade = Fixtures.Trade(); long now = trade.ExitTime + 60000;
                await s.Send(now, Saved(812), trade);
                Assert.Equal(new long[] { 812 }, s.Opened); Assert.Equal(0, s.Queue.Count);
                Assert.Contains(s.Log.Recent, x => x.Contains("Opened the reset for the trade closed at"));
                // The same trade saved again (a resend) never opens twice in this run.
                s.Api.SendAnswers.Enqueue(Saved(812)); s.Queue.Add(Fixtures.Trade("exit1")); await s.Step(now);
                Assert.Single(s.Opened);
            }
        }
        [Fact]
        public async Task R7_TwoTradesInOneSendOpenOnlyTheNewest()
        {
            using (var s = new Scope())
            {
                long now = Fixtures.Time + 3600000;
                await s.Send(now, Saved(500, 501), TradeAt("exit1", now - 50000), TradeAt("exit2", now - 20000));
                Assert.Equal(new long[] { 501 }, s.Opened);
            }
        }
        [Fact]
        public async Task R7_TwoTradesInARowOpenEachInTurn()
        {
            using (var s = new Scope())
            {
                long now = Fixtures.Time + 3600000;
                await s.Send(now, Saved(500), TradeAt("exit1", now - 50000));
                s.Api.SendAnswers.Enqueue(Saved(501)); s.Queue.Add(TradeAt("exit2", now - 1000)); await s.Step(now);
                Assert.Equal(new long[] { 500, 501 }, s.Opened);
            }
        }
        [Fact]
        public async Task R7_NoWindowForFailedSendsOldAnswersOrInvalidAnswers()
        {
            foreach (var answer in new[] {
                new ApiResult { Kind = ApiKind.BadRequest, Status = 400, Error = "bad" },
                new ApiResult { Kind = ApiKind.Offline, Status = 503 },
                new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TradesAnswer { Inserted = 5, Rejected = new List<RejectedTrade>(), Ids = new List<SavedTrade> { new SavedTrade { Index = 0, Id = 1 } } } },
                Fixtures.Sent(1) })
                using (var s = new Scope())
                {
                    var trade = Fixtures.Trade();
                    await s.Send(trade.ExitTime + 1000, answer, trade);
                    Assert.Empty(s.Opened);
                }
        }
        [Fact]
        public async Task R7_AHandlerThatThrowsNeverStopsTradeSending()
        {
            using (var s = new Scope())
            {
                s.Session.ResetWanted += id => { throw new InvalidOperationException("window broke"); };
                var trade = Fixtures.Trade();
                await s.Send(trade.ExitTime + 1000, Saved(812), trade);
                Assert.Equal(0, s.Queue.Count); Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
                Assert.Equal(new long[] { 812 }, s.Opened);
                Assert.Contains(s.Log.Recent, x => x.Contains("Could not open the reset window (InvalidOperationException)"));
            }
        }
        [Fact]
        public async Task R7_CheckboxOffMeansNothingOpens()
        {
            using (var s = new Scope())
            {
                s.Settings.SetAutoReset(false);
                var trade = Fixtures.Trade();
                await s.Send(trade.ExitTime + 1000, Saved(812), trade);
                Assert.Empty(s.Opened); Assert.Equal(0, s.Queue.Count); Assert.False(s.Session.Snapshot().AutoReset);
                s.Settings.SetAutoReset(true);
                s.Api.SendAnswers.Enqueue(Saved(813)); s.Queue.Add(TradeAt("exit1", trade.ExitTime + 500)); await s.Step(trade.ExitTime + 1000);
                Assert.Equal(new long[] { 813 }, s.Opened);
            }
        }
        [Fact]
        public async Task R7_AfterARestartATradeFromBeforeTheStartNeverOpens()
        {
            var trade = Fixtures.Trade();
            using (var s = new Scope(runStartedMs: trade.ExitTime + 30000))
            {
                // Closed before NinjaTrader restarted, sent after the start: fresh, but no pop-up.
                await s.Send(trade.ExitTime + 60000, Saved(812), trade);
                Assert.Empty(s.Opened); Assert.Equal(0, s.Queue.Count);
                // A trade closed after the start opens normally.
                s.Api.SendAnswers.Enqueue(Saved(813)); s.Queue.Add(TradeAt("exit1", trade.ExitTime + 45000)); await s.Step(trade.ExitTime + 60000);
                Assert.Equal(new long[] { 813 }, s.Opened);
            }
        }
        [Fact]
        public async Task R7_ATradeSentElevenMinutesLateNeverOpens()
        {
            using (var s = new Scope())
            {
                var trade = Fixtures.Trade();
                await s.Send(trade.ExitTime + 11 * 60000, Saved(812), trade);
                Assert.Empty(s.Opened); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Fact]
        public async Task R7_DisconnectRaisesKeyRemoved()
        {
            using (var s = new Scope())
            {
                await s.Session.DisconnectAsync(Fixtures.Time, CancellationToken.None);
                Assert.Null(s.Settings.Token); Assert.Equal(1, s.KeyRemoved);
            }
        }

        // R8
        [Fact]
        public void R8_AutoResetIsOnByDefaultAndSaved()
        {
            var files = new MemoryFiles();
            files.Write("settings", "{\"version\":1,\"disabled_accounts\":[\"Playback101\"],\"debug_fills\":false}");
            var old = Fixtures.Settings(files);
            Assert.True(old.AutoReset); Assert.Null(old.ResetWindow); Assert.False(old.IsEnabled("Playback101"));
            Assert.True(old.SetAutoReset(false));
            Assert.Contains("\"auto_reset\":false", files.Read("settings"));
            Assert.False(Fixtures.Settings(files).AutoReset);
            Assert.True(Fixtures.Settings(files).SetAutoReset(true));
            Assert.True(Fixtures.Settings(files).AutoReset);
            Assert.True(Fixtures.Settings(new MemoryFiles()).AutoReset);
        }
        [Fact]
        public void R8_SavedWindowPlaceIsKeptAndBadValuesAreIgnored()
        {
            var files = new MemoryFiles();
            var settings = Fixtures.Settings(files);
            Assert.True(settings.SetResetWindow(new WindowBounds { Left = 1200, Top = 40, Width = 470, Height = 880 }));
            var back = Fixtures.Settings(files).ResetWindow;
            Assert.Equal(1200, back.Left); Assert.Equal(40, back.Top); Assert.Equal(470, back.Width); Assert.Equal(880, back.Height);
            Assert.False(settings.SetResetWindow(new WindowBounds { Left = 0, Top = 0, Width = 100, Height = 880 }));
            Assert.False(settings.SetResetWindow(null));
            foreach (string bad in new[] { "{\"left\":0,\"top\":0,\"width\":10,\"height\":880}", "{\"left\":0,\"top\":0,\"width\":460,\"height\":-5}",
                "{\"left\":9e12,\"top\":0,\"width\":460,\"height\":860}", "{\"left\":0,\"top\":0,\"width\":460,\"height\":99999}", "{}" })
            {
                var badFiles = new MemoryFiles();
                badFiles.Write("settings", "{\"version\":1,\"disabled_accounts\":[],\"reset_window\":" + bad + "}");
                var loaded = Fixtures.Settings(badFiles);
                Assert.Null(loaded.ResetWindow); Assert.True(loaded.AutoReset);
            }
        }
        [Fact]
        public void R8_WindowPlaceUsesSavedPlaceOnlyWhenItIsOnAScreen()
        {
            var screen = new WindowBounds { Left = 0, Top = 0, Width = 1920, Height = 1080 };
            var work = new WindowBounds { Left = 0, Top = 0, Width = 1920, Height = 1040 };
            var fallback = ResetRules.Place(null, screen, work);
            Assert.Equal(1460, fallback.Left); Assert.Equal(0, fallback.Top); Assert.Equal(460, fallback.Width); Assert.Equal(860, fallback.Height);
            var saved = new WindowBounds { Left = 100, Top = 50, Width = 500, Height = 900 };
            Assert.Equal(100, ResetRules.Place(saved, screen, work).Left);
            // Second monitor that is gone now: back to the default place.
            Assert.Equal(1460, ResetRules.Place(new WindowBounds { Left = 2500, Top = 50, Width = 500, Height = 900 }, screen, work).Left);
            Assert.Equal(1460, ResetRules.Place(new WindowBounds { Left = 100, Top = 1070, Width = 500, Height = 900 }, screen, work).Left);
            // Small work area: the height fits it.
            Assert.Equal(700, ResetRules.Place(null, screen, new WindowBounds { Left = 0, Top = 0, Width = 1280, Height = 700 }).Height);
        }
        [Fact]
        public void LoaderFolderIsSetOnlyWhenOurLoaderIsInBinCustom()
        {
            string custom = System.IO.Path.Combine("C:", "NT", "bin", "Custom");
            string loader = System.IO.Path.Combine(custom, "WebView2Loader.dll");
            Assert.Equal(custom, ResetRules.LoaderFolder(custom, path => path == loader));
            Assert.Null(ResetRules.LoaderFolder(custom, path => false));
            Assert.Null(ResetRules.LoaderFolder(null, path => true));
        }
        [Fact]
        public void OnlyADeadMainBrowserProcessClosesTheWindow()
        {
            Assert.True(ResetRules.ClosesWindow("BrowserProcessExited"));
            foreach (string kind in new[] { "RenderProcessExited", "RenderProcessUnresponsive", "FrameRenderProcessExited", "GpuProcessExited", "UtilityProcessExited", "SandboxHelperProcessExited", "UnknownProcessExited", "", null })
                Assert.False(ResetRules.ClosesWindow(kind));
        }
        [Fact]
        public void BrowserProfileLivesInLocalAppDataNotDocuments()
        {
            string local = System.IO.Path.Combine("C:", "Users", "Admin", "AppData", "Local");
            Assert.Equal(System.IO.Path.Combine(local, "ZoneKeeper", "webview2"), ResetRules.ProfileFolder(local));
        }
        [Fact]
        public void RetryWaitsGrowAndRespectRetryAfter()
        {
            Assert.Equal(new long[] { 5000, 15000, 30000, 60000, 60000, 60000 }, Enumerable.Range(0, 6).Select(i => ResetRules.RetryDelayMs(i, null)).ToArray());
            Assert.Equal(120000, ResetRules.RetryDelayMs(0, TimeSpan.FromMinutes(2)));
            Assert.Equal(3600000, ResetRules.RetryDelayMs(0, TimeSpan.FromDays(2)));
            Assert.Equal(5000, ResetRules.RetryDelayMs(0, TimeSpan.FromSeconds(-5)));
        }
        [Fact]
        public void StatusTextsAreSimpleAndCountSeconds()
        {
            Assert.Equal("No answer from getzonekeeper.com. Check the internet. Trying again in 15 seconds.", UiText.ResetStatus(ResetView.NoAnswer, 15000));
            Assert.Equal("", UiText.ResetStatus(ResetView.Page, 0));
            foreach (ResetView view in Enum.GetValues(typeof(ResetView)))
                if (view != ResetView.Page) Assert.NotEqual("", UiText.ResetStatus(view, 5000));
            Assert.StartsWith("Reset window could not start (FileNotFoundException).", UiText.ResetStartFailed("FileNotFoundException"));
        }
    }
}
