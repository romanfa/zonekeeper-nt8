// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public enum AudioMode { None, Voice, Timer }

    // One reset for one trade: the 5 steps, what each tap does, and the body to save. The WPF panel only shows it.
    // Steps: 1 rate tilt → (2 what happened: no tap, shown with step 3) → 3 pick a thought → 4 pick a line, the audio
    // plays → 5 rate again. The step-5 tap saves; nothing is saved before it.
    public sealed class ResetSession
    {
        public const int Saved = 6;
        public Catalog Catalog { get; private set; }
        public TodayTrade Trade { get; private set; }
        public TimeZoneInfo Zone { get; private set; }
        // Fixed at creation: a retry of the same save gives the same row on the site.
        public string RequestId { get; private set; }
        public int Step { get; private set; }
        public CatalogTrigger Trigger { get; private set; }
        public int TiltBefore { get; private set; }
        public long TiltBeforeMs { get; private set; }
        // Tilt 7+ at step 1: the red box is open until "Do a reset anyway" (or the panel closes).
        public bool RedLineOpen { get; private set; }
        public CatalogThought Thought { get; private set; }
        public CatalogLine Line { get; private set; }
        public AudioMode AudioMode { get; private set; }
        // Part index when "Skip audio" was tapped; -1 = not skipped.
        public int SkippedAtPart { get; private set; }
        public bool AudioEnded { get; private set; }
        public int TiltAfter { get; private set; }

        public ResetSession(Catalog catalog, TodayTrade trade, TimeZoneInfo zone)
        {
            Catalog = catalog; Trade = trade; Zone = zone ?? TimeZoneInfo.Local;
            RequestId = Guid.NewGuid().ToString("N"); Step = 1; SkippedAtPart = -1;
            Trigger = catalog == null ? null : catalog.Trigger(TodayRules.TriggerFor(trade.PnlCents));
        }

        // A newer trade may take the panel only while nothing was tapped yet.
        public bool Interruptible { get { return Step == 1 && TiltBefore == 0; } }
        public bool RedLineAtStart { get { return TiltBefore >= Catalog.RedLine; } }
        public bool AudioPhase { get { return Step == 4 && Line != null; } }
        public bool CanRateAgain { get { return Step == 5; } }
        public bool IsSaved { get { return Step == Saved; } }
        public List<CatalogThought> Thoughts
        {
            get
            {
                var list = new List<CatalogThought>();
                if (Trigger == null) return list;
                if (Trigger.Thoughts != null) list.AddRange(Trigger.Thoughts);
                if (Trigger.Other != null) list.Add(Trigger.Other);
                return list;
            }
        }

        public bool RateBefore(int n, long nowMs)
        {
            if (Step != 1 || n < 1 || n > 10 || Trigger == null) return false;
            TiltBefore = n; TiltBeforeMs = nowMs; RedLineOpen = n >= Catalog.RedLine; Step = 3;
            return true;
        }
        public bool ContinueAnyway()
        {
            if (!RedLineOpen) return false;
            RedLineOpen = false; return true;
        }
        public bool PickThought(CatalogThought thought)
        {
            if (Step != 3 || RedLineOpen || thought == null || !Thoughts.Contains(thought)) return false;
            Thought = thought; Step = 4; return true;
        }
        public bool PickLine(CatalogLine line)
        {
            if (Step != 4 || Line != null || line == null || Thought.Lines == null || !Thought.Lines.Contains(line)) return false;
            Line = line; return true;
        }
        public void VoiceStarted() { if (AudioPhase) AudioMode = AudioMode.Voice; }
        public void TimerStarted() { if (AudioPhase) AudioMode = AudioMode.Timer; }
        // The voice ended (or the timer ran out): step 5 opens.
        public bool FinishAudio()
        {
            if (!AudioPhase) return false;
            AudioEnded = true; Step = 5; return true;
        }
        // "Skip audio": step 5 opens at once. Skipped before part 2 started = saved without an audio id.
        public bool SkipAudio(int partIndex)
        {
            if (!AudioPhase) return false;
            SkippedAtPart = Math.Max(0, partIndex); Step = 5; return true;
        }
        public string AudioIdToSave() { return Line == null || SkippedAtPart == 0 ? null : Line.Audio; }
        // The step-5 tap: the body to save, at once. No extra step.
        public ResetBody RateAgain(int n, long nowMs)
        {
            if (!CanRateAgain || n < 1 || n > 10) return null;
            TiltAfter = n; Step = Saved;
            return Body(nowMs);
        }
        public ResetBody Body(long nowMs)
        {
            return new ResetBody {
                RequestId = RequestId, TradeId = Trade.Id, Thought = Thought == null ? null : Thought.Key, LogicId = Line == null ? null : Line.Id,
                AudioId = AudioIdToSave(), TiltBefore = TiltBefore, TiltBeforeLocal = Helpers.LocalStamp(TiltBeforeMs, Zone),
                TiltAfter = TiltAfter, TiltAfterLocal = Helpers.LocalStamp(nowMs, Zone), Local = Helpers.LocalStamp(nowMs, Zone), TappedAt = nowMs };
        }
        // The rule text after the step-5 tap. previousAfter = the last saved tilt_after of this NinjaTrader run (0 = none).
        public string AfterText(int n, int previousAfter) { return AfterText(Catalog, n, previousAfter); }
        public static string AfterText(Catalog catalog, int n, int previousAfter)
        {
            if (n <= 3) return catalog.After.Green;
            if (n >= catalog.RedLine) return catalog.After.Red;
            return previousAfter >= 4 ? catalog.After.OrangeTwice : catalog.After.Orange;
        }
        public static string AfterBand(Catalog catalog, int n, int previousAfter)
        {
            if (n <= 3) return "g";
            if (n >= catalog.RedLine || previousAfter >= 4) return "r";
            return "o";
        }
    }
}
