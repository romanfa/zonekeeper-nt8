using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class BlockerRegressionTests
    {
        private static TradeBuilder Builder() { return new TradeBuilder(TimeZoneInfo.Utc, delegate { }); }
        private static PositionSnapshot Long(decimal price = 100)
        { return new PositionSnapshot { Instrument = "MNQ 12-26", Net = 1, AveragePrice = price, PointValue = 2, Currency = "USD" }; }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ResyncBoundaryPreservesBuyAndSellQueuedAfterStartupOrReconnectRequest(bool reconnect)
        {
            var builder = Builder(); var queued = new Queue<Action>(); var ids = new List<string>();
            var source = new object(); var refreshed = false; var closed = new List<TradeRecord>();
            ResyncBoundary.Enqueue(source, queued.Enqueue,
                current => { Assert.Same(source, current); return new AccountSnapshot(new PositionSnapshot[0], ids.Select(id => new ExecutionEvidence { ExecId = id })); },
                reconnect ? (Action)(() => refreshed = true) : null,
                (current, snapshot) => { Assert.Same(source, current); builder.OnResync("Sim101", snapshot.Positions, snapshot.ExecutionIds); });
            ids.Add("buy"); queued.Enqueue(() => closed.AddRange(builder.OnAdd(Fixtures.Fill("buy", true, 1, 100, 1))));
            ids.Add("sell"); queued.Enqueue(() => closed.AddRange(builder.OnAdd(Fixtures.Fill("sell", false, 1, 101, 0))));
            while (queued.Count != 0) queued.Dequeue()();
            Assert.Equal(reconnect, refreshed); Assert.Equal("sell", Assert.Single(closed).ExitExecId);
        }
        [Fact]
        public void SignedMismatchRefusesFalseReversalAndItsReplacement()
        {
            var builder = Builder(); builder.OnAdd(Fixtures.Fill("entry", true, 1, 100, 1));
            Assert.Empty(builder.OnAdd(Fixtures.Fill("false-flip", false, 2, 110, 1)));
            Assert.True(Assert.Single(builder.Open).Broken);
            Assert.Empty(builder.OnAdd(Fixtures.Fill("end", true, 1, 105, 0)));
        }
        [Fact]
        public void OppositeSignedEntryDoesNotProduceASendableTrade()
        {
            var builder = Builder(); builder.OnAdd(Fixtures.Fill("entry", false, 1, 100, 1));
            Assert.True(Assert.Single(builder.Open).Broken);
            Assert.Empty(builder.OnAdd(Fixtures.Fill("end", true, 1, 99, 0)));
        }
        [Theory]
        [InlineData(120)]
        [InlineData(100)]
        public void SameSizeDisconnectedCloseAndReopenCannotResumeOldEntry(int price)
        {
            var builder = Builder(); builder.OnAdd(Fixtures.Fill("entry", true, 1, 100, 1));
            var restored = Builder(); restored.Restore(Json.Read<List<OpenTrade>>(Json.Write(builder.Open)));
            restored.OnResync("Sim101", new[] { Long(price) }, new[] { "entry", "offline-close", "offline-open" });
            Assert.True(Assert.Single(restored.Open).UnknownStart);
            Assert.Empty(restored.OnAdd(Fixtures.Fill("end", false, 1, 125, 0)));
        }
        [Fact]
        public void MissingExecutionHistoryCannotProveSameTrade()
        {
            var builder = Builder(); builder.OnAdd(Fixtures.Fill("entry", true, 1, 100, 1));
            var restored = Builder(); restored.Restore(Json.Read<List<OpenTrade>>(Json.Write(builder.Open)));
            restored.OnResync("Sim101", new[] { Long() }, new string[0]);
            Assert.True(Assert.Single(restored.Open).UnknownStart);
        }
        [Theory]
        [InlineData("Update", "entry")]
        [InlineData("Remove", "partial")]
        [InlineData("Update", "end")]
        public void ClosedCorrectionsInvalidateAllRawFillContributions(string operation, string id)
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { });
            var worker = new FillCoordinator(Builder(), new OpenTradesFile("open", files, delegate { }), queue);
            worker.Apply(Fixtures.Fill("entry", true, 2, 100, 2)); worker.Apply(Fixtures.Fill("partial", false, 1, 101, 1));
            worker.Apply(Fixtures.Fill("end", false, 1, 102, 0)); Assert.Single(queue.Peek(20));
            var correction = Fixtures.Fill(id, id == "entry", id == "entry" ? 2 : 1, id == "entry" ? 100 : id == "partial" ? 101 : 102, 0, 1);
            correction.Op = operation; worker.Apply(correction);
            Assert.Empty(queue.Peek(20)); Assert.Empty(worker.Snapshot().PendingClosed);
        }
        [Fact]
        public void SharedReversalCorrectionInvalidatesBothQueuedTrades()
        {
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { });
            var worker = new FillCoordinator(Builder(), new OpenTradesFile("open", files, delegate { }), queue);
            worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); worker.Apply(Fixtures.Fill("flip", false, 2, 110, -1));
            worker.Apply(Fixtures.Fill("end", true, 1, 105, 0)); Assert.Equal(2, queue.Peek(20).Count);
            var correction = Fixtures.Fill("flip", false, 2, 110, 0); correction.Op = "Remove"; worker.Apply(correction);
            Assert.Empty(queue.Peek(20));
        }
        [Fact]
        public void CorrectionInvalidatesFailedPendingHandoff()
        {
            var files = new MemoryFiles { FailWrites = path => path == "queue" };
            var queue = new OutboxQueue("queue", files, delegate { }); var file = new OpenTradesFile("open", files, delegate { });
            var worker = new FillCoordinator(Builder(), file, queue);
            worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); worker.Apply(Fixtures.Fill("end", false, 1, 101, 0));
            Assert.Single(worker.Snapshot().PendingClosed);
            var correction = Fixtures.Fill("entry", true, 1, 100, 0, 1); correction.Op = "Update"; worker.Apply(correction);
            files.FailWrites = null; worker.RetryPersistence();
            Assert.Empty(queue.Peek(20)); Assert.Empty(file.Load().PendingClosed);
        }
    }
}
