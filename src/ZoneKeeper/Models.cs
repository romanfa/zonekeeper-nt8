// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public enum ZkStatus { NotConnected, Checking, Connected, Paused, Offline, OtherInstance }
    public enum ApiKind { Ok, NotConnected, NotPaid, CodeInvalid, TooManyDevices, TooManyTries, TooMany, BadRequest, Offline }

    public static class Helpers
    {
        public const string LiveBaseUrl = "https://getzonekeeper.com";
        public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        public static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        // A length prefix prevents collisions when an account or execution ID contains '|'.
        public static string PairKey(string a, string b) { return (a ?? "").Length.ToString(CultureInfo.InvariantCulture) + ":" + (a ?? "") + (b ?? ""); }
        public static string SafeId(string raw)
        {
            raw = raw ?? "";
            if (Regex.IsMatch(raw, @"\A[\x21-\x7e]{1,80}\z")) return raw;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var text = new StringBuilder("h:");
                for (int i = 0; i < 20; i++) text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
        public static string Cut(string raw, int limit)
        {
            var text = new StringBuilder();
            foreach (char c in raw ?? "") if (!char.IsControl(c)) text.Append(c);
            string clean = text.ToString().Trim();
            if (clean.Length > limit) clean = clean.Substring(0, limit).TrimEnd();
            // Do not leave half a UTF-16 surrogate at the cut.
            if (clean.Length > 0 && char.IsHighSurrogate(clean[clean.Length - 1])) clean = clean.Substring(0, clean.Length - 1);
            return clean;
        }
        public static string Cut64(string raw) { return Cut(raw, 64); }
        public static long Cents(decimal amount) { return checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)); }
        public static string CurrencyIso(string raw)
        {
            switch (raw)
            {
                case "UsDollar": case "USD": return "USD";
                case "CanadianDollar": case "CAD": return "CAD";
                case "Euro": case "EUR": return "EUR";
                case "BritishPound": case "GBP": return "GBP";
                case "AustralianDollar": case "AUD": return "AUD";
                case "SwissFranc": case "CHF": return "CHF";
                case "NewZealandDollar": case "NZD": return "NZD";
                default: return null;
            }
        }
        public static bool NormalizeCode(string raw, out string code)
        {
            code = Regex.Replace((raw ?? "").ToUpperInvariant(), @"[\s-]", "");
            if (code.Length == 10 && code.StartsWith("ZK", StringComparison.Ordinal)) code = code.Substring(2);
            if (code.Length != 8) return false;
            foreach (char c in code) if (CodeAlphabet.IndexOf(c) < 0) return false;
            return true;
        }
        public static bool TryToUtcMs(DateTime time, TimeZoneInfo ntZone, out long milliseconds)
        {
            DateTime value = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
            try { milliseconds = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, ntZone ?? TimeZoneInfo.Local)).ToUnixTimeMilliseconds(); return true; }
            catch (ArgumentException)
            {
                try { milliseconds = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, TimeZoneInfo.Local)).ToUnixTimeMilliseconds(); return true; }
                catch (ArgumentException) { milliseconds = 0; return false; }
            }
        }
        public static string LocalDate(long ms, TimeZoneInfo zone)
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    [DataContract]
    public sealed class Fill
    {
        [DataMember] public string Op { get; set; }
        [DataMember] public string Account { get; set; }
        [DataMember] public string Instrument { get; set; }
        [DataMember] public string ExecId { get; set; }
        [DataMember] public bool IsBuy { get; set; }
        [DataMember] public int Qty { get; set; }
        [DataMember] public decimal Price { get; set; }
        [DataMember] public decimal Commission { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public long TimeMs { get; set; }
        [DataMember] public int PositionAfter { get; set; }
        [DataMember] public long ArrivedMs { get; set; }
        [DataMember] public decimal PointValue { get; set; }
        [DataMember] public string Currency { get; set; }
        [DataMember] public bool IsPlayback { get; set; }
        public string Key { get { return Helpers.PairKey(Account, Instrument); } }
    }

    [DataContract]
    public sealed class TradeRecord
    {
        [DataMember(Name = "account")] public string Account { get; set; }
        [DataMember(Name = "instrument")] public string Instrument { get; set; }
        [DataMember(Name = "side")] public string Side { get; set; }
        [DataMember(Name = "qty")] public int Qty { get; set; }
        [DataMember(Name = "entry_time")] public long EntryTime { get; set; }
        [DataMember(Name = "exit_time")] public long ExitTime { get; set; }
        [DataMember(Name = "entry_price")] public double EntryPrice { get; set; }
        [DataMember(Name = "exit_price")] public double ExitPrice { get; set; }
        [DataMember(Name = "pnl_cents")] public long PnlCents { get; set; }
        [DataMember(Name = "commission_cents")] public long CommissionCents { get; set; }
        [DataMember(Name = "currency")] public string Currency { get; set; }
        [DataMember(Name = "point_value")] public double PointValue { get; set; }
        [DataMember(Name = "exit_exec_id")] public string ExitExecId { get; set; }
        [DataMember(Name = "entry_exec_ids", EmitDefaultValue = false)] public List<string> EntryExecIds { get; set; }
        [DataMember(Name = "exit_name", EmitDefaultValue = false)] public string ExitName { get; set; }
        [DataMember(Name = "local_date")] public string LocalDate { get; set; }
        // Local log metadata only; never part of the server JSON.
        public string LogExitNames { get; set; }
        public TradeProvenance Provenance { get; set; }
        public bool RequiresValidation { get; set; }
        public string Key { get { return Helpers.PairKey(Account, ExitExecId); } }
    }

    [DataContract]
    public sealed class AppliedFill
    {
        [DataMember] public string ExecId { get; set; }
        [DataMember] public int SourceQty { get; set; }
        [DataMember] public int AppliedQty { get; set; }
        [DataMember] public decimal Price { get; set; }
        [DataMember] public decimal SourceCommission { get; set; }
        [DataMember] public bool IsBuy { get; set; }
        [DataMember] public bool IsEntry { get; set; }
        [DataMember] public ExecutionEvidence Evidence { get; set; }
    }

    [DataContract]
    public sealed class OpenTrade
    {
        [DataMember] public string Account { get; set; }
        [DataMember] public string Instrument { get; set; }
        [DataMember] public bool IsLong { get; set; }
        [DataMember] public int Net { get; set; }
        [DataMember] public int EntryQty { get; set; }
        [DataMember] public decimal EntryValue { get; set; }
        [DataMember] public int ExitQty { get; set; }
        [DataMember] public decimal ExitValue { get; set; }
        [DataMember] public decimal Commission { get; set; }
        [DataMember] public long EntryTime { get; set; }
        [DataMember] public List<string> EntryExecIds { get; set; }
        [DataMember] public decimal PointValue { get; set; }
        [DataMember] public string Currency { get; set; }
        [DataMember] public bool UnknownStart { get; set; }
        [DataMember] public bool Broken { get; set; }
        [DataMember] public List<AppliedFill> Fills { get; set; }
        [DataMember] public List<string> ExitNames { get; set; }
        [DataMember] public List<ExecutionEvidence> ExecutionCheckpoint { get; set; }
        [DataMember] public decimal? PositionAveragePrice { get; set; }
        [DataMember] public bool EvidenceLimitExceeded { get; set; }
        // A restored entry must be proved by a current snapshot before it can close safely.
        public bool AwaitingContinuity { get; set; }
        public string Key { get { return Helpers.PairKey(Account, Instrument); } }
        public bool Sendable { get { return !UnknownStart && !Broken && !AwaitingContinuity && !EvidenceLimitExceeded && Helpers.CurrencyIso(Currency) != null; } }

        public static OpenTrade Start(Fill fill, int quantity, decimal commission)
        {
            var trade = new OpenTrade {
                Account = fill.Account, Instrument = fill.Instrument, IsLong = fill.IsBuy,
                EntryTime = fill.TimeMs, PointValue = fill.PointValue, Currency = Helpers.CurrencyIso(fill.Currency),
                EntryExecIds = new List<string>(), Fills = new List<AppliedFill>(), ExitNames = new List<string>()
            };
            trade.AddEntry(fill, quantity, commission);
            return trade;
        }
        private void Remember(Fill fill, int quantity, bool entry)
        {
            if (Fills.Count >= ExecutionEvidence.MaximumCount) { EvidenceLimitExceeded = true; return; }
            Fills.Add(new AppliedFill { ExecId = fill.ExecId, SourceQty = fill.Qty, AppliedQty = quantity,
                Price = fill.Price, SourceCommission = fill.Commission, IsBuy = fill.IsBuy, IsEntry = entry,
                Evidence = ExecutionEvidence.From(fill) });
        }
        public void AddEntry(Fill fill, int quantity, decimal commission)
        {
            long remaining = Math.Abs((long)Net);
            PositionAveragePrice = remaining == 0 ? fill.Price :
                ((PositionAveragePrice ?? fill.Price) * remaining + quantity * fill.Price) / (remaining + quantity);
            Net = checked(Net + (IsLong ? quantity : -quantity));
            EntryQty = checked(EntryQty + quantity); EntryValue += quantity * fill.Price; Commission += commission;
            if (EntryExecIds.Count < 10) EntryExecIds.Add(Helpers.SafeId(fill.ExecId));
            Remember(fill, quantity, true);
        }
        public void AddExit(Fill fill, int quantity, decimal commission)
        {
            Net = checked(Net - (IsLong ? quantity : -quantity));
            ExitQty = checked(ExitQty + quantity); ExitValue += quantity * fill.Price; Commission += commission;
            string name = Helpers.Cut(fill.Name, 32);
            if (name.Length != 0 && !ExitNames.Contains(name)) ExitNames.Add(name);
            Remember(fill, quantity, false);
        }
        public TradeRecord ToRecord(Fill closingFill, TimeZoneInfo localZone)
        {
            decimal gross = (IsLong ? ExitValue - EntryValue : EntryValue - ExitValue) * PointValue;
            long commission = Helpers.Cents(Commission);
            string exitName = Helpers.Cut(closingFill.Name, 32);
            return new TradeRecord {
                Account = Helpers.Cut64(Account), Instrument = Helpers.Cut64(Instrument), Side = IsLong ? "long" : "short",
                Qty = EntryQty, EntryTime = EntryTime, ExitTime = closingFill.TimeMs,
                EntryPrice = (double)Math.Round(EntryValue / EntryQty, 8), ExitPrice = (double)Math.Round(ExitValue / ExitQty, 8),
                CommissionCents = commission, PnlCents = Helpers.Cents(gross) - commission,
                Currency = Currency, PointValue = (double)PointValue, ExitExecId = Helpers.SafeId(closingFill.ExecId),
                EntryExecIds = new List<string>(EntryExecIds), ExitName = exitName.Length == 0 ? null : exitName,
                LocalDate = Helpers.LocalDate(closingFill.TimeMs, localZone), LogExitNames = string.Join(", ", ExitNames),
                Provenance = new TradeProvenance { Account = Account, Instrument = Instrument,
                    Executions = Fills.ConvertAll(x => x.Evidence == null ? null : x.Evidence.Copy()) }
            };
        }
    }

    [DataContract]
    public sealed class ExecutionEvidence
    {
        public const int MaximumCount = 5000;
        [DataMember] public string ExecId { get; set; }
        [DataMember] public string Instrument { get; set; }
        [DataMember] public bool IsBuy { get; set; }
        [DataMember] public int Qty { get; set; }
        [DataMember] public decimal Price { get; set; }
        [DataMember] public decimal Commission { get; set; }
        [DataMember] public long TimeMs { get; set; }
        [DataMember] public int PositionAfter { get; set; }
        [DataMember] public decimal PointValue { get; set; }
        [DataMember] public string Currency { get; set; }
        [DataMember] public string Name { get; set; }
        public static ExecutionEvidence From(Fill fill)
        {
            return new ExecutionEvidence { ExecId = fill.ExecId, Instrument = fill.Instrument, IsBuy = fill.IsBuy,
                Qty = fill.Qty, Price = fill.Price, Commission = fill.Commission, TimeMs = fill.TimeMs,
                PositionAfter = fill.PositionAfter, PointValue = fill.PointValue, Currency = fill.Currency, Name = fill.Name ?? "" };
        }
        public ExecutionEvidence Copy() { return (ExecutionEvidence)MemberwiseClone(); }
        public bool Complete { get { return !string.IsNullOrEmpty(ExecId) && !string.IsNullOrEmpty(Instrument) &&
            Qty > 0 && TimeMs > 0 && PointValue > 0 && Currency != null; } }
        public bool Matches(ExecutionEvidence other)
        {
            return other != null && ExecId == other.ExecId && Instrument == other.Instrument && IsBuy == other.IsBuy &&
                Qty == other.Qty && Price == other.Price && Commission == other.Commission && TimeMs == other.TimeMs &&
                PositionAfter == other.PositionAfter && PointValue == other.PointValue && Currency == other.Currency && Name == other.Name;
        }
    }
    [DataContract]
    public sealed class TradeProvenance
    {
        [DataMember] public string Account { get; set; }
        [DataMember] public string Instrument { get; set; }
        [DataMember] public List<ExecutionEvidence> Executions { get; set; }
        public bool Complete { get { return !string.IsNullOrEmpty(Account) && !string.IsNullOrEmpty(Instrument) &&
            Executions != null && Executions.Count > 0 && Executions.Count <= ExecutionEvidence.MaximumCount &&
            Executions.TrueForAll(x => x != null && x.Complete && x.Instrument == Instrument); } }
        public bool AffectedBy(Fill fill)
        {
            // Without full provenance an unrelated correction cannot safely be ruled out.
            return !Complete || (Account == fill.Account && Executions.Exists(x => x.ExecId == fill.ExecId));
        }
        public bool VerifiedBy(string account, AccountSnapshot snapshot)
        {
            if (!Complete || Account != account || !snapshot.Consistent) return false;
            var history = snapshot.Executions.FindAll(x => x.Instrument == Instrument);
            int first = history.FindIndex(x => x.ExecId == Executions[0].ExecId);
            if (first < 0 || first + Executions.Count > history.Count) return false;
            // Every fill in the closed trade's interval must be accounted for, including partial exits.
            for (int i = 0; i < Executions.Count; i++) if (!Executions[i].Matches(history[first + i])) return false;
            return true;
        }
    }

    public sealed class PositionSnapshot
    {
        public string Instrument { get; set; }
        public int Net { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal PointValue { get; set; }
        public string Currency { get; set; }
    }

    [DataContract] public sealed class ClaimBody
    {
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "device_name")] public string DeviceName { get; set; }
        [DataMember(Name = "addon_version")] public string AddonVersion { get; set; }
    }
    [DataContract] public sealed class ClaimAnswer
    {
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "device_id")] public long DeviceId { get; set; }
        [DataMember(Name = "user_email_masked")] public string EmailMasked { get; set; }
    }
    [DataContract] public sealed class PingAnswer
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "device_id")] public long DeviceId { get; set; }
        [DataMember(Name = "plan_status")] public string PlanStatus { get; set; }
        [DataMember(Name = "server_time")] public long ServerTime { get; set; }
    }
    [DataContract] public sealed class RejectedTrade
    {
        [DataMember(Name = "index")] public int Index { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }
    [DataContract] public sealed class TradesAnswer
    {
        [DataMember(Name = "inserted")] public int Inserted { get; set; }
        [DataMember(Name = "duplicates")] public int Duplicates { get; set; }
        [DataMember(Name = "rejected")] public List<RejectedTrade> Rejected { get; set; }
    }
    [DataContract] public sealed class TradesBody
    {
        [DataMember(Name = "trades")] public List<TradeRecord> Trades { get; set; }
    }
    [DataContract] public sealed class ErrorAnswer
    {
        [DataMember(Name = "error")] public string Error { get; set; }
    }
    [DataContract] public sealed class DisconnectAnswer
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
    }
    public sealed class ApiResult
    {
        public ApiKind Kind { get; set; }
        public int Status { get; set; }
        public string Error { get; set; }
        // Typed body: callers never need to retain or log the raw response JSON.
        public object Body { get; set; }
        public TimeSpan? RetryAfter { get; set; }
    }
}
