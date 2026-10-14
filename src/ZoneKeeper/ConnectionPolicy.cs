// PURE: no NinjaTrader types (tests compile this file).
using System;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class PolicyDecision
    {
        public ZkStatus Status { get; set; }
        public long NextTryMs { get; set; }
        public int BackoffStep { get; set; }
        public bool DeleteToken { get; set; }
        public string Message { get; set; }
    }
    public static class ConnectionPolicy
    {
        private static readonly long[] waits = { 15000, 60000, 300000, 900000, 3600000 };
        public static PolicyDecision Apply(ZkStatus state, ApiResult result, int backoffStep, long nowMs)
        {
            var next = new PolicyDecision { Status = state, BackoffStep = backoffStep, NextTryMs = nowMs };
            if (result.Kind == ApiKind.Ok)
            { next.Status = ZkStatus.Connected; next.BackoffStep = 0; next.NextTryMs = nowMs + 300000; return next; }
            if (result.Kind == ApiKind.NotConnected)
            { next.Status = ZkStatus.NotConnected; next.DeleteToken = true; next.Message = UiText.Disconnected; next.NextTryMs = long.MaxValue; return next; }
            if (result.Kind == ApiKind.NotPaid)
            { next.Status = ZkStatus.Paused; next.NextTryMs = nowMs + 3600000; return next; }
            int step = Math.Max(0, Math.Min(backoffStep, waits.Length - 1));
            if (result.Kind == ApiKind.TooMany || result.Kind == ApiKind.TooManyTries) step = Math.Max(step, 1);
            long wait = waits[step];
            if (result.RetryAfter.HasValue) wait = Math.Max(wait, (long)Math.Max(0, result.RetryAfter.Value.TotalMilliseconds));
            next.Status = ZkStatus.Offline; next.NextTryMs = nowMs + wait; next.BackoffStep = Math.Min(step + 1, waits.Length - 1); return next;
        }
        public static string ClaimMessage(ApiKind kind)
        {
            switch (kind)
            {
                case ApiKind.CodeInvalid: return UiText.CodeInvalid;
                case ApiKind.NotPaid: return UiText.ClaimNotPaid;
                case ApiKind.TooManyDevices: return UiText.TooManyDevices;
                case ApiKind.TooManyTries: case ApiKind.TooMany: return UiText.TooManyTries;
                default: return UiText.ClaimOffline;
            }
        }
    }
}
