using System.Security.Cryptography;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Windows-only adapter; the net8 test project deliberately excludes this file.
    public sealed class WindowsTokenProtector : ITokenProtector
    {
        private static readonly byte[] entropy = Encoding.UTF8.GetBytes("ZoneKeeper.NT8");
        public byte[] Protect(byte[] plaintext) { return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser); }
        public byte[] Unprotect(byte[] encrypted) { return ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser); }
    }
}
