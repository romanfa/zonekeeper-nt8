using System;
using System.Linq;
using System.Text.RegularExpressions;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // The 5 steps of one reset: what each tap does and what gets saved (plan 31 section 1).
    public sealed class ResetSessionTests
    {
        private static readonly TimeZoneInfo Toronto = TimeZoneInfo.CreateCustomTimeZone("Test-4", TimeSpan.FromHours(-4), "Test-4", "Test-4");
        private const long Tap1 = 1791902520000L; // 2026-10-13 14:42 UTC = 10:42 at UTC-4

        private static ResetSession Started(long pnl = -1250, int tilt = 5)
        {
            var s = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(812, pnl), Toronto);
            Assert.True(s.RateBefore(tilt, Tap1));
            return s;
        }
        private static ResetSession AtAudio(long pnl = -1250)
        {
            var s = Started(pnl);
            Assert.True(s.PickThought(s.Thoughts[0])); Assert.True(s.PickLine(s.Thought.Lines[1]));
            return s;
        }

        [Fact]
        public void S1_TiltSevenOpensTheRedLineAndResetAnywayGoesOn()
        {
            var s = Started(tilt: 7);
            Assert.True(s.RedLineOpen); Assert.True(s.RedLineAtStart); Assert.Equal(3, s.Step); Assert.False(s.Interruptible);
            Assert.False(s.PickThought(s.Thoughts[0]));
            Assert.True(s.ContinueAnyway()); Assert.False(s.RedLineOpen); Assert.False(s.ContinueAnyway());
            Assert.True(s.PickThought(s.Thoughts[0])); Assert.Equal(4, s.Step);
            Assert.False(Started(tilt: 6).RedLineOpen);
            // The red line comes from the catalog rules.
            var c = Fixtures.Catalog(); c.Rules.RedLine = 9;
            var late = new ResetSession(c, Fixtures.TodayTrade(1), Toronto); late.RateBefore(8, Tap1); Assert.False(late.RedLineOpen);
        }
        [Fact]
        public void S2_StepOrderIsEnforced()
        {
            var s = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(812, -1250), Toronto);
            Assert.Equal(1, s.Step); Assert.True(s.Interruptible);
            var thought = s.Thoughts[0];
            Assert.False(s.PickThought(thought)); Assert.False(s.PickLine(thought.Lines[0])); Assert.False(s.FinishAudio()); Assert.False(s.SkipAudio(0));
            Assert.Null(s.RateAgain(3, Tap1)); Assert.False(s.CanRateAgain);
            Assert.False(s.RateBefore(0, Tap1)); Assert.False(s.RateBefore(11, Tap1));
            Assert.True(s.RateBefore(5, Tap1)); Assert.False(s.RateBefore(4, Tap1)); Assert.Equal(5, s.TiltBefore); Assert.Equal(Tap1, s.TiltBeforeMs);
            Assert.False(s.Interruptible); Assert.Equal(3, s.Step);
            Assert.False(s.PickLine(thought.Lines[0]));           // no line before a thought
            Assert.False(s.PickThought(new CatalogThought { Key = "x", Text = "x" })); // not from this trigger
            Assert.True(s.PickThought(thought)); Assert.Equal(4, s.Step); Assert.False(s.PickThought(s.Thoughts[1]));
            Assert.False(s.PickLine(new CatalogLine { Id = "x" }));
            Assert.False(s.FinishAudio()); Assert.Null(s.RateAgain(3, Tap1));  // no audio phase before a line
            Assert.True(s.PickLine(thought.Lines[2])); Assert.False(s.PickLine(thought.Lines[0])); Assert.True(s.AudioPhase);
            Assert.Null(s.RateAgain(3, Tap1)); Assert.False(s.CanRateAgain);
        }
        [Fact]
        public void S3_RateAgainOnlyAfterTheAudioEndedOrWasSkipped()
        {
            var ended = AtAudio(); ended.VoiceStarted(); Assert.Equal(AudioMode.Voice, ended.AudioMode);
            Assert.True(ended.FinishAudio()); Assert.True(ended.CanRateAgain); Assert.Equal(5, ended.Step); Assert.True(ended.AudioEnded); Assert.False(ended.FinishAudio());
            var skipped = AtAudio(); skipped.TimerStarted(); Assert.Equal(AudioMode.Timer, skipped.AudioMode);
            Assert.True(skipped.SkipAudio(3)); Assert.True(skipped.CanRateAgain); Assert.Equal(3, skipped.SkippedAtPart); Assert.False(skipped.AudioEnded);
            var fresh = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(1), Toronto);
            fresh.VoiceStarted(); Assert.Equal(AudioMode.None, fresh.AudioMode);
        }
        [Fact]
        public void S4_TheStepFiveTapGivesTheBodyAtOnceAndOnlyOnce()
        {
            var s = AtAudio(); s.FinishAudio();
            Assert.Null(s.RateAgain(0, Tap1)); Assert.Null(s.RateAgain(11, Tap1));
            var body = s.RateAgain(3, Tap1 + 180000);
            Assert.NotNull(body); Assert.True(s.IsSaved); Assert.Equal(ResetSession.Saved, s.Step); Assert.Equal(3, s.TiltAfter); Assert.False(s.CanRateAgain);
            Assert.Null(s.RateAgain(4, Tap1 + 200000));
            Assert.Equal(812, body.TradeId); Assert.Equal("make_it_back", body.Thought); Assert.Equal("make_it_back.2", body.LogicId); Assert.Equal("b21", body.AudioId);
            Assert.Equal(5, body.TiltBefore); Assert.Equal(3, body.TiltAfter); Assert.Equal(Tap1 + 180000, body.TappedAt);
        }
        [Fact]
        public void S5_AudioIdIsNullOnlyWhenSkippedBeforePartTwo()
        {
            var early = AtAudio(); early.SkipAudio(0); Assert.Null(early.AudioIdToSave()); Assert.Null(early.RateAgain(2, Tap1).AudioId);
            var loading = AtAudio(); loading.SkipAudio(-1); Assert.Null(loading.AudioIdToSave());
            var later = AtAudio(); later.SkipAudio(2); Assert.Equal("b21", later.AudioIdToSave());
            var ended = AtAudio(); ended.FinishAudio(); Assert.Equal("b21", ended.AudioIdToSave());
            Assert.Null(Started().AudioIdToSave());
        }
        [Fact]
        public void S6_BodyHasExactlyTheServerKeysLocalStampsInTheZoneAndAStableRequestId()
        {
            var s = AtAudio(); s.FinishAudio();
            string id = s.RequestId;
            Assert.Matches(new Regex("^[0-9a-f]{32}$"), id);
            Assert.Equal(id, s.Body(Tap1).RequestId); Assert.Equal(id, s.Body(Tap1 + 5000).RequestId);
            var body = s.RateAgain(3, Tap1 + 180000);
            Assert.Equal(id, body.RequestId);
            string json = Json.Write(body);
            var keys = Regex.Matches(json, "\"([a-z_]+)\":").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "audio_id", "local", "logic_id", "request_id", "tapped_at", "thought", "tilt_after", "tilt_after_local", "tilt_before", "tilt_before_local", "trade_id" }, keys);
            Assert.Equal("2026-10-13T10:42", body.TiltBeforeLocal); Assert.Equal("2026-10-13T10:45", body.TiltAfterLocal); Assert.Equal("2026-10-13T10:45", body.Local);
            Assert.Contains("\"audio_id\":\"b21\"", json);
            var skipped = AtAudio(); skipped.SkipAudio(0);
            Assert.Contains("\"audio_id\":null", Json.Write(skipped.RateAgain(3, Tap1)));
            Assert.NotEqual(id, skipped.RequestId);
            // Another zone, another stamp.
            var utc = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(812, -1250), TimeZoneInfo.Utc); utc.RateBefore(5, Tap1);
            Assert.Equal("2026-10-13T14:42", utc.Body(Tap1).TiltBeforeLocal);
        }
        [Fact]
        public void S7_AfterTextFollowsTheBandAndTheSecondResetRule()
        {
            var c = Fixtures.Catalog();
            Assert.Equal(c.After.Green, ResetSession.AfterText(c, 3, 0)); Assert.Equal("g", ResetSession.AfterBand(c, 1, 5));
            Assert.Equal(c.After.Orange, ResetSession.AfterText(c, 4, 0)); Assert.Equal(c.After.Orange, ResetSession.AfterText(c, 6, 3)); Assert.Equal("o", ResetSession.AfterBand(c, 5, 0));
            Assert.Equal(c.After.OrangeTwice, ResetSession.AfterText(c, 5, 4)); Assert.Equal("r", ResetSession.AfterBand(c, 5, 6));
            Assert.Equal(c.After.Red, ResetSession.AfterText(c, 7, 0)); Assert.Equal(c.After.Red, ResetSession.AfterText(c, 10, 9)); Assert.Equal("r", ResetSession.AfterBand(c, 8, 0));
            var s = AtAudio(); s.FinishAudio(); Assert.Equal(c.After.Green, s.AfterText(2, 0));
        }
        [Fact]
        public void S8_InterruptibleOnlyAtStepOneUntouched()
        {
            var s = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(812), Toronto);
            Assert.True(s.Interruptible);
            s.RateBefore(2, Tap1); Assert.False(s.Interruptible);
            Assert.False(Started(tilt: 8).Interruptible);
            var done = AtAudio(); done.FinishAudio(); done.RateAgain(3, Tap1); Assert.False(done.Interruptible);
        }
        [Fact]
        public void S9_TheTriggerComesFromThePnlAndTheThoughtsEndWithSomethingElse()
        {
            Assert.Equal("Stop loss", new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(1, -50), Toronto).Trigger.Label);
            Assert.Equal("Winning trade", new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(1, 50), Toronto).Trigger.Label);
            var be = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(1, 0), Toronto);
            Assert.Equal("Break-even trade", be.Trigger.Label); Assert.Equal("What happened after your break-even exit?", be.Trigger.Question);
            Assert.Equal(new[] { "be_target", "other" }, be.Thoughts.Select(x => x.Key).ToArray());
            Assert.Equal("Something else", be.Thoughts.Last().Text);
            Assert.True(be.RateBefore(4, Tap1)); Assert.True(be.PickThought(be.Thoughts[1])); Assert.True(be.PickLine(be.Thought.Lines[0]));
            Assert.Equal("b16", be.Line.Audio); Assert.True(be.FinishAudio());
            var body = be.RateAgain(2, Tap1); Assert.Equal("other", body.Thought); Assert.Equal("other.1", body.LogicId); Assert.Equal("b16", body.AudioId);
            // A catalog without the trigger: nothing can start.
            var c = Fixtures.Catalog(); c.Triggers.RemoveAll(x => x.Key == "stop_loss");
            var none = new ResetSession(c, Fixtures.TodayTrade(1, -50), Toronto);
            Assert.Null(none.Trigger); Assert.Empty(none.Thoughts); Assert.False(none.RateBefore(3, Tap1));
        }
        [Fact]
        public void S10_StepLinesAndLabels()
        {
            Assert.Equal("Step 1 of 5 — Rate your tilt", UiText.StepLine(1));
            Assert.Equal("Step 3 of 5 — What you feel", UiText.StepLine(3));
            Assert.Equal("Step 4 of 5 — Reprogram Logic", UiText.StepLine(4));
            Assert.Equal("Step 5 of 5 — Rate tilt again", UiText.StepLine(5));
            Assert.Equal("Step 5 of 5 — Rate tilt again", UiText.StepLine(ResetSession.Saved));
            Assert.Equal("4 · Tightening up", UiText.TiltChoice(4, "Tightening up"));
            Assert.Equal("4 · Tightening up — I keep replaying the result.", UiText.TiltTip(4, "Tightening up", "I keep replaying the result."));
            Assert.Equal("What happened: Stop loss", UiText.WhatHappened("Stop loss"));
        }
    }
}
