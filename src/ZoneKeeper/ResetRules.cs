// PURE: no NinjaTrader, WPF or WebView2 types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public enum NavigationChoice { Allow, LoginPage, OpenOutside, Block }

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

    // What the reset window needs from the engine. Read on a background thread, never on the UI thread.
    public sealed class ResetOpen
    {
        public string BaseUrl { get; set; }
        public WindowBounds Bounds { get; set; }
        public string UserDataFolder { get; set; }
        public string LoaderFolder { get; set; }
    }

    [DataContract]
    public sealed class PageMessage
    {
        [DataMember(Name = "type")] public string Type { get; set; }
    }

    public static class ResetRules
    {
        // A trade is "fresh" for 10 minutes after its exit. Only fresh trades open the window.
        public const long FreshMs = 600000;
        public const int OpenedLimit = 200;
        public const double DefaultWidth = 460, DefaultHeight = 860, MinWidth = 380, MinHeight = 600;
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

        public static bool ValidHandoff(string handoff) { return Regex.IsMatch(handoff ?? "", @"\A[A-Za-z0-9_-]{43}\z"); }

        // The one-use login link for the window. Returns null for a bad key.
        public static string LoginUrl(string baseUrl, string handoff, long tradeId)
        {
            Uri site;
            if (!ValidHandoff(handoff) || !TrySite(baseUrl, out site)) return null;
            string url = site.GetLeftPart(UriPartial.Authority) + "/auth/addon?h=" + Uri.EscapeDataString(handoff);
            if (tradeId > 0) url += "&trade=" + tradeId.ToString(CultureInfo.InvariantCulture);
            return url;
        }

        // True only for the ZoneKeeper site itself: same scheme, host and port, no user name in the link.
        public static bool IsSiteUrl(string baseUrl, string uri)
        {
            Uri site, target;
            if (!TrySite(baseUrl, out site) || !Uri.TryCreate(uri ?? "", UriKind.Absolute, out target)) return false;
            return target.Scheme == site.Scheme && string.Equals(target.Host, site.Host, StringComparison.OrdinalIgnoreCase) &&
                target.Port == site.Port && target.UserInfo.Length == 0;
        }

        public static bool IsLoginPage(string baseUrl, string uri)
        {
            if (!IsSiteUrl(baseUrl, uri)) return false;
            string path = new Uri(uri).AbsolutePath;
            return path == "/login" || path.StartsWith("/login/", StringComparison.Ordinal);
        }

        // What the window does with a page link: site pages stay, the login page means "log in again",
        // other https links open in the member's normal browser only after a click; all else is blocked.
        public static NavigationChoice Navigation(string baseUrl, string uri, bool userInitiated)
        {
            if (IsLoginPage(baseUrl, uri)) return NavigationChoice.LoginPage;
            if (IsSiteUrl(baseUrl, uri)) return NavigationChoice.Allow;
            Uri target;
            if (userInitiated && Uri.TryCreate(uri ?? "", UriKind.Absolute, out target) && target.Scheme == Uri.UriSchemeHttps && target.UserInfo.Length == 0)
                return NavigationChoice.OpenOutside;
            return NavigationChoice.Block;
        }

        public static string TradeMessage(long tradeId)
        { return "{\"type\":\"trade\",\"id\":" + tradeId.ToString(CultureInfo.InvariantCulture) + "}"; }

        public static bool IsReady(string json)
        {
            PageMessage message;
            return Json.TryRead(json ?? "", out message) && message.Type == "ready";
        }

        // Wait before the next login try after no answer: 5 s, 15 s, 30 s, then every 60 s.
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

        // Path S puts WebView2Loader.dll into bin\Custom; then the window must be told where it is.
        // Path N (NinjaTrader ships WebView2) needs nothing: null keeps NinjaTrader's own search.
        public static string LoaderFolder(string customFolder, Func<string, bool> fileExists)
        {
            if (string.IsNullOrEmpty(customFolder)) return null;
            return fileExists(Path.Combine(customFolder, "WebView2Loader.dll")) ? customFolder : null;
        }

        // Only a dead main browser process ends the window. WebView2 restarts render, GPU and helper
        // processes by itself, and "unresponsive" passes when a busy PC catches up.
        public static bool ClosesWindow(string processFailedKind) { return processFailedKind == "BrowserProcessExited"; }

        // The browser profile lives in %LOCALAPPDATA%, not in Documents: OneDrive often syncs Documents,
        // and a synced WebView2 profile breaks (file locks, big cache, login cookie in the cloud).
        public static string ProfileFolder(string localAppData) { return Path.Combine(localAppData ?? "", "ZoneKeeper", "webview2"); }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool TrySite(string baseUrl, out Uri site)
        {
            site = null;
            string normalized;
            return Settings.TryBaseUrl(baseUrl, out normalized) && Uri.TryCreate(normalized, UriKind.Absolute, out site);
        }
    }
}
