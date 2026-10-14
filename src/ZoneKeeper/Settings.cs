// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public interface ITokenProtector
    {
        byte[] Protect(byte[] plaintext);
        byte[] Unprotect(byte[] encrypted);
    }
    [DataContract]
    public sealed class SettingsData
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "base_url")] public string BaseUrl { get; set; }
        [DataMember(Name = "token_protected")] public string TokenProtected { get; set; }
        [DataMember(Name = "token_base_url")] public string TokenBaseUrl { get; set; }
        [DataMember(Name = "device_id")] public long DeviceId { get; set; }
        [DataMember(Name = "email_masked")] public string EmailMasked { get; set; }
        [DataMember(Name = "disabled_accounts")] public List<string> DisabledAccounts { get; set; }
        [DataMember(Name = "allow_playback")] public bool AllowPlayback { get; set; }
        [DataMember(Name = "debug_fills")] public bool DebugFills { get; set; }
        public SettingsData() { Version = 1; DisabledAccounts = new List<string>(); }
    }
    public sealed class Settings
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly ITokenProtector protector;
        private readonly Action<string> log;
        private SettingsData data;
        private string token;
        private bool dirty;
        public Settings(string filePath, IAtomicFileStore store, ITokenProtector tokenProtector, Action<string> writeLog)
        {
            path = filePath; files = store; protector = tokenProtector; log = writeLog ?? delegate { };
            data = new SettingsData();
            if (files.Exists(path))
            {
                SettingsData loaded;
                if (Json.TryRead(files.Read(path), out loaded) && loaded.Version == 1) data = loaded;
                else log("Could not read settings. Connect this PC with a new code.");
            }
            if (data.DisabledAccounts == null) data.DisabledAccounts = new List<string>();
            string normalized;
            if (!TryBaseUrl(data.BaseUrl, out normalized)) { log("Ignored an invalid server address."); data.BaseUrl = null; }
            else data.BaseUrl = normalized == Helpers.LiveBaseUrl ? null : normalized;
            if (!string.IsNullOrEmpty(data.TokenProtected))
            {
                try
                {
                    string bound;
                    if (!TryBaseUrl(data.TokenBaseUrl, out bound) || bound != BaseUrl) throw new InvalidOperationException();
                    token = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(data.TokenProtected)));
                    if (!ValidToken(token)) throw new InvalidOperationException();
                }
                catch (Exception) { token = null; ClearData(); dirty = true; log("Connect this PC with a new code."); }
            }
        }
        public static bool ValidToken(string value) { return Regex.IsMatch(value ?? "", @"\Azkd_[A-Za-z0-9_-]{43}\z"); }
        public static bool TryBaseUrl(string value, out string normalized)
        {
            normalized = Helpers.LiveBaseUrl;
            if (string.IsNullOrWhiteSpace(value)) return true;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) return false;
            normalized = uri.GetLeftPart(UriPartial.Authority); return true;
        }
        public string BaseUrl { get { lock (gate) return data.BaseUrl ?? Helpers.LiveBaseUrl; } }
        public bool IsLive { get { return new Uri(BaseUrl).Host.Equals("getzonekeeper.com", StringComparison.OrdinalIgnoreCase) || new Uri(BaseUrl).Host.Equals("www.getzonekeeper.com", StringComparison.OrdinalIgnoreCase); } }
        public bool AllowPlayback { get { lock (gate) return data.AllowPlayback && !IsLive; } }
        public bool DebugFills { get { lock (gate) return data.DebugFills; } }
        public string Token { get { lock (gate) return token; } }
        public string EmailMasked { get { lock (gate) return data.EmailMasked; } }
        public bool IsDirty { get { lock (gate) return dirty; } }
        public bool IsEnabled(string account) { lock (gate) return !data.DisabledAccounts.Contains(account); }
        public bool SetAccount(string account, bool enabled)
        {
            lock (gate)
            {
                if (enabled) data.DisabledAccounts.RemoveAll(x => x == account);
                else if (!data.DisabledAccounts.Contains(account)) data.DisabledAccounts.Add(account);
                dirty = true; return SaveLocked();
            }
        }
        public bool SetToken(ClaimAnswer answer)
        {
            lock (gate)
            {
                if (!ValidToken(answer.Token) || answer.DeviceId <= 0) throw new ArgumentException("Invalid claim answer.");
                data.TokenProtected = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(answer.Token)));
                data.TokenBaseUrl = BaseUrl; data.DeviceId = answer.DeviceId; data.EmailMasked = Helpers.Cut64(answer.EmailMasked);
                token = answer.Token; dirty = true; return SaveLocked();
            }
        }
        private void ClearData() { data.TokenProtected = null; data.TokenBaseUrl = null; data.DeviceId = 0; data.EmailMasked = null; }
        public bool DeleteToken()
        {
            lock (gate) { token = null; ClearData(); dirty = true; return SaveLocked(); }
        }
        public bool TrySave() { lock (gate) return !dirty || SaveLocked(); }
        private bool SaveLocked()
        {
            try { files.Write(path, Json.Write(data)); dirty = false; return true; }
            catch (Exception) { log(UiText.SaveFailed); return false; }
        }
    }
}
