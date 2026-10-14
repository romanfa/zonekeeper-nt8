using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class SafetyContinuityTests
    {
        private static TradeBuilder Builder() { return new TradeBuilder(TimeZoneInfo.Utc, delegate { }); }
        private static FillCoordinator Worker(MemoryFiles files, OutboxQueue queue)
        { return new FillCoordinator(Builder(), new OpenTradesFile("open", files, delegate { }), queue); }
        private static Fill[] Close(FillCoordinator worker)
        {
            var fills = new[] { Fixtures.Fill("entry", true, 1, 100, 1), Fixtures.Fill("end", false, 1, 101, 0) };
            foreach (var fill in fills) worker.Apply(fill); return fills;
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BackgroundCaptureKeepsCallbacksAlreadyAheadOfStartupOrReconnectApply(bool reconnect)
        {
            var b = Builder(); var worker = new Queue<Action>(); var background = new Queue<Action>(); var source = new object();
            var boundary = new AccountEventBoundary<object>(worker.Enqueue); var fills = new List<Fill>(); var closed = new List<TradeRecord>();
            var refreshed = false;
            boundary.Request(source, background.Enqueue, current => Fixtures.Snapshot(fills), reconnect ? (Action)(() => refreshed = true) : null,
                (current, snapshot) => { Assert.Same(source, current); b.OnResync("Sim101", snapshot); });
            var buy = Fixtures.Fill("buy", true, 1, 100, 1); var sell = Fixtures.Fill("sell", false, 1, 101, 0);
            fills.Add(buy); boundary.Fill(source, () => closed.AddRange(b.OnAdd(buy)));
            fills.Add(sell); boundary.Fill(source, () => closed.AddRange(b.OnAdd(sell)));
            background.Dequeue()(); while (worker.Count != 0) worker.Dequeue()();
            Assert.Equal(reconnect, refreshed); Assert.Equal("sell", Assert.Single(closed).ExitExecId);
        }
        [Fact]
        public void FreshBuyAppliedBeforeBackgroundSnapshotCanStillCloseAfterResync()
        {
            var b = Builder(); var work = new Queue<Action>(); var background = new Queue<Action>(); var source = new object();
            var boundary = new AccountEventBoundary<object>(work.Enqueue); var buy = Fixtures.Fill("buy", true, 1, 100, 1);
            boundary.Request(source, background.Enqueue, current => Fixtures.Snapshot(new[] { buy }, Fixtures.Position(1)), null,
                (current, snapshot) => b.OnResync("Sim101", snapshot));
            boundary.Fill(source, () => b.OnAdd(buy)); work.Dequeue()(); background.Dequeue()(); work.Dequeue()();
            Assert.False(Assert.Single(b.Open).UnknownStart);
            Assert.Single(b.OnAdd(Fixtures.Fill("sell", false, 1, 101, 0)));
        }
        [Fact]
        public void EagerSnapshotOwnsItsPositionsAndExecutionEvidence()
        {
            var position = Fixtures.Position(1); var evidence = ExecutionEvidence.From(Fixtures.Fill("entry", true, 1, 100, 1));
            var snapshot = new AccountSnapshot(new[] { position }, new[] { evidence });
            position.Net = -5; position.TickSize = 10; evidence.Commission = 10;
            snapshot.Positions[0].Net = 99; snapshot.Positions[0].TickSize = 20; snapshot.Executions[0].Price = 999;
            Assert.Equal(1, snapshot.Positions[0].Net); Assert.Equal(100, snapshot.Executions[0].Price); Assert.Equal(0, snapshot.Executions[0].Commission);
            Assert.Equal(.25m, snapshot.Positions[0].TickSize);
        }
        [Fact]
        public void HistoryChangingDuringPositionCaptureDoesNotSeedUncertainExecutions()
        {
            var buy = Fixtures.Fill("buy", true, 1, 100, 1); var sell = Fixtures.Fill("sell", false, 1, 101, 0); int reads = 0;
            var snapshot = AccountSnapshot.Capture(() => new List<PositionSnapshot>(),
                () => (++reads == 1 ? new Fill[0] : new[] { buy, sell }).Select(ExecutionEvidence.From).ToList());
            Assert.False(snapshot.Consistent);
            var b = Builder(); b.OnResync("Sim101", snapshot);
            b.OnAdd(buy); Assert.Single(b.OnAdd(sell));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CallbackDuringBackgroundCaptureRetriesAndSendsObservedTradeOnce(bool reconnect)
        {
            var files = new MemoryFiles(); var outbox = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, outbox);
            var work = new Queue<Action>(); var background = new Queue<Action>(); var source = new object();
            var boundary = new AccountEventBoundary<object>(work.Enqueue); var buy = Fixtures.Fill("buy", true, 1, 100, 1);
            AccountSnapshot applied = null; int captures = 0; int applications = 0; int refreshes = 0;
            boundary.Request(source, background.Enqueue, current => {
                Assert.Same(source, current);
                if (++captures == 1) boundary.Fill(source, () => worker.Apply(buy));
                return Fixtures.Snapshot(new[] { buy }, Fixtures.Position(1));
            }, reconnect ? (Action)(() => refreshes++) : null,
                (current, snapshot) => { applications++; applied = snapshot; worker.Resync("Sim101", snapshot); });
            background.Dequeue()(); while (background.Count != 0) background.Dequeue()();
            while (work.Count != 0) work.Dequeue()();
            var exit = Fixtures.Fill("end", false, 1, 101, 0); worker.Apply(exit); worker.Apply(exit);
            var trade = Assert.Single(outbox.Peek(20)).Trade;
            Assert.Equal(200, trade.PnlCents); Assert.Equal("end", trade.ExitExecId);
            Assert.Equal(2, captures); Assert.Equal(1, applications); Assert.Equal(reconnect ? 1 : 0, refreshes);
            Assert.True(applied.Consistent); Assert.Empty(worker.Builder.Open);
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FractionalScaledAverageFromNinjaTraderKeepsLiveTrade(bool buy)
        {
            int sign = buy ? 1 : -1; decimal scalePrice = buy ? 100.25m : 99.75m;
            var fills = new[] { Fixtures.Fill("entry", buy, 1, 100, sign), Fixtures.Fill("scale", buy, 2, scalePrice, sign * 3) };
            var files = new MemoryFiles(); var outbox = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, outbox);
            foreach (var fill in fills) worker.Apply(fill);
            decimal ntAverage = (decimal)((100d + 2d * (double)scalePrice) / 3d);
            Assert.NotEqual(Assert.Single(worker.Builder.Open).PositionAveragePrice.Value, ntAverage);
            worker.Resync("Sim101", Fixtures.Snapshot(fills, Fixtures.Position(sign * 3, ntAverage)));
            var exit = Fixtures.Fill("end", !buy, 3, buy ? 101 : 99, 0); worker.Apply(exit); worker.Apply(exit);
            var trade = Assert.Single(outbox.Peek(20)).Trade;
            Assert.Equal(500, trade.PnlCents); Assert.Equal(3, trade.Qty); Assert.Equal(buy ? "long" : "short", trade.Side);
            Assert.Equal(new[] { "entry", "scale" }, trade.EntryExecIds); Assert.Empty(worker.Builder.Open);
        }
        [Fact]
        public void RepeatedCaptureCrossingsYieldWithoutApplyingAnUnstableSnapshotOrRefreshingTwice()
        {
            var files = new MemoryFiles(); var outbox = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, outbox);
            var work = new Queue<Action>(); var background = new Queue<Action>(); var source = new object();
            var boundary = new AccountEventBoundary<object>(work.Enqueue); var fills = new List<Fill>(); var messages = new List<string>();
            int captures = 0; int applications = 0; int refreshes = 0;
            boundary.Request(source, background.Enqueue, current => {
                if (++captures <= 5)
                {
                    var fill = Fixtures.Fill("entry" + captures, true, 1, 100, captures); fills.Add(fill);
                    boundary.Fill(source, () => worker.Apply(fill));
                }
                return Fixtures.Snapshot(fills, Fixtures.Position(fills.Count));
            }, () => refreshes++, (current, snapshot) => { Assert.True(snapshot.Consistent); applications++; worker.Resync("Sim101", snapshot); }, messages.Add);
            background.Dequeue()();
            Assert.Equal(3, captures); Assert.Equal(0, applications); Assert.Equal(0, refreshes); Assert.Single(background);
            while (work.Count != 0) work.Dequeue()();
            Assert.False(Assert.Single(worker.Builder.Open).UnknownStart); Assert.Equal(3, Assert.Single(worker.Builder.Open).Net);
            background.Dequeue()(); while (work.Count != 0) work.Dequeue()();
            Assert.Equal(6, captures); Assert.Equal(1, applications); Assert.Equal(1, refreshes); Assert.Empty(background);
            Assert.Equal(5, messages.Count); Assert.All(messages, x => Assert.Contains("retrying", x));
            worker.Apply(Fixtures.Fill("end", false, 5, 101, 0));
            Assert.Equal(1000, Assert.Single(outbox.Peek(20)).Trade.PnlCents);
        }
        [Fact]
        public void StableCallbackRevisionKeepsLiveTradeWhenSnapshotChangesAndIsLogged()
        {
            var messages = new List<string>(); var b = new TradeBuilder(TimeZoneInfo.Utc, messages.Add);
            var entry = Fixtures.Fill("entry", true, 1, 100, 1); b.OnAdd(entry);
            var updated = Fixtures.Fill("entry", true, 1, 100, 1, 1);
            var work = new Queue<Action>(); var background = new Queue<Action>(); var source = new object();
            var boundary = new AccountEventBoundary<object>(work.Enqueue); int captures = 0; int reads = 0; int applications = 0;
            boundary.Request(source, background.Enqueue, current => {
                captures++;
                return AccountSnapshot.Capture(() => new List<PositionSnapshot> { Fixtures.Position(1) },
                    () => new List<ExecutionEvidence> { ExecutionEvidence.From(++reads == 1 ? entry : updated) });
            }, null, (current, snapshot) => { Assert.False(snapshot.Consistent); applications++; b.OnResync("Sim101", snapshot); });
            background.Dequeue()(); while (work.Count != 0) work.Dequeue()();
            Assert.Equal(1, captures); Assert.Equal(1, applications); Assert.Empty(background);
            Assert.False(Assert.Single(b.Open).UnknownStart);
            Assert.Equal(200, Assert.Single(b.OnAdd(Fixtures.Fill("end", false, 1, 101, 0))).PnlCents);
            Assert.Contains(messages, x => x.Contains("snapshot was inconsistent") && x.Contains("left unchanged"));
            Assert.DoesNotContain(messages, x => x.Contains("Not sent"));
        }
        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        public void FloatNoiseAverageDifferenceKeepsLiveTrade(int direction)
        {
            var entry = Fixtures.Fill("entry", true, 1, 100, 1); var b = Builder(); b.OnAdd(entry);
            b.OnResync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(1, 100 + direction * .000000000001m)));
            Assert.Equal(200, Assert.Single(b.OnAdd(Fixtures.Fill("end", false, 1, 101, 0))).PnlCents);
        }
        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        public void AverageBeyondFloatNoiseToleranceIsWithheldAndLogged(int direction)
        {
            var messages = new List<string>(); var b = new TradeBuilder(TimeZoneInfo.Utc, messages.Add);
            var entry = Fixtures.Fill("entry", true, 1, 100, 1); b.OnAdd(entry);
            b.OnResync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(1, 100 + direction * .0002500001m)));
            Assert.Empty(b.OnAdd(Fixtures.Fill("end", false, 1, 101, 0)));
            Assert.Contains(messages, x => x.Contains("Not sent:") && x.Contains("continuity"));
        }
        [Fact]
        public void PositionAndLatestSignedExecutionMustAgreeInSnapshot()
        {
            var snapshot = AccountSnapshot.Capture(() => new List<PositionSnapshot> { Fixtures.Position(1) },
                () => new List<ExecutionEvidence> { ExecutionEvidence.From(Fixtures.Fill("short", false, 1, 100, -1)) });
            Assert.False(snapshot.Consistent);
        }
        [Fact]
        public void InstrumentAmendmentBreaksOriginalOpenTradeByRawExecutionIdentity()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("entry", true, 1, 100, 1));
            var update = Fixtures.Fill("entry", true, 1, 100, 1, 0, "Sim101", "MNQ 03-27"); update.Op = "Update";
            b.Process(update); Assert.True(Assert.Single(b.Open).Broken);
            Assert.Empty(b.OnAdd(Fixtures.Fill("end", false, 1, 101, 0)));
        }
        [Fact]
        public void LiveTimeAmendmentMakesTradeUnknownAndRestartCannotUseItsOldEntryTime()
        {
            var entry = Fixtures.Fill("entry", true, 1, 100, 1); var b = Builder(); b.OnAdd(entry);
            var update = Fixtures.Fill("entry", true, 1, 100, 1); update.Op = "Update"; update.TimeMs++;
            b.Process(update); Assert.True(Assert.Single(b.Open).Broken);
            Assert.True(Assert.Single(b.Open).UnknownStart);
            var restored = Builder();
            restored.OnResync("Sim101", Fixtures.Snapshot(new[] { update }, Fixtures.Position(1)));
            Assert.Empty(restored.OnAdd(Fixtures.Fill("end", false, 1, 101, 0)));
        }
        [Fact]
        public void PositionAverageUsesRemainingEntryCostAfterPartialExitAndScaleIn()
        {
            var fills = new[] { Fixtures.Fill("entry", true, 2, 100, 2), Fixtures.Fill("partial", false, 1, 101, 1), Fixtures.Fill("scale", true, 1, 104, 2) };
            var b = Builder(); foreach (var fill in fills) b.OnAdd(fill);
            Assert.Equal(102m, Assert.Single(b.Open).PositionAveragePrice);
            b.OnResync("Sim101", Fixtures.Snapshot(fills, Fixtures.Position(2, 102)));
            Assert.False(Assert.Single(b.Open).UnknownStart);
            Assert.Equal(1800, Assert.Single(b.OnAdd(Fixtures.Fill("end", false, 2, 106, 0))).PnlCents);
        }
        [Fact]
        public void CompleteClosedProvenanceIncludesAllRawFillsAndSurvivesClosedQueueRestart()
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, queue); var history = new List<Fill>();
            for (int i = 0; i < 12; i++) { var fill = Fixtures.Fill("entry" + i, true, 1, 100, i + 1); history.Add(fill); worker.Apply(fill); }
            var partial = Fixtures.Fill("partial", false, 1, 101, 11); var end = Fixtures.Fill("end", false, 11, 102, 0);
            history.Add(partial); history.Add(end); worker.Apply(partial); worker.Apply(end);
            var trade = Assert.Single(queue.Peek(20)).Trade; Assert.Equal(10, trade.EntryExecIds.Count); Assert.Equal(14, trade.Provenance.Executions.Count);
            var restartedQueue = new OutboxQueue("queue", files, delegate { }); var restarted = Worker(files, restartedQueue); Assert.Empty(restartedQueue.Peek(20));
            restarted.Resync("Sim101", Fixtures.Snapshot(history)); Assert.Single(restartedQueue.Peek(20));
            var correction = Fixtures.Fill("entry11", true, 1, 100, 12, 1); correction.Op = "Update"; restarted.Apply(correction);
            Assert.Empty(restartedQueue.Peek(20)); Assert.Empty(new OutboxQueue("queue", files, delegate { }).Peek(20));
        }
        [Fact]
        public void UnexplainedExecutionInsideRestoredClosedIntervalInvalidatesIt()
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { }); var fills = Close(Worker(files, queue));
            var resumed = new OutboxQueue("queue", files, delegate { });
            var history = new[] { fills[0], Fixtures.Fill("offline-close", false, 1, 110, 0), Fixtures.Fill("offline-open", true, 1, 120, 1), fills[1] };
            resumed.ValidateRestored("Sim101", Fixtures.Snapshot(history)); Assert.Empty(resumed.Peek(20)); Assert.Equal(0, resumed.Count);
        }
        [Theory]
        [InlineData("queue")]
        [InlineData("queue.invalidations")]
        public void FailedCorrectionWriteCannotReviveRowAfterRestart(string failedPath)
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, queue); var history = Close(worker);
            files.FailWrites = path => path == failedPath;
            var correction = Fixtures.Fill("entry", true, 1, 100, 1, 1); correction.Op = "Update"; worker.Apply(correction);
            Assert.Empty(queue.Peek(20));
            files.FailWrites = null; var resumedQueue = new OutboxQueue("queue", files, delegate { }); var resumed = Worker(files, resumedQueue);
            resumed.Resync("Sim101", Fixtures.Snapshot(history)); resumed.RetryPersistence();
            Assert.Empty(resumedQueue.Peek(20)); Assert.Equal(0, resumedQueue.Count);
            Assert.Contains("end", files.Read("queue.invalidations"));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AllWritesFailingDuringCorrectionCannotUploadStaleRecordAfterRestart(bool pendingHandoff)
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles { FailWrites = pendingHandoff ? (Func<string, bool>)(path => path == "queue") : null };
                var queue = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, queue); var history = Close(worker);
                files.FailWrites = path => true;
                var correction = Fixtures.Fill("entry", true, 1, 100, 1, 1); correction.Op = "Update"; worker.Apply(correction); Assert.Empty(queue.Peek(20));
                files.FailWrites = null; var restartedQueue = new OutboxQueue("queue", files, delegate { }); var restarted = Worker(files, restartedQueue);
                restarted.RetryPersistence(); Assert.Empty(restartedQueue.Peek(20));
                var api = new FakeApi(); var session = new SessionCoordinator(Fixtures.Settings(files, true), restartedQueue, api, new EventLog(folder.Path));
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None); await session.ProcessAsync(Fixtures.Time + 300000, CancellationToken.None);
                Assert.Equal(0, api.Sends);
                history[0] = correction; restarted.Resync("Sim101", Fixtures.Snapshot(history));
                await session.ProcessAsync(Fixtures.Time + 600000, CancellationToken.None); Assert.Equal(0, api.Sends); Assert.Equal(0, restartedQueue.Count);
                Assert.Empty(new OpenTradesFile("open", files, delegate { }).Load().PendingClosed);
            }
        }
        [Fact]
        public void RestoredLegacyRowCannotBeMappedOrSentAfterACorrection()
        {
            var files = new MemoryFiles(); var legacy = new OutboxLine { Key = "Sim101|exit", Trade = Fixtures.Trade() };
            files.Data["queue"] = Json.Write(legacy) + "\n";
            var queue = new OutboxQueue("queue", files, delegate { }); var worker = Worker(files, queue);
            Assert.Empty(queue.Peek(20)); var correction = Fixtures.Fill("unmapped", true, 1, 100, 1, 1, "OtherAccount"); correction.Op = "Update";
            worker.Apply(correction); Assert.Empty(queue.Peek(20)); Assert.Equal(0, queue.Count);
        }
        [Fact]
        public void RestoredMissingHistoryIsInvalidatedRatherThanAssumedSafe()
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { }); Close(Worker(files, queue));
            var restored = new OutboxQueue("queue", files, delegate { }); Assert.Empty(restored.Peek(20));
            restored.ValidateRestored("Sim101", Fixtures.Snapshot(new Fill[0])); Assert.Empty(restored.Peek(20)); Assert.Equal(0, restored.Count);
        }
        [Fact]
        public void EvidenceCapacityWithholdsTradeAndBoundsItsLocalFillStorage()
        {
            var messages = new List<string>(); var b = new TradeBuilder(TimeZoneInfo.Utc, messages.Add);
            var entry = Fixtures.Fill("entry", true, 1, 100, 1); b.OnAdd(entry);
            var trade = Assert.Single(b.Open);
            while (trade.Fills.Count < ExecutionEvidence.MaximumCount) trade.Fills.Add(trade.Fills[0]);
            b.OnAdd(Fixtures.Fill("scale", true, 1, 100, 2));
            Assert.True(trade.Broken); Assert.Equal(ExecutionEvidence.MaximumCount, trade.Fills.Count);
            Assert.Contains(messages, text => text.Contains("execution-evidence limit"));
            Assert.Empty(b.OnAdd(Fixtures.Fill("end", false, 2, 101, 0)));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InFlightSendAndCorrectionSerializeAndFailedSendCannotRetryInvalidatedTrade(bool succeeds)
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles(); var messages = new List<string>(); var queue = new OutboxQueue("queue", files, messages.Add); var worker = Worker(files, queue);
                var api = new FakeApi(); var session = new SessionCoordinator(Fixtures.Settings(files, true), queue, api, new EventLog(folder.Path));
                queue.DurableAdded += trade => session.NotifyQueued(); await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                Close(worker); var response = new TaskCompletionSource<ApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                api.SendOverride = cancellation => response.Task;
                var send = session.ProcessAsync(Fixtures.Time, CancellationToken.None); Assert.Equal(1, api.Sends);
                var attempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var correction = Fixtures.Fill("entry", true, 1, 100, 1, 1); correction.Op = "Update";
                var mutate = Task.Run(() => { attempted.SetResult(true); worker.Apply(correction); });
                await attempted.Task; Assert.False(mutate.IsCompleted);
                response.SetResult(succeeds ? Fixtures.Sent(1) : new ApiResult { Kind = ApiKind.Offline });
                await send; await mutate; Assert.Empty(queue.Peek(20));
                api.SendOverride = null; session.TryNow(); await session.ProcessAsync(Fixtures.Time + 15000, CancellationToken.None);
                Assert.Equal(1, api.Sends); Assert.Equal(0, queue.Count);
                Assert.Contains(messages, text => text.Contains(succeeds ? "Already dispatched" : "invalidated"));
            }
        }
    }
}
