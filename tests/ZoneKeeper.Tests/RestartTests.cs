using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class RestartTests
    {
        private sealed class DispatchApi : IAddonApi
        {
            public readonly FakeApi Inner = new FakeApi();
            public readonly List<TradeRecord> Sent = new List<TradeRecord>();
            public Task<ApiResult> ClaimAsync(string code, string name, CancellationToken cancellation)
            { return Inner.ClaimAsync(code, name, cancellation); }
            public Task<ApiResult> PingAsync(string token, CancellationToken cancellation)
            { return Inner.PingAsync(token, cancellation); }
            public Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation)
            { Sent.AddRange(trades); return Inner.SendTradesAsync(token, trades, cancellation); }
            public Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation)
            { return Inner.DisconnectAsync(token, cancellation); }
            public Task<ApiResult> HandoffAsync(string token, CancellationToken cancellation)
            { return Inner.HandoffAsync(token, cancellation); }
            public void Dispose() { Inner.Dispose(); }
        }
        private sealed class Harness
        {
            public readonly MemoryFiles Files = new MemoryFiles();
            public readonly List<string> Messages = new List<string>();
            public OutboxQueue Queue;
            public FillCoordinator Worker;
            public Harness() { Restart(); }
            public void Restart()
            {
                Messages.Clear(); Queue = new OutboxQueue("queue", Files, Messages.Add);
                Worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, Messages.Add), new OpenTradesFile("open", Files, Messages.Add), Queue);
            }
            public void LegacyOpen(int quantity)
            {
                var old = new TradeBuilder(TimeZoneInfo.Utc, delegate { });
                old.OnAdd(Fixtures.Fill("old-entry", true, quantity, 100, quantity));
                Files.Data["open"] = Json.Write(new OpenTradesState { OpenTrades = old.Open }); Restart();
                Assert.Empty(Worker.Builder.Open); Assert.Equal(0, Worker.Builder.SeenCount);
            }
            public void FreshRoundTrip(string suffix)
            {
                Worker.Apply(Fixtures.Fill("entry-" + suffix, true, 1, 100, 1));
                Worker.Apply(Fixtures.Fill("exit-" + suffix, false, 1, 101, 0));
                var trade = Queue.Peek(20).Single(x => x.Trade.ExitExecId == "exit-" + suffix).Trade;
                Assert.Equal(200, trade.PnlCents); Assert.Equal(new[] { "entry-" + suffix }, trade.EntryExecIds);
                Assert.DoesNotContain(Worker.Builder.Open, x => x.Account == "Sim101" && x.Instrument == "MNQ 12-26");
            }
        }
        private static ExecutionEvidence Amend(ExecutionEvidence original, string changed)
        {
            var amended = original.Copy();
            switch (changed)
            {
                case "commission": amended.Commission += 1; break;
                case "price": amended.Price++; break;
                case "quantity": amended.Qty++; break;
                case "side": amended.IsBuy = !amended.IsBuy; break;
                case "time": amended.TimeMs++; break;
                case "instrument": amended.Instrument = "MNQ 03-27"; break;
                case "position-after": amended.PositionAfter++; break;
                case "point-value": amended.PointValue++; break;
                case "currency": amended.Currency = "CAD"; break;
                case "name": amended.Name = "Amended name"; break;
                default: throw new ArgumentException("Unknown amendment", "changed");
            }
            return amended;
        }
        private static void AssertReconnectCommission(Harness h, Fill amended, PositionSnapshot position, decimal expected)
        {
            var snapshot = Fixtures.Snapshot(new[] { amended }, position);
            var live = Assert.Single(h.Worker.Builder.Open);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                h.Worker.Resync(amended.Account, snapshot);
                Assert.True(live.Sendable); Assert.Equal(expected, live.Commission);
                var applied = live.Fills.Single(x => x.ExecId == amended.ExecId);
                Assert.Equal(amended.Commission, applied.SourceCommission);
                Assert.True(ExecutionEvidence.From(amended).Matches(applied.Evidence));
                Assert.Equal(amended.Commission, Assert.Single(snapshot.Executions).Commission);
            }
            amended.Op = "Update"; h.Worker.Apply(amended);
            h.Worker.Resync(amended.Account, snapshot);
            Assert.True(live.Sendable); Assert.Equal(expected, live.Commission);
            Assert.Equal(amended.Commission, live.Fills.Single(x => x.ExecId == amended.ExecId).SourceCommission);
        }
        [Theory]
        [InlineData(2000)]
        [InlineData(1)]
        [InlineData(3)]
        public void Case1_LegacyOpenIsNeverRestoredAndClosingItLogsOnlyOnce(int quantity)
        {
            var h = new Harness(); h.LegacyOpen(quantity);
            var old = Fixtures.Fill("old-entry", true, quantity, 100, quantity);
            for (int restart = 0; restart < 2; restart++)
            {
                if (restart != 0) h.Restart();
                Assert.Empty(h.Worker.Builder.Open); Assert.Equal(0, h.Worker.Builder.SeenCount);
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { old }, Fixtures.Position(quantity)));
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { old }, Fixtures.Position(quantity)));
                Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart); Assert.Equal(0, h.Worker.Builder.SeenCount);
                h.Worker.Apply(Fixtures.Fill("end" + restart, false, quantity, 101, 0));
                Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Equal(0, h.Queue.Count);
                var message = Assert.Single(h.Messages);
                Assert.Equal("Position in MNQ 12-26 was open before ZoneKeeper started. It is not sent. Add it later by CSV.", message);
                Assert.Empty(new OpenTradesFile("open", h.Files, h.Messages.Add).Load().OpenTrades);
            }
        }
        [Fact]
        public void Case2_StaleNonflatHistoryCannotBlockFreshTradesAcrossTwoRestarts()
        {
            var h = new Harness(); h.LegacyOpen(3); var old = Fixtures.Fill("old-entry", true, 3, 100, 3);
            var stale = AccountSnapshot.Capture(() => new List<PositionSnapshot>(),
                () => new List<ExecutionEvidence> { ExecutionEvidence.From(old) });
            Assert.False(stale.Consistent);
            for (int restart = 0; restart < 2; restart++)
            {
                if (restart != 0) h.Restart();
                Assert.Empty(h.Worker.Builder.Open); Assert.Equal(0, h.Worker.Builder.SeenCount);
                h.Worker.Resync("Sim101", stale); Assert.Empty(h.Worker.Builder.Open);
                h.FreshRoundTrip("restart" + restart);
                Assert.Empty(h.Worker.Snapshot().OpenTrades);
            }
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case3_AddingToStartupPositionDoesNotSendButNextTradeFromFlatDoes(bool buy)
        {
            int sign = buy ? 1 : -1; var h = new Harness();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(sign * 3)));
            h.Worker.Apply(Fixtures.Fill("add", buy, 1, 100, sign * 4));
            Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart);
            h.Worker.Apply(Fixtures.Fill("end", !buy, 4, 101, 0));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Single(h.Messages);
            h.FreshRoundTrip("after-unknown");
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case4_UnknownReversalStartsOnlyTheObservedRemainderWithSplitCommission(bool startupLong)
        {
            int sign = startupLong ? 1 : -1; var h = new Harness();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(sign * 2)));
            var flip = Fixtures.Fill("raw reversal/id", !startupLong, 5, startupLong ? 110 : 90, -sign * 3, 5);
            flip.TimeMs += 60000; flip.ArrivedMs = flip.TimeMs;
            h.Worker.Apply(flip); Assert.Empty(h.Queue.Peek(20));
            var live = Assert.Single(h.Worker.Builder.Open);
            Assert.True(live.Sendable); Assert.Equal(-sign * 3, live.Net); Assert.Equal(3, live.EntryQty);
            Assert.Equal(3m, live.Commission); Assert.Equal(flip.TimeMs, live.EntryTime);
            Assert.Equal(Helpers.SafeId(flip.ExecId), Assert.Single(live.EntryExecIds));
            var applied = Assert.Single(live.Fills); Assert.Equal(flip.ExecId, applied.ExecId);
            Assert.Equal(5, applied.SourceQty); Assert.Equal(3, applied.AppliedQty); Assert.Equal(5m, applied.SourceCommission);
            var end = Fixtures.Fill("end", startupLong, 3, startupLong ? 105 : 95, 0, 1); end.TimeMs = flip.TimeMs + 60000; end.ArrivedMs = end.TimeMs;
            h.Worker.Apply(end); var trade = Assert.Single(h.Queue.Peek(20)).Trade;
            Assert.Equal(startupLong ? "short" : "long", trade.Side); Assert.Equal(3, trade.Qty);
            Assert.Equal(400, trade.CommissionCents); Assert.Equal(2600, trade.PnlCents); Assert.Equal(flip.TimeMs, trade.EntryTime);
            Assert.Equal(new[] { Helpers.SafeId(flip.ExecId) }, trade.EntryExecIds); Assert.Empty(h.Worker.Builder.Open);
            Assert.Single(h.Messages);
        }
        [Fact]
        public void Case5_FillWithoutStartupSnapshotCreatesOneUnknownLogThenNextTradeCanSend()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("partial", false, 1, 101, 2));
            var unknown = Assert.Single(h.Worker.Builder.Open); Assert.True(unknown.UnknownStart); Assert.Equal(2, unknown.Net);
            Assert.Single(h.Messages); h.Worker.Apply(Fixtures.Fill("end", false, 2, 102, 0));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Single(h.Messages);
            h.FreshRoundTrip("after-lazy-unknown");
        }
        [Fact]
        public void Case6_OldStartupReplayMovesSilentMarkerAndNextLiveUnknownCloseLogsOnce()
        {
            var h = new Harness();
            var replay = new[] { Fixtures.Fill("nine", true, 1, 100, 1), Fixtures.Fill("nine-thirty", false, 1, 101, 0), Fixtures.Fill("nine-forty", true, 2, 102, 2) };
            for (int i = 0; i < replay.Length; i++)
            { replay[i].TimeMs = Fixtures.Time - 7200000 + (i == 0 ? 0 : i == 1 ? 1800000 : 2400000); h.Worker.Apply(replay[i]); }
            Assert.Empty(h.Messages); Assert.Empty(h.Queue.Peek(20));
            var unknown = Assert.Single(h.Worker.Builder.Open); Assert.True(unknown.UnknownStart); Assert.Equal(2, unknown.Net); Assert.False(unknown.Sendable);
            Assert.Empty(unknown.Fills); Assert.Empty(unknown.EntryExecIds);
            h.Worker.Apply(Fixtures.Fill("live-end", false, 2, 103, 0));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20));
            Assert.Contains("It is not sent", Assert.Single(h.Messages)); h.FreshRoundTrip("after-replay");
            Assert.Single(h.Messages);
        }
        [Fact]
        public void Case6_OldFillAlsoStopsAnExistingLiveTradeWithoutLoggingUntilNextLiveFill()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
            var old = Fixtures.Fill("old-add", true, 1, 100, 2); old.ArrivedMs = old.TimeMs + 900001;
            h.Worker.Apply(old); Assert.Empty(h.Messages); Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart);
            h.Worker.Apply(Fixtures.Fill("end", false, 2, 101, 0));
            Assert.Empty(h.Queue.Peek(20)); Assert.Empty(h.Worker.Builder.Open); Assert.Single(h.Messages);
        }
        [Fact]
        public void Case7_MissedFillMakesCurrentPositionUnknownAndItsCloseCannotSend()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
            h.Worker.Apply(Fixtures.Fill("partial", false, 2, 101, 1));
            var unknown = Assert.Single(h.Worker.Builder.Open); Assert.True(unknown.UnknownStart); Assert.Equal(1, unknown.Net);
            Assert.Contains("missed a fill", Assert.Single(h.Messages)); Assert.Empty(h.Queue.Peek(20));
            h.Worker.Apply(Fixtures.Fill("end", false, 1, 102, 0));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Single(h.Messages);
        }
        [Fact]
        public void Case7_MissedFillCrossingZeroStartsASendableRemainder()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
            h.Worker.Apply(Fixtures.Fill("flip", false, 3, 101, -1, 3));
            Assert.Empty(h.Queue.Peek(20)); var next = Assert.Single(h.Worker.Builder.Open);
            Assert.True(next.Sendable); Assert.Equal(-1, next.Net); Assert.Equal(1m, next.Commission); Assert.Equal(new[] { "flip" }, next.EntryExecIds);
            h.Worker.Apply(Fixtures.Fill("end", true, 1, 100, 0, .5m));
            var trade = Assert.Single(h.Queue.Peek(20)).Trade; Assert.Equal("short", trade.Side); Assert.Equal(50, trade.PnlCents);
            Assert.Equal(150, trade.CommissionCents); Assert.Equal(new[] { "flip" }, trade.EntryExecIds); Assert.Single(h.Messages);
        }
        [Theory]
        [InlineData(3, "100.083333333333")]
        [InlineData(2000, "100.000125")]
        public void Case8_ReconnectRejectsBothReviewerChangedAverages(int quantity, string price)
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", true, quantity, 100, quantity); h.Worker.Apply(entry);
            decimal average = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(quantity, average)));
            Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart); Assert.Single(h.Messages);
            h.Worker.Apply(Fixtures.Fill("end", false, quantity, 101, 0));
            Assert.Empty(h.Queue.Peek(20)); Assert.Empty(h.Worker.Builder.Open); Assert.Single(h.Messages);
        }
        [Fact]
        public void Case8_ReconnectAllowsFractionalFloatNoiseWithoutNeedingExecutionProof()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); h.Worker.Apply(Fixtures.Fill("scale", true, 2, 100.25m, 3));
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(3, 100.16666666666667m)));
            Assert.True(Assert.Single(h.Worker.Builder.Open).Sendable); Assert.Empty(h.Messages);
            h.Worker.Apply(Fixtures.Fill("end", false, 3, 101, 0));
            Assert.Equal(500, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents); Assert.Empty(h.Worker.Builder.Open);
        }
        [Theory]
        [InlineData("0", "0.000000001")]
        [InlineData("100", "0.000000001")]
        [InlineData("100000", "0.00000001")]
        public void Case8_ReconnectFloatNoiseToleranceIsPriceBasedAndRejectsAnyLargerDifference(string priceText, string toleranceText)
        {
            decimal price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
            decimal tolerance = decimal.Parse(toleranceText, System.Globalization.CultureInfo.InvariantCulture);
            foreach (int direction in new[] { -1, 1 }) foreach (bool outside in new[] { false, true })
            {
                var h = new Harness(); var entry = Fixtures.Fill("entry", true, 1, price, 1); h.Worker.Apply(entry);
                var position = Fixtures.Position(1, price + direction * (tolerance + (outside ? .000000000001m : 0))); position.TickSize = 0;
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], position));
                Assert.Equal(outside, Assert.Single(h.Worker.Builder.Open).UnknownStart);
                h.Worker.Apply(Fixtures.Fill("end", false, 1, price + 1, 0));
                if (outside) Assert.Empty(h.Queue.Peek(20)); else Assert.Equal(200, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents);
            }
        }
        [Fact]
        public void Case9_ReconnectWithNoNtPositionDropsLiveTradeWithoutSending()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0]));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Single(h.Messages);
            h.FreshRoundTrip("after-missing-position");
        }
        [Fact]
        public void Case9_InconsistentSnapshotChangesNothingAndNextFillDecides()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1, .25m));
            var live = Assert.Single(h.Worker.Builder.Open);
            h.Worker.Resync("Sim101", new AccountSnapshot(new[] { Fixtures.Position(-5, 120) }, new ExecutionEvidence[0], false));
            Assert.Same(live, Assert.Single(h.Worker.Builder.Open)); Assert.True(live.Sendable); Assert.Equal(1, live.Net); Assert.Equal(.25m, live.Commission);
            h.Worker.Apply(Fixtures.Fill("end", false, 1, 101, 0, .25m));
            Assert.Equal(150, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents);
            Assert.DoesNotContain(h.Messages, x => x.Contains("Not sent"));
        }
        [Fact]
        public async Task Case9_ReconnectCommissionAmendmentDispatchesCorrectNetBeforeDelayedUpdate()
        {
            using (var folder = new TestFolder())
            {
                var h = new Harness(); var api = new DispatchApi();
                var session = new SessionCoordinator(Fixtures.Settings(h.Files, true), h.Queue, api, new EventLog(folder.Path));
                h.Queue.DurableAdded += trade => session.NotifyQueued();
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                h.Worker.Apply(Fixtures.Fill("e1", true, 1, 100, 1));
                var amended = Fixtures.Fill("e1", true, 1, 100, 1, 1);
                var snapshot = AccountSnapshot.Capture(() => new List<PositionSnapshot> { Fixtures.Position(1) },
                    () => new List<ExecutionEvidence> { ExecutionEvidence.From(amended) });
                Assert.True(snapshot.Consistent); h.Worker.Resync("Sim101", snapshot);
                h.Worker.Apply(Fixtures.Fill("exit", false, 1, 101, 0));
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                Assert.Equal(1, api.Inner.Sends); var sent = Assert.Single(api.Sent);
                Assert.Equal(100, sent.PnlCents); Assert.Equal(100, sent.CommissionCents);
                Assert.Empty(h.Worker.Builder.Open); Assert.Equal(0, h.Queue.Count); Assert.Empty(h.Messages);
            }
        }
        [Theory]
        [InlineData("price")]
        [InlineData("quantity")]
        [InlineData("side")]
        [InlineData("time")]
        [InlineData("instrument")]
        [InlineData("position-after")]
        [InlineData("point-value")]
        [InlineData("currency")]
        [InlineData("name")]
        public async Task Case9_ReconnectKnownExecutionAmendmentMakesUnknownOnceAndNeverDispatches(string changed)
        {
            using (var folder = new TestFolder())
            {
                var h = new Harness(); var api = new DispatchApi();
                var session = new SessionCoordinator(Fixtures.Settings(h.Files, true), h.Queue, api, new EventLog(folder.Path));
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                var entry = Fixtures.Fill("entry", true, 1, 100, 1); h.Worker.Apply(entry);
                // Keep all position metadata unchanged: only the known raw execution differs.
                var snapshot = new AccountSnapshot(new[] { Fixtures.Position(1) }, new[] { Amend(ExecutionEvidence.From(entry), changed) });
                h.Worker.Resync("Sim101", snapshot); h.Worker.Resync("Sim101", snapshot);
                var unknown = Assert.Single(h.Worker.Builder.Open);
                Assert.True(unknown.UnknownStart); Assert.True(unknown.Broken); Assert.Equal(1, unknown.Net);
                Assert.Contains("execution changed", Assert.Single(h.Messages));
                h.Worker.Apply(Fixtures.Fill("exit", false, 1, 101, 0));
                Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); Assert.Equal(0, h.Queue.Count);
                await session.ProcessAsync(Fixtures.Time + 300000, CancellationToken.None);
                Assert.Equal(0, api.Inner.Sends); Assert.Empty(api.Sent); Assert.Single(h.Messages);
            }
        }
        [Fact]
        public void Case9_ReconnectPartialExitCommissionAmendmentIsIdempotentWithDelayedUpdate()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 3, 100, 3, .30m));
            h.Worker.Apply(Fixtures.Fill("partial", false, 1, 101, 2, .20m));
            var amended = Fixtures.Fill("partial", false, 1, 101, 2, 1.20m);
            AssertReconnectCommission(h, amended, Fixtures.Position(2), 1.50m);
            h.Worker.Apply(Fixtures.Fill("exit", false, 2, 102, 0, .40m));
            var trade = Assert.Single(h.Queue.Peek(20)).Trade;
            Assert.Equal(190, trade.CommissionCents); Assert.Equal(810, trade.PnlCents);
            Assert.Equal("Execution correction: no affected waiting trade. Already dispatched trades cannot be changed.", Assert.Single(h.Messages));
        }
        [Fact]
        public void Case9_ReconnectLiveReversalCommissionUsesOnlyRemainderAndDelayedUpdateIsIdempotent()
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 2, 100, 2, .20m));
            h.Worker.Apply(Fixtures.Fill("flip", false, 3, 110, -1, .60m));
            var live = Assert.Single(h.Worker.Builder.Open); var applied = Assert.Single(live.Fills);
            Assert.Equal(3, applied.SourceQty); Assert.Equal(1, applied.AppliedQty);
            var amended = Fixtures.Fill("flip", false, 3, 110, -1, 1.50m);
            AssertReconnectCommission(h, amended, Fixtures.Position(-1, 110), .50m);
            h.Worker.Apply(Fixtures.Fill("exit", true, 1, 109, 0, .40m));
            var trade = Assert.Single(h.Queue.Peek(20)).Trade;
            Assert.Equal("exit", trade.ExitExecId); Assert.Equal(90, trade.CommissionCents); Assert.Equal(110, trade.PnlCents);
        }
        [Fact]
        public void Case9_ReconnectUnknownReversalCommissionUsesOnlyRemainderAndDelayedUpdateIsIdempotent()
        {
            var h = new Harness(); h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(2)));
            h.Worker.Apply(Fixtures.Fill("flip", false, 5, 110, -3, 1));
            var live = Assert.Single(h.Worker.Builder.Open); var applied = Assert.Single(live.Fills);
            Assert.Equal(5, applied.SourceQty); Assert.Equal(3, applied.AppliedQty);
            var amended = Fixtures.Fill("flip", false, 5, 110, -3, 2);
            AssertReconnectCommission(h, amended, Fixtures.Position(-3, 110), 1.20m);
            h.Worker.Apply(Fixtures.Fill("exit", true, 3, 109, 0, .40m));
            var trade = Assert.Single(h.Queue.Peek(20)).Trade;
            Assert.Equal(160, trade.CommissionCents); Assert.Equal(440, trade.PnlCents);
            Assert.Equal(2, h.Messages.Count); Assert.Contains("was open before ZoneKeeper started", h.Messages[0]);
            Assert.Equal("Execution correction: no affected waiting trade. Already dispatched trades cannot be changed.", h.Messages[1]);
        }
        [Theory]
        [InlineData("commission")]
        [InlineData("price")]
        [InlineData("quantity")]
        [InlineData("side")]
        [InlineData("time")]
        [InlineData("instrument")]
        [InlineData("position-after")]
        [InlineData("point-value")]
        [InlineData("currency")]
        [InlineData("name")]
        public void Case9_ReconnectInconsistentSnapshotIgnoresKnownExecutionAmendment(string changed)
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", true, 1, 100, 1, .25m); h.Worker.Apply(entry);
            var live = Assert.Single(h.Worker.Builder.Open); var applied = Assert.Single(live.Fills);
            var snapshot = new AccountSnapshot(new[] { Fixtures.Position(1) }, new[] { Amend(ExecutionEvidence.From(entry), changed) }, false);
            h.Worker.Resync("Sim101", snapshot);
            Assert.Same(live, Assert.Single(h.Worker.Builder.Open)); Assert.True(live.Sendable); Assert.Equal(.25m, live.Commission);
            Assert.Equal(.25m, applied.SourceCommission); Assert.True(ExecutionEvidence.From(entry).Matches(applied.Evidence));
            h.Worker.Apply(Fixtures.Fill("exit", false, 1, 101, 0, .25m));
            Assert.Equal(150, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents);
            Assert.Contains("snapshot was inconsistent", Assert.Single(h.Messages));
        }
        [Fact]
        public async Task Case9_ReconnectCommissionMutationKeepsDispatchExcludedThroughPersistence()
        {
            using (var folder = new TestFolder())
            {
                var h = new Harness(); var api = new DispatchApi();
                var session = new SessionCoordinator(Fixtures.Settings(h.Files, true), h.Queue, api, new EventLog(folder.Path));
                h.Queue.DurableAdded += trade => session.NotifyQueued();
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                Assert.True(h.Queue.Add(Fixtures.Trade("unrelated", "Sim102")));
                h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1));
                Task send = null; decimal? persistedCommission = null; decimal? persistedEvidenceCommission = null;
                h.Files.BeforeWrite = path => {
                    if (path != "open" || send != null) return;
                    var live = Assert.Single(h.Worker.Builder.Open);
                    persistedCommission = live.Commission; persistedEvidenceCommission = Assert.Single(live.Fills).Evidence.Commission;
                    send = session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                    Assert.False(send.IsCompleted); Assert.Equal(0, api.Inner.Sends);
                };
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { Fixtures.Fill("entry", true, 1, 100, 1, 1) }, Fixtures.Position(1)));
                h.Files.BeforeWrite = null; Assert.NotNull(send); await send;
                Assert.Equal(1, persistedCommission); Assert.Equal(1, persistedEvidenceCommission);
                Assert.Equal(1, api.Inner.Sends); Assert.Equal("unrelated", Assert.Single(api.Sent).ExitExecId);
                Assert.True(Assert.Single(h.Worker.Builder.Open).Sendable);
            }
        }
        [Theory]
        [InlineData("after")]
        [InlineData("between")]
        public void Case9_UnknownExecutionAfterFirstRawEntryAddsDoubtAtReconnect(string gap)
        {
            var h = new Harness(); var first = Fixtures.Fill("raw entry/id", true, 1, 100, 1); h.Worker.Apply(first);
            var x1 = Fixtures.Fill("x1", false, 1, 100, 0); var x2 = Fixtures.Fill("x2", true, 1, 100, 1);
            var history = new List<Fill> { first, x1, x2 }; int net = 1;
            if (gap == "between") { var scale = Fixtures.Fill("scale", true, 1, 100, 2); h.Worker.Apply(scale); history.Add(scale); net = 2; }
            Assert.NotEqual(first.ExecId, Assert.Single(h.Worker.Builder.Open).EntryExecIds[0]);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(history, Fixtures.Position(net)));
            Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart);
            h.Worker.Apply(Fixtures.Fill("end", false, net, 101, 0));
            Assert.Empty(h.Queue.Peek(20)); Assert.Empty(h.Worker.Builder.Open); Assert.Single(h.Messages);
        }
        [Fact]
        public void Case9_ReversalRemainderUsesRawReversalAnchorAndOlderExecutionsAddNoDoubt()
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", true, 2, 100, 2); var flip = Fixtures.Fill("raw reversal/id", false, 3, 110, -1);
            h.Worker.Apply(entry); h.Worker.Apply(flip);
            var old = Fixtures.Fill("old-prefix", true, 1, 90, 1);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { old, entry, flip }, Fixtures.Position(-1, 110)));
            Assert.True(Assert.Single(h.Worker.Builder.Open).Sendable);
            h.Worker.Apply(Fixtures.Fill("end", true, 1, 109, 0));
            Assert.Equal(2, h.Queue.Peek(20).Count); Assert.Equal(200, h.Queue.Peek(20).Single(x => x.Trade.ExitExecId == "end").Trade.PnlCents);
            Assert.Empty(h.Messages);
        }
        [Fact]
        public void Case9_SnapshotExecutionIdsAreNeverSeededIntoFillDedupe()
        {
            var h = new Harness(); var entry = Fixtures.Fill("cached-entry", true, 1, 100, 1); var end = Fixtures.Fill("cached-end", false, 1, 101, 0);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry, end }));
            Assert.Equal(0, h.Worker.Builder.SeenCount); h.Worker.Apply(entry); h.Worker.Apply(end);
            Assert.Equal(200, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents);
        }
        [Theory]
        [InlineData("net")]
        [InlineData("point-value")]
        [InlineData("currency")]
        public void Case9_ReconnectRejectsChangedPositionMetadata(string changed)
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); var position = Fixtures.Position(1);
            if (changed == "net") position.Net = 2;
            if (changed == "point-value") position.PointValue = 50;
            if (changed == "currency") position.Currency = "CAD";
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], position));
            Assert.True(Assert.Single(h.Worker.Builder.Open).UnknownStart);
            h.Worker.Apply(Fixtures.Fill("end", false, position.Net, 101, 0)); Assert.Empty(h.Queue.Peek(20));
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case10_ResetAndDisableDropLiveTradesAndUnknownMarkers(bool reset)
        {
            var h = new Harness(); h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(2)));
            h.Worker.Apply(Fixtures.Fill("live", true, 1, 100, 1, 0, "Sim101", "MNQ 03-27"));
            h.Worker.Apply(Fixtures.Fill("other-account", true, 1, 100, 1, 0, "Sim102"));
            if (reset) h.Worker.Reset("Sim101"); else h.Worker.Disable("Sim101");
            Assert.Equal("Sim102", Assert.Single(h.Worker.Builder.Open).Account); Assert.Empty(h.Worker.Snapshot().OpenTrades);
            h.Worker.Apply(Fixtures.Fill("old-end", false, 2, 101, 0)); Assert.Empty(h.Queue.Peek(20));
            h.FreshRoundTrip("after-drop");
            Assert.Equal("Sim102", Assert.Single(h.Worker.Builder.Open).Account);
        }
    }
}
