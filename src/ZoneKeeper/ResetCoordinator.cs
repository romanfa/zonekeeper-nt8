// PURE: reset recovery never participates in trade persistence or upload acknowledgements.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public interface IResetApi
    {
        Task<ApiResult> GetResetsAsync(string token, long after, CancellationToken cancellation);
        Task<ApiResult> OpenResetAsync(string token, long tradeId, CancellationToken cancellation);
    }
    [DataContract] public sealed class PendingReset
    {
        [DataMember(Name = "trade_id")] public long TradeId { get; set; }
        [DataMember(Name = "instrument")] public string Instrument { get; set; }
        [DataMember(Name = "pnl_cents")] public long PnlCents { get; set; }
        [DataMember(Name = "currency")] public string Currency { get; set; }
        [DataMember(Name = "exit_time")] public long ExitTime { get; set; }
    }
    [DataContract] public sealed class ResetsAnswer
    {
        [DataMember(Name = "member_id", IsRequired = true)] public long MemberId { get; set; }
        [DataMember(Name = "pending", IsRequired = true)] public List<PendingReset> Pending { get; set; }
        [DataMember(Name = "next_cursor", IsRequired = true)] public long NextCursor { get; set; }
    }
    [DataContract] public sealed class ResetOpenBody { [DataMember(Name = "trade_id")] public long TradeId { get; set; } }
    [DataContract] public sealed class ResetOpenAnswer
    {
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "expires_at")] public long ExpiresAt { get; set; }
    }
    [DataContract] public sealed class ResetCache
    {
        [DataMember] public string Owner { get; set; }
        [DataMember] public long MemberId { get; set; }
        [DataMember] public List<PendingReset> Pending { get; set; }
    }
    public sealed class ResetCoordinator
    {
        private readonly object gate = new object();
        private readonly SemaphoreSlim refresh = new SemaphoreSlim(1, 1);
        private readonly string path;
        private readonly IAtomicFileStore files;
        private ResetCache cache = new ResetCache { Pending = new List<PendingReset>() };
        private bool confirmed, dismissed;
        private long generation;
        public ResetCoordinator(string path, IAtomicFileStore files)
        {
            this.path = path; this.files = files;
            try { ResetCache saved; if (files.Exists(path) && Json.TryRead(files.Read(path), out saved) && saved.Pending != null) cache = saved; }
            catch (Exception) { /* Recover from server; never block trade saving for a reset cache. */ }
        }
        public int PendingCount { get { lock (gate) return confirmed ? cache.Pending.Count : 0; } }
        public long NextTradeId { get { lock (gate) return confirmed && !dismissed && cache.Pending.Count != 0 ? cache.Pending[0].TradeId : 0; } }
        public long MemberId { get { lock (gate) return confirmed ? cache.MemberId : 0; } }
        public void Dismiss() { lock (gate) dismissed = true; }
        public void Reopen() { lock (gate) dismissed = false; }
        public void Suspend() { lock (gate) { confirmed = false; generation++; } }
        public static string OwnerKey(string origin, string token)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(origin + "\n" + token))).Replace("-", ""); }
        public static bool SameOrigin(string origin, string url)
        {
            Uri expected, target;
            return Uri.TryCreate(origin, UriKind.Absolute, out expected) && Uri.TryCreate(url, UriKind.Absolute, out target) &&
                target.Scheme == "https" && target.UserInfo.Length == 0 && target.GetLeftPart(UriPartial.Authority) == expected.GetLeftPart(UriPartial.Authority);
        }
        public static bool ValidHandoff(string origin, string url)
        {
            if (!SameOrigin(origin, url)) return false;
            var uri = new Uri(url);
            return uri.AbsolutePath == "/app/" && uri.Query.Length == 0 && Regex.IsMatch(uri.Fragment, @"\A#nt-reset=[A-Za-z0-9_-]+\z") &&
                !uri.Fragment.Contains("zkd_");
        }
        public async Task<bool> RefreshAsync(IResetApi api, string origin, string token, CancellationToken cancellation)
        {
            if (api == null || string.IsNullOrEmpty(token)) { Suspend(); return false; }
            await refresh.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                string owner = OwnerKey(origin, token); long revision;
                lock (gate)
                {
                    if (cache.Owner != owner) { cache = new ResetCache { Owner = owner, Pending = new List<PendingReset>() }; confirmed = false; dismissed = false; generation++; }
                    revision = generation;
                }
                long after = 0, member = 0; var pending = new List<PendingReset>();
                for (int page = 0; page < 1000; page++)
                {
                    var result = await api.GetResetsAsync(token, after, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var answer = result.Body as ResetsAnswer;
                    if (result.Kind != ApiKind.Ok || answer == null || answer.MemberId <= 0 || answer.Pending == null || answer.Pending.Count > 100 ||
                        (member != 0 && answer.MemberId != member)) { Suspend(); return false; }
                    member = answer.MemberId;
                    long last = after;
                    foreach (var item in answer.Pending)
                    {
                        if (item == null || item.TradeId <= last) { Suspend(); return false; }
                        last = item.TradeId; pending.Add(item);
                    }
                    if (answer.NextCursor != 0 && (answer.Pending.Count != 100 || answer.NextCursor != last || last <= after)) { Suspend(); return false; }
                    if (answer.NextCursor == 0)
                    {
                        lock (gate)
                        {
                            if (revision != generation) return false;
                            if (cache.MemberId != member || pending.Any(x => !cache.Pending.Any(old => old.TradeId == x.TradeId))) dismissed = false;
                            cache = new ResetCache { Owner = owner, MemberId = member, Pending = pending }; confirmed = true;
                            try { files.Write(path, Json.Write(cache)); } catch (Exception) { /* Server remains durable authority. */ }
                        }
                        return true;
                    }
                    after = answer.NextCursor;
                }
                Suspend(); return false;
            }
            catch (Exception) { Suspend(); return false; }
            finally { refresh.Release(); }
        }
    }
}
