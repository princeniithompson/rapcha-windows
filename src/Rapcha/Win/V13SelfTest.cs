using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// CI self-test for v13 (--selftest-v13 outDir). Renders a test screen like the user's video (dark #1e1e1e
    /// panel: white all-caps title line, smaller monospaced line with green + white tokens, a list whose two items
    /// are both "1.", a blue link, 9 px grey text; light panel: paragraph, red/blue/green links, 9 px grey text)
    /// and opens the real select-to-speak overlay on it. Checks: a 2-line box reads both lines in order; a box
    /// around the list never reads the title and reads every item in order; a tap on a code token reads it; small
    /// grey and coloured text are read; release to first word is under 1.5 s; word recall before (v12: one pass
    /// + old snap) vs after; the ring has no rotating decoration while select to speak is on (frame diff); the app
    /// icon is on a white rounded square at every size; Settings → Select to speak is locked until Edit, Done
    /// saves and locks, Reset (confirmed) restores only those settings. No recognised words are written to the
    /// log; only counts, percentages and timings.
    /// Exit: 0 ok, 60 OCR unavailable, 61 two-line box, 62 list box, 63 tap on code, 64 small/coloured text,
    /// 65 first word too slow, 66 ring decoration changes, 67 icon, 68 Settings Edit/Done, 69 Settings Reset, 1 crash.
    /// </summary>
    internal sealed class V13SelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "voice.txt", "voice-speed.txt", "glow.txt", "shortcuts.txt" };
        private readonly byte[][] _backup;

        public V13SelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest v13: " + s);

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
            Log("exit=" + code);
            Environment.ExitCode = code;
            ExitThread();
        }

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var at = DateTime.UtcNow;
            while ((DateTime.UtcNow - at).TotalMilliseconds < ms) { if (ok()) return true; await Task.Delay(15); }
            return ok();
        }

        // ---------------- The test screen ----------------

        private sealed class GtLine
        {
            public string Group, Text;
            public RectangleF Box;
            public readonly List<(string text, RectangleF box)> Tokens = new List<(string, RectangleF)>();
        }

        private readonly List<GtLine> _gt = new List<GtLine>();

        private static readonly Color Dark = Color.FromArgb(0x1e, 0x1e, 0x1e);
        private static readonly Color DarkInk = Color.FromArgb(0xe3, 0xe3, 0xe3);
        private static readonly Color CodeGreen = Color.FromArgb(0x81, 0xc9, 0x95);

        /// <summary>Draws one line of (text, colour) runs; records every token's box.</summary>
        private GtLine Draw(Graphics g, string group, float x, float y, Font f, params (string text, Color c)[] runs)
        {
            var line = new GtLine { Group = group, Text = string.Join("", runs.Select(r => r.text)).Trim() };
            var fmt = StringFormat.GenericTypographic;
            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            float cx = x, h = f.GetHeight(g);
            foreach (var (text, c) in runs)
            {
                var parts = text.Split(' ');
                for (int i = 0; i < parts.Length; i++)
                {
                    var p = parts[i];
                    if (p.Length > 0)
                    {
                        var size = g.MeasureString(p, f, 10000, fmt);
                        using (var b = new SolidBrush(c)) g.DrawString(p, f, b, cx, y, fmt);
                        line.Tokens.Add((p, new RectangleF(cx, y, size.Width, h)));
                        cx += size.Width;
                    }
                    if (i < parts.Length - 1) cx += g.MeasureString(" ", f, 10000, fmt).Width;
                }
            }
            line.Box = new RectangleF(x, y, cx - x, h);
            _gt.Add(line);
            return line;
        }

        private Bitmap RenderScreen(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.White);
                int split = 470;
                using (var b = new SolidBrush(Dark)) g.FillRectangle(b, 0, 0, w, split);
                using (var title = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var mono = new Font("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var head = new Font("Segoe UI Semibold", 18f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var body = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var lhead = new Font("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    Draw(g, "title", 40, 40, title, ("CRITICAL CRASH REPORT - DIAGNOSE AND FIX IN ONE SINGLE TURN (DO NOT ASK FOR APPROVAL FIRST, AND DO NOT", DarkInk));
                    Draw(g, "code", 40, 62, mono, ("RUN ", DarkInk), (":app:testDebugUnitTest", CodeGreen), (" OR ", DarkInk), (":app:packageDebug", CodeGreen), (" SO THIS TURN DOES NOT GET CANCELED).", DarkInk));
                    Draw(g, "listTitle", 40, 120, head, ("Critical crash report", Color.White));
                    Draw(g, "item1", 56, 162, body, ("1. Just installed the updated app on my Samsung phone and it crashes", DarkInk));
                    Draw(g, "item1", 74, 182, body, ("on start. Nothing else changed since yesterday.", DarkInk));
                    Draw(g, "item2", 56, 214, body, ("1. Before opening the app I cleared its cache and storage.", DarkInk));
                    Draw(g, "darkLink", 40, 270, body, ("See the logs in ", DarkInk), ("crash report details", Color.FromArgb(0x8a, 0xb4, 0xf8)), (" or open ", DarkInk), ("Logcat", Color.FromArgb(0xf2, 0x8b, 0x82)));
                    Draw(g, "darkSmall", 40, 300, small, ("Gemini can make mistakes, so double-check it", Color.FromArgb(0x9a, 0xa0, 0xa6)));

                    Draw(g, "lightTitle", 40, 500, lhead, ("Release notes", Color.FromArgb(0x20, 0x21, 0x24)));
                    Draw(g, "lightPara", 40, 532, body, ("The update fixes the startup crash on Samsung phones.", Color.FromArgb(0x20, 0x21, 0x24)));
                    Draw(g, "lightPara", 40, 552, body, ("Read more on the ", Color.FromArgb(0x20, 0x21, 0x24)), ("support page", Color.FromArgb(0x1a, 0x73, 0xe8)), (" or ", Color.FromArgb(0x20, 0x21, 0x24)), ("contact the team", Color.FromArgb(0xd9, 0x30, 0x25)));
                    Draw(g, "lightSmall", 40, 586, small, ("Last updated 16 November 2026 by the Rapcha team", Color.FromArgb(0x80, 0x86, 0x8b)));
                    Draw(g, "lightGreen", 40, 606, body, ("View the build log", Color.FromArgb(0x18, 0x80, 0x38)));
                }
            }
            return bmp;
        }

        private IEnumerable<GtLine> Lines(params string[] groups) => _gt.Where(l => groups.Contains(l.Group));
        private static RectangleF Union(IEnumerable<GtLine> ls) => ls.Select(l => l.Box).Aggregate(RectangleF.Union);

        // ---------------- Recall ----------------

        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Share of the expected words found in the read words, in order (joined or split tokens count).</summary>
        private static double OrderedRecall(IEnumerable<GtLine> expected, IEnumerable<string> read)
        {
            var gt = expected.SelectMany(l => l.Tokens.Select(t => Norm(t.text))).Where(t => t.Length > 0).ToList();
            string r = string.Concat(read.Select(Norm));
            int pos = 0, hit = 0;
            foreach (var t in gt)
            {
                int i = r.IndexOf(t, pos, StringComparison.Ordinal);
                if (i >= 0 && (hit == 0 || i - pos <= 40)) { hit++; pos = i + t.Length; }
            }
            return gt.Count == 0 ? 1 : (double)hit / gt.Count;
        }

        /// <summary>Share of the expected words found anywhere on the page (any order).</summary>
        private static double PageRecall(IEnumerable<GtLine> expected, PageLayout page)
        {
            var words = page.Lines.SelectMany(l => l.Words).Select(w => Norm(w.Text)).Where(t => t.Length > 0).ToList();
            var set = new HashSet<string>(words);
            var gt = expected.SelectMany(l => l.Tokens.Select(t => Norm(t.text))).Where(t => t.Length > 0).ToList();
            int hit = gt.Count(t => set.Contains(t) || (t.Length >= 4 && words.Any(w => w.Contains(t))));
            return gt.Count == 0 ? 1 : (double)hit / gt.Count;
        }

        private static string Pct(double v) => ((int)Math.Round(v * 100)).ToString() + "%";

        // ---------------- Steps ----------------

        private readonly List<string> _report = new List<string>();

        private async Task<int> Steps()
        {
            var screen = Screen.PrimaryScreen.Bounds;
            int dpi = Native.DpiAt(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
            Log("screen " + screen.Width + "x" + screen.Height + " dpi=" + dpi);
            Voices.SavedId = "";       // Windows default voice for the timing
            VoiceSettings.Speed = 1.0;

            int ocr = await OcrTests(screen, dpi);
            if (ocr != 0) return ocr;
            int ring = await RingTest();
            if (ring != 0) return ring;
            int icon = IconTest();
            if (icon != 0) return icon;
            return await SettingsTest();
        }

        private async Task<int> OcrTests(Rectangle screen, int dpi)
        {
            using (var img = RenderScreen(screen.Width, screen.Height))
            {
                img.Save(Path.Combine(_out, "ocr-test-screen.png"), ImageFormat.Png);
                var src = FrameSource.From(img);

                // Before: the v12 recogniser (one colour pass) and the v12 snap rules.
                var legacy = await OcrService.LegacyAsync(src, dpi);
                if (legacy.Page.WordCount == 0) { Log("legacy OCR found nothing (OCR unavailable?)"); return 60; }

                var overlay = new ScreenOverlay(screen, img, dpi, silentVoice: true);
                overlay.Show();
                overlay.Activate();
                try
                {
                    var sw = Stopwatch.StartNew();
                    var full = await OcrService.RecognizeAsync(overlay.Source, dpi);
                    if (full.Problem.Length > 0) { Log("OCR problem"); return 60; }
                    overlay.SetOcr(full);
                    Log("full passes=" + full.Passes.Count + " ms=" + full.Ms + " words=" + full.Page.WordCount + " legacy_words=" + legacy.Page.WordCount +
                        " " + string.Join(",", full.Passes.Select(p => p.Name + ":" + p.WordCount + "w/" + p.Ms + "ms")));

                    var results = new List<(string name, Box box, double before, double after, int ms, bool ok)>();

                    // S1: the video's 2-line box: top edge through the title line, bottom past the code line.
                    var t = Lines("title").First().Box; var c = Lines("code").First().Box;
                    var s1 = Box.FromCorners(30, t.Y + 0.4 * t.Height, Math.Max(t.Right, c.Right) + 8, c.Bottom + 5);
                    var r1 = await BoxScenario(overlay, legacy, s1, Lines("title", "code"), "two-line");
                    bool titleFirst = r1.words.Count > 0 && Norm(r1.words[0]).StartsWith("critical");
                    bool noList = !r1.words.Any(w => Norm(w).Contains("crashreport") || w == "Critical");
                    bool ok1 = r1.after >= 0.9 && titleFirst && noList && r1.ms >= 0 && r1.ms < 1500;
                    results.Add(("two-line box (title + code line)", s1, r1.before, r1.after, r1.ms, ok1));
                    Log("two-line after=" + Pct(r1.after) + " before=" + Pct(r1.before) + " title_first=" + titleFirst + " no_list=" + noList + " first_word_ms=" + r1.ms + " ok=" + ok1);

                    // S2: a box around the list starting just under its title.
                    var lt = Lines("listTitle").First().Box; var i2 = Lines("item2").First().Box;
                    var s2 = Box.FromCorners(30, lt.Bottom + 3, 820, i2.Bottom + 8);
                    var r2 = await BoxScenario(overlay, legacy, s2, Lines("item1", "item2"), "list", overlayShot: "overlay-list-box.png");
                    bool noTitle = !r2.wordsBoxes.Any(w => w.CenterY < s2.Y - 4) && !r2.words.Any(w => w == "Critical");
                    int iJust = r2.words.FindIndex(w => Norm(w).Contains("just")), iBefore = r2.words.FindIndex(w => Norm(w).Contains("before"));
                    bool order = iJust >= 0 && iJust < iBefore;
                    bool ok2 = r2.after >= 0.9 && noTitle && order && r2.ms >= 0 && r2.ms < 1500;
                    results.Add(("list box (two items both '1.')", s2, r2.before, r2.after, r2.ms, ok2));
                    Log("list after=" + Pct(r2.after) + " before=" + Pct(r2.before) + " no_title=" + noTitle + " items_in_order=" + order + " first_word_ms=" + r2.ms + " ok=" + ok2);

                    // S3/S4: coloured links and 9 px grey text, dark and light.
                    var s3r = Union(Lines("darkLink", "darkSmall"));
                    var s3 = Box.FromCorners(30, s3r.Y - 6, s3r.Right + 12, s3r.Bottom + 6);
                    var r3 = await BoxScenario(overlay, legacy, s3, Lines("darkLink", "darkSmall"), "dark-small-coloured");
                    var s4r = Union(Lines("lightPara", "lightSmall", "lightGreen"));
                    var s4 = Box.FromCorners(30, s4r.Y - 6, s4r.Right + 12, s4r.Bottom + 6);
                    var r4 = await BoxScenario(overlay, legacy, s4, Lines("lightPara", "lightSmall", "lightGreen"), "light-small-coloured");
                    double smallDark = OrderedRecall(Lines("darkSmall"), r3.words), smallLight = OrderedRecall(Lines("lightSmall"), r4.words);
                    bool ok3 = r3.after >= 0.85 && smallDark >= 0.8, ok4 = r4.after >= 0.85 && smallLight >= 0.8;
                    results.Add(("dark: blue/red links + 9px grey", s3, r3.before, r3.after, r3.ms, ok3));
                    results.Add(("light: blue/red/green links + 9px grey", s4, r4.before, r4.after, r4.ms, ok4));
                    Log("dark-small after=" + Pct(r3.after) + " before=" + Pct(r3.before) + " small9px=" + Pct(smallDark) + " ok=" + ok3);
                    Log("light-small after=" + Pct(r4.after) + " before=" + Pct(r4.before) + " small9px=" + Pct(smallLight) + " ok=" + ok4);

                    // S5: taps on the green code token (and on the 9 px grey text).
                    var tok = Lines("code").First().Tokens.First(x => x.text.Contains("testDebugUnitTest")).box;
                    var r5 = await TapScenario(overlay, legacy, tok.X + tok.Width * 0.5, tok.Y + tok.Height * 0.5, "testdebugunittest");
                    var below = await TapScenario(overlay, legacy, tok.X + tok.Width * 0.3, tok.Bottom + 9, "testdebugunittest"); // 9 px under the line
                    var sm = Lines("lightSmall").First().Tokens.First(x => x.text == "November").box;
                    var r6 = await TapScenario(overlay, legacy, sm.X + sm.Width / 2, sm.Y + sm.Height / 2, "november");
                    bool ok5 = r5.found && r5.spokenOk && below.found;
                    Log("tap code found=" + r5.found + " sayable=" + r5.spokenOk + " ms=" + r5.ms + " legacy_found=" + r5.legacyFound +
                        "; tap 9px under code found=" + below.found + " legacy_found=" + below.legacyFound + "; tap 9px grey found=" + r6.found + " legacy_found=" + r6.legacyFound);

                    // Whole page detection, before vs after (after: with the background 2x pass merged too).
                    await WaitFor(() => overlay.Ocr.Passes.Any(p => p.Name.StartsWith("full-norm-")), 15000);
                    double pageBefore = PageRecall(_gt, legacy.Page), pageAfter = PageRecall(_gt, overlay.Ocr.Page);
                    Log("page recall after=" + Pct(pageAfter) + " before=" + Pct(pageBefore) + " passes=" + overlay.Ocr.Passes.Count +
                        " (" + string.Join(",", overlay.Ocr.Passes.Select(p => p.Name)) + ") open_to_all_ms=" + sw.ElapsedMilliseconds);
                    foreach (var g in _gt.Select(l => l.Group).Distinct())
                        Log("group " + g + " page_recall after=" + Pct(PageRecall(Lines(g), overlay.Ocr.Page)) + " before=" + Pct(PageRecall(Lines(g), legacy.Page)));

                    // OneCore voice: release to first word (chunked synthesis), when one is installed.
                    var onecore = Voices.All().FirstOrDefault(v => !v.Desktop);
                    int oneMs = -1;
                    if (onecore != null)
                    {
                        Voices.SavedId = onecore.Id;
                        overlay.ResetVoice();
                        var ro = await BoxScenario(overlay, legacy, s2, Lines("item1", "item2"), "list-onecore");
                        oneMs = ro.ms;
                        Voices.SavedId = "";
                        overlay.ResetVoice();
                        Log("onecore first_word_ms=" + oneMs + " voice_label=\"" + onecore.Label + "\"");
                    }

                    OcrService.DebugDump(overlay.Ocr, "v13-after");
                    OcrService.DebugDump(legacy, "v13-before");
                    SaveResultsImage(img, legacy.Page, overlay.Ocr.Page, results, pageBefore, pageAfter);
                    File.WriteAllLines(Path.Combine(_out, "ocr-recall.txt"), new[]
                    {
                        "Word recall on the v13 test screen (before = v12: one colour pass + v12 snap; after = v13 multi-pass + new snap)",
                        "page (all words detected): before " + Pct(pageBefore) + " after " + Pct(pageAfter),
                    }.Concat(results.Select(r => r.name + ": before " + Pct(r.before) + " after " + Pct(r.after) + " first word " + r.ms + " ms " + (r.ok ? "PASS" : "FAIL")))
                     .Concat(new[]
                     {
                        "tap on :app:testDebugUnitTest: " + (r5.found ? "read" : "not found") + " (v12: " + (r5.legacyFound ? "read" : "No text found there") + ")",
                        "tap 9 px under the code line: " + (below.found ? "read" : "not found") + " (v12: " + (below.legacyFound ? "read" : "No text found there") + ")",
                        "tap on 9 px grey text: " + (r6.found ? "read" : "not found") + " (v12: " + (r6.legacyFound ? "read" : "No text found there") + ")",
                        "first word with a OneCore voice (list box): " + oneMs + " ms",
                     }));

                    if (!ok1) return 61;
                    if (!ok2) return 62;
                    if (!ok5) return 63;
                    if (!ok3 || !ok4 || !r6.found) return 64;
                    if (onecore != null && (oneMs < 0 || oneMs >= 1500)) return 65;
                    return 0;
                }
                finally
                {
                    overlay.Close();
                    overlay.Dispose();
                }
            }
        }

        private async Task<(double before, double after, int ms, List<string> words, List<Box> wordsBoxes)> BoxScenario(
            ScreenOverlay overlay, OcrService.Result legacy, Box box, IEnumerable<GtLine> expected, string name, string overlayShot = null)
        {
            overlay.Reader?.Stop();
            await Task.Delay(150);
            overlay.SimulateDrag(box);
            bool done = await WaitFor(() => overlay.FirstWordMs >= 0 || overlay.LastReadText == ScreenOverlay.NoTextThere, 10000);
            var words = overlay.LastReadWords.Select(w => w.Text).ToList();
            var boxes = overlay.LastReadWords.Select(w => w.Box).ToList();
            if (overlayShot != null)
            {
                await Task.Delay(250);
                using (var bmp = V11SelfTest.Capture(Screen.PrimaryScreen.Bounds)) bmp.Save(Path.Combine(_out, overlayShot), ImageFormat.Png);
            }
            overlay.Reader?.Stop();
            var legacyWords = PageLayout.WordsOf(legacy.Page.SnapLegacy(box)).Select(w => w.Text).ToList();
            double before = OrderedRecall(expected, legacyWords), after = OrderedRecall(expected, words);
            Log(name + " done=" + done + " words=" + words.Count + " region_passes=" + overlay.RegionPassesUsed + " legacy_words=" + legacyWords.Count);
            try { File.AppendAllText(Path.Combine(_out, "ocr-scenarios.txt"), name + " box=" + box + "\n  after:  " + string.Join(" ", words) + "\n  before: " + string.Join(" ", legacyWords) + "\n"); } catch { }
            return (before, after, overlay.FirstWordMs, words, boxes);
        }

        private async Task<(bool found, bool legacyFound, bool spokenOk, int ms)> TapScenario(ScreenOverlay overlay, OcrService.Result legacy, double x, double y, string want)
        {
            overlay.Reader?.Stop();
            await Task.Delay(150);
            overlay.SimulateTap(x, y);
            await WaitFor(() => overlay.FirstWordMs >= 0, 6000);
            var words = overlay.LastReadWords.Select(w => Norm(w.Text)).ToList();
            bool found = overlay.FirstWordMs >= 0 && words.Any(w => w.Contains(want));
            bool spoken = want != "testdebugunittest" || overlay.LastReadText.Contains("test Debug Unit Test");
            int ms = overlay.FirstWordMs;
            overlay.Reader?.Stop();
            overlay.CancelCorner();
            var ll = legacy.Page.LineAt(x, y);
            bool legacyFound = ll != null && ll.Words.Any(w => Norm(w.Text).Contains(want));
            return (found, legacyFound, spoken, ms);
        }

        private void SaveResultsImage(Bitmap img, PageLayout before, PageLayout after, List<(string name, Box box, double before, double after, int ms, bool ok)> results, double pb, double pa)
        {
            using (var bmp = new Bitmap(img))
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI Semibold", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var red = new Pen(Color.FromArgb(200, 255, 70, 70), 1f) { DashStyle = DashStyle.Dot })
                    foreach (var w in before.Lines.SelectMany(l => l.Words)) g.DrawRectangle(red, (float)w.Box.X - 2, (float)w.Box.Y - 2, (float)w.Box.W + 4, (float)w.Box.H + 4);
                using (var green = new Pen(Color.FromArgb(230, 70, 235, 120), 1.4f))
                    foreach (var w in after.Lines.SelectMany(l => l.Words)) g.DrawRectangle(green, (float)w.Box.X, (float)w.Box.Y, (float)w.Box.W, (float)w.Box.H);
                int n = 0;
                foreach (var r in results)
                {
                    n++;
                    using (var pen = new Pen(r.ok ? Color.Gold : Color.OrangeRed, 2f) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(pen, (float)r.box.X, (float)r.box.Y, (float)r.box.W, (float)r.box.H);
                    var label = n + ". " + r.name + ": recall " + Pct(r.before) + " → " + Pct(r.after) + ", first word " + r.ms + " ms";
                    var size = g.MeasureString(label, font);
                    float lx = (float)Math.Min(r.box.Right + 8, bmp.Width - size.Width - 8), ly = (float)r.box.Y;
                    if (lx < r.box.Right) ly = (float)r.box.Bottom + 2;
                    using (var bg = new SolidBrush(Color.FromArgb(220, 0, 0, 0))) g.FillRectangle(bg, lx - 3, ly - 1, size.Width + 6, size.Height + 2);
                    g.DrawString(label, font, r.ok ? Brushes.Gold : Brushes.OrangeRed, lx, ly);
                }
                var legend = "green = v13 words (multi-pass)   red dotted = v12 words (one pass)   page recall " + Pct(pb) + " → " + Pct(pa);
                var ls = g.MeasureString(legend, font);
                using (var bg = new SolidBrush(Color.FromArgb(230, 0, 0, 0))) g.FillRectangle(bg, 8, bmp.Height - ls.Height - 14, ls.Width + 10, ls.Height + 6);
                g.DrawString(legend, font, Brushes.White, 13, bmp.Height - ls.Height - 11);
                bmp.Save(Path.Combine(_out, "ocr-results-overlay.png"), ImageFormat.Png);
            }
        }

        // ---------------- Ring: nothing rotates while select to speak is on ----------------

        private async Task<int> RingTest()
        {
            using (var ring = new RingWindow())
            {
                ring.RestAllowed = () => false;
                ring.ShowDocked();
                await Task.Delay(600);
                Bitmap plain = ring.Compose();
                ring.SelectMode = true;
                await Task.Delay(400);
                var frames = new List<Bitmap>();
                var screens = new List<Bitmap>();
                var area = Rectangle.Inflate(ring.VisibleRingBounds, 24, 24);
                for (int i = 0; i < 4; i++)
                {
                    frames.Add(ring.Compose());
                    screens.Add(V11SelfTest.Capture(area));
                    await Task.Delay(250);
                }
                int composeDiff = frames.Skip(1).Max(f => Diff(frames[0], f));
                int screenDiff = screens.Skip(1).Max(f => Diff(screens[0], f));
                int vsPlain = Diff(plain, frames[0]);
                bool animating = ring.Animating;
                screens[0].Save(Path.Combine(_out, "ring-select-mode.png"), ImageFormat.Png);
                ring.SelectMode = false;
                Log("ring select-mode frame_diff=" + composeDiff + " screen_diff=" + screenDiff + " vs_plain=" + vsPlain + " anim_timer=" + animating);
                plain.Dispose();
                foreach (var b in frames.Concat(screens)) b.Dispose();
                return composeDiff == 0 && screenDiff <= 4 && vsPlain == 0 && !animating ? 0 : 66;
            }
        }

        private static int Diff(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return int.MaxValue;
            int n = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                    if (Math.Abs(p.A - q.A) > 8 || Math.Abs(p.R - q.R) > 8 || Math.Abs(p.G - q.G) > 8 || Math.Abs(p.B - q.B) > 8) n++;
                }
            return n;
        }

        // ---------------- Icon ----------------

        private int IconTest()
        {
            bool all = true;
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
            using (var sheet = new Bitmap(40 + sizes.Sum() + 20 * sizes.Length, 320))
            using (var sg = Graphics.FromImage(sheet))
            {
                sg.Clear(Color.FromArgb(0x20, 0x20, 0x20));
                int x = 20;
                foreach (int s in sizes)
                {
                    using (var st = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                    using (var ico = new Icon(st, s, s))
                    using (var bmp = ico.ToBitmap())
                    {
                        bool ok = WhiteBackground(bmp, out string why) && bmp.Width == s;
                        Log("icon " + s + "px got=" + bmp.Width + " white_rounded=" + ok + " " + why);
                        all &= ok;
                        if (s == 256 || s == 48 || s == 32 || s == 16) bmp.Save(Path.Combine(_out, "icon-" + s + ".png"), ImageFormat.Png);
                        sg.DrawImageUnscaled(bmp, x, 300 - s - 20);
                        x += s + 20;
                    }
                }
                sheet.Save(Path.Combine(_out, "icon-sizes-on-dark.png"), ImageFormat.Png);
            }
            try
            {
                using (var exe = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                using (var bmp = exe.ToBitmap())
                {
                    bool ok = WhiteBackground(bmp, out string why);
                    Log("exe icon " + bmp.Width + "px white_rounded=" + ok + " " + why);
                    all &= ok;
                }
            }
            catch (Exception e) { Log("exe icon failed " + e.GetType().Name); all = false; }
            bool app = Program.AppIcon != null && WhiteBackground(Program.AppIcon.ToBitmap(), out _);
            Log("window icon white_rounded=" + app);
            return all && app ? 0 : 67;
        }

        /// <summary>Corners transparent (rounded), a white band just inside the left/top edge, the red ring in the middle area.</summary>
        private static bool WhiteBackground(Bitmap b, out string why)
        {
            int s = b.Width;
            var corner = b.GetPixel(0, 0);
            int inset = Math.Max(1, (int)Math.Round(s * 0.09));
            var edges = new[] { b.GetPixel(inset, s / 2), b.GetPixel(s / 2, inset), b.GetPixel(s - 1 - inset, s / 2), b.GetPixel(s / 2, s - 1 - inset) };
            bool white = edges.All(p => p.A > 220 && p.R > 225 && p.G > 225 && p.B > 225);
            bool red = false;
            for (int y = 0; y < s && !red; y++)
                for (int x = 0; x < s && !red; x++) { var p = b.GetPixel(x, y); red = p.A > 200 && p.R > 150 && p.G < 90 && p.B < 90; }
            bool cornerClear = corner.A < 60;
            why = "corner_a=" + corner.A + " edge_white=" + white + " red=" + red;
            return white && red && cornerClear;
        }

        // ---------------- Settings: Edit / Done / Reset ----------------

        private async Task<int> SettingsTest()
        {
            SettingsPage.SilentPreview = true;
            Voices.SavedId = "";
            VoiceSettings.Speed = 1.0;
            AppStorage.SaveGlowColor(0x3366FF);
            var glowBefore = File.ReadAllBytes(Path.Combine(AppStorage.Folder, "glow.txt"));
            var scPath = Path.Combine(AppStorage.Folder, "shortcuts.txt");
            var scBefore = File.Exists(scPath) ? File.ReadAllBytes(scPath) : null;

            var w = new MainWindow(MainWindow.Page.Settings);
            w.Show();
            w.BringUp();
            await Task.Delay(800);
            var st = w.Settings;
            var picker = st.VoicePicker;
            for (Control c = picker; c != null; c = c.Parent)
                if (c is ScrollableControl sc && sc.AutoScroll) { sc.ScrollControlIntoView(st.EditVoiceButton.Parent ?? picker); break; }
            await Task.Delay(300);

            // Locked by default.
            bool locked = st.VoiceLocked && !picker.Enabled && !st.PreviewButton.Enabled && !st.SpeedSlider.Enabled && st.EditVoiceButton.Text == "Edit";
            w.SavePng(Path.Combine(_out, "settings-select-to-speak-locked.png"));
            st.EditVoiceButton.PerformClick();
            await Task.Delay(250);
            bool unlocked = !st.VoiceLocked && picker.Enabled && st.PreviewButton.Enabled && st.SpeedSlider.Enabled && st.EditVoiceButton.Text == "Done";
            var all = Voices.All();
            var want = all.FirstOrDefault(v => !v.Desktop && v.Id != Voices.DefaultSapiId()) ?? all.Last();
            picker.SelectedItem = picker.Items.Cast<VoiceEntry>().First(v => v.Id == want.Id);
            st.SpeedSlider.Value = 13;
            await Task.Delay(250);
            bool notYetSaved = Voices.SavedId == "" && File.Exists(Path.Combine(AppStorage.Folder, "voice-speed.txt")) && Math.Abs(ReadSavedSpeed() - 1.0) < 0.01;
            w.SavePng(Path.Combine(_out, "settings-select-to-speak-unlocked.png"));
            st.EditVoiceButton.PerformClick(); // Done
            await Task.Delay(250);
            bool saved = st.VoiceLocked && !picker.Enabled && Voices.SavedId == want.Id && Math.Abs(VoiceSettings.Speed - 1.3) < 0.01 && Math.Abs(ReadSavedSpeed() - 1.3) < 0.01;
            Log("settings locked_default=" + locked + " unlocked_by_edit=" + unlocked + " held_until_done=" + notYetSaved + " saved_and_locked_on_done=" + saved);
            if (!locked || !unlocked || !notYetSaved || !saved) { w.Close(); return 68; }

            // Reset: a "No" changes nothing; a "Yes" restores only the select to speak settings.
            SettingsPage.ConfirmReset = () => false;
            st.ResetVoiceButton.PerformClick();
            await Task.Delay(200);
            bool keptOnNo = Voices.SavedId == want.Id && Math.Abs(VoiceSettings.Speed - 1.3) < 0.01;
            SettingsPage.ConfirmReset = () => true;
            st.ResetVoiceButton.PerformClick();
            await Task.Delay(300);
            var def = Voices.DefaultSapiId();
            bool reset = Voices.SavedId == "" && Math.Abs(VoiceSettings.Speed - 1.0) < 0.01 && st.SpeedSlider.Value == 10 &&
                         picker.SelectedItem is VoiceEntry sel && sel.Id == def && st.VoiceLocked;
            bool othersKept = File.ReadAllBytes(Path.Combine(AppStorage.Folder, "glow.txt")).SequenceEqual(glowBefore) &&
                              ((scBefore == null && !File.Exists(scPath)) || (scBefore != null && File.ReadAllBytes(scPath).SequenceEqual(scBefore)));
            w.SavePng(Path.Combine(_out, "settings-select-to-speak-after-reset.png"));
            Log("settings reset kept_on_no=" + keptOnNo + " restored_defaults=" + reset + " other_settings_unchanged=" + othersKept);
            w.Close();
            w.Dispose();
            return keptOnNo && reset && othersKept ? 0 : 69;
        }

        private static double ReadSavedSpeed()
        {
            try
            {
                var t = File.ReadAllText(Path.Combine(AppStorage.Folder, "voice-speed.txt")).Trim();
                return double.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return -1; }
        }
    }
}
