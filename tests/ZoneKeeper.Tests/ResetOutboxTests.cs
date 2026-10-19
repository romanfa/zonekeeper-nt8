using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // resets.jsonl: finished resets waiting to be sent (plan 31 section 2.5).
    public sealed class ResetOutboxTests
    {
        private static ResetBody Body(long tradeId, string requestId = null)
        {
            return new ResetBody { RequestId = requestId ?? Guid.NewGuid().ToString("N"), TradeId = tradeId, Thought = "make_it_back", LogicId = "make_it_back.2", AudioId = "b21",
                TiltBefore = 5, TiltBeforeLocal = "2026-10-19T10:13", TiltAfter = 3, TiltAfterLocal = "2026-10-19T10:16", Local = "2026-10-19T10:16", TappedAt = Fixtures.Time };
        }
        [Fact]
        public void O1_AddWritesAndFlushesBeforeItReturns()
        {
            var files = new MemoryFiles(); bool writtenBeforeReturn = false;
            var box = new ResetOutbox("resets.jsonl", files, delegate { });
            files.BeforeWrite = path => { writtenBeforeReturn = path == "resets.jsonl"; };
            var body = Body(812, "3f2a" + new string('0', 28));
            Assert.True(box.Add(body, Fixtures.Time - 60000, Fixtures.Time));
            Assert.True(writtenBeforeReturn); Assert.Equal(1, files.Writes);
            string text = files.Read("resets.jsonl");
            Assert.EndsWith("\n", text); Assert.Single(text.TrimEnd('\n').Split('\n'));
            Assert.Contains("\"request_id\":\"3f2a", text); Assert.Contains("\"trade_id\":812", text); Assert.Contains("\"exit_time\":" + (Fixtures.Time - 60000), text);
            Assert.Contains("\"body\":{", text); Assert.Contains("\"logic_id\":\"make_it_back.2\"", text);
            // The same request id again is not a second line.
            Assert.True(box.Add(body, 0, Fixtures.Time)); Assert.Equal(1, box.Count); Assert.Equal(1, files.Writes);
            // Bad bodies are refused.
            Assert.False(box.Add(null, 0, 0)); Assert.False(box.Add(new ResetBody { RequestId = "", TradeId = 1 }, 0, 0)); Assert.False(box.Add(new ResetBody { RequestId = "x", TradeId = 0 }, 0, 0));
        }
        [Fact]
        public void O2_PeekIsOldestFirstAndRemoveRewritesTheFile()
        {
            var files = new MemoryFiles(); var box = new ResetOutbox("resets.jsonl", files, delegate { });
            var first = Body(812, "a" + new string('0', 31)); var second = Body(813, "b" + new string('0', 31));
            box.Add(first, 0, 1); box.Add(second, 0, 2);
            Assert.Equal(first.RequestId, box.Peek().RequestId); Assert.Equal(new long[] { 812, 813 }, box.PendingTradeIds.ToArray());
            Assert.True(box.Remove(first.RequestId)); Assert.Equal(second.RequestId, box.Peek().RequestId); Assert.Equal(1, box.Count);
            Assert.DoesNotContain("\"trade_id\":812", files.Read("resets.jsonl"));
            Assert.True(box.Remove("unknown")); Assert.Equal(1, box.Count);
            Assert.True(box.Remove(second.RequestId)); Assert.Null(box.Peek()); Assert.Equal("", files.Read("resets.jsonl")); Assert.Empty(box.PendingTradeIds);
            // A new engine reads what is left.
            box.Add(first, 0, 3);
            var again = new ResetOutbox("resets.jsonl", files, delegate { });
            Assert.Equal(1, again.Count); Assert.Equal(812, again.Peek().TradeId); Assert.Equal("make_it_back.2", again.Peek().Body.LogicId);
        }
        [Fact]
        public void O3_DamagedLinesAreSkippedWithOneLogLine()
        {
            var files = new MemoryFiles(); var logs = new List<string>();
            var good = Json.Write(new ResetLine { RequestId = "r1", TradeId = 812, ExitTime = 1, QueuedAt = 2, Body = Body(812, "r1") });
            files.Write("resets.jsonl", good + "\n{\"request_id\":\"r2\"\n" + "junk\n" + "{\"request_id\":\"r3\",\"trade_id\":5}\n" + good + "\n");
            var box = new ResetOutbox("resets.jsonl", files, logs.Add);
            Assert.Equal(1, box.Count); Assert.Equal(new[] { "Skipped 3 damaged reset lines." }, logs.ToArray());
        }
        [Fact]
        public void O4_CapIsOneHundredOldestDropped()
        {
            var files = new MemoryFiles(); var logs = new List<string>();
            var box = new ResetOutbox("resets.jsonl", files, logs.Add);
            for (int i = 1; i <= 101; i++) Assert.True(box.Add(Body(i, i.ToString("x32")), 0, i));
            Assert.Equal(100, box.Count); Assert.Equal(2, box.Peek().TradeId); Assert.Equal(new[] { UiText.ResetQueueFull }, logs.ToArray());
            // The same cap on load.
            files.Write("resets.jsonl", string.Join("\n", Enumerable.Range(1, 105).Select(i => Json.Write(new ResetLine { RequestId = i.ToString("x32"), TradeId = i, Body = Body(i, i.ToString("x32")) }))) + "\n");
            var loaded = new ResetOutbox("resets.jsonl", files, logs.Add);
            Assert.Equal(100, loaded.Count); Assert.Equal(6, loaded.Peek().TradeId);
        }
        [Fact]
        public void O5_AWriteFailureKeepsNothingAndSaysSo()
        {
            var files = new MemoryFiles { FailWrites = path => true }; var logs = new List<string>();
            var box = new ResetOutbox("resets.jsonl", files, logs.Add);
            Assert.False(box.Add(Body(812), 0, 1)); Assert.Equal(0, box.Count); Assert.Equal(new[] { UiText.SaveFailed }, logs.ToArray());
            files.FailWrites = null;
            Assert.True(box.Add(Body(812), 0, 1)); Assert.Equal(1, box.Count);
            files.FailWrites = path => true;
            Assert.False(box.Remove(box.Peek().RequestId)); Assert.Equal(1, box.Count); // still there: sent again later with the same request_id
            files.FailWrites = null;
            Assert.True(box.Remove(box.Peek().RequestId)); Assert.Equal(0, box.Count);
        }
    }
}
