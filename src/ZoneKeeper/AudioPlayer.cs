// WINDOWS ONLY: WPF (not in the Mac tests). One System.Windows.Media.MediaPlayer for the voice of a reset.
// Made and used on the window's dispatcher only. Close() stops the sound and deletes the temp file.
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class AudioPlayer
    {
        private readonly MediaPlayer player = new MediaPlayer();
        private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private string path;
        public event Action Opened;
        public event Action Ended;
        public event Action<Exception> Failed;
        // The play position in seconds, 4 times a second while playing.
        public event Action<double> Position;
        public bool IsPaused { get; private set; }
        public bool IsOpen { get { return path != null; } }
        public AudioPlayer()
        {
            player.MediaOpened += delegate { Raise(Opened); };
            player.MediaEnded += delegate { Raise(Ended); };
            player.MediaFailed += (sender, e) => { var handler = Failed; if (handler != null) try { handler(e.ErrorException); } catch (Exception) { } };
            tick.Tick += delegate {
                var handler = Position;
                if (handler == null || path == null) return;
                try { handler(player.Position.TotalSeconds); } catch (Exception) { }
            };
        }
        public double Seconds { get { return path == null ? 0 : player.Position.TotalSeconds; } }
        public double Duration { get { return path != null && player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0; } }
        // Opens the temp file; Play() comes from the Opened event. Volume = the Windows volume (no control in v1).
        public void Open(string file)
        {
            Close();
            path = file; IsPaused = false;
            player.Open(new Uri(file));
        }
        public void Play() { if (path == null) return; player.Play(); IsPaused = false; tick.Start(); }
        public void Pause() { if (path == null) return; player.Pause(); IsPaused = true; }
        // Stop the sound, let go of the file, delete it. The media thread may still hold the file for a moment,
        // so a failed delete is tried again every 250 ms for 5 s on a background thread. The wipe at the next
        // engine start / window open / stop is the last net.
        public void Close()
        {
            tick.Stop();
            string file = path; path = null; IsPaused = false;
            try { player.Stop(); player.Close(); } catch (Exception) { }
            DeleteWithRetries(file);
        }
        public static void DeleteWithRetries(string file)
        {
            if (string.IsNullOrEmpty(file) || TryDelete(file)) return;
            Task.Run(async () => {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    if (TryDelete(file)) return;
                }
                ZkEngine.Note("Could not delete the voice temp file yet. It is wiped at the next start.");
            });
        }
        private static bool TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); return true; }
            catch (Exception) { return false; }
        }
        private static void Raise(Action handler) { if (handler != null) try { handler(); } catch (Exception) { } }
    }
}
