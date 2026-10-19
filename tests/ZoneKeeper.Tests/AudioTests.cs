using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // The voice audio: downloaded into memory per reset, size check only, nothing cached (plan 31 section 3).
    public sealed class AudioTests
    {
        private const string Base = Helpers.LiveBaseUrl;
        private const long ThreeMb = 3 * 1024 * 1024;

        [Fact]
        public void A1_SizeCheck()
        {
            Assert.True(AudioRules.SizeOk(ThreeMb, ThreeMb)); Assert.True(AudioRules.SizeOk(ThreeMb, null));
            Assert.True(AudioRules.SizeOk(AudioRules.MinBytes, null)); Assert.True(AudioRules.SizeOk(AudioRules.MaxBytes, null));
            Assert.False(AudioRules.SizeOk(50 * 1024, 50 * 1024)); Assert.False(AudioRules.SizeOk(12 * 1024 * 1024, 12 * 1024 * 1024));
            Assert.False(AudioRules.SizeOk(ThreeMb - 1, ThreeMb)); Assert.False(AudioRules.SizeOk(ThreeMb, ThreeMb + 1)); Assert.False(AudioRules.SizeOk(0, 0));
        }
        [Fact]
        public void A2_TempFolderNameAndLeftovers()
        {
            string temp = Path.Combine("C:", "Users", "Admin", "AppData", "Local", "Temp");
            Assert.Equal(Path.Combine(temp, "ZoneKeeper"), AudioRules.TempFolder(temp));
            string name = AudioRules.TempName();
            Assert.EndsWith(".mp3", name); Assert.Equal(36, name.Length); Assert.NotEqual(name, AudioRules.TempName());
            Assert.Equal(new[] { "a.mp3", "B.MP3" }, AudioRules.Leftovers(new[] { "a.mp3", "notes.txt", "B.MP3", "x.mp3.tmp", null }).ToArray());
            Assert.Empty(AudioRules.Leftovers(null));
            Assert.Equal(60000, AudioRules.DownloadTimeoutMs);
        }
        [Fact]
        public async Task A3_DownloadStreamsIntoMemoryWithProgressAndTheKeyInTheHeader()
        {
            var bytes = new byte[ThreeMb]; new Random(7).NextBytes(bytes);
            var handler = new FakeHttp();
            handler.Send = (request, cancellation) => {
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/api/nt/audio/b21", request.RequestUri.AbsolutePath); Assert.Equal("", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.Equal(Fixtures.Token, request.Headers.Authorization.Parameter);
                var content = new StreamContent(new MemoryStream(bytes), 4096);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg"); content.Headers.ContentLength = ThreeMb;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            var percents = new List<int>(); var files = new MemoryFiles();
            using (var client = new ApiClient(Base, handler, delegate { }))
            using (var target = new MemoryStream())
            {
                var result = await client.DownloadAudioAsync(Fixtures.Token, "b21", target, new Progress(percents.Add), CancellationToken.None);
                Assert.Equal(ApiKind.Ok, result.Kind); Assert.Equal(200, result.Status); Assert.Equal(ThreeMb, result.ContentLength);
                Assert.Equal(ThreeMb, target.Length); Assert.True(bytes.SequenceEqual(target.ToArray()));
                Assert.True(AudioRules.SizeOk(target.Length, result.ContentLength));
                Assert.True(percents.Count >= 2); Assert.Equal(100, percents.Last()); Assert.True(percents.SequenceEqual(percents.OrderBy(x => x)));
            }
            Assert.Equal(0, files.Writes); // the pure code writes no file; only the engine (Windows) makes the temp file
        }
        [Fact]
        public async Task A3_ErrorAnswersLeaveTheStreamEmpty()
        {
            foreach (var row in new[] { new object[] { 403, "{\"error\":\"not_paid\"}", ApiKind.NotPaid }, new object[] { 404, "{\"error\":\"unknown audio\"}", ApiKind.Offline },
                new object[] { 429, "{\"error\":\"too many\"}", ApiKind.TooMany }, new object[] { 401, "{\"error\":\"not_connected\"}", ApiKind.NotConnected }, new object[] { 503, "", ApiKind.Offline } })
            {
                var handler = new FakeHttp();
                handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)(int)row[0]) { Content = new StringContent((string)row[1]) });
                using (var client = new ApiClient(Base, handler, delegate { }))
                using (var target = new MemoryStream())
                {
                    var result = await client.DownloadAudioAsync(Fixtures.Token, "b1", target, null, CancellationToken.None);
                    Assert.Equal((ApiKind)row[2], result.Kind); Assert.Equal((int)row[0], result.Status); Assert.Equal(0, target.Length);
                }
            }
            // No answer at all (network error) and a cancelled wait.
            var broken = new FakeHttp(); broken.Send = (request, cancellation) => { throw new HttpRequestException("dns"); };
            using (var client = new ApiClient(Base, broken, delegate { }))
                Assert.Equal(ApiKind.Offline, (await client.DownloadAudioAsync(Fixtures.Token, "b1", new MemoryStream(), null, CancellationToken.None)).Kind);
            var slow = new FakeHttp(); slow.Send = async (request, cancellation) => { await Task.Delay(Timeout.Infinite, cancellation); return null; };
            using (var client = new ApiClient(Base, slow, delegate { }))
            using (var cts = new CancellationTokenSource(200))
                Assert.Equal(ApiKind.Offline, (await client.DownloadAudioAsync(Fixtures.Token, "b1", new MemoryStream(), null, cts.Token)).Kind);
        }
        [Fact]
        public async Task A3_ABodyOverTenMegabytesIsStoppedAndTreatedAsNoAnswer()
        {
            var handler = new FakeHttp();
            handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[11 * 1024 * 1024]), 65536) });
            using (var client = new ApiClient(Base, handler, delegate { }))
            using (var target = new MemoryStream())
            {
                var result = await client.DownloadAudioAsync(Fixtures.Token, "b1", target, null, CancellationToken.None);
                Assert.Equal(ApiKind.Offline, result.Kind); Assert.True(target.Length <= AudioRules.MaxBytes + 65536); Assert.False(AudioRules.SizeOk(target.Length, result.ContentLength));
            }
        }
        [Fact]
        public async Task A4_NotPaidOnTheAudioPausesTheSessionAndATimeoutDoesNot()
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles(); var settings = Fixtures.Settings(files, true); var api = new FakeApi();
                var session = new SessionCoordinator(settings, new OutboxQueue("queue", files, delegate { }), api, new EventLog(folder.Path));
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                using (var target = new MemoryStream())
                {
                    var ok = await session.AudioAsync("b21", target, null, Fixtures.Time, CancellationToken.None);
                    Assert.Equal(ApiKind.Ok, ok.Kind); Assert.Equal(api.AudioBytes, target.Length); Assert.Equal(new[] { "b21" }, api.AudioIds.ToArray());
                }
                api.AudioAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Error = "No answer from getzonekeeper.com." });
                Assert.Equal(ApiKind.Offline, (await session.AudioAsync("b21", new MemoryStream(), null, Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(ZkStatus.Connected, session.Snapshot().Status);
                api.AudioAnswers.Enqueue(new ApiResult { Kind = ApiKind.TooMany, Status = 429 });
                Assert.Equal(ApiKind.TooMany, (await session.AudioAsync("b21", new MemoryStream(), null, Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(ZkStatus.Connected, session.Snapshot().Status);
                api.AudioAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403, Error = "not_paid" });
                Assert.Equal(ApiKind.NotPaid, (await session.AudioAsync("b21", new MemoryStream(), null, Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(ZkStatus.Paused, session.Snapshot().Status); Assert.NotNull(settings.Token);
                // Trades still go out of the paused queue only after the hourly check; the key stays.
                Assert.Equal(Fixtures.Time + 3600000, session.Snapshot().NextTryMs);
            }
        }
        private sealed class Progress : IProgress<int>
        {
            private readonly Action<int> report;
            public Progress(Action<int> onReport) { report = onReport; }
            public void Report(int value) { report(value); }
        }
    }
}
