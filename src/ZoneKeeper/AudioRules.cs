// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;
using System.IO;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    // Rules for the voice audio: downloaded into memory for each reset, played from one temp file, never cached.
    public static class AudioRules
    {
        public const long MinBytes = 100 * 1024;
        public const long MaxBytes = 10 * 1024 * 1024;
        public const int DownloadTimeoutMs = 60000;

        // The only check: the whole file came (same as Content-Length when sent) and its size makes sense.
        public static bool SizeOk(long received, long? contentLength)
        {
            if (received < MinBytes || received > MaxBytes) return false;
            return !contentLength.HasValue || contentLength.Value == received;
        }

        // %TEMP%\ZoneKeeper — the only place a temp file is ever written.
        public static string TempFolder(string tempRoot) { return Path.Combine(tempRoot ?? "", "ZoneKeeper"); }
        public static string TempName() { return Guid.NewGuid().ToString("N") + ".mp3"; }

        // Files to wipe at engine start and when the window opens: every .mp3 in the temp folder.
        public static List<string> Leftovers(IEnumerable<string> files)
        {
            var result = new List<string>();
            foreach (string file in files ?? new string[0])
                if (file != null && file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) result.Add(file);
            return result;
        }
    }
}
