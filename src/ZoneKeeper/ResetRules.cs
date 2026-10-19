// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Window place and size in WPF units.
    [DataContract]
    public sealed class WindowBounds
    {
        [DataMember(Name = "left")] public double Left { get; set; }
        [DataMember(Name = "top")] public double Top { get; set; }
        [DataMember(Name = "width")] public double Width { get; set; }
        [DataMember(Name = "height")] public double Height { get; set; }
        public WindowBounds Copy() { return (WindowBounds)MemberwiseClone(); }
    }

    public static class ResetRules
    {
        // A trade is "fresh" for 10 minutes after its exit. Only fresh trades raise the window.
        public const long FreshMs = 600000;
        public const int OpenedLimit = 200;
        public const double DefaultWidth = 480, DefaultHeight = 720, MinWidth = 400, MinHeight = 560;
        private static readonly long[] retryWaits = { 5000, 15000, 30000, 60000 };

        // Picks the trade to open after a good send: the newest fresh trade that the server saved
        // and that this run did not open yet. Trades closed before this NinjaTrader run never open.
        public static SavedTrade PickTrade(IList<TradeRecord> batch, TradesAnswer answer, long nowMs, long runStartedMs, ICollection<long> opened)
        {
            if (batch == null || answer == null || answer.Ids == null) return null;
            var rejected = new HashSet<int>();
            if (answer.Rejected != null) foreach (var item in answer.Rejected) if (item != null) rejected.Add(item.Index);
            SavedTrade best = null; long bestExit = 0;
            foreach (var item in answer.Ids)
            {
                if (item == null || item.Index < 0 || item.Index >= batch.Count || item.Id <= 0) continue;
                if (rejected.Contains(item.Index) || (opened != null && opened.Contains(item.Id))) continue;
                var trade = batch[item.Index];
                if (trade == null || trade.ExitTime < runStartedMs || nowMs - trade.ExitTime > FreshMs) continue;
                if (best == null || trade.ExitTime > bestExit || (trade.ExitTime == bestExit && item.Index > best.Index))
                { best = new SavedTrade { Index = item.Index, Id = item.Id }; bestExit = trade.ExitTime; }
            }
            return best;
        }

        // Wait before the next try after no answer: 5 s, 15 s, 30 s, then every 60 s.
        public static long RetryDelayMs(int attempt, TimeSpan? retryAfter)
        {
            long wait = retryWaits[Math.Max(0, Math.Min(attempt, retryWaits.Length - 1))];
            if (retryAfter.HasValue) wait = Math.Max(wait, (long)Math.Min(3600000, Math.Max(0, retryAfter.Value.TotalMilliseconds)));
            return wait;
        }

        public static bool ValidBounds(WindowBounds b)
        {
            return b != null && Finite(b.Left) && Finite(b.Top) && Math.Abs(b.Left) < 100000 && Math.Abs(b.Top) < 100000 &&
                b.Width >= MinWidth && b.Width <= 10000 && b.Height >= MinHeight && b.Height <= 10000;
        }

        // Saved place when its title bar is on a screen, else the right edge of the main work area.
        public static WindowBounds Place(WindowBounds saved, WindowBounds screen, WindowBounds workArea)
        {
            if (ValidBounds(saved) && screen != null)
            {
                double visibleWidth = Math.Min(saved.Left + saved.Width, screen.Left + screen.Width) - Math.Max(saved.Left, screen.Left);
                bool titleOnScreen = saved.Top >= screen.Top && saved.Top <= screen.Top + screen.Height - 40;
                if (visibleWidth >= 100 && titleOnScreen) return saved.Copy();
            }
            double height = workArea == null ? DefaultHeight : Math.Min(DefaultHeight, workArea.Height);
            double left = workArea == null ? 0 : workArea.Left + workArea.Width - DefaultWidth;
            double top = workArea == null ? 0 : workArea.Top;
            return new WindowBounds { Left = left, Top = top, Width = DefaultWidth, Height = height };
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
