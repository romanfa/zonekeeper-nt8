// PURE: no NinjaTrader types (tests compile this file).
using System;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public interface IAtomicFileStore
    {
        bool Exists(string path);
        string Read(string path);
        void Write(string path, string text);
    }

    public sealed class AtomicFileStore : IAtomicFileStore
    {
        public bool Exists(string path) { return File.Exists(path); }
        public string Read(string path) { return File.ReadAllText(path, Encoding.UTF8); }
        public void Write(string path, string text)
        {
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            string temp = path + ".tmp";
            byte[] data = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            // Same-directory rename/replace: a crash leaves the old complete file or the new complete file.
            // A failed replace is never followed by deleting the original.
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
