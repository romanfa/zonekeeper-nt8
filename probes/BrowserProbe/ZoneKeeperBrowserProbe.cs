// Disposable host proof only. Never link this folder into the production add-on.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeperBrowserProof
{
    public sealed class ZoneKeeperBrowserProbe : AddOnBase
    {
        private readonly object gate = new object();
        private readonly Dictionary<ControlCenter, NTMenuItem> menus = new Dictionary<ControlCenter, NTMenuItem>();
        private BrowserProbeWindow window;
        private bool terminated;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ZoneKeeper browser probe";
                Description = "Isolated browser/login/audio proof; no trade integration";
            }
            else if (State == State.Terminated)
            {
                BrowserProbeWindow old;
                KeyValuePair<ControlCenter, NTMenuItem>[] oldMenus;
                lock (gate)
                {
                    terminated = true;
                    old = window;
                    oldMenus = new List<KeyValuePair<ControlCenter, NTMenuItem>>(menus).ToArray();
                    menus.Clear();
                }
                if (old != null) Dispatch(old, () => old.Close());
                foreach (var pair in oldMenus)
                {
                    var captured = pair;
                    Dispatch(captured.Key, () => RemoveMenu(captured.Key, captured.Value));
                }
            }
        }

        protected override void OnWindowCreated(Window created)
        {
            var cc = created as ControlCenter;
            if (cc == null) return;
            lock (gate)
            {
                if (terminated || menus.ContainsKey(cc)) return;
                var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
                if (parent == null) return;
                var menu = new NTMenuItem { Header = "ZoneKeeper browser probe",
                    Style = Application.Current.TryFindResource("MainMenuItem") as Style };
                menu.Click += Open;
                parent.Items.Add(menu);
                menus.Add(cc, menu);
            }
        }

        protected override void OnWindowDestroyed(Window destroyed)
        {
            var cc = destroyed as ControlCenter;
            if (cc == null) return;
            NTMenuItem menu;
            lock (gate)
            {
                if (!menus.TryGetValue(cc, out menu)) return;
                menus.Remove(cc);
            }
            RemoveMenu(cc, menu);
        }

        private void RemoveMenu(ControlCenter cc, NTMenuItem menu)
        {
            menu.Click -= Open;
            var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (parent != null) parent.Items.Remove(menu);
        }

        private void Open(object sender, RoutedEventArgs args)
        {
            // Creation can use any NT UI dispatcher, but activation belongs to the existing window.
            try
            {
                NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() => {
                    BrowserProbeWindow current;
                    bool created = false;
                    lock (gate)
                    {
                        if (terminated) return;
                        if (window == null)
                        {
                            current = new BrowserProbeWindow();
                            window = current;
                            current.Closed += delegate { lock (gate) if (ReferenceEquals(window, current)) window = null; };
                            created = true;
                        }
                        current = window;
                    }
                    Dispatch(current, () => {
                        lock (gate) if (terminated || !ReferenceEquals(window, current)) return;
                        if (created) current.Show();
                        else { if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal; current.Activate(); }
                    });
                }));
            }
            catch (Exception ex) { Diagnostic(ex); }
        }

        private static void Dispatch(Window target, Action action)
        {
            try { target.Dispatcher.InvokeAsync(new Action(() => { try { action(); } catch (Exception ex) { Diagnostic(ex); } })); }
            catch (Exception ex) { Diagnostic(ex); }
        }

        internal static void Diagnostic(Exception ex)
        {
            // A navigation exception can contain an email login token. Never log its message/URL.
            System.Diagnostics.Trace.WriteLine("ZoneKeeper browser probe: " + ex.GetType().Name + " " + ex.HResult.ToString("X8"));
        }
    }

    public sealed class BrowserProbeWindow : NTWindow
    {
        private const string Cabinet = "https://getzonekeeper.com/app/";
        private readonly WebView2 browser = new WebView2();
        private readonly TextBlock status = new TextBlock { Text = "Starting browser...", TextWrapping = TextWrapping.Wrap };
        private readonly TextBox loginLink = new TextBox { MinWidth = 200, Margin = new Thickness(0, 4, 8, 4),
            ToolTip = "Paste a fresh getzonekeeper.com email login link here. It opens in this browser." };
        private bool closed;
        private CoreWebView2 core;

        public BrowserProbeWindow()
        {
            Caption = "ZoneKeeper browser probe (no trade integration)";
            Width = 760; Height = 850; MinWidth = 480; MinHeight = 500;
            var layout = new DockPanel();
            var controls = new StackPanel { Margin = new Thickness(12) };
            controls.Children.Add(new TextBlock { Text = "Browser proof only. Test login and the audio library. No automatic resets.", TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Button("Open cabinet / retry", () => Navigate(Cabinet)));
            buttons.Children.Add(Button("Close probe", Close));
            controls.Children.Add(buttons);
            controls.Children.Add(new TextBlock { Text = "Email login link (if needed):" });
            var login = new DockPanel();
            var go = Button("Open login link", () => {
                string value = loginLink.Text.Trim();
                loginLink.Clear();
                Navigate(value);
            });
            DockPanel.SetDock(go, Dock.Right); login.Children.Add(go); login.Children.Add(loginLink);
            controls.Children.Add(login); controls.Children.Add(status);
            DockPanel.SetDock(controls, Dock.Top); layout.Children.Add(controls); layout.Children.Add(browser);
            Content = layout;
            Loaded += Initialize;
            Closed += DisposeBrowser;
        }

        private static Button Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(8, 4, 8, 4) };
            button.Click += delegate { action(); };
            return button;
        }

        private async void Initialize(object sender, RoutedEventArgs args)
        {
            Loaded -= Initialize;
            try
            {
                string custom = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "bin", "Custom");
                // NT's executable is outside Custom. Explicitly locate the x64 native loader.
                try { CoreWebView2Environment.SetLoaderDllFolderPath(custom); }
                catch (InvalidOperationException) { /* The loader is already loaded (reopen/F5/another host). */ }
                string profile = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "ZoneKeeper", "BrowserProbe");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                if (closed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.NavigationStarting += NavigationStarting;
                core.NavigationCompleted += NavigationCompleted;
                core.NewWindowRequested += NewWindowRequested;
                core.PermissionRequested += PermissionRequested;
                core.DownloadStarting += DownloadStarting;
                core.ProcessFailed += ProcessFailed;
                Navigate(Cabinet);
            }
            catch (Exception ex)
            {
                ZoneKeeperBrowserProbe.Diagnostic(ex);
                if (!closed) status.Text = "Browser initialization failed (" + ex.GetType().Name + "). Close and reopen after checking the SDK/runtime installation.";
            }
        }

        private static bool Allowed(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, "getzonekeeper.com", StringComparison.OrdinalIgnoreCase)
                && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo);
        }

        private void Navigate(string value)
        {
            if (closed) return;
            if (!Allowed(value)) { status.Text = "Only https://getzonekeeper.com links can open in this probe."; return; }
            if (core == null) { status.Text = "Browser is not ready. Wait, or close and reopen if initialization failed."; return; }
            try { core.Navigate(value); }
            catch (Exception ex) { ZoneKeeperBrowserProbe.Diagnostic(ex); status.Text = "Could not navigate. Close and reopen the probe."; }
        }

        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            args.Cancel = !Allowed(args.Uri);
            status.Text = args.Cancel ? "External navigation blocked in this probe." : "Loading ZoneKeeper...";
        }

        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            status.Text = args.IsSuccess ? "Page loaded. Browser PID: " + core.BrowserProcessId + ". Verify login and audio manually."
                : "Page load failed: " + args.WebErrorStatus + ". Use Open cabinet / retry.";
        }

        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs args)
        { args.Handled = true; Navigate(args.Uri); }
        private void PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs args)
        { args.State = CoreWebView2PermissionState.Deny; }
        private void DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs args)
        { args.Cancel = true; status.Text = "Downloads are disabled in this probe."; }
        private void ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs args)
        { status.Text = "Browser process failed: " + args.ProcessFailedKind + ". Close and reopen the probe."; }

        private void DisposeBrowser(object sender, EventArgs args)
        {
            closed = true;
            Loaded -= Initialize;
            loginLink.Clear();
            if (core != null)
            {
                core.NavigationStarting -= NavigationStarting;
                core.NavigationCompleted -= NavigationCompleted;
                core.NewWindowRequested -= NewWindowRequested;
                core.PermissionRequested -= PermissionRequested;
                core.DownloadStarting -= DownloadStarting;
                core.ProcessFailed -= ProcessFailed;
                core = null;
            }
            try { browser.Dispose(); }
            catch (Exception ex) { ZoneKeeperBrowserProbe.Diagnostic(ex); }
        }
    }
}
