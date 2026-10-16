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
