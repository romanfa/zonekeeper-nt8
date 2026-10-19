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
    // Add-on 0.2.0: the window comes up by itself after a closed trade (plan notes 29 and 31).
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
            public readonly ResetOutbox Resets;
            public readonly Settings Settings;
            public readonly SessionCoordinator Session;
            public readonly EventLog Log;
            public readonly List<long> Opened = new List<long>();
            public readonly List<string> Saved = new List<string>();  // "812:logged" / "812:refused"
            public int KeyRemoved;
            public Scope(bool connected = true, long runStartedMs = 0)
            {
                Settings = Fixtures.Settings(Files, connected); Queue = new OutboxQueue("queue", Files, delegate { }); Log = new EventLog(Folder.Path);
                Resets = new ResetOutbox("resets", Files, Log.Write);
                Session = new SessionCoordinator(Settings, Queue, Resets, Api, Log) { RunStartedMs = runStartedMs };
                Queue.DurableAdded += delegate { Session.NotifyQueued(); };
                Session.ResetWanted += id => Opened.Add(id);
                Session.ResetSaved += (id, logged) => Saved.Add(id + (logged ? ":logged" : ":refused"));
                Session.KeyRemoved += () => KeyRemoved++;
            }
            // The step-5 tap of the window: the body goes into resets.jsonl and the sender is woken.
            public ResetBody QueueReset(long tradeId, long exitMs)
            {
                var session = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(tradeId, -1250, exitMs), TimeZoneInfo.Utc);
                session.RateBefore(5, exitMs + 60000); session.PickThought(session.Thoughts[0]); session.PickLine(session.Thought.Lines[1]); session.FinishAudio();
                var body = session.RateAgain(3, exitMs + 240000);
                Assert.True(Resets.Add(body, exitMs, exitMs + 240000)); Session.NotifyQueued();
                return body;
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
            Assert.True(old.AutoReset); Assert.Null(old.WindowBounds); Assert.False(old.IsEnabled("Playback101"));
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
            Assert.True(settings.SetWindowBounds(new WindowBounds { Left = 1200, Top = 40, Width = 470, Height = 700 }));
            var back = Fixtures.Settings(files).WindowBounds;
            Assert.Equal(1200, back.Left); Assert.Equal(40, back.Top); Assert.Equal(470, back.Width); Assert.Equal(700, back.Height);
            Assert.False(settings.SetWindowBounds(new WindowBounds { Left = 0, Top = 0, Width = 100, Height = 700 }));
            Assert.False(settings.SetWindowBounds(null));
            foreach (string bad in new[] { "{\"left\":0,\"top\":0,\"width\":10,\"height\":700}", "{\"left\":0,\"top\":0,\"width\":480,\"height\":-5}",
                "{\"left\":9e12,\"top\":0,\"width\":480,\"height\":720}", "{\"left\":0,\"top\":0,\"width\":480,\"height\":99999}", "{}" })
            {
                var badFiles = new MemoryFiles();
                badFiles.Write("settings", "{\"version\":1,\"disabled_accounts\":[],\"window\":" + bad + "}");
                var loaded = Fixtures.Settings(badFiles);
                Assert.Null(loaded.WindowBounds); Assert.True(loaded.AutoReset);
            }
        }
        [Fact]
        public void R8_WindowPlaceUsesSavedPlaceOnlyWhenItIsOnAScreen()
        {
            var screen = new WindowBounds { Left = 0, Top = 0, Width = 1920, Height = 1080 };
            var work = new WindowBounds { Left = 0, Top = 0, Width = 1920, Height = 1040 };
            var fallback = ResetRules.Place(null, screen, work);
            Assert.Equal(1440, fallback.Left); Assert.Equal(0, fallback.Top); Assert.Equal(480, fallback.Width); Assert.Equal(720, fallback.Height);
            var saved = new WindowBounds { Left = 100, Top = 50, Width = 500, Height = 900 };
            Assert.Equal(100, ResetRules.Place(saved, screen, work).Left);
            // Second monitor that is gone now: back to the default place.
            Assert.Equal(1440, ResetRules.Place(new WindowBounds { Left = 2500, Top = 50, Width = 500, Height = 900 }, screen, work).Left);
            Assert.Equal(1440, ResetRules.Place(new WindowBounds { Left = 100, Top = 1070, Width = 500, Height = 900 }, screen, work).Left);
            // Small work area: the height fits it.
            Assert.Equal(700, ResetRules.Place(null, screen, new WindowBounds { Left = 0, Top = 0, Width = 1280, Height = 700 }).Height);
        }
        // R9: a finished reset is sent after the trades (plan 31 section 2.5).
        [Fact]
        public async Task R9_AQueuedResetIsSentAfterTheTradesBatchWithTheExactBody()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                var trade = Fixtures.Trade(); long now = trade.ExitTime + 60000;
                var body = s.QueueReset(812, trade.ExitTime);
                s.Queue.Add(trade);
                Assert.Equal(0, s.Session.Snapshot().NextTryMs);
                Assert.Equal(new long[] { 812 }, s.Session.Snapshot().SavingTradeIds.ToArray());
                await s.Step(now);
                Assert.Equal(new[] { "ping", "trades", "reset" }, s.Api.Order.ToArray());
                Assert.Equal(Json.Write(body), Json.Write(s.Api.ResetBodies[0]));
                Assert.Equal(0, s.Resets.Count); Assert.Equal(0, s.Queue.Count); Assert.Empty(s.Session.Snapshot().SavingTradeIds);
                Assert.Equal(new[] { "812:logged" }, s.Saved.ToArray());
                Assert.Contains(s.Log.Recent, x => x.Contains("Reset saved for the trade closed at"));
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status); Assert.Equal(now + 300000, s.Session.Snapshot().NextTryMs);
                Assert.Equal(now, s.Session.Snapshot().LastSentMs);
            }
        }
        [Fact]
        public async Task R9_ResetsGoWithoutAPingAndOnePerLoopOldestFirst()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.QueueReset(812, Fixtures.Time - 500000); s.QueueReset(813, Fixtures.Time - 100000);
                await s.Step(Fixtures.Time);
                Assert.Equal(new[] { "ping", "reset" }, s.Api.Order.ToArray()); Assert.Equal(812, s.Api.ResetBodies[0].TradeId);
                Assert.Equal(1, s.Resets.Count); Assert.Equal(Fixtures.Time, s.Session.Snapshot().NextTryMs);  // the next loop runs at once
                await s.Step(Fixtures.Time);
                Assert.Equal(new[] { "ping", "reset", "reset" }, s.Api.Order.ToArray()); Assert.Equal(813, s.Api.ResetBodies[1].TradeId);
                Assert.Equal(0, s.Resets.Count); Assert.Equal(new[] { "812:logged", "813:logged" }, s.Saved.ToArray());
            }
        }
        [Fact]
        public async Task R9_409MeansDoneElsewhereAndNoRetry()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.QueueReset(812, Fixtures.Time - 100000);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.AlreadyDone, Status = 409, Error = "this trade already has a reset" });
                await s.Step(Fixtures.Time);
                Assert.Equal(0, s.Resets.Count); Assert.Equal(1, s.Api.Resets); Assert.Equal(new[] { "812:logged" }, s.Saved.ToArray());
                Assert.Contains(s.Log.Recent, x => x.EndsWith(UiText.ResetDoneElsewhere, StringComparison.Ordinal));
                Assert.DoesNotContain(s.Log.Recent, x => x.Contains("Reset saved"));
                await s.Step(Fixtures.Time + 300000);
                Assert.Equal(1, s.Api.Resets); Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
            }
        }
        [Fact]
        public async Task R9_400IsDroppedWithOneLogLineAndMarksTheCatalogStaleWhenTextsChanged()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.QueueReset(812, Fixtures.Time - 100000);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.BadRequest, Status = 400, Error = "local time is too far from now" });
                await s.Step(Fixtures.Time);
                Assert.Equal(0, s.Resets.Count); Assert.Equal(new[] { "812:refused" }, s.Saved.ToArray());
                Assert.Equal(1, s.Log.Recent.Count(x => x.EndsWith("The server refused this reset: local time is too far from now", StringComparison.Ordinal)));
                Assert.False(s.Session.TakeCatalogStale()); Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
                await s.Step(Fixtures.Time + 300000);
                Assert.Equal(1, s.Api.Resets);
                s.QueueReset(813, Fixtures.Time - 50000);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.BadRequest, Status = 400, Error = "logic_id does not fit the thought" });
                await s.Step(Fixtures.Time + 300000);
                Assert.True(s.Session.TakeCatalogStale()); Assert.False(s.Session.TakeCatalogStale());
                Assert.Equal(new[] { "812:refused", "813:refused" }, s.Saved.ToArray());
            }
        }
        [Fact]
        public async Task R9_OfflineKeepsTheLineAndTradeSendingGoesOnLater()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                var body = s.QueueReset(812, Fixtures.Time - 100000);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Error = "No answer from getzonekeeper.com." });
                await s.Step(Fixtures.Time);
                Assert.Equal(1, s.Resets.Count); Assert.Empty(s.Saved); Assert.Equal(ZkStatus.Offline, s.Session.Snapshot().Status);
                Assert.Equal(Fixtures.Time + 15000, s.Session.Snapshot().NextTryMs);
                // At the next try: the ping first (Offline → Connected); a waiting trade keeps the reset back.
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.TooMany, Status = 429 });
                s.Queue.Add(Fixtures.Trade()); await s.Step(Fixtures.Time + 15000);
                Assert.Equal(0, s.Api.Sends); Assert.Equal(1, s.Api.Resets); Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status); Assert.Equal(Fixtures.Time + 15000, s.Session.Snapshot().NextTryMs);
                // Then the trade goes, then the reset is tried again with the same request_id (429 → Offline again).
                await s.Step(Fixtures.Time + 15000);
                Assert.Equal(1, s.Api.Sends); Assert.Equal(0, s.Queue.Count); Assert.Equal(new[] { "ping", "reset", "ping", "trades", "reset" }, s.Api.Order.ToArray());
                Assert.Equal(2, s.Api.Resets); Assert.Equal(body.RequestId, s.Api.ResetBodies[1].RequestId); Assert.Equal(1, s.Resets.Count);
                Assert.Equal(ZkStatus.Offline, s.Session.Snapshot().Status); Assert.Equal(Fixtures.Time + 15000 + 60000, s.Session.Snapshot().NextTryMs);
                await s.Step(Fixtures.Time + 75000);
                Assert.Equal(3, s.Api.Resets); Assert.Equal(0, s.Resets.Count); Assert.Equal(new[] { "812:logged" }, s.Saved.ToArray());
                Assert.Equal(body.RequestId, s.Api.ResetBodies[2].RequestId);
            }
        }
        [Fact]
        public async Task R9_401DeletesTheKeyAnd403PausesTheLineWaits()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.QueueReset(812, Fixtures.Time - 100000);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403, Error = "not_paid" });
                await s.Step(Fixtures.Time);
                Assert.Equal(1, s.Resets.Count); Assert.Equal(ZkStatus.Paused, s.Session.Snapshot().Status); Assert.Equal(Fixtures.Time + 3600000, s.Session.Snapshot().NextTryMs);
                s.Api.ResetAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotConnected, Status = 401, Error = "not_connected" });
                await s.Step(Fixtures.Time + 3600000);
                Assert.Null(s.Settings.Token); Assert.Equal(1, s.KeyRemoved); Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status);
                Assert.Equal(1, s.Resets.Count); Assert.Empty(s.Saved);
            }
        }
        [Fact]
        public async Task R9_ADiskErrorOnRemoveTriesAgainLaterAndAThrowingHandlerNeverStopsSending()
        {
            using (var s = new Scope())
            {
                await s.Step(Fixtures.Time);
                s.Session.ResetSaved += (id, logged) => { throw new InvalidOperationException("list broke"); };
                s.QueueReset(812, Fixtures.Time - 100000); s.QueueReset(813, Fixtures.Time - 50000);
                s.Files.FailWrites = path => path == "resets";
                await s.Step(Fixtures.Time);
                Assert.Equal(1, s.Api.Resets); Assert.Equal(2, s.Resets.Count); Assert.Empty(s.Saved); Assert.Equal(ZkStatus.Offline, s.Session.Snapshot().Status);
                s.Files.FailWrites = null;
                // Offline → the ping first, then the same reset again (same request_id = the same row on the site), then the next.
                await s.Step(Fixtures.Time + 15000); await s.Step(Fixtures.Time + 15000);
                Assert.Equal(3, s.Api.Resets); Assert.Equal(s.Api.ResetBodies[0].RequestId, s.Api.ResetBodies[1].RequestId);
                Assert.Equal(0, s.Resets.Count); Assert.Equal(new[] { "812:logged", "813:logged" }, s.Saved.ToArray());
                Assert.Contains(s.Log.Recent, x => x.Contains("Could not update the Today list (InvalidOperationException)"));
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
            }
        }
        [Fact]
        public async Task R9_NothingIsSentWhileNotConnectedOrPaused()
        {
            using (var s = new Scope(connected: false))
            {
                s.QueueReset(812, Fixtures.Time - 100000);
                await s.Step(Fixtures.Time);
                Assert.Equal(0, s.Api.Resets); Assert.Equal(1, s.Resets.Count); Assert.Equal(new long[] { 812 }, s.Session.Snapshot().SavingTradeIds.ToArray());
            }
            using (var s = new Scope())
            {
                s.Api.PingAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403, Error = "not_paid" });
                await s.Step(Fixtures.Time);
                Assert.Equal(ZkStatus.Paused, s.Session.Snapshot().Status);
                s.QueueReset(812, Fixtures.Time - 100000);
                await s.Step(Fixtures.Time + 1000);
                Assert.Equal(0, s.Api.Resets); Assert.Equal(1, s.Resets.Count);
            }
        }
        [Fact]
        public void RetryWaitsGrowAndRespectRetryAfter()
        {
            Assert.Equal(new long[] { 5000, 15000, 30000, 60000, 60000, 60000 }, Enumerable.Range(0, 6).Select(i => ResetRules.RetryDelayMs(i, null)).ToArray());
            Assert.Equal(120000, ResetRules.RetryDelayMs(0, TimeSpan.FromMinutes(2)));
            Assert.Equal(3600000, ResetRules.RetryDelayMs(0, TimeSpan.FromDays(2)));
            Assert.Equal(5000, ResetRules.RetryDelayMs(0, TimeSpan.FromSeconds(-5)));
        }
    }
}
