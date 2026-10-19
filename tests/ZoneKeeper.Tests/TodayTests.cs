using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // The Today list and the part / breath / timer rules of the reset panel (plan 31 sections 1 and 2.3).
    public sealed class TodayTests
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test-4", TimeSpan.FromHours(-4), "Test-4", "Test-4");
        private const long T = 1791902520000L; // 2026-10-13 14:42 UTC = 10:42 at UTC-4

        [Fact]
        public void T1_RowsNewestFirstWithTheRightState()
        {
            var answer = (TodayAnswer)Fixtures.TodayOk(
                Fixtures.TodayTrade(810, exitMs: T - 7200000, entryId: 640),
                Fixtures.TodayTrade(811, exitMs: T - 3600000),
                Fixtures.TodayTrade(812, exitMs: T),
                Fixtures.TodayTrade(813, exitMs: T),
                Fixtures.TodayTrade(0, exitMs: T + 5)).Body;
            var rows = TodayRules.Rows(answer, new List<long> { 811 }, new HashSet<long> { 813 });
            Assert.Equal(new long[] { 813, 812, 811, 810 }, rows.Select(x => x.Trade.Id).ToArray());
            Assert.Equal(new[] { RowState.Reset, RowState.Reset, RowState.Saving, RowState.Logged }, rows.Select(x => x.State).ToArray());
            Assert.Equal(new[] { true, false, false, false }, rows.Select(x => x.IsNew).ToArray());
            // A trade with an entry on the site is Logged even while a copy waits here.
            Assert.Equal(RowState.Logged, TodayRules.Rows(answer, new List<long> { 810 }, null).Last().State);
            Assert.Empty(TodayRules.Rows(null, null, null)); Assert.Empty(TodayRules.Rows(new TodayAnswer(), null, null));
        }
        [Fact]
        public void T2_RowAndAmountTexts()
        {
            var t = Fixtures.TodayTrade(812, 21250, T);
            Assert.Equal("10:42 · ES 12-26 · Short 2 · +212.50", TodayRules.RowText(t, Zone));
            Assert.Equal("Reset · ES 12-26 · Short 2 · +212.50 · 10:42", TodayRules.ResetHeader(t, Zone));
            Assert.Equal("New trade: ES 12-26 +212.50", TodayRules.NewTradeNote(t));
            Assert.Equal("−62.50 CAD", TodayRules.AmountText(Fixtures.TodayTrade(1, -6250, currency: "CAD")));
            Assert.Equal("−12.50", TodayRules.AmountText(Fixtures.TodayTrade(1, -1250)));
            Assert.Equal("0", TodayRules.AmountText(Fixtures.TodayTrade(1, 0)));
            Assert.Equal("+0.01", TodayRules.AmountText(Fixtures.TodayTrade(1, 1)));
            var one = Fixtures.TodayTrade(1); one.Side = "long"; one.Qty = 1;
            Assert.Equal("Long 1", TodayRules.SideText(one));
            Assert.Equal("Today · Oct 13", TodayRules.TodayTitle(T, Zone));
            Assert.Equal("Today · Oct 14", TodayRules.TodayTitle(T + 14 * 3600000L, Zone));
        }
        [Fact]
        public void T3_TriggerFromThePnl()
        {
            Assert.Equal("stop_loss", TodayRules.TriggerFor(-1)); Assert.Equal("winning_trade", TodayRules.TriggerFor(1)); Assert.Equal("break_even", TodayRules.TriggerFor(0));
        }
        [Fact]
        public void T4_PartAndBreathRoundFromTheAudioMarks()
        {
            var marks = Fixtures.Catalog().Audio("b1").Marks;
            Assert.Equal(0, TodayRules.PartAt(marks, 0)); Assert.Equal(0, TodayRules.PartAt(marks, 9.96)); Assert.Equal(1, TodayRules.PartAt(marks, 9.97));
            Assert.Equal(2, TodayRules.PartAt(marks, 30)); Assert.Equal(3, TodayRules.PartAt(marks, 60)); Assert.Equal(4, TodayRules.PartAt(marks, 100));
            Assert.Equal(5, TodayRules.PartAt(marks, 120)); Assert.Equal(6, TodayRules.PartAt(marks, 123.88)); Assert.Equal(6, TodayRules.PartAt(marks, 200));
            Assert.Equal(0, TodayRules.PartAt(null, 50));
            Assert.Equal(57.2, TodayRules.PartStart(marks, 3)); Assert.Equal(76.01, TodayRules.PartEnd(marks, 3)); Assert.Equal(153.09, TodayRules.PartEnd(marks, 6));
            // Seconds inside the BREATHE part: before the first "In…" = get ready, then rounds 1-3.
            Assert.Equal(0, TodayRules.BreathRound(marks, 1)); Assert.Equal(1, TodayRules.BreathRound(marks, 2.65)); Assert.Equal(1, TodayRules.BreathRound(marks, 14.99));
            Assert.Equal(2, TodayRules.BreathRound(marks, 15)); Assert.Equal(2, TodayRules.BreathRound(marks, 20)); Assert.Equal(3, TodayRules.BreathRound(marks, 27.28)); Assert.Equal(3, TodayRules.BreathRound(marks, 40));
            Assert.Equal("Get ready.", UiText.RoundLine(0)); Assert.Equal("Round 2 of 3", UiText.RoundLine(2));
        }
        [Fact]
        public void T5_TimerModeFollowsThePartSeconds()
        {
            var parts = Fixtures.Catalog().Parts;
            Assert.Equal(121, TodayRules.TimerTotal(parts));
            var secs = new[] { 0, 4.9, 5, 14.9, 15, 54.9, 55, 79.9, 80, 94.9, 95, 102.9, 103, 120.9, 121, 500 };
            var parts_ = new[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 6, 6 };
            for (int i = 0; i < secs.Length; i++) Assert.Equal(parts_[i], TodayRules.TimerPartAt(parts, secs[i]));
            Assert.Equal(55, TodayRules.TimerPartStart(parts, 3)); Assert.Equal(0, TodayRules.TimerPartStart(parts, 0)); Assert.Equal(103, TodayRules.TimerPartStart(parts, 6));
            Assert.Equal(0, TodayRules.TimerBreathRound(3.9)); Assert.Equal(1, TodayRules.TimerBreathRound(4)); Assert.Equal(1, TodayRules.TimerBreathRound(15.9));
            Assert.Equal(2, TodayRules.TimerBreathRound(16)); Assert.Equal(3, TodayRules.TimerBreathRound(28)); Assert.Equal(3, TodayRules.TimerBreathRound(40));
            Assert.Equal(0, TodayRules.TimerPartAt(null, 10)); Assert.Equal(0, TodayRules.TimerTotal(null));
        }
        [Fact]
        public void T6_SmallTexts()
        {
            Assert.Equal("1:52 left", TodayRules.LeftText(112)); Assert.Equal("1:52 left", TodayRules.LeftText(111.2)); Assert.Equal("0:00 left", TodayRules.LeftText(-3)); Assert.Equal("2:33 left", TodayRules.LeftText(153));
            Assert.Equal("g", TodayRules.Band(3, 7)); Assert.Equal("o", TodayRules.Band(4, 7)); Assert.Equal("o", TodayRules.Band(6, 7)); Assert.Equal("r", TodayRules.Band(7, 7)); Assert.Equal("o", TodayRules.Band(7, 8));
            Assert.Equal("Loading audio… 45 %", UiText.AudioLoading(45)); Assert.Equal("Loading audio… 100 %", UiText.AudioLoading(140));
            Assert.Equal("Part 2 of 7 · RATE", UiText.PartLine(1, 7, "RATE"));
            Assert.Equal("Connected to r***@gmail.com", UiText.TodayStatus(ZkStatus.Connected, "r***@gmail.com"));
            Assert.Equal("Not connected — open the Connection tab.", UiText.TodayStatus(ZkStatus.NotConnected, null));
            Assert.Equal("Paused: your plan is not active.", UiText.TodayStatus(ZkStatus.Paused, "x"));
            Assert.Equal("Offline: trades and resets wait here.", UiText.TodayStatus(ZkStatus.Offline, "x"));
            Assert.Equal("Reset saved for the trade closed at " + DateTimeOffset.FromUnixTimeMilliseconds(T).ToLocalTime().ToString("HH:mm") + ".", UiText.ResetSaved(T));
            Assert.Equal("The server refused this reset: logic_id does not fit", UiText.ResetRefused("logic_id does not fit"));
            Assert.Equal("Audio needs a connection. The reset runs with the timer.", UiText.AudioNeedsConnection);
            Assert.Equal("Your ZoneKeeper plan is not active. Renew it on getzonekeeper.com.", UiText.AudioNotPaid);
            Assert.Equal(5, UiText.StepNames.Length);
        }
        [Fact]
        public void T7_LocalStamps()
        {
            Assert.Equal("2026-10-13T10:42", Helpers.LocalStamp(T, Zone)); Assert.Equal("2026-10-13T14:42", Helpers.LocalStamp(T, TimeZoneInfo.Utc));
            Assert.Equal("10:42", Helpers.LocalClock(T, Zone)); Assert.Equal("2026-10-13", Helpers.LocalDate(T, Zone));
            Assert.Equal(10, Helpers.LocalDateNow(TimeZoneInfo.Utc).Length);
        }
    }
}
