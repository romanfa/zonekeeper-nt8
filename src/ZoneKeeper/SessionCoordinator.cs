// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
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
        public long NextTryMs { get; set; }
        public long LastSentMs { get; set; }
        public long ClockOffsetMs { get; set; }
        public int Waiting { get; set; }
        public bool HasToken { get; set; }
        public string BaseUrl { get; set; }
    }
    public sealed class SessionCoordinator
    {
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1, 1);
        private readonly object stateGate = new object();
        private readonly Settings settings;
        private readonly OutboxQueue outbox;
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
        public event Action Changed;
        public event Action WorkAvailable;
        public SessionCoordinator(Settings localSettings, OutboxQueue queue, IAddonApi client, EventLog eventLog)
        {
            settings = localSettings; outbox = queue; api = client; log = eventLog;
            log.ProtectSecret(settings.Token);
            status = settings.Token == null ? ZkStatus.NotConnected : ZkStatus.Checking;
            nextTryMs = settings.Token == null ? long.MaxValue : 0;
        }
        public SessionSnapshot Snapshot()
        {
            SessionSnapshot snapshot;
            lock (stateGate) snapshot = new SessionSnapshot { Status = status, Message = message,
                NextTryMs = nextTryMs, LastSentMs = lastSentMs, ClockOffsetMs = clockOffsetMs };
            // Never acquire an external lock while holding stateGate.
            snapshot.EmailMasked = settings.EmailMasked; snapshot.Waiting = outbox.Count;
            snapshot.HasToken = settings.Token != null; snapshot.BaseUrl = settings.BaseUrl; return snapshot;
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
            try { settings.SetAccount(account, enabled); Publish(); }
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
                settings.DeleteToken(); disconnectPending = false;
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
            if (decision.DeleteToken) { settings.DeleteToken(); if (settings.IsDirty) lock (stateGate) nextTryMs = nowMs + 15000; }
        }
        public async Task ProcessAsync(long nowMs, CancellationToken cancellation)
        {
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (cancellation.IsCancellationRequested) return;
                if (!outbox.TryFlush() || !settings.TrySave())
                {
                    bool hasToken = settings.Token != null;
                    lock (stateGate) { nextTryMs = Math.Max(nextTryMs, nowMs + 15000); if (hasToken && status != ZkStatus.Paused) status = ZkStatus.Offline; }
                    Publish(); return;
                }
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
                        }
                    }
                    else Apply(result, nowMs);
                }
                else
                {
                    var result = await api.PingAsync(settings.Token, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var answer = result.Body as PingAnswer;
                    if (result.Kind == ApiKind.Ok && (answer == null || !answer.Ok || answer.ServerTime < 1000000000000L)) result = new ApiResult { Kind = ApiKind.Offline };
                    Apply(result, nowMs);
                    if (result.Kind == ApiKind.Ok)
                    {
                        int remaining = outbox.Count;
                        lock (stateGate) { clockOffsetMs = answer.ServerTime - nowMs; if (remaining != 0) nextTryMs = nowMs; }
                        string warning = UiText.ClockWarning(clockOffsetMs);
                        if (warning.Length != 0) log.Write(warning);
                    }
                }
                Publish();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.Error("Could not send waiting trades", ex); Apply(new ApiResult { Kind = ApiKind.Offline }, nowMs); Publish(); }
            finally { serial.Release(); }
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
