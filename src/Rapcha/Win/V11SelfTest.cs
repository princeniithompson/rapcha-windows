using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;
using Shortcut = Rapcha.Core.Shortcut;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v11 (--selftest-v11 outDir): real text-field detection (Notepad accepted; a big pane, a list
    /// and the desktop rejected), the outline following focus and hiding when it leaves, the ring waking on focus
    /// into a field, the glow colour on the words box, the 4-line words box at 100/150/200% and with text scale,
    /// the rounded screen-edge glow, Space+V / Space+B chords (a lone Space still types), hold-to-talk events,
    /// conflict warnings, and the Settings Shortcuts card. Saves PNGs to outDir.
    /// Exit codes: 0 ok, 31 Notepad not accepted, 32 pane/list/desktop not rejected, 33 outline did not move,
    /// 34 outline did not hide, 35 ring wake, 36 bubble colour, 37 bubble layout, 38 edge glow, 39 chords/lone Space,
    /// 40 hold-to-talk events, 41 conflict logic, 42 Settings shortcuts, 1 crash.
    /// </summary>
    internal sealed class V11SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "shortcuts.txt", "glow.txt" };
        private readonly byte[][] _backup;
        private Process _forms, _notepad;

        public V11SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v11: " + s);

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

        private static async Task<IntPtr> WaitWindow(Func<IntPtr> find, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms)
            {
                var h = find();
                if (h != IntPtr.Zero) return h;
                await Task.Delay(150);
            }
            return IntPtr.Zero;
        }

        /// <summary>Brings a window to the front and clicks a point in it (so the right control has keyboard focus).</summary>
        private static async Task Focus(IntPtr top, IntPtr child = default(IntPtr))
        {
            TextDelivery.BringToFront(top, new System.Text.StringBuilder());
            await Task.Delay(150);
            var target = child != IntPtr.Zero ? child : top;
            Native.GetWindowRect(target, out var r);
            int cx = (r.Left + r.Right) / 2, cy = child != IntPtr.Zero ? (r.Top + r.Bottom) / 2 : r.Top + (r.Bottom - r.Top) * 2 / 3;
            RapchaApp.SendClick(new Point(cx, cy));
            await Task.Delay(250);
        }

        /// <summary>Waits until the indicator's decision matches (it re-checks every 300 ms and on focus events).</summary>
        private static async Task<bool> WaitField(FieldIndicator fi, Func<bool> ok, int ms = 4000)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms)
            {
                if (ok()) return true;
                await Task.Delay(100);
            }
            return ok();
        }

        private static string Describe(FieldIndicator fi) =>
            "has_field=" + fi.HasField + " type=" + fi.Last?.Type + " fw=" + fi.Last?.Framework + " rule=" + fi.Last?.Reason +
            " rect=" + fi.Last?.Bounds.Width + "x" + fi.Last?.Bounds.Height + " outline=" + (fi.Active ? fi.Outline.Width + "x" + fi.Outline.Height : "none") +
            " target_set=" + (fi.Target != IntPtr.Zero) + " moves=" + fi.Moves + " checks=" + fi.Checks;

        private static IntPtr Root(IntPtr h) => h == IntPtr.Zero ? h : Native.GetAncestor(h, Native.GA_ROOT);

        private async Task<int> Steps()
        {
            Uia.WarmUp();
            // Windows to test against: Notepad, and helper windows in a second Rapcha process.
            _notepad = Process.Start("notepad.exe");
            var notepad = await WaitWindow(() => { try { _notepad.Refresh(); return _notepad.MainWindowHandle; } catch { return IntPtr.Zero; } }, 10000);
            _forms = Process.Start(Application.ExecutablePath, "--testform");
            var textForm = await WaitWindow(() => Native.FindWindow(null, FieldTestForms.Prefix + "text"), 10000);
            var paneForm = Native.FindWindow(null, FieldTestForms.Prefix + "pane");
            var listForm = Native.FindWindow(null, FieldTestForms.Prefix + "list");
            Log("windows notepad=" + (notepad != IntPtr.Zero) + " text=" + (textForm != IntPtr.Zero) + " pane=" + (paneForm != IntPtr.Zero) + " list=" + (listForm != IntPtr.Zero));
            if (notepad == IntPtr.Zero || textForm == IntPtr.Zero || paneForm == IntPtr.Zero || listForm == IntPtr.Zero) return 31;
            Native.SetWindowPos(notepad, IntPtr.Zero, 60, 60, 560, 380, 0x0014 /* NOZORDER|NOACTIVATE */);
            var edit = SelfTest.FindEditor(notepad);
            await Task.Delay(500);

            var pink = Color.FromArgb(0xF0, 0x55, 0x9B);
            Accent.Set(pink);
            using (var fi = new FieldIndicator { Color = pink })
            {
                fi.EnableFocusEvents();
                int changes = 0;
                fi.Changed += _ => changes++;

                // 1. Notepad: a real edit with keyboard focus and a caret.
                await Focus(notepad, edit);
                fi.Start(IntPtr.Zero);
                bool ok = await WaitField(fi, () => fi.HasField == true && Root(fi.Target) == notepad && fi.Active);
                var notepadOutline = fi.Outline;
                Log("notepad " + Describe(fi));
                Shot("outline-notepad.png");
                if (!ok) return 31;

                // 2. Focus moves to another field (a WinForms TextBox in another app): the outline and paste target follow.
                int movesBefore = fi.Moves;
                await Focus(textForm, Native.FindWindowEx(textForm, IntPtr.Zero, null, null));
                ok = await WaitField(fi, () => fi.HasField == true && Root(fi.Target) == textForm && fi.Active && fi.Outline != notepadOutline);
                Log("moved_to_textbox " + Describe(fi) + " moves_before=" + movesBefore);
                if (!ok || fi.Moves <= movesBefore) return 33;

                // 3. Focus leaves for a big pane (no caret, ~80% of the screen): hidden, no target (clipboard only).
                await Focus(paneForm, Native.FindWindowEx(paneForm, IntPtr.Zero, null, null));
                ok = await WaitField(fi, () => fi.HasField == false && fi.Target == IntPtr.Zero);
                await Task.Delay(400); // fade
                Log("pane " + Describe(fi));
                if (!ok) return 32;
                if (fi.Active) return 34;

                // 4. A list box: rejected.
                await Focus(textForm, Native.FindWindowEx(textForm, IntPtr.Zero, null, null));
                await WaitField(fi, () => fi.HasField == true, 3000);
                await Focus(listForm, Native.FindWindowEx(listForm, IntPtr.Zero, null, null));
                ok = await WaitField(fi, () => fi.HasField == false && !fi.Active);
                Log("list " + Describe(fi));
                if (!ok) return 32;

                // 5. The desktop: rejected.
                var progman = Native.FindWindow("Progman", null);
                TextDelivery.BringToFront(progman, new System.Text.StringBuilder());
                await Task.Delay(300);
                bool desktopFront = Root(Native.GetForegroundWindow()) == progman || TextDelivery.ClassOf(Native.GetForegroundWindow()) == "WorkerW";
                ok = await WaitField(fi, () => fi.HasField == false && !fi.Active, 3000);
                Log("desktop foreground=" + desktopFront + " " + Describe(fi));
                if (!ok) return 32;

                // 6. Back to Notepad: shown again.
                await Focus(notepad, edit);
                ok = await WaitField(fi, () => fi.HasField == true && Root(fi.Target) == notepad && fi.Active);
                Log("notepad_again " + Describe(fi) + " changes=" + changes);
                if (!ok) return 31;
                fi.Stop();
                await Task.Delay(400);
                Log("stopped outline_active=" + fi.Active);
                if (fi.Active) return 34;

                // 7. The ring wakes when focus enters a text field (UIA focus event), not for a pane.
                using (var ring = new RingWindow())
                {
                    ring.RestAllowed = () => true;
                    ring.ShowDocked();
                    ring.RestDelayMs = 400;
                    fi.WantFocusChecks = () => ring.Resting;
                    int wakes = 0;
                    fi.FocusEnteredField += () => { wakes++; ring.Wake(); };
                    await Focus(paneForm, Native.FindWindowEx(paneForm, IntPtr.Zero, null, null));
                    bool rested = await WaitField(fi, () => ring.Resting, 4000);
                    await Task.Delay(1200);
                    bool stillResting = ring.Resting && wakes == 0;
                    ring.RestDelayMs = 60000;
                    await Focus(textForm, Native.FindWindowEx(textForm, IntPtr.Zero, null, null));
                    bool woke = await WaitField(fi, () => !ring.Resting, 4000);
                    Log("ring rested=" + rested + " pane_kept_resting=" + stillResting + " woke_on_field=" + woke + " wakes=" + wakes);
                    fi.WantFocusChecks = null;
                    if (!rested || !stillResting || !woke || wakes < 1) return 35;
                }
            }

            // 8. Words box: the glow colour, 4 lines, slim buttons, keyboard badge; 100/150/200% and text scale.
            int r8 = Bubbles(pink);
            if (r8 != 0) return r8;

            // 9. Rounded screen-edge glow.
            int r9 = await EdgeGlowTest(pink);
            if (r9 != 0) return r9;

            // 10. Shortcuts: chords, lone Space, hold-to-talk, conflicts.
            int r10 = await ShortcutTest(notepad, edit);
            if (r10 != 0) return r10;

            // 11. Settings → Shortcuts.
            return await SettingsTest();
        }

        private void Shot(string name)
        {
            try
            {
                var vs = Screen.PrimaryScreen.Bounds;
                using (var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                    bmp.Save(Path.Combine(_out, name), ImageFormat.Png);
                }
            }
            catch (Exception e) { Log("screenshot failed " + name + " " + e.GetType().Name); }
        }

        private static double Dist(Color a, Color b) => Math.Sqrt(Sq(a.R - b.R) + Sq(a.G - b.G) + Sq(a.B - b.B));
        private static double Sq(double x) => x * x;

        /// <summary>The Complete button's fill: the closest of a few points away from its label.</summary>
        private static double Closest(Bitmap b, Rectangle r, Color want)
        {
            var pts = new[] { new Point(r.X + r.Width / 2, r.Y + 3), new Point(r.X + r.Height / 2, r.Y + r.Height / 2), new Point(r.Right - r.Height / 2, r.Y + r.Height / 2), new Point(r.X + r.Width / 2, r.Bottom - 3) };
            return pts.Where(p => p.X >= 0 && p.Y >= 0 && p.X < b.Width && p.Y < b.Height).Select(p => Dist(b.GetPixel(p.X, p.Y), want)).DefaultIfEmpty(999).Min();
        }

        private const string Long = "Remind me to send the invoice to Ama before Friday, and copy Kofi on the email. The meeting has moved to Thursday at 3 pm, so please bring the printed slides, the budget sheet and the new design notes for the whole team. After that we will walk over to Osu for kelewele and plan the trip to Labadi Beach on Saturday morning.";

        private int Bubbles(Color pink)
        {
            var cases = new[] { (96, 1.0), (144, 1.0), (192, 1.0), (96, 1.5), (144, 1.5), (96, 2.25) };
            bool allOk = true, colourOk = true;
            var sheet = new List<Bitmap>();
            foreach (var (dpi, ts) in cases)
            {
                foreach (bool? field in new bool?[] { true, false })
                {
                    var p = new BubblePainter();
                    p.Setup(dpi, ts);
                    p.SetMode(WordsBubble.Mode.Listening, Long);
                    p.Field = field;
                    Bitmap bmp = null;
                    for (int i = 0; i < 40; i++) { p.Step(); bmp?.Dispose(); bmp = p.Render(); }
                    var bounds = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    bool inside = bounds.Contains(p.CancelRect) && bounds.Contains(p.CompleteRect) && bounds.Contains(p.IconRect) &&
                                  p.CancelRect.Right <= p.IconRect.Left && p.IconRect.Right <= p.CompleteRect.Left;
                    bool lines = p.VisibleLines == 4 && p.LineCount > 4;
                    bool sizeOk = Math.Abs(bmp.Width - 420 * dpi / 96.0) <= 2 && Math.Abs(bmp.Height - 120 * dpi / 96.0) <= 2;
                    double d = Closest(bmp, p.CompleteRect, pink);
                    if (d > 40) colourOk = false;
                    Log("bubble dpi=" + dpi + " text_scale=" + ts + " field=" + field + " size=" + bmp.Width + "x" + bmp.Height + " lines=" + p.LineCount +
                        " visible=" + p.VisibleLines + " button_px=" + p.ButtonPx.ToString("0.0") + " buttons_inside=" + inside + " complete_fill_vs_pink=" + d.ToString("0"));
                    if (!inside || !lines || !sizeOk) allOk = false;
                    string tag = (field == true ? "check" : "nofield") + "-" + (dpi * 100 / 96) + (ts != 1.0 ? "-text" + (int)(ts * 100) : "");
                    SaveOnBackdrop(bmp, "bubble-" + tag + ".png");
                    sheet.Add(bmp);
                }
            }
            foreach (var b in sheet) b.Dispose();
            // The same painter with the default colour: the accent really comes from the setting.
            Accent.Set(Color.FromArgb(0x42, 0x85, 0xF4));
            var q = new BubblePainter(); q.Setup(96, 1.0); q.SetMode(WordsBubble.Mode.Listening, "Testing the blue glow colour."); q.Field = true;
            using (var blue = q.Render())
            {
                bool blueOk = Closest(blue, q.CompleteRect, Color.FromArgb(0x42, 0x85, 0xF4)) < 40;
                Log("bubble colour pink_ok=" + colourOk + " blue_ok=" + blueOk);
                SaveOnBackdrop(blue, "bubble-check-100-blue.png");
                Accent.Set(pink);
                if (!colourOk || !blueOk) return 36;
            }
            return allOk ? 0 : 37;
        }

        private void SaveOnBackdrop(Bitmap bubble, string name)
        {
            int pad = 24;
            using (var bmp = new Bitmap(bubble.Width + pad * 2, bubble.Height + pad * 2, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height), Color.FromArgb(232, 236, 240), Color.FromArgb(200, 210, 222), 45f))
                        g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);
                    g.DrawImage(bubble, pad, pad);
                }
                bmp.Save(Path.Combine(_out, name), ImageFormat.Png);
            }
        }

        private async Task<int> EdgeGlowTest(Color pink)
        {
            // Shape: zero outside the rounded corner, strong at the edge, soft fall-off inwards.
            var size = new Size(1600, 900);
            int corner = EdgeGlow.AlphaAt(size, 1.0, 0, 0);
            int nearCorner = EdgeGlow.AlphaAt(size, 1.0, 3, 3);
            int edge = EdgeGlow.AlphaAt(size, 1.0, 800, 0);
            int in8 = EdgeGlow.AlphaAt(size, 1.0, 800, 8);
            int in24 = EdgeGlow.AlphaAt(size, 1.0, 800, 24);
            int deep = EdgeGlow.AlphaAt(size, 1.0, 800, 120);
            int side = EdgeGlow.AlphaAt(size, 1.0, 0, 450);
            int corner2 = EdgeGlow.AlphaAt(size, 2.0, 0, 0);
            bool shape = corner == 0 && nearCorner < edge / 2 && edge > 180 && in8 < edge && in24 < in8 && in24 > 0 && deep == 0 && side > 180 && corner2 == 0;
            Log("edge_glow alpha corner=" + corner + " near_corner=" + nearCorner + " edge=" + edge + " in8=" + in8 + " in24=" + in24 + " deep=" + deep + " side=" + side + " corner_200=" + corner2 + " ok=" + shape);

            // A preview over a light and a dark backdrop.
            foreach (var dark in new[] { false, true })
                using (var bg = new Bitmap(1280, 720, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bg))
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), dark ? Color.FromArgb(28, 30, 36) : Color.FromArgb(246, 246, 242), dark ? Color.FromArgb(52, 56, 70) : Color.FromArgb(214, 222, 232), 30f))
                    {
                        g.FillRectangle(br, 0, 0, 1280, 720);
                        using (var f = new Font("Segoe UI", 22f)) using (var b = new SolidBrush(dark ? Color.White : Color.FromArgb(40, 40, 40)))
                            g.DrawString("Select to speak — drag over text to hear it", f, b, 300, 330);
                    }
                    using (var shot = EdgeGlow.RenderPreview(bg, 1.0, pink, 1.1))
                        shot.Save(Path.Combine(_out, dark ? "edge-glow-dark.png" : "edge-glow.png"), ImageFormat.Png);
                }

            // The real windows on screen, captured.
            using (var glow = new EdgeGlow { Color = pink })
            {
                var screen = Screen.PrimaryScreen.Bounds;
                glow.Show(screen, null);
                await Task.Delay(900);
                Color mid;
                using (var cap = new Bitmap(1, 1))
                {
                    using (var g = Graphics.FromImage(cap)) g.CopyFromScreen(screen.X + screen.Width / 2, screen.Y + 1, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                    mid = cap.GetPixel(0, 0);
                }
                Shot("edge-glow-screen.png");
                bool shown = glow.Active && glow.WindowCount >= 4;
                glow.Hide();
                await Task.Delay(600);
                bool hidden = !glow.Active;
                bool pinkish = mid.R > mid.G + 40;
                Log("edge_glow screen windows=" + glow.WindowCount + " shown=" + shown + " top_mid_pinkish=" + pinkish + " rgb=" + mid.R + "," + mid.G + "," + mid.B + " hidden_after_fade=" + hidden);
                if (!shape || !shown || !hidden || !pinkish) return 38;
            }
            return 0;
        }

        // ---- Keyboard shortcuts ----

        private static void Key(int vk, bool down)
        {
            var i = new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)Native.MapVirtualKey((uint)vk, 0), dwFlags = down ? 0 : Native.KEYEVENTF_KEYUP } }
            };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static async Task Tap(int vk, int holdMs = 30) { Key(vk, true); await Task.Delay(holdMs); Key(vk, false); await Task.Delay(60); }

        private async Task<int> ShortcutTest(IntPtr notepad, IntPtr edit)
        {
            // Conflict logic (warn, don't block).
            var warnCopy = ShortcutRules.Warning(Shortcut.TryParse("Ctrl+C", out var c1) ? c1 : null);
            var warnAltTab = ShortcutRules.Warning(Shortcut.TryParse("Alt+Tab", out var c2) ? c2 : null);
            var warnSpace = ShortcutRules.Warning(Shortcut.TryParse("Space", out var c3) ? c3 : null);
            var warnWin = ShortcutRules.Warning(Shortcut.TryParse("Win+V", out var c4) ? c4 : null);
            var warnOk = ShortcutRules.Warning(Shortcut.TryParse("Ctrl+Alt+V", out var c5) ? c5 : null);
            var warnChord = ShortcutRules.Warning(Shortcut.TryParse("Space+V", out var c6) ? c6 : null);
            var clashSet = ShortcutSet.Defaults();
            var clash = clashSet.Clash(ShortcutAction.HoldToTalk, clashSet.Tap);
            bool conflicts = warnCopy != null && warnAltTab != null && warnSpace != null && warnWin != null && warnOk == null && warnChord == null && clash == ShortcutAction.TapToTalk;

            var events = new List<string>();
            using (var m = new ShortcutManager(makeCurrent: false))
            using (var other = new ShortcutManager(makeCurrent: false))
            {
                m.Triggered += (a, down) => events.Add(a + (down ? "+" : "-"));
                var set = new ShortcutSet();
                Shortcut.TryParse("Space+V", out var tap); Shortcut.TryParse("Space+B", out var hold); Shortcut.TryParse("Ctrl+Shift+F9", out var read);
                set.Tap = tap; set.Hold = hold; set.Read = read;
                m.Apply(set);
                // A combo another app (here: a second manager) already owns is reported.
                var taken = new ShortcutSet { Tap = new Shortcut(), Read = new Shortcut() };
                Shortcut.TryParse("Ctrl+Shift+F9", out var dup); taken.Hold = dup;
                other.Apply(taken);
                var problem = other.ProblemFor(ShortcutAction.HoldToTalk);
                Log("conflicts copy=\"" + warnCopy + "\" alt_tab=" + (warnAltTab != null) + " space_alone=" + (warnSpace != null) + " win=" + (warnWin != null) +
                    " ctrl_alt_v_clean=" + (warnOk == null) + " space_v_clean=" + (warnChord == null) + " clash=" + clash + " register_failed_reported=" + (problem != null) + " hook=" + m.HookActive);
                if (!conflicts || problem == null || !m.HookActive) return 41;
                other.Apply(new ShortcutSet { Tap = new Shortcut(), Read = new Shortcut(), Hold = new Shortcut() });

                await Focus(notepad, edit);
                Native.SendMessage(edit, 0x000C /* WM_SETTEXT */, IntPtr.Zero, new System.Text.StringBuilder(""));
                await Task.Delay(200);

                // a. A lone Space still types a space.
                await Tap(0x20, 40);
                await Task.Delay(250);
                string afterSpace = SelfTest.GetText(edit);
                // b. Space+V: tap to talk (nothing typed).
                Key(0x20, true); await Task.Delay(250); await Tap(0x56); Key(0x20, false); await Task.Delay(300);
                // c. Space+B held: hold to talk starts, and ends when B is let go.
                Key(0x20, true); await Task.Delay(250); Key(0x42, true); await Task.Delay(400);
                var whileHeld = string.Join(",", events);
                Key(0x42, false); await Task.Delay(100); Key(0x20, false); await Task.Delay(300);
                // d. Fast typing roll-over (" a") is typing, not a chord; then a plain "b".
                Key(0x20, true); await Task.Delay(15); Key(0x41, true); await Task.Delay(20); Key(0x20, false); await Task.Delay(20); Key(0x41, false); await Task.Delay(80);
                await Tap(0x42);
                // e. Ctrl+Shift+F9 (RegisterHotKey): select to speak.
                Key(0x11, true); Key(0x10, true); await Tap(0x78); Key(0x10, false); Key(0x11, false);
                await Task.Delay(400);
                string text = SelfTest.GetText(edit);
                string all = string.Join(",", events);
                Log("chords after_lone_space=\"" + afterSpace.Replace(" ", "·") + "\" notepad=\"" + text.Replace(" ", "·") + "\" events=" + all + " while_held=" + whileHeld +
                    " max_hook_us=" + m.MaxHookMicros);
                bool chordOk = afterSpace == " " && text == "  ab" && events.Count(e => e == "TapToTalk+") == 1 && events.Count(e => e == "SelectToSpeak+") == 1;
                bool holdOk = whileHeld.EndsWith("HoldToTalk+") && events.Count(e => e == "HoldToTalk+") == 1 && events.Count(e => e == "HoldToTalk-") == 1 &&
                              events.IndexOf("HoldToTalk-") > events.IndexOf("HoldToTalk+");
                if (!chordOk) return 39;
                if (!holdOk) return 40;

                // f. A modifier hold-to-talk (Ctrl+Shift+F8): pressed while held, released when F8 is let go.
                events.Clear();
                var set2 = new ShortcutSet { Tap = new Shortcut(), Read = new Shortcut() };
                Shortcut.TryParse("Ctrl+Shift+F8", out var hold2); set2.Hold = hold2;
                m.Apply(set2);
                Key(0x11, true); Key(0x10, true); Key(0x77, true); await Task.Delay(500);
                var held2 = string.Join(",", events);
                Key(0x77, false); await Task.Delay(300); Key(0x10, false); Key(0x11, false); await Task.Delay(200);
                Log("modifier_hold while_held=" + held2 + " events=" + string.Join(",", events) + " hook=" + m.HookActive);
                if (held2 != "HoldToTalk+" || string.Join(",", events) != "HoldToTalk+,HoldToTalk-" || m.HookActive) return 40;
            }
            return 0;
        }

        private async Task<int> SettingsTest()
        {
            using (var mgr = new ShortcutManager())   // Current: Settings shows registration problems from it
            {
                AppStorage.SaveShortcuts(ShortcutSet.Defaults());
                mgr.Apply(AppStorage.LoadShortcuts());
                var w = new MainWindow(MainWindow.Page.Settings);
                w.Show();
                w.BringUp();
                await Task.Delay(800);
                var rows = w.Settings.Shortcuts;
                for (Control c = rows; c != null; c = c.Parent)
                    if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(rows.Parent ?? rows); break; }
                await Task.Delay(400);

                // Record a new combo through the capture box: hold Space, press B → "Space+B" for hold to talk.
                var holdBox = rows.BoxFor(ShortcutAction.HoldToTalk);
                var change = rows.Controls.OfType<PillButton>().First(b => b.Text == "Change" && rows.GetRow(b) == rows.GetRow(holdBox));
                change.PerformClick();
                await Task.Delay(200);
                bool capturing = holdBox.Capturing;
                holdBox.KeyDownVk(0x20); holdBox.KeyDownVk(0x42); holdBox.KeyUpVk(0x42); holdBox.KeyUpVk(0x20);
                await Task.Delay(300);
                var saved = AppStorage.LoadShortcuts();
                bool recorded = saved.Hold.ToString() == "Space+B" && saved.Tap.ToString() == "Ctrl+Alt+V" && saved.Read.ToString() == "Ctrl+Alt+R";
                await Task.Delay(300);
                w.SavePng(Path.Combine(_out, "settings-shortcuts.png"));
                Log("settings capturing=" + capturing + " saved=" + saved.ToText().Replace("\r\n", ";") + " hook_now=" + mgr.HookActive + " recorded_ok=" + recorded);

                // A risky combo is warned about but kept; a clash moves the combo.
                var set = AppStorage.LoadShortcuts();
                Shortcut.TryParse("Ctrl+C", out var copy); set.Read = copy;
                AppStorage.SaveShortcuts(set);
                rows.Refresh2();
                await Task.Delay(300);
                var readNote = rows.NoteFor(ShortcutAction.SelectToSpeak);
                bool warned = readNote.Contains("⚠") && AppStorage.LoadShortcuts().Read.ToString() == "Ctrl+C";
                w.SavePng(Path.Combine(_out, "settings-shortcuts-warning.png"));
                // Clear.
                var clear = rows.Controls.OfType<PillButton>().First(b => b.Text == "Clear" && rows.GetRow(b) == rows.GetRow(rows.BoxFor(ShortcutAction.SelectToSpeak)));
                clear.PerformClick();
                await Task.Delay(200);
                bool cleared = AppStorage.LoadShortcuts().Read.IsEmpty;
                bool fileOk = File.ReadAllText(Path.Combine(AppStorage.Folder, "shortcuts.txt")).Contains("hold=Space+B");
                Log("settings warned=" + warned + " note=\"" + readNote + "\" cleared=" + cleared + " file_ok=" + fileOk);
                w.Close();
                w.Dispose();
                if (!capturing || !recorded || !warned || !cleared || !fileOk) return 42;
            }
            return 0;
        }
    }
}
