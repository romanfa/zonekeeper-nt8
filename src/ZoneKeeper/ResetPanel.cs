// WINDOWS ONLY: WPF (not in the Mac tests). The 5-step reset panel under the Today list. Every choice asks
// ResetSession; this file only draws it. Every event handler has its own try/catch: nothing reaches NinjaTrader.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class ResetPanel : Border
    {
        // Frozen: a frozen brush may be used by a window on any NinjaTrader UI thread.
        private static readonly Brush Green = Frozen(Color.FromRgb(0x2e, 0x7d, 0x32));
        private static readonly Brush Orange = Frozen(Color.FromRgb(0xef, 0x6c, 0x00));
        private static readonly Brush Red = Frozen(Color.FromRgb(0xc6, 0x28, 0x28));
        private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        private readonly TextBlock header = Text("", true);
        private readonly TextBlock stepLine = Text("", false);
        private readonly TextBlock note = Text("", false);
        private readonly Button back = MakeButton(UiText.BackToToday);
        private readonly StackPanel body = new StackPanel();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private AudioPlayer player;
        private CancellationTokenSource loading;
        private double timerSeconds;
        private bool paused;
        private bool saving;
        private string audioPath;
        // Audio view controls (made once per audio phase).
        private TextBlock partLine, partText, partSay, roundLine, leftText;
        private ProgressBar bar;
        private Button pause;
        public ResetSession Session { get; private set; }
        public TimeZoneInfo Zone { get; set; }
        // The step-5 tap, after the body is on disk (resets.jsonl): the window shows the rule text and the row "Saving…".
        public event Action<ResetSession, ResetBody> Saved;
        // The panel went away without a save (Back to today, Stop for today, plan not active, key gone).
        public event Action<ResetSession> Closed;
        public event Action NotPaid;
        public ResetPanel()
        {
            Zone = TimeZoneInfo.Local;
            Visibility = Visibility.Collapsed; Padding = new Thickness(12); Margin = new Thickness(0, 8, 0, 8);
            BorderThickness = new Thickness(1); BorderBrush = Brushes.Gray; CornerRadius = new CornerRadius(4);
            var root = new StackPanel();
            var top = new DockPanel();
            DockPanel.SetDock(back, Dock.Right); back.Margin = new Thickness(8, 0, 0, 0); back.VerticalAlignment = VerticalAlignment.Top;
            top.Children.Add(back); top.Children.Add(header);
            root.Children.Add(top); root.Children.Add(stepLine); root.Children.Add(note); root.Children.Add(body);
            Child = root;
            back.Click += delegate { Guard("Could not close the reset", () => Close(true)); };
            timer.Tick += delegate { Guard("Could not run the reset timer", TimerTick); };
        }
        // A reset counts as running once a tilt was tapped, and while its save is being written. A new trade never interrupts it.
        public bool Running { get { return saving || (Session != null && !Session.Interruptible && !Session.IsSaved); } }

        public void Open(Catalog catalog, TodayTrade trade)
        {
            StopAudio();
            Session = new ResetSession(catalog, trade, Zone);
            note.Text = ""; note.Visibility = Visibility.Collapsed;
            header.Text = TodayRules.ResetHeader(trade, Zone);
            Visibility = Visibility.Visible;
            Render();
        }
        // Back to today / Stop for today / Disconnect: sound stops, temp file gone, nothing saved.
        public void Close(bool raise)
        {
            var session = Session;
            StopAudio();
            Session = null; Visibility = Visibility.Collapsed; body.Children.Clear();
            if (raise && session != null) { var handler = Closed; if (handler != null) handler(session); }
        }
        // F5 / NinjaTrader exit / window close: stop the sound and delete the temp file; the panel keeps nothing.
        public void StopAudio()
        {
            timer.Stop(); paused = false; timerSeconds = 0;
            if (loading != null) { try { loading.Cancel(); } catch (ObjectDisposedException) { } loading = null; }
            if (player != null) player.Close();
            AudioPlayer.DeleteWithRetries(audioPath); audioPath = null;
        }

        private void Render()
        {
            var s = Session;
            body.Children.Clear(); body.IsEnabled = true;
            if (s == null) return;
            stepLine.Text = UiText.StepLine(s.Step);
            if (s.Step == 1) { AddTiltGrid(n => { if (s.RateBefore(n, Helpers.NowMs())) Render(); }); return; }
            if (s.Step == 3 && s.RedLineOpen) { AddRedLine(s); return; }
            if (s.Step == 3)
            {
                body.Children.Add(Text(UiText.WhatHappened(s.Trigger.Label), false));
                body.Children.Add(Text(s.Trigger.Question ?? "", true));
                foreach (var thought in s.Thoughts)
                {
                    var choice = thought;
                    var button = WideButton(thought.Text);
                    button.Click += delegate { Guard("Could not pick a thought", () => { if (s.PickThought(choice)) Render(); }); };
                    body.Children.Add(button);
                }
                return;
            }
            if (s.Step == 4 && s.Line == null)
            {
                body.Children.Add(Text(UiText.YourLogic, true));
                foreach (var line in s.Thought.Lines)
                {
                    var choice = line;
                    var button = WideButton(line.Text);
                    button.Click += delegate { Guard("Could not pick a line", () => { if (s.PickLine(choice)) StartAudio(s); }); };
                    body.Children.Add(button);
                }
                return;
            }
            if (s.Step == 4) { AddAudioView(s); return; }
            if (s.Step == 5)
            {
                AddTiltGrid(n => {
                    var reset = Session;
                    if (reset == null || saving) return;
                    var saved = reset.RateAgain(n, Helpers.NowMs());
                    if (saved != null) SaveAsync(reset, saved);
                });
            }
        }
        // The step-5 tap: first the line in resets.jsonl (written and flushed), only then the panel closes.
        // A failed write keeps step 5 open with a clear text; the next tap sends the same request_id.
        private async void SaveAsync(ResetSession s, ResetBody body)
        {
            saving = true; this.body.IsEnabled = false;
            bool ok;
            try { ok = await ZkEngine.QueueResetAsync(body, s.Trade.ExitTime); }
            catch (Exception ex) { ZkEngine.ReportError("Could not save the reset", ex); ok = false; }
            saving = false;
            try
            {
                if (Session != s) return;
                if (!ok) { s.SaveFailed(); note.Text = UiText.ResetSaveRetry; note.Visibility = Visibility.Visible; Render(); return; }
                Close(false);
                var handler = Saved; if (handler != null) handler(s, body);
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not finish the reset", ex); }
        }
        private void AddTiltGrid(Action<int> tap)
        {
            var s = Session;
            var grid = new UniformGrid { Columns = 5, Rows = 2, Margin = new Thickness(0, 4, 0, 8) };
            for (int n = 1; n <= 10; n++)
            {
                int value = n;
                var tilt = s.Catalog.Tilt(n);
                string name = tilt == null ? "" : tilt.Name;
                var button = new Button { Content = n.ToString(System.Globalization.CultureInfo.InvariantCulture), Margin = new Thickness(3), Padding = new Thickness(4, 10, 4, 10),
                    FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = BandBrush(TodayRules.Band(n, s.Catalog.RedLine)),
                    ToolTip = UiText.TiltTip(n, name, tilt == null ? "" : tilt.Feel) };
                button.Click += delegate { Guard("Could not rate the tilt", () => tap(value)); };
                grid.Children.Add(button);
            }
            body.Children.Add(grid);
            if (s.TiltBefore != 0 && s.Step == 5)
            {
                var before = s.Catalog.Tilt(s.TiltBefore);
                body.Children.Add(Text("Before: " + UiText.TiltChoice(s.TiltBefore, before == null ? "" : before.Name), false));
            }
        }
        private void AddRedLine(ResetSession s)
        {
            var tilt = s.Catalog.Tilt(s.TiltBefore);
            body.Children.Add(Text(UiText.TiltChoice(s.TiltBefore, tilt == null ? "" : tilt.Name), false));
            var box = new Border { Background = Red, Padding = new Thickness(12), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 8) };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = s.Catalog.After.Red, Foreground = Brushes.White, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var stop = MakeButton(UiText.StopForToday); var anyway = MakeButton(UiText.ResetAnyway);
            stop.Click += delegate { Guard("Could not close the reset", () => Close(true)); };
            anyway.Click += delegate { Guard("Could not go on with the reset", () => { if (s.ContinueAnyway()) Render(); }); };
            buttons.Children.Add(stop); buttons.Children.Add(anyway);
            inner.Children.Add(buttons); box.Child = inner;
            body.Children.Add(box);
        }

        // ---- Step 4: the voice (downloaded now, into memory) or the timer ----
        private void StartAudio(ResetSession s)
        {
            Render();
            loading = new CancellationTokenSource();
            var token = loading;
            ShowLoading(0);
            var progress = new Progress<int>(p => Guard("Could not show the audio progress", () => { if (Session == s && loading == token) ShowLoading(p); }));
            RunDownload(s, token, progress);
        }
        private async void RunDownload(ResetSession s, CancellationTokenSource token, IProgress<int> progress)
        {
            AudioResult result;
            try { result = await ZkEngine.AudioTempFileAsync(s.Line.Audio, progress, token.Token); }
            catch (Exception ex) { ZkEngine.ReportError("Could not get the voice", ex); result = new AudioResult { Kind = ApiKind.Offline }; }
            try
            {
                if (loading == token) { loading = null; token.Dispose(); }
                // Closed, skipped or replaced while loading: the file is deleted and nothing else happens.
                if (Session != s || !s.AudioPhase) { AudioPlayer.DeleteWithRetries(result.Path); return; }
                if (result.Kind == ApiKind.NotPaid) { Close(true); var handler = NotPaid; if (handler != null) handler(); return; }
                if (result.Kind == ApiKind.NotConnected) { Close(true); return; }
                if (result.Kind != ApiKind.Ok || string.IsNullOrEmpty(result.Path)) { StartTimer(s, UiText.AudioNeedsConnection); return; }
                if (player == null)
                {
                    player = new AudioPlayer();
                    player.Opened += delegate { Guard("Could not start the voice", () => { if (player != null) player.Play(); }); };
                    player.Ended += delegate { Guard("Could not end the voice", VoiceEnded); };
                    player.Failed += ex => Guard("Could not play the voice", () => VoiceFailed(ex));
                    player.Position += sec => Guard("Could not show the voice position", () => { var reset = Session; if (reset != null && reset.AudioMode == AudioMode.Voice) RenderAudio(reset, sec); });
                }
                audioPath = result.Path;
                player.Open(result.Path);
                s.VoiceStarted();
                RenderAudio(s, 0);
            }
            catch (Exception ex)
            {
                ZkEngine.ReportError("Could not play the voice", ex);
                AudioPlayer.DeleteWithRetries(result.Path); audioPath = null;
                if (Session == s && s.AudioPhase) { ZkEngine.Note(UiText.AudioFailed); StartTimer(s, UiText.AudioFailed); }
            }
        }
        private void VoiceEnded()
        {
            var s = Session;
            if (player != null) player.Close();
            audioPath = null;
            if (s != null && s.FinishAudio()) Render();
        }
        private void VoiceFailed(Exception ex)
        {
            var s = Session;
            if (player != null) player.Close();
            audioPath = null;
            ZkEngine.Note(UiText.AudioFailed + (ex == null ? "" : " (" + ex.GetType().Name + ")"));
            if (s != null && s.AudioPhase) StartTimer(s, UiText.AudioFailed);
        }
        private void StartTimer(ResetSession s, string why)
        {
            s.TimerStarted();
            timerSeconds = 0; paused = false;
            note.Text = why; note.Visibility = Visibility.Visible;
            RenderAudio(s, 0);
            timer.Start();
        }
        private void TimerTick()
        {
            var s = Session;
            if (s == null || s.AudioMode != AudioMode.Timer || paused) return;
            timerSeconds += 1;
            if (timerSeconds >= TodayRules.TimerTotal(s.Catalog.Parts)) { timer.Stop(); if (s.FinishAudio()) Render(); return; }
            RenderAudio(s, timerSeconds);
        }
        private void ShowLoading(int percent)
        {
            if (partLine == null) return;
            partLine.Text = UiText.AudioLoading(percent); partText.Text = ""; partSay.Text = ""; roundLine.Text = ""; leftText.Text = "";
            bar.Value = percent; pause.Visibility = Visibility.Collapsed;
        }
        private void AddAudioView(ResetSession s)
        {
            partLine = Text("", true); partText = Text("", false); partSay = Text("", false); partSay.FontStyle = FontStyles.Italic;
            roundLine = Text("", false); leftText = Text("", false);
            bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, Margin = new Thickness(0, 4, 0, 8) };
            pause = MakeButton(UiText.PauseButton);
            var skip = MakeButton(UiText.SkipAudio);
            pause.Click += delegate { Guard("Could not pause the voice", TogglePause); };
            skip.Click += delegate { Guard("Could not skip the voice", Skip); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(pause); buttons.Children.Add(skip);
            body.Children.Add(Text(s.Line.Text, false));
            body.Children.Add(partLine); body.Children.Add(partText); body.Children.Add(partSay); body.Children.Add(roundLine);
            body.Children.Add(bar); body.Children.Add(leftText); body.Children.Add(buttons);
            if (s.AudioMode == AudioMode.None) ShowLoading(0);
            else RenderAudio(s, s.AudioMode == AudioMode.Timer ? timerSeconds : (player == null ? 0 : player.Seconds));
        }
        private void RenderAudio(ResetSession s, double sec)
        {
            if (partLine == null || !s.AudioPhase) return;
            var parts = s.Catalog.Parts;
            bool voice = s.AudioMode == AudioMode.Voice;
            var marks = voice ? s.Catalog.Audio(s.Line.Audio).Marks : null;
            int i = voice ? TodayRules.PartAt(marks, sec) : TodayRules.TimerPartAt(parts, sec);
            var part = parts[i];
            double total = voice ? marks.End : TodayRules.TimerTotal(parts);
            double partStart = voice ? TodayRules.PartStart(marks, i) : TodayRules.TimerPartStart(parts, i);
            partLine.Text = UiText.PartLine(i, parts.Count, part.Name);
            partText.Text = part.Kind == "line" ? s.Line.Text : (part.Todo ?? "");
            partSay.Text = part.Say ?? ""; partSay.Visibility = string.IsNullOrEmpty(part.Say) ? Visibility.Collapsed : Visibility.Visible;
            int round = part.Kind == "breathe" ? (voice ? TodayRules.BreathRound(marks, sec - partStart) : TodayRules.TimerBreathRound(sec - partStart)) : -1;
            roundLine.Text = round < 0 ? "" : UiText.RoundLine(round); roundLine.Visibility = round < 0 ? Visibility.Collapsed : Visibility.Visible;
            bar.Value = total <= 0 ? 0 : Math.Max(0, Math.Min(100, sec * 100 / total));
            leftText.Text = TodayRules.LeftText(total - sec);
            pause.Visibility = Visibility.Visible; pause.Content = paused ? UiText.ResumeButton : UiText.PauseButton;
        }
        private int CurrentPart(ResetSession s)
        {
            if (s.AudioMode == AudioMode.Voice && player != null) return TodayRules.PartAt(s.Catalog.Audio(s.Line.Audio).Marks, player.Seconds);
            if (s.AudioMode == AudioMode.Timer) return TodayRules.TimerPartAt(s.Catalog.Parts, timerSeconds);
            return 0; // still loading: no part started
        }
        private void TogglePause()
        {
            var s = Session;
            if (s == null || s.AudioMode == AudioMode.None) return;
            paused = !paused;
            if (s.AudioMode == AudioMode.Voice && player != null) { if (paused) player.Pause(); else player.Play(); }
            pause.Content = paused ? UiText.ResumeButton : UiText.PauseButton;
        }
        private void Skip()
        {
            var s = Session;
            if (s == null || !s.AudioPhase) return;
            int part = CurrentPart(s);
            StopAudio();
            if (s.SkipAudio(part)) Render();
        }

        private static Brush BandBrush(string band) { return band == "r" ? Red : band == "o" ? Orange : Green; }
        public static Brush BandBrushFor(string band) { return BandBrush(band); }
        private static TextBlock Text(string value, bool bold)
        { return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }; }
        private static Button MakeButton(string text)
        { return new Button { Content = text, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left }; }
        // A full-width button with wrapped, left-aligned text (thoughts and Reprogram Logic lines).
        private static Button WideButton(string text)
        {
            return new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left },
                Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(10, 8, 10, 8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        }
        private static void Guard(string context, Action action)
        {
            try { action(); }
            catch (Exception ex) { ZkEngine.ReportError(context, ex); }
        }
    }
}
