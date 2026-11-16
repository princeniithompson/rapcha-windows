using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v12 (--selftest-v12 outDir): every Windows voice is listed (OneCore + SAPI) with name,
    /// gender and language; reading with a chosen voice (each engine) moves through the words to the end; a
    /// missing voice falls back; the Settings voice picker saves the choice and Preview uses it; typing in a text
    /// field wakes a resting ring with a smooth, monotonic, edge-anchored animation and keeps it awake; typing
    /// with no text field does not. Exit: 0 ok, 51 no voices, 52 reading with a voice, 53 fallback,
    /// 54 Settings voice picker, 55 typing did not wake, 56 wake animation not smooth, 57 typing did not keep it
    /// awake, 58 typing without a text field woke it, 1 crash.
    /// </summary>
    internal sealed class V12SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "voice.txt", "voice-speed.txt" };
        private readonly byte[][] _backup;
        private Process _forms, _notepad;

        public V12SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v12: " + s);

        private async void Run()
        {
            int code;
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e); code = 1; }
            for (int i = 0; i < _files.Length; i++)
            {
                var p = Path.Combine(AppStorage.Folder, _files[i]);
                try { if (_backup[i] != null) File.WriteAllBytes(p, _backup[i]); else if (File.Exists(p)) File.Delete(p); } catch { }
            }
            try { _forms?.Kill(); } catch { }
            try { if (_notepad != null && !_notepad.HasExited) _notepad.Kill(); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            ExitThread();
        }

        private const string Sample = "The quick brown fox jumps over the lazy dog. Rapcha reads this line with the voice you picked.";

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms) { if (ok()) return true; await Task.Delay(20); }
            return ok();
        }

        private async Task<int> Steps()
        {
            // 1. The voice list.
            var all = Voices.All();
            foreach (var v in all) Log("voice " + (v.Desktop ? "sapi" : "onecore") + " label=\"" + v.Label + "\" lang=" + v.Language);
            int onecore = all.Count(v => !v.Desktop), sapi = all.Count(v => v.Desktop);
            Log("voices total=" + all.Count + " onecore=" + onecore + " sapi=" + sapi);
            if (all.Count == 0 || sapi == 0) return 51;
            bool labelsOk = all.All(v => v.Label.Length > 0 && !v.Label.StartsWith("Microsoft", StringComparison.Ordinal) && v.Label.Contains(","));
            if (!labelsOk) return 51;

            // 2. Reading with a chosen voice, each engine (silent output: the same timing, no speakers needed).
            var picks = new List<VoiceEntry>();
            var firstOneCore = all.FirstOrDefault(v => !v.Desktop);
            if (firstOneCore != null) picks.Add(firstOneCore);
            picks.Add(all.First(v => v.Desktop));
            foreach (var pick in picks)
            {
                foreach (var speed in new[] { 1.0, 1.5 })
                {
                    VoiceSettings.Speed = speed;
                    Voices.SavedId = pick.Id;
                    var sw = Stopwatch.StartNew();
                    using (var r = Voices.CreateReader(silent: true))   // what the overlay does
                    {
                        bool rightEngine = pick.Desktop ? r is SpeechReader : r is OneCoreReader;
                        r.Read(Sample);
                        bool done = await WaitFor(() => r.Status == ReadState.Finished, 30000);
                        bool end = r.Plan != null && r.CurrentWord == r.Plan.Words.Count - 1;
                        string cues = r is OneCoreReader oc ? " word_cues=" + oc.UsedWordCues + " cue_count=" + oc.LastCueCount : "";
                        Log("read voice=\"" + pick.Label + "\" engine=" + r.GetType().Name + " speed=" + speed + " right_engine=" + rightEngine + " reader_voice=\"" + r.VoiceName +
                            "\" finished=" + done + " at_last_word=" + end + " progress_events=" + r.ProgressEvents + " ms=" + sw.ElapsedMilliseconds + cues);
                        bool nameOk = pick.Desktop ? r.VoiceName == pick.FullName : r.VoiceName == pick.FullName;
                        if (!rightEngine || !done || !end || r.ProgressEvents < (pick.Desktop ? 3 : 2) || !nameOk) return 52;
                    }
                }
            }
            VoiceSettings.Speed = 1.0;

            // 3. A voice that is gone falls back to the default.
            Voices.SavedId = "onecore:HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Speech_OneCore\\Voices\\Tokens\\Rapcha_Missing";
            using (var r = Voices.CreateReader(silent: true))
            {
                r.Read("Fallback check.");
                bool done = await WaitFor(() => r.Status == ReadState.Finished, 15000);
                Log("missing voice fallback engine=" + r.GetType().Name + " finished=" + done);
                if (!(r is SpeechReader) || !done) return 53;
            }
            Voices.SavedId = "sapi:Rapcha Missing Voice";
            using (var r = Voices.CreateReader(silent: true))
            {
                r.Read("Fallback check.");
                bool done = await WaitFor(() => r.Status == ReadState.Finished, 15000);
                Log("missing desktop voice fallback finished=" + done + " voice=\"" + r.VoiceName + "\"");
                if (!done || r.VoiceName.Length == 0) return 53;
            }

            // 4. Settings: the picker lists them, saves the choice, Preview speaks with it.
            int s4 = await SettingsTest(all);
            if (s4 != 0) return s4;

            // 5. Typing keeps the ring awake.
            return await TypingTest();
        }

        private async Task<int> SettingsTest(List<VoiceEntry> all)
        {
            Voices.SavedId = "";
            SettingsPage.SilentPreview = true;
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var picker = w.Settings.VoicePicker;
            var def = Voices.DefaultSapiId();
            bool defaultShown = picker.SelectedItem is VoiceEntry d0 && d0.Id == def;
            for (Control c = picker; c != null; c = c.Parent)
                if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(picker.Parent.Parent ?? picker); break; }
            await Task.Delay(300);
            var want = all.FirstOrDefault(v => !v.Desktop) ?? all.First();
            w.Settings.UnlockSelectToSpeak();   // v13: the section is locked until Edit
            picker.SelectedItem = picker.Items.Cast<VoiceEntry>().First(v => v.Id == want.Id);
            await Task.Delay(200);
            w.Settings.PreviewButton.PerformClick();
            var prev = w.Settings.LastPreview;
            bool previewOk = prev != null && prev.VoiceName == want.FullName && await WaitFor(() => prev.Status == ReadState.Finished, 20000) && prev.ProgressEvents > 0;
            Log("settings items=" + picker.Items.Count + " default_selected=" + defaultShown + " chose=\"" + want.Label + "\"" +
                " preview_engine=" + prev?.GetType().Name + " preview_ok=" + previewOk);
            await Task.Delay(300);
            w.SavePng(Path.Combine(_out, "settings-voice.png"));
            picker.Focus();
            picker.DroppedDown = true;
            await Task.Delay(600);
            var r = w.Bounds;
            var shotArea = Rectangle.Intersect(Screen.PrimaryScreen.Bounds, new Rectangle(r.X, r.Y, r.Width, r.Height + 260));
            using (var bmp = V11SelfTest.Capture(shotArea)) bmp.Save(Path.Combine(_out, "settings-voice-list.png"), ImageFormat.Png);
            picker.DroppedDown = false;
            w.Settings.SaveSelectToSpeak();     // Done saves
            bool saved = Voices.SavedId == want.Id;
            Log("settings saved_on_done=" + saved);
            // Reopening Settings shows the saved voice.
            w.Settings.LoadVoices();
            bool reloaded = picker.SelectedItem is VoiceEntry d1 && d1.Id == want.Id;
            Log("settings reload_selected_saved=" + reloaded);
            w.Close();
            w.Dispose();
            if (picker.Items.Count != all.Count || !defaultShown || !saved || !previewOk || !reloaded) return 54;
            return 0;
        }

        private async Task<int> TypingTest()
        {
            _notepad = Process.Start("notepad.exe");
            var notepad = await V11SelfTest.WaitWindow(() => { try { _notepad.Refresh(); return _notepad.MainWindowHandle; } catch { return IntPtr.Zero; } }, 10000);
            _forms = Process.Start(Application.ExecutablePath, "--testform");
            var paneForm = await V11SelfTest.WaitWindow(() => Native.FindWindow(null, FieldTestForms.Prefix + "pane"), 10000);
            if (notepad == IntPtr.Zero || paneForm == IntPtr.Zero) return 55;
            Native.SetWindowPos(notepad, IntPtr.Zero, 60, 60, 560, 380, 0x0014);
            var edit = SelfTest.FindEditor(notepad);
            Uia.WarmUp();
            using (var field = new FieldIndicator())
            using (var ring = new RingWindow())
            {
                field.EnableFocusEvents();
                ring.RestAllowed = () => true;
                ring.ShowDocked();
                ring.RestDelayMs = 1500;
                using (var typing = new TypingWaker(ring, field))
                {
                    await V11SelfTest.Focus(notepad, edit);
                    if (!await WaitFor(() => ring.Resting && ring.RestAmount >= 1, 6000)) { Log("ring did not rest"); return 55; }
                    var restBounds = ring.VisibleRingBounds;

                    // a. A key press in Notepad wakes it: smooth, monotonic, anchored. Extra keys during the animation do not restart it.
                    ring.FrameLog = new List<float>();
                    var sw = Stopwatch.StartNew();
                    V11SelfTest.Key(0x41, true); V11SelfTest.Key(0x41, false);
                    bool started = await WaitFor(() => !ring.Resting, 3000);
                    long wakeAt = sw.ElapsedMilliseconds;
                    await Task.Delay(60); V11SelfTest.Key(0x42, true); V11SelfTest.Key(0x42, false);
                    await Task.Delay(60); V11SelfTest.Key(0x43, true); V11SelfTest.Key(0x43, false);
                    bool awake = await WaitFor(() => ring.RestAmount <= 0, 2000);
                    long doneAt = sw.ElapsedMilliseconds;
                    var frames = ring.FrameLog.ToList();
                    ring.FrameLog = null;
                    var awakeBounds = ring.VisibleRingBounds;
                    // Frames are logged for every redraw; keep the transition (from first change to full size).
                    var moving = frames.SkipWhile(f => Math.Abs(f - frames[0]) < 0.01f).ToList();
                    bool monotonic = frames.Zip(frames.Skip(1), (a, b) => b >= a - 0.01f).All(x => x);
                    float full = ring.RingSize, small = (float)(ring.RingSize * RingWindow.RestScale);
                    bool endsFull = frames.Count > 0 && Math.Abs(frames.Last() - full) < 0.5f && frames.First() <= small + 1;
                    int steps = frames.Distinct().Count();
                    long anim = doneAt - wakeAt;
                    bool anchored;
                    switch (ring.Edge)
                    {
                        case DockEdge.Left: anchored = Math.Abs(restBounds.Left - awakeBounds.Left) <= 1; break;
                        case DockEdge.Right: anchored = Math.Abs(restBounds.Right - awakeBounds.Right) <= 1; break;
                        case DockEdge.Top: anchored = Math.Abs(restBounds.Top - awakeBounds.Top) <= 1; break;
                        default: anchored = Math.Abs(restBounds.Bottom - awakeBounds.Bottom) <= 1; break;
                    }
                    Log("wake by_typing=" + started + " key_to_wake_ms=" + wakeAt + " anim_ms=" + anim + " frames=" + frames.Count + " distinct_sizes=" + steps +
                        " monotonic=" + monotonic + " from=" + (frames.Count > 0 ? frames.First().ToString("0.0") : "-") + " to=" + (frames.Count > 0 ? frames.Last().ToString("0.0") : "-") +
                        " full=" + full + " edge=" + ring.Edge + " anchored=" + anchored + " rest_rect=" + restBounds + " awake_rect=" + awakeBounds + " wakes=" + typing.Wakes);
                    if (!started || !awake) return 55;
                    if (!monotonic || !endsFull || steps < 6 || anim > 700 || !anchored || typing.Wakes != 1) return 56;

                    // b. Typing for longer than the idle time keeps it awake.
                    bool restedWhileTyping = false;
                    for (int i = 0; i < 12; i++)
                    {
                        V11SelfTest.Key(0x44, true); V11SelfTest.Key(0x44, false);
                        await Task.Delay(400);
                        if (ring.Resting) restedWhileTyping = true;
                    }
                    Log("typing 4.8 s with a 1.5 s idle time: rested=" + restedWhileTyping + " pokes=" + typing.Pokes + " wakes=" + typing.Wakes + " probes=" + typing.Probes);
                    if (restedWhileTyping || typing.Pokes < 5 || typing.Wakes != 1) return 57;
                    // ...and it rests again once typing stops.
                    bool restedAfter = await WaitFor(() => ring.Resting, 5000);
                    Log("stopped typing: rested_again=" + restedAfter);
                    if (!restedAfter) return 57;

                    // c. Keys with no text field focused (a pane): stays resting.
                    await V11SelfTest.Focus(paneForm, Native.FindWindowEx(paneForm, IntPtr.Zero, null, null));
                    await WaitFor(() => ring.Resting && ring.RestAmount >= 1, 5000);
                    int wakesBefore = typing.Wakes;
                    for (int i = 0; i < 5; i++) { V11SelfTest.Key(0x45, true); V11SelfTest.Key(0x45, false); await Task.Delay(300); }
                    await Task.Delay(500);
                    Log("typing with no text field: still_resting=" + ring.Resting + " wakes_added=" + (typing.Wakes - wakesBefore));
                    if (!ring.Resting || typing.Wakes != wakesBefore) return 58;
                }
            }
            return 0;
        }
    }
}
