// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Object identities, not account names alone, distinguish replacement connections.
    public sealed class AccountIdentityMap<T> where T : class
    {
        private readonly object gate = new object();
        private Dictionary<string, T> current = new Dictionary<string, T>(StringComparer.Ordinal);
        private bool frozen;
        public void Update(IEnumerable<KeyValuePair<string, T>> accounts)
        {
            var next = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var account in accounts) next[account.Key] = account.Value;
            lock (gate) if (!frozen) current = next;
        }
        public bool IsCurrent(string name, T source)
        {
            lock (gate)
            { T value; return current.TryGetValue(name, out value) && ReferenceEquals(source, value); }
        }
        public void Freeze()
        {
            // Stop unsubscribes handlers, but queued work must still drain against this final snapshot.
            lock (gate) frozen = true;
        }
    }
}
