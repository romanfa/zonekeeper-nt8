// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class EventLog
    {
        private readonly object gate = new object();
        private readonly string directory;
        private readonly List<string> events = new List<string>();
        private readonly HashSet<string> secrets = new HashSet<string>(StringComparer.Ordinal);
        public event Action Changed;
        public EventLog(string folder) { directory = folder; }
        public void ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (gate) secrets.Add(value);
        }
        public List<string> Recent { get { lock (gate) return events.ToList(); } }
        public void Error(string context, Exception exception) { Write(context + " (" + exception.GetType().Name + ")."); }
        public void Write(string message)
        {
            lock (gate)
            {
                string text = Helpers.Cut(message, 1000);
                foreach (string secret in secrets) text = text.Replace(secret, "[redacted]");
                text = Regex.Replace(text, @"zkd_[A-Za-z0-9_-]+", "[redacted]");
                text = Regex.Replace(text, @"(?i)ZK-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}", "[redacted]");
                var now = DateTime.Now;
                events.Insert(0, now.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + text);
                if (events.Count > 20) events.RemoveAt(events.Count - 1);
                try
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "zonekeeper.log");
                    if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                    {
                        string old = Path.Combine(directory, "zonekeeper.1.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception) { } // logging must never stop fills or enter NinjaTrader's exception path
            }
            var handler = Changed;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        public void TradeClosed(TradeRecord trade)
        {
            decimal amount = trade.PnlCents / 100m;
            string value = (amount >= 0 ? "+" : "−") + (trade.Currency == "USD" ? "$" : "") + Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture);
            if (trade.Currency != "USD") value += " " + trade.Currency;
            Write("Trade closed: " + trade.Account + " · " + trade.Instrument + " · " + trade.Side + " " + trade.Qty + " · " + value +
                (string.IsNullOrEmpty(trade.LogExitNames ?? trade.ExitName) ? "" : " · " + (trade.LogExitNames ?? trade.ExitName)));
        }
    }
}
