using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v17 (--selftest-v17 outDir), run inside the real app (ring, tray menu, Settings):
    /// hold-to-snooze (a quick drag across the middle never shows the circle; held still ~5.5 s it does; dropped on
    /// it the ring hides; dropped elsewhere it docks), "Wake Rapcha now" in the tray menu and on Home, the snooze
    /// coming back by itself, a shortcut waking it, Settings → Snooze durations saved; Whisper on a Low laptop
    /// ("Download lightest model anyway", the warning, a real Tiny download, Whisper chosen offline with a 2 minute
    /// cap, S mode fallback, Delete → built-in speech); and the Start menu / Windows Search, as far as the runner
    /// allows (informational: classification, File Explorer still a normal target, Start opened with the Windows
    /// key, field detection, delivery, a click on a non-activating window keeping Start open).
    /// Exit: 0 ok, 101 snooze gesture, 102 tray, 103 auto-restore, 104 shortcut / settings / Home, 105 Low Whisper,
    /// 107 File Explorer / normal targets, 1 crash. Only counts, sizes, timings and reasons are logged, never words.
    /// </summary>
    internal sealed class V17SelfTest
    {
        private readonly RapchaApp _app;
        private readonly string _out;
        private readonly List<string> _notes = new List<string>();
        private readonly string[] _files = { "snooze-minutes.txt", "snooze-until.txt", "ring.txt", "whisper-model.txt" };
        private readonly byte[][] _backup;
        private readonly string _models;

        public V17SelfTest(RapchaApp app, string outDir)
        {
            _app = app;
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _models = Path.Combine(Path.GetTempPath(), "rapcha-v17-models");
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v17: " + s);

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
            try { Snooze.Wake("self-test end"); } catch { }
            DeviceProbe.Override = null;
            BuiltInSpeech.Override = null;
            WhisperNative.SimulateBlocked = false;
            ModelStore.FolderOverride = null;
            LowMemoryWarning.TestHook = null;
            for (int i = 0; i < _files.Length; i++)
            {
                var p = Path.Combine(AppStorage.Folder, _files[i]);
                try { if (_backup[i] != null) File.WriteAllBytes(p, _backup[i]); else if (File.Exists(p)) File.Delete(p); } catch { }
            }
            Snooze.Reload();
            OfflineSettings.Reload();
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            try { File.WriteAllLines(Path.Combine(_out, "v17-results.txt"), _notes); } catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            _app.ExitForTest();
        }

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms) { if (ok()) return true; await Task.Delay(50); }
            return ok();
        }

        private async Task<int> Steps()
        {
            foreach (var f in new[] { "snooze-minutes.txt", "snooze-until.txt" })
                try { File.Delete(Path.Combine(AppStorage.Folder, f)); } catch { }
            Snooze.Reload();
            int r;
            if ((r = await GestureTest()) != 0) return r;
            if ((r = await TrayTest()) != 0) return r;
            if ((r = await RestoreTest()) != 0) return r;
            if ((r = await ShortcutAndSettingsTest()) != 0) return r;
            if ((r = await LowWhisperTest()) != 0) return r;
            if ((r = await StartMenuTest()) != 0) return r;
            return 0;
        }

        // ---------------- hold-to-snooze ----------------

        private Rectangle Work => Screen.PrimaryScreen.WorkingArea;

        private async Task<int> GestureTest()
        {
            var ring = _app.Ring;
            var wa = Work;
            bool minutesDefault = Snooze.Minutes == 19;

            // 1. A quick drag across the middle (1 s): no circle, docks at an edge.
            ring.SimulatePress();
            for (int i = 0; i <= 20; i++)
            {
                ring.SimulateMoveTo(new Point(wa.X + wa.Width / 4 + i * wa.Width / 40, wa.Y + wa.Height / 2));
                await Task.Delay(50);
            }
            bool noCircleQuick = !ring.SnoozeTarget.Showing && !ring.SnoozeArmed;
            ring.SimulateRelease();
            await Task.Delay(200);
            bool quickOk = noCircleQuick && !Snooze.Active && ring.Visible && OnEdge(ring.RingBounds, wa);

            // 2. A slow wander through the middle, moving a bit more than the tolerance every 300 ms for 8 s: no circle.
            ring.SimulatePress();
            var c0 = new Point(wa.X + wa.Width * 2 / 5, wa.Y + wa.Height * 2 / 5);
            for (int i = 0; i < 27; i++) { ring.SimulateMoveTo(new Point(c0.X + i * 12, c0.Y)); await Task.Delay(300); }
            bool noCircleWander = !ring.SnoozeTarget.Showing;
            ring.SimulateRelease();
            await Task.Delay(200);

            // 3. Held still (2 px of hand jitter) in the middle: the circle appears after ~5.5 s.
            var (armedMs, shown) = await HoldStill(ring, c0);
            bool holdOk = shown && armedMs >= SnoozeHold.HoldMs - 50 && armedMs <= SnoozeHold.HoldMs + 1500;
            var circle = ring.SnoozeTarget;
            var shotArea = Rectangle.Union(ring.RingBounds, new Rectangle(circle.Center.X - circle.SizePx, circle.Center.Y - circle.SizePx, circle.SizePx * 2, circle.SizePx * 2));
            shotArea.Inflate(60, 40);
            shotArea.Intersect(Screen.PrimaryScreen.Bounds);
            await Task.Delay(250);
            Capture(shotArea, "v17-snooze-circle.png");
            bool circleCentred = Math.Abs(circle.Center.X - (wa.X + wa.Width / 2)) < circle.SizePx * 2 && SnoozeZone.InMiddle(new IntRect(wa.X, wa.Y, wa.Width, wa.Height), circle.Center.X, circle.Center.Y);

            // 4. Onto the circle (it fills in), then let go: snoozed, the ring hides.
            var dockBefore = ring.DockSpot;
            ring.SimulateMoveTo(circle.Center);
            await Task.Delay(250);
            bool hover = circle.Hover;
            Capture(shotArea, "v17-snooze-circle-hover.png");
            ring.SimulateRelease();
            await Task.Delay(300);
            bool snoozed = Snooze.Active && !ring.Visible && !circle.Showing && Math.Abs(Snooze.Remaining.TotalMinutes - 19) < 0.2 &&
                           File.Exists(Path.Combine(AppStorage.Folder, "snooze-until.txt")) && ring.DockSpot.Edge == dockBefore.Edge;
            Snooze.Wake("self-test");
            await Task.Delay(200);
            bool backSameDock = ring.Visible && ring.DockSpot.Edge == dockBefore.Edge && Math.Abs(ring.DockSpot.Fraction - dockBefore.Fraction) < 0.01;

            // 5. Circle shown, but dropped near the right edge: docks there as usual, no snooze.
            var (_, shown2) = await HoldStill(ring, c0);
            ring.SimulateMoveTo(new Point(wa.Right - 30, wa.Y + wa.Height / 3));
            await Task.Delay(150);
            ring.SimulateRelease();
            await Task.Delay(300);
            bool elsewhere = shown2 && !Snooze.Active && ring.Visible && ring.Edge == DockEdge.Right && !circle.Showing;

            Log("gesture: quick=" + quickOk + " wander=" + noCircleWander + " armed_ms=" + armedMs + " hold=" + holdOk + " centred=" + circleCentred + " hover=" + hover +
                " snoozed=" + snoozed + " back_same_dock=" + backSameDock + " elsewhere=" + elsewhere + " default19=" + minutesDefault);
            _notes.Add("Hold-to-snooze: a quick drag across the middle shows nothing and docks: " + quickOk + "; a slow wander through the middle shows nothing: " + noCircleWander +
                       "; held still in the middle the Snooze circle appeared after " + armedMs + " ms: " + holdOk + " (centred: " + circleCentred + ")");
            _notes.Add("Dropped on the circle (it fills in: " + hover + "): the ring hides for 19 minutes (default): " + snoozed + "; woken, it is back at the same dock spot: " + backSameDock +
                       "; circle shown but dropped at the right edge: docks there, no snooze: " + elsewhere);
            return quickOk && noCircleWander && holdOk && hover && snoozed && backSameDock && elsewhere && minutesDefault && circleCentred ? 0 : 101;
        }

        private static async Task<(long, bool)> HoldStill(RingWindow ring, Point c)
        {
            ring.SimulatePress();
            ring.SimulateMoveTo(c);
            var sw = Stopwatch.StartNew();
            int i = 0;
            while (!ring.SnoozeTarget.Showing && sw.ElapsedMilliseconds < 9000)
            {
                await Task.Delay(200);
                i++;
                ring.SimulateMoveTo(new Point(c.X + (i % 3) - 1, c.Y + ((i / 2) % 3) - 1));   // hand jitter, within the tolerance
            }
            return (sw.ElapsedMilliseconds, ring.SnoozeTarget.Showing);
        }

        private static bool OnEdge(Rectangle r, Rectangle wa) =>
            r.Left - wa.Left < 30 || wa.Right - r.Right < 30 || r.Top - wa.Top < 30 || wa.Bottom - r.Bottom < 30;

        // ---------------- tray menu ----------------

        private async Task<int> TrayTest()
        {
            Snooze.Start(Snooze.Minutes, "self-test");
            await Task.Delay(300);
            var menu = _app.TrayMenu;
            var wa = Work;
            menu.Show(new Point(wa.Right - 40, wa.Bottom - 10), ToolStripDropDownDirection.AboveLeft);
            await Task.Delay(600);
            bool item = _app.WakeItem.Visible && _app.WakeItem.Text.StartsWith("Wake Rapcha now (") && _app.WakeItem.Text.Contains("min left");
            bool tip = _app.TrayText.StartsWith("Rapcha is snoozed");
            var b = menu.Bounds;
            b.Inflate(12, 12);
            b.Intersect(Screen.PrimaryScreen.Bounds);
            Capture(b, "v17-tray-menu.png");
            _app.WakeItem.PerformClick();
            menu.Close();
            await Task.Delay(300);
            bool woke = !Snooze.Active && _app.Ring.Visible && !_app.WakeItem.Visible;
            Log("tray: item=" + item + " tooltip=" + tip + " woke=" + woke);
            _notes.Add("Tray menu while snoozed shows \"" + _app.WakeItem.Text.Split('(')[0].Trim() + " (… min left)\" and the tooltip says snoozed: " + (item && tip) + "; clicking it brings the ring back: " + woke);
            return item && tip && woke ? 0 : 102;
        }

        // ---------------- comes back by itself ----------------

        private async Task<int> RestoreTest()
        {
            Snooze.StartFor(TimeSpan.FromSeconds(3), "self-test");
            await Task.Delay(300);
            bool hidden = !_app.Ring.Visible;
            var sw = Stopwatch.StartNew();
            bool back = await WaitFor(() => _app.Ring.Visible && !Snooze.Active, 8000);
            long ms = sw.ElapsedMilliseconds + 300;

            // A restart while snoozed stays snoozed (the end time is saved).
            Snooze.Start(19, "self-test");
            Snooze.Reload();
            bool survives = Snooze.Active && Math.Abs(Snooze.Remaining.TotalMinutes - 19) < 0.2;
            Snooze.Wake("self-test");
            Log("restore: hidden=" + hidden + " back=" + back + " ms=" + ms + " survives_restart=" + survives);
            _notes.Add("A 3 s snooze came back by itself after " + ms + " ms: " + (hidden && back) + "; the end time is saved, so a restart stays snoozed: " + survives);
            return hidden && back && survives ? 0 : 103;
        }

        // ---------------- shortcut, Settings → Snooze, Home ----------------

        private async Task<int> ShortcutAndSettingsTest()
        {
            // A voice-typing shortcut while snoozed wakes Rapcha and does its job (here: no key → Settings opens).
            Snooze.Start(19, "self-test");
            await Task.Delay(200);
            _app.SimulateShortcut(ShortcutAction.TapToTalk, true);
            _app.SimulateShortcut(ShortcutAction.TapToTalk, false);
            await Task.Delay(500);
            bool shortcutWakes = !Snooze.Active && _app.Ring.Visible;
            if (_app.IsRecording) _app.CancelForTest();
            try { _app.Main?.Close(); } catch { }
            await Task.Delay(300);

            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var card = w.Settings.SnoozeSettings;
            async Task Shot(string name, Control focus)
            {
                for (Control c = focus; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(focus); break; }
                await Task.Delay(250);
                w.SavePng(Path.Combine(_out, name));
            }
            bool def = card.Option19.Checked && Snooze.Minutes == 19;
            await Shot("v17-settings-snooze.png", card.Status);
            card.Option1h.Checked = true;
            bool h1 = Snooze.Minutes == 60 && Saved() == "60";
            card.Option2h.Checked = true;
            bool h2 = Snooze.Minutes == 120 && Saved() == "120";
            card.OptionCustom.Checked = true;                       // Custom with the suggested 45 minutes
            bool custom = card.OptionCustom.Checked && Snooze.Minutes == 45 && Saved() == "45";
            card.CustomMinutes.Value = 50;                          // then typed 50
            custom &= Snooze.Minutes == 50 && Saved() == "50";
            Snooze.Reload();
            bool reloaded = Snooze.Minutes == 50;
            await Shot("v17-settings-snooze-custom.png", card.Status);
            card.Option19.Checked = true;
            bool back19 = Snooze.Minutes == 19;

            // Home: the snoozed bar with the time left and "Wake Rapcha now".
            Snooze.Start(19, "self-test");
            w.ShowPage(MainWindow.Page.Home);
            await Task.Delay(500);
            bool bar = w.SnoozeBar.Visible && w.SnoozeText.Text.Contains("min left") && w.WakeButton.Visible;
            w.SavePng(Path.Combine(_out, "v17-home-snoozed.png"));
            w.WakeButton.PerformClick();
            await Task.Delay(300);
            bool homeWakes = !Snooze.Active && !w.SnoozeBar.Visible && _app.Ring.Visible;
            w.Close();
            w.Dispose();

            Log("settings: shortcut_wakes=" + shortcutWakes + " default=" + def + " 1h=" + h1 + " 2h=" + h2 + " custom=" + custom + " reloaded=" + reloaded + " back19=" + back19 + " home_bar=" + bar + " home_wakes=" + homeWakes);
            _notes.Add("A voice-typing shortcut while snoozed wakes Rapcha and carries on as usual: " + shortcutWakes);
            _notes.Add("Settings → Snooze: 19 minutes by default: " + def + "; 1 hour, 2 hours and custom minutes (45, then 50) are saved in %AppData%\\Rapcha\\snooze-minutes.txt and read back: " + (h1 && h2 && custom && reloaded && back19));
            _notes.Add("Home while snoozed shows the time left and Wake Rapcha now, which brings the ring back: " + (bar && homeWakes));
            return shortcutWakes && def && h1 && h2 && custom && reloaded && back19 && bar && homeWakes ? 0 : 104;
        }

        private static string Saved()
        {
            try { return File.ReadAllText(Path.Combine(AppStorage.Folder, "snooze-minutes.txt")).Trim(); } catch { return ""; }
        }

        // ---------------- Whisper on a Low laptop ----------------

        private async Task<int> LowWhisperTest()
        {
            ModelStore.FolderOverride = _models;
            try { if (Directory.Exists(_models)) Directory.Delete(_models, true); } catch { }
            OfflineCard.DontOpenSettings = true;
            OfflineSettings.ChosenModel = "";
            DeviceProbe.Override = DeviceFacts.Parse("ram=4096,free=1500,cores=4,disk=20000");
            BuiltInSpeech.Override = true;
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var card = w.Settings.Offline;
            card.Refresh();
            async Task Shot(string name, Control focus)
            {
                for (Control c = focus; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(focus); break; }
                await Task.Delay(250);
                w.SavePng(Path.Combine(_out, name));
            }
            bool offered = card.DownloadButton.Visible && !card.DownloadButton.Enabled && card.AnywayButton.Visible && card.AnywayButton.Enabled &&
                           card.WhisperNote.Text.StartsWith("Your laptop's memory is too low for this.");
            await Shot("v17-settings-whisper-low-anyway.png", card.AnywayButton);
            bool beforeBuiltIn = EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false)).Engine == SpeechEngine.BuiltIn;

            // Cancel in the warning: nothing downloads.
            LowMemoryWarning.TestHook = d => d.NoButton.PerformClick();
            card.AnywayButton.PerformClick();
            await Task.Delay(300);
            bool cancelKeepsNothing = card.Warnings == 1 && !card.Downloading && !ModelStore.IsInstalled(WhisperModels.Tiny);

            // "Download anyway": the warning (hot, slow, memory, battery), then a real download of Tiny.
            bool warningText = false;
            LowMemoryWarning.TestHook = d =>
            {
                var all = string.Join(" ", AllText(d));
                warningText = all.Contains("hot") && all.Contains("slow") && all.Contains("memory") && all.Contains("battery") && all.Contains("Delete model");
                d.SavePng(Path.Combine(_out, "v17-whisper-warning.png"));
                d.YesButton.PerformClick();
            };
            ModelStore.Outcome? outcome = null;
            Action<ModelStore.Outcome> onDone = o => outcome = o;
            card.DownloadFinished += onDone;
            var sw = Stopwatch.StartNew();
            card.AnywayButton.PerformClick();
            await WaitFor(() => outcome.HasValue, 300000);
            card.DownloadFinished -= onDone;
            long dlMs = sw.ElapsedMilliseconds;
            await WaitFor(() => WhisperNative.Status != WhisperNative.State.NotTried, 15000);
            card.Refresh();
            bool installed = outcome == ModelStore.Outcome.Done && ModelStore.IsInstalled(WhisperModels.Tiny) && card.DeleteButton.Visible && !card.AnywayButton.Visible &&
                             card.WhisperNote.Text.StartsWith("Whisper Tiny is downloaded");
            await Shot("v17-settings-whisper-low-downloaded.png", card.DeleteButton);

            // Offline: Whisper (2 minutes); online: Gemini.
            var choice = EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false));
            bool usesWhisper = choice.Engine == SpeechEngine.Whisper && SessionCaps.MaxSeconds(SpeechEngine.Whisper, DeviceProbe.Tier) == 120;
            bool engineStarts = false;
            if (usesWhisper)
            {
                var engine = OfflineEngines.Start(choice, new List<string>(), out var note);
                engineStarts = engine != null && engine.Kind == SpeechEngine.Whisper;
                try { engine?.Dispose(); } catch { }
            }
            bool online = EnginePolicy.Choose(OfflineEngines.Inputs(true)).Engine == SpeechEngine.Gemini;

            // S mode: Whisper refused → the message, and built-in speech.
            WhisperNative.SimulateBlocked = true;
            card.Refresh();
            bool smode = card.WhisperNote.Text == WhisperNative.BlockedMessage && EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false)).Engine == SpeechEngine.BuiltIn;
            WhisperNative.SimulateBlocked = false;
            card.Refresh();

            // Delete: straight back to built-in Windows speech.
            card.DeleteButton.PerformClick();
            await Task.Delay(200);
            bool deleted = !ModelStore.IsInstalled(WhisperModels.Tiny) && card.WhisperNote.Text.StartsWith("Deleted.") &&
                           EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false)).Engine == SpeechEngine.BuiltIn && card.AnywayButton.Visible;
            w.Close();
            w.Dispose();
            DeviceProbe.Override = null;
            BuiltInSpeech.Override = null;
            ModelStore.FolderOverride = null;
            LowMemoryWarning.TestHook = null;

            Log("low whisper: offered=" + offered + " before_builtin=" + beforeBuiltIn + " cancel=" + cancelKeepsNothing + " warning=" + warningText + " outcome=" + outcome + " ms=" + dlMs +
                " installed=" + installed + " whisper=" + usesWhisper + " starts=" + engineStarts + " engine_state=" + WhisperNative.Status + " online_gemini=" + online + " smode=" + smode + " deleted=" + deleted);
            _notes.Add("Low laptop: the memory warning stays, Download Whisper model stays greyed out, and \"Download lightest model anyway\" is offered: " + offered + " (built-in speech until then: " + beforeBuiltIn + ")");
            _notes.Add("The warning says the laptop may get hot, slow down, and use a lot of memory and battery: " + warningText + "; Cancel downloads nothing: " + cancelKeepsNothing +
                       "; Download anyway fetched Whisper Tiny with the SHA-256 check in " + (dlMs / 1000) + " s: " + installed);
            _notes.Add("Low laptop with Tiny: offline uses Whisper with a 2 minute limit: " + usesWhisper + " (engine starts: " + engineStarts + "); online still uses Gemini: " + online +
                       "; simulated S mode shows the message and uses built-in speech: " + smode + "; Delete model goes straight back to built-in Windows speech: " + deleted);
            return offered && beforeBuiltIn && cancelKeepsNothing && warningText && installed && usesWhisper && engineStarts && online && smode && deleted ? 0 : 105;
        }

        private static IEnumerable<string> AllText(Control c)
        {
            yield return c.Text ?? "";
            foreach (Control ch in c.Controls) foreach (var t in AllText(ch)) yield return t;
        }

        // ---------------- Start menu / Windows Search (as far as the runner allows) ----------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private sealed class Probe : LayeredWindow
        {
            public int Downs;
            public Probe() : base(false) { }
            protected override void OnMouseDown(MouseEventArgs e) { Downs++; base.OnMouseDown(e); }
        }

        private async Task<int> StartMenuTest()
        {
            // Normal targets are unchanged: File Explorer is a normal app, the taskbar and desktop are not targets.
            var tray = FindWindow("Shell_TrayWnd", null);
            bool trayNot = tray == IntPtr.Zero || (!TextDelivery.IsUsableTarget(tray) && ShellWindows.SurfaceOf(tray) == ShellSurface.OtherShell);
            var desk = FindWindow("Progman", null);
            bool deskNot = desk == IntPtr.Zero || !TextDelivery.IsUsableTarget(desk);
            IntPtr explorer = IntPtr.Zero;
            bool explorerOk = true, explorerFound = false;
            try
            {
                var before = new HashSet<IntPtr>(TopWindows("CabinetWClass"));
                Process.Start(new ProcessStartInfo("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.Windows)) { UseShellExecute = true });
                await WaitFor(() => TopWindows("CabinetWClass").Any(h => !before.Contains(h)), 10000);
                explorer = TopWindows("CabinetWClass").FirstOrDefault(h => !before.Contains(h));
                explorerFound = explorer != IntPtr.Zero;
                if (explorerFound) explorerOk = TextDelivery.IsUsableTarget(explorer) && ShellWindows.SurfaceOf(explorer) == ShellSurface.None && !TextDelivery.IsStartSearch(explorer);
            }
            catch (Exception e) { Log("explorer: " + e.GetType().Name); }
            if (explorer != IntPtr.Zero) PostMessage(explorer, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
            await Task.Delay(500);
            Log("targets: taskbar_not=" + trayNot + " desktop_not=" + deskNot + " explorer_found=" + explorerFound + " explorer_normal=" + explorerOk + " hook=" + ShellWindows.Hooked);
            _notes.Add("Normal targets unchanged: File Explorer is still a normal app to type into: " + (explorerFound ? explorerOk.ToString() : "not opened on this runner") +
                       "; the taskbar and desktop are still not targets: " + (trayNot && deskNot) + "; foreground hook on: " + ShellWindows.Hooked);
            if (!(trayNot && deskNot && explorerOk)) return 107;

            // Open Start with the Windows key.
            var sb = new System.Text.StringBuilder();
            Key(0x5B, false); Key(0x5B, true);
            bool open = await WaitFor(() => ShellWindows.StartOrSearchInFront(out _), 4000);
            if (!open)
            {
                var fg = Native.GetForegroundWindow();
                var info = "foreground=" + TextDelivery.ClassOf(Native.GetAncestor(fg, Native.GA_ROOT)) + " exe=" + ShellTargets.ExeName(ShellWindows.ExeOf(fg));
                Log("start menu: did not open on this runner (" + info + ")");
                _notes.Add("Start menu on the CI runner: pressing the Windows key did not bring up Start / Search here (" + info + "), so Start was checked by unit tests only (not verified on a real Start menu).");
                return 0;
            }
            ShellWindows.StartOrSearchInFront(out var root);
            await Task.Delay(1200);
            var exe = ShellTargets.ExeName(ShellWindows.ExeOf(root));
            var bounds = ShellWindows.BoundsOf(root);
            Capture(Screen.PrimaryScreen.Bounds, "v17-start-menu-runner.png");
            bool usable = TextDelivery.IsUsableTarget(root);
            var field = await Uia.FocusedFieldAsync(3000, sb);
            bool aside = _app.MovedAside || !_app.Ring.RingBounds.IntersectsWith(bounds);

            // A click on a non-activating Rapcha-style window outside Start: does Start stay open?
            bool stayedOpen = false; int downs = 0;
            var wa = Work;
            var spot = new[] { new Point(wa.Left + 40, wa.Top + 40), new Point(wa.Right - 100, wa.Top + 40), new Point(wa.Left + 40, wa.Bottom - 100), new Point(wa.Right - 100, wa.Bottom - 100) }
                .FirstOrDefault(p => !new Rectangle(p.X, p.Y, 60, 60).IntersectsWith(bounds));
            if (spot != Point.Empty)
            {
                using (var probe = new Probe())
                using (var bmp = new Bitmap(60, 60))
                {
                    using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, 200, 220, 120));
                    probe.Present(bmp, spot.X, spot.Y);
                    probe.Show();
                    probe.KeepOnTop();
                    await Task.Delay(300);
                    RapchaApp.SendClick(new Point(spot.X + 30, spot.Y + 30));
                    await Task.Delay(700);
                    downs = probe.Downs;
                    stayedOpen = ShellWindows.StartOrSearchInFront(out _);
                    probe.Hide();
                }
            }

            // Delivery into the Start search box (a test word; only lengths and reasons are logged).
            string result = "skipped";
            if (ShellWindows.StartOrSearchInFront(out root))
            {
                var r = TextDelivery.Deliver("rapcha", root);
                result = r + " (" + TextDelivery.LastReason + ")";
            }
            Capture(Screen.PrimaryScreen.Bounds, "v17-start-menu-typed-runner.png");
            Key(0x1B, false); Key(0x1B, true);
            await Task.Delay(300);
            if (ShellWindows.StartOrSearchInFront(out _)) { Key(0x1B, false); Key(0x1B, true); }
            bool closed = await WaitFor(() => !ShellWindows.StartOrSearchInFront(out _), 3000);
            await Task.Delay(400);
            bool restored = !_app.MovedAside;

            Log("start menu: exe=" + exe + " bounds=" + bounds.Width + "x" + bounds.Height + " usable=" + usable + " field_editable=" + field?.Editable + " rule=" + field?.Reason + " type=" + field?.Type +
                " framework=" + field?.Framework + " app_saw_start=" + _app.StartInFront + " ring_clear=" + aside + " probe_downs=" + downs + " stayed_open=" + stayedOpen + " delivery=" + result + " closed=" + closed + " restored=" + restored);
            _notes.Add("Start / Search on the CI runner (informational): opened " + exe + " (" + bounds.Width + "x" + bounds.Height + "); now a voice-typing target: " + usable +
                       "; search box detected as a text field: " + (field?.Editable ?? false) + " (" + field?.Type + "/" + field?.Framework + ", rule " + field?.Reason + ")" +
                       "; Rapcha's ring kept clear of it: " + aside);
            _notes.Add("A click on a non-activating window beside Start reached it (" + downs + " clicks) and Start stayed open: " + stayedOpen + "; typing a test word into Start search: " + result +
                       "; after Esc the ring went back to its spot: " + (closed && restored));
            return 0;
        }

        private static IEnumerable<IntPtr> TopWindows(string cls)
        {
            var list = new List<IntPtr>();
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero) { if (Native.IsWindowVisible(h)) list.Add(h); if (list.Count > 50) break; }
            return list;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        private static void Key(ushort vk, bool up)
        {
            var i = new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (vk == 0x5B ? 1u : 0u) } }
            };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
            Thread.Sleep(30);
        }

        /// <summary>Screen pixels including layered windows (the ring, the circle).</summary>
        private void Capture(Rectangle r, string name)
        {
            try
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr dst = g.GetHdc(), src = Native.GetDC(IntPtr.Zero);
                        try { Native.BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, Native.SRCCOPY | Native.CAPTUREBLT); }
                        finally { Native.ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
                    }
                    bmp.Save(Path.Combine(_out, name), ImageFormat.Png);
                }
            }
            catch (Exception e) { Log("capture " + name + " failed " + e.GetType().Name); }
        }
    }
}
