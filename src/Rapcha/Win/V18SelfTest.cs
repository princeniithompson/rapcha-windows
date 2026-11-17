using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// CI self-test for v18 (--selftest-v18 outDir), run inside the real app. Closes the gap in the v16 tests, which
    /// gave the engines 16 kHz speech directly and so never went through the microphone conversion: here speech is
    /// made at 48 kHz, turned into what a laptop driver hands over (stereo, 32-bit float, quiet, a little noise, in
    /// 50 ms buffers that don't line up with frames) and pushed through WaveInRecorder's own conversion and the
    /// DictationRouter into built-in Windows speech and Whisper Tiny, then the words are scored. Also: silence and
    /// noise give Whisper no invented words; the memory guard on this runner's real numbers with a faked 4 GB laptop;
    /// no new dictation after an automatic stop; Settings → Test microphone (screenshots).
    /// Exit: 0 ok, 111 conversion, 112 built-in speech, 113 Whisper, 114 silence gate, 115 no auto-restart,
    /// 116 Test microphone, 117 memory guard, 1 crash. Only counts, sizes, timings and scores are logged, never words.
    /// </summary>
    internal sealed class V18SelfTest
    {
        private const string Passage =
            "My name is Prince. I am testing voice typing on my laptop today. " +
            "The quick brown fox jumps over the lazy dog. " +
            "Please type this sentence into the search box for me. " +
            "We will meet at the market on Saturday morning.";
        private const string Short = "My name is Prince.";

        private readonly RapchaApp _app;
        private readonly string _out;
        private readonly List<string> _notes = new List<string>();
        private readonly string _models;

        public V18SelfTest(RapchaApp app, string outDir)
        {
            _app = app;
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _models = Path.Combine(Path.GetTempPath(), "rapcha-v18-models");
        }

        private static void Log(string s) => AppStorage.Log("Selftest v18: " + s);

        public void Start()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private async void Run()
        {
            int code;
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e.GetType().Name + " " + e.StackTrace); code = 1; }
            DeviceProbe.Override = null;
            ModelStore.FolderOverride = null;
            MicTestCard.TestMode = false;
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            try { File.WriteAllLines(Path.Combine(_out, "v18-results.txt"), _notes); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            _app.ExitForTest();
        }

        private async Task<int> Steps()
        {
            var speech48 = Synthesize(Passage, 48000, out double seconds);
            var short48 = Synthesize(Short, 48000, out double shortSeconds);
            Log("synthesized: passage_s=" + seconds.ToString("0.0") + " short_s=" + shortSeconds.ToString("0.0"));
            var driver = ToDriverFloat(speech48, 0.15, 7);
            var driverShort = ToDriverFloat(short48, 0.15, 8);
            int r;
            if ((r = ConversionTest(driver, seconds)) != 0) return r;
            if ((r = await BuiltInTest(driver, driverShort)) != 0) return r;
            if ((r = await WhisperTest(driver, driverShort, speech48)) != 0) return r;
            if ((r = await GateTest()) != 0) return r;
            if ((r = await MicTestUi(driverShort)) != 0) return r;
            return 0;
        }

        // ---------------- audio ----------------

        /// <summary>Speech from the Windows voice at the given rate, stereo 16-bit (both channels the same).</summary>
        private static byte[] Synthesize(string text, int rate, out double seconds)
        {
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.Rate = 0;
                synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(rate, AudioBitsPerSample.Sixteen, AudioChannel.Stereo));
                var pb = new PromptBuilder();
                pb.AppendBreak(TimeSpan.FromMilliseconds(600));
                foreach (var sentence in Regex.Split(text, @"(?<=\.)\s+"))
                {
                    pb.AppendText(sentence);
                    pb.AppendBreak(TimeSpan.FromMilliseconds(700));
                }
                synth.Speak(pb);
                synth.SetOutputToNull();
                var b = ms.ToArray();
                seconds = b.Length / (double)(rate * 4);
                return b;
            }
        }

        /// <summary>
        /// What a laptop microphone driver delivers in shared mode: interleaved 32-bit float, quiet (gain, e.g. 0.15 =
        /// -16 dB), a little hiss, and the second microphone of the array slightly weaker and 2 samples later.
        /// </summary>
        private static byte[] ToDriverFloat(byte[] stereo16, double gain, int seed)
        {
            var rnd = new Random(seed);
            int frames = stereo16.Length / 4;
            var o = new byte[frames * 8];
            short S(int f) => f < 0 ? (short)0 : (short)(stereo16[4 * f] | (stereo16[4 * f + 1] << 8));
            for (int f = 0; f < frames; f++)
            {
                float l = (float)(S(f) / 32768.0 * gain + (rnd.NextDouble() - 0.5) * 0.002);
                float rr = (float)(S(f - 2) / 32768.0 * gain * 0.8 + (rnd.NextDouble() - 0.5) * 0.002);
                Buffer.BlockCopy(BitConverter.GetBytes(l), 0, o, f * 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(rr), 0, o, f * 8 + 4, 4);
            }
            return o;
        }

        /// <summary>Pushes driver audio through WaveInRecorder's conversion in 50 ms buffers (+/- a few bytes, not frame aligned).</summary>
        private static WaveInRecorder Push(byte[] native, int rate, int channels, SampleKind kind, Action<byte[]> onChunk, List<byte> tap = null)
        {
            var rec = new WaveInRecorder(onChunk, _ => { });
            if (tap != null) rec.Tap = b => { lock (tap) tap.AddRange(b); };
            rec.StartSimulated(rate, channels, kind, "simulated driver");
            int step = rate * channels * PcmResampler.BytesPerSample(kind) / 20;
            int off = 0; bool odd = false;
            while (off < native.Length)
            {
                int n = Math.Min(step + (odd ? 5 : -5), native.Length - off);
                odd = !odd;
                var b = new byte[n];
                Buffer.BlockCopy(native, off, b, 0, n);
                rec.PushNative(b, n);
                off += n;
            }
            rec.FinishNative();
            return rec;
        }

        private static List<string> Words(string s) => Regex.Matches((s ?? "").ToLowerInvariant(), "[a-z]+").Cast<Match>().Select(m => m.Value).ToList();

        private static double Score(string expected, string got)
        {
            var want = Words(expected);
            var have = Words(got).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            int hit = 0;
            foreach (var w in want) if (have.TryGetValue(w, out var n) && n > 0) { hit++; have[w] = n - 1; }
            return want.Count == 0 ? 0 : (double)hit / want.Count;
        }

        private int ConversionTest(byte[] driver, double seconds)
        {
            var tap = new List<byte>();
            long packets = 0, bytes = 0;
            var rec = Push(driver, 48000, 2, SampleKind.Float32, b => { packets++; bytes += b.Length; }, tap);
            double outSeconds = bytes / 32000.0;
            var samples = WavFile.ToSamples(tap.ToArray());
            var check = MicCheck.Of(samples, 0);
            bool rightLength = Math.Abs(outSeconds - seconds) < 0.05;   // a missing resample would give 3x the length
            bool level = check.PeakDb > -30 && check.PeakDb < -6 && !check.Clipped;
            Log("conversion: in_s=" + seconds.ToString("0.00") + " out_s=" + outSeconds.ToString("0.00") + " packets=" + packets + " peak_db=" + check.PeakDb.ToString("0") +
                " speech_s=" + check.SpeechSeconds.ToString("0.0") + " format=" + rec.FormatForLog);
            _notes.Add("Microphone conversion (48 kHz stereo 32-bit float driver audio in odd-sized 50 ms buffers → 16 kHz mono 16-bit): " +
                       seconds.ToString("0.00") + " s in, " + outSeconds.ToString("0.00") + " s out (right speed: " + rightLength + "), loudest " + check.PeakDb.ToString("0") + " dBFS (scaled, not clipped: " + level + ")");
            return rightLength && level && packets > 10 ? 0 : 111;
        }

        private async Task<int> BuiltInTest(byte[] driver, byte[] driverShort)
        {
            BuiltInSpeech.Refresh();
            if (!BuiltInSpeech.Available)
            {
                _notes.Add("Built-in Windows speech: this runner has no recogniser, so it could not be tested here");
                return 0;
            }
            async Task<(double score, string summary)> One(byte[] audio, string expected)
            {
                var e = new BuiltInEngine(new List<string> { "Rapcha", "Prince" }, useDefaultMicrophone: false);
                var router = DictationRouter.Offline(e.Feed);
                Push(audio, 48000, 2, SampleKind.Float32, router.Feed);
                var raw = await e.FinishAsync();
                var summary = e.Summary;
                e.Dispose();
                return (Score(expected, RulePolish.Clean(raw)), summary);
            }
            var sw = Stopwatch.StartNew();
            var (score, sum) = await One(driver, Passage);
            var (scoreShort, _) = await One(driverShort, Short);
            Log("built-in through the microphone path: " + sum + " score=" + score.ToString("0.00") + " short_score=" + scoreShort.ToString("0.00") + " ms=" + sw.ElapsedMilliseconds);
            _notes.Add("Built-in Windows speech (" + BuiltInSpeech.RecognizerName + ") fed through the real microphone conversion (48 kHz stereo float, quiet): words recognised " +
                       (score * 100).ToString("0") + "% of the passage, " + (scoreShort * 100).ToString("0") + "% of \"" + Short + "\"");
            // Default-microphone mode can't be tested here (no microphone on the runner); it falls back to Rapcha's audio.
            var probe = new BuiltInEngine(new List<string>(), useDefaultMicrophone: true);
            _notes.Add("Built-in Windows speech listening to the Windows default microphone itself: " + (probe.OwnMicrophone ? "opened" : "no microphone on the runner, so it fell back to Rapcha's audio (no crash)"));
            probe.Dispose();
            return score >= 0.5 ? 0 : 112;
        }

        private async Task<int> WhisperTest(byte[] driver, byte[] driverShort, byte[] speech48)
        {
            var st = WhisperNative.EnsureLoaded();
            if (st != WhisperNative.State.Ready) { _notes.Add("Whisper engine: " + st + " (" + WhisperNative.LastError + ")"); return 113; }
            ModelStore.FolderOverride = _models;
            if (!ModelStore.IsInstalled(WhisperModels.Tiny))
            {
                var outcome = await ModelStore.DownloadAsync(WhisperModels.Tiny, (a, b) => { }, CancellationToken.None);
                Log("tiny download: " + outcome);
                if (outcome != ModelStore.Outcome.Done) { _notes.Add("Whisper Tiny download failed: " + outcome); return 113; }
            }
            var vocab = new List<string> { "Rapcha", "Prince" };
            async Task<(double score, string raw, WhisperEngine e)> One(byte[] audio, string expected, bool convert = true)
            {
                var e = new WhisperEngine(WhisperModels.Tiny, vocab, Environment.ProcessorCount);
                await Task.Run(() => e.WaitLoaded(60000));
                var router = DictationRouter.Offline(e.Feed);
                if (convert) Push(audio, 48000, 2, SampleKind.Float32, router.Feed);
                else
                {
                    // the bug v18 looked for: 48 kHz audio handed over as if it were 16 kHz (3x slow, wrong pitch)
                    for (int i = 0; i + 3200 <= audio.Length; i += 3200) { var b = new byte[3200]; Buffer.BlockCopy(audio, i, b, 0, 3200); router.Feed(b); }
                }
                var raw = await e.FinishAsync();
                e.Dispose();
                return (Score(expected, RulePolish.Clean(raw)), raw, e);
            }
            var sw = Stopwatch.StartNew();
            var (score, _, eng) = await One(driver, Passage);
            long ms = sw.ElapsedMilliseconds;
            var (scoreShort, _, _) = await One(driverShort, Short);
            // The wrong-rate bug, reproduced on purpose, to show what it would sound like to Whisper.
            var mono48 = new byte[speech48.Length / 2];
            for (int f = 0; f < mono48.Length / 2; f++) { mono48[2 * f] = speech48[4 * f]; mono48[2 * f + 1] = speech48[4 * f + 1]; }
            var (scoreWrong, _, _) = await One(mono48, Passage, convert: false);
            Log("whisper through the microphone path: " + eng.Summary + " ms=" + ms + " score=" + score.ToString("0.00") + " short_score=" + scoreShort.ToString("0.00") + " wrong_rate_score=" + scoreWrong.ToString("0.00"));
            _notes.Add("Whisper Tiny fed through the real microphone conversion (48 kHz stereo float, quiet): words recognised " + (score * 100).ToString("0") + "% of the passage in " + ms + " ms, " +
                       (scoreShort * 100).ToString("0") + "% of \"" + Short + "\"; for comparison 48 kHz audio fed as 16 kHz (the bug we looked for) scores " + (scoreWrong * 100).ToString("0") + "%");

            // Silence and steady noise: nothing reaches Whisper, nothing is invented.
            var quiet = new WhisperEngine(WhisperModels.Tiny, vocab, Environment.ProcessorCount);
            await Task.Run(() => quiet.WaitLoaded(60000));
            var rnd = new Random(9);
            var noise = new byte[48000 * 8 * 4];   // 4 s of hiss at 48 kHz stereo float
            for (int i = 0; i < noise.Length; i += 4) Buffer.BlockCopy(BitConverter.GetBytes((float)((rnd.NextDouble() - 0.5) * 0.01)), 0, noise, i, 4);
            var qr = DictationRouter.Offline(quiet.Feed);
            Push(noise, 48000, 2, SampleKind.Float32, qr.Feed);
            Push(new byte[48000 * 8 * 2], 48000, 2, SampleKind.Float32, qr.Feed);
            var none = await quiet.FinishAsync();
            quiet.Dispose();
            bool gated = (none ?? "").Trim().Length == 0;
            Log("whisper silence/noise: chars=" + (none ?? "").Length + " " + quiet.Summary);
            _notes.Add("Whisper with 6 s of hiss and silence: no words invented: " + gated + " (" + quiet.Summary.Split(' ').FirstOrDefault(x => x.StartsWith("skipped")) + ")");
            if (!gated) return 114;

            // Memory guard with this runner's real numbers, as if on a 4 GB laptop with 250 MB free.
            var g = new MemoryGuard();
            var check = new WhisperEngine(WhisperModels.Tiny, vocab, Environment.ProcessorCount);
            await Task.Run(() => check.WaitLoaded(60000));
            var p = Process.GetCurrentProcess(); p.Refresh();
            long priv = p.PrivateMemorySize64 / (1024 * 1024);
            bool kept = true;
            for (int t = 0; t < 10; t++) kept &= !g.ShouldStop(t, SpeechEngine.Whisper, priv, 250, check.HeldAudioBytes, out _);
            check.Dispose();
            bool oldWouldStop = 250 < SessionCaps.MinFreeMbWhisper;
            Log("memory guard: private_mb=" + priv + " free_mb=250 kept=" + kept + " old_rule_would_stop=" + oldWouldStop);
            _notes.Add("Memory guard, faked 4 GB laptop with 250 MB free and Whisper Tiny loaded (Rapcha " + priv + " MB): keeps dictating: " + kept +
                       " (the v16-v17 rule stopped below " + SessionCaps.MinFreeMbWhisper + " MB free: " + oldWouldStop + ")");
            if (!kept) return 117;
            return score >= 0.6 && scoreShort >= 0.5 ? 0 : 113;
        }

        // ---------------- no auto-restart ----------------

        private async Task<int> GateTest()
        {
            int starts = _app.RecordingStarts, ignored = _app.StartsIgnored;
            // Rapcha stopped a dictation by itself; the user's late "stop" tap and a shortcut press arrive.
            _app.Gate.Stopped(true, DateTime.UtcNow);
            _app.Gate.BackToIdle(DateTime.UtcNow);
            _app.SimulateRingTap();   // (a simulated tap has no tap time, so the select-to-speak guard may take it first; either way no start)
            _app.SimulateShortcut(ShortcutAction.TapToTalk, true);
            await Task.Delay(300);
            bool noRestart = _app.RecordingStarts == starts && _app.StartsIgnored >= ignored + 1;
            // A fresh press once the quiet time is over does start (here: no key on the runner, so Settings opens).
            await Task.Delay(StartGate.AfterAutoStopMs + 200);
            _app.SimulateShortcut(ShortcutAction.TapToTalk, true);
            await Task.Delay(500);
            bool freshWorks = _app.RecordingStarts == starts + 1;
            _app.CancelForTest();
            Log("gate: starts_before=" + starts + " after=" + _app.RecordingStarts + " ignored=" + (_app.StartsIgnored - ignored) + " fresh_works=" + freshWorks);
            _notes.Add("After Rapcha stops by itself, a tap on the ring and a shortcut press in the next 3 s do not open a new dictation: " + noRestart + "; a press after that starts as usual: " + freshWorks);
            noRestart &= freshWorks;
            return noRestart ? 0 : 115;
        }

        // ---------------- Settings → Test microphone ----------------

        private async Task<int> MicTestUi(byte[] driverShort)
        {
            MicTestCard.TestMode = true;
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var card = w.Settings.MicTest;
            async Task Shot(string name)
            {
                for (Control c = card.Card; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(card.Card); break; }
                await Task.Delay(300);
                w.SavePng(Path.Combine(_out, name));
            }
            bool idle = card.TestButton.Visible && card.TestButton.Enabled && !card.PlayButton.Enabled && !card.SaveButton.Enabled && card.DeviceLabel.Text.StartsWith("Microphone:");
            await Shot("v18-test-microphone-idle.png");
            // The real button on the runner: there is no microphone, so it must explain that and not crash.
            card.TestButton.PerformClick();
            await Task.Delay(600);
            string noMic = card.ResultLabel.Text;
            bool explained = !card.Busy || noMic.StartsWith("Recording");
            if (card.Busy) await Task.Delay(MicTestCard.RecordMs + 1500);
            await Shot("v18-test-microphone-runner.png");
            // Then the same conversion path with a driver-style 48 kHz stereo float "My name is Prince".
            card.RunSimulated(driverShort, 48000, 2, SampleKind.Float32, "Microphone Array (simulated 48 kHz stereo float driver)");
            await Task.Delay(200);
            var c2 = card.LastCheck;
            bool result = card.PlayButton.Enabled && card.SaveButton.Enabled && c2.SpeechSeconds > 0.5 && card.FormatLabel.Text.Contains("48000 Hz") && card.FormatLabel.Text.Contains("16000 Hz");
            card.PlayForTest();
            bool playback = Math.Abs(card.LastPlaybackMs - c2.Seconds * 1000) < 50;
            await Shot("v18-test-microphone.png");
            bool savedBefore = File.Exists(MicTestCard.SavePath);
            card.SaveButton.PerformClick();
            bool saved = File.Exists(MicTestCard.SavePath) && new FileInfo(MicTestCard.SavePath).Length == 44 + card.LastAudio.Length;
            try { File.Delete(MicTestCard.SavePath); } catch { }
            w.Close();
            Log("mic test ui: idle=" + idle + " no_mic_explained=" + explained + " result=" + result + " playback_ms=" + (int)card.LastPlaybackMs + " saved_only_on_press=" + (!savedBefore && saved));
            _notes.Add("Settings → Test microphone: shows the microphone, Windows' format and what Rapcha opened, a level meter, what it heard (" + c2.Verdict + "), plays back the 16 kHz mono audio (" +
                       (int)card.LastPlaybackMs + " ms) and saves a WAV only when Save is pressed: " + (idle && result && playback && !savedBefore && saved) + "; on the runner without a microphone it says so: " + explained);
            return idle && result && playback && !savedBefore && saved ? 0 : 116;
        }
    }
}
