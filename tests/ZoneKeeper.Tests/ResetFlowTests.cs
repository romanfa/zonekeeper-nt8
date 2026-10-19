using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // The reset window's choices without WPF or WebView2: login, retry, which trade, what goes to the page.
    public sealed class ResetFlowTests
    {
        private const string Base = Helpers.LiveBaseUrl;
        private const string Page = "https://getzonekeeper.com/app/#today";
        private const string Ready = "{\"type\":\"ready\"}";

        private sealed class FakeHost : IResetHost
        {
            public readonly Queue<HandoffResult> Answers = new Queue<HandoffResult>();
            public readonly List<string> Navigated = new List<string>();
            public readonly List<LoginRequest> Logins = new List<LoginRequest>();
            public readonly List<string> Posted = new List<string>();
            public readonly List<string> Logs = new List<string>();
            public readonly List<ResetView> Views = new List<ResetView>();
            public readonly List<long> Delays = new List<long>();
            public int Handoffs;
            public bool BlockDelays;
            public Task<HandoffResult> HandoffAsync()
            { Handoffs++; return Task.FromResult(Answers.Count == 0 ? Ok() : Answers.Dequeue()); }
            public void OpenLogin(LoginRequest request) { Navigated.Add(request.Url); Logins.Add(request); }
            public void Post(string json) { Posted.Add(json); }
            public void ShowStatus(ResetView view, long retryMs) { Views.Add(view); }
            public Task DelayAsync(long ms, CancellationToken cancellation)
            { Delays.Add(ms); return BlockDelays ? Task.Delay(Timeout.Infinite, cancellation) : Task.FromResult(0); }
            public void Log(string message) { Logs.Add(message); }
        }
        private static HandoffResult Ok() { return new HandoffResult { Status = HandoffStatus.Ok, Handoff = Fixtures.HandoffKey }; }
        private static HandoffResult Fail(HandoffStatus status) { return HandoffResult.Of(status); }
        private static string Login(long trade) { return ResetRules.LoginRequest(Base, Fixtures.HandoffKey, trade).Url; }

        [Fact]
        public async Task FirstTradeRidesInTheLoginLinkAndIsNotSentAgain()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            Assert.Equal(ResetView.Starting, flow.View);
            await flow.BrowserReadyAsync();
            Assert.Equal(new[] { Login(812) }, host.Navigated); Assert.Equal(ResetView.Page, flow.View);
            // The key goes in the header and the body of a POST, never in the URL.
            var login = host.Logins[0];
            Assert.Equal("POST", login.Method); Assert.Equal("https://getzonekeeper.com/auth/addon?trade=812", login.Url);
            Assert.DoesNotContain(Fixtures.HandoffKey, login.Url);
            Assert.Contains("X-ZK-Handoff: " + Fixtures.HandoffKey, login.Headers); Assert.Equal("h=" + Fixtures.HandoffKey, login.Body);
            Assert.Equal(NavigationChoice.Allow, flow.NavigationStarting(Login(812), false));
            flow.MessageReceived(Page, Ready);
            Assert.Empty(host.Posted); Assert.Equal(1, host.Handoffs);
        }
        [Fact]
        public async Task TwoTradesBeforeTheBrowserIsReadyShowTheNewest()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 811);
            await flow.WantAsync(812);
            await flow.BrowserReadyAsync();
            Assert.Equal(new[] { Login(812) }, host.Navigated);
        }
        [Fact]
        public async Task NewTradesGoToThePageAfterReadyNewestOnly()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            await flow.WantAsync(813); await flow.WantAsync(814);
            Assert.Empty(host.Posted);
            flow.MessageReceived(Page, Ready);
            Assert.Equal(new[] { "{\"type\":\"trade\",\"id\":814}" }, host.Posted);
            // Window open and page ready: the next trade goes at once, no new window and no new login.
            await flow.WantAsync(815);
            Assert.Equal(new[] { "{\"type\":\"trade\",\"id\":814}", "{\"type\":\"trade\",\"id\":815}" }, host.Posted);
            Assert.Single(host.Navigated); Assert.Equal(1, host.Handoffs);
            // The button (id 0) on an open window does nothing to the page.
            await flow.WantAsync(0);
            Assert.Equal(2, host.Posted.Count);
        }
        [Fact]
        public async Task ANewSitePageWaitsForItsOwnReady()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 0);
            await flow.BrowserReadyAsync();
            Assert.Equal(new[] { Login(0) }, host.Navigated);
            flow.MessageReceived(Page, Ready);
            Assert.Equal(NavigationChoice.Allow, flow.NavigationStarting("https://getzonekeeper.com/app/", true));
            await flow.WantAsync(900);
            Assert.Empty(host.Posted);
            flow.MessageReceived(Page, Ready);
            Assert.Equal(new[] { "{\"type\":\"trade\",\"id\":900}" }, host.Posted);
        }
        [Fact]
        public async Task MessagesFromOtherSitesOrJunkAreIgnored()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 1);
            await flow.BrowserReadyAsync();
            await flow.WantAsync(2);
            flow.MessageReceived("https://evil.example.com/", Ready);
            flow.MessageReceived(Page, "{\"type\":\"trade\",\"id\":3}");
            flow.MessageReceived(Page, "junk");
            flow.MessageReceived(null, null);
            Assert.Empty(host.Posted);
        }
        [Fact]
        public async Task NotPaidShowsItsTextAndDoesNotRetryInALoop()
        {
            var host = new FakeHost(); host.Answers.Enqueue(Fail(HandoffStatus.NotPaid));
            var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            Assert.Equal(ResetView.NotPaid, flow.View); Assert.Equal(1, host.Handoffs);
            Assert.Empty(host.Navigated); Assert.Empty(host.Delays);
            // A new trade does not hammer the server either: one try per request.
            host.Answers.Enqueue(Fail(HandoffStatus.NotPaid));
            await flow.WantAsync(813);
            Assert.Equal(2, host.Handoffs); Assert.Equal(ResetView.NotPaid, flow.View);
            // After renewing, the next request logs in with the newest trade.
            await flow.WantAsync(0);
            Assert.Equal(new[] { Login(813) }, host.Navigated);
        }
        [Fact]
        public async Task NoInternetKeepsTheWindowAndRetriesWithBackoff()
        {
            var host = new FakeHost();
            host.Answers.Enqueue(Fail(HandoffStatus.Offline)); host.Answers.Enqueue(Fail(HandoffStatus.Offline)); host.Answers.Enqueue(Fail(HandoffStatus.Offline));
            var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            Assert.Equal(new long[] { 5000, 15000, 30000 }, host.Delays);
            Assert.Equal(4, host.Handoffs); Assert.Equal(new[] { Login(812) }, host.Navigated);
            Assert.Equal(ResetView.Page, flow.View);
            Assert.Contains(ResetView.NoAnswer, host.Views);
            Assert.Equal(1, host.Logs.Count(x => x == UiText.ResetNoAnswer));
        }
        [Fact]
        public async Task BusyServerRetryAfterIsRespected()
        {
            var host = new FakeHost(); host.Answers.Enqueue(new HandoffResult { Status = HandoffStatus.Offline, RetryAfter = TimeSpan.FromSeconds(120) });
            var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            Assert.Equal(new long[] { 120000 }, host.Delays); Assert.Single(host.Navigated);
        }
        [Fact]
        public async Task ATradeThatArrivesDuringARetryWaitRidesInTheNextLogin()
        {
            var host = new FakeHost { BlockDelays = true }; host.Answers.Enqueue(Fail(HandoffStatus.Offline));
            var flow = new ResetFlow(Base, host, 812);
            var login = flow.BrowserReadyAsync();
            Assert.Equal(ResetView.NoAnswer, flow.View);
            await flow.WantAsync(813);
            Assert.Equal(1, host.Handoffs);
            // Closing the window (= skip) stops the retries.
            flow.Close();
            await login;
            Assert.Equal(1, host.Handoffs); Assert.Empty(host.Navigated);
            await flow.WantAsync(814);
            Assert.Equal(1, host.Handoffs);
        }
        [Theory]
        [InlineData(HandoffStatus.NotConnected, ResetView.ConnectFirst)]
        [InlineData(HandoffStatus.Unsupported, ResetView.Unsupported)]
        public async Task StopStatesShowTheirTextOnce(HandoffStatus status, ResetView view)
        {
            var host = new FakeHost(); host.Answers.Enqueue(Fail(status));
            var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            Assert.Equal(view, flow.View); Assert.Equal(1, host.Handoffs); Assert.Empty(host.Delays); Assert.Empty(host.Navigated);
        }
        [Fact]
        public async Task LoginPageGetsOneNewKeyThenStopsWithL3()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            Assert.Equal(NavigationChoice.LoginPage, flow.NavigationStarting("https://getzonekeeper.com/login/?state=expired", false));
            await flow.LoginPageAsync();
            // The trade that was in the failed link rides again.
            Assert.Equal(new[] { Login(812), Login(812) }, host.Navigated);
            await flow.LoginPageAsync();
            Assert.Equal(ResetView.LoginFailed, flow.View); Assert.Equal(2, host.Handoffs);
            Assert.Contains(UiText.ResetLoginFailed, host.Logs);
            // "Open reset window" tries again.
            await flow.WantAsync(0);
            Assert.Equal(3, host.Handoffs); Assert.Equal(3, host.Navigated.Count);
        }
        [Fact]
        public async Task AGoodPageResetsTheLoginTries()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 0);
            await flow.BrowserReadyAsync();
            await flow.LoginPageAsync();
            flow.MessageReceived(Page, Ready);
            await flow.LoginPageAsync();
            Assert.Equal(ResetView.Page, flow.View); Assert.Equal(3, host.Handoffs);
        }
        [Fact]
        public async Task APageThatDidNotLoadWaitsAndLogsInAgain()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            await flow.PageFailedAsync();
            Assert.Equal(new long[] { 5000 }, host.Delays);
            Assert.Equal(new[] { Login(812), Login(812) }, host.Navigated);
        }
        [Fact]
        public async Task PagesThatKeepFailingWaitLongerEachTimeUntilAGoodPage()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            await flow.BrowserReadyAsync();
            for (int i = 0; i < 5; i++) await flow.PageFailedAsync();
            Assert.Equal(new long[] { 5000, 15000, 30000, 60000, 60000 }, host.Delays);
            Assert.Equal(6, host.Handoffs);
            // A page that loads ("ready") starts the waits again from 5 s.
            flow.MessageReceived(Page, Ready);
            await flow.PageFailedAsync();
            Assert.Equal(5000, host.Delays.Last());
        }
        [Fact]
        public async Task BrowserFailureStopsWithItsText()
        {
            var host = new FakeHost(); var flow = new ResetFlow(Base, host, 812);
            flow.BrowserFailed();
            Assert.Equal(ResetView.BrowserFailed, flow.View);
            await flow.WantAsync(813);
            Assert.Equal(0, host.Handoffs);
        }
    }
}
