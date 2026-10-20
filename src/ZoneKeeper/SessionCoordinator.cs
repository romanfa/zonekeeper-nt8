// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class SessionSnapshot
    {
        public ZkStatus Status { get; set; }
        public string EmailMasked { get; set; }
        public string Message { get; set; }
        public string AccountSaveMessage { get; set; }
        public long NextTryMs { get; set; }
        public long LastSentMs { get; set; }
        public long ClockOffsetMs { get; set; }
        public int Waiting { get; set; }
        public bool HasToken { get; set; }
        public string BaseUrl { get; set; }
        public bool AutoReset { get; set; }
        // Trade ids with a finished reset waiting in resets.jsonl (the row shows "Saving…").
        public List<long> SavingTradeIds { get; set; }
    }
    public sealed class SessionCoordinator
    {
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1, 1);
        private readonly object stateGate = new object();
        private readonly Settings settings;
        private readonly OutboxQueue outbox;
        private readonly ResetOutbox resets;
        private readonly IAddonApi api;
        private readonly EventLog log;
        private ZkStatus status;
        private long nextTryMs;
        private long lastSentMs;
        private long clockOffsetMs;
        private int backoffStep;
        private bool singles;
        private volatile bool disconnectPending;
        private string message = "";
        private string accountSaveMessage = "";
        private bool accountSavePending;
        // A refused reset named logic_id or thought: the catalog texts changed on the site.
        private volatile bool catalogStale;
        // Trade ids this run already raised the window for (the last 200).
        private readonly Queue<long> openedOrder = new Queue<long>();
        private readonly HashSet<long> opened = new HashSet<long>();
        public event Action Changed;
        public event Action WorkAvailable;
        // A fresh trade was saved: bring the window up with the reset for this trade id.
        public event Action<long> ResetWanted;
        // The device key was deleted (Disconnect or 401): a running reset must stop.
        public event Action KeyRemoved;
        // A waiting reset left the queue: (trade id, logged on the site). logged = false means the server refused it.
        public event Action<long, bool> ResetSaved;
        // Engine start time. Trades closed before it never raise the window (no pop-up after a restart).
        public long RunStartedMs { get; set; }
        public SessionCoordinator(Settings localSettings, OutboxQueue queue, IAddonApi client, EventLog eventLog) : this(localSettings, queue, null, client, eventLog) { }
        public SessionCoordinator(Settings localSettings, OutboxQueue queue, ResetOutbox resetQueue, IAddonApi client, EventLog eventLog)
        {
            settings = localSettings; outbox = queue; resets = resetQueue; api = client; log = eventLog;
            log.ProtectSecret(settings.Token);
            status = settings.Token == null ? ZkStatus.NotConnected : ZkStatus.Checking;
            nextTryMs = settings.Token == null ? long.MaxValue : 0;
        }
        public SessionSnapshot Snapshot()
        {
            SessionSnapshot snapshot;
            lock (stateGate) snapshot = new SessionSnapshot { Status = status, Message = message,
                AccountSaveMessage = accountSaveMessage,
                NextTryMs = nextTryMs, LastSentMs = lastSentMs, ClockOffsetMs = clockOffsetMs };
            // Never acquire an external lock while holding stateGate.
            snapshot.EmailMasked = settings.EmailMasked; snapshot.Waiting = outbox.Count;
            snapshot.HasToken = settings.Token != null; snapshot.BaseUrl = settings.BaseUrl; snapshot.AutoReset = settings.AutoReset;
            snapshot.SavingTradeIds = resets == null ? new List<long>() : resets.PendingTradeIds; return snapshot;
        }
        private void Publish()
        {
            var handler = Changed;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        private void Wake()
        {
            var handler = WorkAvailable;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        private void RaiseKeyRemoved()
        {
            var handler = KeyRemoved;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) try { subscriber(); } catch (Exception) { }
        }
        // A window error must never stop trade sending.
        private void RaiseResetWanted(long tradeId)
        {
            var handler = ResetWanted;
            if (handler != null) foreach (Action<long> subscriber in handler.GetInvocationList()) try { subscriber(tradeId); } catch (Exception ex) { log.Error("Could not open the reset window", ex); }
        }
        private void RaiseResetSaved(long tradeId, bool logged)
        {
            var handler = ResetSaved;
            if (handler != null) foreach (Action<long, bool> subscriber in handler.GetInvocationList()) try { subscriber(tradeId, logged); } catch (Exception ex) { log.Error("Could not update the Today list", ex); }
        }
        // True once after a refused reset said the texts changed; the engine then loads the catalog again.
        public bool TakeCatalogStale() { bool stale = catalogStale; catalogStale = false; return stale; }
        private void RememberOpened(long tradeId)
        {
            opened.Add(tradeId); openedOrder.Enqueue(tradeId);
            while (openedOrder.Count > ResetRules.OpenedLimit) opened.Remove(openedOrder.Dequeue());
        }
        public void NotifyQueued()
        {
            lock (stateGate) if (status == ZkStatus.Connected && !disconnectPending) nextTryMs = 0;
            Publish(); Wake(); // Paused/Offline deadlines are deliberately preserved.
        }
        public void TryNow()
        {
            lock (stateGate) if (status == ZkStatus.Paused || status == ZkStatus.Offline) nextTryMs = 0;
            Wake();
        }
        public async Task ConnectAsync(string input, string machineName, long nowMs, CancellationToken cancellation)
        {
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (settings.Token != null || cancellation.IsCancellationRequested) return;
                log.ProtectSecret(input);
                string normalized;
                if (!Helpers.NormalizeCode(input, out normalized))
                { lock (stateGate) { status = ZkStatus.NotConnected; message = UiText.WrongShape; } Publish(); return; }
                log.ProtectSecret(normalized);
                var result = await api.ClaimAsync(normalized, machineName, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var answer = result.Body as ClaimAnswer;
                if (result.Kind == ApiKind.Ok && answer != null && Settings.ValidToken(answer.Token) && answer.DeviceId > 0)
                {
                    log.ProtectSecret(answer.Token);
                    bool saved = settings.SetToken(answer);
                    lock (stateGate) { status = saved ? ZkStatus.Connected : ZkStatus.Checking; message = saved ? "" : UiText.SaveFailed; nextTryMs = saved ? nowMs : nowMs + 15000; backoffStep = 0; }
                    log.Write("Connected to " + settings.EmailMasked + ". This PC is " + Helpers.Cut(machineName, 60) + ".");
                    Wake();
                }
                else
                {
                    lock (stateGate) { status = ZkStatus.NotConnected; message = ConnectionPolicy.ClaimMessage(result.Kind); nextTryMs = long.MaxValue; }
                }
                Publish();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.Error(UiText.SaveFailed, ex);
                lock (stateGate) { status = ZkStatus.NotConnected; message = UiText.SaveFailed; }
                Publish();
            }
            finally { serial.Release(); }
        }
        public async Task SetAccountAsync(string account, bool enabled, CancellationToken cancellation)
        {
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                bool saved = settings.SetAccount(account, enabled);
                lock (stateGate) { accountSavePending = !saved; accountSaveMessage = saved ? UiText.AccountSaved : UiText.SaveFailed; }
                Publish();
            }
            finally { serial.Release(); }
        }
        public async Task DisconnectAsync(long nowMs, CancellationToken cancellation)
        {
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                disconnectPending = true;
                await DisconnectCoreAsync(nowMs, cancellation).ConfigureAwait(false);
            }
            finally { serial.Release(); }
        }
        private async Task DisconnectCoreAsync(long nowMs, CancellationToken cancellation)
        {
            if (settings.Token == null) { disconnectPending = false; return; }
            var result = await api.DisconnectAsync(settings.Token, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var answer = result.Body as DisconnectAnswer;
            if ((result.Kind == ApiKind.Ok && answer != null && answer.Ok) || result.Kind == ApiKind.NotConnected || result.Status == 404)
            {
                settings.DeleteToken(); disconnectPending = false; RaiseKeyRemoved();
                lock (stateGate)
                {
                    status = ZkStatus.NotConnected; nextTryMs = long.MaxValue;
                    message = result.Status == 404 ? "This PC stopped sending. It still uses one of your 5 places. To free it: getzonekeeper.com → Cabinet → Platforms → NinjaTrader 8 → Connected PCs → Disconnect next to " + Helpers.Cut(Environment.MachineName, 60) + "." : "";
                }
                if (settings.IsDirty) lock (stateGate) nextTryMs = nowMs + 15000;
            }
            else Apply(result, nowMs);
            Publish(); Wake();
        }
        private void Apply(ApiResult result, long nowMs)
        {
            PolicyDecision decision;
            lock (stateGate)
            {
                decision = ConnectionPolicy.Apply(status, result, backoffStep, nowMs);
                status = decision.Status; nextTryMs = decision.NextTryMs; backoffStep = decision.BackoffStep; message = decision.Message ?? "";
            }
            if (decision.DeleteToken) { settings.DeleteToken(); if (settings.IsDirty) lock (stateGate) nextTryMs = nowMs + 15000; RaiseKeyRemoved(); }
        }
        public async Task ProcessAsync(long nowMs, CancellationToken cancellation)
        {
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            IDisposable sendGuard = null;
            long resetId = 0;
            SavedReset saved = null;
            try
            {
                sendGuard = await outbox.EnterSendAsync(cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) return;
                if (!outbox.TryFlush() || !settings.TrySave())
                {
                    bool hasToken = settings.Token != null;
                    lock (stateGate) { nextTryMs = Math.Max(nextTryMs, nowMs + 15000); if (hasToken && status != ZkStatus.Paused) status = ZkStatus.Offline; }
                    Publish(); return;
                }
                bool accountSaveRecovered;
                lock (stateGate)
                {
                    accountSaveRecovered = accountSavePending;
                    if (accountSaveRecovered) { accountSavePending = false; accountSaveMessage = UiText.AccountSaved; }
                }
                if (accountSaveRecovered) Publish();
                long due; lock (stateGate) due = nextTryMs;
                if (settings.Token == null || nowMs < due) return;
                if (disconnectPending) { await DisconnectCoreAsync(nowMs, cancellation).ConfigureAwait(false); return; }
                List<OutboxLine> batch;
                ZkStatus state; lock (stateGate) state = status;
                batch = state == ZkStatus.Connected ? outbox.Peek(singles ? 1 : 20) : new List<OutboxLine>();
                if (batch.Count != 0)
                {
                    var result = await api.SendTradesAsync(settings.Token, batch.Select(x => x.Trade).ToList(), cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (result.Kind == ApiKind.BadRequest && (result.Status == 400 || result.Status == 413))
                    {
                        if (batch.Count > 1) { singles = true; lock (stateGate) nextTryMs = nowMs; }
                        else if (outbox.Remove(batch.Select(x => x.Identity)))
                        { log.Write("The server refused this trade: " + result.Error); Apply(new ApiResult { Kind = ApiKind.Ok }, nowMs); int remaining = outbox.Count; lock (stateGate) nextTryMs = remaining != 0 ? nowMs : nowMs + 300000; }
                        else Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs);
                    }
                    else if (result.Kind == ApiKind.Ok)
                    {
                        var answer = result.Body as TradesAnswer;
                        if (!ValidTradesAnswer(answer, batch.Count)) Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs);
                        else if (!outbox.Remove(batch.Select(x => x.Identity))) Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs);
                        else
                        {
                            if (answer.Inserted != 0) log.Write("Sent " + answer.Inserted + " trade" + (answer.Inserted == 1 ? "." : "s."));
                            if (answer.Duplicates != 0) log.Write("Already saved: " + answer.Duplicates + " trade" + (answer.Duplicates == 1 ? "." : "s."));
                            foreach (var rejected in answer.Rejected)
                            {
                                log.Write("The server refused 1 trade: " + rejected.Error);
                                if ((rejected.Error ?? "").IndexOf("future", StringComparison.OrdinalIgnoreCase) >= 0) log.Write("Fix it: Windows Settings → Time & language → Date & time → Sync now.");
                            }
                            Apply(result, nowMs);
                            int remaining = outbox.Count;
                            lock (stateGate) { lastSentMs = nowMs; nextTryMs = remaining != 0 ? nowMs : nowMs + 300000; }
                            if (outbox.Count == 0) singles = false;
                            if (settings.AutoReset)
                            {
                                var pick = ResetRules.PickTrade(batch.Select(x => x.Trade).ToList(), answer, nowMs, RunStartedMs, opened);
                                if (pick != null) { resetId = pick.Id; RememberOpened(pick.Id); log.Write(UiText.ResetOpened(batch[pick.Index].Trade.ExitTime)); }
                            }
                        }
                    }
                    else Apply(result, nowMs);
                }
                else if (state != ZkStatus.Connected || resets == null || resets.Peek(nowMs) == null)
                {
                    var result = await api.PingAsync(settings.Token, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var answer = result.Body as PingAnswer;
                    if (result.Kind == ApiKind.Ok && (answer == null || !answer.Ok || answer.ServerTime < 1000000000000L)) result = new ApiResult { Kind = ApiKind.Offline };
                    Apply(result, nowMs);
                    if (result.Kind == ApiKind.Ok)
                    {
                        int remaining = outbox.Peek(1).Count;
                        lock (stateGate) { clockOffsetMs = answer.ServerTime - nowMs; if (remaining != 0) nextTryMs = nowMs; }
                        string warning = UiText.ClockWarning(clockOffsetMs);
                        if (warning.Length != 0) log.Write(warning);
                    }
                }
                // Resets go after the trades: one per loop, only while Connected and no trade is waiting.
                ZkStatus after; lock (stateGate) after = status;
                var dueReset = after == ZkStatus.Connected && resets != null && outbox.Peek(1).Count == 0 && !cancellation.IsCancellationRequested ? resets.Peek(nowMs) : null;
                if (dueReset != null) saved = await SendOneResetAsync(dueReset, nowMs, cancellation).ConfigureAwait(false);
                // A deferred line wakes the loop at its retry time.
                if (resets != null) { long wake = resets.NextDueMs; lock (stateGate) if (status == ZkStatus.Connected && wake < nextTryMs) nextTryMs = Math.Max(wake, nowMs); }
                Publish();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.Error("Could not send waiting trades", ex); Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs); Publish(); }
            finally { if (sendGuard != null) sendGuard.Dispose(); serial.Release(); }
            // Raised after the send is fully done and outside every lock.
            if (resetId > 0) RaiseResetWanted(resetId);
            if (saved != null) RaiseResetSaved(saved.Line.TradeId, saved.Logged);
        }
        private sealed class SavedReset { public ResetLine Line; public bool Logged; }
        // Answers (plan 31 section 2.5): 200 and 409 → gone, row Logged; 400 → gone, row back to Reset, one log line;
        // 401 / 403 / 429 / no answer → the normal policy, the line waits. Any other server answer is this line's own
        // problem: it backs off (5 s, 15 s, 30 s, 60 s), trade sending untouched. Only 404 / 405 / 410 (the route is
        // gone) count toward the drop after ResetMaxTries; 5xx, 302 and an unreadable 200 keep trying.
        private async Task<SavedReset> SendOneResetAsync(ResetLine line, long nowMs, CancellationToken cancellation)
        {
            var result = await api.SaveResetAsync(settings.Token, line.Body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            SavedReset done = null;
            if (result.Kind == ApiKind.Ok || result.Kind == ApiKind.AlreadyDone || result.Kind == ApiKind.BadRequest)
            {
                if (!resets.Remove(line.RequestId)) { Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs); return null; }
                if (result.Kind == ApiKind.Ok) log.Write(UiText.ResetSaved(line.ExitTime));
                else if (result.Kind == ApiKind.AlreadyDone) log.Write(UiText.ResetDoneElsewhere);
                else
                {
                    log.Write(UiText.ResetRefused(result.Error));
                    string error = result.Error ?? "";
                    if (error.IndexOf("logic_id", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("thought", StringComparison.OrdinalIgnoreCase) >= 0) catalogStale = true;
                }
                Apply(new ApiResult { Kind = ApiKind.Ok }, nowMs);
                lock (stateGate) if (result.Kind != ApiKind.BadRequest) lastSentMs = nowMs;
                ScheduleNextReset(nowMs);
                done = new SavedReset { Line = line, Logged = result.Kind != ApiKind.BadRequest };
            }
            else if (result.Kind == ApiKind.NotConnected || result.Kind == ApiKind.NotPaid || result.Kind == ApiKind.TooMany || result.Kind == ApiKind.TooManyTries || result.Status == 0)
                Apply(result, nowMs);
            else
            {
                line.Attempts++;
                if (result.Status == 404 || result.Status == 405 || result.Status == 410) line.GoneAnswers++;
                if (line.GoneAnswers >= ResetRules.ResetMaxTries)
                {
                    if (!resets.Remove(line.RequestId)) { Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs); return null; }
                    log.Write(UiText.ResetGaveUp(line.GoneAnswers, result.Status.ToString(CultureInfo.InvariantCulture) + " " + (result.Error ?? "")));
                    ScheduleNextReset(nowMs);
                    done = new SavedReset { Line = line, Logged = false };
                }
                else
                {
                    line.NextTryMs = nowMs + ResetRules.RetryDelayMs(line.Attempts - 1, result.RetryAfter);
                    // Nothing else is due now: the loop sleeps until this line's retry time (set after this call) or the next ping.
                    lock (stateGate) if (nextTryMs <= nowMs) nextTryMs = nowMs + 300000;
                }
            }
            return done;
        }
        // After a reset left the queue: more work due now → at once; else the normal 5-minute ping.
        private void ScheduleNextReset(long nowMs)
        {
            bool more = outbox.Count != 0 || resets.Peek(nowMs) != null;
            lock (stateGate) nextTryMs = more ? nowMs : nowMs + 300000;
        }
        // Today's trades, the catalog and the audio run outside the serial lock (a 60-second audio download must never
        // delay a trade send). Only 401 and 403 not_paid change the connection, and only while the same key is still set.
        public Task<ApiResult> TodayAsync(string date, long nowMs, CancellationToken cancellation)
        { return GuardedAsync(token => api.TodayAsync(token, date, cancellation), nowMs, cancellation); }
        public Task<ApiResult> CatalogAsync(string etag, long nowMs, CancellationToken cancellation)
        { return GuardedAsync(token => api.CatalogAsync(token, etag, cancellation), nowMs, cancellation); }
        public Task<ApiResult> AudioAsync(string id, Stream target, IProgress<int> percent, long nowMs, CancellationToken cancellation)
        { return GuardedAsync(token => api.DownloadAudioAsync(token, id, target, percent, cancellation), nowMs, cancellation); }
        private async Task<ApiResult> GuardedAsync(Func<string, Task<ApiResult>> call, long nowMs, CancellationToken cancellation)
        {
            string token = settings.Token;
            if (token == null || disconnectPending) return new ApiResult { Kind = ApiKind.NotConnected };
            var result = await call(token).ConfigureAwait(false);
            if (result.Kind != ApiKind.NotConnected && result.Kind != ApiKind.NotPaid) return result;
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try { if (settings.Token == token) { Apply(result, nowMs); Publish(); Wake(); } }
            finally { serial.Release(); }
            return result;
        }
        private static bool ValidTradesAnswer(TradesAnswer answer, int count)
        {
            return answer != null && answer.Rejected != null && answer.Inserted >= 0 && answer.Duplicates >= 0 &&
                answer.Inserted + answer.Duplicates + answer.Rejected.Count == count &&
                answer.Rejected.All(x => x != null && x.Index >= 0 && x.Index < count) && answer.Rejected.Select(x => x.Index).Distinct().Count() == answer.Rejected.Count;
        }
        // Called only after the engine prevents new operations and cancels pending callers.
        public async Task QuiesceAsync()
        { await serial.WaitAsync().ConfigureAwait(false); serial.Release(); }
    }
}
