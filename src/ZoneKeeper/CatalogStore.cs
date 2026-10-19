// PURE: no NinjaTrader or WPF types (tests compile this file).
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    [DataContract]
    public sealed class CatalogFile
    {
        [DataMember(Name = "etag")] public string ETag { get; set; }
        [DataMember(Name = "fetched_at")] public long FetchedAt { get; set; }
        [DataMember(Name = "catalog")] public Catalog Catalog { get; set; }
    }

    // The copy of the reset texts on disk (catalog.json). Refreshed every 6 hours; an old copy is used offline.
    public sealed class CatalogStore
    {
        public const long RefreshMs = 6 * 3600000L;
        private readonly object gate = new object();
        private readonly string path;
        private readonly IAtomicFileStore files;
        private readonly Action<string> log;
        private CatalogFile data;
        public CatalogStore(string filePath, IAtomicFileStore store, Action<string> writeLog)
        {
            path = filePath; files = store; log = writeLog ?? delegate { };
            try
            {
                CatalogFile loaded;
                if (files.Exists(path) && Json.TryRead(files.Read(path), out loaded) && Valid(loaded.Catalog)) data = loaded;
                else if (files.Exists(path)) log("Could not read the reset texts. They are loaded again.");
            }
            catch (Exception) { log("Could not read the reset texts. They are loaded again."); }
        }
        public Catalog Current { get { lock (gate) return data == null ? null : data.Catalog; } }
        public string ETag { get { lock (gate) return data == null ? null : data.ETag; } }
        public long FetchedAt { get { lock (gate) return data == null ? 0 : data.FetchedAt; } }
        public bool NeedsRefresh(long nowMs) { lock (gate) return data == null || nowMs - data.FetchedAt >= RefreshMs || data.FetchedAt > nowMs; }
        // A new catalog from the site. A bad one is refused and the old one stays.
        public bool Save(Catalog catalog, string etag, long nowMs)
        {
            if (!Valid(catalog)) { log("The reset texts from the site look wrong. The old ones stay."); return false; }
            lock (gate)
            {
                data = new CatalogFile { ETag = etag ?? ("\"" + catalog.Version + "\""), FetchedAt = nowMs, Catalog = catalog };
                return Write();
            }
        }
        // 304 Not Modified: the texts are still good, only the 6-hour clock starts again.
        public void Touch(long nowMs) { lock (gate) { if (data == null) return; data.FetchedAt = nowMs; Write(); } }
        // A refused save named logic_id or thought: the texts changed on the site. Load them again soon.
        public void MarkStale() { lock (gate) { if (data != null) { data.FetchedAt = 0; Write(); } } }
        private bool Write()
        {
            try { files.Write(path, Json.Write(data)); return true; }
            catch (Exception) { log(UiText.SaveFailed); return false; }
        }
        // What the panel needs: 10 tilts, the 3 trade triggers with thoughts of 3 lines whose audio is known, 7 parts, 4 after texts.
        public static bool Valid(Catalog c)
        {
            if (c == null || c.Tilts == null || c.Triggers == null || c.Parts == null || c.After == null || c.Audios == null) return false;
            if (c.Tilts.Count != 10) return false;
            for (int n = 1; n <= 10; n++) { var tilt = c.Tilt(n); if (tilt == null || string.IsNullOrEmpty(tilt.Name)) return false; }
            if (c.Triggers.Count < 3) return false;
            foreach (string key in new[] { "stop_loss", "winning_trade", "break_even" }) if (c.Trigger(key) == null) return false;
            foreach (var trigger in c.Triggers)
            {
                if (trigger == null || string.IsNullOrEmpty(trigger.Key) || string.IsNullOrEmpty(trigger.Label) || trigger.Thoughts == null || trigger.Thoughts.Count == 0) return false;
                foreach (var thought in trigger.Thoughts) if (!ValidThought(c, thought)) return false;
                if (!ValidThought(c, trigger.Other)) return false;
            }
            if (c.Parts.Count != 7) return false;
            foreach (var part in c.Parts) if (part == null || string.IsNullOrEmpty(part.Name) || part.Sec <= 0) return false;
            if (string.IsNullOrEmpty(c.After.Green) || string.IsNullOrEmpty(c.After.Orange) || string.IsNullOrEmpty(c.After.OrangeTwice) || string.IsNullOrEmpty(c.After.Red)) return false;
            foreach (var audio in c.Audios.Values)
            {
                if (audio == null || audio.Marks == null || audio.Marks.Steps == null || audio.Marks.Breaths == null) return false;
                if (audio.Marks.Steps.Count != 7 || audio.Marks.Breaths.Count != 3 || audio.Marks.End <= 0) return false;
            }
            return true;
        }
        private static bool ValidThought(Catalog c, CatalogThought thought)
        {
            if (thought == null || string.IsNullOrEmpty(thought.Key) || string.IsNullOrEmpty(thought.Text) || thought.Lines == null || thought.Lines.Count != 3) return false;
            foreach (var line in thought.Lines)
                if (line == null || string.IsNullOrEmpty(line.Id) || string.IsNullOrEmpty(line.Text) || c.Audio(line.Audio) == null) return false;
            return true;
        }
    }
}
