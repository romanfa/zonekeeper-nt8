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
        // Reset window (0.2.0). L1-L5 are the log lines from plan note 29.
        public const string AutoResetLabel = "Open the reset after each closed trade";
        public const string OpenResetButton = "Open reset window";
        public const string ResetTitle = "Reset after a trade";
        public const string ResetRuntimeMissing = "Reset window: Microsoft Edge WebView2 Runtime is missing. Install it, then restart NinjaTrader.";
        public const string ResetLoginFailed = "Reset window: could not log in. Try \"Open reset window\" again.";
        public const string ResetUnsupported = "The site does not support the reset window yet.";
        public const string ResetNoAnswer = "Reset window: no answer from getzonekeeper.com. It tries again by itself.";
        public static string ResetOpened(long exitMs)
        { return "Opened the reset for the trade closed at " + DateTimeOffset.FromUnixTimeMilliseconds(exitMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "."; }
        public static string ResetStartFailed(string typeName)
        { return "Reset window could not start (" + Helpers.Cut(typeName, 80) + "). Trades are still sent."; }
        public static string ResetBrowserHiccup(string kind)
        { return "Reset window: a browser part stopped or hung (" + Helpers.Cut(kind, 80) + "). The window stays open."; }
        public static string ResetBrowserStopped(string kind)
        { return "Reset window: the browser stopped (" + Helpers.Cut(kind, 80) + "). The window closed. Trades are still sent."; }
        public static string ResetStatus(ResetView view, long retryMs)
        {
            switch (view)
            {
                case ResetView.Starting: return "Starting the browser…";
                case ResetView.Opening: return "Opening your reset…";
                case ResetView.NoAnswer:
                    return "No answer from getzonekeeper.com. Check the internet. Trying again in " +
                        Math.Max(1, (retryMs + 999) / 1000).ToString(CultureInfo.InvariantCulture) + " seconds.";
                case ResetView.NotPaid: return "Your ZoneKeeper plan is not active. Renew it on getzonekeeper.com, then click \"Open reset window\" in ZoneKeeper.";
                case ResetView.ConnectFirst: return "Connect this PC first: Control Center → New → ZoneKeeper, then paste a code.";
                case ResetView.LoginFailed: return "Could not log in. Click \"Open reset window\" in ZoneKeeper to try again.";
                case ResetView.Unsupported: return ResetUnsupported;
                case ResetView.BrowserFailed: return "The browser could not start. Trades are still sent. You can close this window.";
                default: return "";
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
