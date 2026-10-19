// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public interface IAddonApi : IDisposable
    {
        Task<ApiResult> ClaimAsync(string code, string deviceName, CancellationToken cancellation);
        Task<ApiResult> PingAsync(string token, CancellationToken cancellation);
        Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation);
        Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation);
        Task<ApiResult> CatalogAsync(string token, string etag, CancellationToken cancellation);
        Task<ApiResult> TodayAsync(string token, string date, CancellationToken cancellation);
        Task<ApiResult> SaveResetAsync(string token, ResetBody body, CancellationToken cancellation);
        Task<ApiResult> DownloadAudioAsync(string token, string id, Stream target, IProgress<int> percent, CancellationToken cancellation);
    }
    public sealed class ApiClient : IAddonApi
    {
        private readonly HttpClient client;
        private readonly Action<string> protectSecret;
        public TimeSpan Timeout { get { return client.Timeout; } }
        public static HttpClientHandler CreateHandler() { return new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }; }
        public ApiClient(string baseUrl, Action<string> registerSecret) : this(baseUrl, CreateHandler(), registerSecret) { }
        public ApiClient(string baseUrl, HttpMessageHandler handler, Action<string> registerSecret)
        {
            string normalized;
            if (!Settings.TryBaseUrl(baseUrl, out normalized)) throw new ArgumentException("An HTTPS server origin is required.");
            protectSecret = registerSecret ?? delegate { };
            client = new HttpClient(handler, true) { BaseAddress = new Uri(normalized), Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 65536 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZoneKeeper-NT8/" + ZkVersion.Value);
            client.DefaultRequestHeaders.Add("X-ZK-Addon-Version", ZkVersion.Value);
        }
        public static ApiKind Map(int status, string error)
        {
            if (status == 200) return ApiKind.Ok;
            if (status == 401) return ApiKind.NotConnected;
            if (status == 403 && error == "not_paid") return ApiKind.NotPaid;
            if (status == 400 && error == "code_invalid") return ApiKind.CodeInvalid;
            if (status == 409 && error == "too_many_devices") return ApiKind.TooManyDevices;
            if (status == 409 && error == "this trade already has a reset") return ApiKind.AlreadyDone;
            if (status == 429) return error == "too_many_tries" ? ApiKind.TooManyTries : ApiKind.TooMany;
            if (status == 400 || status == 413 || status == 415) return ApiKind.BadRequest;
            return ApiKind.Offline;
        }
        public Task<ApiResult> ClaimAsync(string code, string deviceName, CancellationToken cancellation)
        {
            protectSecret(code);
            return CallAsync<ClaimAnswer>(HttpMethod.Post, "/api/device/claim", null,
                Json.Write(new ClaimBody { Code = code, DeviceName = Helpers.Cut(deviceName, 60), AddonVersion = ZkVersion.Value }), null, cancellation);
        }
        public Task<ApiResult> PingAsync(string token, CancellationToken cancellation)
        { return CallAsync<PingAnswer>(HttpMethod.Get, "/api/nt/ping", token, null, null, cancellation); }
        public Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation)
        { return CallAsync<TradesAnswer>(HttpMethod.Post, "/api/nt/trades", token, Json.Write(new TradesBody { Trades = trades }), null, cancellation); }
        public Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation)
        { return CallAsync<DisconnectAnswer>(HttpMethod.Post, "/api/nt/disconnect", token, null, null, cancellation); }
        // The reset texts. With the saved ETag the site answers 304 and an empty body when nothing changed.
        public Task<ApiResult> CatalogAsync(string token, string etag, CancellationToken cancellation)
        { return CallAsync<Catalog>(HttpMethod.Get, "/api/nt/catalog", token, null, etag, cancellation); }
        // Today's trades for the PC's local date (the only call with a query string).
        public Task<ApiResult> TodayAsync(string token, string date, CancellationToken cancellation)
        { return CallAsync<TodayAnswer>(HttpMethod.Get, "/api/nt/today?date=" + Uri.EscapeDataString(date ?? ""), token, null, null, cancellation); }
        public Task<ApiResult> SaveResetAsync(string token, ResetBody body, CancellationToken cancellation)
        { return CallAsync<ResetAnswer>(HttpMethod.Post, "/api/nt/resets", token, Json.Write(body), null, cancellation); }
        private async Task<ApiResult> CallAsync<T>(HttpMethod method, string path, string token, string body, string etag, CancellationToken cancellation)
        {
            try
            {
                protectSecret(token);
                using (var request = new HttpRequestMessage(method, path))
                {
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                    if (body != null) request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                    using (var response = await client.SendAsync(request, cancellation).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 304 && etag != null) return new ApiResult { Status = 304, Kind = ApiKind.Ok, ETag = etag };
                        string text = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ErrorAnswer errorBody; string error = Json.TryRead(text, out errorBody) ? errorBody.Error : null;
                        var result = new ApiResult { Status = status, Kind = Map(status, error), Error = Helpers.Cut(error, 160), RetryAfter = RetryAfter(response), ETag = ETagOf(response) };
                        if (result.Kind == ApiKind.Ok)
                        {
                            T answer;
                            if (!Json.TryRead(text, out answer)) { result.Kind = ApiKind.Offline; result.Error = "Could not read the server answer."; }
                            else
                            {
                                result.Body = answer;
                                var claim = answer as ClaimAnswer;
                                if (claim != null) protectSecret(claim.Token);
                            }
                        }
                        return result;
                    }
                }
            }
            catch (Exception) { return new ApiResult { Kind = ApiKind.Offline, Error = "No answer from getzonekeeper.com." }; }
        }
        // The audio of one Reprogram Logic line, streamed into memory in 64 KB pieces (never buffered by HttpClient).
        // Own 60-second limit: the 10-second client timeout is too short for a slow line. The key stays in the header.
        public async Task<ApiResult> DownloadAudioAsync(string token, string id, Stream target, IProgress<int> percent, CancellationToken cancellation)
        {
            try
            {
                protectSecret(token);
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/nt/audio/" + Uri.EscapeDataString(id ?? "")))
                {
                    limit.CancelAfter(AudioRules.DownloadTimeoutMs);
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        long? length = response.Content == null ? null : response.Content.Headers.ContentLength;
                        if (status != 200)
                        {
                            string text = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            ErrorAnswer errorBody; string error = Json.TryRead(text, out errorBody) ? errorBody.Error : null;
                            return new ApiResult { Status = status, Kind = Map(status, error), Error = Helpers.Cut(error, 160), RetryAfter = RetryAfter(response) };
                        }
                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var buffer = new byte[65536]; long received = 0; int lastPercent = -1;
                            while (true)
                            {
                                int read = await source.ReadAsync(buffer, 0, buffer.Length, limit.Token).ConfigureAwait(false);
                                if (read <= 0) break;
                                target.Write(buffer, 0, read); received += read;
                                if (received > AudioRules.MaxBytes) return new ApiResult { Status = 200, Kind = ApiKind.Offline, Error = "The audio is too big.", ContentLength = length };
                                if (percent != null && length.HasValue && length.Value > 0)
                                {
                                    int now = (int)Math.Min(100, received * 100 / length.Value);
                                    if (now != lastPercent) { lastPercent = now; percent.Report(now); }
                                }
                            }
                            if (percent != null && lastPercent != 100) percent.Report(100);
                            return new ApiResult { Status = 200, Kind = ApiKind.Ok, ContentLength = length };
                        }
                    }
                }
            }
            catch (Exception) { return new ApiResult { Kind = ApiKind.Offline, Error = "No answer from getzonekeeper.com." }; }
        }
        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter == null) return null;
            if (response.Headers.RetryAfter.Delta.HasValue) return response.Headers.RetryAfter.Delta;
            if (response.Headers.RetryAfter.Date.HasValue) return response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }
        private static string ETagOf(HttpResponseMessage response)
        {
            try { return response.Headers.ETag == null ? null : response.Headers.ETag.ToString(); }
            catch (Exception) { return null; }
        }
        public void Dispose() { client.Dispose(); }
    }
}
