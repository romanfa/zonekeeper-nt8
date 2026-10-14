using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class ZoneKeeperWindow : NTWindow
    {
        private static ZoneKeeperWindow one;
        private static readonly object oneGate = new object();
        private readonly TextBlock status = Text("", true);
        private readonly TextBlock info = Text("", false);
        private readonly TextBlock clock = Text("", false);
        private readonly TextBlock server = Text("", false);
        private readonly TextBlock message = Text("", false);
        private readonly TextBox code = new TextBox { Margin = new Thickness(0, 4, 0, 8), ToolTip = "ZK-AB3D-9KXP", MinHeight = 28 };
        private readonly Button connect = MakeButton("Connect");
        private readonly StackPanel connectBox = new StackPanel();
        private readonly StackPanel accounts = new StackPanel();
        private readonly ListBox events = new ListBox { MinHeight = 120, MaxHeight = 200 };
        private readonly Button retry = MakeButton("Try again now");
        private readonly Button disconnect = MakeButton("Disconnect this PC");
        private bool closed;
        private bool connecting;
        public ZoneKeeperWindow()
        {
            Caption = "ZoneKeeper"; Width = 480; Height = 600; MinWidth = 360; MinHeight = 400;
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(status); content.Children.Add(info);
            clock.Foreground = Brushes.DarkOrange; content.Children.Add(clock); content.Children.Add(server);
            connectBox.Children.Add(Text("Connect code", false)); connectBox.Children.Add(code);
            connectBox.Children.Add(Text("ZK-AB3D-9KXP", false)); connectBox.Children.Add(connect);
            connectBox.Children.Add(Text(UiText.ConnectHelp, false)); content.Children.Add(connectBox); content.Children.Add(message);
            content.Children.Add(Text("Send trades from these accounts", true)); content.Children.Add(accounts);
            content.Children.Add(Text("Playback accounts are never sent.", false));
            content.Children.Add(Text("Last events", true)); content.Children.Add(events);
            content.Children.Add(retry);
            var open = MakeButton("Open getzonekeeper.com");
            open.Click += delegate {
                try { Process.Start(new ProcessStartInfo("https://getzonekeeper.com/app/#platforms") { UseShellExecute = true }); }
                catch (Exception ex) { message.Text = "Could not open your browser. Open getzonekeeper.com."; ZkEngine.ReportError("Could not open your browser", ex); }
            };
            content.Children.Add(open); content.Children.Add(disconnect);
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            connect.Click += async delegate {
                if (connecting) return;
                connecting = true; connect.Content = "Connecting…"; connect.IsEnabled = false;
                try { await ZkEngine.ConnectAsync(code.Text); }
                finally { connecting = false; if (!closed) { connect.Content = "Connect"; connect.IsEnabled = true; Render(); } }
            };
            retry.Click += delegate { ZkEngine.TryNow(); };
            disconnect.Click += async delegate {
                if (!DisconnectPrompt.Confirm(this)) return;
                disconnect.IsEnabled = false;
                try { await ZkEngine.DisconnectAsync(); }
                finally { if (!closed) { disconnect.IsEnabled = true; Render(); } }
            };
            ZkEngine.Changed += OnChanged;
            Closed += delegate { closed = true; ZkEngine.Changed -= OnChanged; lock (oneGate) if (ReferenceEquals(one, this)) one = null; };
            Render();
        }
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
        private static TextBlock Text(string value, bool bold)
        { return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }; }
        private static Button MakeButton(string text)
        { return new Button { Content = text, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left }; }
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
            connectBox.Visibility = s.Status == ZkStatus.NotConnected ? Visibility.Visible : Visibility.Collapsed;
            if (s.HasToken) code.Clear();
            retry.Visibility = s.Status == ZkStatus.Paused || s.Status == ZkStatus.Offline ? Visibility.Visible : Visibility.Collapsed;
            disconnect.Visibility = s.HasToken ? Visibility.Visible : Visibility.Collapsed;
            accounts.Children.Clear();
            if (snapshot.Accounts.Count == 0) accounts.Children.Add(Text("No accounts yet. Connect NinjaTrader to your broker first.", false));
            foreach (var choice in snapshot.Accounts)
            {
                string name = choice.Name;
                var box = new CheckBox { Content = name, IsChecked = choice.Enabled, Margin = new Thickness(0, 4, 0, 4) };
                box.Checked += async delegate { await ZkEngine.SetAccountAsync(name, true); };
                box.Unchecked += async delegate { await ZkEngine.SetAccountAsync(name, false); };
                accounts.Children.Add(box);
            }
            events.ItemsSource = snapshot.Events;
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
