using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public class ResetTests
    {
        private sealed class Api : IResetApi
        {
            public Queue<ApiResult> Answers = new Queue<ApiResult>();
            public Func<Task<ApiResult>> GetOverride;
            public Task<ApiResult> GetResetsAsync(string token, long after, CancellationToken ct) { return GetOverride == null ? Task.FromResult(Answers.Dequeue()) : GetOverride(); }
            public Task<ApiResult> OpenResetAsync(string token, long id, CancellationToken ct) { throw new NotImplementedException(); }
            public void Page(long member, long cursor, params long[] ids) { Answers.Enqueue(new ApiResult { Kind = ApiKind.Ok, Body = new ResetsAnswer { MemberId = member, NextCursor = cursor, Pending = ids.Select(x => new PendingReset { TradeId = x }).ToList() } }); }
        }
        [Fact] public async Task CacheRequiresConfirmationAndOfflineDoesNotEraseIt()
        {
            var files = new MemoryFiles(); var api = new Api(); var c = new ResetCoordinator("resets", files);
            api.Page(1, 0, 12); Assert.True(await c.RefreshAsync(api, "https://getzonekeeper.com", "token", CancellationToken.None));
            Assert.Equal(12, c.NextTradeId); var restored = new ResetCoordinator("resets", files); Assert.Equal(0, restored.NextTradeId);
            api.Answers.Enqueue(new ApiResult { Kind = ApiKind.Offline });
            Assert.False(await restored.RefreshAsync(api, "https://getzonekeeper.com", "token", CancellationToken.None));
            Assert.Contains("12", files.Data["resets"]); Assert.Equal(0, restored.NextTradeId);
            api.Page(1, 0, 12); await restored.RefreshAsync(api, "https://getzonekeeper.com", "token", CancellationToken.None); Assert.Equal(12, restored.NextTradeId);
        }
        [Fact] public async Task EarlyCloseSuppressesUntilManualReopenOrNewTrade()
        {
            var api = new Api(); var c = new ResetCoordinator("r", new MemoryFiles());
            api.Page(1, 0, 1); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None); c.Dismiss(); Assert.Equal(0, c.NextTradeId);
            api.Page(1, 0, 1); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None); Assert.Equal(0, c.NextTradeId);
            c.Reopen(); Assert.Equal(1, c.NextTradeId); c.Dismiss();
            api.Page(1, 0, 1, 2); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None); Assert.Equal(1, c.NextTradeId);
        }
        [Fact] public async Task PartialPaginationAndMemberSwitchNeverReplaceQueue()
        {
            var files = new MemoryFiles(); var api = new Api(); var c = new ResetCoordinator("r", files);
            api.Page(1, 0, 1); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None); string before = files.Data["r"];
            api.Page(1, 100, Enumerable.Range(1,100).Select(x => (long)x).ToArray()); api.Page(2, 0, 101);
            Assert.False(await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None)); Assert.Equal(before, files.Data["r"]);
            api.Page(2, 0, 8); Assert.True(await c.RefreshAsync(api, "https://getzonekeeper.com", "new-token", CancellationToken.None)); Assert.Equal(8, c.NextTradeId);
        }
        [Fact] public async Task DiskFailureDoesNotBreakServerQueueRecovery()
        {
            var files = new MemoryFiles { FailWrites = p => true }; var c = new ResetCoordinator("r", files); var api = new Api(); api.Page(1, 0, 4);
            Assert.True(await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None)); Assert.Equal(4, c.NextTradeId);
        }
        [Fact] public async Task EntirePaginationCompletesBeforeRemovingOldPending()
        {
            var files = new MemoryFiles(); var c = new ResetCoordinator("r", files); var api = new Api();
            api.Page(1, 0, 5); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None);
            string before = files.Data["r"];
            api.Page(1, 101, Enumerable.Range(2, 100).Select(x => (long)x).ToArray()); api.Answers.Enqueue(new ApiResult { Kind = ApiKind.Offline });
            Assert.False(await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None)); Assert.Equal(before, files.Data["r"]);
            api.Page(1, 101, Enumerable.Range(2, 100).Select(x => (long)x).ToArray()); api.Page(1, 0, 102);
            Assert.True(await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None)); Assert.Equal(101, c.PendingCount);
            api.Page(1, 0); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None); Assert.Equal(0, c.PendingCount);
        }
        [Fact] public async Task ChangedOriginOrTokenCannotExposeOldCacheWhileOffline()
        {
            var files = new MemoryFiles(); var api = new Api(); var c = new ResetCoordinator("r", files);
            api.Page(1, 0, 5); await c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None);
            api.Answers.Enqueue(new ApiResult { Kind = ApiKind.Offline });
            await c.RefreshAsync(api, "https://test.example", "another", CancellationToken.None);
            Assert.Equal(0, c.PendingCount); Assert.Equal(0, c.NextTradeId);
            Assert.DoesNotContain("another", files.Data["r"]);
        }
        [Fact] public void MissingPaginationCompletionIsNotAValidEmptyQueue()
        {
            ResetsAnswer parsed;
            Assert.False(Json.TryRead("{\"member_id\":1,\"pending\":[]}", out parsed));
        }
        [Fact] public async Task SuspensionDiscardsAnAlreadyInFlightAuthenticatedResponse()
        {
            var files = new MemoryFiles(); var c = new ResetCoordinator("r", files); var api = new Api();
            var response = new TaskCompletionSource<ApiResult>(); api.GetOverride = () => response.Task;
            var refreshing = c.RefreshAsync(api, "https://getzonekeeper.com", "t", CancellationToken.None);
            c.Suspend(); response.SetResult(new ApiResult { Kind = ApiKind.Ok, Body = new ResetsAnswer { MemberId = 1, NextCursor = 0, Pending = new List<PendingReset> { new PendingReset { TradeId = 8 } } } });
            Assert.False(await refreshing); Assert.Equal(0, c.PendingCount); Assert.False(files.Exists("r"));
        }
        [Fact] public async Task ApiOptInAndResetEndpointsPreserveLegacyAcknowledgements()
        {
            var handler = new FakeHttp(); int calls = 0;
            handler.Send = async (request, ct) => {
                calls++; Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
                Assert.Equal(Fixtures.Token, request.Headers.Authorization.Parameter);
                string response;
                if (calls == 1) { Assert.Equal("/api/nt/trades", request.RequestUri.AbsolutePath); Assert.True(Json.Read<TradesBody>(await request.Content.ReadAsStringAsync()).AutoReset); response = "{\"inserted\":1,\"duplicates\":0,\"rejected\":[]}"; }
                else if (calls == 2) { Assert.Equal("/api/nt/resets?after=100", request.RequestUri.PathAndQuery); response = "{\"member_id\":1,\"pending\":[],\"next_cursor\":0}"; }
                else { Assert.Equal("/api/nt/resets/open", request.RequestUri.AbsolutePath); Assert.Equal(10, Json.Read<ResetOpenBody>(await request.Content.ReadAsStringAsync()).TradeId); response = "{\"url\":\"https://getzonekeeper.com/app/#nt-reset=abc\",\"expires_at\":1800000000000}"; }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(response) };
            };
            using (var client = new ApiClient(null, handler, delegate { }))
            {
                Assert.Equal(ApiKind.Ok, (await client.SendTradesAsync(Fixtures.Token, new List<TradeRecord>(), CancellationToken.None)).Kind);
                Assert.IsType<ResetsAnswer>((await client.GetResetsAsync(Fixtures.Token, 100, CancellationToken.None)).Body);
                Assert.IsType<ResetOpenAnswer>((await client.OpenResetAsync(Fixtures.Token, 10, CancellationToken.None)).Body);
            }
        }
        [Theory]
        [InlineData("https://getzonekeeper.com/app/#nt-reset=abc", true)]
        [InlineData("https://evil.com/app/#nt-reset=abc", false)]
        [InlineData("https://getzonekeeper.com/app/?token=secret#nt-reset=abc", false)]
        [InlineData("https://getzonekeeper.com/app/#nt-reset=zkd_secret", false)]
        public void HandoffIsRestricted(string url, bool expected) { Assert.Equal(expected, ResetCoordinator.ValidHandoff("https://getzonekeeper.com", url)); }
    }
}
