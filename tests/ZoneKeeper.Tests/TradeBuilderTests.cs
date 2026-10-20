using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class TradeBuilderTests
    {
        private static TradeBuilder Builder() { return new TradeBuilder(TimeZoneInfo.Utc, delegate { }); }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void T1_T2_MnqLongAndShortNet4340(bool buy)
        {
            var b = Builder();
            Assert.Empty(b.OnAdd(Fixtures.Fill("entry", buy, 2, buy ? 21034.25m : 21045.75m, buy ? 2 : -2, 1.30m)));
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("exit", !buy, 2, buy ? 21045.75m : 21034.25m, 0, 1.30m)));
            Assert.Equal(4340, trade.PnlCents); Assert.Equal(260, trade.CommissionCents);
            Assert.Equal(buy ? "long" : "short", trade.Side); Assert.Empty(b.Open);
        }
        [Fact]
        public void T3_ScaleInWeightedEntry()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 1, 100, 1, .1m)); b.OnAdd(Fixtures.Fill("b", true, 1, 102, 2, .1m));
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("c", false, 2, 105, 0, .2m)));
            Assert.Equal(2, trade.Qty); Assert.Equal(101, trade.EntryPrice); Assert.Equal(1560, trade.PnlCents);
        }
        [Fact]
        public void T4_PartialExitsMakeOneTradeAndKeepNames()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2));
            var first = Fixtures.Fill("b", false, 1, 103, 1); first.Name = "Target1";
            Assert.Empty(b.OnAdd(first));
            var last = Fixtures.Fill("c", false, 1, 105, 0); last.Name = "Stop1";
            var trade = Assert.Single(b.OnAdd(last));
            Assert.Equal(104, trade.ExitPrice); Assert.Equal("c", trade.ExitExecId); Assert.Equal("Stop1", trade.ExitName);
            Assert.Equal("Target1, Stop1", trade.LogExitNames); Assert.Equal(1600, trade.PnlCents);
        }
        [Fact]
        public void T5_AddAfterPartialExitUsesCashFlow()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2)); b.OnAdd(Fixtures.Fill("b", false, 1, 102, 1));
            b.OnAdd(Fixtures.Fill("c", true, 1, 104, 2));
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("d", false, 2, 106, 0)));
            Assert.Equal(3, trade.Qty); Assert.Equal(2000, trade.PnlCents); Assert.Equal(101.33333333, trade.EntryPrice);
        }
        [Fact]
        public void T6_ReversalSplitsCommissionAndSharesExecution()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2, 2));
            var old = Assert.Single(b.OnAdd(Fixtures.Fill("flip", false, 3, 110, -1, 3)));
            Assert.Equal(3600, old.PnlCents); Assert.Equal(400, old.CommissionCents); Assert.Equal("flip", old.ExitExecId);
            var next = Assert.Single(b.OnAdd(Fixtures.Fill("end", true, 1, 105, 0, 1)));
            Assert.Equal("short", next.Side); Assert.Equal(800, next.PnlCents); Assert.Equal(200, next.CommissionCents);
            Assert.Equal("flip", Assert.Single(next.EntryExecIds));
        }
        [Fact]
        public void T6_ReversalTradeIsMarkedAndKnowsItsPosition()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 1, 100, 1));
            var old = Assert.Single(b.OnAdd(Fixtures.Fill("flip", false, 2, 101, -1)));
            Assert.True(old.Reversal); Assert.Equal(Helpers.PairKey("Sim101", "MNQ 12-26"), old.PositionKey);
            var next = Assert.Single(b.OnAdd(Fixtures.Fill("end", true, 1, 100, 0)));
            Assert.False(next.Reversal); Assert.Equal(old.PositionKey, next.PositionKey);
            // Local only: never in the server JSON.
            Assert.DoesNotContain("Reversal", Json.Write(old)); Assert.DoesNotContain("PositionKey", Json.Write(old));
        }
        [Fact]
        public void FillCoordinatorKnowsWhichPositionsAreOpen()
        {
            var files = new MemoryFiles();
            var fills = new FillCoordinator(Builder(), new OpenTradesFile("open", files, delegate { }), new OutboxQueue("queue", files, delegate { }));
            string mnq = Helpers.PairKey("Sim101", "MNQ 12-26"), es = Helpers.PairKey("Sim101", "ES 12-26");
            Assert.False(fills.IsOpen(mnq)); Assert.False(fills.IsOpen(null));
            fills.Apply(Fixtures.Fill("a", true, 1, 100, 1)); Assert.True(fills.IsOpen(mnq)); Assert.False(fills.IsOpen(es));
            fills.Apply(Fixtures.Fill("flip", false, 2, 101, -1)); Assert.True(fills.IsOpen(mnq));
            fills.Apply(Fixtures.Fill("end", true, 1, 100, 0)); Assert.False(fills.IsOpen(mnq));
            fills.Apply(Fixtures.Fill("b", true, 1, 100, 1)); fills.Reset("Sim101"); Assert.False(fills.IsOpen(mnq));
            // A position open before ZoneKeeper started (unknown start) also counts.
            fills.Resync("Sim101", Fixtures.Snapshot(new Fill[0], Fixtures.Position(2)));
            Assert.True(fills.IsOpen(mnq));
            fills.Disable("Sim101"); Assert.False(fills.IsOpen(mnq));
        }
        [Fact]
        public void T7_PartialFillThenReversal()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2)); b.OnAdd(Fixtures.Fill("b", false, 1, 101, 1));
            var old = Assert.Single(b.OnAdd(Fixtures.Fill("c", false, 2, 102, -1)));
            var next = Assert.Single(b.OnAdd(Fixtures.Fill("d", true, 1, 100, 0)));
            Assert.Equal(600, old.PnlCents); Assert.Equal(400, next.PnlCents);
        }
        [Fact]
        public void T8_DuplicateAddIsIgnoredPerAccountOnly()
        {
            var b = Builder(); var f = Fixtures.Fill("same", true, 1, 100, 1);
            b.OnAdd(f); b.OnAdd(f); b.OnAdd(Fixtures.Fill("same", true, 1, 100, 1, 0, "Sim102"));
            Assert.Equal(2, b.Open.Count);
            Assert.Single(b.OnAdd(Fixtures.Fill("exit", false, 1, 101, 0)));
            Assert.Single(b.OnAdd(Fixtures.Fill("exit", false, 1, 101, 0, 0, "Sim102")));
        }
        [Fact]
        public void T9_T10_AccountsAndExpiriesAreSeparate()
        {
            var b = Builder();
            foreach (var account in new[] { "Sim101", "Sim102" }) foreach (var instrument in new[] { "MNQ 12-26", "MNQ 03-27" })
                b.OnAdd(Fixtures.Fill(account + instrument, true, 1, 100, 1, 0, account, instrument));
            Assert.Equal(4, b.Open.Count);
            foreach (var account in new[] { "Sim101", "Sim102" }) foreach (var instrument in new[] { "MNQ 12-26", "MNQ 03-27" })
                Assert.Single(b.OnAdd(Fixtures.Fill("exit" + instrument, false, 1, 101, 0, 0, account, instrument)));
            Assert.Empty(b.Open);
        }
        [Fact]
        public void T11_CommissionUpdateReplacesAmountWhileLiveAndRestartRestoresNothing()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 1, 100, 1, .1m));
            var update = Fixtures.Fill("a", true, 1, 100, 1, .7m); update.Op = "Update";
            b.Process(update); b.Process(update);
            var files = new MemoryFiles(); files.Data["open"] = Json.Write(new OpenTradesState { OpenTrades = b.Open });
            var restarted = Builder();
            new FillCoordinator(restarted, new OpenTradesFile("open", files, delegate { }), new OutboxQueue("queue", files, delegate { }));
            Assert.Empty(restarted.Open); Assert.Equal(0, restarted.SeenCount);
            restarted.OnResync("Sim101", Fixtures.Snapshot(new[] { update }, Fixtures.Position(1)));
            Assert.Empty(restarted.OnAdd(Fixtures.Fill("b", false, 1, 101, 0, .3m)));
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("b", false, 1, 101, 0, .3m)));
            Assert.Equal(100, trade.CommissionCents); Assert.Equal(100, trade.PnlCents);
        }
        [Fact]
        public void LateReversalCommissionUsesOnlyItsRemainingFraction()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, 2)); b.OnAdd(Fixtures.Fill("flip", false, 3, 110, -1, 3));
            b.OnResync("Sim101", Fixtures.Snapshot(new[] { Fixtures.Fill("a", true, 2, 100, 2), Fixtures.Fill("flip", false, 3, 110, -1, 3) }, Fixtures.Position(-1, 110)));
            var update = Fixtures.Fill("flip", false, 3, 110, -1, 6); update.Op = "Update"; b.Process(update);
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("end", true, 1, 109, 0)));
            Assert.Equal(200, trade.CommissionCents); Assert.Equal(0, trade.PnlCents);
        }
        [Theory]
        [InlineData("qty")]
        [InlineData("price")]
        [InlineData("direction")]
        [InlineData("remove")]
        [InlineData("remove-zero")]
        [InlineData("update-zero")]
        public void T11_ChangedOrRemovedFillsAreNeverSent(string change)
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 1, 100, 1));
            var update = Fixtures.Fill("a", true, 1, 100, 1); update.Op = change.StartsWith("remove") ? "Remove" : "Update";
            if (change == "qty") update.Qty = 2;
            if (change == "price") update.Price = 101;
            if (change == "direction") update.IsBuy = false;
            if (change.EndsWith("zero")) update.Qty = 0;
            b.Process(update); Assert.True(Assert.Single(b.Open).Broken);
            Assert.Empty(b.OnAdd(Fixtures.Fill("b", false, 1, 101, 0))); Assert.Empty(b.Open);
        }
        [Fact]
        public void ClosedFillChangesDoNotCreateOrChangeAnotherTrade()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 1, 100, 1));
            var trade = Assert.Single(b.OnAdd(Fixtures.Fill("b", false, 1, 101, 0)));
            var update = Fixtures.Fill("a", true, 1, 100, 0, 100); update.Op = "Update";
            b.Process(update); update.Op = "Remove"; b.Process(update);
            Assert.Equal(200, trade.PnlCents); Assert.Empty(b.Open);
        }
        [Fact]
        public void T15_ResyncChecksSignedSizeWithoutRestoringOrSeedingExecutions()
        {
            var old = Fixtures.Fill("old", true, 1, 100, 1);
            var b = Builder(); b.OnResync("Sim101", Fixtures.Snapshot(new[] { old }));
            Assert.Equal(0, b.SeenCount); b.OnAdd(old);
            Assert.False(Assert.Single(b.Open).UnknownStart);
            Assert.Single(b.OnAdd(Fixtures.Fill("b", false, 1, 101, 0)));
            b.OnAdd(Fixtures.Fill("new", true, 1, 100, 1));
            b.OnResync("Sim101", new[] { new PositionSnapshot { Instrument = "MNQ 12-26", Net = -1, PointValue = 2, Currency = "USD" } }, new string[0]);
            Assert.True(Assert.Single(b.Open).UnknownStart); Assert.Equal(-1, Assert.Single(b.Open).Net);
            Assert.Empty(b.OnAdd(Fixtures.Fill("close", true, 1, 99, 0))); Assert.Empty(b.Open);
        }
        [Fact]
        public void T15_UnknownStartAndResetDoNotSendTrades()
        {
            var b = Builder(); b.OnResync("Sim101", new[] { new PositionSnapshot { Instrument = "MNQ 12-26", Net = 2, AveragePrice = 100, PointValue = 2, Currency = "USD" } }, new string[0]);
            Assert.Empty(b.OnAdd(Fixtures.Fill("close", false, 2, 110, 0)));
            b.OnAdd(Fixtures.Fill("new", true, 1, 100, 1)); b.OnReset("Sim101"); Assert.Empty(b.Open);
        }
        [Fact]
        public void T16_ExitLocalDateOverTorontoMidnight()
        {
            var b = new TradeBuilder(TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"), delegate { });
            var start = Fixtures.Fill("a", true, 1, 100, 1); start.TimeMs = DateTimeOffset.Parse("2026-10-13T23:57:00-04:00").ToUnixTimeMilliseconds(); start.ArrivedMs = start.TimeMs;
            var end = Fixtures.Fill("b", false, 1, 101, 0); end.TimeMs = DateTimeOffset.Parse("2026-10-14T00:03:00-04:00").ToUnixTimeMilliseconds(); end.ArrivedMs = end.TimeMs;
            b.OnAdd(start); var trade = Assert.Single(b.OnAdd(end)); Assert.Equal("2026-10-14", trade.LocalDate);
        }
        [Theory]
        [InlineData("MNQ", "0.25", "2", 50)]
        [InlineData("CL", "0.01", "1000", 1000)]
        [InlineData("6E", "0.00005", "125000", 625)]
        public void T17_TicksAndPointValuesRoundToCents(string instrument, string tickText, string pointText, long cents)
        {
            var b = Builder(); decimal tick = decimal.Parse(tickText, System.Globalization.CultureInfo.InvariantCulture), point = decimal.Parse(pointText, System.Globalization.CultureInfo.InvariantCulture);
            var a = Fixtures.Fill("a", true, 1, 100, 1, 0, "Sim101", instrument); a.PointValue = point;
            var z = Fixtures.Fill("z", false, 1, 100 + tick, 0, 0, "Sim101", instrument); z.PointValue = point;
            b.OnAdd(a); Assert.Equal(cents, Assert.Single(b.OnAdd(z)).PnlCents);
            Assert.Equal(1, Helpers.Cents(.005m)); Assert.Equal(-1, Helpers.Cents(-.005m));
        }
        [Fact]
        public void EsCommissionAndFlatGross()
        {
            var b = Builder(); var a = Fixtures.Fill("a", false, 2, 4300, -2, 2.5m, "Sim101", "ES 12-26"); a.PointValue = 50;
            var z = Fixtures.Fill("z", true, 2, 4300, 0, 2.5m, "Sim101", "ES 12-26"); z.PointValue = 50;
            b.OnAdd(a); var trade = Assert.Single(b.OnAdd(z)); Assert.Equal(500, trade.CommissionCents); Assert.Equal(-500, trade.PnlCents);
        }
        [Fact]
        public void T18_HashIdsCleanNamesAndLimitEntryIds()
        {
            Assert.Equal("abc:123!", Helpers.SafeId("abc:123!"));
            Assert.Matches(@"^h:[a-f0-9]{40}$", Helpers.SafeId("bad id\n")); Assert.Equal(Helpers.SafeId("bad id\n"), Helpers.SafeId("bad id\n"));
            Assert.NotEqual(Helpers.SafeId("bad id\n"), Helpers.SafeId("bad id"));
            Assert.Equal("ab", Helpers.Cut64(" \ta\0b\n ")); Assert.Equal(64, Helpers.Cut64(new string('a', 90)).Length);
            var b = Builder();
            for (int i = 0; i < 12; i++) b.OnAdd(Fixtures.Fill("entry" + i, true, 1, 100, i + 1));
            var close = Fixtures.Fill("bad id", false, 12, 101, 0); close.Name = "  " + new string('x', 40) + "\n";
            var t = Assert.Single(b.OnAdd(close)); Assert.Equal(10, t.EntryExecIds.Count); Assert.Equal(32, t.ExitName.Length); Assert.Equal(Helpers.SafeId("bad id"), t.ExitExecId);
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void T19_OldFillsSkippedExceptPlayback(bool playback)
        {
            var b = Builder(); var a = Fixtures.Fill("a", true, 1, 100, 1); a.ArrivedMs = a.TimeMs + 900001; a.IsPlayback = playback;
            b.OnAdd(a);
            var trade = Assert.Single(b.Open); Assert.Equal(!playback, trade.UnknownStart); Assert.Equal(playback, trade.Sendable);
        }
        [Fact]
        public void SeenSetKeepsLast5000AndAccountIdentityIsUnambiguous()
        {
            var b = Builder();
            for (int i = 0; i < 5001; i++) { var f = Fixtures.Fill("e" + i, true, 1, 100, 1); f.ArrivedMs += 1000000; b.OnAdd(f); }
            Assert.Equal(5000, b.SeenCount); b.OnAdd(Fixtures.Fill("e0", true, 1, 100, 1)); Assert.Single(b.Open);
            Assert.NotEqual(Helpers.PairKey("a|b", "c"), Helpers.PairKey("a", "b|c"));
        }
        [Theory]
        [InlineData("start")]
        [InlineData("scale-in")]
        [InlineData("scale-out")]
        [InlineData("close")]
        public void T21_PositionMismatchNeverSends(string where)
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 2, 100, where == "start" ? 3 : 2));
            if (where == "scale-in") b.OnAdd(Fixtures.Fill("b", true, 1, 100, 2));
            if (where == "scale-out") b.OnAdd(Fixtures.Fill("b", false, 1, 101, 2));
            int own = Math.Abs(Assert.Single(b.Open).Net);
            Assert.Empty(b.OnAdd(Fixtures.Fill("end", false, own, 101, where == "close" ? 1 : 0)));
            if (where == "close")
            {
                Assert.True(Assert.Single(b.Open).UnknownStart);
                Assert.Empty(b.OnAdd(Fixtures.Fill("flat", false, 1, 101, 0)));
            }
            Assert.Empty(b.Open);
        }
        [Fact]
        public void T21_UnknownPositionEndsOnNtFlatThenNextTradeIsSent()
        {
            var b = Builder(); b.OnAdd(Fixtures.Fill("a", true, 3, 100, 4));
            Assert.True(Assert.Single(b.Open).UnknownStart);
            Assert.Empty(b.OnAdd(Fixtures.Fill("b", false, 4, 101, 0))); Assert.Empty(b.Open);
            b.OnAdd(Fixtures.Fill("c", true, 1, 100, 1)); Assert.Single(b.OnAdd(Fixtures.Fill("d", false, 1, 101, 0)));
        }
        [Fact]
        public void UnsupportedCurrencyCannotBeQueued()
        {
            var b = Builder(); var a = Fixtures.Fill("a", true, 1, 100, 1); a.Currency = "JapaneseYen";
            b.OnAdd(a); Assert.Empty(b.OnAdd(Fixtures.Fill("b", false, 1, 101, 0)));
        }
    }
}
