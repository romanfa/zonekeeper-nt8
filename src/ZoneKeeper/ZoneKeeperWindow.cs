// WINDOWS ONLY: WPF (not in the Mac tests). One window, two tabs: Today (trades + the reset panel) and Connection.
// All choices live in the pure classes (TodayRules, ResetSession, ...); this file only shows them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class ZoneKeeperWindow : NTWindow
    {
        private const int ShowNoActivate = 4; // SW_SHOWNOACTIVATE
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
        private static ZoneKeeperWindow one;
        private static readonly object oneGate = new object();
        // The last saved tilt_after of this NinjaTrader run (the "second reset still 4+" rule). 0 = none yet.
        private static int previousAfter;
        // Connection tab
        private readonly TextBlock status = Text("", true);
        private readonly TextBlock info = Text("", false);
        private readonly TextBlock clock = Text("", false);
        private readonly TextBlock server = Text("", false);
        private readonly TextBlock message = Text("", false);
        private readonly TextBlock accountSaveMessage = Text("", false);
        private readonly TextBox code = new TextBox { Margin = new Thickness(0, 4, 0, 8), ToolTip = "ZK-AB3D-9KXP", MinHeight = 28 };
        private readonly Button connect = MakeButton("Connect");
        private readonly StackPanel connectBox = new StackPanel();
        private readonly StackPanel accounts = new StackPanel();
        private readonly ListBox events = new ListBox { MinHeight = 120, MaxHeight = 200 };
        private readonly Button retry = MakeButton("Try again now");
        private readonly Button disconnect = MakeButton("Disconnect this PC");
        private readonly CheckBox autoReset = new CheckBox { Content = UiText.AutoResetLabel, Margin = new Thickness(0, 4, 0, 4) };
        // Today tab
        private readonly TabControl tabs = new TabControl();
        private readonly TabItem todayTab = new TabItem { Header = UiText.TabToday };
        private readonly TabItem connectionTab = new TabItem { Header = UiText.TabConnection };
        private readonly TextBlock todayStatus = Text("", true);
        private readonly TextBlock todayTitle = Text("", true);
        private readonly Button refresh = MakeButton(UiText.RefreshButton);
        private readonly TextBlock note = Text("", false);
        private readonly Border ruleBox = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 8), Visibility = Visibility.Collapsed };
        private readonly TextBlock ruleText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
        private readonly TextBlock catalogLoading = Text(UiText.CatalogLoading, false);
        private readonly StackPanel todayList = new StackPanel();
        private readonly TextBlock emptyText = Text(UiText.NoTradesToday, false);
        private readonly StackPanel todayBody = new StackPanel();
        private readonly ResetPanel panel = new ResetPanel();
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private readonly DispatcherTimer boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private TodayAnswer today;
        private Catalog catalog;
        private readonly HashSet<long> knownIds = new HashSet<long>();
        private readonly HashSet<long> newIds = new HashSet<long>();
        // Trade ids whose reset was just tapped: "Saving…" at once, before the engine snapshot shows them.
        private readonly HashSet<long> queuedNow = new HashSet<long>();
        private bool firstLoad = true;
        private Task refreshTask;
        private long pollPausedUntil;
        private long lastSentSeen;
        private bool closed;
        private bool connecting;
        private bool rendering;
        public ZoneKeeperWindow()
        {
            Caption = "ZoneKeeper"; MinWidth = ResetRules.MinWidth; MinHeight = ResetRules.MinHeight;
            ApplySavedPlace();
            // Connection tab: today's content without the old "Open reset window" button.
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(status); content.Children.Add(info);
            clock.Foreground = Brushes.DarkOrange; content.Children.Add(clock); content.Children.Add(server);
            connectBox.Children.Add(Text("Connect code", false)); connectBox.Children.Add(code);
            connectBox.Children.Add(Text("Example: ZK-AB3D-9KXP", false)); connectBox.Children.Add(connect);
            connectBox.Children.Add(Text(UiText.ConnectHelp, false)); content.Children.Add(connectBox); content.Children.Add(message);
            content.Children.Add(Text("Send trades from these accounts", true)); content.Children.Add(accounts);
            content.Children.Add(accountSaveMessage);
            content.Children.Add(Text("Playback accounts are never sent.", false));
            content.Children.Add(Text(UiText.ResetTitle, true)); content.Children.Add(autoReset);
            content.Children.Add(Text("Last events", true)); content.Children.Add(events);
            content.Children.Add(retry);
            var open = MakeButton("Open getzonekeeper.com");
            open.Click += delegate {
                try { Process.Start(new ProcessStartInfo("https://getzonekeeper.com/app/#platforms") { UseShellExecute = true }); }
                catch (Exception ex) { message.Text = "Could not open your browser. Open getzonekeeper.com."; ZkEngine.ReportError("Could not open your browser", ex); }
            };
            content.Children.Add(open); content.Children.Add(disconnect);
            connectionTab.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            // Today tab
            var todayRoot = new StackPanel { Margin = new Thickness(16) };
            todayRoot.Children.Add(todayStatus);
            var head = new DockPanel();
            DockPanel.SetDock(refresh, Dock.Right); refresh.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(refresh); head.Children.Add(todayTitle);
            todayBody.Children.Add(head); todayBody.Children.Add(note);
            ruleBox.Child = ruleText; todayBody.Children.Add(ruleBox);
            todayBody.Children.Add(catalogLoading); todayBody.Children.Add(todayList); todayBody.Children.Add(emptyText); todayBody.Children.Add(panel);
            todayRoot.Children.Add(todayBody);
            todayTab.Content = new ScrollViewer { Content = todayRoot, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tabs.Items.Add(todayTab); tabs.Items.Add(connectionTab);
            Content = tabs;
            tabs.SelectedItem = ZkEngine.Snapshot().Session.HasToken ? todayTab : connectionTab;
            // Handlers (each guarded)
            connect.Click += async delegate {
                if (connecting) return;
                connecting = true; connect.Content = "Connecting…"; connect.IsEnabled = false;
                try { await ZkEngine.ConnectAsync(code.Text); }
                catch (Exception ex) { ZkEngine.ReportError("Could not connect", ex); }
                finally { connecting = false; if (!closed) { connect.Content = "Connect"; connect.IsEnabled = true; Render(); } }
            };
            retry.Click += delegate { ZkEngine.TryNow(); };
            // Render sets IsChecked from the saved setting; only a click by the member saves it.
            autoReset.Checked += async delegate { try { if (!rendering) await ZkEngine.SetAutoResetAsync(true); } catch (Exception ex) { ZkEngine.ReportError("Could not save the reset setting", ex); } };
            autoReset.Unchecked += async delegate { try { if (!rendering) await ZkEngine.SetAutoResetAsync(false); } catch (Exception ex) { ZkEngine.ReportError("Could not save the reset setting", ex); } };
            disconnect.Click += async delegate {
                try
                {
                    if (!DisconnectPrompt.Confirm(this)) return;
                    panel.Close(true);
                    disconnect.IsEnabled = false;
                    try { await ZkEngine.DisconnectAsync(); }
                    finally { if (!closed) { disconnect.IsEnabled = true; Render(); } }
                }
                catch (Exception ex) { ZkEngine.ReportError("Could not disconnect", ex); }
            };
            refresh.Click += delegate { Guard("Could not refresh the list", () => RefreshToday(true)); };
            tabs.SelectionChanged += (sender, e) => { if (ReferenceEquals(e.Source, tabs)) Guard("Could not switch the tab", () => { if (tabs.SelectedItem == todayTab) RefreshToday(false); }); };
            poll.Tick += delegate { Guard("Could not refresh the list", () => { if (IsVisible && WindowState != WindowState.Minimized && tabs.SelectedItem == todayTab) RefreshToday(false); }); };
            noteTimer.Tick += delegate { noteTimer.Stop(); note.Visibility = Visibility.Collapsed; };
            boundsTimer.Tick += delegate { boundsTimer.Stop(); Guard("Could not save the window place", SaveBounds); };
            LocationChanged += delegate { boundsTimer.Stop(); boundsTimer.Start(); };
            SizeChanged += delegate { boundsTimer.Stop(); boundsTimer.Start(); };
            panel.Saved += (session, body) => Guard("Could not save the reset", () => OnResetSaved(session, body));
            panel.Closed += session => Guard("Could not close the reset", () => RenderToday());
            panel.NotPaid += () => Guard("Could not show the plan text", () => ShowNote(UiText.AudioNotPaid, false));
            ZkEngine.Changed += OnChanged;
            ZkEngine.ResetSaved += OnEngineResetSaved;
            Closed += delegate {
                closed = true; poll.Stop(); noteTimer.Stop();
                if (boundsTimer.IsEnabled) { boundsTimer.Stop(); Guard("Could not save the window place", SaveBounds); }
                panel.StopAudio();
                ZkEngine.Changed -= OnChanged; ZkEngine.ResetSaved -= OnEngineResetSaved;
                lock (oneGate) if (ReferenceEquals(one, this)) one = null;
            };
            ZkEngine.WipeAudioTemp();
            poll.Start();
            Render();
            RefreshToday(false);
        }
        // The menu click: the member's own click may activate the window.
        public static void ShowOne()
        {
            try
            {
                ZoneKeeperWindow window;
                bool created = false;
                lock (oneGate)
                {
                    if (one == null) { one = new ZoneKeeperWindow(); created = true; }
                    window = one;
                }
                // An existing window can belong to a different NinjaTrader UI dispatcher.
                window.Dispatcher.InvokeAsync(new Action(() => {
                    try { if (window.closed) return; if (created) window.Show(); else window.Activate(); }
                    catch (Exception ex) { ZkEngine.ReportError("Could not show the ZoneKeeper window", ex); }
                }));
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not create the ZoneKeeper window", ex); }
        }
        // A fresh closed trade (from the engine's sender thread): the window comes on top without taking the keyboard
        // and opens the reset for this trade, unless a reset is running.
        public static void RaiseForTrade(long tradeId)
        {
            try
            {
                ZoneKeeperWindow existing;
                lock (oneGate) existing = one;
                if (existing != null && !existing.closed) { existing.Dispatcher.InvokeAsync(new Action(() => existing.RaiseCore(tradeId, false))); return; }
                NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() => {
                    try
                    {
                        ZoneKeeperWindow window; bool created = false;
                        lock (oneGate)
                        {
                            if (one == null) { one = new ZoneKeeperWindow(); created = true; }
                            window = one;
                        }
                        if (!created) { window.Dispatcher.InvokeAsync(new Action(() => window.RaiseCore(tradeId, false))); return; }
                        window.ShowActivated = false;
                        window.Show();
                        window.RaiseCore(tradeId, true);
                    }
                    catch (Exception ex) { ZkEngine.ReportError("Could not show the ZoneKeeper window", ex); }
                }));
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not raise the ZoneKeeper window", ex); }
        }
        // F5, NinjaTrader exit, Disconnect or a lost key: the voice stops and the temp file goes. No window = nothing to do.
        public static void StopAudio()
        {
            try
            {
                ZoneKeeperWindow window;
                lock (oneGate) window = one;
                if (window == null || window.closed) return;
                window.Dispatcher.InvokeAsync(new Action(() => {
                    try { if (!window.closed) window.panel.Close(true); }
                    catch (Exception ex) { ZkEngine.ReportError("Could not stop the voice", ex); }
                }));
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not stop the voice", ex); }
        }
        private async void RaiseCore(long tradeId, bool justShown)
        {
            try
            {
                if (closed) return;
                if (!justShown)
                {
                    if (WindowState == WindowState.Minimized) ShowWindow(new WindowInteropHelper(this).Handle, ShowNoActivate);
                }
                // On top of the chart without becoming the active window. Never Activate() / Focus() here.
                Topmost = true; Topmost = false;
                tabs.SelectedItem = todayTab;
                await RefreshTodayAsync(true);
                if (closed || today == null || today.Trades == null) return;
                var trade = today.Trades.FirstOrDefault(x => x != null && x.Id == tradeId);
                if (trade == null) return;
                // A running reset is never interrupted: the row shows "new" + Reset; the member taps it after the save.
                if (panel.Running) return;
                if (catalog == null) catalog = await ZkEngine.CatalogAsync();
                if (closed || catalog == null) { RenderToday(); return; }
                newIds.Remove(tradeId);
                OpenReset(trade);
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not open the reset", ex); }
        }
        private void OpenReset(TodayTrade trade)
        {
            ruleBox.Visibility = Visibility.Collapsed;
            panel.Open(catalog, trade);
            RenderToday();
        }
        private void OnResetSaved(ResetSession session, ResetBody body)
        {
            string text = session.AfterText(body.TiltAfter, previousAfter);
            string band = ResetSession.AfterBand(session.Catalog, body.TiltAfter, previousAfter);
            previousAfter = body.TiltAfter;
            ruleText.Text = text; ruleBox.Background = ResetPanel.BandBrushFor(band); ruleBox.Visibility = Visibility.Visible;
            queuedNow.Add(body.TradeId);
            RenderToday();
            QueueReset(session, body);
        }
        private async void QueueReset(ResetSession session, ResetBody body)
        {
            try
            {
                bool ok = await ZkEngine.QueueResetAsync(body, session.Trade.ExitTime);
                if (closed) return;
                if (!ok) { queuedNow.Remove(body.TradeId); ShowNote(UiText.SaveFailed, false); }
                RenderToday();
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not queue the reset", ex); }
        }
        private void OnEngineResetSaved(long tradeId, bool logged)
        {
            if (closed) return;
            try { Dispatcher.InvokeAsync(new Action(() => Guard("Could not update the Today list", () => RefreshToday(true)))); }
            catch (Exception ex) { ZkEngine.ReportError("Could not dispatch a Today update", ex); }
        }
        private void RefreshToday(bool force) { var task = RefreshTodayAsync(force); }
        // A forced refresh always ends with a fresh answer: a refresh in flight is awaited first, then a new one runs.
        private async Task RefreshTodayAsync(bool force)
        {
            if (closed) return;
            if (refreshTask != null && !refreshTask.IsCompleted)
            {
                try { await refreshTask; } catch (Exception) { }
                if (!force || closed) return;
            }
            refreshTask = RefreshCoreAsync(force);
            await refreshTask;
        }
        // Asks the site for today's trades. 429 / no answer → wait 60 s, keep the old list, no error box.
        private async Task RefreshCoreAsync(bool force)
        {
            var s = ZkEngine.Snapshot().Session;
            if (!s.HasToken) { today = null; knownIds.Clear(); newIds.Clear(); queuedNow.Clear(); firstLoad = true; RenderToday(); return; }
            long now = Helpers.NowMs();
            if (!force && now < pollPausedUntil) return;
            try
            {
                if (catalog == null) catalog = await ZkEngine.CatalogAsync();
                var answer = await ZkEngine.TodayAsync();
                if (closed) return;
                if (answer == null) { pollPausedUntil = Helpers.NowMs() + 60000; RenderToday(); return; }
                today = answer;
                if (answer.Trades != null)
                    foreach (var trade in answer.Trades)
                    {
                        if (trade == null) continue;
                        if (knownIds.Add(trade.Id) && !firstLoad) { newIds.Add(trade.Id); ShowNote(TodayRules.NewTradeNote(trade), true); }
                    }
                firstLoad = false;
                RenderToday();
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not refresh the list", ex); }
        }
        private void ShowNote(string text, bool fade)
        {
            note.Text = text; note.Visibility = Visibility.Visible;
            noteTimer.Stop(); if (fade) noteTimer.Start();
        }
        private static TextBlock Text(string value, bool bold)
        { return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }; }
        private static Button MakeButton(string text)
        { return new Button { Content = text, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left }; }
        private static void Guard(string context, Action action)
        {
            try { action(); }
            catch (Exception ex) { ZkEngine.ReportError(context, ex); }
        }
        private void OnChanged()
        {
            if (closed) return;
            try { Dispatcher.InvokeAsync(new Action(() => { try { if (!closed) Render(); } catch (Exception ex) { ZkEngine.ReportError("Could not update the ZoneKeeper window", ex); } })); }
            catch (Exception ex) { ZkEngine.ReportError("Could not dispatch a ZoneKeeper window update", ex); }
        }
        private void Render()
        {
            var snapshot = ZkEngine.Snapshot(); var s = snapshot.Session;
            status.Text = UiText.Status(s.Status, s.EmailMasked, s.NextTryMs);
            info.Text = "Waiting to send: " + s.Waiting + " trades" + (s.LastSentMs == 0 ? "" : " · Last sent: " + DateTimeOffset.FromUnixTimeMilliseconds(s.LastSentMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
            clock.Text = UiText.ClockWarning(s.ClockOffsetMs); clock.Visibility = clock.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            server.Text = "Test server: " + s.BaseUrl; server.Visibility = s.BaseUrl == Helpers.LiveBaseUrl ? Visibility.Collapsed : Visibility.Visible;
            message.Text = s.Message;
            accountSaveMessage.Text = s.AccountSaveMessage;
            accountSaveMessage.Visibility = string.IsNullOrEmpty(s.AccountSaveMessage) ? Visibility.Collapsed : Visibility.Visible;
            connectBox.Visibility = s.Status == ZkStatus.NotConnected ? Visibility.Visible : Visibility.Collapsed;
            if (s.HasToken) code.Clear();
            retry.Visibility = s.Status == ZkStatus.Paused || s.Status == ZkStatus.Offline ? Visibility.Visible : Visibility.Collapsed;
            disconnect.Visibility = s.HasToken ? Visibility.Visible : Visibility.Collapsed;
            rendering = true;
            try { autoReset.IsChecked = s.AutoReset; }
            finally { rendering = false; }
            accounts.Children.Clear();
            if (snapshot.Accounts.Count == 0) accounts.Children.Add(Text("No accounts yet. Connect NinjaTrader to your broker first.", false));
            foreach (var choice in snapshot.Accounts)
            {
                string name = choice.Name;
                var box = new CheckBox { Content = name, IsChecked = choice.Enabled, Margin = new Thickness(0, 4, 0, 4) };
                box.Checked += async delegate { try { await ZkEngine.SetAccountAsync(name, true); } catch (Exception ex) { ZkEngine.ReportError("Could not save the account choice", ex); } };
                box.Unchecked += async delegate { try { await ZkEngine.SetAccountAsync(name, false); } catch (Exception ex) { ZkEngine.ReportError("Could not save the account choice", ex); } };
                accounts.Children.Add(box);
            }
            events.ItemsSource = snapshot.Events;
            // Today tab: status line; the list only with a key. A sent trade refreshes the list.
            todayStatus.Text = UiText.TodayStatus(s.Status, s.EmailMasked);
            todayBody.Visibility = s.HasToken ? Visibility.Visible : Visibility.Collapsed;
            if (!s.HasToken && panel.Session != null) panel.Close(true);
            if (s.LastSentMs != lastSentSeen) { lastSentSeen = s.LastSentMs; if (s.HasToken) RefreshToday(true); }
            RenderToday();
        }
        private void RenderToday()
        {
            var s = ZkEngine.Snapshot().Session;
            todayTitle.Text = TodayRules.TodayTitle(Helpers.NowMs(), TimeZoneInfo.Local);
            catalogLoading.Visibility = catalog == null && s.HasToken ? Visibility.Visible : Visibility.Collapsed;
            var pending = new HashSet<long>(s.SavingTradeIds ?? new List<long>());
            foreach (long id in queuedNow.ToList()) { if (pending.Contains(id)) queuedNow.Remove(id); else pending.Add(id); }
            var rows = TodayRules.Rows(today, pending, newIds);
            todayList.Children.Clear();
            emptyText.Visibility = today != null && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool running = panel.Running;
            foreach (var row in rows)
            {
                var trade = row.Trade;
                var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(right, Dock.Right);
                if (row.IsNew) right.Children.Add(new TextBlock { Text = UiText.NewTag, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontStyle = FontStyles.Italic });
                if (row.State == RowState.Reset)
                {
                    var button = new Button { Content = UiText.ResetButton, Padding = new Thickness(10, 2, 10, 2), IsEnabled = catalog != null && !running };
                    button.Click += delegate { Guard("Could not open the reset", () => { if (catalog == null || panel.Running) return; newIds.Remove(trade.Id); OpenReset(trade); }); };
                    right.Children.Add(button);
                }
                else right.Children.Add(new TextBlock { Text = row.State == RowState.Logged ? UiText.LoggedTag : UiText.SavingTag, VerticalAlignment = VerticalAlignment.Center });
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(Helpers.LocalClock(trade.ExitTime, TimeZoneInfo.Local) + " · " + trade.Instrument + " · " + TodayRules.SideText(trade) + " · "));
                text.Inlines.Add(new System.Windows.Documents.Run(TodayRules.AmountText(trade)) { Foreground = trade.PnlCents > 0 ? Brushes.Green : trade.PnlCents < 0 ? Brushes.Red : text.Foreground, FontWeight = FontWeights.Bold });
                line.Children.Add(right); line.Children.Add(text);
                todayList.Children.Add(line);
            }
        }
        // ---- Window place: saved in settings.json "window"; used only when its title bar is on a screen ----
        private void ApplySavedPlace()
        {
            try
            {
                var screen = new WindowBounds { Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop, Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight };
                var work = SystemParameters.WorkArea;
                var place = ResetRules.Place(ZkEngine.SavedWindowBounds(), screen, new WindowBounds { Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height });
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = place.Left; Top = place.Top; Width = place.Width; Height = place.Height;
            }
            catch (Exception ex) { Width = ResetRules.DefaultWidth; Height = ResetRules.DefaultHeight; ZkEngine.ReportError("Could not place the ZoneKeeper window", ex); }
        }
        private void SaveBounds()
        {
            if (closed || WindowState != WindowState.Normal) return;
            ZkEngine.SaveWindowBounds(new WindowBounds { Left = Left, Top = Top, Width = ActualWidth, Height = ActualHeight });
        }
    }
    public sealed class DisconnectPrompt : Window
    {
        public static bool Confirm(Window owner)
        {
            var prompt = new DisconnectPrompt { Owner = owner, Title = "ZoneKeeper", Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(new TextBlock { Text = UiText.DisconnectConfirm, TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var yes = new Button { Content = "Disconnect", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            var no = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(8, 4, 8, 4) };
            yes.Click += delegate { prompt.DialogResult = true; };
            no.Click += delegate { prompt.DialogResult = false; };
            buttons.Children.Add(yes); buttons.Children.Add(no); content.Children.Add(buttons); prompt.Content = content;
            return prompt.ShowDialog() == true;
        }
    }
}
