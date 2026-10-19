// WINDOWS ONLY: WPF + WebView2 (not in the Mac tests). All choices live in ResetFlow and ResetRules;
// this file only shows them. Every event handler has its own try/catch: nothing reaches NinjaTrader.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // One reset window for all trades: a new trade reuses it. It is made on a NinjaTrader UI dispatcher and
    // touched only on its own dispatcher. It never takes the keyboard focus by itself: ShowActivated = false,
    // and "raise" is Topmost on/off (Windows puts it on top of the chart without making it the active window).
    // Closing it = skip this reset; nothing opens it again until the next closed trade.
    public sealed class ResetWindow : NTWindow, IResetHost
    {
        private const int ShowNoActivate = 4; // SW_SHOWNOACTIVATE
        private static ResetWindow one;
        private static readonly object oneGate = new object();
        private readonly ResetOpen open;
        private readonly ResetFlow flow;
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        private readonly DockPanel layout = new DockPanel();
        private ResetBrowser browser;
        private bool closed;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        private ResetWindow(ResetOpen info, long tradeId)
        {
            open = info;
            Caption = "ZoneKeeper Reset";
            var work = SystemParameters.WorkArea;
            var place = ResetRules.Place(info.Bounds,
                new WindowBounds { Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop, Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight },
                new WindowBounds { Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height });
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = place.Left; Top = place.Top; Width = place.Width; Height = place.Height;
            MinWidth = ResetRules.MinWidth; MinHeight = ResetRules.MinHeight;
            ShowActivated = false;
            flow = new ResetFlow(info.BaseUrl, this, tradeId);
            DockPanel.SetDock(status, Dock.Top);
            layout.Children.Add(status);
            Content = layout;
            ((IResetHost)this).ShowStatus(flow.View, 0);
            Closed += WhenClosed;
        }

        // Any thread. tradeId 0 = the "Open reset window" button (it may take the focus: activate).
        public static void Request(long tradeId, ResetOpen info, bool activate)
        {
            try
            {
                ResetWindow window;
                lock (oneGate) window = one;
                if (window != null) { window.RunOnWindow(() => window.WantTrade(tradeId, activate)); return; }
                NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() => Create(tradeId, info, activate)));
            }
            catch (Exception ex) { ZkEngine.Note(UiText.ResetStartFailed(ex.GetType().Name)); }
        }

        // Any thread, never blocks: add-on stop, F5, Disconnect and a removed device key.
        public static void CloseIfOpen()
        {
            try
            {
                ResetWindow window;
                lock (oneGate) window = one;
                if (window != null) window.RunOnWindow(window.Close);
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not close the reset window", ex); }
        }

        private static void Create(long tradeId, ResetOpen info, bool activate)
        {
            try
            {
                // Checked outside oneGate: never take the engine lock while holding oneGate.
                if (!ZkEngine.Running) return;
                ResetWindow window = null, existing;
                lock (oneGate) existing = one;
                if (existing == null)
                {
                    // Missing DLLs or runtime stop here, before any window shows.
                    if (ResetBrowser.Prepare(info.LoaderFolder) == null) { ZkEngine.Note(UiText.ResetRuntimeMissing); return; }
                    lock (oneGate)
                    {
                        existing = one;
                        if (existing == null) { window = new ResetWindow(info, tradeId); one = window; }
                    }
                }
                if (window == null) { existing.RunOnWindow(() => existing.WantTrade(tradeId, activate)); return; }
                window.ShowQuietly(activate);
                // The engine may have stopped while the window was made (F5): then close it at once.
                if (!ZkEngine.Running) window.Close();
            }
            catch (Exception ex) { ZkEngine.Note(UiText.ResetStartFailed(ex.GetType().Name)); }
        }

        private void RunOnWindow(Action action)
        {
            Dispatcher.InvokeAsync(new Action(() => {
                try { if (!closed) action(); }
                catch (Exception ex) { ZkEngine.ReportError("Reset window error", ex); }
            }));
        }

        private void ShowQuietly(bool activate)
        {
            Show();
            RaiseQuietly(activate);
            Watch(StartBrowserAsync());
        }

        private void WantTrade(long tradeId, bool activate)
        {
            // A window whose browser could not start is replaced by a new one.
            if (flow.View == ResetView.BrowserFailed) { Close(); Request(tradeId, open, activate); return; }
            RaiseQuietly(activate);
            Watch(flow.WantAsync(tradeId));
        }

        private void RaiseQuietly(bool activate)
        {
            if (WindowState == WindowState.Minimized)
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero) ShowWindow(handle, ShowNoActivate);
            }
            Topmost = true; Topmost = false;
            if (activate) Activate();
        }

        private async Task StartBrowserAsync()
        {
            try
            {
                browser = new ResetBrowser(this);
                layout.Children.Add(browser.Control);
                await browser.StartAsync(open.UserDataFolder);
                if (closed) return;
                await flow.BrowserReadyAsync();
            }
            catch (Exception ex)
            {
                if (closed) return;
                ZkEngine.Note(UiText.ResetStartFailed(ex.GetType().Name));
                flow.BrowserFailed();
            }
        }

        private static void Watch(Task task)
        {
            task.ContinueWith(t => ZkEngine.ReportError("Reset window error", t.Exception.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);
        }

        // Called by ResetBrowser on this window's dispatcher.
        internal NavigationChoice PageNavigationStarting(string uri, bool userInitiated)
        {
            var choice = flow.NavigationStarting(uri, userInitiated);
            if (choice == NavigationChoice.LoginPage) Watch(flow.LoginPageAsync());
            else if (choice == NavigationChoice.OpenOutside) OpenOutside(uri);
            return choice;
        }

        internal void PageNavigationCompleted(bool success, bool canceled)
        {
            // A navigation we canceled ourselves is not a failure.
            if (!success && !canceled) Watch(flow.PageFailedAsync());
        }

        internal void PageNewWindow(string uri, bool userInitiated)
        {
            var choice = ResetRules.Navigation(open.BaseUrl, uri, userInitiated);
            if (choice == NavigationChoice.Allow && browser != null) browser.Navigate(uri);
            else if (choice == NavigationChoice.OpenOutside) OpenOutside(uri);
        }

        internal void PageMessage(string source, string json) { flow.MessageReceived(source, json); }

        internal void BrowserStopped(string kind)
        {
            if (!ResetRules.ClosesWindow(kind)) { ZkEngine.Note(UiText.ResetBrowserHiccup(kind)); return; }
            ZkEngine.Note(UiText.ResetBrowserStopped(kind));
            // Never close or dispose inside the WebView2 event itself: do it a moment later on the dispatcher.
            RunOnWindow(Close);
        }

        // Only https links after a click (ResetRules). Never logged: a link can hold private data.
        private static void OpenOutside(string uri)
        {
            Task.Run(() => {
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
                catch (Exception ex) { ZkEngine.ReportError("Could not open your browser", ex); }
            });
        }

        private void WhenClosed(object sender, EventArgs e)
        {
            try
            {
                closed = true;
                flow.Close();
                lock (oneGate) if (ReferenceEquals(one, this)) one = null;
                Rect place = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                if (browser != null) { browser.Dispose(); browser = null; }
                if (!place.IsEmpty) ZkEngine.SaveResetBounds(new WindowBounds { Left = place.Left, Top = place.Top, Width = place.Width, Height = place.Height });
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not close the reset window", ex); }
        }

        Task<HandoffResult> IResetHost.HandoffAsync() { return ZkEngine.HandoffAsync(); }
        void IResetHost.OpenLogin(LoginRequest request) { if (browser != null) browser.OpenLogin(request); }
        void IResetHost.Post(string json) { if (browser != null) browser.Post(json); }
        void IResetHost.ShowStatus(ResetView view, long retryMs)
        {
            status.Text = UiText.ResetStatus(view, retryMs);
            status.Visibility = view == ResetView.Page ? Visibility.Collapsed : Visibility.Visible;
        }
        Task IResetHost.DelayAsync(long ms, CancellationToken cancellation) { return Task.Delay(TimeSpan.FromMilliseconds(ms), cancellation); }
        void IResetHost.Log(string message) { ZkEngine.Note(message); }
    }

    // All WebView2 types live in this class, not in ResetWindow fields or signatures. So a missing
    // WebView2 DLL fails only when this class is used, inside a try/catch.
    internal sealed class ResetBrowser : IDisposable
    {
        private static bool loaderSet;
        private readonly ResetWindow owner;
        private readonly WebView2 view = new WebView2();
        private CoreWebView2Environment environment;
        private CoreWebView2 core;
        private bool disposed;

        public ResetBrowser(ResetWindow window) { owner = window; }
        public UIElement Control { get { return view; } }

        // Runs before the first WebView2 call. Returns the WebView2 Runtime version, or null when it is missing.
        public static string Prepare(string loaderFolder)
        {
            if (loaderFolder != null && !loaderSet) { loaderSet = true; SetLoaderFolder(loaderFolder); }
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(null); }
            catch (WebView2RuntimeNotFoundException) { return null; }
        }

        // SetLoaderDllFolderPath exists from SDK 1.0.1293.44. Reflection keeps this file compiling with an
        // older WebView2 copy shipped by NinjaTrader (path N), where the call is not needed.
        private static void SetLoaderFolder(string folder)
        {
            MethodInfo method = typeof(CoreWebView2Environment).GetMethod("SetLoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (method == null) return;
            // Already loaded (after F5, or by another add-on): the first folder stays.
            try { method.Invoke(null, new object[] { folder }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        }

        public async Task StartAsync(string userDataFolder)
        {
            var created = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            if (disposed) return;
            await view.EnsureCoreWebView2Async(created);
            environment = created;
            if (disposed) return;
            core = view.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.ProcessFailed += OnProcessFailed;
        }

        // POST /auth/addon with the one-use key in the X-ZK-Handoff header and the form body, never in the URL.
        // Never log the request or an exception message about it. (SDK 1.0.790.44 and newer have this API.)
        public void OpenLogin(LoginRequest login)
        {
            try
            {
                if (core == null || environment == null || login == null) return;
                var body = new MemoryStream(Encoding.UTF8.GetBytes(login.Body));
                var request = environment.CreateWebResourceRequest(login.Url, login.Method, body, login.Headers);
                core.NavigateWithWebResourceRequest(request);
            }
            catch (Exception ex) { ZkEngine.ReportError("Reset window could not open the page", ex); }
        }

        // Site pages without any key (links the member clicked).
        public void Navigate(string url)
        {
            try { if (core != null) core.Navigate(url); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window could not open the page", ex); }
        }

        public void Post(string json)
        {
            try { if (core != null) core.PostWebMessageAsJson(json); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window could not send the trade", ex); }
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try { if (owner.PageNavigationStarting(e.Uri, e.IsUserInitiated) != NavigationChoice.Allow) e.Cancel = true; }
            catch (Exception ex) { ZkEngine.ReportError("Reset window navigation", ex); try { e.Cancel = true; } catch (Exception) { } }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try { owner.PageNavigationCompleted(e.IsSuccess, e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window page load", ex); }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try { e.Handled = true; owner.PageNewWindow(e.Uri, e.IsUserInitiated); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window new window", ex); }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { owner.PageMessage(e.Source, e.WebMessageAsJson); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window message", ex); }
        }

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            try { e.State = CoreWebView2PermissionState.Deny; }
            catch (Exception ex) { ZkEngine.ReportError("Reset window permission", ex); }
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try { e.Cancel = true; }
            catch (Exception ex) { ZkEngine.ReportError("Reset window download", ex); }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try { owner.BrowserStopped(e.ProcessFailedKind.ToString()); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window browser stopped", ex); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (core != null)
                {
                    core.NavigationStarting -= OnNavigationStarting;
                    core.NavigationCompleted -= OnNavigationCompleted;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.WebMessageReceived -= OnWebMessageReceived;
                    core.PermissionRequested -= OnPermissionRequested;
                    core.DownloadStarting -= OnDownloadStarting;
                    core.ProcessFailed -= OnProcessFailed;
                    core = null;
                }
                environment = null;
            }
            catch (Exception ex) { ZkEngine.ReportError("Reset window cleanup", ex); }
            try { view.Dispose(); }
            catch (Exception ex) { ZkEngine.ReportError("Reset window cleanup", ex); }
        }
    }
}
