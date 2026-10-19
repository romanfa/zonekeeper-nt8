// STEP 0 PROBE for add-on 0.2.0 (plan note 29, section 5). A separate test add-on: no trades, no device key,
// no ZoneKeeper login. It answers: which WebView2 DLLs does NinjaTrader load, does the page show, do web
// messages work, can a window come on top without taking the keyboard focus, does F5 close it cleanly.
// Copy only this file to <NinjaTrader 8>\bin\Custom\AddOns\ZoneKeeperBrowserProbe. Remove it after Step 0.
// Every line it shows is also written to <NinjaTrader 8>\ZoneKeeper\browser-probe.log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
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
            try
            {
                if (State == State.SetDefaults)
                {
                    Name = "ZoneKeeper browser probe";
                    Description = "Step 0 test for the ZoneKeeper reset window; no trades";
                }
                else if (State == State.Terminated)
                {
                    BrowserProbeWindow old;
                    var oldMenus = new List<KeyValuePair<ControlCenter, NTMenuItem>>();
                    lock (gate)
                    {
                        terminated = true;
                        old = window;
                        oldMenus.AddRange(menus);
                        menus.Clear();
                    }
                    if (old != null) Dispatch(old, () => { ProbeLog.Write("Add-on terminated (F5 or exit): closing the probe window."); old.Close(); });
                    foreach (var pair in oldMenus)
                    {
                        var captured = pair;
                        Dispatch(captured.Key, () => RemoveMenu(captured.Key, captured.Value));
                    }
                }
            }
            catch (Exception ex) { ProbeLog.Error("OnStateChange", ex); }
        }

        protected override void OnWindowCreated(Window created)
        {
            try
            {
                var cc = created as ControlCenter;
                if (cc == null) return;
                lock (gate)
                {
                    if (terminated || menus.ContainsKey(cc)) return;
                    var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
                    if (parent == null) return;
                    var menu = new NTMenuItem { Header = "ZoneKeeper browser probe", Style = Application.Current.TryFindResource("MainMenuItem") as Style };
                    menu.Click += Open;
                    parent.Items.Add(menu);
                    menus.Add(cc, menu);
                }
            }
            catch (Exception ex) { ProbeLog.Error("OnWindowCreated", ex); }
        }

        protected override void OnWindowDestroyed(Window destroyed)
        {
            try
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
            catch (Exception ex) { ProbeLog.Error("OnWindowDestroyed", ex); }
        }

        private void RemoveMenu(ControlCenter cc, NTMenuItem menu)
        {
            menu.Click -= Open;
            var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (parent != null) parent.Items.Remove(menu);
        }

        private void Open(object sender, RoutedEventArgs args)
        {
            try
            {
                NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() => {
                    try
                    {
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
                            if (created) current.Show();
                            else { if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal; current.Activate(); }
                        });
                    }
                    catch (Exception ex) { ProbeLog.Error("Open window", ex); }
                }));
            }
            catch (Exception ex) { ProbeLog.Error("Open", ex); }
        }

        private static void Dispatch(DispatcherObject target, Action action)
        {
            try { target.Dispatcher.InvokeAsync(new Action(() => { try { action(); } catch (Exception ex) { ProbeLog.Error("Dispatch", ex); } })); }
            catch (Exception ex) { ProbeLog.Error("Dispatch", ex); }
        }
    }

    internal static class ProbeLog
    {
        private static readonly object gate = new object();
        public static event Action<string> Line;
        public static string Folder { get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "ZoneKeeper"); } }
        public static void Error(string where, Exception ex) { Write("ERROR in " + where + ": " + ex.GetType().FullName + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture)); }
        public static void Write(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;
            var handler = Line;
            if (handler != null) try { handler(line); } catch (Exception) { }
            // File writes off the UI thread.
            Task.Run(() => {
                try { lock (gate) { Directory.CreateDirectory(Folder); File.AppendAllText(Path.Combine(Folder, "browser-probe.log"), line + Environment.NewLine, new UTF8Encoding(false)); } }
                catch (Exception) { }
            });
        }
    }

    public sealed class BrowserProbeWindow : NTWindow
    {
        private const string Site = "https://getzonekeeper.com/";
        private const int ShowNoActivate = 4; // SW_SHOWNOACTIVATE
        // A local page that sends every message back, so the web message path is tested without the site.
        private const string EchoPage = "<!doctype html><html><body style=\"font-family:sans-serif\"><h3>ZoneKeeper probe echo page</h3>" +
            "<p>Click \"Send test message\". The message comes back to the probe log.</p><script>" +
            "window.chrome.webview.addEventListener('message',function(e){window.chrome.webview.postMessage({type:'echo',data:e.data});});" +
            "window.chrome.webview.postMessage({type:'ready'});</script></body></html>";
        private static bool loaderSet;
        private readonly WebView2 browser = new WebView2();
        private readonly TextBox loaderFolder = new TextBox { MinWidth = 300, Margin = new Thickness(0, 2, 8, 2) };
        private readonly ListBox lines = new ListBox { Height = 220 };
        private CoreWebView2 core;
        private DispatcherTimer raiseTimer;
        private bool closed, starting;
        private int sent;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        public BrowserProbeWindow()
        {
            Caption = "ZoneKeeper browser probe (Step 0, no trades)";
            Width = 820; Height = 900; MinWidth = 500; MinHeight = 500;
            var top = new StackPanel { Margin = new Thickness(10) };
            top.Children.Add(new TextBlock { Text = "WebView2Loader.dll folder (empty = NinjaTrader's own). Used once per NinjaTrader start:", TextWrapping = TextWrapping.Wrap });
            top.Children.Add(loaderFolder);
            var row1 = new WrapPanel();
            row1.Children.Add(MakeButton("Start browser", () => { _ = StartAsync(); }));
            row1.Children.Add(MakeButton("Open getzonekeeper.com", () => Go(Site)));
            row1.Children.Add(MakeButton("Echo test page", ShowEchoPage));
            row1.Children.Add(MakeButton("Send test message", SendTestMessage));
            row1.Children.Add(MakeButton("Raise in 5 s", RaiseLater));
            row1.Children.Add(MakeButton("Write facts again", WriteFacts));
            row1.Children.Add(MakeButton("Close", Close));
            top.Children.Add(row1);
            top.Children.Add(lines);
            var layout = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(browser);
            Content = layout;
            ProbeLog.Line += AddLine;
            Closed += WhenClosed;
            loaderFolder.Text = SuggestedLoaderFolder();
            WriteFacts();
        }

        private static Button MakeButton(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(8, 4, 8, 4) };
            button.Click += delegate { try { action(); } catch (Exception ex) { ProbeLog.Error(label, ex); } };
            return button;
        }

        private void AddLine(string line)
        {
            try
            {
                Dispatcher.InvokeAsync(new Action(() => {
                    if (closed) return;
                    lines.Items.Insert(0, line);
                    while (lines.Items.Count > 200) lines.Items.RemoveAt(lines.Items.Count - 1);
                }));
            }
            catch (Exception) { }
        }

        private static string NtBin { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        private static string CustomBin { get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "bin", "Custom"); } }

        // Path S: our 3 files in bin\Custom and none in NinjaTrader's bin → point the loader there.
        private static string SuggestedLoaderFolder()
        {
            bool ntHasLoader = File.Exists(Path.Combine(NtBin, "WebView2Loader.dll")) || File.Exists(Path.Combine(NtBin, "runtimes", "win-x64", "native", "WebView2Loader.dll"));
            return !ntHasLoader && File.Exists(Path.Combine(CustomBin, "WebView2Loader.dll")) ? CustomBin : "";
        }

        private void WriteFacts()
        {
            ProbeLog.Write("NinjaTrader bin: " + NtBin + " | 64-bit process: " + Environment.Is64BitProcess);
            foreach (string folder in new[] { NtBin, Path.Combine(NtBin, "runtimes", "win-x64", "native"), CustomBin })
                foreach (string name in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", "WebView2Loader.dll" })
                {
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path)) ProbeLog.Write("File: " + path + " | file version " + FileVersionInfo.GetVersionInfo(path).FileVersion);
                }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName name = assembly.GetName();
                if (!name.Name.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase)) continue;
                string location = assembly.IsDynamic ? "(dynamic)" : assembly.Location;
                string fileVersion = string.IsNullOrEmpty(location) || assembly.IsDynamic ? "?" : FileVersionInfo.GetVersionInfo(location).FileVersion;
                ProbeLog.Write("Loaded assembly: " + name.Name + " " + name.Version + " | file version " + fileVersion + " | from " + location);
            }
            try
            {
                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                    if (string.Equals(module.ModuleName, "WebView2Loader.dll", StringComparison.OrdinalIgnoreCase))
                        ProbeLog.Write("Loaded native loader: " + module.FileName + " | file version " + module.FileVersionInfo.FileVersion);
            }
            catch (Exception ex) { ProbeLog.Error("Read process modules", ex); }
        }

        private async Task StartAsync()
        {
            if (core != null || starting || closed) { ProbeLog.Write("Browser already started."); return; }
            starting = true;
            try
            {
                string folder = loaderFolder.Text.Trim();
                if (folder.Length != 0 && !loaderSet)
                {
                    loaderSet = true;
                    MethodInfo method = typeof(CoreWebView2Environment).GetMethod("SetLoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (method == null) ProbeLog.Write("This WebView2 SDK has no SetLoaderDllFolderPath (it came in SDK 1.0.1293.44).");
                    else
                    {
                        try { method.Invoke(null, new object[] { folder }); ProbeLog.Write("Loader folder set: " + folder); }
                        catch (TargetInvocationException ex) { ProbeLog.Write("Loader folder not set (already loaded): " + ex.InnerException.GetType().Name); }
                    }
                }
                else ProbeLog.Write(folder.Length == 0 ? "Loader folder: NinjaTrader's own search." : "Loader folder was set before in this NinjaTrader start.");
                ProbeLog.Write("WebView2 Runtime version: " + CoreWebView2Environment.GetAvailableBrowserVersionString(null));
                string profile = Path.Combine(ProbeLog.Folder, "webview2-probe");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile, null);
                if (closed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                core = browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = true;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.NewWindowRequested += OnNewWindowRequested;
                core.WebMessageReceived += OnWebMessageReceived;
                core.PermissionRequested += OnPermissionRequested;
                core.ProcessFailed += OnProcessFailed;
                ProbeLog.Write("Browser ready. Browser version " + environment.BrowserVersionString + " | profile " + profile);
                WriteFacts();
                Go(Site);
            }
            catch (Exception ex) { ProbeLog.Error("Start browser", ex); }
            finally { starting = false; }
        }

        private static bool Allowed(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value ?? "", UriKind.Absolute, out uri)) return false;
            if (uri.Scheme == "about" || uri.Scheme == "data") return true;
            return uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "getzonekeeper.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443 && uri.UserInfo.Length == 0;
        }

        private void Go(string url)
        {
            if (core == null) { ProbeLog.Write("Press Start browser first."); return; }
            try { core.Navigate(url); }
            catch (Exception ex) { ProbeLog.Error("Navigate", ex); }
        }

        private void ShowEchoPage()
        {
            if (core == null) { ProbeLog.Write("Press Start browser first."); return; }
            try { core.NavigateToString(EchoPage); }
            catch (Exception ex) { ProbeLog.Error("Echo page", ex); }
        }

        private void SendTestMessage()
        {
            if (core == null) { ProbeLog.Write("Press Start browser first."); return; }
            sent++;
            string json = "{\"type\":\"probe\",\"n\":" + sent.ToString(CultureInfo.InvariantCulture) + "}";
            try { core.PostWebMessageAsJson(json); ProbeLog.Write("Sent: " + json); }
            catch (Exception ex) { ProbeLog.Error("Send test message", ex); }
        }

        // Click this, then click into Chart Trader or a chart and type. After 5 s the probe comes on top.
        // Expected: "active: False" and your typing still goes to NinjaTrader.
        private void RaiseLater()
        {
            if (raiseTimer != null) raiseTimer.Stop();
            raiseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            raiseTimer.Tick += delegate {
                try
                {
                    raiseTimer.Stop();
                    if (closed) return;
                    if (WindowState == WindowState.Minimized)
                    {
                        IntPtr handle = new WindowInteropHelper(this).Handle;
                        if (handle != IntPtr.Zero) ShowWindow(handle, ShowNoActivate);
                    }
                    Topmost = true; Topmost = false;
                    ProbeLog.Write("Raised (Topmost on/off, no Activate). Probe window active: " + IsActive + ".");
                }
                catch (Exception ex) { ProbeLog.Error("Raise", ex); }
            };
            raiseTimer.Start();
            ProbeLog.Write("Raise in 5 s: now click into Chart Trader or a chart and type.");
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                if (Allowed(e.Uri)) return;
                e.Cancel = true;
                Uri uri;
                ProbeLog.Write("Blocked a link to " + (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) ? uri.Host : "?"));
            }
            catch (Exception ex) { ProbeLog.Error("NavigationStarting", ex); }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try { ProbeLog.Write("Page loaded: " + e.IsSuccess + " | " + e.WebErrorStatus + " | browser process " + core.BrowserProcessId); }
            catch (Exception ex) { ProbeLog.Error("NavigationCompleted", ex); }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try { e.Handled = true; if (Allowed(e.Uri)) Go(e.Uri); }
            catch (Exception ex) { ProbeLog.Error("NewWindowRequested", ex); }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson ?? "";
                if (json.Length > 200) json = json.Substring(0, 200);
                ProbeLog.Write("Message from the page (" + (e.Source ?? "").Split('?')[0] + "): " + json);
            }
            catch (Exception ex) { ProbeLog.Error("WebMessageReceived", ex); }
        }

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            try { e.State = CoreWebView2PermissionState.Deny; ProbeLog.Write("Denied permission: " + e.PermissionKind); }
            catch (Exception ex) { ProbeLog.Error("PermissionRequested", ex); }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try { ProbeLog.Write("Browser process failed: " + e.ProcessFailedKind); }
            catch (Exception ex) { ProbeLog.Error("ProcessFailed", ex); }
        }

        private void WhenClosed(object sender, EventArgs args)
        {
            closed = true;
            try
            {
                ProbeLog.Line -= AddLine;
                if (raiseTimer != null) raiseTimer.Stop();
                if (core != null)
                {
                    core.NavigationStarting -= OnNavigationStarting;
                    core.NavigationCompleted -= OnNavigationCompleted;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.WebMessageReceived -= OnWebMessageReceived;
                    core.PermissionRequested -= OnPermissionRequested;
                    core.ProcessFailed -= OnProcessFailed;
                    core = null;
                }
                browser.Dispose();
                ProbeLog.Write("Probe window closed; browser disposed.");
            }
            catch (Exception ex) { ProbeLog.Error("Close", ex); }
        }
    }
}
