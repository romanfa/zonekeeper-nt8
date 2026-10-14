using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class RestartProofTests
    {
        private const string Refusal = "Open trade from before the restart could not be checked. Not sent. You can add it later by CSV.";
        private sealed class Harness
        {
            public readonly MemoryFiles Files = new MemoryFiles();
            public readonly List<string> Messages = new List<string>();
            public OutboxQueue Queue;
            public FillCoordinator Worker;
            public Harness() { Restart(); }
            public void Restart()
            {
                Queue = new OutboxQueue("queue", Files, Messages.Add);
                Worker = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Utc, Messages.Add),
                    new OpenTradesFile("open", Files, Messages.Add), Queue);
            }
            public OpenTrade Open { get { return Assert.Single(Worker.Builder.Open); } }
            public void RefusedOnce() { Assert.Single(Messages, x => x == Refusal); }
            public void NextTrade(bool buy = true)
            {
                Worker.Apply(Fixtures.Fill("new-entry", buy, 1, 100, buy ? 1 : -1));
                Worker.Apply(Fixtures.Fill("new-exit", !buy, 1, buy ? 101 : 99, 0));
                var record = Assert.Single(Queue.Peek(20)).Trade;
                Assert.Equal(200, record.PnlCents); Assert.Equal("new-exit", record.ExitExecId);
                Assert.Equal(new[] { "new-entry" }, record.EntryExecIds);
            }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void CachedOldIdCannotResumeOfflineReplacementAtMateriallyChangedAverage(bool buy, bool restored)
        {
            int sign = buy ? 1 : -1; var h = new Harness();
            var old = Fixtures.Fill("old-entry", buy, 3, 100, sign * 3); h.Worker.Apply(old);
            if (restored) h.Restart();
            // The provider reports a new 2@100 + 1@100.25 (or short 99.75) position, but caches only the old entry.
            decimal changedAverage = (decimal)((200d + (buy ? 100.25d : 99.75d)) / 3d);
            var snapshot = AccountSnapshot.Capture(() => new List<PositionSnapshot> { Fixtures.Position(sign * 3, changedAverage) },
                () => new List<ExecutionEvidence> { ExecutionEvidence.From(old) });
            Assert.True(snapshot.Consistent);
            h.Worker.Resync("Sim101", snapshot); Assert.True(h.Open.AwaitingFlat);
            h.Worker.Apply(Fixtures.Fill("end", !buy, 3, buy ? 101 : 99, 0));
            Assert.Empty(h.Queue.Peek(20)); Assert.Empty(h.Worker.Builder.Open);
            if (restored) h.RefusedOnce();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FullRawEntryAndPartialExitProofResumesScaledTrade(bool buy)
        {
            int sign = buy ? 1 : -1; var h = new Harness();
            var fills = new[] { Fixtures.Fill("entry", buy, 1, 100, sign, .1m),
                Fixtures.Fill("scale", buy, 2, buy ? 100.25m : 99.75m, sign * 3, .2m),
                Fixtures.Fill("partial", !buy, 1, buy ? 101 : 99, sign * 2, .1m) };
            foreach (var fill in fills) h.Worker.Apply(fill);
            h.Restart(); Assert.True(h.Open.AwaitingContinuity);
            decimal average = (decimal)((100d + 2d * (buy ? 100.25d : 99.75d)) / 3d);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(fills, Fixtures.Position(sign * 2, average)));
            Assert.True(h.Open.Sendable); Assert.False(h.Open.AwaitingContinuity); Assert.False(h.Open.AwaitingFlat);
            h.Worker.Apply(Fixtures.Fill("end", !buy, 2, buy ? 102 : 98, 0, .2m));
            var record = Assert.Single(h.Queue.Peek(20)).Trade;
            Assert.Equal(840, record.PnlCents); Assert.Equal(60, record.CommissionCents);
            Assert.Equal(3, record.Qty); Assert.Equal(buy ? "long" : "short", record.Side);
            Assert.Equal(fills[0].TimeMs, record.EntryTime); Assert.DoesNotContain(Refusal, h.Messages);
        }

        [Theory]
        [InlineData(true, "entry")]
        [InlineData(false, "entry")]
        [InlineData(true, "partial")]
        [InlineData(false, "partial")]
        [InlineData(true, "beyond-api-limit")]
        [InlineData(false, "beyond-api-limit")]
        public void EverySavedRawFillMustBePresentEvenIfCheckpointOmitsIt(bool buy, string missing)
        {
            int sign = buy ? 1 : -1; var h = new Harness(); var history = new List<Fill>();
            for (int i = 0; i < 12; i++)
            {
                var fill = Fixtures.Fill("raw entry " + i + new string('x', 90), buy, 1, 100, sign * (i + 1));
                history.Add(fill); h.Worker.Apply(fill);
            }
            var partial = Fixtures.Fill("partial", !buy, 1, buy ? 101 : 99, sign * 11);
            var scale = Fixtures.Fill("last-scale", buy, 1, 100, sign * 12);
            history.Add(partial); history.Add(scale); h.Worker.Apply(partial); h.Worker.Apply(scale);
            Assert.Equal(10, h.Open.EntryExecIds.Count);
            string missingId = missing == "partial" ? partial.ExecId : history[missing == "entry" ? 0 : 11].ExecId;
            Assert.DoesNotContain(missingId, h.Open.EntryExecIds);
            var file = new OpenTradesFile("open", h.Files, h.Messages.Add); var state = file.Load();
            // Isolate the raw-fill proof from checkpoint equality: the incomplete checkpoint itself matches.
            Assert.Single(state.OpenTrades).ExecutionCheckpoint.RemoveAll(x => x.ExecId == missingId);
            Assert.True(file.Save(state)); h.Restart();
            var actual = history.Where(x => x.ExecId != missingId).ToList();
            var snapshot = AccountSnapshot.Capture(() => new List<PositionSnapshot> { Fixtures.Position(sign * 12) },
                () => actual.Select(ExecutionEvidence.From).ToList());
            Assert.True(snapshot.Consistent); h.Worker.Resync("Sim101", snapshot);
            Assert.True(h.Open.AwaitingFlat); Assert.False(h.Open.Sendable); Assert.Equal(new[] { Refusal }, h.Messages);
            h.Worker.Apply(Fixtures.Fill("end", !buy, 12, buy ? 102 : 98, 0));
            Assert.Empty(h.Queue.Peek(20)); h.RefusedOnce();
        }

        [Theory]
        [InlineData(true, -3)]
        [InlineData(false, 3)]
        [InlineData(true, 2)]
        [InlineData(false, -2)]
        public void SavedSignedNetMustEqualActualPosition(bool buy, int actualNet)
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", buy, 3, 100, buy ? 3 : -3);
            h.Worker.Apply(entry); h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(actualNet)));
            Assert.True(h.Open.AwaitingFlat); Assert.Equal(actualNet, h.Open.Net); h.RefusedOnce();
            h.Worker.Apply(Fixtures.Fill("end", actualNet < 0, Math.Abs(actualNet), 101, 0));
            Assert.Empty(h.Queue.Peek(20)); h.NextTrade();
        }

        [Theory]
        [InlineData("ids-only")]
        [InlineData("changed-source-quantity")]
        [InlineData("changed-source-evidence")]
        [InlineData("missing-source-evidence")]
        [InlineData("null-saved-fill")]
        public void ExactPriceAndIdsCannotSubstituteForCompleteUnchangedSourceEvidence(string invalid)
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", true, 3, 100, 3); h.Worker.Apply(entry);
            if (invalid != "ids-only")
            {
                var file = new OpenTradesFile("open", h.Files, h.Messages.Add); var state = file.Load();
                var saved = Assert.Single(Assert.Single(state.OpenTrades).Fills);
                if (invalid == "changed-source-quantity") saved.SourceQty++;
                if (invalid == "changed-source-evidence") saved.Evidence.Price++;
                if (invalid == "missing-source-evidence") saved.Evidence = null;
                if (invalid == "null-saved-fill") Assert.Single(state.OpenTrades).Fills[0] = null;
                Assert.True(file.Save(state));
            }
            h.Restart();
            var snapshot = invalid == "ids-only" ? new AccountSnapshot(new[] { Fixtures.Position(3) },
                new[] { new ExecutionEvidence { ExecId = entry.ExecId, Instrument = entry.Instrument } }) :
                Fixtures.Snapshot(new[] { entry }, Fixtures.Position(3));
            h.Worker.Resync("Sim101", snapshot); Assert.True(h.Open.AwaitingFlat);
            Assert.Equal(new[] { Refusal }, h.Messages);
            h.Worker.Apply(Fixtures.Fill("end", false, 3, 101, 0)); Assert.Empty(h.Queue.Peek(20));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MissingProofWithholdsReversalsAcrossResyncAndSecondRestartThenSendsNextTrade(bool buy)
        {
            int sign = buy ? 1 : -1; var h = new Harness(); var entry = Fixtures.Fill("entry", buy, 3, 100, sign * 3);
            h.Worker.Apply(entry); h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(sign * 3)));
            Assert.True(h.Open.AwaitingFlat); h.RefusedOnce();
            h.Worker.Apply(Fixtures.Fill("flip", !buy, 6, 101, -sign * 3));
            h.Worker.Apply(Fixtures.Fill("flip-back", buy, 6, 100, sign * 3));
            Assert.Empty(h.Queue.Peek(20)); Assert.True(h.Open.AwaitingFlat);
            // Even later complete matching evidence does not resurrect the already refused saved trade.
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(sign * 3)));
            h.Restart(); Assert.True(h.Open.AwaitingFlat); Assert.False(h.Open.AwaitingContinuity);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry }, Fixtures.Position(sign * 3)));
            h.RefusedOnce(); Assert.True(h.Open.AwaitingFlat);
            using (var folder = new TestFolder())
            {
                var api = new FakeApi(); var session = new SessionCoordinator(Fixtures.Settings(h.Files, true), h.Queue, api, new EventLog(folder.Path));
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None); Assert.Equal(0, api.Sends);
                h.Worker.Apply(Fixtures.Fill("observed-flat", !buy, 3, 101, 0));
                Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20));
                h.NextTrade(buy); session.NotifyQueued();
                await session.ProcessAsync(Fixtures.Time + 15000, CancellationToken.None);
                Assert.Equal(1, api.Sends); Assert.Equal(new[] { 1 }, api.BatchSizes); Assert.Equal(0, h.Queue.Count);
                h.RefusedOnce();
            }
        }

        [Theory]
        [InlineData("Update")]
        [InlineData("Remove")]
        [InlineData("duplicate")]
        [InlineData("stale")]
        [InlineData("negative-quantity")]
        [InlineData("inconsistent-snapshot")]
        public void HistoricalOrRejectedFlatSignalsCannotReleaseQuarantine(string signal)
        {
            var h = new Harness(); var entry = Fixtures.Fill("entry", true, 3, 100, 3); h.Worker.Apply(entry); h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(3)));
            if (signal == "inconsistent-snapshot")
                h.Worker.Resync("Sim101", new AccountSnapshot(new PositionSnapshot[0], new ExecutionEvidence[0], false));
            else
            {
                var flat = Fixtures.Fill(signal == "duplicate" || signal == "Update" || signal == "Remove" ? "entry" : "old-flat", false, 0, 101, 0);
                if (signal == "Update" || signal == "Remove") flat.Op = signal;
                if (signal == "stale") flat.TimeMs -= 16 * 60000L;
                if (signal == "negative-quantity") flat.Qty = -1;
                h.Worker.Apply(flat);
            }
            Assert.True(h.Open.AwaitingFlat); Assert.Equal(3, h.Open.Net);
            h.Worker.Apply(Fixtures.Fill("flip", false, 6, 101, -3));
            Assert.True(h.Open.AwaitingFlat); Assert.Empty(h.Queue.Peek(20)); h.RefusedOnce();
        }

        [Theory]
        [InlineData("Update")]
        [InlineData("Remove")]
        public void OldFlatExecutionCorrectionCannotReleaseAnUnrelatedQuarantinedPosition(string operation)
        {
            var h = new Harness(); var old = Fixtures.Fill("old-flat", false, 1, 90, 0);
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { old }));
            var entry = Fixtures.Fill("entry", true, 3, 100, 3); h.Worker.Apply(entry); h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { old }, Fixtures.Position(3)));
            old.Op = operation; old.Commission = 1; h.Worker.Apply(old);
            Assert.True(h.Open.AwaitingFlat); h.Worker.Apply(Fixtures.Fill("flip", false, 6, 101, -3));
            Assert.True(h.Open.AwaitingFlat); Assert.Empty(h.Queue.Peek(20)); h.RefusedOnce();
        }

        [Theory]
        [InlineData("Update")]
        [InlineData("Remove")]
        public void HistoricalFlatCorrectionAheadOfStartupProofPreservesTheBarrier(string operation)
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 3, 100, 3)); h.Restart();
            var historical = Fixtures.Fill("entry", true, 3, 100, 0); historical.Op = operation;
            h.Worker.Apply(historical); Assert.True(h.Open.AwaitingFlat); Assert.Equal(3, h.Open.Net); h.RefusedOnce();
            h.Worker.Apply(Fixtures.Fill("flip", false, 6, 101, -3));
            Assert.True(h.Open.AwaitingFlat); Assert.Empty(h.Queue.Peek(20));
            h.Worker.Apply(Fixtures.Fill("observed-flat", true, 3, 100, 0));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); h.NextTrade(); h.RefusedOnce();
        }

        [Theory]
        [InlineData("zero-quantity-add")]
        [InlineData("explicit-flat-snapshot")]
        [InlineData("absent-flat-snapshot")]
        [InlineData("first-snapshot-flat")]
        public void ObservedCurrentFlatReleasesBarrierForTheNextTrade(string signal)
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 3, 100, 3)); h.Restart();
            if (signal != "first-snapshot-flat")
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(3)));
            if (signal == "zero-quantity-add") h.Worker.Apply(Fixtures.Fill("flat", false, 0, 101, 0));
            else h.Worker.Resync("Sim101", signal == "explicit-flat-snapshot" ? Fixtures.Snapshot(new Fill[0], Fixtures.Position(0)) : Fixtures.Snapshot(new Fill[0]));
            Assert.Empty(h.Worker.Builder.Open); Assert.Empty(h.Queue.Peek(20)); h.RefusedOnce(); h.NextTrade();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UnknownStartupPositionCannotSendAReversalRemainderBeforeObservedFlat(bool buy)
        {
            int sign = buy ? 1 : -1; var h = new Harness();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(sign * 3)));
            Assert.True(h.Open.AwaitingFlat); h.Restart();
            h.Worker.Apply(Fixtures.Fill("flip", !buy, 6, 101, -sign * 3));
            h.Worker.Apply(Fixtures.Fill("end", buy, 3, 100, 0));
            Assert.Empty(h.Queue.Peek(20)); Assert.Empty(h.Worker.Builder.Open); Assert.DoesNotContain(Refusal, h.Messages);
            h.NextTrade(buy);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void UnprovedRestoredCloseOrReversalAheadOfStartupCannotSend(bool buy, bool reversal)
        {
            int sign = buy ? 1 : -1; var h = new Harness(); var entry = Fixtures.Fill("entry", buy, 1, 100, sign);
            h.Worker.Apply(entry); h.Restart();
            var end = Fixtures.Fill("end", !buy, reversal ? 3 : 1, 101, reversal ? -sign * 2 : 0);
            h.Worker.Apply(end); Assert.Empty(h.Queue.Peek(20)); Assert.Equal(new[] { Refusal }, h.Messages);
            if (reversal)
            {
                Assert.True(h.Open.AwaitingFlat); h.Restart();
                h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry, end }, Fixtures.Position(-sign * 2, 101)));
                Assert.True(h.Open.AwaitingFlat);
                h.Worker.Apply(Fixtures.Fill("observed-flat", buy, 2, 100, 0));
                Assert.Empty(h.Queue.Peek(20));
            }
            Assert.Empty(h.Worker.Builder.Open); h.RefusedOnce(); h.NextTrade(buy);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FullProofResumesFractionalCommissionReversalWithOriginalSourceQuantity(bool buy)
        {
            int sign = buy ? 1 : -1; var h = new Harness();
            var entry = Fixtures.Fill("entry", buy, 1, 100, sign);
            var reversal = Fixtures.Fill("reversal", !buy, 3, buy ? 101 : 99, -sign * 2, 1m);
            h.Worker.Apply(entry); h.Worker.Apply(reversal);
            Assert.Equal(3, Assert.Single(h.Open.Fills).SourceQty); Assert.Equal(2, Assert.Single(h.Open.Fills).AppliedQty);
            h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry, reversal }, Fixtures.Position(-sign * 2, buy ? 101 : 99)));
            Assert.True(h.Open.Sendable); h.Worker.Apply(Fixtures.Fill("end", buy, 2, 100, 0, .1m));
            var records = h.Queue.Peek(20).Select(x => x.Trade).ToList(); Assert.Equal(2, records.Count);
            var remainder = Assert.Single(records, x => x.ExitExecId == "end");
            Assert.Equal(77, remainder.CommissionCents); Assert.Equal(323, remainder.PnlCents);
            Assert.Equal(new[] { "reversal" }, remainder.EntryExecIds); Assert.DoesNotContain(Refusal, h.Messages);
        }

        [Theory]
        [InlineData("0.25")]
        [InlineData("0.01")]
        [InlineData("0.00001")]
        [InlineData("0.03125")]
        [InlineData("0.005")]
        public void TickDerivedFloatNoiseBoundaryRequiresProofAndRejectsAnyLargerDifference(string tickText)
        {
            decimal tick = decimal.Parse(tickText, CultureInfo.InvariantCulture), tolerance = tick * .001m;
            foreach (bool restored in new[] { false, true }) foreach (int direction in new[] { -1, 1 }) foreach (bool exceeds in new[] { false, true })
            {
                var h = new Harness(); var entry = Fixtures.Fill("entry", true, 1, 100, 1); h.Worker.Apply(entry);
                if (restored) h.Restart();
                var position = Fixtures.Position(1, 100 + direction * (tolerance + (exceeds ? tolerance * .000001m : 0)));
                position.TickSize = tick; h.Worker.Resync("Sim101", Fixtures.Snapshot(new[] { entry }, position));
                h.Worker.Apply(Fixtures.Fill("end", false, 1, 101, 0));
                if (exceeds) { Assert.Empty(h.Queue.Peek(20)); if (restored) h.RefusedOnce(); }
                else Assert.Equal(200, Assert.Single(h.Queue.Peek(20)).Trade.PnlCents);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AccountResetAndDisableClearQuarantineAsTheyClearOpenState(bool reset)
        {
            var h = new Harness(); h.Worker.Apply(Fixtures.Fill("entry", true, 1, 100, 1)); h.Restart();
            h.Worker.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(1))); Assert.True(h.Open.AwaitingFlat);
            if (reset) h.Worker.Reset("Sim101"); else h.Worker.Disable("Sim101");
            h.Restart(); Assert.Empty(h.Worker.Builder.Open); h.NextTrade();
        }
    }
}
