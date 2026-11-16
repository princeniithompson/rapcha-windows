using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v14 (--selftest-v14 outDir), with real injected mouse input (SendInput) on the real
    /// select-to-speak overlay showing a rendered test page (title, a 5-line paragraph, a second paragraph, a sidebar
    /// column level with the paragraph). Runs in its own process so the first overlay is a true cold start (OCR
    /// engine and voice never used before).
    /// Checks: (B) a thin drag down the left edge of the paragraph reads every paragraph line whole, nothing else, no
    /// repeated words; (C) a touchpad-like tap (40 ms click, 2 px jitter) reads the whole paragraph from its start;
    /// (A) click, move, pause for 1.2 s (nothing is read), click again: reads the box; the green box never disappears
    /// in any captured frame of the transition (frame check); Esc cancels corner 1 without closing; latencies from
    /// release to first audio, cold and warm, for each gesture (and an early release before the index is ready, and
    /// a OneCore voice when one is installed). Only counts and timings are logged, never words.
    /// Exit: 0 ok, 70 OCR unavailable, 71 strip not full lines, 72 tap paragraph, 73 two-corner mode, 74 flicker,
    /// 75 Esc, 76 latency, 77 repeated words, 1 crash.
    /// </summary>
    internal sealed class V14SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "voice.txt", "voice-speed.txt" };
        private readonly byte[][] _backup;
        private readonly List<string> _table = new List<string>();
        private readonly List<string> _notes = new List<string>();

        public V14SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v14: " + s);

        private async void Run()
        {
            int code;
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e.GetType().Name + " " + e.StackTrace); code = 1; }
            for (int i = 0; i < _files.Length; i++)
            {
                var p = Path.Combine(AppStorage.Folder, _files[i]);
                try { if (_backup[i] != null) File.WriteAllBytes(p, _backup[i]); else if (File.Exists(p)) File.Delete(p); } catch { }
            }
            try
            {
                File.WriteAllLines(Path.Combine(_out, "latency.txt"),
                    new[] { "| scenario | voice | index used | release to words (ms) | release to first audio (ms) |", "|---|---|---|---|---|" }
                    .Concat(_table).Concat(new[] { "" }).Concat(_notes));
            }
            catch { }
            Log("exit=" + code);
            Environment.ExitCode = code;
            ExitThread();
        }

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms) { if (ok()) return true; await Task.Delay(10); }
            return ok();
        }

        // ---------------- Injected input ----------------

        private static Native.INPUT M(Point p, uint flags)
        {
            var vs = SystemInformation.VirtualScreen;
            int ax = (int)Math.Round((p.X - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1));
            int ay = (int)Math.Round((p.Y - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1));
            return new Native.INPUT { type = 0, u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK } } };
        }

        private static void Send(params Native.INPUT[] i) => Native.SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(Native.INPUT)));
        private static void MoveTo(Point p) => Send(M(p, Native.MOUSEEVENTF_MOVE));
        private static void Down(Point p) => Send(M(p, Native.MOUSEEVENTF_MOVE), M(p, Native.MOUSEEVENTF_LEFTDOWN));
        private static void Up(Point p) => Send(M(p, Native.MOUSEEVENTF_MOVE), M(p, Native.MOUSEEVENTF_LEFTUP));

        private static void Key(ushort vk)
        {
            var d = new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk } } };
            var u = new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = Native.KEYEVENTF_KEYUP } } };
            Send(d, u);
        }

        /// <summary>A pointer glide in ~16 ms steps (like a touchpad), optionally capturing frames on the way.</summary>
        private static async Task Glide(Point a, Point b, int ms, Action<int> frame = null)
        {
            int steps = Math.Max(2, ms / 16);
            for (int i = 1; i <= steps; i++)
            {
                MoveTo(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps));
                await Task.Delay(16);
                frame?.Invoke(i);
            }
        }

        // ---------------- The test page ----------------

        private sealed class GtLine { public string Group, Text; public RectangleF Box; }
        private readonly List<GtLine> _gt = new List<GtLine>();

        private static readonly string[] Paragraph =
        {
            "Select to Speak should read the text you pick aloud, quickly and",
            "smoothly. Drag a thin strip down the left edge of this paragraph",
            "and every line must be read from its first word to its last word,",
            "with nothing cut at the edge of the box and no word said twice.",
            "This is the fifth and last line of the first paragraph.",
        };

        private Bitmap RenderPage(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var title = new Font("Segoe UI Semibold", 22f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var body = new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var ink = new SolidBrush(Color.FromArgb(0x20, 0x21, 0x24)))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.White);
                void Line(string group, string text, Font f, float x, float y)
                {
                    g.DrawString(text, f, ink, x, y, StringFormat.GenericTypographic);
                    var size = g.MeasureString(text, f, 10000, StringFormat.GenericTypographic);
                    _gt.Add(new GtLine { Group = group, Text = text, Box = new RectangleF(x, y, size.Width, size.Height) });
                }
                Line("title", "Reading test page for select to speak", title, 80, 90);
                for (int i = 0; i < Paragraph.Length; i++) Line("para", Paragraph[i], body, 80, 160 + i * 24);
                Line("next", "A second paragraph starts here after a clear gap and must not be read.", body, 80, 320);
                Line("next", "It has a second line too, so it is a real paragraph.", body, 80, 344);
                Line("side", "Sidebar column level with the paragraph.", body, 820, 160);
                Line("side", "It must stay out of a strip on the left.", body, 820, 184);
            }
            return bmp;
        }

        private IEnumerable<GtLine> G(string group) => _gt.Where(l => l.Group == group);

        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static List<string> Tokens(IEnumerable<GtLine> ls) => ls.SelectMany(l => l.Text.Split(' ')).Select(Norm).Where(t => t.Length > 0).ToList();

        /// <summary>Share of the expected words found in order in the read words.</summary>
        private static double Recall(List<string> gt, IEnumerable<string> read)
        {
            string r = string.Concat(read.Select(Norm));
            int pos = 0, hit = 0;
            foreach (var t in gt)
            {
                int i = r.IndexOf(t, pos, StringComparison.Ordinal);
                if (i >= 0 && (hit == 0 || i - pos <= 30)) { hit++; pos = i + t.Length; }
            }
            return gt.Count == 0 ? 1 : (double)hit / gt.Count;
        }

        /// <summary>Words said twice in a row that the page does not repeat.</summary>
        private static int Repeats(List<string> read, List<string> gt)
        {
            int n = 0;
            for (int i = 1; i < read.Count; i++)
            {
                if (read[i].Length == 0 || read[i] != read[i - 1]) continue;
                bool real = false;
                for (int j = 1; j < gt.Count; j++) if (gt[j] == read[i] && gt[j - 1] == read[i]) real = true;
                if (!real) n++;
            }
            return n;
        }

        private static string Pct(double v) => ((int)Math.Round(v * 100)) + "%";

        // ---------------- Steps ----------------

        private Rectangle _screen;
        private int _dpi;
        private Bitmap _page;

        private ScreenOverlay Open()
        {
            var o = new ScreenOverlay(_screen, _page, _dpi, silentVoice: true);
            o.Show();
            o.Activate();
            Native.SetForegroundWindow(o.Handle);
            o.StartIndex();
            return o;
        }

        private static async Task Close(ScreenOverlay o)
        {
            o.Close();
            o.Dispose();
            await Task.Delay(300);
        }

        private void Row(string scenario, ScreenOverlay o)
        {
            string voice = o.Reader is OneCoreReader ? "OneCore" : "SAPI";
            _table.Add("| " + scenario + " | " + voice + " | " + o.LastIndexState + " | " + o.WordsReadyMs + " | " + o.FirstWordMs + " |");
            Log("latency " + scenario + " voice=" + voice + " index=" + o.LastIndexState + " words_ms=" + o.WordsReadyMs + " audio_ms=" + o.FirstWordMs);
        }

        private int _mark;
        /// <summary>Remembers how many selections the overlay has started (call before a gesture).</summary>
        private void Mark(ScreenOverlay o) => _mark = o.ReadCount;
        /// <summary>Waits for a selection started after <see cref="Mark"/> to reach its first audio.</summary>
        private async Task<bool> WaitRead(ScreenOverlay o, int ms = 8000) => await WaitFor(() => o.ReadCount > _mark && o.FirstWordMs >= 0, ms);

        private static void Stop(ScreenOverlay o) { o.Reader?.Stop(); }

        private void Shot(string name)
        {
            try { using (var b = V11SelfTest.Capture(_screen)) b.Save(Path.Combine(_out, name), ImageFormat.Png); } catch { }
        }

        private async Task<int> Steps()
        {
            _screen = Screen.PrimaryScreen.Bounds;
            _dpi = Native.DpiAt(_screen.X + _screen.Width / 2, _screen.Y + _screen.Height / 2);
            Log("screen " + _screen.Width + "x" + _screen.Height + " dpi=" + _dpi);
            Voices.SavedId = "";   // the Windows default (SAPI) voice first
            VoiceSettings.Speed = 1.0;
            _page = RenderPage(_screen.Width, _screen.Height);
            _page.Save(Path.Combine(_out, "v14-test-page.png"), ImageFormat.Png);

            var para = G("para").ToList();
            var paraTokens = Tokens(para);
            var rp = para.Select(l => l.Box).Aggregate(RectangleF.Union);
            // The thin strip: 14 px wide, over the first letters of the paragraph's lines only.
            var stripA = new Point((int)rp.X - 6, (int)rp.Y + 2);
            var stripB = new Point((int)rp.X + 8, (int)rp.Bottom - 4);
            var tapWord = new Point((int)(rp.X + rp.Width * 0.55), (int)(para[2].Box.Y + para[2].Box.Height / 2)); // middle of line 3

            // ===== 1. Cold: first overlay of the process, drag (mode B) 1.2 s after opening =====
            var o = Open();
            try
            {
                await Task.Delay(1200);   // the user positions the pointer
                Log("cold: fast_ready_ms=" + o.FastPassMs + " index_ready=" + o.IndexReady);
                if (o.Ocr != null && o.Ocr.Problem.Length > 0) { Log("OCR problem"); return 70; }
                Mark(o); MoveTo(stripA); await Task.Delay(40);
                Down(stripA); await Task.Delay(30);
                await Glide(stripA, stripB, 250);
                Up(stripB);
                if (!await WaitRead(o)) { Log("cold drag: nothing read (" + o.LastReadText.Length + " chars)"); return o.Ocr == null || o.Ocr.Page.WordCount == 0 ? 70 : 71; }
                Row("B drag, cold start (thin strip)", o);
                var read = o.LastReadWords.Select(w => Norm(w.Text)).ToList();
                double rec = Recall(paraTokens, read);
                bool noTitle = !o.LastReadWords.Any(w => w.Box.CenterY < rp.Y - 8);
                bool noOthers = !o.LastReadWords.Any(w => w.Box.CenterY > rp.Bottom + 8 || w.Box.X > 700);
                int rep = Repeats(read, paraTokens);
                bool lastWordsIn = read.Contains("quickly") && read.Contains("paragraph") && read.Contains("twice");
                await Task.Delay(200);
                Shot("v14-strip-full-lines.png");
                Log("strip: recall=" + Pct(rec) + " words=" + read.Count + "/" + paraTokens.Count + " line_ends_read=" + lastWordsIn + " no_title=" + noTitle + " no_other_blocks=" + noOthers + " repeats=" + rep);
                _notes.Add("Thin left strip (mode B): recall " + Pct(rec) + ", words " + read.Count + " of " + paraTokens.Count + ", line ends read: " + lastWordsIn + ", title/sidebar/next paragraph read: " + !(noTitle && noOthers) + ", repeated words: " + rep);
                Stop(o);
                if (rep > 0) return 77;
                if (rec < 0.9 || !lastWordsIn || !noTitle || !noOthers) return 71;

                // ===== 2. Tap (mode C), touchpad-like: 40 ms click with 2 px jitter, then still =====
                await Task.Delay(300);
                Mark(o); MoveTo(tapWord); await Task.Delay(60);
                Down(tapWord); await Task.Delay(20);
                MoveTo(new Point(tapWord.X + 2, tapWord.Y + 1)); await Task.Delay(20);
                Up(new Point(tapWord.X + 2, tapWord.Y + 1));
                if (!await WaitRead(o)) { Log("tap: nothing read"); return 72; }
                Row("C tap (incl. 280 ms tap window)", o);
                var tread = o.LastReadWords.Select(w => Norm(w.Text)).ToList();
                double trec = Recall(paraTokens, tread);
                bool fromStart = tread.Count > 0 && tread[0] == paraTokens[0];
                bool tOnly = !o.LastReadWords.Any(w => w.Box.CenterY < rp.Y - 8 || w.Box.CenterY > rp.Bottom + 8 || w.Box.X > 700);
                await Task.Delay(150);
                Shot("v14-tap-paragraph.png");
                Log("tap: recall=" + Pct(trec) + " from_paragraph_start=" + fromStart + " only_paragraph=" + tOnly + " prepared=" + o.TapsPrepared + " repeats=" + Repeats(tread, paraTokens));
                _notes.Add("Tap (mode C) on line 3: paragraph recall " + Pct(trec) + ", starts at the paragraph's first word: " + fromStart + ", only that paragraph: " + tOnly);
                Stop(o);
                if (trec < 0.9 || !fromStart || !tOnly) return 72;

                // ===== 3. Two corners (mode A): click, move (no button), pause 1.2 s, click =====
                await Task.Delay(300);
                var c1 = new Point((int)rp.X - 20, (int)rp.Y - 6);
                var c2 = new Point((int)rp.Right + 10, (int)para[1].Box.Bottom + 2);
                var area = Rectangle.FromLTRB(c1.X - 12, c1.Y - 12, c2.X + 12, c2.Y + 12);
                var frames = new List<int>();
                Action<int> grab = _ => { using (var b = V11SelfTest.Capture(area)) frames.Add(GreenPixels(b)); };
                Mark(o); MoveTo(c1); await Task.Delay(60);
                Down(c1); await Task.Delay(35); Up(c1);
                await Task.Delay(30);
                await Glide(c1, new Point(c1.X + 40, c1.Y + 20), 80);           // > threshold within the tap window
                bool cornerSet = o.GestureState == BoxSelector.State.CornerSet;
                string modeText = o.ModeText();
                int framesBefore = frames.Count;
                await Glide(new Point(c1.X + 40, c1.Y + 20), c2, 600, grab);   // the live box follows, no button held
                Shot("v14-corner1-set.png");
                for (int i = 0; i < 12; i++) { await Task.Delay(100); grab(0); } // a long pause: nothing may be read
                bool silentWhilePaused = o.ReadCount == _mark && o.GestureState == BoxSelector.State.CornerSet;
                Down(c2); grab(0); await Task.Delay(35); Up(c2);
                for (int i = 0; i < 16; i++) { await Task.Delay(25); grab(0); }  // through the transition into reading
                bool readA = await WaitRead(o);
                if (readA) Row("A two corners (second click)", o);
                var aread = o.LastReadWords.Select(w => Norm(w.Text)).ToList();
                var first2 = Tokens(para.Take(2));
                double arec = Recall(first2, aread);
                bool aOnly = !aread.Contains("fifth") && !aread.Contains("selecttospeak");
                int blank = frames.Count(f => f < 40);
                Log("corners: corner_set=" + cornerSet + " pill=\"" + modeText + "\" silent_while_paused=" + silentWhilePaused + " read=" + readA +
                    " recall=" + Pct(arec) + " frames=" + frames.Count + " frames_without_green=" + blank + " min_green=" + (frames.Count > 0 ? frames.Min() : -1));
                _notes.Add("Two corners (mode A): corner 1 set after moving: " + cornerSet + ", pill says \"" + modeText + "\", nothing read during a 1.2 s pause: " + silentWhilePaused +
                           ", read after the second click: " + readA + " (recall " + Pct(arec) + ")");
                _notes.Add("Flicker check: " + frames.Count + " frames captured from corner 1 through the start of reading; frames without the green box: " + blank + " (min green pixels " + (frames.Count > 0 ? frames.Min() : -1) + ")");
                Stop(o);
                if (!cornerSet || !modeText.StartsWith("Corner 1 set") || !silentWhilePaused || !readA || arec < 0.9) return 73;
                if (blank > 0) return 74;

                // ===== 4. Esc cancels corner 1 (and does not close the overlay) =====
                await Task.Delay(300);
                Mark(o); MoveTo(c1); await Task.Delay(60);
                Down(c1); await Task.Delay(35); Up(c1);
                await Task.Delay(30);
                await Glide(c1, new Point(c1.X + 60, c1.Y + 40), 80);
                bool set2 = o.GestureState == BoxSelector.State.CornerSet;
                Key(Native.VK_ESCAPE);
                await Task.Delay(200);
                bool cancelled = o.GestureState == BoxSelector.State.Idle && !o.IsDisposed && o.Visible && o.ReadCount == _mark;
                Log("esc: corner_set=" + set2 + " cancelled_overlay_open=" + cancelled);
                _notes.Add("Esc after corner 1: cancelled, overlay still open: " + cancelled);
                if (!set2 || !cancelled) return 75;

                // ===== 5. Warm, same overlay: drag and tap again =====
                await Task.Delay(300);
                Mark(o); MoveTo(stripA); await Task.Delay(40);
                Down(stripA); await Task.Delay(30);
                await Glide(stripA, stripB, 200);
                Up(stripB);
                if (await WaitRead(o)) Row("B drag, warm", o);
                int warmDrag = o.FirstWordMs;
                Stop(o);
                await Task.Delay(300);
                Mark(o); MoveTo(tapWord); await Task.Delay(60);
                Down(tapWord); await Task.Delay(40); Up(tapWord);
                if (await WaitRead(o)) Row("C tap, warm (incl. 280 ms tap window)", o);
                int warmTap = o.FirstWordMs;
                Stop(o);
                _notes.Add("Index: first look ready " + o.FastPassMs + " ms after opening; all passes merged " + o.IndexMs + " ms after opening (" + (o.Ocr?.Passes.Count ?? 0) + " passes, low priority)");
                await Close(o);
                o = null;

                // ===== 6. A new overlay (warm voice and OCR engine), drag at 1.2 s; then one released early =====
                o = Open();
                await Task.Delay(1200);
                Mark(o); MoveTo(stripA); await Task.Delay(40);
                Down(stripA); await Task.Delay(30);
                await Glide(stripA, stripB, 200);
                Up(stripB);
                if (await WaitRead(o)) Row("B drag, new overlay (warm)", o);
                int warmNew = o.FirstWordMs;
                Stop(o);
                await Close(o);
                o = Open();
                await Task.Delay(120);   // released before the first look is back
                Mark(o);
                Down(stripA); await Task.Delay(20);
                await Glide(stripA, stripB, 100);
                Up(stripB);
                if (await WaitRead(o, 10000)) Row("B drag released 0.25 s after opening", o);
                int early = o.FirstWordMs;
                Stop(o);
                await Close(o);
                o = null;

                // ===== 7. A OneCore voice, when installed: cold (first use) and warm =====
                var onecore = Voices.All().FirstOrDefault(v => !v.Desktop);
                int oneWarm = -1;
                if (onecore != null)
                {
                    Voices.SavedId = onecore.Id;
                    Voices.ResetShared();
                    o = Open();
                    await Task.Delay(1200);
                    Mark(o); MoveTo(stripA); await Task.Delay(40);
                    Down(stripA); await Task.Delay(30);
                    await Glide(stripA, stripB, 200);
                    Up(stripB);
                    if (await WaitRead(o)) Row("B drag, OneCore first use", o);
                    Stop(o);
                    await Task.Delay(300);
                    Mark(o);
                    Down(stripA); await Task.Delay(30);
                    await Glide(stripA, stripB, 200);
                    Up(stripB);
                    if (await WaitRead(o)) Row("B drag, OneCore warm", o);
                    oneWarm = o.FirstWordMs;
                    Stop(o);
                    await Task.Delay(300);
                    Mark(o); MoveTo(tapWord); await Task.Delay(60);
                    Down(tapWord); await Task.Delay(40); Up(tapWord);
                    if (await WaitRead(o)) Row("C tap, OneCore warm (incl. 280 ms tap window)", o);
                    Stop(o);
                    await Close(o);
                    o = null;
                    Voices.SavedId = "";
                    Voices.ResetShared();
                }
                _notes.Add("Latency is measured from the release (for a tap: the click's release, so it includes the 280 ms wait that tells a tap from the start of two corners) to the first audio: SAPI's first word-progress event, or the first OneCore audio buffer handed to the sound device. CI voices write to memory (no speaker on the runner); synthesis time is included.");

                bool fast = warmDrag >= 0 && warmDrag < 600 && warmNew >= 0 && warmNew < 600 && warmTap >= 0 && warmTap < 900 && early >= 0 && early < 4000 &&
                            (onecore == null || (oneWarm >= 0 && oneWarm < 900));
                Log("latency ok=" + fast + " warm_drag=" + warmDrag + " warm_new=" + warmNew + " warm_tap=" + warmTap + " early=" + early + " onecore_warm=" + oneWarm);
                if (!fast) return 76;
                return 0;
            }
            finally
            {
                if (o != null && !o.IsDisposed) { o.Close(); o.Dispose(); }
            }
        }

        /// <summary>Bright green pixels (the live box, the snapped outlines or the word highlight).</summary>
        private static int GreenPixels(Bitmap b)
        {
            int n = 0;
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[d.Stride * b.Height];
                Marshal.Copy(d.Scan0, px, 0, px.Length);
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        int i = y * d.Stride + x * 4;
                        int bl = px[i], g = px[i + 1], r = px[i + 2];
                        if (g > 170 && g - r > 70 && g - bl > 40) n++;
                    }
            }
            finally { b.UnlockBits(d); }
            return n;
        }
    }
}
