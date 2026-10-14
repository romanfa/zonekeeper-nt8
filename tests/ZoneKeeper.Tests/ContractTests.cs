using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NinjaTrader.NinjaScript.AddOns.ZoneKeeper;
using Xunit;

namespace ZoneKeeper.Tests
{
    public sealed class ContractTests
    {
        [Fact]
        public void T12_TradeJsonHasExactlyServerKeysAndNoLogMetadata()
        {
            var trade = Fixtures.Trade(); trade.LogExitNames = "Target1, Stop1";
            using (var doc = JsonDocument.Parse(Json.Write(trade)))
            {
                string[] expected = { "account", "instrument", "side", "qty", "entry_time", "exit_time", "entry_price", "exit_price", "pnl_cents", "commission_cents", "currency", "point_value", "exit_exec_id", "entry_exec_ids", "exit_name", "local_date" };
                Assert.Equal(expected.OrderBy(x => x), doc.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
                Assert.Equal(4340, doc.RootElement.GetProperty("pnl_cents").GetInt64()); Assert.Equal("Target1", doc.RootElement.GetProperty("exit_name").GetString());
            }
            trade.ExitName = null; Assert.DoesNotContain("exit_name", Json.Write(trade));
        }
        [Theory]
        [InlineData("zk-ab3d-9kxp", "AB3D9KXP")]
        [InlineData(" AB3D 9KXP ", "AB3D9KXP")]
        [InlineData("ZKAB3D9KXP", "AB3D9KXP")]
        [InlineData("zkzk2345", "ZKZK2345")]
        public void CodeNormalizationMatchesServer(string input, string expected)
        { string value; Assert.True(Helpers.NormalizeCode(input, out value)); Assert.Equal(expected, value); }
        [Theory]
        [InlineData("ZK-ABID-9KXP")]
        [InlineData("ZK-ABOD-9KXP")]
        [InlineData("ZK-AB0D-9KXP")]
        [InlineData("ZK-AB1D-9KXP")]
        [InlineData("ZK-ABCD-9KX")]
        [InlineData("")]
        public void InvalidCodeShapeIsRejected(string input)
        { string value; Assert.False(Helpers.NormalizeCode(input, out value)); }
        [Fact]
        public void EveryCodeAlphabetSymbolIsAccepted()
        {
            Assert.Equal("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", Helpers.CodeAlphabet);
            foreach (char sign in Helpers.CodeAlphabet) { string code; Assert.True(Helpers.NormalizeCode(new string(sign, 8), out code)); }
        }
        [Fact]
        public void SettingsFakeProtectionHostBindingAndAccountsPersist()
        {
            var files = new MemoryFiles(); var protector = new FakeProtector();
            var settings = new Settings("settings", files, protector, delegate { });
            Assert.Equal(Helpers.LiveBaseUrl, settings.BaseUrl); settings.SetAccount("Sim102", false); Assert.True(settings.IsEnabled("new-account"));
            Assert.True(settings.SetToken((ClaimAnswer)Fixtures.Claim().Body)); Assert.DoesNotContain(Fixtures.Token, files.Data["settings"]);
            var loaded = new Settings("settings", files, protector, delegate { }); Assert.Equal(Fixtures.Token, loaded.Token); Assert.False(loaded.IsEnabled("Sim102"));
            var data = Json.Read<SettingsData>(files.Data["settings"]); data.BaseUrl = "https://example.test"; files.Data["settings"] = Json.Write(data);
            Assert.Null(new Settings("settings", files, protector, delegate { }).Token);
        }
        [Fact]
        public void OtherWindowsUserOrPcDecryptionErrorClearsToken()
        {
            var files = new MemoryFiles(); Fixtures.Settings(files, true);
            var log = new List<string>(); var loaded = new Settings("settings", files, new FakeProtector { Fail = true }, log.Add);
            Assert.Null(loaded.Token); Assert.True(loaded.IsDirty); Assert.Contains("Connect this PC with a new code.", log);
            loaded.TrySave(); Assert.DoesNotContain(Fixtures.Token, files.Data["settings"]);
        }
        [Theory]
        [InlineData("http://example.test")]
        [InlineData("https://example.test/path")]
        [InlineData("https://example.test/?x=1")]
        [InlineData("https://example.test/#x")]
        [InlineData("https://person@example.test")]
        public void UnsafeBaseUrlsFallBackToExactLiveOrigin(string origin)
        {
            var files = new MemoryFiles(); files.Data["settings"] = Json.Write(new SettingsData { BaseUrl = origin });
            Assert.Equal("https://getzonekeeper.com", new Settings("settings", files, new FakeProtector(), delegate { }).BaseUrl);
        }
        [Theory]
        [InlineData(null, false)]
        [InlineData("https://getzonekeeper.com", false)]
        [InlineData("https://www.getzonekeeper.com", false)]
        [InlineData("https://getzonekeeper.com:443", false)]
        [InlineData("https://example.test", true)]
        public void PlaybackCannotBeEnabledOnLiveEvenByEditingSettings(string origin, bool expected)
        {
            var files = new MemoryFiles(); files.Data["settings"] = Json.Write(new SettingsData { BaseUrl = origin, AllowPlayback = true });
            Assert.Equal(expected, new Settings("settings", files, new FakeProtector(), delegate { }).AllowPlayback);
        }
        [Fact]
        public void TokenDeletionRemovesAllCredentialFieldsAndRetainsQueue()
        {
            var files = new MemoryFiles(); var settings = Fixtures.Settings(files, true); var q = new OutboxQueue("queue", files, delegate { }); q.Add(Fixtures.Trade());
            settings.DeleteToken(); Assert.Null(settings.Token); var data = Json.Read<SettingsData>(files.Data["settings"]);
            Assert.Null(data.TokenProtected); Assert.Null(data.TokenBaseUrl); Assert.Null(data.EmailMasked); Assert.Equal(0, data.DeviceId); Assert.Equal(1, q.Count);
        }
        [Fact]
        public void FailedSettingsSaveRetainsNewValueAndRetries()
        {
            var files = new MemoryFiles { FailWrites = x => true }; var settings = Fixtures.Settings(files);
            Assert.False(settings.SetToken((ClaimAnswer)Fixtures.Claim().Body)); Assert.True(settings.IsDirty); Assert.Equal(Fixtures.Token, settings.Token);
            files.FailWrites = null; Assert.True(settings.TrySave()); Assert.Equal(Fixtures.Token, Fixtures.Settings(files).Token);
        }
        [Fact]
        public void EventLogRedactsSecretsKeeps20AndRotatesAtOneMegabyte()
        {
            using (var folder = new TestFolder())
            {
                var log = new EventLog(folder.Path); log.ProtectSecret("AB3D9KXP"); log.ProtectSecret("sensitive-exception-value");
                log.Write(Fixtures.Token + " ZK-AB3D-9KXP AB3D9KXP");
                log.Error("HTTP failed", new InvalidOperationException("sensitive-exception-value"));
                string text = File.ReadAllText(Path.Combine(folder.Path, "zonekeeper.log"));
                Assert.DoesNotContain(Fixtures.Token, text); Assert.DoesNotContain("AB3D9KXP", text); Assert.DoesNotContain("sensitive-exception-value", text);
                for (int i = 0; i < 25; i++) log.Write("event " + i);
                Assert.Equal(20, log.Recent.Count); Assert.Contains("event 24", log.Recent[0]);
                File.WriteAllText(Path.Combine(folder.Path, "zonekeeper.log"), new string('x', 1024 * 1024)); log.Write("rotated");
                Assert.True(File.Exists(Path.Combine(folder.Path, "zonekeeper.1.log"))); Assert.Contains("rotated", File.ReadAllText(Path.Combine(folder.Path, "zonekeeper.log")));
            }
        }
        [Fact]
        public void NinjaTimeConversionUsesSelectedZoneWithoutDoubleLocalConversion()
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"); long ms;
            Assert.True(Helpers.TryToUtcMs(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Local), zone, out ms));
            Assert.Equal(DateTimeOffset.Parse("2026-10-14T16:00:00Z").ToUnixTimeMilliseconds(), ms);
        }
    }
}
