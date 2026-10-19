// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public static class Json
    {
        // Simple dictionary format: a Dictionary<string, T> is a plain JSON object ({"b1": {...}}), as the site writes it.
        private static DataContractJsonSerializer For<T>()
        { return new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }); }

        public static string Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                For<T>().WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Read<T>(string text)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)For<T>().ReadObject(stream);
        }

        public static bool TryRead<T>(string text, out T value)
        {
            try { value = Read<T>(text); return value != null; }
            catch (Exception) { value = default(T); return false; }
        }
    }
}
