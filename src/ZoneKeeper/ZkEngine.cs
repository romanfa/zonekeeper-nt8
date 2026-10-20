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
    // What the window gets back when it asks for the voice of a line: a temp file to play, or why not.
    public sealed class AudioResult
    {
        public ApiKind Kind { get; set; }
        public string Path { get; set; }
    }
    // The static events remain valid for a window across an engine stop/start on F5.
    public static class ZkEngine
    {
        private static readonly object gate = new object();
        private static Run current;
        private static object pendingOwner;
        public static event Action Changed;
        // A waiting reset left the queue: (trade id, logged on the site). The Today list updates its row.
        public static event Action<long, bool> ResetSaved;
        private static void Publish()
        {
            var handler = Changed;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        private static void PublishResetSaved(long tradeId, bool logged)
        {
            var handler = ResetSaved;
            if (handler != null) foreach (Action<long, bool> subscriber in handler.GetInvocationList()) try { subscriber(tradeId, logged); } catch (Exception) { }
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
            Run run = null;
            lock (gate)
            {
                if (ReferenceEquals(pendingOwner, owner)) pendingOwner = null;
                if (current != null && ReferenceEquals(current.Owner, owner)) { run = current; run.MarkStopping(); }
            }
            // Outside the lock: BeginStop reaches the window, and a window being built reads the engine (lock order).
            if (run != null) run.BeginStop();
            // No joining, HTTP, DPAPI, or file writes on the NinjaTrader lifecycle thread.
        }
        private static Run ActiveRun() { lock (gate) return current; }
        public static EngineSnapshot Snapshot()
        {
            var run = ActiveRun();
            return run == null ? new EngineSnapshot { Session = new SessionSnapshot { Status = ZkStatus.NotConnected, NextTryMs = 0, BaseUrl = Helpers.LiveBaseUrl, AutoReset = true }, Accounts = new List<AccountChoice>(), Events = new List<string>() } : run.Snapshot();
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
        public static void Note(string message)
        {
            try { var run = ActiveRun(); if (run != null) run.Queue(() => run.log.Write(message)); }
            catch (Exception) { }
        }
        public static bool Running { get { var run = ActiveRun(); return run != null && !run.Stopping && run.session != null; } }
        public static Task SetAutoResetAsync(bool on)
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult(0) : Task.Run(() => run.SetAutoReset(on));
        }
        public static void SaveWindowBounds(WindowBounds bounds)
        {
            var run = ActiveRun();
            if (run != null) Task.Run(() => run.SaveWindowBounds(bounds));
        }
        public static WindowBounds SavedWindowBounds()
        {
            try { var run = ActiveRun(); return run == null || run.settings == null ? null : run.settings.WindowBounds; }
            catch (Exception) { return null; }
        }
        // The reset texts: the copy on disk, loaded again from the site when it is older than 6 hours. Null = none yet.
        public static Task<Catalog> CatalogAsync()
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult<Catalog>(null) : Task.Run(() => run.CatalogAsync());
        }
        // Today's trades from the site. Null = no answer (the window keeps its old list).
        public static Task<TodayAnswer> TodayAsync()
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult<TodayAnswer>(null) : Task.Run(() => run.TodayAsync());
        }
        // The voice of one line: downloaded into memory now, size-checked, written to one temp file. Nothing is cached.
        public static Task<AudioResult> AudioTempFileAsync(string id, IProgress<int> percent, CancellationToken cancellation)
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult(new AudioResult { Kind = ApiKind.Offline }) : Task.Run(() => run.AudioTempFileAsync(id, percent, cancellation));
        }
        // Deletes every .mp3 left in %TEMP%\ZoneKeeper (a crash can leave one). At engine start and when the window opens.
        public static void WipeAudioTemp()
        {
            Task.Run(() => {
                try
                {
                    string folder = AudioRules.TempFolder(Path.GetTempPath());
                    if (!Directory.Exists(folder)) return;
                    foreach (string file in AudioRules.Leftovers(Directory.GetFiles(folder))) try { File.Delete(file); } catch (Exception) { }
                }
                catch (Exception ex) { ReportError("Could not clean the audio temp folder", ex); }
            });
        }
        // The step-5 tap: the finished reset goes into resets.jsonl (written before this returns true) and the sender is woken.
        public static Task<bool> QueueResetAsync(ResetBody body, long exitMs)
        {
            var run = ActiveRun();
            return run == null ? Task.FromResult(false) : Task.Run(() => run.QueueReset(body, exitMs));
        }

        private sealed class Run
        {
            private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
            private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
            private readonly CancellationTokenSource cancel = new CancellationTokenSource();
            private readonly object accountsGate = new object();
            internal readonly EventLog log;
            private readonly string folder;
            private FileStream runningLock;
            private Task lifetime;
            private Task fillTask;
            private Task senderTask;
            private ApiClient api;
            internal Settings settings;
            private CatalogStore catalogs;
            private ResetOutbox resets;
            private readonly SemaphoreSlim catalogGate = new SemaphoreSlim(1, 1);
            private long catalogTriedMs;
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
            public void MarkStopping() { stopping = true; }
            public void BeginStop()
            {
                stopping = true;
                ZoneKeeperWindow.StopAudio();
                WipeAudioTemp();
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
            public void SetAutoReset(bool on)
            {
                // Never write settings while stopping: after F5 the new engine owns the file.
                try { if (!stopping && settings != null) settings.SetAutoReset(on); }
                catch (Exception ex) { log.Error(UiText.SaveFailed, ex); }
                Publish();
            }
            public void SaveWindowBounds(WindowBounds bounds)
            {
                try { if (!stopping && settings != null) settings.SetWindowBounds(bounds); }
                catch (Exception ex) { log.Error(UiText.SaveFailed, ex); }
            }
            public async Task<Catalog> CatalogAsync()
            {
                if (stopping || session == null || catalogs == null) return null;
                try
                {
                    if (session.TakeCatalogStale()) catalogs.MarkStale();
                    long now = Helpers.NowMs();
                    if (catalogs.NeedsRefresh(now) && settings.Token != null)
                    {
                        await catalogGate.WaitAsync(cancel.Token).ConfigureAwait(false);
                        try
                        {
                            // With an old copy in hand, a failed refresh is tried again after a minute, not on every call.
                            if (catalogs.NeedsRefresh(now) && (catalogs.Current == null || now - catalogTriedMs >= 60000))
                            {
                                catalogTriedMs = now;
                                var result = await session.CatalogAsync(catalogs.ETag, now, cancel.Token).ConfigureAwait(false);
                                var catalog = result.Body as Catalog;
                                if (result.Kind == ApiKind.Ok && result.Status == 304) catalogs.Touch(now);
                                else if (result.Kind == ApiKind.Ok && catalog != null) { if (catalogs.Save(catalog, result.ETag, now)) log.Write("Reset texts loaded from the site."); }
                                // Any other answer: the old copy stays (works offline).
                            }
                        }
                        finally { catalogGate.Release(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.Error("Could not load the reset texts", ex); }
                return catalogs.Current;
            }
            public async Task<TodayAnswer> TodayAsync()
            {
                if (stopping || session == null) return null;
                try
                {
                    var result = await session.TodayAsync(Helpers.LocalDateNow(TimeZoneInfo.Local), Helpers.NowMs(), cancel.Token).ConfigureAwait(false);
                    return result.Kind == ApiKind.Ok ? result.Body as TodayAnswer : null;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { log.Error("Could not load today's trades", ex); return null; }
            }
            public async Task<AudioResult> AudioTempFileAsync(string id, IProgress<int> percent, CancellationToken cancellation)
            {
                if (stopping || session == null) return new AudioResult { Kind = ApiKind.Offline };
                string path = null;
                try
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cancel.Token))
                    using (var memory = new MemoryStream())
                    {
                        var result = await session.AudioAsync(id, memory, percent, Helpers.NowMs(), linked.Token).ConfigureAwait(false);
                        if (result.Kind == ApiKind.NotPaid || result.Kind == ApiKind.NotConnected) return new AudioResult { Kind = result.Kind };
                        if (result.Kind != ApiKind.Ok || !AudioRules.SizeOk(memory.Length, result.ContentLength))
                        {
                            if (!cancellation.IsCancellationRequested) log.Write("No voice for this reset (" + Helpers.Cut(id, 16) + ", " + result.Kind + "). " + UiText.AudioNeedsConnection);
                            return new AudioResult { Kind = ApiKind.Offline };
                        }
                        string folder = AudioRules.TempFolder(Path.GetTempPath());
                        Directory.CreateDirectory(folder);
                        path = Path.Combine(folder, AudioRules.TempName());
                        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { memory.Position = 0; memory.CopyTo(file); file.Flush(true); }
                        return new AudioResult { Kind = ApiKind.Ok, Path = path };
                    }
                }
                // A half-written file never stays behind.
                catch (OperationCanceledException) { DeleteQuietly(path); return new AudioResult { Kind = ApiKind.Offline }; }
                catch (Exception ex) { DeleteQuietly(path); log.Error("Could not get the voice for this reset", ex); return new AudioResult { Kind = ApiKind.Offline }; }
            }
            private static void DeleteQuietly(string path)
            {
                try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception) { }
            }
            public bool QueueReset(ResetBody body, long exitMs)
            {
                try
                {
                    if (stopping || resets == null || session == null) return false;
                    if (!resets.Add(body, exitMs, Helpers.NowMs())) return false;
                    session.NotifyQueued(); return true;
                }
                catch (Exception ex) { log.Error(UiText.SaveFailed, ex); return false; }
            }
            public void SetAccounts(List<AccountChoice> choices)
            { lock (accountsGate) accounts = choices; Publish(); }
            public EngineSnapshot Snapshot()
            {
                List<AccountChoice> choices;
                lock (accountsGate) choices = accounts.Select(x => new AccountChoice { Name = x.Name, Enabled = x.Enabled }).ToList();
                return new EngineSnapshot { Session = session == null ? new SessionSnapshot { Status = otherInstance ? ZkStatus.OtherInstance : ZkStatus.Checking, BaseUrl = Helpers.LiveBaseUrl, AutoReset = true } : session.Snapshot(),
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
                            catalogs = new CatalogStore(Path.Combine(folder, "catalog.json"), files, log.Write);
                            resets = new ResetOutbox(Path.Combine(folder, "resets.jsonl"), files, log.Write);
                            api = new ApiClient(settings.BaseUrl, log.ProtectSecret);
                            session = new SessionCoordinator(settings, outbox, resets, api, log) { RunStartedMs = Helpers.NowMs() };
                            session.Changed += Publish; session.WorkAvailable += Wake;
                            // A fresh closed trade: the window comes up with its reset (never on NinjaTrader's threads: the window dispatches itself).
                            session.ResetWanted += id => { if (!stopping) ZoneKeeperWindow.RaiseForTrade(id); };
                            session.ResetSaved += PublishResetSaved;
                            // Key gone (Disconnect or 401): a running voice stops at once.
                            session.KeyRemoved += ZoneKeeperWindow.StopAudio;
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
                    WipeAudioTemp();
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
                        // The catalog refresh (at start when Connected, then every 6 hours, or after a refused save) rides on the sender loop.
                        if (snapshot.Status == ZkStatus.Connected) await CatalogAsync().ConfigureAwait(false);
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
