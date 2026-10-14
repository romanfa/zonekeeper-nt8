using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class SessionTests
    {
        private sealed class Scope : IDisposable
        {
            public readonly TestFolder Folder = new TestFolder();
            public readonly MemoryFiles Files = new MemoryFiles();
            public readonly FakeApi Api = new FakeApi();
            public readonly OutboxQueue Queue;
            public readonly Settings Settings;
            public readonly SessionCoordinator Session;
            public readonly EventLog Log;
            public Scope(bool connected = true)
            {
                Settings = Fixtures.Settings(Files, connected); Queue = new OutboxQueue("queue", Files, delegate { }); Log = new EventLog(Folder.Path);
                Session = new SessionCoordinator(Settings, Queue, Api, Log); Queue.DurableAdded += delegate { Session.NotifyQueued(); };
            }
            public Task Step(long now = Fixtures.Time) { return Session.ProcessAsync(now, CancellationToken.None); }
            public void Dispose() { Folder.Dispose(); }
        }
        [Fact]
        public async Task StartWithKeyChecksAndIdlePingsEveryFiveMinutes()
        {
            using (var s = new Scope())
            {
                Assert.Equal(ZkStatus.Checking, s.Session.Snapshot().Status); await s.Step(); Assert.Equal(1, s.Api.Pings);
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status); await s.Step(Fixtures.Time + 299999); Assert.Equal(1, s.Api.Pings);
                await s.Step(Fixtures.Time + 300000); Assert.Equal(2, s.Api.Pings);
            }
        }
        [Fact]
        public async Task QueueCollectsWithoutTokenAndGoesOutAfterClaim()
        {
            using (var s = new Scope(false))
            {
                s.Queue.Add(Fixtures.Trade()); await s.Step(); Assert.Equal(0, s.Api.Sends); Assert.Equal(0, s.Api.Pings);
                await s.Session.ConnectAsync("zk-ab3d-9kxp", "TRADING-PC", Fixtures.Time, CancellationToken.None);
                Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status); Assert.Equal(Fixtures.Token, s.Settings.Token);
                await s.Step(); Assert.Equal(1, s.Api.Sends); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Fact]
        public async Task WrongShapeMakesNoNetworkClaim()
        {
            using (var s = new Scope(false))
            {
                await s.Session.ConnectAsync("ZK-IBCD-1234", "PC", Fixtures.Time, CancellationToken.None);
                Assert.Equal(0, s.Api.Claims); Assert.Equal(UiText.WrongShape, s.Session.Snapshot().Message); Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status);
            }
        }
        [Theory]
        [InlineData(ApiKind.CodeInvalid)]
        [InlineData(ApiKind.NotPaid)]
        [InlineData(ApiKind.TooManyDevices)]
        [InlineData(ApiKind.TooManyTries)]
        [InlineData(ApiKind.Offline)]
        public async Task FailedClaimAlwaysKeepsConnectFieldAvailable(ApiKind kind)
        {
            using (var s = new Scope(false))
            {
                s.Api.ClaimResult = new ApiResult { Kind = kind };
                await s.Session.ConnectAsync("ZK-AB3D-9KXP", "PC", Fixtures.Time, CancellationToken.None);
                Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status); Assert.Null(s.Settings.Token);
                Assert.Equal(ConnectionPolicy.ClaimMessage(kind), s.Session.Snapshot().Message); await s.Step(Fixtures.Time + 3600000); Assert.Equal(1, s.Api.Claims);
            }
        }
        [Fact]
        public async Task UnauthorizedDeletesTokenAndKeepsOutbox()
        {
            using (var s = new Scope())
            {
                s.Queue.Add(Fixtures.Trade()); s.Api.PingAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotConnected, Status = 401 });
                await s.Step(); Assert.Null(s.Settings.Token); Assert.Equal(1, s.Queue.Count); Assert.Equal(UiText.Disconnected, s.Session.Snapshot().Message);
                Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status); Assert.Null(Fixtures.Settings(s.Files).Token);
            }
        }
        [Fact]
        public async Task PausedKeepsTokenAndHourlyDeadlineThroughQueueWake()
        {
            using (var s = new Scope())
            {
                s.Api.PingAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403 }); await s.Step();
                s.Queue.Add(Fixtures.Trade()); Assert.Equal(Fixtures.Time + 3600000, s.Session.Snapshot().NextTryMs);
                await s.Step(Fixtures.Time + 60000); Assert.Equal(1, s.Api.Pings); Assert.Equal(0, s.Api.Sends); Assert.Equal(Fixtures.Token, s.Settings.Token);
                s.Session.TryNow(); await s.Step(Fixtures.Time + 60000); Assert.Equal(ZkStatus.Connected, s.Session.Snapshot().Status);
                await s.Step(Fixtures.Time + 60000); Assert.Equal(1, s.Api.Sends); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Fact]
        public async Task RateLimitWaitIsNotBypassedByNewTradesButExplicitRetryWorks()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade("a")); s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.TooMany, Status = 429 }); await s.Step();
                Assert.Equal(Fixtures.Time + 60000, s.Session.Snapshot().NextTryMs); s.Queue.Add(Fixtures.Trade("b"));
                Assert.Equal(Fixtures.Time + 60000, s.Session.Snapshot().NextTryMs); await s.Step(Fixtures.Time + 15000); Assert.Equal(1, s.Api.Sends);
                s.Session.TryNow(); await s.Step(Fixtures.Time + 15000); await s.Step(Fixtures.Time + 15000); Assert.Equal(2, s.Api.Sends); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Fact]
        public async Task ConnectedQueueWakeSendsImmediatelyAndInBatchesOf20()
        {
            using (var s = new Scope())
            {
                await s.Step(); Assert.Equal(Fixtures.Time + 300000, s.Session.Snapshot().NextTryMs);
                for (int i = 0; i < 21; i++) s.Queue.Add(Fixtures.Trade("e" + i));
                Assert.Equal(0, s.Session.Snapshot().NextTryMs); await s.Step(); Assert.Equal(1, s.Queue.Count);
                await s.Step(); Assert.Equal(new[] { 20, 1 }, s.Api.BatchSizes); Assert.Equal(0, s.Queue.Count);
            }
        }
        [Theory]
        [InlineData(400)]
        [InlineData(413)]
        public async Task RejectedBatchSplitsSinglesAndDropsOnlyPermanentlyBadSingle(int code)
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade("a")); s.Queue.Add(Fixtures.Trade("b"));
                s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.BadRequest, Status = code, Error = "bad batch" });
                s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.BadRequest, Status = 400, Error = "bad trade" });
                await s.Step(); Assert.Equal(2, s.Queue.Count); await s.Step(); Assert.Equal(1, s.Queue.Count); await s.Step(); Assert.Equal(0, s.Queue.Count);
                Assert.Equal(new[] { 2, 1, 1 }, s.Api.BatchSizes); Assert.Contains(s.Log.Recent, x => x.Contains("The server refused this trade: bad trade"));
            }
        }
        [Fact]
        public async Task JsonContentTypeErrorKeepsQueueAndBacksOff()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade()); s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.BadRequest, Status = 415 }); await s.Step();
                Assert.Equal(1, s.Queue.Count); Assert.Equal(ZkStatus.Offline, s.Session.Snapshot().Status); Assert.Equal(Fixtures.Time + 15000, s.Session.Snapshot().NextTryMs);
            }
        }
        [Fact]
        public async Task All200TradesSavedDuplicateOrRejectedAreRemovedAndFutureHintLogged()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade("a")); s.Queue.Add(Fixtures.Trade("b")); s.Queue.Add(Fixtures.Trade("c"));
                s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TradesAnswer { Inserted = 1, Duplicates = 1, Rejected = new List<RejectedTrade> { new RejectedTrade { Index = 2, Error = "exit_time is in the future" } } } });
                await s.Step(); Assert.Equal(0, s.Queue.Count); Assert.Equal(Fixtures.Time, s.Session.Snapshot().LastSentMs);
                Assert.Contains(s.Log.Recent, x => x.Contains("Sent 1 trade.")); Assert.Contains(s.Log.Recent, x => x.Contains("Already saved: 1 trade."));
                Assert.Contains(s.Log.Recent, x => x.Contains("Sync now."));
            }
        }
        [Fact]
        public async Task Incomplete200ResponseKeepsOutbox()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade()); s.Api.SendAnswers.Enqueue(Fixtures.Sent(0)); await s.Step();
                Assert.Equal(1, s.Queue.Count); Assert.Equal(ZkStatus.Offline, s.Session.Snapshot().Status);
            }
        }
        [Fact]
        public async Task FailedOutboxRemovalResendsSafelyAsDuplicateAfterRecovery()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade()); s.Files.FailWrites = x => x == "queue";
                await s.Step(); Assert.Equal(1, s.Queue.Count);
                s.Files.FailWrites = null; await s.Step(Fixtures.Time + 15000);
                s.Api.SendAnswers.Enqueue(new ApiResult { Kind = ApiKind.Ok, Status = 200, Body = new TradesAnswer { Inserted = 0, Duplicates = 1, Rejected = new List<RejectedTrade>() } });
                await s.Step(Fixtures.Time + 15000); Assert.Equal(0, s.Queue.Count); Assert.Equal(2, s.Api.Sends);
            }
        }
        [Fact]
        public async Task FailedClaimTokenSaveCannotSendUntilPersistenceRecovers()
        {
            using (var s = new Scope(false))
            {
                s.Queue.Add(Fixtures.Trade()); s.Files.FailWrites = x => x == "settings";
                await s.Session.ConnectAsync("AB3D9KXP", "PC", Fixtures.Time, CancellationToken.None); await s.Step(); Assert.Equal(0, s.Api.Sends); Assert.True(s.Settings.IsDirty);
                s.Files.FailWrites = null; await s.Step(Fixtures.Time + 15000); await s.Step(Fixtures.Time + 15000);
                Assert.Equal(1, s.Api.Sends); Assert.Equal(Fixtures.Token, Fixtures.Settings(s.Files).Token);
            }
        }
        [Fact]
        public async Task ClockWarningDoesNotAlterQueuedTradeTimes()
        {
            using (var s = new Scope())
            {
                s.Api.PingAnswers.Enqueue(Fixtures.Ping(Fixtures.Time + 420000)); await s.Step();
                Assert.Equal(420000, s.Session.Snapshot().ClockOffsetMs); Assert.Contains("7 minutes off", UiText.ClockWarning(s.Session.Snapshot().ClockOffsetMs));
                var trade = Fixtures.Trade(); s.Queue.Add(trade); Assert.Equal(Fixtures.Time, s.Queue.Peek(1)[0].Trade.EntryTime);
                Assert.Equal("", UiText.ClockWarning(120000));
            }
        }
        [Fact]
        public async Task DisconnectSerializesBehindInFlightSendAndThenDeletesKey()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade());
                var response = new TaskCompletionSource<ApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                s.Api.SendOverride = cancellation => response.Task;
                var send = s.Step(); Assert.Equal(1, s.Api.Sends);
                var disconnect = s.Session.DisconnectAsync(Fixtures.Time, CancellationToken.None); Assert.Equal(0, s.Api.Disconnects);
                response.SetResult(Fixtures.Sent(1)); await send; await disconnect;
                Assert.Equal(1, s.Api.Disconnects); Assert.Null(s.Settings.Token); Assert.Equal(ZkStatus.NotConnected, s.Session.Snapshot().Status);
                await s.Step(Fixtures.Time + 3600000); Assert.Equal(1, s.Api.Sends);
            }
        }
        [Fact]
        public async Task FailedDisconnectStopsSendingAndRetriesRevocation()
        {
            using (var s = new Scope())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade()); s.Api.DisconnectResult = new ApiResult { Kind = ApiKind.Offline };
                await s.Session.DisconnectAsync(Fixtures.Time, CancellationToken.None); Assert.Equal(Fixtures.Token, s.Settings.Token);
                s.Api.DisconnectResult = new ApiResult { Kind = ApiKind.NotConnected, Status = 401 };
                await s.Step(Fixtures.Time + 15000); Assert.Equal(0, s.Api.Sends); Assert.Null(s.Settings.Token); Assert.Equal(1, s.Queue.Count);
            }
        }
        [Fact]
        public async Task CancellationQuiescesInFlightSendAndDoesNotRemoveQueue()
        {
            using (var s = new Scope()) using (var cancellation = new CancellationTokenSource())
            {
                await s.Step(); s.Queue.Add(Fixtures.Trade());
                s.Api.SendOverride = token => {
                    var result = new TaskCompletionSource<ApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() => result.TrySetCanceled()); return result.Task;
                };
                var send = s.Session.ProcessAsync(Fixtures.Time, cancellation.Token); cancellation.Cancel(); await send; await s.Session.QuiesceAsync();
                Assert.Equal(1, s.Queue.Count); Assert.Equal(Fixtures.Token, s.Settings.Token);
            }
        }
        [Fact]
        public async Task SnapshotDuringQueueUpdatesAndTransitionsDoesNotDeadlock()
        {
            using (var s = new Scope())
            {
                await s.Step();
                var writer = Task.Run(() => { for (int i = 0; i < 40; i++) s.Queue.Add(Fixtures.Trade("e" + i)); });
                var reader = Task.Run(() => { for (int i = 0; i < 300; i++) s.Session.Snapshot(); });
                var both = Task.WhenAll(writer, reader);
                Assert.Same(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)))); await both;
                Assert.Equal(40, s.Queue.Count);
            }
        }
    }
}
