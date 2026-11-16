using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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
    /// CI self-test for v16 (--selftest-v16 outDir): offline voice typing.
    /// Device level from faked values; the recording timer and the "Offline" tag in the real Words box (every dock
    /// edge, 100/150/200% and large text: never over the words or a button) with the countdown; Settings → Offline
    /// voice typing on a Low, Medium and Strong laptop (Download Whisper model greyed out on Low, a click does
    /// nothing), "Get Windows speech", the Offline fallback switch; a real download of Whisper Tiny from Hugging
    /// Face with progress and the SHA-256 check, cancel, and a bad checksum; the embedded whisper.cpp engine loading
    /// from inside the exe and transcribing synthesized speech in ~30 s pieces with the model freed afterwards;
    /// simulated S mode (Whisper refused → built-in speech + the message); built-in Windows speech when the runner
    /// has a recogniser; switching from Gemini to offline part-way, keeping the captured words; the rule-based polish.
    /// Only counts, sizes, timings and scores are logged, never words.
    /// Exit: 0 ok, 91 tier, 92 timer / Offline tag, 93 settings, 94 download, 95 Whisper engine, 96 S mode fallback,
    /// 97 built-in speech, 98 fallback switch, 1 crash.
    /// </summary>
    internal sealed class V16SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "offline-fallback.txt", "whisper-model.txt" };
        private readonly byte[][] _backup;
        private readonly List<string> _notes = new List<string>();
        private readonly string _models;

        private const string Passage =
            "Rapcha types what you say into any text box on your laptop. " +
            "When the internet goes away it keeps listening and writes your words offline. " +
            "The small timer in the corner shows how long you have been talking. " +
            "Near the end it counts down gently and then stops without losing anything. " +
            "Whisper turns each half minute of speech into text while you keep talking. " +
            "Your custom vocabulary helps it spell names the way you like. " +
            "When you are back online the words go to Gemini again for smart polish. " +
            "This sentence is here so the recording is longer than thirty seconds.";

        public V16SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _models = Path.Combine(Path.GetTempPath(), "rapcha-v16-models");
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v16: " + s);

        private async void Run()
        {
            int code;
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e.GetType().Name + " " + e.StackTrace); code = 1; }
            DeviceProbe.Override = null;
            BuiltInSpeech.Override = null;
            Connectivity.ForceOffline = null;
            WhisperNative.SimulateBlocked = false;
            ModelStore.FolderOverride = null;
            for (int i = 0; i < _files.Length; i++)
            {
                var p = Path.Combine(AppStorage.Folder, _files[i]);
                try { if (_backup[i] != null) File.WriteAllBytes(p, _backup[i]); else if (File.Exists(p)) File.Delete(p); } catch { }
            }
            OfflineSettings.Reload();
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            try { File.WriteAllLines(Path.Combine(_out, "v16-results.txt"), _notes); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            ExitThread();
        }

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms) { if (ok()) return true; await Task.Delay(50); }
            return ok();
        }

        private async Task<int> Steps()
        {
            ModelStore.FolderOverride = _models;
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            int r;
            if ((r = TierTest()) != 0) return r;
            if ((r = BubbleTest()) != 0) return r;
            if ((r = await SettingsTest()) != 0) return r;     // includes the downloads (94) and S mode note
            var pcm = Synthesize(Passage, out double seconds);
            Log("synthesized speech: seconds=" + seconds.ToString("0.0") + " bytes=" + pcm.Length);
            if ((r = await WhisperTest(pcm)) != 0) return r;
            if ((r = await SModeTest()) != 0) return r;
            if ((r = await BuiltInTest(pcm)) != 0) return r;
            if ((r = await SwitchTest(pcm)) != 0) return r;
            return 0;
        }

        // ---------------- device level ----------------

        private int TierTest()
        {
            var real = DeviceProbe.Measure();
            var realTier = DeviceTiers.Classify(real);
            Log("device: " + real + " tier=" + DeviceTiers.Name(realTier) + " s_mode=" + DeviceProbe.IsSMode());
            var cases = new[] { ("ram=4096,free=1500,cores=4,disk=20000", DeviceTier.Low), ("ram=6144", DeviceTier.Medium), ("ram=8192", DeviceTier.Medium), ("ram=16384", DeviceTier.Strong), ("ram=7987", DeviceTier.Medium) };
            bool ok = true;
            foreach (var (text, want) in cases)
            {
                DeviceProbe.Override = DeviceFacts.Parse(text);
                ok &= DeviceProbe.Tier == want;
            }
            DeviceProbe.Override = null;
            ok &= real.TotalRamMb > 0 && real.Cores > 0;
            _notes.Add("Device check on the runner: " + DeviceTiers.Describe(real) + ", level " + DeviceTiers.Name(realTier) + "; faked 4 / 6 / 8 / 16 GB laptops gave Low / Medium / Medium / Strong: " + ok);
            return ok ? 0 : 91;
        }

        // ---------------- timer and Offline tag ----------------

        private int BubbleTest()
        {
            bool ok = true;
            var words = "Hello this is a test of the words box with a few lines of text so the timer and the offline tag must stay out of the way of everything here";
            foreach (var dpi in new[] { 96, 144, 192 })
                foreach (var scale in new[] { 1.0, 1.5, 2.25 })
                    foreach (var mode in new[] { 0, 1, 2 })   // online timer, offline timer, offline countdown
                    {
                        var p = new BubblePainter();
                        p.Setup(dpi, scale);
                        p.SetMode(WordsBubble.Mode.Listening, words);
                        p.Field = true;
                        p.Offline = mode > 0;
                        var t = mode == 2 ? TimerText.For(108.4, 120) : TimerText.For(mode == 0 ? 65 : 42, mode == 0 ? SessionCaps.GeminiSeconds : 300);
                        p.Timer = t.Text; p.TimerCountdown = t.Countdown;
                        using (var bmp = p.Render())
                        {
                            bool clean = Clear(p.TimerRect, p) && Clear(p.OfflineRect, p) && (p.OfflineRect.IsEmpty || !p.OfflineRect.IntersectsWith(p.TimerRect));
                            bool shown = scale > 1.6 || (!p.TimerRect.IsEmpty && (mode == 0 || !p.OfflineRect.IsEmpty));   // at 225% text they may hide, never overlap
                            if (!clean || !shown) Log("bubble geometry problem dpi=" + dpi + " scale=" + scale + " mode=" + mode + " clean=" + clean + " shown=" + shown);
                            ok &= clean && shown;
                            if (dpi == 96 && scale == 1.0)
                                bmp.Save(Path.Combine(_out, mode == 0 ? "v16-timer-online.png" : mode == 1 ? "v16-timer-offline.png" : "v16-timer-countdown.png"), ImageFormat.Png);
                            if (dpi == 192 && scale == 1.0 && mode == 1) bmp.Save(Path.Combine(_out, "v16-timer-offline-200.png"), ImageFormat.Png);
                        }
                    }
            // The real Words box beside the ring on every edge: on screen, never over the ring, tag and timer drawn.
            var wa = Screen.PrimaryScreen.WorkingArea;
            int ring = 48;
            var rings = new Dictionary<DockEdge, Rectangle>
            {
                { DockEdge.Left, new Rectangle(wa.Left, wa.Top + wa.Height / 2, ring, ring) },
                { DockEdge.Right, new Rectangle(wa.Right - ring, wa.Top + wa.Height / 2, ring, ring) },
                { DockEdge.Top, new Rectangle(wa.Left + wa.Width / 2, wa.Top, ring, ring) },
                { DockEdge.Bottom, new Rectangle(wa.Left + wa.Width / 2, wa.Bottom - ring, ring, ring) },
            };
            bool edges = true;
            using (var b = new WordsBubble())
            {
                foreach (var kv in rings)
                {
                    b.Open(kv.Value, kv.Key, 96);
                    b.Offline = true;
                    b.SetTimer(TimerText.For(12, 300));
                    b.SetWords("Offline words appear here");
                    Application.DoEvents();
                    var a = b.Area;
                    bool e = wa.Contains(a) && !a.IntersectsWith(kv.Value) && !b.Painter.TimerRect.IsEmpty && !b.Painter.OfflineRect.IsEmpty && Clear(b.Painter.TimerRect, b.Painter) && Clear(b.Painter.OfflineRect, b.Painter);
                    if (!e) Log("bubble on edge " + kv.Key + " problem");
                    edges &= e;
                }
                b.SetTimer(TimerText.For(110, 120));
                bool countdown = b.Painter.TimerCountdown && b.Painter.Timer == "0:10 left";
                edges &= countdown;
                b.Close2();
            }
            _notes.Add("Recording timer and Offline tag: in the bottom row's gaps, never over the words, Cancel, the keyboard icon or Complete, at 100/150/200% and with large text: " + ok +
                       "; the real words box on all four ring edges stays on screen with the timer and tag shown, and counts down in the last 20 s: " + edges);
            return ok && edges ? 0 : 92;
        }

        private static bool Clear(Rectangle r, BubblePainter p)
        {
            if (r.IsEmpty) return true;
            var card = new Rectangle(0, 0, p.Layout.Width, p.Layout.Height);
            foreach (var other in new[] { p.CancelRect, p.CompleteRect, p.IconRect, p.TextRect })
                if (!other.IsEmpty && r.IntersectsWith(other)) return false;
            return card.Contains(r);
        }

        // ---------------- Settings ----------------

        private async Task<int> SettingsTest()
        {
            OfflineCard.DontOpenSettings = true;
            OfflineSettings.Reload();
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var card = w.Settings.Offline;
            async Task Shot(string name, Control focus)
            {
                for (Control c = focus; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(focus); break; }
                await Task.Delay(250);
                w.SavePng(Path.Combine(_out, name));
            }

            // Offline fallback: on by default, saved when changed.
            bool fallbackDefault = card.FallbackToggle.Checked && OfflineSettings.Fallback;
            card.FallbackToggle.Checked = false;
            OfflineSettings.Reload();
            bool savedOff = !OfflineSettings.Fallback && File.ReadAllText(Path.Combine(AppStorage.Folder, "offline-fallback.txt")).Trim() == "0";
            card.FallbackToggle.Checked = true;
            OfflineSettings.Reload();
            bool savedOn = OfflineSettings.Fallback;

            // Low: the button is there but greyed out with the note; clicking does nothing.
            DeviceProbe.Override = DeviceFacts.Parse("ram=4096,free=1500,cores=4,disk=20000");
            BuiltInSpeech.Override = true;
            await Task.Delay(200);
            card.DownloadButton.PerformClick();
            await Task.Delay(200);
            bool lowGrey = card.DownloadButton.Visible && !card.DownloadButton.Enabled && card.DownloadClicks == 0 && !card.Downloading &&
                           card.WhisperNote.Text.StartsWith("Your laptop's memory is too low for this.");
            await Shot("v16-settings-whisper-low.png", card.DownloadButton);

            // No Windows speech: "Get Windows speech" opens Speech settings.
            BuiltInSpeech.Override = false;
            card.Refresh();
            await Task.Delay(150);
            bool getVisible = card.GetSpeechButton.Visible && card.GetSpeechButton.Enabled;
            await Shot("v16-settings-get-windows-speech.png", card.GetSpeechButton);
            card.GetSpeechButton.PerformClick();
            bool opens = card.LastSpeechUri == "ms-settings:speech";
            BuiltInSpeech.Override = true;
            card.Refresh();
            bool hiddenWhenInstalled = !card.GetSpeechButton.Visible;

            // Medium: Tiny; download it for real (progress, SHA-256), then it's ready.
            DeviceProbe.Override = DeviceFacts.Parse("ram=8192,free=4000,cores=4,disk=50000");
            await Task.Delay(200);
            bool mediumReady = card.DownloadButton.Enabled && card.ModelPicker.Items.Count == 1 && card.ModelPicker.Text.StartsWith("Tiny");
            await Shot("v16-settings-whisper-medium.png", card.DownloadButton);
            ModelStore.Outcome? outcome = null;
            Action<ModelStore.Outcome> onDone = o => outcome = o;
            card.DownloadFinished += onDone;
            var sw = Stopwatch.StartNew();
            card.DownloadButton.PerformClick();
            bool progressShown = card.Downloading;
            await WaitFor(() => outcome.HasValue, 300000);
            progressShown &= card.ProgressEvents > 3;
            long dlMs = sw.ElapsedMilliseconds;
            bool tinyOk = outcome == ModelStore.Outcome.Done && ModelStore.IsInstalled(WhisperModels.Tiny);
            card.Refresh();
            bool deleteShown = card.DeleteButton.Visible && !card.DownloadButton.Enabled;
            await Shot("v16-settings-whisper-medium-downloaded.png", card.DownloadButton);
            Log("download tiny: outcome=" + outcome + " ms=" + dlMs + " progress_shown=" + progressShown);

            // Strong: Base recommended, Small offered; cancel a download part-way: nothing is kept.
            DeviceProbe.Override = DeviceFacts.Parse("ram=16384,free=9000,cores=8,disk=80000");
            OfflineSettings.ChosenModel = "";
            await Task.Delay(200);
            bool strongChoices = card.ModelPicker.Items.Count == 2 && card.ModelPicker.Text.StartsWith("Base") && card.PolishToggle.Visible && !card.PolishToggle.Enabled && !card.PolishToggle.Checked;
            await Shot("v16-settings-whisper-strong.png", card.DownloadButton);
            outcome = null;
            card.DownloadButton.PerformClick();
            await WaitFor(() => File.Exists(Path.Combine(_models, WhisperModels.Base.File + ".part")) && new FileInfo(Path.Combine(_models, WhisperModels.Base.File + ".part")).Length > 2_000_000, 60000);
            Application.DoEvents();
            w.SavePng(Path.Combine(_out, "v16-settings-downloading.png"));   // already scrolled there: no wait, Base must still be downloading
            card.CancelButton.PerformClick();
            await WaitFor(() => outcome.HasValue, 30000);
            bool cancelled = outcome == ModelStore.Outcome.Cancelled && !File.Exists(Path.Combine(_models, WhisperModels.Base.File + ".part")) && !ModelStore.IsInstalled(WhisperModels.Base);
            card.DownloadFinished -= onDone;

            // A file that doesn't match the published checksum is refused and deleted.
            var fake = new WhisperModel { Id = "fake", File = "ggml-fake.bin", Bytes = 1, Sha256 = new string('0', 64), Label = "fake", RamMb = 1 };
            var bad = await ModelStore.DownloadAsync(fake, null, CancellationToken.None, WhisperModels.BaseUrl + "README.md");
            bool badRefused = bad == ModelStore.Outcome.BadChecksum && !File.Exists(ModelStore.PathOf(fake)) && !File.Exists(ModelStore.PathOf(fake) + ".part");

            // S mode note (Whisper refused): shown on the Medium laptop that has Tiny.
            DeviceProbe.Override = DeviceFacts.Parse("ram=8192,free=4000,cores=4,disk=50000");
            WhisperNative.SimulateBlocked = true;
            card.Refresh();
            bool smodeNote = card.WhisperNote.Text == WhisperNative.BlockedMessage;
            await Shot("v16-settings-smode.png", card.WhisperNote);
            WhisperNative.SimulateBlocked = false;
            BuiltInSpeech.Override = null;
            DeviceProbe.Override = null;
            w.Close();
            w.Dispose();

            Log("settings: fallback_default_on=" + fallbackDefault + " saved_off=" + savedOff + " saved_on=" + savedOn + " low_grey=" + lowGrey + " get_speech=" + getVisible + " opens=" + opens +
                " hidden_when_installed=" + hiddenWhenInstalled + " medium=" + mediumReady + " strong=" + strongChoices + " smode_note=" + smodeNote);
            Log("downloads: tiny_ok=" + tinyOk + " delete_shown=" + deleteShown + " cancelled=" + cancelled + " bad_checksum_refused=" + badRefused);
            _notes.Add("Settings, Offline fallback: on by default and saved when changed: " + (fallbackDefault && savedOff && savedOn));
            _notes.Add("Settings on a Low laptop: Download Whisper model is shown but greyed out with the memory note, and a click does nothing: " + lowGrey);
            _notes.Add("No Windows speech: Get Windows speech is shown and opens ms-settings:speech; hidden when speech is installed: " + (getVisible && opens && hiddenWhenInstalled));
            _notes.Add("Medium laptop: Whisper Tiny offered: " + mediumReady + "; Strong laptop: Base recommended, Small offered, offline polish placeholder off: " + strongChoices);
            _notes.Add("Download of Whisper Tiny from Hugging Face with progress and the SHA-256 check: " + tinyOk + " (" + (dlMs / 1000) + " s); Delete shown after: " + deleteShown +
                       "; cancel part-way keeps nothing: " + cancelled + "; a file with the wrong checksum is refused and deleted: " + badRefused);
            _notes.Add("S mode message in Settings when Whisper is refused: " + smodeNote);
            if (!(fallbackDefault && savedOff && savedOn && lowGrey && getVisible && opens && hiddenWhenInstalled && mediumReady && strongChoices && smodeNote)) return 93;
            if (!(tinyOk && deleteShown && cancelled && badRefused && progressShown)) return 94;
            return 0;
        }

        // ---------------- speech ----------------

        private static byte[] Synthesize(string text, out double seconds)
        {
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.Rate = 0;
                synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                var pb = new PromptBuilder();
                foreach (var sentence in Regex.Split(text, @"(?<=\.)\s+"))
                {
                    pb.AppendText(sentence);
                    pb.AppendBreak(TimeSpan.FromMilliseconds(700));
                }
                synth.Speak(pb);
                synth.SetOutputToNull();
                var b = ms.ToArray();
                seconds = b.Length / 32000.0;
                return b;
            }
        }

        private static List<string> Words(string s) => Regex.Matches((s ?? "").ToLowerInvariant(), "[a-z]+").Cast<Match>().Select(m => m.Value).ToList();

        /// <summary>Share of the expected words that were recognised (each counted once).</summary>
        private static double Score(string expected, string got)
        {
            var want = Words(expected);
            var have = Words(got).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            int hit = 0;
            foreach (var w in want) if (have.TryGetValue(w, out var n) && n > 0) { hit++; have[w] = n - 1; }
            return want.Count == 0 ? 0 : (double)hit / want.Count;
        }

        private static void Feed(IOfflineEngine e, byte[] pcm, int from, int to)
        {
            for (int i = from; i < to; i += 3200)
            {
                var n = Math.Min(3200, to - i);
                var b = new byte[n];
                Buffer.BlockCopy(pcm, i, b, 0, n);
                e.Feed(b);
            }
        }

        private static long Private() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / (1024 * 1024); }

        private async Task<int> WhisperTest(byte[] pcm)
        {
            var bundled = WhisperNative.IsBundled;
            var st = WhisperNative.EnsureLoaded();
            Log("whisper engine: bundled=" + bundled + " state=" + st + " variant=" + WhisperNative.Variant + " error=" + WhisperNative.LastError);
            if (st != WhisperNative.State.Ready)
            {
                _notes.Add("Whisper engine inside the exe: bundled=" + bundled + ", state " + st + " (error " + WhisperNative.LastError + ")");
                return 95;
            }
            Log("whisper system info: " + WhisperNative.SystemInfo());
            long before = Private();
            var engine = new WhisperEngine(WhisperModels.Tiny, new List<string> { "Rapcha", "Gemini" }, Environment.ProcessorCount);
            bool loaded = await Task.Run(() => engine.WaitLoaded(60000));
            long loadedMb = Private();
            var sw = Stopwatch.StartNew();
            Feed(engine, pcm, 0, pcm.Length);
            var raw = await engine.FinishAsync();
            long ms = sw.ElapsedMilliseconds;
            await Task.Delay(300);
            long after = Private();
            engine.Dispose();
            var clean = RulePolish.Clean(raw, new List<string> { "Rapcha", "Gemini" });
            double score = Score(Passage, clean);
            bool flat = engine.PeakAudioSamples <= 31 * 16000;
            bool freed = after - before < Math.Max(60, (loadedMb - before) / 2);
            bool polished = clean.Length > 0 && char.IsUpper(clean[0]) && clean.EndsWith(".");
            Log("whisper: loaded=" + loaded + " load_ms=" + engine.LoadMs + " pieces=" + engine.Pieces + " transcribe_total_ms=" + ms + " score=" + score.ToString("0.00") +
                " mem_before_mb=" + before + " mem_loaded_mb=" + loadedMb + " mem_after_mb=" + after + " peak_audio_s=" + (engine.PeakAudioSamples / 16000) + " chars=" + clean.Length);
            _notes.Add("Whisper engine inside the exe (" + WhisperNative.Variant + " build) loaded with LoadLibrary from %AppData%\\Rapcha\\engine: yes; Tiny model load " + engine.LoadMs + " ms; " +
                       engine.Pieces + " pieces of up to 30 s; " + (pcm.Length / 32000) + " s of synthesized speech turned into text in " + ms + " ms; words recognised " + (score * 100).ToString("0") + "%");
            _notes.Add("Memory: " + before + " MB before, " + loadedMb + " MB with the model loaded, " + after + " MB after it was freed; audio held at most " + (engine.PeakAudioSamples / 16000) +
                       " s (flat: " + flat + "); rule-based polish (capital, full stop): " + polished);
            return loaded && score >= 0.6 && engine.Pieces >= 2 && flat && freed && polished ? 0 : 95;
        }

        private async Task<int> SModeTest()
        {
            WhisperNative.SimulateBlocked = true;
            DeviceProbe.Override = DeviceFacts.Parse("ram=8192,free=4000,cores=4,disk=50000");
            var inputs = OfflineEngines.Inputs(false);
            var choice = EnginePolicy.ChooseOffline(inputs);
            // Even if Rapcha tried Whisper, a refused load falls back to built-in speech with the message.
            var engine = OfflineEngines.Start(new EngineChoice { Ok = true, Engine = SpeechEngine.Whisper }, new List<string>(), out var note);
            bool fellBack = note == WhisperNative.BlockedMessage && choice.Engine != SpeechEngine.Whisper &&
                            (BuiltInSpeech.Available ? engine is BuiltInEngine : engine == null);
            engine?.Dispose();
            WhisperNative.SimulateBlocked = false;
            DeviceProbe.Override = null;
            await Task.Delay(50);
            Log("s mode simulated: choice=" + choice + " engine=" + (engine?.Kind.ToString() ?? "none") + " message_ok=" + (note == WhisperNative.BlockedMessage));
            _notes.Add("Simulated S mode (Windows refuses the DLL, error 577): no crash, Rapcha says \"" + WhisperNative.BlockedMessage + "\" and uses " +
                       (BuiltInSpeech.Available ? "built-in Windows speech" : "nothing (the runner has no Windows speech)") + ": " + fellBack);
            return fellBack ? 0 : 96;
        }

        private async Task<int> BuiltInTest(byte[] pcm)
        {
            BuiltInSpeech.Refresh();
            if (!BuiltInSpeech.Available)
            {
                Log("built-in speech: no recogniser on this runner");
                _notes.Add("Built-in Windows speech: this CI runner (Windows Server) has no speech recogniser installed, so it could not be tested here; the Get Windows speech path was tested instead");
                return 0;
            }
            var sw = Stopwatch.StartNew();
            BuiltInEngine e;
            try { e = new BuiltInEngine(new List<string> { "Rapcha", "Gemini" }); }
            catch (Exception ex) { Log("built-in speech: start failed " + ex.GetType().Name); _notes.Add("Built-in Windows speech: listed but could not start (" + ex.GetType().Name + ")"); return 97; }
            Feed(e, pcm, 0, pcm.Length);
            var raw = await e.FinishAsync();
            e.Dispose();
            double score = Score(Passage, RulePolish.Clean(raw));
            Log("built-in speech: " + e.Summary + " ms=" + sw.ElapsedMilliseconds + " score=" + score.ToString("0.00"));
            _notes.Add("Built-in Windows speech (" + BuiltInSpeech.RecognizerName + "), offline: words recognised " + (score * 100).ToString("0") + "% of synthesized speech in " + sw.ElapsedMilliseconds + " ms");
            return score >= 0.3 ? 0 : 97;
        }

        /// <summary>Gemini loses the internet part-way: the words it showed are kept and the rest is typed offline.</summary>
        private async Task<int> SwitchTest(byte[] pcm)
        {
            DeviceProbe.Override = DeviceFacts.Parse("ram=8192,free=4000,cores=4,disk=50000");
            long onlineBytes = 0;
            var router = new DictationRouter(b => onlineBytes += b.Length);
            int half = pcm.Length / 2 / 3200 * 3200;
            int part1 = half / 2 / 3200 * 3200;
            for (int i = 0; i < part1; i += 3200) { var b = new byte[3200]; Buffer.BlockCopy(pcm, i, b, 0, 3200); router.Feed(b); }
            router.OnOnlineWords("Words Gemini already showed");
            for (int i = part1; i < half; i += 3200) { var b = new byte[3200]; Buffer.BlockCopy(pcm, i, b, 0, 3200); router.Feed(b); }
            int replay = router.ReplayBytes;
            var choice = EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false));
            var engine = OfflineEngines.Start(choice, new List<string>(), out _);
            if (engine == null)
            {
                DeviceProbe.Override = null;
                _notes.Add("Switch to offline part-way: no offline engine on this runner (" + choice + ")");
                return 98;
            }
            router.SwitchToOffline(engine.Feed);
            for (int i = half; i < pcm.Length; i += 3200) { int n = Math.Min(3200, pcm.Length - i); var b = new byte[n]; Buffer.BlockCopy(pcm, i, b, 0, n); router.Feed(b); }
            var raw = await engine.FinishAsync();
            engine.Dispose();
            DeviceProbe.Override = null;
            var text = EnginePolicy.Join(router.Captured, RulePolish.Clean(raw));
            // the offline part covers the replayed audio plus the second half
            double secondHalf = Score(string.Join(" ", Regex.Split(Passage, @"(?<=\.)\s+").Skip(4)), raw);
            bool kept = text.StartsWith("Words Gemini already showed. ");
            bool replayed = router.OfflineBytes == replay + (pcm.Length - half) && replay == half - part1;
            Log("switch: engine=" + engine.Kind + " online_bytes=" + onlineBytes + " replay_bytes=" + replay + " offline_bytes=" + router.OfflineBytes + " kept=" + kept + " score_rest=" + secondHalf.ToString("0.00"));
            _notes.Add("Gemini lost the internet part-way: the words already shown were kept, the " + (replay / 32000) + " s of audio after them was replayed into " + engine.Kind +
                       " and the rest was typed offline (" + (secondHalf * 100).ToString("0") + "% of the later words): " + (kept && replayed));
            return kept && replayed && secondHalf >= 0.5 ? 0 : 98;
        }
    }
}
