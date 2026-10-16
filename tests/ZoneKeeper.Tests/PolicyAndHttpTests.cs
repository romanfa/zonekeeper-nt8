using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class PolicyAndHttpTests
    {
        [Theory]
        [InlineData(200, null, ApiKind.Ok)]
        [InlineData(401, "not_connected", ApiKind.NotConnected)]
        [InlineData(403, "not_paid", ApiKind.NotPaid)]
        [InlineData(403, "Forbidden", ApiKind.Offline)]
        [InlineData(400, "code_invalid", ApiKind.CodeInvalid)]
        [InlineData(409, "too_many_devices", ApiKind.TooManyDevices)]
        [InlineData(409, "unexpected", ApiKind.Offline)]
        [InlineData(429, "too_many_tries", ApiKind.TooManyTries)]
        [InlineData(429, "too many calls, wait a little", ApiKind.TooMany)]
        [InlineData(429, "too many trades in 24 hours", ApiKind.TooMany)]
        [InlineData(400, "bad trade", ApiKind.BadRequest)]
        [InlineData(413, "too big", ApiKind.BadRequest)]
        [InlineData(415, "send JSON", ApiKind.BadRequest)]
        [InlineData(301, null, ApiKind.Offline)]
        [InlineData(500, null, ApiKind.Offline)]
        [InlineData(503, null, ApiKind.Offline)]
        public void T13_AllHttpAnswerMappings(int code, string error, ApiKind expected)
        { Assert.Equal(expected, ApiClient.Map(code, error)); }
        [Fact]
        public void T13_BackoffProgressesAndResetsOnlyAfterOk()
        {
            int step = 0; long[] expected = { 15000, 60000, 300000, 900000, 3600000, 3600000 };
            foreach (long wait in expected)
            {
                var d = ConnectionPolicy.Apply(ZkStatus.Connected, new ApiResult { Kind = ApiKind.Offline }, step, Fixtures.Time);
                Assert.Equal(Fixtures.Time + wait, d.NextTryMs); Assert.Equal(ZkStatus.Offline, d.Status); step = d.BackoffStep;
            }
            var ok = ConnectionPolicy.Apply(ZkStatus.Offline, new ApiResult { Kind = ApiKind.Ok }, step, Fixtures.Time);
            Assert.Equal(0, ok.BackoffStep); Assert.Equal(ZkStatus.Connected, ok.Status); Assert.Equal(Fixtures.Time + 300000, ok.NextTryMs);
        }
        [Fact]
        public void T13_PausedIsHourly401DeletesAnd429StartsAtMinute()
        {
            var paused = ConnectionPolicy.Apply(ZkStatus.Connected, new ApiResult { Kind = ApiKind.NotPaid }, 3, Fixtures.Time);
            Assert.Equal(ZkStatus.Paused, paused.Status); Assert.False(paused.DeleteToken); Assert.Equal(Fixtures.Time + 3600000, paused.NextTryMs);
            var disconnected = ConnectionPolicy.Apply(ZkStatus.Paused, new ApiResult { Kind = ApiKind.NotConnected }, 3, Fixtures.Time);
            Assert.True(disconnected.DeleteToken); Assert.Equal(ZkStatus.NotConnected, disconnected.Status); Assert.Equal(UiText.Disconnected, disconnected.Message);
            var rate = ConnectionPolicy.Apply(ZkStatus.Connected, new ApiResult { Kind = ApiKind.TooMany }, 0, Fixtures.Time);
            Assert.Equal(Fixtures.Time + 60000, rate.NextTryMs);
            rate = ConnectionPolicy.Apply(ZkStatus.Connected, new ApiResult { Kind = ApiKind.TooMany, RetryAfter = TimeSpan.FromMinutes(3) }, 0, Fixtures.Time);
            Assert.Equal(Fixtures.Time + 180000, rate.NextTryMs);
        }
        [Theory]
        [InlineData(ApiKind.CodeInvalid)]
        [InlineData(ApiKind.NotPaid)]
        [InlineData(ApiKind.TooManyDevices)]
        [InlineData(ApiKind.TooManyTries)]
        [InlineData(ApiKind.Offline)]
        public void T13_ClaimFailuresHaveExactDistinctMessages(ApiKind kind)
        {
            string expected = kind == ApiKind.CodeInvalid ? UiText.CodeInvalid : kind == ApiKind.NotPaid ? UiText.ClaimNotPaid : kind == ApiKind.TooManyDevices ? UiText.TooManyDevices : kind == ApiKind.TooManyTries ? UiText.TooManyTries : UiText.ClaimOffline;
            Assert.Equal(expected, ConnectionPolicy.ClaimMessage(kind));
        }
        [Fact]
        public async Task T20_RedirectIsNotFollowedAndNoCookiesOrOriginAreSent()
        {
            using (var handlerConfig = ApiClient.CreateHandler()) { Assert.False(handlerConfig.AllowAutoRedirect); Assert.False(handlerConfig.UseCookies); }
            var handler = new FakeHttp();
            handler.Send = (request, cancellation) => {
                Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("Origin"));
                var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently); response.Headers.Location = new Uri("https://example.test/redirected");
                response.Content = new StringContent(""); return Task.FromResult(response);
            };
            using (var client = new ApiClient(Helpers.LiveBaseUrl, handler, delegate { }))
            {
                var result = await client.ClaimAsync("AB3D9KXP", "TRADING-PC", CancellationToken.None);
                Assert.Equal(ApiKind.Offline, result.Kind); Assert.Equal(301, result.Status); Assert.Equal(1, handler.Calls);
            }
        }
        [Fact]
        public async Task HttpHeadersJsonAndTypedResponsesMatchContract()
        {
            var handler = new FakeHttp();
            handler.Send = async (request, cancellation) => {
                Assert.Equal("ZoneKeeper-NT8/0.1.1", request.Headers.UserAgent.ToString()); Assert.Contains("0.1.1", request.Headers.GetValues("X-ZK-Addon-Version"));
                Assert.Equal(Helpers.LiveBaseUrl, request.RequestUri.GetLeftPart(UriPartial.Authority));
                if (request.RequestUri.AbsolutePath == "/api/device/claim")
                {
                    Assert.Null(request.Headers.Authorization); Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);
                    Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet); var claim = Json.Read<ClaimBody>(await request.Content.ReadAsStringAsync());
                    Assert.Equal("AB3D9KXP", claim.Code); Assert.Equal("TRADING-PC", claim.DeviceName); Assert.Equal(ZkVersion.Value, claim.AddonVersion);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Write((ClaimAnswer)Fixtures.Claim().Body)) };
                }
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.Equal(Fixtures.Token, request.Headers.Authorization.Parameter);
                if (request.RequestUri.AbsolutePath == "/api/nt/trades")
                { Assert.Single(Json.Read<TradesBody>(await request.Content.ReadAsStringAsync()).Trades); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Write((TradesAnswer)Fixtures.Sent(1).Body)) }; }
                if (request.RequestUri.AbsolutePath == "/api/nt/disconnect")
                { Assert.Null(request.Content); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") }; }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Write((PingAnswer)Fixtures.Ping().Body)) };
            };
            using (var client = new ApiClient(null, handler, delegate { }))
            {
                Assert.Equal(ApiKind.Ok, (await client.ClaimAsync("AB3D9KXP", "TRADING-PC", CancellationToken.None)).Kind);
                Assert.Equal(ApiKind.Ok, (await client.PingAsync(Fixtures.Token, CancellationToken.None)).Kind);
                Assert.Equal(ApiKind.Ok, (await client.SendTradesAsync(Fixtures.Token, new List<TradeRecord> { Fixtures.Trade() }, CancellationToken.None)).Kind);
                Assert.Equal(ApiKind.Ok, (await client.DisconnectAsync(Fixtures.Token, CancellationToken.None)).Kind);
            }
        }
        [Fact]
        public async Task T20_ActualTenSecondTimeoutReturnsOffline()
        {
            var handler = new FakeHttp(); handler.Send = (request, cancellation) => {
                var stalled = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellation.Register(() => stalled.TrySetCanceled()); return stalled.Task;
            };
            using (var client = new ApiClient(Helpers.LiveBaseUrl, handler, delegate { }))
            {
                Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout); var clock = Stopwatch.StartNew();
                Assert.Equal(ApiKind.Offline, (await client.PingAsync(Fixtures.Token, CancellationToken.None)).Kind);
                Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(9));
            }
        }
        [Theory]
        [InlineData("dns")]
        [InlineData("tls")]
        [InlineData("timeout")]
        public async Task NetworkFailuresReturnOfflineWithoutExceptionMessages(string reason)
        {
            var handler = new FakeHttp(); handler.Send = (request, cancellation) => {
                if (reason == "timeout") throw new TaskCanceledException("SECRET");
                throw new HttpRequestException("SECRET");
            };
            using (var client = new ApiClient(Helpers.LiveBaseUrl, handler, delegate { }))
            { var answer = await client.PingAsync(Fixtures.Token, CancellationToken.None); Assert.Equal(ApiKind.Offline, answer.Kind); Assert.DoesNotContain("SECRET", answer.Error); }
        }
        [Fact]
        public async Task ApiNeverLogsClaimCodeTokenBodyOrSensitiveError()
        {
            using (var folder = new TestFolder())
            {
                var log = new EventLog(folder.Path); var handler = new FakeHttp();
                handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Json.Write(new ErrorAnswer { Error = Fixtures.Token + " AB3D9KXP" })) });
                using (var client = new ApiClient(Helpers.LiveBaseUrl, handler, log.ProtectSecret))
                { var result = await client.ClaimAsync("AB3D9KXP", "TRADING-PC", CancellationToken.None); log.Write(result.Error); }
                string text = File.ReadAllText(Path.Combine(folder.Path, "zonekeeper.log")); Assert.DoesNotContain(Fixtures.Token, text); Assert.DoesNotContain("AB3D9KXP", text);
            }
        }
        [Fact]
        public async Task MalformedSuccessfulJsonIsOfflineAndRetryAfterIsRespected()
        {
            var handler = new FakeHttp(); handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("broken") });
            using (var client = new ApiClient(Helpers.LiveBaseUrl, handler, delegate { })) Assert.Equal(ApiKind.Offline, (await client.PingAsync(Fixtures.Token, CancellationToken.None)).Kind);
            var rateHandler = new FakeHttp(); rateHandler.Send = (request, cancellation) => {
                var result = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":\"too many calls, wait a little\"}") };
                result.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return Task.FromResult(result);
            };
            using (var client = new ApiClient(Helpers.LiveBaseUrl, rateHandler, delegate { })) Assert.Equal(TimeSpan.FromMinutes(2), (await client.PingAsync(Fixtures.Token, CancellationToken.None)).RetryAfter);
        }
    }
}
