using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>
    /// v19 diagnostics: one timeline of everything Rapcha does with audio (and every rapcha.log line), kept in memory
    /// (the last 20,000 lines) for the Diagnostics page and written to a rolling file in %AppData%\Rapcha\diagnostics
    /// (5 MB x 3). Never API keys. Transcribed words only when "Include transcribed text" is switched on.
    /// </summary>
    public static class DiagLog
    {
        public const int MaxLines = 20000;
        private static readonly object Lock = new object();
        private static readonly LinkedList<string> Lines = new LinkedList<string>();
        private static readonly DateTime Started = DateTime.UtcNow;

        /// <summary>Opt-in: engine outputs per chunk are recorded (for debugging only). Off by default and at every start.</summary>
        public static bool IncludeText;
        /// <summary>Where each line also goes (the rolling file). Set by the app.</summary>
        public static Action<string> Sink;
        /// <summary>A new line (the live view).</summary>
        public static event Action<string> Added;
        public static Func<DateTime> Now = () => DateTime.Now;

        public static double UptimeMs => (DateTime.UtcNow - Started).TotalMilliseconds;

        public static void Add(string category, string text)
        {
            var line = Now().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + category + "] " + RedactKeys(text ?? "");
            lock (Lock)
            {
                Lines.AddLast(line);
                while (Lines.Count > MaxLines) Lines.RemoveFirst();
            }
            if (category != "text") { try { Sink?.Invoke(line); } catch { } }   // words never go to the file on disk
            try { Added?.Invoke(line); } catch { }
        }

        public static void Info(string text) => Add("info", text);
        public static void Warn(string text) => Add("warn", text);

        public static void Error(string where, Exception e)
        {
            if (e == null) { Add("error", where); return; }
            Add("error", where + ": " + e.GetType().FullName + " (HResult 0x" + e.HResult.ToString("X8") + ")\n" + (e.StackTrace ?? "(no stack)") +
                (e.InnerException != null ? "\n  inner: " + e.InnerException.GetType().FullName + "\n" + e.InnerException.StackTrace : ""));
        }

        /// <summary>Transcribed words: recorded only when IncludeText is on.</summary>
        public static void Words(string label, string words)
        {
            if (!IncludeText) return;
            Add("text", label + ": \"" + (words ?? "") + "\"");
        }

        public static List<string> Snapshot() { lock (Lock) return Lines.ToList(); }
        public static int Count { get { lock (Lock) return Lines.Count; } }
        public static void Clear() { lock (Lock) Lines.Clear(); }

        /// <summary>The timeline for the saved file: "[text]" lines only when IncludeText is on.</summary>
        public static List<string> ForExport(bool includeText) => Snapshot().Where(l => includeText || !l.Contains(" [text] ")).ToList();

        /// <summary>Removes anything that looks like a Google API key (AIza + 35 characters) or a "key=" URL parameter.</summary>
        public static string RedactKeys(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"AIza[0-9A-Za-z_\-]{20,}", "[key removed]");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"([?&]key=)[^&\s""]+", "$1[removed]");
            return s;
        }
    }

    /// <summary>
    /// v19: who has the microphone open, since when. Every open and close is a timeline line; the Diagnostics page shows
    /// "microphone open now: yes/no, since, by". Owners: voice typing, Settings level meter, Test microphone, built-in
    /// recogniser (SetInputToDefaultAudioDevice).
    /// </summary>
    public static class MicTracker
    {
        public sealed class Holder { public int Id; public string Owner; public string Detail; public DateTime Since; }
        private static readonly object Lock = new object();
        private static readonly Dictionary<int, Holder> Open = new Dictionary<int, Holder>();
        private static int _next;
        public static int Opens { get; private set; }
        public static int Closes { get; private set; }
        public static Func<DateTime> Now = () => DateTime.Now;

        public static int Opened(string owner, string detail = "")
        {
            Holder h;
            lock (Lock)
            {
                h = new Holder { Id = ++_next, Owner = owner, Detail = detail ?? "", Since = Now() };
                Open[h.Id] = h;
                Opens++;
            }
            DiagLog.Add("mic", "OPEN #" + h.Id + " by " + owner + (h.Detail.Length > 0 ? " (" + h.Detail + ")" : "") + "; open now: " + Count);
            return h.Id;
        }

        public static void Closed(int id, string why = "")
        {
            Holder h;
            lock (Lock)
            {
                if (!Open.TryGetValue(id, out h)) return;
                Open.Remove(id);
                Closes++;
            }
            var ms = (Now() - h.Since).TotalMilliseconds;
            DiagLog.Add("mic", "CLOSE #" + id + " by " + h.Owner + " after " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms" + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")") + "; open now: " + Count);
        }

        public static int Count { get { lock (Lock) return Open.Count; } }
        public static List<Holder> Holders { get { lock (Lock) return Open.Values.OrderBy(h => h.Since).ToList(); } }

        public static string NowText()
        {
            var hs = Holders;
            if (hs.Count == 0) return "Microphone open now: no";
            return "Microphone open now: YES - " + string.Join("; ", hs.Select(h => "by " + h.Owner + " since " + h.Since.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                                                                       " (" + (Now() - h.Since).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s)"));
        }

        /// <summary>Tests only.</summary>
        public static void ResetForTest() { lock (Lock) { Open.Clear(); _next = 0; Opens = 0; Closes = 0; } }
    }

    /// <summary>Numbers for one stretch of 16 kHz mono 16-bit audio (no content).</summary>
    public struct AudioStats
    {
        public int Samples;
        public double PeakDb, RmsDb, ClipPercent, SpeechRatio;

        public static double Db(double v) => v <= 0 ? -96 : Math.Max(-96, 20 * Math.Log10(v / 32768.0));

        public static AudioStats Of(short[] s)
        {
            var a = new AudioStats { Samples = s?.Length ?? 0 };
            if (a.Samples == 0) { a.PeakDb = a.RmsDb = -96; return a; }
            int peak = 0, clip = 0; double sum = 0;
            foreach (var v in s) { int x = Math.Abs((int)v); if (x > peak) peak = x; if (x >= 32700) clip++; sum += (double)v * v; }
            a.PeakDb = Db(peak);
            a.RmsDb = Db(Math.Sqrt(sum / a.Samples));
            a.ClipPercent = 100.0 * clip / a.Samples;
            var vad = SpeechVad.Analyze(s);
            a.SpeechRatio = vad.Frames > 0 ? vad.SpeechFrames / (double)vad.Frames : 0;
            return a;
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "samples={0} peak={1:0.0}dBFS rms={2:0.0}dBFS clip={3:0.00}% speech={4:0}%",
            Samples, PeakDb, RmsDb, ClipPercent, SpeechRatio * 100);
    }

    public static class TextStats
    {
        public static int WordCount(string s) => string.IsNullOrWhiteSpace(s) ? 0 : s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>Whisper's "compression ratio": text bytes / zlib-compressed bytes. Above 2.4 means it repeats itself.</summary>
        public static double CompressionRatio(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var raw = Encoding.UTF8.GetBytes(s);
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                return raw.Length / (double)Math.Max(1, ms.Length + 6);   // + zlib header and checksum
            }
        }
    }

    /// <summary>
    /// A text file that never grows without limit: diagnostics.log up to 5 MB, then it becomes diagnostics.1.log
    /// (and .1 becomes .2; the oldest is dropped), so at most 3 files (15 MB).
    /// </summary>
    public sealed class RollingFile
    {
        private readonly object _lock = new object();
        public string Folder { get; }
        public string BaseName { get; }
        public long MaxBytes { get; }
        public int Files { get; }

        public RollingFile(string folder, string baseName = "diagnostics", long maxBytes = 5L * 1024 * 1024, int files = 3)
        {
            Folder = folder; BaseName = baseName; MaxBytes = maxBytes; Files = Math.Max(1, files);
        }

        public string PathOf(int i) => System.IO.Path.Combine(Folder, BaseName + (i == 0 ? "" : "." + i) + ".log");

        public void Append(string line)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var p = PathOf(0);
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length + line.Length + 2 > MaxBytes) Roll();
                    File.AppendAllText(p, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        private void Roll()
        {
            var last = PathOf(Files - 1);
            if (File.Exists(last)) File.Delete(last);
            for (int i = Files - 2; i >= 0; i--)
                if (File.Exists(PathOf(i))) File.Move(PathOf(i), PathOf(i + 1));
        }

        /// <summary>All files, oldest first.</summary>
        public List<string> Existing() => Enumerable.Range(0, Files).Reverse().Select(PathOf).Where(File.Exists).ToList();
    }

    /// <summary>The audio the engines got in the last dictation (16 kHz mono 16-bit), most recent 3 minutes, for "Save last session audio".</summary>
    public sealed class LastAudio
    {
        public const int MaxBytes = 3 * 60 * 16000 * 2;
        private readonly object _lock = new object();
        private readonly LinkedList<byte[]> _parts = new LinkedList<byte[]>();
        private int _bytes;
        public int Bytes { get { lock (_lock) return _bytes; } }

        public void Clear() { lock (_lock) { _parts.Clear(); _bytes = 0; } }

        public void Add(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            lock (_lock)
            {
                _parts.AddLast(pcm); _bytes += pcm.Length;
                while (_bytes > MaxBytes && _parts.Count > 1) { _bytes -= _parts.First.Value.Length; _parts.RemoveFirst(); }
            }
        }

        public byte[] ToArray()
        {
            lock (_lock)
            {
                var o = new byte[_bytes]; int at = 0;
                foreach (var p in _parts) { Buffer.BlockCopy(p, 0, o, at, p.Length); at += p.Length; }
                return o;
            }
        }
    }
}
