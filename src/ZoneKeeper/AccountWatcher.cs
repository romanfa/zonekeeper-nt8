using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class AccountWatcher
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Account> subscriptions = new Dictionary<string, Account>(StringComparer.Ordinal);
        private readonly AccountIdentityMap<Account> identities = new AccountIdentityMap<Account>();
        private readonly HashSet<string> warnedCurrency = new HashSet<string>();
        private readonly Settings settings;
        private readonly bool allowPlayback;
        private volatile HashSet<string> enabledNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly EventLog log;
        private readonly Action<Account, Fill> fill;
        private readonly Action<Account> reset;
        private readonly Action<Account> resync;
        private readonly Action<Action> queue;
        private readonly Action<List<AccountChoice>> accountsChanged;
        private Timer timer;
        private bool stopped;
        public AccountWatcher(Settings localSettings, EventLog eventLog, Action<Account, Fill> onFill, Action<Account> onReset,
            Action<Account> onResync, Action<Action> enqueue, Action<List<AccountChoice>> onAccounts)
        { settings = localSettings; allowPlayback = localSettings.AllowPlayback; log = eventLog; fill = onFill; reset = onReset; resync = onResync; queue = enqueue; accountsChanged = onAccounts; }
        public void Start()
        {
            lock (gate)
            {
                if (stopped) return;
                Account.AccountStatusUpdate += OnAccountStatusUpdate;
                Account.SimulationAccountReset += OnSimulationReset;
                timer = new Timer(delegate { queue(Rescan); }, null, 30000, 30000);
            }
            Rescan();
        }
        public void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                identities.Freeze();
                if (timer != null) timer.Dispose();
                Account.AccountStatusUpdate -= OnAccountStatusUpdate;
                Account.SimulationAccountReset -= OnSimulationReset;
                foreach (var account in subscriptions.Values) account.ExecutionUpdate -= OnExecutionUpdate;
                subscriptions.Clear();
                enabledNames = new HashSet<string>(StringComparer.Ordinal);
            }
        }
        private bool Allowed(Account account)
        {
            bool playback = account.Provider == Provider.Playback || account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
            return !playback || allowPlayback;
        }
        public void Rescan()
        {
            try
            {
                List<Account> all;
                lock (Account.All) all = Account.All.Where(x => x != null).ToList();
                all = all.Where(Allowed).ToList();
                var active = all.Where(x => settings.IsEnabled(x.Name)).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
                enabledNames = new HashSet<string>(active.Keys, StringComparer.Ordinal);
                lock (gate)
                {
                    if (stopped) return;
                    // Publish the new generation before any new callback/resync can reach the worker.
                    identities.Update(active);
                    foreach (var name in subscriptions.Keys.ToList())
                    {
                        Account replacement;
                        if (!active.TryGetValue(name, out replacement) || !ReferenceEquals(replacement, subscriptions[name]))
                        { subscriptions[name].ExecutionUpdate -= OnExecutionUpdate; subscriptions.Remove(name); }
                    }
                    foreach (var account in active.Values)
                    {
                        if (subscriptions.ContainsKey(account.Name)) continue;
                        account.ExecutionUpdate += OnExecutionUpdate; subscriptions.Add(account.Name, account); resync(account);
                    }
                }
                accountsChanged(all.Select(x => new AccountChoice { Name = x.Name, Enabled = settings.IsEnabled(x.Name) }).OrderBy(x => x.Name).ToList());
            }
            catch (Exception ex) { log.Error("Could not read NinjaTrader accounts", ex); }
        }
        public bool IsCurrent(Account account)
        {
            return account != null && identities.IsCurrent(account.Name, account);
        }
        private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            // Never enumerate collections or write files on an NT event thread.
            try
            {
                Account account = e.Account; bool connected = e.Status.ToString() == "Connected";
                queue(() => { Rescan(); if (connected && account != null && IsCurrent(account)) resync(account); });
            }
            catch (Exception) { }
        }
        private void OnSimulationReset(object sender, EventArgs e)
        {
            try { var account = sender as Account; if (account != null && Allowed(account)) reset(account); }
            catch (Exception) { }
        }
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                var x = e.Execution;
                if (x == null || x.Account == null || x.Instrument == null) return;
                string op = e.Operation.ToString();
                // Remove/Update with zero quantity still invalidate a tracked fill.
                if (op == "Add" && x.Quantity <= 0) return;
                if (!Allowed(x.Account) || !enabledNames.Contains(x.Account.Name)) return;
                var master = x.Instrument.MasterInstrument;
                long ms;
                if (!Helpers.TryToUtcMs(x.Time, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, out ms))
                { queue(() => log.Write("Could not read the fill time in a daylight-saving gap.")); return; }
                string rawCurrency = master.Currency.ToString();
                string currency = Helpers.CurrencyIso(rawCurrency);
                string denomination = x.Account.Denomination.ToString();
                string accountName = x.Account.Name;
                if (currency == null)
                    queue(() => { if (warnedCurrency.Add(rawCurrency)) log.Write("Currency " + Helpers.Cut64(rawCurrency) + " is not supported yet. This trade was not sent."); });
                else if (Helpers.CurrencyIso(denomination) != currency)
                    queue(() => { if (warnedCurrency.Add(accountName + "|" + rawCurrency)) log.Write("The instrument currency differs from the account denomination (" + accountName + "). Check commission in NinjaTrader's CSV."); });
                fill(x.Account, new Fill { Op = op, Account = accountName, Instrument = x.Instrument.FullName, ExecId = x.ExecutionId,
                    IsBuy = x.MarketPosition == MarketPosition.Long, Qty = x.Quantity, Price = (decimal)x.Price, Commission = (decimal)x.Commission,
                    Name = x.Name ?? "", TimeMs = ms, PositionAfter = x.Position, ArrivedMs = Helpers.NowMs(), PointValue = (decimal)master.PointValue,
                    Currency = currency, IsPlayback = x.Account.Provider == Provider.Playback });
            }
            catch (Exception)
            { queue(() => log.Write("Could not read a NinjaTrader fill.")); }
        }
    }
}
