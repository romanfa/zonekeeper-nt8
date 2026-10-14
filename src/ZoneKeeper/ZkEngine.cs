using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class AccountChoice
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }
    public sealed class EngineSnapshot
    {
        public SessionSnapshot Session { get; set; }
        public List<AccountChoice> Accounts { get; set; }
        public List<string> Events { get; set; }
    }
    // The static event remains valid for a window across an engine stop/start on F5.
    public static class ZkEngine
    {
        private static readonly object gate = new object();
        private static Run current;
        private static object pendingOwner;
        public static event Action Changed;
        private static void Publish()
        {
            var handler = Changed;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        public static bool Start(object owner)
        {
            lock (gate)
            {
                if (current != null && !current.Stopping) return ReferenceEquals(current.Owner, owner);
                if (current != null) { pendingOwner = owner; return true; }
                current = new Run(owner); current.Launch(); return true;
            }
        }
        public static void Stop(object owner)
        {
            lock (gate)
            {
                if (ReferenceEquals(pendingOwner, owner)) pendingOwner = null;
                if (current != null && ReferenceEquals(current.Owner, owner)) current.BeginStop();
            }
            // No joining, HTTP, DPAPI, or file writes on the NinjaTrader lifecycle thread.
        }
        private static Run ActiveRun() { lock (gate) return current; }
        public static EngineSnapshot Snapshot()
        {
            var run = ActiveRun();
            return run == null ? new EngineSnapshot { Session = new SessionSnapshot { Status = ZkStatus.NotConnected, NextTryMs = 0, BaseUrl = Helpers.LiveBaseUrl }, Accounts = new List<AccountChoice>(), Events = new List<string>() } : run.Snapshot();
        }
        public static Task ConnectAsync(string input)
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult(0) : run.OperationAsync(s => s.ConnectAsync(input, Environment.MachineName, Helpers.NowMs(), run.Cancellation));
        }
        public static Task DisconnectAsync()
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult(0) : run.OperationAsync(s => s.DisconnectAsync(Helpers.NowMs(), run.Cancellation));
        }
        public static Task SetAccountAsync(string name, bool enabled)
        {
            var run = ActiveRun();
            if (run == null) return Task.FromResult(0);
            return run.OperationAsync(async s => {
                await s.SetAccountAsync(name, enabled, run.Cancellation).ConfigureAwait(false);
                if (!run.Stopping)
                {
                    // Drop before resubscribe: a disabled interval must never join two halves of a trade.
                    if (!enabled) run.Queue(() => run.fills.Disable(name));
                    run.Queue(() => { if (run.watcher != null) run.watcher.Rescan(); });
                }
            });
        }
        public static void TryNow()
        { var run = ActiveRun(); if (run != null && !run.Stopping && run.session != null) run.session.TryNow(); }
        public static void ReportError(string context, Exception exception)
        {
            try { var run = ActiveRun(); if (run != null) run.Queue(() => run.log.Error(context, exception)); }
            catch (Exception) { }
        }

        private sealed class Run
        {
            private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
            private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
            private readonly CancellationTokenSource cancel = new CancellationTokenSource();
            private readonly object accountsGate = new object();
            private readonly EventLog log;
            private readonly string folder;
            private FileStream runningLock;
            private Task lifetime;
            private Task fillTask;
            private Task senderTask;
            private ApiClient api;
            private Settings settings;
            internal FillCoordinator fills;
            internal AccountWatcher watcher;
            internal SessionCoordinator session;
            private readonly AccountEventBoundary<Account> eventBoundary;
            private List<AccountChoice> accounts = new List<AccountChoice>();
            private volatile bool stopping;
            private volatile bool otherInstance;
            public object Owner { get; private set; }
            public bool Stopping { get { return stopping; } }
            public CancellationToken Cancellation { get { return cancel.Token; } }
            public Run(object owner)
            {
                Owner = owner;
                eventBoundary = new AccountEventBoundary<Account>(Queue);
                folder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "ZoneKeeper");
                log = new EventLog(folder); log.Changed += Publish;
            }
            public void Launch() { lifetime = Task.Run((Func<Task>)LifetimeAsync); }
            public void BeginStop()
            {
                stopping = true;
                cancel.Cancel();
                if (watcher != null) watcher.Stop();
                work.CompleteAdding();
                Wake();
            }
            public void Queue(Action action)
            {
                if (stopping) return;
                try { work.Add(action); }
                catch (InvalidOperationException) { }
            }
            public void QueueFill(Account source, Fill fill)
            {
                eventBoundary.Fill(source, () => {
                    // A fill queued just before a checkbox was cleared must also be ignored.
                    if (!settings.IsEnabled(fill.Account) || watcher == null || !watcher.IsCurrent(source)) return;
                    if (settings.DebugFills)
                        log.Write("Fill: op=" + fill.Op + "; account=" + fill.Account + "; instrument=" + fill.Instrument +
                            "; buy=" + fill.IsBuy + "; qty=" + fill.Qty + "; price=" + fill.Price + "; commission=" + fill.Commission +
                            "; position=" + fill.PositionAfter + "; time_ms=" + fill.TimeMs + "; arrived_ms=" + fill.ArrivedMs +
                            "; point_value=" + fill.PointValue + "; currency=" + fill.Currency + "; id=" + Helpers.SafeId(fill.ExecId));
                    fills.Apply(fill);
                });
            }
            public void QueueReset(Account source)
            { eventBoundary.Fill(source, () => { if (watcher != null && watcher.IsCurrent(source)) fills.Reset(source.Name); }); }
            public void QueueResync(Account account, Action beforeApply)
            {
                eventBoundary.Request(account, action => Task.Run(() => {
                    if (stopping) return;
                    try { action(); }
                    catch (Exception ex)
                    {
                        log.Error("Could not capture NinjaTrader positions", ex);
                        Queue(() => {
                            if (settings.IsEnabled(account.Name) && watcher != null && watcher.IsCurrent(account))
                                fills.Resync(account.Name, new AccountSnapshot(new PositionSnapshot[0], new ExecutionEvidence[0], false));
                        });
                    }
                }), CaptureAccount, beforeApply, (source, snapshot) => {
                    if (!settings.IsEnabled(source.Name) || watcher == null || !watcher.IsCurrent(source)) return;
                    fills.Resync(source.Name, snapshot);
                }, message => log.Write(message + " (" + account.Name + ")."));
            }
            private AccountSnapshot CaptureAccount(Account account)
            {
                return AccountSnapshot.Capture(() => {
                    var positions = new List<PositionSnapshot>();
                    lock (account.Positions)
                    {
                        foreach (var p in account.Positions)
                        {
                            if (p.Instrument == null || p.MarketPosition == MarketPosition.Flat || p.Quantity == 0) continue;
                            positions.Add(new PositionSnapshot { Instrument = p.Instrument.FullName, Net = p.MarketPosition == MarketPosition.Long ? Math.Abs(p.Quantity) : -Math.Abs(p.Quantity),
                                AveragePrice = (decimal)p.AveragePrice, TickSize = (decimal)p.Instrument.MasterInstrument.TickSize,
                                PointValue = (decimal)p.Instrument.MasterInstrument.PointValue,
                                Currency = Helpers.CurrencyIso(p.Instrument.MasterInstrument.Currency.ToString()) });
                        }
                    }
                    return positions;
                }, () => {
                    var executions = new List<ExecutionEvidence>();
                    lock (account.Executions)
                        foreach (var x in account.Executions)
                        {
                            if (x == null || x.Instrument == null) continue;
                            long ms;
                            if (!Helpers.TryToUtcMs(x.Time, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, out ms)) ms = 0;
                            var master = x.Instrument.MasterInstrument;
                            executions.Add(new ExecutionEvidence { ExecId = x.ExecutionId, Instrument = x.Instrument.FullName,
                                IsBuy = x.MarketPosition == MarketPosition.Long, Qty = x.Quantity, Price = (decimal)x.Price,
                                Commission = (decimal)x.Commission, TimeMs = ms, PositionAfter = x.Position,
                                PointValue = (decimal)master.PointValue, Currency = Helpers.CurrencyIso(master.Currency.ToString()), Name = x.Name ?? "" });
                        }
                    return executions;
                });
            }
            public void SetAccounts(List<AccountChoice> choices)
            { lock (accountsGate) accounts = choices; Publish(); }
            public EngineSnapshot Snapshot()
            {
                List<AccountChoice> choices;
                lock (accountsGate) choices = accounts.Select(x => new AccountChoice { Name = x.Name, Enabled = x.Enabled }).ToList();
                return new EngineSnapshot { Session = session == null ? new SessionSnapshot { Status = otherInstance ? ZkStatus.OtherInstance : ZkStatus.Checking, BaseUrl = Helpers.LiveBaseUrl } : session.Snapshot(),
                    Accounts = choices, Events = log.Recent };
            }
            public Task OperationAsync(Func<SessionCoordinator, Task> action)
            {
                // Task.Run makes even a synchronously completed semaphore acquisition safe for WPF/NT callers.
                return Task.Run(async () => {
                    if (stopping || session == null) return;
                    try { await action(session).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { log.Error("Could not finish this ZoneKeeper action", ex); }
                });
            }
            private void Wake()
            {
                try { wake.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
            }
            private async Task<bool> AcquireLockAsync()
            {
                long contentionStarted = 0;
                while (!stopping)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    try
                    {
                        Directory.CreateDirectory(folder);
                        runningLock = new FileStream(Path.Combine(folder, "running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return true;
                    }
                    catch (IOException ex)
                    {
                        int code = ex.HResult & 0xffff;
                        if (code == 32 || code == 33) // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION on Windows
                        {
                            if (contentionStarted == 0) contentionStarted = Helpers.NowMs();
                            if (Helpers.NowMs() - contentionStarted >= 10000) return false;
                            await Task.Delay(200, cancel.Token).ConfigureAwait(false);
                        }
                        else { contentionStarted = 0; log.Error(UiText.SaveFailed, ex); await Task.Delay(15000, cancel.Token).ConfigureAwait(false); }
                    }
                    catch (UnauthorizedAccessException ex)
                    { contentionStarted = 0; log.Error(UiText.SaveFailed, ex); await Task.Delay(15000, cancel.Token).ConfigureAwait(false); }
                }
                return false;
            }
            private async Task LifetimeAsync()
            {
                try
                {
                    if (!await AcquireLockAsync().ConfigureAwait(false))
                    {
                        otherInstance = true; Publish();
                        await Task.Delay(Timeout.Infinite, cancel.Token).ConfigureAwait(false); return;
                    }
                    // An unreadable file is retried, never replaced with an empty queue/settings by default.
                    while (!stopping)
                    {
                        try
                        {
                            var files = new AtomicFileStore();
                            settings = new Settings(Path.Combine(folder, "settings.json"), files, new WindowsTokenProtector(), log.Write);
                            var outbox = new OutboxQueue(Path.Combine(folder, "outbox.jsonl"), files, log.Write);
                            fills = new FillCoordinator(new TradeBuilder(TimeZoneInfo.Local, log.Write), new OpenTradesFile(Path.Combine(folder, "open-trades.json"), files, log.Write), outbox);
                            api = new ApiClient(settings.BaseUrl, log.ProtectSecret);
                            session = new SessionCoordinator(settings, outbox, api, log);
                            session.Changed += Publish; session.WorkAvailable += Wake;
                            outbox.DurableAdded += trade => { log.TradeClosed(trade); session.NotifyQueued(); };
                            outbox.ReadyToSend += session.NotifyQueued;
                            break;
                        }
                        catch (Exception ex)
                        {
                            log.Error(UiText.SaveFailed, ex);
                            await Task.Delay(15000, cancel.Token).ConfigureAwait(false);
                        }
                    }
                    cancel.Token.ThrowIfCancellationRequested();
                    fillTask = Task.Run((Action)FillLoop);
                    watcher = new AccountWatcher(settings, log, QueueFill, QueueReset, QueueResync, Queue, SetAccounts);
                    watcher.Start();
                    // Covers Stop racing startup between the cancellation check and watcher.Start.
                    if (stopping) watcher.Stop();
                    senderTask = Task.Run((Func<Task>)SenderLoopAsync);
                    log.Write("ZoneKeeper engine started (Active)."); Publish();
                    await Task.WhenAll(fillTask, senderTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.Error("Could not start ZoneKeeper", ex); }
                finally
                {
                    BeginStop();
                    if (fillTask != null) try { await fillTask.ConfigureAwait(false); } catch (Exception) { }
                    if (senderTask != null) try { await senderTask.ConfigureAwait(false); } catch (Exception) { }
                    if (session != null) await session.QuiesceAsync().ConfigureAwait(false);
                    if (api != null) api.Dispose();
                    log.Write("ZoneKeeper engine stopped.");
                    log.Changed -= Publish;
                    // Keep ownership until every data-accessing worker and token operation has stopped.
                    if (runningLock != null) runningLock.Dispose();
                    lock (gate)
                    {
                        if (ReferenceEquals(current, this))
                        {
                            current = null;
                            if (pendingOwner != null) { object owner = pendingOwner; pendingOwner = null; current = new Run(owner); current.Launch(); }
                        }
                    }
                    Publish();
                }
            }
            private void FillLoop()
            {
                try
                {
                    fills.PruneDisabledAccounts(settings.IsEnabled);
                    fills.RetryPersistence();
                    while (!work.IsCompleted)
                    {
                        Action action;
                        if (work.TryTake(out action, 1000))
                        { try { action(); } catch (Exception ex) { log.Error("Could not handle a fill", ex); } }
                        else fills.RetryPersistence();
                    }
                    fills.PruneDisabledAccounts(settings.IsEnabled);
                    fills.RetryPersistence();
                }
                catch (Exception ex) { log.Error("Could not save open trades", ex); }
            }
            private async Task SenderLoopAsync()
            {
                try
                {
                    while (!cancel.IsCancellationRequested)
                    {
                        await session.ProcessAsync(Helpers.NowMs(), cancel.Token).ConfigureAwait(false);
                        var snapshot = session.Snapshot();
                        long remaining = snapshot.NextTryMs == long.MaxValue ? int.MaxValue : Math.Max(0, snapshot.NextTryMs - Helpers.NowMs());
                        await wake.WaitAsync((int)Math.Min(int.MaxValue, remaining), cancel.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.Error("Could not send waiting trades", ex); }
            }
        }
    }
}
