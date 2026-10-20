// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public static class UiText
    {
        public const string NotConnected = "Not connected. Paste a code to start.";
        public const string Checking = "Checking the connection…";
        public const string Paused = "Paused: your ZoneKeeper plan is not active. Renew it on getzonekeeper.com. Your trades wait here. We check again every hour.";
        public const string OtherInstance = "ZoneKeeper already runs in another NinjaTrader on this PC.";
        public const string WrongShape = "This code looks wrong. It has 8 letters and numbers, like ZK-AB3D-9KXP.";
        public const string CodeInvalid = "This code does not work. Make a new one: Cabinet → Platforms → NinjaTrader 8 → Connect NinjaTrader.";
        public const string ClaimNotPaid = "Your ZoneKeeper plan is not active. Renew it on getzonekeeper.com, then make a new code.";
        public const string TooManyDevices = "5 PCs are connected, the most. Disconnect one in Cabinet → Platforms → NinjaTrader 8, then make a new code.";
        public const string TooManyTries = "Too many tries from this internet address. Wait one hour and try again.";
        public const string ClaimOffline = "No answer from getzonekeeper.com. Check the internet and click Connect again.";
        public const string Disconnected = "This PC was disconnected. Make a new code on getzonekeeper.com → Cabinet → Platforms → NinjaTrader 8 and paste it here.";
        public const string ConnectHelp = "Get the code on getzonekeeper.com → Cabinet → Platforms → NinjaTrader 8 → Connect NinjaTrader. It works for 10 minutes, one time.";
        public const string QueueFull = "Waiting list is full (500 trades). The oldest trade was dropped.";
        public const string SaveFailed = "Could not save a ZoneKeeper file. We try again soon.";
        public const string AccountSaved = "Saved. You can close this window.";
        public const string DisconnectConfirm = "Disconnect this PC from ZoneKeeper? NinjaTrader stops sending trades. To connect again, you need a new code.";
        // Reset inside the window (0.2.0).
        public const string AutoResetLabel = "Open the reset after each closed trade";
        public const string ResetTitle = "Reset after a trade";
        public static string ResetOpened(long exitMs)
        { return "Opened the reset for the trade closed at " + DateTimeOffset.FromUnixTimeMilliseconds(exitMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "."; }
        public static string ResetSaved(long exitMs)
        { return "Reset saved for the trade closed at " + DateTimeOffset.FromUnixTimeMilliseconds(exitMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "."; }
        public const string ResetDoneElsewhere = "Already done on another device.";
        public static string ResetRefused(string error) { return "The server refused this reset: " + Helpers.Cut(error, 160); }
        public const string ResetQueueFull = "Waiting list of resets is full (100). The oldest was dropped.";
        public const string ResetSaveRetry = "Could not save the reset. Tap your tilt again.";
        public static string ResetGaveUp(int tries, string error) { return "Gave up on a reset after " + tries.ToString(CultureInfo.InvariantCulture) + " tries: " + Helpers.Cut(error, 160); }
        public static string AudioLoading(int percent) { return "Loading audio… " + Math.Max(0, Math.Min(100, percent)).ToString(CultureInfo.InvariantCulture) + " %"; }
        public const string AudioNeedsConnection = "Audio needs a connection. The reset runs with the timer.";
        public const string AudioNotPaid = "Your ZoneKeeper plan is not active. Renew it on getzonekeeper.com.";
        public const string AudioFailed = "The voice could not play. The reset runs with the timer.";
        public const string CatalogLoading = "Loading the reset texts…";
        public const string NoTradesToday = "No trades today.";
        // Button labels and tags of the Today tab. All content texts (tilts, thoughts, lines, parts, rule lines) come from the catalog.
        public const string TabToday = "Today";
        public const string TabConnection = "Connection";
        public const string RefreshButton = "Refresh";
        public const string ResetButton = "Reset";
        public const string LoggedTag = "Logged";
        public const string SavingTag = "Saving…";
        public const string NewTag = "new";
        public const string BackToToday = "Back to today";
        public const string StopForToday = "Stop for today";
        public const string ResetAnyway = "Do a reset anyway";
        public const string SkipAudio = "Skip audio";
        public const string PauseButton = "Pause";
        public const string ResumeButton = "Resume";
        public const string YourLogic = "Your Reprogram Logic";
        public static readonly string[] StepNames = { "Rate your tilt", "What happened", "What you feel", "Reprogram Logic", "Rate tilt again" };
        public static string StepLine(int step)
        {
            int i = Math.Max(1, Math.Min(StepNames.Length, step));
            return "Step " + i.ToString(CultureInfo.InvariantCulture) + " of 5 — " + StepNames[i - 1];
        }
        public static string WhatHappened(string triggerLabel) { return "What happened: " + triggerLabel; }
        public static string TiltChoice(int n, string name) { return n.ToString(CultureInfo.InvariantCulture) + " · " + name; }
        public static string TiltTip(int n, string name, string feel) { return TiltChoice(n, name) + (string.IsNullOrEmpty(feel) ? "" : " — " + feel); }
        public static string PartLine(int index, int count, string name)
        { return "Part " + (index + 1).ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture) + " · " + name; }
        public static string RoundLine(int round)
        { return round <= 0 ? "Get ready." : "Round " + round.ToString(CultureInfo.InvariantCulture) + " of " + TodayRules.BreathRounds.ToString(CultureInfo.InvariantCulture); }
        // The short status line on top of the Today tab.
        public static string TodayStatus(ZkStatus status, string email)
        {
            switch (status)
            {
                case ZkStatus.Checking: return Checking;
                case ZkStatus.Connected: return "Connected to " + email;
                case ZkStatus.Paused: return "Paused: your plan is not active.";
                case ZkStatus.Offline: return "Offline: trades and resets wait here.";
                case ZkStatus.OtherInstance: return OtherInstance;
                default: return "Not connected — open the Connection tab.";
            }
        }
        public static string Status(ZkStatus status, string email, long nextTryMs)
        {
            switch (status)
            {
                case ZkStatus.Checking: return Checking;
                case ZkStatus.Connected: return "Connected to " + email + ". Trades are sent by themselves.";
                case ZkStatus.Paused: return Paused;
                case ZkStatus.Offline: return "No answer from getzonekeeper.com. Next try at " + DateTimeOffset.FromUnixTimeMilliseconds(nextTryMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ". Your trades wait here.";
                case ZkStatus.OtherInstance: return OtherInstance;
                default: return NotConnected;
            }
        }
        public static string ClockWarning(long offsetMs)
        {
            if (Math.Abs(offsetMs) <= 120000) return "";
            return "Your PC clock is " + Math.Max(1, (long)Math.Round(Math.Abs(offsetMs) / 60000m, 0, MidpointRounding.AwayFromZero)) +
                " minutes off. Trades may be refused. Fix it: Windows Settings → Time & language → Date & time → Sync now.";
        }
    }
}
