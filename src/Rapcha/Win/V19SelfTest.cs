using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    /// CI self-test for v19 (--selftest-v19 outDir), run inside the real app: the hold-to-snooze delay is 3 s with
    /// hand jitter; Whisper's per-piece numbers (no-speech probability, log-prob, tokens) reach the Diagnostics
    /// timeline; words appear only with "Include transcribed text"; showing Settings no longer opens the microphone and
    /// nothing holds it at the end; the Diagnostics tab (screenshots), a saved .txt sample and a WAV, and the rolling file.
    /// Exit: 0 ok, 121 snooze, 122 Whisper numbers, 123 words opt-in, 124 microphone left open, 125 page / file, 1 crash.
    /// </summary>
    internal sealed class V19SelfTest
    {
        private const string Passage = "The boy is coming. He is coming home from school today. We will meet at the market on Saturday morning.";
        private readonly RapchaApp _app;
        private readonly string _out;
        private readonly List<string> _notes = new List<string>();
        private readonly string _models;

        public V19SelfTest(RapchaApp app, string outDir)
        {
            _app = app;
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _models = Path.Combine(Path.GetTempPath(), "rapcha-v19-models");
        }

        private static void Log(string s) => AppStorage.Log("Selftest v19: " + s);

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
            ModelStore.FolderOverride = null;
            MicTestCard.TestMode = false;
            DiagLog.IncludeText = false;
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            try { File.WriteAllLines(Path.Combine(_out, "v19-results.txt"), _notes); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            _app.ExitForTest();
        }

        private async Task<int> Steps()
        {
            DiagnosticsInfo.Start("v19 self-test");
            int r;
            if ((r = await SnoozeTest()) != 0) return r;
            if ((r = await WhisperDiag()) != 0) return r;
            if ((r = await MicAndPage()) != 0) return r;
            return 0;
        }

        // ---------------- snooze: 3 s with hand jitter ----------------

        private async Task<int> SnoozeTest()
        {
            var ring = _app.Ring;
            var wa = Screen.PrimaryScreen.WorkingArea;
            var c = new Point(wa.X + wa.Width * 2 / 5, wa.Y + wa.Height * 2 / 5);
            ring.SimulatePress();
            // drag in from the side, then stop and hold with up to 4 px of hand shake (8 px from side to side) every 100 ms
            for (int i = 6; i >= 0; i--) { ring.SimulateMoveTo(new Point(c.X - i * 40, c.Y)); await Task.Delay(60); }
            var sw = Stopwatch.StartNew();
            var rnd = new Random(3);
            while (!ring.SnoozeTarget.Showing && sw.ElapsedMilliseconds < 8000)
            {
                await Task.Delay(100);
                ring.SimulateMoveTo(new Point(c.X + rnd.Next(-4, 5), c.Y + rnd.Next(-4, 5)));
            }
            long ms = sw.ElapsedMilliseconds;
            bool shown = ring.SnoozeTarget.Showing;
            int tol = ring.HoldTolerancePx;
            ring.SimulateMoveTo(new Point(wa.Right - 30, wa.Y + wa.Height / 3));
            await Task.Delay(150);
            ring.SimulateRelease();
            await Task.Delay(300);
            bool notSnoozed = !Snooze.Active && ring.Visible;
            // The v17/v18 rule (6 px from the anchor, 5.5 s) with the same shake: how often the wait restarted.
            var now = new SnoozeHold(10);
            var r2 = new Random(3);
            int oldRestarts = 0, ax = 0, ay = 0; double since = 0, oldArmed = -1, newArmed = -1;
            for (double t = 0; t < 30000 && (oldArmed < 0 || newArmed < 0); t += 100)
            {
                int x = 500 + r2.Next(-4, 5), y = 400 + r2.Next(-4, 5);
                if (t == 0) { ax = x; ay = y; }
                else if (Math.Abs(x - ax) > 6 || Math.Abs(y - ay) > 6) { oldRestarts++; ax = x; ay = y; since = t; }
                if (oldArmed < 0 && t - since >= 5500) oldArmed = t;
                if (newArmed < 0 && now.Update(true, x, y, t)) newArmed = t;
            }
            bool ok = shown && ms >= SnoozeHold.HoldMs - 100 && ms <= SnoozeHold.HoldMs + 700 && notSnoozed;
            Log("snooze: shown=" + shown + " ms=" + ms + " tolerance_px=" + tol + " not_snoozed=" + notSnoozed + " old_rule_restarts=" + oldRestarts + " old_armed_ms=" + oldArmed + " new_armed_ms=" + newArmed);
            _notes.Add("Hold to snooze: with up to 4 px of hand shake (8 px from side to side) the Snooze circle appeared " + ms + " ms after the ring stopped (wanted 3000 ms; tolerance here " + tol + " px): " + ok +
                       ". The old rule (6 px, 5.5 s) with the same shake restarted the wait " + oldRestarts + " times and took " + (oldArmed < 0 ? "more than 30 s" : oldArmed + " ms") +
                       "; the new rule took " + newArmed + " ms");
            return ok ? 0 : 121;
        }

        // ---------------- Whisper numbers on the timeline ----------------

        private async Task<int> WhisperDiag()
        {
            var st = WhisperNative.EnsureLoaded();
            if (st != WhisperNative.State.Ready) { _notes.Add("Whisper engine: " + st); return 122; }
            ModelStore.FolderOverride = _models;
            if (!ModelStore.IsInstalled(WhisperModels.Tiny))
            {
                var outcome = await ModelStore.DownloadAsync(WhisperModels.Tiny, (a, b) => { }, CancellationToken.None);
                if (outcome != ModelStore.Outcome.Done) { _notes.Add("Whisper Tiny download failed: " + outcome); return 122; }
            }
            var speech = Synthesize(Passage, 48000);
            var driver = ToDriverFloat(speech, 0.15, 5);
            int before = DiagLog.Count;

            async Task<string> One(bool words)
            {
                DiagLog.IncludeText = words;
                RapchaApp.LastSessionAudio.Clear();
                var e = new WhisperEngine(WhisperModels.Tiny, new List<string> { "Rapcha" }, Environment.ProcessorCount);
                await Task.Run(() => e.WaitLoaded(60000));
                var router = DictationRouter.Offline(e.Feed);
                var rec = new WaveInRecorder(b => { RapchaApp.LastSessionAudio.Add(b); router.Feed(b); }, _ => { }, "self-test simulated driver", true);
                rec.StartSimulated(48000, 2, SampleKind.Float32, "simulated 48 kHz stereo float driver");
                int step = 48000 * 8 / 20;
                for (int off = 0; off < driver.Length; off += step)
                {
                    int n = Math.Min(step, driver.Length - off);
                    var b = new byte[n]; Buffer.BlockCopy(driver, off, b, 0, n);
                    rec.PushNative(b, n);
                }
                rec.FinishNative();
                var raw = await e.FinishAsync();
                e.Dispose();
                DiagLog.IncludeText = false;
                return raw ?? "";
            }

            var raw1 = await One(false);
            var lines1 = DiagLog.Snapshot().Skip(before).ToList();
            bool pieces = lines1.Any(l => l.Contains("whisper piece") && l.Contains("no_speech=") && l.Contains("avg_logprob=") && l.Contains("compression="));
            bool seconds = lines1.Any(l => l.Contains("[audio] self-test simulated driver second 1:") && l.Contains("rms="));
            bool noWords = !lines1.Any(l => l.Contains(" [text] "));
            Log("whisper diag: pieces=" + pieces + " audio_seconds=" + seconds + " no_words=" + noWords + " chars=" + raw1.Length);
            _notes.Add("Diagnostics gets one line per second of audio (level, clipping, speech, missing audio, resampler counts): " + seconds +
                       "; one line per Whisper piece with no-speech probability, average log-prob, tokens, compression ratio and filter results: " + pieces + "; no words by default: " + noWords);
            if (!pieces || !seconds) return 122;
            if (!noWords) return 123;

            int mid = DiagLog.Count;
            await One(true);
            var lines2 = DiagLog.Snapshot().Skip(mid).ToList();
            bool words = lines2.Any(l => l.Contains(" [text] whisper piece"));
            bool exportHides = !DiagLog.ForExport(false).Any(l => l.Contains(" [text] "));
            bool fileHasNoWords = DiagnosticsInfo.File != null && DiagnosticsInfo.File.Existing().All(p => !File.ReadAllText(p).Contains(" [text] "));
            Log("words opt-in: shown=" + words + " export_hides_when_off=" + exportHides + " rolling_file_has_no_words=" + fileHasNoWords);
            _notes.Add("With \"Include transcribed text\" ticked, Whisper's raw words per piece are added: " + words + "; unticked, the saved report leaves them out: " + exportHides +
                       "; the always-on file in %AppData%\\Rapcha\\diagnostics never has words: " + fileHasNoWords);
            return words && exportHides && fileHasNoWords ? 0 : 123;
        }

        // ---------------- microphone lifecycle and the Diagnostics tab ----------------

        private async Task<int> MicAndPage()
        {
            MicTestCard.TestMode = true;
            int opensBefore = MicTracker.Opens;
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(1500);
            bool noAutoMeter = !w.Settings.MeterRunning;     // v18 opened the microphone here and kept it open
            var micCard = w.Settings.CheckLevelButton.Parent?.Parent;
            if (micCard != null)
                for (Control c = micCard; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(micCard); break; }
            await Task.Delay(300);
            w.SavePng(Path.Combine(_out, "v19-settings-check-level.png"));
            // On the runner there is no microphone: Check level must explain that and leave nothing open.
            w.Settings.SetWindowActive(true);
            w.Settings.CheckLevelButton.PerformClick();
            await Task.Delay(800);
            w.Settings.SetWindowActive(false);
            bool closedAfterDeactivate = !w.Settings.MeterRunning;
            w.Settings.MicTest.TestButton.PerformClick();
            await Task.Delay(600);
            if (w.Settings.MicTest.Busy) await Task.Delay(MicTestCard.RecordMs + 1500);

            w.ShowPage(MainWindow.Page.Diagnostics);
            await Task.Delay(1200);
            var d = w.Diagnostics;
            bool defaultOff = !d.IncludeText.Checked;
            w.SavePng(Path.Combine(_out, "v19-diagnostics.png"));

            string txt = Path.Combine(_out, "Rapcha-diagnostics-sample.txt");
            string wav = Path.Combine(_out, "Rapcha-diagnostics-sample.wav");
            d.PickPath = name => name.EndsWith(".wav") ? wav : txt;
            d.SaveTxt();
            d.SaveWav();
            await Task.Delay(700);
            w.SavePng(Path.Combine(_out, "v19-diagnostics-saved.png"));
            var body = File.Exists(txt) ? File.ReadAllText(txt) : "";
            bool saved = body.Contains("RAPCHA DIAGNOSTICS") && body.Contains("== About this PC ==") && body.Contains("Windows: ") && body.Contains("Recording devices") &&
                         body.Contains("Microphone open now: no") && body.Contains("whisper piece") && body.Contains("[mic]") == (MicTracker.Opens > 0) &&
                         !body.Contains(" [text] ") && !Regex.IsMatch(body, @"AIza[0-9A-Za-z_\-]{20,}");
            bool wavOk = File.Exists(wav) && new FileInfo(wav).Length == 44 + RapchaApp.LastSessionAudio.Bytes && RapchaApp.LastSessionAudio.Bytes > 0;
            bool defaultName = DiagnosticsView.DefaultName(new DateTime(2026, 11, 17, 9, 5, 0)) == "Rapcha-diagnostics-2026-11-17-0905.txt";
            w.Close();
            await Task.Delay(500);
            bool nothingOpen = MicTracker.Count == 0;
            var rolling = DiagnosticsInfo.File?.PathOf(0);
            bool rollingOk = rolling != null && File.Exists(rolling) && new FileInfo(rolling).Length > 0;
            Log("mic+page: no_auto_meter=" + noAutoMeter + " closed_after_deactivate=" + closedAfterDeactivate + " opens=" + (MicTracker.Opens - opensBefore) + " open_now=" + MicTracker.Count +
                " default_off=" + defaultOff + " saved=" + saved + " txt_kb=" + (body.Length / 1024) + " wav=" + wavOk + " rolling=" + rollingOk + " default_name=" + defaultName);
            _notes.Add("Opening Settings no longer opens the microphone (v18 kept it open the whole time Settings was open, even behind other windows, and reopened it after every dictation): " + noAutoMeter +
                       "; the level meter closes when the window loses focus: " + closedAfterDeactivate + "; nothing holds the microphone at the end: " + nothingOpen);
            _notes.Add("Diagnostics tab: \"Include transcribed text\" is off by default: " + defaultOff + "; Save as .txt wrote a report with the PC, the microphones, the microphone state and the timeline, no key and no words (" +
                       (body.Length / 1024) + " KB): " + saved + "; Save last session audio wrote a WAV: " + wavOk + "; the always-on file " + rolling + " exists: " + rollingOk);
            if (!noAutoMeter || !closedAfterDeactivate || !nothingOpen) return 124;
            return defaultOff && saved && wavOk && rollingOk && defaultName ? 0 : 125;
        }

        // ---------------- audio ----------------

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
                float l = (float)(s / 32768.0 * gain + (rnd.NextDouble() - 0.5) * 0.002);
                Buffer.BlockCopy(BitConverter.GetBytes(l), 0, o, f * 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(l * 0.8f), 0, o, f * 8 + 4, 4);
            }
            return o;
        }
    }
}
