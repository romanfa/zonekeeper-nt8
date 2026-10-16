using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // No device credential, host object, script injection or completion bridge enters this browser.
    public sealed class ResetWindow : NTWindow
    {
        private readonly WebView2 browser = new WebView2();
        private readonly TextBlock status = new TextBlock { Text = "Starting browser...", TextWrapping = TextWrapping.Wrap };
        private readonly TextBox loginLink = new TextBox { MinWidth = 200, Margin = new Thickness(0, 4, 8, 4) };
        private readonly string origin, profile;
        private readonly Func<Task<string>> freshHandoff;
        private readonly Action dismissed;
        private CoreWebView2 core;
        private bool closed, automaticClose, requesting;
        public long TradeId { get; private set; }
        public ResetWindow(string origin, string profile, long tradeId, Func<Task<string>> freshHandoff, Action dismissed)
        {
            this.origin = origin; this.profile = profile; this.freshHandoff = freshHandoff; this.dismissed = dismissed; TradeId = tradeId;
            Caption = "ZoneKeeper reset"; Width = 760; Height = 850; MinWidth = 480; MinHeight = 500;
            var layout = new DockPanel(); var controls = new StackPanel { Margin = new Thickness(12) };
            controls.Children.Add(new TextBlock { Text = "Sign in to ZoneKeeper here. Closing keeps this reset pending.", TextWrapping = TextWrapping.Wrap });
            var retry = new Button { Content = "Open reset / retry", Margin = new Thickness(0, 4, 0, 4) };
            retry.Click += async delegate { await OpenAsync(); }; controls.Children.Add(retry);
            controls.Children.Add(new TextBlock { Text = "Fresh email login link (if needed):" });
            var login = new DockPanel(); var go = new Button { Content = "Open login link" };
            go.Click += delegate { string value = loginLink.Text.Trim(); loginLink.Clear(); Navigate(value); };
            DockPanel.SetDock(go, Dock.Right); login.Children.Add(go); login.Children.Add(loginLink); controls.Children.Add(login); controls.Children.Add(status);
            DockPanel.SetDock(controls, Dock.Top); layout.Children.Add(controls); layout.Children.Add(browser); Content = layout;
            Loaded += Initialize; Closed += DisposeBrowser;
        }
        public void CloseAutomatically() { automaticClose = true; Close(); }
        private async void Initialize(object sender, RoutedEventArgs args)
        {
            Loaded -= Initialize;
            try
            {
                try { CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "bin", "Custom")); }
                catch (InvalidOperationException) { }
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                if (closed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false; core.Settings.AreDevToolsEnabled = false;
                core.NavigationStarting += NavigationStarting; core.FrameNavigationStarting += NavigationStarting; core.NewWindowRequested += NewWindowRequested;
                core.PermissionRequested += PermissionRequested; core.DownloadStarting += DownloadStarting; core.NavigationCompleted += NavigationCompleted;
                await OpenAsync();
            }
            catch (Exception) { if (!closed) status.Text = "Browser could not start. Check WebView2 installation, then close and reopen pending resets."; }
        }
        private async Task OpenAsync()
        {
            if (closed || core == null || requesting) return;
            requesting = true;
            try
            {
                string url = await freshHandoff();
                if (closed) return;
                if (!ResetCoordinator.ValidHandoff(origin, url)) { status.Text = "Reset unavailable. Reconnect or use Open reset / retry."; return; }
                Navigate(url);
            }
            catch (Exception) { if (!closed) status.Text = "Reset unavailable. Use Open reset / retry."; }
            finally { requesting = false; }
        }
        private void Navigate(string url)
        {
            if (closed || core == null) return;
            if (!ResetCoordinator.SameOrigin(origin, url)) { status.Text = "Only links from this ZoneKeeper server can open here."; return; }
            try { core.Navigate(url); } catch (Exception) { status.Text = "Page could not load. Use Open reset / retry."; }
        }
        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e) { e.Cancel = !ResetCoordinator.SameOrigin(origin, e.Uri); }
        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; Navigate(e.Uri); }
        private void PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; }
        private void DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; }
        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e) { status.Text = e.IsSuccess ? "Complete the reset and select Done. Sign in here if prompted, then use Open reset / retry." : "Page could not load. Use Open reset / retry."; }
        private void DisposeBrowser(object sender, EventArgs args)
        {
            closed = true; Loaded -= Initialize; loginLink.Clear();
            if (core != null)
            {
                core.NavigationStarting -= NavigationStarting; core.FrameNavigationStarting -= NavigationStarting; core.NewWindowRequested -= NewWindowRequested;
                core.PermissionRequested -= PermissionRequested; core.DownloadStarting -= DownloadStarting; core.NavigationCompleted -= NavigationCompleted; core = null;
            }
            try { browser.Dispose(); } catch (Exception) { }
            if (!automaticClose) dismissed();
        }
    }
}
