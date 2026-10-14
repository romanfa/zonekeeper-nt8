using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class PersistenceTests
    {
        [Fact]
        public void T14_AddReloadDedupeAndRemoveOldest500()
        {
            var files = new MemoryFiles(); var messages = new List<string>();
            var queue = new OutboxQueue("queue", files, messages.Add);
            for (int i = 0; i < 501; i++) Assert.True(queue.Add(Fixtures.Trade("exit" + i)));
            Assert.Equal(500, queue.Count); Assert.Contains(UiText.QueueFull, messages);
            Assert.Equal("exit1", queue.Peek(1)[0].Trade.ExitExecId);
            Assert.True(queue.Add(Fixtures.Trade("exit500"))); Assert.Equal(500, queue.Count);
            var reloaded = new OutboxQueue("queue", files, messages.Add);
            Fixtures.VerifyPersisted(reloaded, files);
            Assert.Equal(500, reloaded.Count); Assert.True(reloaded.Remove(reloaded.Peek(20).Select(x => x.Identity)));
            Assert.Equal(480, new OutboxQueue("queue", files, messages.Add).Count);
        }
        [Fact]
        public void T14_BrokenTailSkippedAndNextWriteRepairsIt()
        {
            var files = new MemoryFiles(); var log = new List<string>();
            var q = new OutboxQueue("queue", files, log.Add); q.Add(Fixtures.Trade("good")); files.Data["queue"] += "{broken";
            var loaded = new OutboxQueue("queue", files, log.Add); Assert.Equal(1, loaded.Count); Assert.Contains(log, x => x.Contains("1 damaged"));
            loaded.Add(Fixtures.Trade("next")); Assert.Equal(2, new OutboxQueue("queue", files, log.Add).Count);
        }
        [Fact]
        public void QueueDedupeSeparatesAccountsAndDelimiterCollisions()
        {
            var q = new OutboxQueue("queue", new MemoryFiles(), delegate { });
            q.Add(Fixtures.Trade("e", "Sim101")); q.Add(Fixtures.Trade("e", "Sim102")); q.Add(Fixtures.Trade("e", "Sim101"));
            q.Add(Fixtures.Trade("c", "a|b")); q.Add(Fixtures.Trade("b|c", "a"));
            Assert.Equal(4, q.Count);
            q.Remove(new[] { Helpers.PairKey("a|b", "c") }); Assert.Equal(3, q.Count);
        }
        [Fact]
        public void FailedEnqueueRetainsRecordAndDoesNotPublishOrSendBeforeDurable()
        {
            var files = new MemoryFiles { FailWrites = x => x == "queue" };
            var q = new OutboxQueue("queue", files, delegate { }); int closed = 0;
            q.DurableAdded += delegate { closed++; };
            Assert.False(q.Add(Fixtures.Trade())); Assert.Equal(1, q.Count); Assert.Empty(q.Peek(20)); Assert.Equal(0, closed);
            files.FailWrites = null; Assert.True(q.TryFlush()); Assert.Equal(1, closed);
            Assert.True(q.Add(Fixtures.Trade())); Assert.Equal(1, closed);
            var restored = new OutboxQueue("queue", files, delegate { }); Assert.Empty(restored.Peek(20));
            Fixtures.VerifyPersisted(restored, files); Assert.Single(restored.Peek(20));
        }
        [Fact]
        public void FailedBatchRemovalPreservesMemoryAndDiskForRetry()
        {
            var files = new MemoryFiles(); var q = new OutboxQueue("queue", files, delegate { }); q.Add(Fixtures.Trade());
            files.FailWrites = x => true;
            Assert.False(q.Remove(new[] { Fixtures.Trade().Key })); Assert.Equal(1, q.Count);
            Assert.Equal(1, new OutboxQueue("queue", files, delegate { }).Count);
            files.FailWrites = null; Assert.True(q.Remove(new[] { Fixtures.Trade().Key })); Assert.Equal(0, new OutboxQueue("queue", files, delegate { }).Count);
        }
        [Fact]
        public void ClosedHandoffRecoversAfterRestartIfOutboxWriteFailed()
        {
            var files = new MemoryFiles { FailWrites = x => x == "queue" };
            var queue = new OutboxQueue("queue", files, delegate { });
            var file = new OpenTradesFile("open", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), file, queue);
            worker.Apply(Fixtures.Fill("a", true, 1, 100, 1)); worker.Apply(Fixtures.Fill("b", false, 1, 101, 0));
            var checkpoint = file.Load(); Assert.Empty(checkpoint.OpenTrades); Assert.Single(checkpoint.PendingClosed); Assert.Empty(queue.Peek(20));
            files.FailWrites = null;
            var restartedQueue = new OutboxQueue("queue", files, delegate { });
            var restarted = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), file, restartedQueue);
            Assert.True(restarted.RetryPersistence()); Assert.Empty(restartedQueue.Peek(20));
            restarted.Resync("Sim101", Fixtures.Snapshot(new[] { Fixtures.Fill("a", true, 1, 100, 1), Fixtures.Fill("b", false, 1, 101, 0) }));
            Assert.Single(restartedQueue.Peek(20)); Assert.Empty(file.Load().PendingClosed);
        }
        [Fact]
        public void SimultaneousFileFailuresRetainPendingInMemoryUntilRecovery()
        {
            var files = new MemoryFiles(); var q = new OutboxQueue("queue", files, delegate { }); var file = new OpenTradesFile("open", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), file, q);
            worker.Apply(Fixtures.Fill("a", true, 1, 100, 1)); files.FailWrites = x => true;
            worker.Apply(Fixtures.Fill("b", false, 1, 101, 0)); Assert.Single(worker.Snapshot().PendingClosed); Assert.Empty(q.Peek(20));
            files.FailWrites = null; Assert.True(worker.RetryPersistence()); Assert.Single(q.Peek(20)); Assert.Empty(file.Load().PendingClosed);
        }
        [Fact]
        public void T15_AtomicOpenStateRetainsAllFillContributions()
        {
            using (var folder = new TestFolder())
            {
                var file = new OpenTradesFile(Path.Combine(folder.Path, "open-trades.json"), new AtomicFileStore(), delegate { });
                var b = new TradeBuilder(TimeZoneInfo.Utc, delegate { }); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2, 1));
                b.OnAdd(Fixtures.Fill("b", false, 1, 101, 1, .5m));
                Assert.True(file.Save(new OpenTradesState { OpenTrades = b.Open }));
                var restored = Assert.Single(file.Load().OpenTrades); Assert.Equal(2, restored.Fills.Count); Assert.Equal(1.5m, restored.Commission);
                Assert.False(File.Exists(Path.Combine(folder.Path, "open-trades.json.tmp")));
                b.OnReset("Sim101"); Assert.True(file.Save(new OpenTradesState { OpenTrades = b.Open })); Assert.Empty(file.Load().OpenTrades);
            }
        }
        [Fact]
        public void CorruptOpenFileFallsBackToUnknownStartsWithLog()
        {
            var files = new MemoryFiles(); files.Data["open"] = "{broken"; var log = new List<string>();
            var file = new OpenTradesFile("open", files, log.Add); Assert.Empty(file.Load().OpenTrades);
            Assert.Contains(log, x => x.Contains("unknown starts"));
        }
        [Fact]
        public void DisablingMidTradeDoesNotPairAcrossDisabledInterval()
        {
            var files = new MemoryFiles(); var q = new OutboxQueue("queue", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), new OpenTradesFile("open", files, delegate { }), q);
            worker.Apply(Fixtures.Fill("a", true, 1, 100, 1)); worker.Disable("Sim101");
            worker.Resync("Sim101", new[] { new PositionSnapshot { Instrument = "MNQ 12-26", Net = 1, AveragePrice = 100, PointValue = 2, Currency = "USD" } }, new[] { "a" });
            worker.Apply(Fixtures.Fill("b", false, 1, 101, 0)); Assert.Equal(0, q.Count);
        }
        [Fact]
        public void DurableCallbackRunsOutsideQueueLockAndSnapshotCannotDeadlock()
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles(); var q = new OutboxQueue("queue", files, delegate { });
                var session = new SessionCoordinator(Fixtures.Settings(files), q, new FakeApi(), new EventLog(folder.Path)); bool completed = false;
                q.DurableAdded += delegate {
                    var read = Task.Run(() => session.Snapshot());
                    completed = read.Wait(TimeSpan.FromSeconds(2));
                    session.NotifyQueued();
                };
                q.Add(Fixtures.Trade()); Assert.True(completed);
            }
        }
        [Fact]
        public void IdleFillCoordinatorDoesNotRewriteFilesWithoutChanges()
        {
            var files = new MemoryFiles(); var q = new OutboxQueue("queue", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), new OpenTradesFile("open", files, delegate { }), q);
            worker.RetryPersistence(); int writes = files.Writes;
            worker.RetryPersistence(); Assert.Equal(writes, files.Writes);
        }
        [Fact]
        public void ShutdownFrozenIdentitiesDrainCurrentClosingFillAndRejectSupersededObject()
        {
            var oldAccount = new object(); var newAccount = new object(); var identities = new AccountIdentityMap<object>();
            identities.Update(new[] { new KeyValuePair<string, object>("Sim101", oldAccount) });
            identities.Update(new[] { new KeyValuePair<string, object>("Sim101", newAccount) }); identities.Freeze();
            identities.Update(new KeyValuePair<string, object>[0]);
            Assert.False(identities.IsCurrent("Sim101", oldAccount)); Assert.True(identities.IsCurrent("Sim101", newAccount));
            var files = new MemoryFiles(); var queue = new OutboxQueue("queue", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), new OpenTradesFile("open", files, delegate { }), queue);
            worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
            if (identities.IsCurrent("Sim101", oldAccount)) worker.Apply(Fixtures.Fill("old-exit", false, 1, 100, 0));
            if (identities.IsCurrent("Sim101", newAccount)) worker.Apply(Fixtures.Fill("exit", false, 1, 101, 0));
            Assert.Equal("exit", Assert.Single(queue.Peek(1)).Trade.ExitExecId);
        }
        [Fact]
        public void SavedDisabledAccountIsPrunedEvenIfDisableWorkNeverQueuedBeforeStop()
        {
            var files = new MemoryFiles(); var settings = Fixtures.Settings(files); var queue = new OutboxQueue("queue", files, delegate { }); var file = new OpenTradesFile("open", files, delegate { });
            var worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), file, queue);
            worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); settings.SetAccount("Sim101", false);
            var restored = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, delegate { }), file, queue);
            restored.PruneDisabledAccounts(Fixtures.Settings(files).IsEnabled);
            Assert.Empty(restored.Builder.Open); Assert.Empty(file.Load().OpenTrades);
        }
    }
}
