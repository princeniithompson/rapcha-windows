using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    /// CI self-test for v15 (--selftest-v15 outDir) on the real select-to-speak overlay with injected input:
    /// Previous / Next by paragraph and list item (Right, Page Down and the Next button; Left, Page Up and the
    /// Previous button), the 1.5 s restart rule, the highlight on the segment's first word, paused moves, Finished
    /// on the last segment, a tap going on into the neighbouring blocks, press-to-first-audio for SAPI and OneCore
    /// (target under 300 ms); number badges outside the blocks, never on text, outlines or each other, in reading
    /// order, and the Settings toggle (off: no badges, same order and Previous / Next); the navigation keys are
    /// never taken when nothing is being read (the app's keyboard hook lets them through); the app icon's bigger
    /// ring. Only counts and timings are logged, never words.
    /// Exit: 0 ok, 80 OCR unavailable, 81 badges, 82 Previous / Next order or highlight, 83 paused move,
    /// 84 Finished, 85 tap neighbours, 86 badges off changed reading, 87 keys taken while inactive, 88 latency,
    /// 89 settings toggle, 90 icon, 1 crash.
    /// </summary>
    internal sealed class V15SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "voice.txt", "voice-speed.txt", "number-badges.txt" };
        private readonly byte[][] _backup;
        private readonly List<string> _table = new List<string>();
        private readonly List<string> _notes = new List<string>();

        public V15SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v15: " + s);

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
            VoiceSettings.ReloadBadges();
            try
            {
                File.WriteAllLines(Path.Combine(_out, "v15-results.txt"),
                    new[] { "| press | voice | went to | press to first audio (ms) |", "|---|---|---|---|" }
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

        private const ushort VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_LEFT = 0x25, VK_RIGHT = 0x27;

        private static void Key(ushort vk)
        {
            uint ext = vk >= 0x21 && vk <= 0x28 ? Native.KEYEVENTF_EXTENDEDKEY : 0;
            var d = new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = ext } } };
            var u = new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = ext | Native.KEYEVENTF_KEYUP } } };
            Send(d, u);
        }

        private static async Task Click(Point p) { MoveTo(p); await Task.Delay(40); Down(p); await Task.Delay(40); Up(p); }

        private static async Task Drag(Point a, Point b)
        {
            MoveTo(a); await Task.Delay(60);
            Down(a); await Task.Delay(40);
            int steps = 14;
            for (int i = 1; i <= steps; i++) { MoveTo(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps)); await Task.Delay(16); }
            Up(b);
        }

        // ---------------- The test page ----------------

        private sealed class GtLine { public string Group, Text; public RectangleF Box; }
        private readonly List<GtLine> _gt = new List<GtLine>();

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
                Line("title", "Previous and Next test page", title, 80, 50);
                Line("paraA", "Previous and Next jump between paragraphs and list items.", body, 80, 110);
                Line("paraA", "This first paragraph has two lines so it is one block.", body, 80, 134);
                Line("item1", "1. The first item of a short numbered list.", body, 80, 190);
                Line("item2", "2. The second item comes after the first one.", body, 80, 230);
                Line("paraB", "The last paragraph ends the reading when Next is", body, 80, 290);
                Line("paraB", "pressed again on it, and the bar then says Finished.", body, 80, 314);
                Line("left", "Left column text sits close", body, 80, 410);
                Line("left", "to the right column here.", body, 80, 434);
                Line("right", "Right column text beside it", body, 300, 410);
                Line("right", "keeps its own outline too.", body, 300, 434);
                Line("edge", "Edge text starts at the very left edge of the screen.", body, 4, 520);
                Line("edge", "Its badge has no room on the left and must move.", body, 4, 544);
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

        // ---------------- Overlay helpers ----------------

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

        private void Shot(string name, Rectangle? area = null, int zoom = 1)
        {
            try
            {
                using (var b = V11SelfTest.Capture(area ?? _screen))
                {
                    if (zoom <= 1) { b.Save(Path.Combine(_out, name), ImageFormat.Png); return; }
                    using (var z = new Bitmap(b.Width * zoom, b.Height * zoom))
                    using (var g = Graphics.FromImage(z))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(b, 0, 0, z.Width, z.Height);
                        z.Save(Path.Combine(_out, name), ImageFormat.Png);
                    }
                }
            }
            catch { }
        }

        private static Point P(Box b) => new Point((int)Math.Round(b.CenterX), (int)Math.Round(b.CenterY));
        private static Point P(Rectangle r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        private int _readMark;
        private async Task<bool> ReadBox(ScreenOverlay o, Point a, Point b)
        {
            _readMark = o.ReadCount;
            await Drag(a, b);
            return await WaitFor(() => o.ReadCount > _readMark && o.FirstWordMs >= 0, 8000);
        }

        private string Voice(ScreenOverlay o) => o.Reader is OneCoreReader ? "OneCore" : "SAPI";

        /// <summary>Runs one Previous / Next press and waits for where it went (its first audio, or the move when paused).</summary>
        private async Task<bool> Press(ScreenOverlay o, string label, Func<Task> press, bool expectAudio = true)
        {
            int n = o.NavCount;
            await press();
            if (!await WaitFor(() => o.NavCount > n, 3000)) { Log("press " + label + ": not handled"); return false; }
            if (!expectAudio) return true;
            bool ok = await WaitFor(() => o.NavFirstAudioMs >= 0, 3000);
            _table.Add("| " + label + " | " + Voice(o) + " | " + o.LastNav + " segment " + o.Nav.Current + " | " + o.NavFirstAudioMs + " |");
            Log("press " + label + " voice=" + Voice(o) + " went=" + o.LastNav + " segment=" + o.Nav.Current + " press_to_audio_ms=" + o.NavFirstAudioMs);
            if (ok) _latencies.Add(o.NavFirstAudioMs);
            return ok;
        }

        private readonly List<int> _latencies = new List<int>();

        /// <summary>At the segment's first word: the voice and the green highlight.</summary>
        private static bool AtSegmentStart(ScreenOverlay o, int seg)
        {
            if (seg < 0 || seg >= o.Nav.List.Count || o.Spoken == null) return false;
            int first = o.Nav.List[seg].First;
            var want = o.Spoken.HighlightFor(first);
            var hl = o.HighlightTarget;
            bool voice = o.CurrentSpokenWord == first;
            bool box = Math.Abs(hl.X - want.X) < 1 && Math.Abs(hl.Y - want.Y) < 1;
            return voice && box;
        }

        private ScreenOverlay.Control Back => ScreenOverlay.Control.Back;
        private ScreenOverlay.Control Fwd => ScreenOverlay.Control.Forward;

        // ---------------- Badge geometry ----------------

        private bool BadgesClean(ScreenOverlay o, string name, int wantBadges)
        {
            var outlines = o.PreviewOutlines;
            var badges = o.PreviewBadges;
            var text = o.Ocr.Page.Lines.SelectMany(l => l.Words).Select(w => w.Box).ToList();
            int onText = badges.Count(b => BadgeLayout.Hits(b, text, 0));
            int onOutline = badges.Count(b => BadgeLayout.Hits(b, outlines, 0));
            int badgeBadge = 0, boxBox = 0;
            for (int i = 0; i < badges.Count; i++) for (int j = i + 1; j < badges.Count; j++) if (BadgeLayout.Overlap(badges[i], badges[j])) badgeBadge++;
            for (int i = 0; i < outlines.Count; i++) for (int j = i + 1; j < outlines.Count; j++) if (BadgeLayout.Overlap(outlines[i], outlines[j])) boxBox++;
            // Reading order: badge n is closest to outline n.
            int misordered = 0;
            for (int i = 0; i < badges.Count; i++)
            {
                double Dist(Box a, Box b) { double dx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right)), dy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom)); return dx * dx + dy * dy; }
                int nearest = Enumerable.Range(0, outlines.Count).OrderBy(k => Dist(badges[i], outlines[k])).First();
                if (nearest != i) misordered++;
            }
            double size = badges.Count > 0 ? badges[0].W * 96.0 / _dpi : 0;
            Log("badges " + name + ": blocks=" + outlines.Count + " badges=" + badges.Count + " size_px_at_100=" + size.ToString("0.0") + " on_text=" + onText + " on_outline=" + onOutline +
                " badge_overlaps=" + badgeBadge + " outline_overlaps=" + boxBox + " misordered=" + misordered);
            _notes.Add("Badges (" + name + "): " + outlines.Count + " blocks, " + badges.Count + " badges of " + size.ToString("0") + " px at 100%; on text " + onText +
                       ", on an outline " + onOutline + ", badge on badge " + badgeBadge + ", outline on outline " + boxBox + ", out of reading order " + misordered);
            return badges.Count == wantBadges && onText == 0 && onOutline == 0 && badgeBadge == 0 && boxBox == 0 && misordered == 0 && size >= 13 && size <= 17;
        }

        // ---------------- Steps ----------------

        private async Task<int> Steps()
        {
            SpeechReader.PaceSilent = true;   // readings last as long as out loud
            Voices.SavedId = "";
            VoiceSettings.Speed = 1.0;
            VoiceSettings.ShowBadges = true;
            Voices.ResetShared();
            OcrService.WarmUp();
            _screen = Screen.PrimaryScreen.Bounds;
            _dpi = Native.DpiAt(_screen.X + _screen.Width / 2, _screen.Y + _screen.Height / 2);
            Log("screen " + _screen.Width + "x" + _screen.Height + " dpi=" + _dpi);
            _page = RenderPage(_screen.Width, _screen.Height);
            _page.Save(Path.Combine(_out, "v15-test-page.png"), ImageFormat.Png);

            int icon = IconTest();
            if (icon != 0) return icon;

            var o = Open();
            if (!await WaitFor(() => o.Ocr != null, 15000) || o.Ocr.Problem.Length > 0) { await Close(o); return 80; }
            await WaitFor(() => o.IndexReady, 30000);
            await Task.Delay(400);

            // ===== Badges on: five blocks (heading, paragraph, two list items, paragraph) =====
            var mainA = new Point(60, 38); var mainB = new Point(760, 352);
            if (!await ReadBox(o, mainA, mainB)) { Log("main box not read"); await Close(o); return 82; }
            await Task.Delay(120);
            Shot("v15-badges-on.png");
            bool cleanMain = BadgesClean(o, "five blocks", 5);
            var onOrder = o.LastReadWords.Select(w => Norm(w.Text)).ToList();
            var onOutlines = o.PreviewOutlines;
            var onBadges = o.PreviewBadges;
            int segCount = o.Nav.List.Count;
            Log("segments=" + segCount + " words=" + onOrder.Count);
            _notes.Add("Box over the heading, paragraph, two list items and last paragraph: " + segCount + " segments (want 5)");
            if (segCount != 5) { await Close(o); return 82; }

            // ===== Previous / Next with SAPI =====
            var buttons = Rectangle.Union(o.ControlRect(Back), o.ControlRect(ScreenOverlay.Control.Stop));
            buttons.Inflate(Si(30), Si(14));
            buttons.Width += Si(120);
            Shot("v15-reader-buttons.png", buttons, 2);
            bool order = true;
            order &= await Press(o, "Next (Right key)", () => { Key(VK_RIGHT); return Task.CompletedTask; }) && o.Nav.Current == 1 && AtSegmentStart(o, 1);
            order &= await Press(o, "Next (Page Down)", () => { Key(VK_NEXT); return Task.CompletedTask; }) && o.Nav.Current == 2 && AtSegmentStart(o, 2);
            order &= await Press(o, "Next (button)", () => Click(P(o.ControlRect(Fwd)))) && o.Nav.Current == 3 && AtSegmentStart(o, 3);
            Log("next order ok=" + order);
            await Task.Delay(1700);   // past the 1.5 s window: Previous restarts the current item
            bool restart = await Press(o, "Previous after 1.7 s (Page Up)", () => { Key(VK_PRIOR); return Task.CompletedTask; }) && o.Nav.Current == 3 && AtSegmentStart(o, 3);
            await Task.Delay(250);
            bool back1 = await Press(o, "Previous within 1.5 s (Left key)", () => { Key(VK_LEFT); return Task.CompletedTask; }) && o.Nav.Current == 2 && AtSegmentStart(o, 2);
            await Task.Delay(250);
            bool back2 = await Press(o, "Previous within 1.5 s (button)", () => Click(P(o.ControlRect(Back)))) && o.Nav.Current == 1 && AtSegmentStart(o, 1);
            Log("previous restart=" + restart + " back_within_window=" + back1 + " back_button=" + back2);
            _notes.Add("Previous / Next (SAPI): Next by Right, Page Down and the button went 1, 2, 3 with the highlight on each first word: " + order +
                       "; Previous after 1.7 s restarted the item: " + restart + "; Previous within 1.5 s went back (key, then button): " + (back1 && back2));
            if (!order || !restart || !back1 || !back2) { await Close(o); return 82; }

            // Paused: Next moves the highlight and waits.
            Key(VK_SPACE);
            await WaitFor(() => o.Reader.Status == ReadState.Paused, 2000);
            int seg = o.Nav.Current;
            bool pausedMove = await Press(o, "Next while paused", () => { Key(VK_RIGHT); return Task.CompletedTask; }, expectAudio: false);
            await Task.Delay(600);
            pausedMove &= o.Reader.Status == ReadState.Paused && o.Nav.Current == seg + 1 && AtSegmentStart(o, seg + 1);
            Shot("v15-paused-next.png");
            Key(VK_SPACE);
            bool resumed = await WaitFor(() => o.Reader.Status == ReadState.Playing, 2000) && await WaitFor(() => o.CurrentSpokenWord >= o.Nav.List[seg + 1].First, 2000);
            Log("paused next: moved_and_waited=" + pausedMove + " resumed_from_there=" + resumed);
            _notes.Add("Paused: Next moved the highlight to the next item's first word and stayed paused: " + pausedMove + "; Play went on from there: " + resumed);
            if (!pausedMove || !resumed) { await Close(o); return 83; }

            // Next to the last segment, then Next again: Finished, no close.
            while (o.Nav.Current < segCount - 1)
                if (!await Press(o, "Next to item " + (o.Nav.Current + 2), () => { Key(VK_RIGHT); return Task.CompletedTask; })) break;
            await Press(o, "Next on the last item", () => { Key(VK_RIGHT); return Task.CompletedTask; }, expectAudio: false);
            await Task.Delay(300);
            bool finished = o.ModeText() == "Finished" && o.Reader.Status == ReadState.Idle && o.Visible && !o.IsDisposed;
            int navBefore = o.NavCount;
            Key(VK_NEXT); Key(VK_PRIOR); Key(VK_LEFT); Key(VK_RIGHT);
            await Task.Delay(300);
            bool inactive = o.NavCount == navBefore;
            Shot("v15-finished.png");
            Log("finished: status_finished=" + finished + " keys_ignored_after=" + inactive);
            _notes.Add("Next on the last item: the bar says Finished, the overlay stays open: " + finished + "; afterwards the keys do nothing: " + inactive);
            if (!finished) { await Close(o); return 84; }
            if (!inactive) { await Close(o); return 87; }

            // ===== Tight blocks: two close columns and text at the screen's left edge =====
            if (!await ReadBox(o, new Point(2, 398), new Point(720, 572))) { Log("tight box not read"); await Close(o); return 81; }
            await Task.Delay(120);
            Shot("v15-badges-tight.png");
            bool cleanTight = BadgesClean(o, "two close columns and the left edge", o.PreviewOutlines.Count);
            cleanTight &= o.PreviewOutlines.Count == 3;
            o.Reader.Stop();
            if (!cleanMain || !cleanTight) { await Close(o); return 81; }

            // ===== OneCore =====
            var onecore = Voices.All().FirstOrDefault(v => !v.Desktop);
            if (onecore != null)
            {
                Voices.SavedId = onecore.Id;
                o.ResetVoice();
                Voices.PrimeShared(true);
                await Task.Delay(500);
                if (!await ReadBox(o, mainA, mainB)) { Log("onecore box not read"); await Close(o); return 82; }
                await Task.Delay(400);
                bool oc = await Press(o, "Next (Right key)", () => { Key(VK_RIGHT); return Task.CompletedTask; }) && o.Nav.Current == 1;
                await Task.Delay(700);
                oc &= await Press(o, "Next (Page Down, made ahead)", () => { Key(VK_NEXT); return Task.CompletedTask; }) && o.Nav.Current == 2;
                await Task.Delay(300);
                oc &= await Press(o, "Previous within 1.5 s (Page Up)", () => { Key(VK_PRIOR); return Task.CompletedTask; }) && o.Nav.Current == 1;
                Log("onecore nav ok=" + oc);
                o.Reader.Stop();
                Voices.SavedId = "";
                o.ResetVoice();
                if (!oc) { await Close(o); return 82; }
            }
            else _notes.Add("No OneCore voice on this machine: OneCore Previous / Next not measured.");

            // ===== Tap: Next and Previous go on into the neighbouring blocks =====
            var aLine = G("paraA").First().Box;
            var tapAt = new Point((int)(aLine.X + 40), (int)(aLine.Y + aLine.Height / 2));
            _readMark = o.ReadCount;
            await Click(tapAt);
            bool tapRead = await WaitFor(() => o.ReadCount > _readMark && o.FirstWordMs >= 0, 6000);
            float yA = aLine.Y, yItem1 = G("item1").First().Box.Y;
            bool tapStart = tapRead && o.LastReadWords.Count > 0 && Math.Abs(o.LastReadWords[0].Box.Y - yA) < 10;
            await Task.Delay(300);
            bool nextBlock = await Press(o, "Next after a tap", () => { Key(VK_RIGHT); return Task.CompletedTask; }) && o.LastNav == "next:NextBlock" &&
                             o.LastReadWords.Count > 0 && Math.Abs(o.LastReadWords[0].Box.Y - yItem1) < 10;
            await Task.Delay(300);
            bool prevBlock = await Press(o, "Previous within 1.5 s after a tap", () => { Key(VK_LEFT); return Task.CompletedTask; }) && o.LastNav == "previous:PreviousBlock" &&
                             o.LastReadWords.Count > 0 && Math.Abs(o.LastReadWords[0].Box.Y - yA) < 10;
            Log("tap neighbours: tap_read=" + tapStart + " next_block=" + nextBlock + " previous_block=" + prevBlock);
            _notes.Add("After a tap on the first paragraph: Next read the first list item: " + nextBlock + "; Previous within 1.5 s went back to the paragraph: " + prevBlock);
            o.Reader.Stop();
            if (!tapStart || !nextBlock || !prevBlock) { await Close(o); return 85; }

            // ===== The overlay open but nothing read: the keys are left alone =====
            await Task.Delay(300);
            int navIdle = o.NavCount;
            Key(VK_NEXT); Key(VK_PRIOR);
            await Task.Delay(300);
            bool idleIgnored = o.NavCount == navIdle;
            await Close(o);

            // ===== Badges off: no badges, the same order and Previous / Next =====
            VoiceSettings.ShowBadges = false;
            var off = Open();
            await WaitFor(() => off.Ocr != null, 15000);
            await WaitFor(() => off.IndexReady, 30000);
            await Task.Delay(400);
            bool offRead = await ReadBox(off, mainA, mainB);
            await Task.Delay(120);
            Shot("v15-badges-off.png");
            var offOrder = off.LastReadWords.Select(w => Norm(w.Text)).ToList();
            bool sameOrder = offRead && offOrder.SequenceEqual(onOrder);
            bool sameOutlines = off.PreviewOutlines.Count == onOutlines.Count && off.PreviewOutlines.Zip(onOutlines, (a, b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.W - b.W) < 0.5).All(x => x);
            bool noBadges = off.PreviewBadges.Count == 0 && !off.BadgesOn;
            int greenWhereBadges = 0;
            using (var shot = V11SelfTest.Capture(_screen))
                foreach (var b in onBadges)
                    for (int y = (int)b.Y + 2; y < (int)b.Bottom - 2; y++)
                        for (int x = (int)b.X + 2; x < (int)b.Right - 2; x++)
                        {
                            var c = shot.GetPixel(Math.Max(0, Math.Min(shot.Width - 1, x)), Math.Max(0, Math.Min(shot.Height - 1, y)));
                            if (c.G > 170 && c.G - c.R > 70 && c.G - c.B > 40) greenWhereBadges++;
                        }
            bool offNav = await Press(off, "Next with badges off", () => { Key(VK_RIGHT); return Task.CompletedTask; }) && off.Nav.Current == 1 && AtSegmentStart(off, 1);
            Log("badges off: no_badges=" + noBadges + " green_px_where_badges_were=" + greenWhereBadges + " same_order=" + sameOrder + " same_outlines=" + sameOutlines + " segments=" + off.Nav.List.Count + " next_ok=" + offNav);
            _notes.Add("Badges off: no badges drawn: " + (noBadges && greenWhereBadges == 0) + "; same words in the same order: " + sameOrder + "; same outlines: " + sameOutlines +
                       "; same segments: " + (off.Nav.List.Count == segCount) + "; Next works: " + offNav);
            off.Reader?.Stop();
            await Close(off);
            VoiceSettings.ShowBadges = true;
            if (!noBadges || greenWhereBadges > 0 || !sameOrder || !sameOutlines || off.Nav.List.Count != segCount || !offNav) return 86;

            // ===== Nothing being read: Page Up / Down and the arrows reach the app that has the focus =====
            int keysThrough = await KeysPassThrough();
            Log("keys while inactive: idle_overlay_ignored=" + idleIgnored + " reached_other_app=" + keysThrough + "/4 (with the keyboard hook on)");
            _notes.Add("Not reading: the overlay ignored Page Up / Down: " + idleIgnored + "; with the overlay closed and Rapcha's keyboard hook on, Page Up, Page Down, Left and Right all reached another window: " + (keysThrough == 4));
            if (!idleIgnored || keysThrough != 4) return 87;

            // ===== Latency =====
            int worst = _latencies.Count > 0 ? _latencies.Max() : -1;
            Log("latency presses=" + _latencies.Count + " worst_ms=" + worst + " median_ms=" + (_latencies.Count > 0 ? _latencies.OrderBy(x => x).ElementAt(_latencies.Count / 2) : -1));
            _notes.Add("Press to first audio: " + _latencies.Count + " presses, worst " + worst + " ms (target under 300 ms)");
            if (_latencies.Count == 0 || worst >= 300) return 88;

            // ===== Settings toggle =====
            int st = await SettingsTest();
            if (st != 0) return st;
            return 0;
        }

        private int Si(double v) => (int)Math.Round(v * _dpi / 96.0);

        private async Task<int> KeysPassThrough()
        {
            using (var mgr = new ShortcutManager(makeCurrent: false))
            {
                var set = ShortcutSet.Defaults();
                set.Hold = Rapcha.Core.Shortcut.Chord(KeyNames.Space, 0x56);   // a chord, so the low-level keyboard hook is on
                mgr.Apply(set);
                await Task.Delay(300);
                Log("keyboard hook active=" + mgr.HookActive);
                var f = new Form { Text = "Rapcha key test", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 200, 500, 300), TopMost = true };
                var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = string.Join("\r\n", Enumerable.Range(1, 40).Select(i => "line " + i)) };
                var got = new HashSet<Keys>();
                box.KeyDown += (s, e) => { if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) got.Add(e.KeyCode); };
                f.Controls.Add(box);
                f.Show();
                f.Activate();
                Native.SetForegroundWindow(f.Handle);
                box.Focus();
                await Task.Delay(400);
                foreach (var vk in new[] { VK_NEXT, VK_PRIOR, VK_LEFT, VK_RIGHT }) { Key(vk); await Task.Delay(120); }
                await Task.Delay(300);
                f.Close(); f.Dispose();
                return got.Count;
            }
        }

        // ---------------- Settings: Show number badges ----------------

        private async Task<int> SettingsTest()
        {
            SettingsPage.SilentPreview = true;
            VoiceSettings.ShowBadges = true;
            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var st = w.Settings;
            var t = st.BadgesToggle;
            for (System.Windows.Forms.Control c = t; c != null; c = c.Parent)
                if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(t); break; }
            await Task.Delay(300);
            bool lockedOn = st.VoiceLocked && !t.Enabled && t.Checked;
            w.SavePng(Path.Combine(_out, "v15-settings-toggle-locked-on.png"));
            st.EditVoiceButton.PerformClick();
            await Task.Delay(250);
            bool editable = t.Enabled;
            t.Checked = false;
            await Task.Delay(200);
            VoiceSettings.ReloadBadges();
            bool heldUntilDone = VoiceSettings.ShowBadges;
            w.SavePng(Path.Combine(_out, "v15-settings-toggle-editing-off.png"));
            st.EditVoiceButton.PerformClick();   // Done
            await Task.Delay(250);
            VoiceSettings.ReloadBadges();
            var path = Path.Combine(AppStorage.Folder, "number-badges.txt");
            bool savedOff = !VoiceSettings.ShowBadges && File.Exists(path) && File.ReadAllText(path).Trim() == "0" && !t.Enabled && !t.Checked;
            SettingsPage.ConfirmReset = () => true;
            st.ResetVoiceButton.PerformClick();
            await Task.Delay(300);
            VoiceSettings.ReloadBadges();
            bool resetOn = VoiceSettings.ShowBadges && t.Checked && st.VoiceLocked;
            w.SavePng(Path.Combine(_out, "v15-settings-toggle-after-reset.png"));
            Log("settings badges: locked_default_on=" + lockedOn + " editable_after_edit=" + editable + " held_until_done=" + heldUntilDone + " saved_off_on_done=" + savedOff + " reset_on=" + resetOn);
            _notes.Add("Settings, Show number badges: on and locked by default: " + lockedOn + "; changeable after Edit: " + editable + "; saved only on Done: " + (heldUntilDone && savedOff) + "; Reset turns it back on: " + resetOn);
            w.Close();
            w.Dispose();
            return lockedOn && editable && heldUntilDone && savedOff && resetOn ? 0 : 89;
        }

        // ---------------- Icon ----------------

        private int IconTest()
        {
            try
            {
                byte[] ico;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                using (var ms = new MemoryStream()) { stream.CopyTo(ms); ico = ms.ToArray(); }
                int count = BitConverter.ToUInt16(ico, 4);
                var sizes = new List<int>();
                double frac256 = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + 16 * i;
                    int w = ico[e] == 0 ? 256 : ico[e];
                    sizes.Add(w);
                    if (w != 256) continue;
                    int size = BitConverter.ToInt32(ico, e + 8), offset = BitConverter.ToInt32(ico, e + 12);
                    using (var ms = new MemoryStream(ico, offset, size))
                    using (var b = new Bitmap(Image.FromStream(ms)))
                    {
                        int tl = 999, tr = -1, rl = 999, rr = -1;
                        for (int x = 0; x < b.Width; x++)
                        {
                            var p = b.GetPixel(x, b.Height / 2);
                            if (p.A > 200) { tl = Math.Min(tl, x); tr = Math.Max(tr, x); }
                            if (p.A > 200 && (p.R - p.G > 60 || Math.Max(p.R, Math.Max(p.G, p.B)) < 215)) { if (x > tl + 4) { rl = Math.Min(rl, x); rr = Math.Max(rr, x); } }
                        }
                        // The ring's outer edge on the middle row (left of the tile's right border).
                        int ringL = rl, ringR = rr;
                        for (int x = tr - 3; x > tl; x--) { var p = b.GetPixel(x, b.Height / 2); if (p.A > 200 && (p.R - p.G > 60 || Math.Max(p.R, Math.Max(p.G, p.B)) < 215)) { ringR = x; break; } }
                        for (int x = tl + 3; x < tr; x++) { var p = b.GetPixel(x, b.Height / 2); if (p.A > 200 && (p.R - p.G > 60 || Math.Max(p.R, Math.Max(p.G, p.B)) < 215)) { ringL = x; break; } }
                        frac256 = (double)(ringR - ringL + 1) / Math.Max(1, tr - tl + 1);
                        b.Save(Path.Combine(_out, "v15-icon-256.png"), ImageFormat.Png);
                    }
                }
                Log("icon sizes=" + string.Join(",", sizes) + " ring_of_tile=" + frac256.ToString("0.00"));
                _notes.Add("App icon: " + count + " sizes (" + string.Join(", ", sizes) + "); the ring fills " + (int)Math.Round(frac256 * 100) + "% of the tile's width (was about 73%)");
                return count == 8 && frac256 >= 0.80 && frac256 <= 0.88 ? 0 : 90;
            }
            catch (Exception e) { Log("icon failed " + e.GetType().Name); return 90; }
        }
    }
}
