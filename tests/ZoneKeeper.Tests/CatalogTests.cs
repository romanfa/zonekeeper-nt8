using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    // The reset texts from the site: catalog.json, ETag, 6-hour refresh, offline use (plan 31 section 2.2).
    public sealed class CatalogTests
    {
        private const string Base = Helpers.LiveBaseUrl;
        private const long Hour = 3600000L;

        [Fact]
        public void C1_FixtureParsesAndIsValid()
        {
            var c = Fixtures.Catalog();
            Assert.Equal("0123456789abcdef", c.Version); Assert.Equal(7, c.Rules.RedLine); Assert.Equal(7, c.RedLine);
            Assert.Equal(10, c.Tilts.Count); Assert.Equal("Tightening up", c.Tilt(4).Name); Assert.Equal("o", c.Tilt(4).Band);
            Assert.Equal(3, c.Triggers.Count); Assert.Equal("Stop loss", c.Trigger("stop_loss").Label);
            Assert.Equal("What happened after your break-even exit?", c.Trigger("break_even").Question);
            Assert.Equal("make_it_back.2", c.Trigger("stop_loss").Thoughts[0].Lines[1].Id); Assert.Equal("b21", c.Trigger("stop_loss").Thoughts[0].Lines[1].Audio);
            Assert.Equal("other", c.Trigger("stop_loss").Other.Key); Assert.Equal(3, c.Trigger("stop_loss").Other.Lines.Count);
            Assert.Equal(7, c.Parts.Count); Assert.Equal(121, c.Parts.Sum(x => x.Sec)); Assert.Equal("breathe", c.Parts[2].Kind); Assert.Null(c.Parts[0].Say);
            Assert.Equal("Red line: tilt 7 or higher. Stop for today.", c.After.Red);
            Assert.Equal(18, c.Audios.Count); Assert.Equal(7, c.Audio("b1").Marks.Steps.Count); Assert.Equal(3, c.Audio("b1").Marks.Breaths.Count); Assert.Equal(153.09, c.Audio("b1").Marks.End);
            Assert.Null(c.Audio("nope")); Assert.Null(c.Audio(null));
            Assert.True(CatalogStore.Valid(c));
            // Round trip: the audios dictionary stays a plain JSON object.
            string again = Json.Write(c);
            Assert.Contains("\"audios\":{\"b1\":{", again.Replace(" ", ""));
            Assert.True(CatalogStore.Valid(Json.Read<Catalog>(again)));
        }
        [Fact]
        public void C1_OldSettingsAndOutboxFilesStillParseWithTheDictionarySetting()
        {
            var files = new MemoryFiles();
            files.Write("settings", "{\"version\":1,\"disabled_accounts\":[\"Sim102\"],\"debug_fills\":true,\"auto_reset\":false}");
            var settings = Fixtures.Settings(files);
            Assert.False(settings.IsEnabled("Sim102")); Assert.True(settings.DebugFills); Assert.False(settings.AutoReset);
            files.Write("queue", Json.Write(new OutboxLine { Key = "k", QueuedAt = 1, Trade = Fixtures.Trade(), Provenance = Fixtures.Trade().Provenance }) + "\n");
            var queue = new OutboxQueue("queue", files, delegate { });
            Assert.Equal(1, queue.Count);
        }
        [Fact]
        public void C2_ValidRefusesBrokenCatalogs()
        {
            Assert.False(CatalogStore.Valid(null));
            var c = Fixtures.Catalog(); c.Trigger("stop_loss").Thoughts[0].Lines[1].Audio = "b999"; Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Parts.RemoveAt(6); Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Tilts.RemoveAt(9); Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Triggers.RemoveAll(x => x.Key == "break_even"); Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Trigger("stop_loss").Other = null; Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Trigger("stop_loss").Thoughts[0].Lines.RemoveAt(2); Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Audios["b1"].Marks.Breaths.RemoveAt(0); Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.Audios["b1"].Marks.End = 0; Assert.False(CatalogStore.Valid(c));
            c = Fixtures.Catalog(); c.After.OrangeTwice = ""; Assert.False(CatalogStore.Valid(c));
            Assert.False(CatalogStore.Valid(Json.Read<Catalog>("{\"version\":\"x\"}")));
        }
        [Fact]
        public void C3_StoreSavesLoadsTouchesAndRefreshesEverySixHours()
        {
            var files = new MemoryFiles(); var logs = new List<string>();
            var store = new CatalogStore("catalog.json", files, logs.Add);
            Assert.Null(store.Current); Assert.Null(store.ETag); Assert.True(store.NeedsRefresh(Fixtures.Time));
            Assert.True(store.Save(Fixtures.Catalog(), "\"0123456789abcdef\"", Fixtures.Time));
            Assert.Equal("\"0123456789abcdef\"", store.ETag); Assert.Equal(Fixtures.Time, store.FetchedAt); Assert.False(store.NeedsRefresh(Fixtures.Time + 6 * Hour - 1));
            Assert.True(store.NeedsRefresh(Fixtures.Time + 6 * Hour));
            Assert.Contains("\"etag\":\"\\\"0123456789abcdef\\\"\"", files.Read("catalog.json"));
            // A new engine reads the file back.
            var again = new CatalogStore("catalog.json", files, logs.Add);
            Assert.Equal("0123456789abcdef", again.Current.Version); Assert.Equal("\"0123456789abcdef\"", again.ETag);
            // 304: the texts stay, the clock starts again.
            again.Touch(Fixtures.Time + 7 * Hour);
            Assert.Equal("0123456789abcdef", again.Current.Version); Assert.False(again.NeedsRefresh(Fixtures.Time + 12 * Hour));
            // A bad answer keeps the old file.
            var broken = Fixtures.Catalog(); broken.Parts.Clear();
            Assert.False(again.Save(broken, "\"ffff\"", Fixtures.Time + 8 * Hour));
            Assert.Equal("0123456789abcdef", again.Current.Version); Assert.Equal("\"0123456789abcdef\"", again.ETag);
            Assert.Contains(logs, x => x.Contains("look wrong"));
            // The ETag falls back to the version when the header was missing.
            Assert.True(again.Save(Fixtures.Catalog(), null, Fixtures.Time)); Assert.Equal("\"0123456789abcdef\"", again.ETag);
            // A refused save that named logic_id: load again soon.
            again.MarkStale(); Assert.True(again.NeedsRefresh(Fixtures.Time)); Assert.NotNull(again.Current);
        }
        [Fact]
        public void C3_DamagedFileIsIgnoredAndLogged()
        {
            var files = new MemoryFiles(); var logs = new List<string>();
            files.Write("catalog.json", "{\"etag\":\"x\",\"fetched_at\":1,\"catalog\":{\"version\":\"v\"}}");
            var store = new CatalogStore("catalog.json", files, logs.Add);
            Assert.Null(store.Current); Assert.True(store.NeedsRefresh(Fixtures.Time)); Assert.Single(logs);
            files.Write("catalog.json", "not json");
            Assert.Null(new CatalogStore("catalog.json", files, logs.Add).Current);
        }
        [Fact]
        public async Task C5_CatalogRequestSendsBearerAndIfNoneMatchWithoutAQuery()
        {
            var handler = new FakeHttp(); int calls = 0;
            handler.Send = (request, cancellation) => {
                calls++;
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/api/nt/catalog", request.RequestUri.AbsolutePath); Assert.Equal("", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.Equal(Fixtures.Token, request.Headers.Authorization.Parameter);
                Assert.Null(request.Content);
                if (calls == 1)
                {
                    Assert.False(request.Headers.Contains("If-None-Match"));
                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.CatalogJson) };
                    ok.Headers.ETag = new EntityTagHeaderValue("\"0123456789abcdef\"");
                    return Task.FromResult(ok);
                }
                Assert.Equal("\"0123456789abcdef\"", string.Join(",", request.Headers.GetValues("If-None-Match")));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            };
            using (var client = new ApiClient(Base, handler, delegate { }))
            {
                var first = await client.CatalogAsync(Fixtures.Token, null, CancellationToken.None);
                Assert.Equal(ApiKind.Ok, first.Kind); Assert.Equal(200, first.Status); Assert.Equal("\"0123456789abcdef\"", first.ETag);
                Assert.True(CatalogStore.Valid((Catalog)first.Body));
                var second = await client.CatalogAsync(Fixtures.Token, first.ETag, CancellationToken.None);
                Assert.Equal(ApiKind.Ok, second.Kind); Assert.Equal(304, second.Status); Assert.Null(second.Body);
            }
        }
        [Fact]
        public async Task C5_TodayAndResetRequestsHaveTheRightShape()
        {
            var handler = new FakeHttp(); string resetBody = null;
            handler.Send = async (request, cancellation) => {
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
                if (request.RequestUri.AbsolutePath == "/api/nt/today")
                {
                    Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("?date=2026-10-19", request.RequestUri.Query); Assert.Null(request.Content);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"date\":\"2026-10-19\",\"server_time\":1791902520000,\"trades\":[{\"id\":812,\"account\":\"Sim101\",\"instrument\":\"ES 12-26\",\"side\":\"short\",\"qty\":2,\"exit_time\":1791902520000,\"pnl_cents\":21250,\"currency\":\"USD\",\"entry_id\":640},{\"id\":813,\"account\":\"Sim101\",\"instrument\":\"ES 12-26\",\"side\":\"long\",\"qty\":1,\"exit_time\":1791902520001,\"pnl_cents\":-1250,\"currency\":\"USD\",\"entry_id\":null}]}") };
                }
                Assert.Equal("/api/nt/resets", request.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("", request.RequestUri.Query);
                Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);
                resetBody = await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"entry\":{\"id\":641,\"trade_id\":813,\"tilt_before\":5,\"tilt_after\":3}}") };
            };
            using (var client = new ApiClient(Base, handler, delegate { }))
            {
                var today = await client.TodayAsync(Fixtures.Token, "2026-10-19", CancellationToken.None);
                var answer = (TodayAnswer)today.Body;
                Assert.Equal(2, answer.Trades.Count); Assert.Equal(640, answer.Trades[0].EntryId); Assert.Null(answer.Trades[1].EntryId); Assert.Equal(-1250, answer.Trades[1].PnlCents);
                var session = new ResetSession(Fixtures.Catalog(), Fixtures.TodayTrade(813), TimeZoneInfo.Utc);
                session.RateBefore(5, Fixtures.Time); session.PickThought(session.Thoughts[0]); session.PickLine(session.Thought.Lines[1]); session.FinishAudio();
                var saved = await client.SaveResetAsync(Fixtures.Token, session.RateAgain(3, Fixtures.Time + 180000), CancellationToken.None);
                Assert.Equal(ApiKind.Ok, saved.Kind); Assert.Equal(813, ((ResetAnswer)saved.Body).Entry.TradeId); Assert.Equal(641, ((ResetAnswer)saved.Body).Entry.Id);
                Assert.Contains("\"trade_id\":813", resetBody); Assert.Contains("\"request_id\":\"" + session.RequestId + "\"", resetBody); Assert.DoesNotContain("zkd_", resetBody);
            }
        }
        [Theory]
        [InlineData(409, "{\"error\":\"this trade already has a reset\"}", ApiKind.AlreadyDone)]
        [InlineData(409, "{\"error\":\"other\"}", ApiKind.Offline)]
        [InlineData(400, "{\"error\":\"logic_id does not fit\"}", ApiKind.BadRequest)]
        [InlineData(403, "{\"error\":\"not_paid\"}", ApiKind.NotPaid)]
        [InlineData(401, "{\"error\":\"not_connected\"}", ApiKind.NotConnected)]
        [InlineData(429, "{\"error\":\"too many\"}", ApiKind.TooMany)]
        public async Task C5_ResetAnswersMap(int status, string body, ApiKind kind)
        {
            var handler = new FakeHttp();
            handler.Send = (request, cancellation) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
            using (var client = new ApiClient(Base, handler, delegate { }))
            {
                var result = await client.SaveResetAsync(Fixtures.Token, new ResetBody { RequestId = "r", TradeId = 1 }, CancellationToken.None);
                Assert.Equal(kind, result.Kind); Assert.Equal(status, result.Status);
            }
        }
        [Fact]
        public async Task C6_CatalogAndToday403PauseAnd401DeleteTheKeyThroughTheSession()
        {
            using (var folder = new TestFolder())
            {
                var files = new MemoryFiles(); var settings = Fixtures.Settings(files, true); var api = new FakeApi();
                var session = new SessionCoordinator(settings, new OutboxQueue("queue", files, delegate { }), api, new EventLog(folder.Path));
                int keyRemoved = 0; session.KeyRemoved += () => keyRemoved++;
                await session.ProcessAsync(Fixtures.Time, CancellationToken.None);
                var ok = await session.CatalogAsync(null, Fixtures.Time, CancellationToken.None);
                Assert.Equal(ApiKind.Ok, ok.Kind); Assert.Equal(new string[] { null }, api.CatalogETags.ToArray());
                api.CatalogAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotPaid, Status = 403, Error = "not_paid" });
                Assert.Equal(ApiKind.NotPaid, (await session.CatalogAsync("\"e\"", Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(ZkStatus.Paused, session.Snapshot().Status); Assert.NotNull(settings.Token); Assert.Equal(0, keyRemoved);
                api.TodayAnswers.Enqueue(new ApiResult { Kind = ApiKind.Offline, Status = 503 });
                Assert.Equal(ApiKind.Offline, (await session.TodayAsync("2026-10-19", Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(ZkStatus.Paused, session.Snapshot().Status);
                api.TodayAnswers.Enqueue(new ApiResult { Kind = ApiKind.NotConnected, Status = 401, Error = "not_connected" });
                Assert.Equal(ApiKind.NotConnected, (await session.TodayAsync("2026-10-19", Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Null(settings.Token); Assert.Equal(1, keyRemoved); Assert.Equal(ZkStatus.NotConnected, session.Snapshot().Status);
                // No key: no call at all.
                Assert.Equal(ApiKind.NotConnected, (await session.TodayAsync("2026-10-19", Fixtures.Time, CancellationToken.None)).Kind);
                Assert.Equal(2, api.Todays); Assert.Equal(new[] { "2026-10-19", "2026-10-19" }, api.TodayDates.ToArray());
            }
        }
    }
}
