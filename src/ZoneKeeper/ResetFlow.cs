// PURE: no NinjaTrader, WPF or WebView2 types (tests compile this file).
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public enum ResetView { Starting, Opening, Page, NoAnswer, NotPaid, ConnectFirst, LoginFailed, Unsupported, BrowserFailed }

    // The reset window (WPF + WebView2) does only what this interface says. All choices live in ResetFlow.
    public interface IResetHost
    {
        Task<HandoffResult> HandoffAsync();
        void Navigate(string url);
        void Post(string json);
        void ShowStatus(ResetView view, long retryMs);
        Task DelayAsync(long ms, CancellationToken cancellation);
        void Log(string message);
    }

    // The steps of one reset window: log in with a one-use key, open the trade, send newer trades to the page.
    // The window calls every method on its own UI thread, so there is no lock. No ConfigureAwait(false):
    // each await must come back to that UI thread.
    public sealed class ResetFlow
    {
        private static readonly Task Done = Task.FromResult(0);
        private readonly string baseUrl;
        private readonly IResetHost host;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private long waitingId;   // newest trade not yet on the page
        private long urlId;       // trade that rode in the last login link; the page opens it by itself
        private bool browserReady, pageReady, busy, stopped, closed;
        private int loginPages;
        public ResetView View { get; private set; }

        public ResetFlow(string siteBaseUrl, IResetHost resetHost, long firstTradeId)
        {
            baseUrl = siteBaseUrl; host = resetHost; waitingId = Math.Max(0, firstTradeId); View = ResetView.Starting;
        }

        // A new closed trade (id > 0) or the "Open reset window" button (0). The newest trade wins.
        public Task WantAsync(long tradeId)
        {
            if (closed) return Done;
            if (tradeId > 0) waitingId = tradeId;
            if (!browserReady || busy) return Done;     // the login that starts later or runs now takes the id
            if (stopped) { loginPages = 0; return LoginAsync(false); }  // asked again after "not paid", "connect first", ...
            if (pageReady) SendWaiting();               // else the page is loading: the id waits for "ready"
            return Done;
        }

        public Task BrowserReadyAsync()
        {
            if (closed || browserReady) return Done;
            browserReady = true;
            return LoginAsync(false);
        }

        public void BrowserFailed()
        {
            if (closed) return;
            stopped = true; Show(ResetView.BrowserFailed, 0);
        }

        public NavigationChoice NavigationStarting(string uri, bool userInitiated)
        {
            var choice = ResetRules.Navigation(baseUrl, uri, userInitiated);
            if (choice == NavigationChoice.Allow) pageReady = false;   // the new page says "ready" again
            return choice;
        }

        // The site sent the window to its login page: the key did not work. One new try, then stop.
        public Task LoginPageAsync()
        {
            if (closed || busy) return Done;
            KeepUrlTrade();
            loginPages++;
            if (loginPages >= 2) { stopped = true; Show(ResetView.LoginFailed, 0); host.Log(UiText.ResetLoginFailed); return Done; }
            return LoginAsync(false);
        }

        // The page did not load (no internet, server error). The used key is gone: wait, then log in again.
        public Task PageFailedAsync()
        {
            if (closed || busy || stopped) return Done;
            KeepUrlTrade();
            return LoginAsync(true);
        }

        public void MessageReceived(string source, string json)
        {
            if (closed || !ResetRules.IsSiteUrl(baseUrl, source) || !ResetRules.IsReady(json)) return;
            pageReady = true; loginPages = 0;
            SendWaiting();
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { closing.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void SendWaiting()
        {
            long id = waitingId; long fromUrl = urlId;
            waitingId = 0; urlId = 0;
            if (id > 0 && id != fromUrl) host.Post(ResetRules.TradeMessage(id));
        }

        private void KeepUrlTrade()
        {
            pageReady = false;
            if (waitingId == 0) waitingId = urlId;
            urlId = 0;
        }

        private void Show(ResetView view, long retryMs) { View = view; host.ShowStatus(view, retryMs); }

        private async Task LoginAsync(bool waitFirst)
        {
            if (busy || closed) return;
            busy = true; stopped = false;
            try
            {
                int attempt = 0;
                if (waitFirst)
                {
                    long wait = ResetRules.RetryDelayMs(attempt++, null);
                    Show(ResetView.NoAnswer, wait);
                    await host.DelayAsync(wait, closing.Token);
                }
                while (!closed)
                {
                    Show(ResetView.Opening, 0);
                    var result = await host.HandoffAsync() ?? HandoffResult.Of(HandoffStatus.Offline);
                    if (closed) return;
                    string url = result.Status == HandoffStatus.Ok ? ResetRules.LoginUrl(baseUrl, result.Handoff, waitingId) : null;
                    if (url != null)
                    {
                        urlId = waitingId; waitingId = 0; pageReady = false;
                        Show(ResetView.Page, 0);
                        host.Navigate(url);
                        return;
                    }
                    if (result.Status == HandoffStatus.NotPaid) { stopped = true; Show(ResetView.NotPaid, 0); return; }
                    if (result.Status == HandoffStatus.NotConnected) { stopped = true; Show(ResetView.ConnectFirst, 0); return; }
                    if (result.Status == HandoffStatus.Unsupported) { stopped = true; Show(ResetView.Unsupported, 0); return; }
                    // No answer, 429, 5xx or a bad key: keep the window and try again later.
                    if (attempt == 0) host.Log(UiText.ResetNoAnswer);
                    long delay = ResetRules.RetryDelayMs(attempt++, result.RetryAfter);
                    Show(ResetView.NoAnswer, delay);
                    await host.DelayAsync(delay, closing.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { busy = false; }
        }
    }
}
