// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public enum RowState { Reset, Saving, Logged }

    public sealed class TodayRow
    {
        public TodayTrade Trade { get; set; }
        public RowState State { get; set; }
        // Arrived while the window was open: shows the "new" tag.
        public bool IsNew { get; set; }
    }

    // The Today list and the small texts of the reset panel that come from numbers, not from the catalog.
    public static class TodayRules
    {
        public const int BreathRounds = 3;
        public const double BreathRoundSec = 12;   // in 4, out 8
        public const double TimerBreathLeadSec = 4; // without the voice the breathing starts after a short "Get ready"

        // Newest first. A trade with an entry on the site = Logged; one waiting in resets.jsonl = Saving; else Reset.
        public static List<TodayRow> Rows(TodayAnswer answer, ICollection<long> pendingTradeIds, ICollection<long> newIds)
        {
            var rows = new List<TodayRow>();
            if (answer == null || answer.Trades == null) return rows;
            foreach (var trade in answer.Trades.Where(x => x != null && x.Id > 0).OrderByDescending(x => x.ExitTime).ThenByDescending(x => x.Id))
            {
                var state = trade.EntryId.HasValue && trade.EntryId.Value > 0 ? RowState.Logged
                    : pendingTradeIds != null && pendingTradeIds.Contains(trade.Id) ? RowState.Saving : RowState.Reset;
                rows.Add(new TodayRow { Trade = trade, State = state, IsNew = newIds != null && newIds.Contains(trade.Id) });
            }
            return rows;
        }

        public static string RowText(TodayTrade t, TimeZoneInfo zone)
        { return Helpers.LocalClock(t.ExitTime, zone) + " · " + t.Instrument + " · " + SideText(t) + " · " + AmountText(t); }
        public static string ResetHeader(TodayTrade t, TimeZoneInfo zone)
        { return "Reset · " + t.Instrument + " · " + SideText(t) + " · " + AmountText(t) + " · " + Helpers.LocalClock(t.ExitTime, zone); }
        public static string NewTradeNote(TodayTrade t) { return "New trade: " + t.Instrument + " " + AmountText(t); }
        public static string TodayTitle(long nowMs, TimeZoneInfo zone)
        { return "Today · " + TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), zone).ToString("MMM d", CultureInfo.InvariantCulture); }
        public static string SideText(TodayTrade t)
        {
            string side = t.Side ?? "";
            if (side.Length != 0) side = char.ToUpperInvariant(side[0]) + side.Substring(1);
            return side + " " + t.Qty.ToString(CultureInfo.InvariantCulture);
        }
        // +212.50 / −62.50 CAD / 0 (the code only for non-USD).
        public static string AmountText(TodayTrade t)
        {
            if (t.PnlCents == 0) return "0";
            string text = (t.PnlCents > 0 ? "+" : "−") + (Math.Abs(t.PnlCents) / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            return t.Currency == null || t.Currency == "USD" ? text : text + " " + t.Currency;
        }
        // Same rule as the site's tradeTrigger: the P/L decides "What happened".
        public static string TriggerFor(long pnlCents) { return pnlCents < 0 ? "stop_loss" : pnlCents > 0 ? "winning_trade" : "break_even"; }

        // Which of the 7 parts plays at this audio second (marks.steps are the part starts).
        public static int PartAt(CatalogMarks marks, double sec)
        {
            if (marks == null || marks.Steps == null || marks.Steps.Count == 0) return 0;
            int i = 0;
            while (i < marks.Steps.Count - 1 && sec >= marks.Steps[i + 1]) i++;
            return i;
        }
        public static double PartStart(CatalogMarks marks, int i) { return marks.Steps[Math.Max(0, Math.Min(i, marks.Steps.Count - 1))]; }
        public static double PartEnd(CatalogMarks marks, int i) { return i < marks.Steps.Count - 1 ? marks.Steps[i + 1] : marks.End; }

        // Timer mode: the parts follow their own seconds (5, 10, 40, 25, 15, 8, 18 = 121 s).
        public static int TimerPartAt(IList<CatalogPart> parts, double sec)
        {
            if (parts == null || parts.Count == 0) return 0;
            double start = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                start += parts[i].Sec;
                if (sec < start) return i;
            }
            return parts.Count - 1;
        }
        public static double TimerPartStart(IList<CatalogPart> parts, int i)
        {
            double start = 0;
            for (int k = 0; k < i && k < parts.Count; k++) start += parts[k].Sec;
            return start;
        }
        public static double TimerTotal(IList<CatalogPart> parts) { return parts == null ? 0 : parts.Sum(x => x.Sec); }

        // Breath round 1–3 from the seconds inside the BREATHE part; 0 = "Get ready". With the voice each round
        // starts at its mark (marks.breaths, seconds inside the part).
        public static int BreathRound(CatalogMarks marks, double secInPart)
        {
            if (marks == null || marks.Breaths == null || marks.Breaths.Count == 0) return TimerBreathRound(secInPart);
            if (secInPart < marks.Breaths[0]) return 0;
            int r = 0;
            while (r < marks.Breaths.Count - 1 && secInPart >= marks.Breaths[r + 1]) r++;
            return Math.Min(BreathRounds, r + 1);
        }
        public static int TimerBreathRound(double secInPart)
        {
            double t = secInPart - TimerBreathLeadSec;
            if (t < 0) return 0;
            return Math.Min(BreathRounds, (int)(t / BreathRoundSec) + 1);
        }
        // "1:52 left"
        public static string LeftText(double secondsLeft)
        {
            int whole = (int)Math.Max(0, Math.Ceiling(secondsLeft));
            return (whole / 60).ToString(CultureInfo.InvariantCulture) + ":" + (whole % 60).ToString("00", CultureInfo.InvariantCulture) + " left";
        }
        // g ≤ 3, o 4–6, r 7+ (the red line comes from the catalog rules).
        public static string Band(int n, int redLine) { return n >= redLine ? "r" : n >= 4 ? "o" : "g"; }
    }
}
