// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
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
    }
    public sealed class ApiClient : IAddonApi, IResetApi
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
            if (status == 429) return error == "too_many_tries" ? ApiKind.TooManyTries : ApiKind.TooMany;
            if (status == 400 || status == 413 || status == 415) return ApiKind.BadRequest;
            return ApiKind.Offline;
        }
        public Task<ApiResult> ClaimAsync(string code, string deviceName, CancellationToken cancellation)
        {
            protectSecret(code);
            return CallAsync<ClaimAnswer>(HttpMethod.Post, "/api/device/claim", null,
                Json.Write(new ClaimBody { Code = code, DeviceName = Helpers.Cut(deviceName, 60), AddonVersion = ZkVersion.Value }), cancellation);
        }
        public Task<ApiResult> PingAsync(string token, CancellationToken cancellation)
        { return CallAsync<PingAnswer>(HttpMethod.Get, "/api/nt/ping", token, null, cancellation); }
        public Task<ApiResult> SendTradesAsync(string token, List<TradeRecord> trades, CancellationToken cancellation)
        { return CallAsync<TradesAnswer>(HttpMethod.Post, "/api/nt/trades", token, Json.Write(new TradesBody { Trades = trades, AutoReset = true }), cancellation); }
        public Task<ApiResult> GetResetsAsync(string token, long after, CancellationToken cancellation)
        { return CallAsync<ResetsAnswer>(HttpMethod.Get, "/api/nt/resets?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture), token, null, cancellation); }
        public Task<ApiResult> OpenResetAsync(string token, long tradeId, CancellationToken cancellation)
        { return CallAsync<ResetOpenAnswer>(HttpMethod.Post, "/api/nt/resets/open", token, Json.Write(new ResetOpenBody { TradeId = tradeId }), cancellation); }
        public Task<ApiResult> DisconnectAsync(string token, CancellationToken cancellation)
        { return CallAsync<DisconnectAnswer>(HttpMethod.Post, "/api/nt/disconnect", token, null, cancellation); }
        private async Task<ApiResult> CallAsync<T>(HttpMethod method, string path, string token, string body, CancellationToken cancellation)
        {
            try
            {
                protectSecret(token);
                using (var request = new HttpRequestMessage(method, path))
                {
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (body != null) request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                    using (var response = await client.SendAsync(request, cancellation).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        string text = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ErrorAnswer errorBody; string error = Json.TryRead(text, out errorBody) ? errorBody.Error : null;
                        var result = new ApiResult { Status = status, Kind = Map(status, error), Error = Helpers.Cut(error, 160) };
                        if (response.Headers.RetryAfter != null)
                        {
                            if (response.Headers.RetryAfter.Delta.HasValue) result.RetryAfter = response.Headers.RetryAfter.Delta;
                            else if (response.Headers.RetryAfter.Date.HasValue) result.RetryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                        }
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
        public void Dispose() { client.Dispose(); }
    }
}
