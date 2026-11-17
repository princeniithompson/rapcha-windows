using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v20 (--selftest-v20 outDir), run inside the real app with the internet switched off: a quiet
    /// microphone (speech peaking at -30 dBFS, like the user's Realtek mic) goes through the real voice-typing path —
    /// one microphone open, the automatic mic boost, then built-in Windows speech or Whisper Tiny (the no-AVX "sse" build
    /// the Celeron N3350 uses, 2 threads, on a pretend 4 GB / 2-core laptop). Checks the words, that the microphone opens
    /// exactly once per dictation, the per-second boost lines, the CPU guard, the Diagnostics header and Settings.
    /// Exit: 0 ok, 131 built-in, 132 Whisper, 133 microphone opened more than once, 134 boost lines, 135 page / diagnostics, 1 crash.
    /// </summary>
    internal sealed class V20SelfTest
    {
        private const string Passage = "The boy is coming. He is coming home from school today. We will meet at the market on Saturday morning.";
        private readonly RapchaApp _app;
        private readonly string _out;
        private readonly List<string> _notes = new List<string>();
        private readonly string _models, _empty;

        public V20SelfTest(RapchaApp app, string outDir)
        {
            _app = app;
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _models = Path.Combine(Path.GetTempPath(), "rapcha-v20-models");
            _empty = Path.Combine(Path.GetTempPath(), "rapcha-v20-empty");
        }

        private static void Log(string s) => AppStorage.Log("Selftest v20: " + s);

        public void Start()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private async void Run()
        {
            int code;
            string boost = AppStorage.LoadMicBoost();
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e.GetType().Name + " " + e.StackTrace); code = 1; }
            if (_app.IsRecording) _app.CancelForTest();
            WaveInRecorder.Simulated = null;
            Connectivity.ForceOffline = null;
            ModelStore.FolderOverride = null;
            DeviceProbe.Override = null;
            AppStorage.SaveMicBoost(boost);
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            try { if (Directory.Exists(_empty)) Directory.Delete(_empty, true); } catch { }
            try { File.WriteAllLines(Path.Combine(_out, "v20-results.txt"), _notes); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            _app.ExitForTest();
        }

        private async Task<int> Steps()
        {
            DiagnosticsInfo.Start("v20 self-test");
            var cf = CpuProbe.Features;
            _notes.Add("This runner's CPU: " + cf.Name + " - " + cf + " -> Whisper build " + CpuFeatures.Choose(cf) + (CpuProbe.CpuidNote.Length > 0 ? " (" + CpuProbe.CpuidNote + ")" : ""));
            var n3350 = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Sse41 = true, Sse42 = true, Name = "Celeron N3350" };
            _notes.Add("CPU guard: a Celeron N3350 (SSE4.2, no AVX) gets the " + CpuFeatures.Choose(n3350) + " build; a CPU without SSSE3 gets " +
                       CpuFeatures.Choose(new CpuFeatures { Sse2 = true, Sse3 = true }) + " (Whisper switched off with a message, built-in speech used)");
            _notes.Add("Rapcha process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit"));

            // A pretend Celeron N3350 laptop: 4 GB, 2 cores, no AVX; the no-AVX Whisper build; no internet.
            DeviceProbe.Override = DeviceFacts.Parse("ram=4096,free=2500,cores=2,avx=0,avx2=0");
            WhisperNative.ForceVariant = "sse";
            Connectivity.ForceOffline = true;
            OfflineSettings.Fallback = true;

            var speech = Synthesize(Passage, 48000);
            double peak = PeakOf(speech);
            double gain = Math.Pow(10, -30 / 20.0) / Math.Max(1e-6, peak);   // speech peaks at -30 dBFS
            var quiet = ToDriverFloat(speech, gain, 20);
            WaveInRecorder.Simulated = new WaveInRecorder.FakeDevice { Native = quiet, Rate = 48000, Channels = 2, Kind = SampleKind.Float32, Name = "simulated quiet Realtek microphone (speech peaks -30 dBFS)" };
            double audioSeconds = speech.Length / 4.0 / 48000;

            int r;
            AppStorage.SaveMicBoost("off");
            var off = await Dictate("built-in, mic boost Off", _empty, audioSeconds);
            AppStorage.SaveMicBoost("auto");
            var bi = await Dictate("built-in, mic boost Auto", _empty, audioSeconds);
            if (bi == null) return 131;
            if ((r = CheckSession(bi, true)) != 0) return r;
            double offAcc = off?.Accuracy ?? 0;
            _notes.Add("Built-in Windows speech, quiet microphone (-30 dBFS): with mic boost Off it got " + Pct(offAcc) + " of the words; with mic boost Auto " + Pct(bi.Accuracy) +
                       " (wanted at least 50%). Microphone opened " + bi.Opens + " time(s) in that dictation (wanted 1). " + bi.AgcSummary);
            if (bi.Accuracy < 0.5) return 131;

            // Whisper Tiny on the no-AVX build
            ModelStore.FolderOverride = _models;
            if (!ModelStore.IsInstalled(WhisperModels.Tiny))
            {
                var sw0 = Stopwatch.StartNew();
                var outcome = await ModelStore.DownloadAsync(WhisperModels.Tiny, (a, b) => { }, CancellationToken.None);
                _notes.Add("Downloaded Whisper Tiny (English) with Rapcha's own downloader and SHA-256 check in " + sw0.ElapsedMilliseconds / 1000 + " s: " + outcome);
                if (outcome != ModelStore.Outcome.Done) return 132;
            }
            var wh = await Dictate("whisper tiny.en sse build, mic boost Auto", _models, audioSeconds);
            if (wh == null) return 132;
            if ((r = CheckSession(wh, false)) != 0) return r;
            _notes.Add("Whisper Tiny (no-AVX build " + WhisperNative.Variant + ", " + WhisperNative.BuildFlags("sse") + "), quiet microphone (-30 dBFS), mic boost Auto: " + Pct(wh.Accuracy) +
                       " of the words (wanted at least 75%); stopping to typed took " + wh.FinishMs + " ms. Microphone opened " + wh.Opens + " time(s). system_info=" + WhisperNative.SystemInfo());
            if (wh.Accuracy < 0.75 || !wh.Engine.Contains("whisper")) return 132;

            // Real-time factor of the sse build with 2 threads and 1 thread on this runner's CPU (the whole passage at once)
            foreach (int th in new[] { 2, 1 })
            {
                var ctx = WhisperNative.LoadModel(ModelStore.PathOf(WhisperModels.Tiny), th);
                var pcm16 = PcmResampler16k(speech);
                var sw = Stopwatch.StartNew();
                var text = WhisperNative.Transcribe(ctx, OfflineChunker.ToFloat(WavFile.ToSamples(pcm16)), th, "");
                sw.Stop();
                WhisperNative.FreeModel(ctx);
                double rtf = sw.Elapsed.TotalSeconds / audioSeconds;
                _notes.Add("Whisper Tiny sse build, " + th + " thread(s), " + audioSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s of audio: " + sw.ElapsedMilliseconds + " ms, real-time factor " +
                           rtf.ToString("0.00", CultureInfo.InvariantCulture) + " on this runner (accuracy " + Pct(Accuracy(text)) + ")");
                Log("rtf threads=" + th + " ms=" + sw.ElapsedMilliseconds + " rtf=" + rtf.ToString("0.00", CultureInfo.InvariantCulture));
            }
            return await Pages();
        }

        private sealed class Result { public string Text, Engine, AgcSummary; public int Opens; public double Accuracy; public long FinishMs; public List<string> Lines; }

        private async Task<Result> Dictate(string label, string modelFolder, double audioSeconds)
        {
            ModelStore.FolderOverride = modelFolder;
            Directory.CreateDirectory(modelFolder);
            int before = DiagLog.Count;
            _app.StartForTest();
            await Task.Delay(300);
            if (!_app.IsRecording) { _notes.Add(label + ": dictation did not start"); Log(label + ": not started"); return null; }
            await Task.Delay((int)(audioSeconds * 1000) + 1800);
            var sw = Stopwatch.StartNew();
            await _app.FinishForTest();
            sw.Stop();
            await Task.Delay(500);
            var lines = DiagLog.Snapshot().Skip(before).ToList();
            var res = new Result
            {
                Text = _app.LastResultForTest ?? "",
                Opens = _app.LastSessionMicOpens,
                FinishMs = sw.ElapsedMilliseconds,
                Lines = lines,
                AgcSummary = _app.LastAgc?.Summary ?? "no boost",
                Engine = lines.LastOrDefault(l => l.Contains(" END "))?.Contains("engine=whisper") == true ? "whisper" : "built-in",
            };
            res.Accuracy = Accuracy(res.Text);
            Log(label + ": accuracy=" + res.Accuracy.ToString("0.00", CultureInfo.InvariantCulture) + " chars=" + res.Text.Length + " opens=" + res.Opens + " finish_ms=" + res.FinishMs + " engine=" + res.Engine + " " + res.AgcSummary);
            return res;
        }

        private int CheckSession(Result r, bool builtIn)
        {
            if (r.Opens != 1) { _notes.Add("Microphone opened " + r.Opens + " times in one dictation (wanted exactly 1)"); return 133; }
            var agc = r.Lines.Where(l => l.Contains("[agc]") && l.Contains("gain_db")).ToList();
            bool own = r.Lines.Any(l => l.Contains("built-in speech listens to the Windows default microphone itself"));
            bool endLine = r.Lines.Any(l => l.Contains(" END ") && l.Contains("mic_opens_this_session=1"));
            _notes.Add((builtIn ? "Built-in" : "Whisper") + " dictation: " + agc.Count + " per-second mic boost lines in Diagnostics, e.g. \"" + (agc.Count > 2 ? Strip(agc[2]) : "-") +
                       "\"; built-in speech opening the microphone itself: " + own + "; END line has mic_opens_this_session=1: " + endLine);
            if (agc.Count < 3 || own || !endLine) return 134;
            return 0;
        }

        private static string Strip(string l) { int i = l.IndexOf("agc second"); return i >= 0 ? l.Substring(i) : l; }

        private async Task<int> Pages()
        {
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(1500);
            var boost = w.Settings.MicBoostPicker;
            for (Control c = boost.Parent; c != null; c = c.Parent)
                if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(boost); break; }
            await Task.Delay(300);
            bool autoShown = (boost.SelectedItem as string) == "Auto (recommended)";
            w.SavePng(Path.Combine(_out, "v20-settings-mic-boost.png"));
            w.ShowPage(MainWindow.Page.Diagnostics);
            await Task.Delay(1200);
            w.SavePng(Path.Combine(_out, "v20-diagnostics.png"));
            var report = DiagnosticsInfo.Report(false);
            File.WriteAllText(Path.Combine(_out, "Rapcha-diagnostics-v20-sample.txt"), report);
            bool header = report.Contains("CPU features: SSE2=") && report.Contains("Rapcha process: 32-bit") && report.Contains("Whisper build for this CPU: sse") &&
                          report.Contains("Mic boost for offline typing: Auto") && report.Contains("== Windows modern speech recogniser ==") && report.Contains("[agc]") &&
                          !report.Contains(" [text] ") && !Regex.IsMatch(report, @"AIza[0-9A-Za-z_\-]{20,}");
            var modern = report.Split('\n').FirstOrDefault(l => l.StartsWith("Windows modern speech")) ?? "";
            w.Close();
            await Task.Delay(500);
            bool nothingOpen = MicTracker.Count == 0;
            _notes.Add("Settings shows \"Mic boost for offline typing: Auto (recommended)\": " + autoShown + "; Diagnostics header has CPU features, 32-bit process, Whisper build and flags, mic boost, the modern recogniser check and the boost lines (no key, no words): " + header +
                       "; nothing holds the microphone at the end: " + nothingOpen);
            _notes.Add(modern.Trim());
            Log("pages: auto=" + autoShown + " header=" + header + " nothing_open=" + nothingOpen);
            return autoShown && header && nothingOpen ? 0 : 135;
        }

        // ---------------- audio and scoring ----------------

        private static string Pct(double a) => (a * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        private static readonly string[] Want = Words(Passage);
        private static string[] Words(string s) => Regex.Matches((s ?? "").ToLowerInvariant(), "[a-z]+").Cast<Match>().Select(m => m.Value).ToArray();

        /// <summary>Share of the passage's words found in order (longest common subsequence).</summary>
        internal static double Accuracy(string heard)
        {
            var h = Words(heard);
            var d = new int[Want.Length + 1, h.Length + 1];
            for (int i = 1; i <= Want.Length; i++)
                for (int j = 1; j <= h.Length; j++)
                    d[i, j] = Want[i - 1] == h[j - 1] ? d[i - 1, j - 1] + 1 : Math.Max(d[i - 1, j], d[i, j - 1]);
            return (double)d[Want.Length, h.Length] / Want.Length;
        }

        private static double PeakOf(byte[] stereo16)
        {
            int m = 0;
            for (int i = 0; i + 1 < stereo16.Length; i += 2) m = Math.Max(m, Math.Abs((int)(short)(stereo16[i] | (stereo16[i + 1] << 8))));
            return m / 32768.0;
        }

        private static byte[] PcmResampler16k(byte[] stereo48)
        {
            int frames = stereo48.Length / 4 / 3;
            var o = new byte[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                int sum = 0;
                for (int k = 0; k < 3; k++) { int p = (f * 3 + k) * 4; sum += (short)(stereo48[p] | (stereo48[p + 1] << 8)); }
                short v = (short)(sum / 3);
                o[2 * f] = (byte)v; o[2 * f + 1] = (byte)(v >> 8);
            }
            return o;
        }

        private static byte[] Synthesize(string text, int rate)
        {
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(rate, AudioBitsPerSample.Sixteen, AudioChannel.Stereo));
                var pb = new PromptBuilder();
                pb.AppendBreak(TimeSpan.FromMilliseconds(600));
                foreach (var sentence in Regex.Split(text, @"(?<=\.)\s+")) { pb.AppendText(sentence); pb.AppendBreak(TimeSpan.FromMilliseconds(700)); }
                synth.Speak(pb);
                synth.SetOutputToNull();
                return ms.ToArray();
            }
        }

        private static byte[] ToDriverFloat(byte[] stereo16, double gain, int seed)
        {
            var rnd = new Random(seed);
            int frames = stereo16.Length / 4;
            var o = new byte[frames * 8];
            for (int f = 0; f < frames; f++)
            {
                short s = (short)(stereo16[4 * f] | (stereo16[4 * f + 1] << 8));
                float l = (float)(s / 32768.0 * gain + (rnd.NextDouble() - 0.5) * 0.0006);   // room noise about -70 dBFS
                Buffer.BlockCopy(BitConverter.GetBytes(l), 0, o, f * 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(l * 0.8f), 0, o, f * 8 + 4, 4);
            }
            return o;
        }
    }
}
