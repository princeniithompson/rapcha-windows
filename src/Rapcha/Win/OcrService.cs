using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Rapcha.Core;
using OcrWord = Rapcha.Core.OcrWord;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Rapcha.Win
{
    /// <summary>The frozen screen in physical pixels (BGRA), kept for the region passes.</summary>
    public sealed class FrameSource
    {
        public readonly byte[] Bgra;
        public readonly int W, H;
        public FrameSource(byte[] bgra, int w, int h) { Bgra = bgra; W = w; H = h; }

        public static FrameSource From(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[b.Width * b.Height * 4];
                for (int y = 0; y < b.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * b.Width * 4, b.Width * 4);
                for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
                return new FrameSource(bytes, b.Width, b.Height);
            }
            finally { b.UnlockBits(data); }
        }
    }

    /// <summary>
    /// Offline text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr), run several
    /// times on differently prepared copies of the screen and merged (see <see cref="OcrMerge"/>):
    /// - colour: the screenshot as it is (enlarged 1.5x on 100% screens, 2x for a selection);
    /// - "norm": grey, with dark areas inverted (dark themes become dark text on white), using the brightest
    ///   colour channel on dark areas and the darkest on light ones so green, blue and red text stay strong,
    ///   then contrast-stretched;
    /// - "bin": the norm image thresholded against its local mean (faint grey text becomes black);
    /// - enlarged 2x and 3x with high-quality bicubic scaling for small text, tiled to the engine's size limit.
    /// The user's language and English are both tried on a selection. Nothing is downloaded; words and pixels
    /// never leave the PC and are never logged.
    /// </summary>
    public static class OcrService
    {
        public sealed class Result
        {
            public OcrLayout Layout = new OcrLayout();
            public PageLayout Page = new PageLayout();
            public readonly List<OcrPass> Passes = new List<OcrPass>();
            public string Problem = "";
            public string Language = "";
            public int Ms;
            public double Scale = 1;
        }

        public const string NoLanguage = "Windows text recognition isn't available for your language. Add English in Settings > Time & language > Language & region, then try again.";

        // ---------------- Engines ----------------

        private static string _userTag;
        private static bool? _haveEnglishExtra;

        private static OcrEngine CreateEngine(bool english)
        {
            OcrEngine e = null;
            if (!english) { try { e = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { } }
            if (e == null) { try { e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); } catch { } }
            return e;
        }

        /// <summary>Is there an English engine that differs from the user's own (worth a second opinion)?</summary>
        private static bool EnglishExtra()
        {
            if (_haveEnglishExtra.HasValue) return _haveEnglishExtra.Value;
            try
            {
                var u = CreateEngine(false);
                _userTag = u?.RecognizerLanguage?.LanguageTag ?? "";
                bool en = OcrEngine.IsLanguageSupported(new Windows.Globalization.Language("en-US"));
                _haveEnglishExtra = en && !_userTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            }
            catch { _haveEnglishExtra = false; }
            return _haveEnglishExtra.Value;
        }

        // ---------------- Whole screen (when the overlay opens) ----------------

        /// <summary>The first look at the whole screen: colour and norm passes in parallel, merged.</summary>
        public static Task<Result> RecognizeAsync(FrameSource src, int dpi)
        {
            double scale = dpi >= 120 ? 1.0 : 1.5; // small text on 100% screens reads better slightly enlarged
            return Task.Run(async () =>
            {
                var r = new Result { Scale = scale };
                var t0 = DateTime.UtcNow;
                try
                {
                    var probe = CreateEngine(false);
                    if (probe == null) { r.Problem = NoLanguage; return r; }
                    r.Language = probe.RecognizerLanguage?.LanguageTag ?? "";
                    var full = new Rectangle(0, 0, src.W, src.H);
                    var passes = await Task.WhenAll(
                        Task.Run(() => RunPass(src, full, scale, Variant.Colour, false, "full-colour", 0)),
                        Task.Run(() => RunPass(src, full, scale, Variant.Norm, false, "full-norm", 1)),
                        Task.Run(() => TiledPass(src, full, scale, Variant.Norm, false, "full-norm-tiles", 2, TileWidth(dpi)))).ConfigureAwait(false);
                    r.Passes.AddRange(passes.Where(p => p != null));
                    Rebuild(r);
                }
                catch (Exception e) { Fail(r, e); }
                r.Ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                return r;
            });
        }

        /// <summary>A slower, sharper look at the whole screen (norm, 2x, tiled) for after the first result.</summary>
        public static Task<OcrPass> EnhanceAsync(FrameSource src, int dpi)
        {
            double scale = dpi >= 120 ? 1.5 : 2.0;
            return Task.Run(() => TiledPass(src, new Rectangle(0, 0, src.W, src.H), scale, Variant.Norm, false, "full-norm-tiles-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x", 3, TileWidth(dpi)));
        }

        // ---------------- A selection or a tap ----------------

        /// <summary>
        /// Passes over one part of the screen (a selection plus a margin, or the patch around a tap), started in
        /// parallel; each task finishes on its own so the caller can use whatever is ready in time.
        /// </summary>
        /// <summary>
        /// Windows OCR drops words on long mixed-font lines (a green monospaced token in a line of capitals was missed
        /// by every whole-line pass and found by every pass that saw only part of the line), so some passes look at
        /// overlapping vertical slices this wide (480 px at 100%).
        /// </summary>
        public static int TileWidth(int dpi) => (int)Math.Round(480 * Math.Max(96, dpi) / 96.0);

        public static List<Task<OcrPass>> RegionPasses(FrameSource src, Box area, int dpi = 96)
        {
            var rect = Rectangle.Intersect(new Rectangle((int)Math.Floor(area.X), (int)Math.Floor(area.Y), (int)Math.Ceiling(area.W) + 1, (int)Math.Ceiling(area.H) + 1),
                                           new Rectangle(0, 0, src.W, src.H));
            var tasks = new List<Task<OcrPass>>();
            if (rect.Width < 4 || rect.Height < 4) return tasks;
            // Keep each pass under ~3 Mpx so a selection reads fast; big boxes rely on the whole-screen passes.
            double px = (double)rect.Width * rect.Height;
            double s2 = Math.Max(1.25, Math.Min(2.0, Math.Sqrt(3.0e6 / px)));
            double s3 = Math.Max(1.25, Math.Min(3.0, Math.Sqrt(3.0e6 / px)));
            tasks.Add(Task.Run(() => RunPass(src, rect, s2, Variant.Norm, false, "region-norm-2x", 4)));
            tasks.Add(Task.Run(() => RunPass(src, rect, s2, Variant.Colour, false, "region-colour-2x", 3)));
            if (s3 > s2 + 0.4) tasks.Add(Task.Run(() => RunPass(src, rect, s3, Variant.Norm, false, "region-norm-3x", 5)));
            tasks.Add(Task.Run(() => RunPass(src, rect, s2, Variant.Bin, false, "region-bin-2x", 2)));
            int tile = TileWidth(dpi);
            if (rect.Width > tile * 0.7)
            {
                int t2 = Math.Max(160, rect.Width / 2 + 60); // narrower slices than the box, so long lines are always cut
                int tw = Math.Min(tile, t2);
                tasks.Add(Task.Run(() => TiledPass(src, rect, s2, Variant.Norm, false, "region-norm-2x-tiles", 4, tw)));
                tasks.Add(Task.Run(() => TiledPass(src, rect, s2, Variant.Colour, false, "region-colour-2x-tiles", 3, tw)));
            }
            if (EnglishExtra()) tasks.Add(Task.Run(() => RunPass(src, rect, s2, Variant.Norm, true, "region-norm-2x-en", 3)));
            return tasks;
        }

        /// <summary>Merges all passes into the result's lines and page layout.</summary>
        public static void Rebuild(Result r)
        {
            r.Layout = OcrMerge.Merge(r.Passes);
            r.Page = PageLayout.Analyze(r.Layout); // lines, columns and blocks in reading order
        }

        /// <summary>The v12 recogniser: one colour pass (kept only for the self-test's before/after recall).</summary>
        public static Task<Result> LegacyAsync(FrameSource src, int dpi)
        {
            double scale = dpi >= 120 ? 1.0 : 1.5;
            return Task.Run(() =>
            {
                var r = new Result { Scale = scale };
                var p = RunPass(src, new Rectangle(0, 0, src.W, src.H), scale, Variant.Colour, false, "legacy", 0);
                if (p != null)
                {
                    foreach (var l in p.Lines) r.Layout.AddLine(l);
                    r.Page = PageLayout.Analyze(r.Layout);
                    r.Passes.Add(p);
                }
                return r;
            });
        }

        /// <summary>
        /// Self-test only (CI artifacts, never the log): every pass and the merged page with words and boxes.
        /// Written only when RUNNER_TEMP is set (GitHub Actions) and a self-test asks for it.
        /// </summary>
        public static void DebugDump(Result r, string name)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("RUNNER_TEMP");
                if (string.IsNullOrEmpty(root) || r == null) return;
                var dir = System.IO.Path.Combine(root, "rapcha-shots", "ocr-debug");
                System.IO.Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                string B(Box b) => ((int)b.X) + "," + ((int)b.Y) + " " + ((int)b.W) + "x" + ((int)b.H);
                foreach (var p in r.Passes)
                {
                    sb.AppendLine("== pass " + p.Name + " area=" + B(p.Area) + " ms=" + p.Ms + " words=" + p.WordCount);
                    foreach (var l in p.Lines) sb.AppendLine("  " + string.Join(" ", l.Select(w => w.Text + "[" + B(w.Box) + "]")));
                }
                sb.AppendLine("== merged page lines");
                foreach (var l in r.Page.Lines) sb.AppendLine("  #" + l.Id + " " + B(l.Box) + " | " + string.Join(" ", l.Words.Select(w => w.Text + "[" + B(w.Box) + "]")));
                sb.AppendLine("== blocks");
                foreach (var b in r.Page.Blocks) sb.AppendLine("  block " + b.Order + " " + B(b.Box) + " lines=" + string.Join(",", b.Lines.Select(l => l.Id)));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name + ".txt"), sb.ToString());
            }
            catch { }
        }

        private static void Fail(Result r, Exception e)
        {
            r.Problem = e is COMException || e is TypeLoadException || e is PlatformNotSupportedException
                ? "Windows text recognition isn't available on this PC." : "Couldn't read the screen (" + e.GetType().Name + ").";
            AppStorage.Log("OCR failed: " + e.GetType().Name);
        }

        /// <summary>
        /// One variant run over overlapping vertical slices of the area (overlap = 40% of a slice, so any word up to
        /// that wide is whole in at least one slice); words touching a cut edge are left to the neighbouring slice.
        /// </summary>
        public static OcrPass TiledPass(FrameSource src, Rectangle rect, double scale, Variant v, bool english, string name, int priority, int tileW)
        {
            var t0 = DateTime.UtcNow;
            var pass = new OcrPass { Name = name, Priority = priority, Area = new Box(rect.X, rect.Y, rect.Width, rect.Height) };
            if (tileW <= 0 || rect.Width <= tileW) { var one = RunPass(src, rect, scale, v, english, name, priority); return one; }
            int overlap = (int)(tileW * 0.4), step = Math.Max(1, tileW - overlap);
            for (int x = rect.X; ; x += step)
            {
                int w = Math.Min(tileW, rect.Right - x);
                var sub = new Rectangle(x, rect.Y, w, rect.Height);
                var p = RunPass(src, sub, scale, v, english, name, priority);
                if (p != null)
                {
                    double cutL = x > rect.X ? x + 2 : double.MinValue, cutR = x + w < rect.Right ? x + w - 2 : double.MaxValue;
                    foreach (var line in p.Lines)
                    {
                        var keep = line.Where(word => word.Box.X > cutL && word.Box.Right < cutR).ToList();
                        if (keep.Count > 0) pass.Lines.Add(keep);
                    }
                }
                if (x + w >= rect.Right) break;
            }
            pass.Ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            return pass;
        }

        // ---------------- One pass ----------------

        public enum Variant { Colour, Norm, Bin }

        /// <summary>Crops, enlarges and prepares one copy, runs OCR (tiled if needed); words in screen pixels.</summary>
        public static OcrPass RunPass(FrameSource src, Rectangle rect, double scale, Variant v, bool english, string name, int priority)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                var engine = CreateEngine(english);
                if (engine == null) return null;
                var crop = Crop(src, rect);
                int w = rect.Width, h = rect.Height;
                byte[] img = crop;
                int sw = w, sh = h;
                if (Math.Abs(scale - 1) > 0.01) img = Scale(crop, w, h, scale, out sw, out sh);
                double sx = (double)sw / w, sy = (double)sh / h;
                if (v != Variant.Colour) img = Normalize(img, sw, sh, Math.Max(16, (int)(24 * scale)));
                if (v == Variant.Bin) img = Binarize(img, sw, sh, Math.Max(15, (int)(14 * scale)) | 1);

                var pass = new OcrPass { Name = name, Priority = priority, Area = new Box(rect.X, rect.Y, rect.Width, rect.Height) };
                int max = (int)OcrEngine.MaxImageDimension;
                const int Overlap = 160;
                for (int ty = 0; ty < sh; ty += max - Overlap)
                {
                    int th = Math.Min(max, sh - ty);
                    for (int tx = 0; tx < sw; tx += max - Overlap)
                    {
                        int tw = Math.Min(max, sw - tx);
                        var tile = (tx == 0 && ty == 0 && tw == sw && th == sh) ? img : Crop(img, sw, new Rectangle(tx, ty, tw, th));
                        using (var bmp = SoftwareBitmap.CreateCopyFromBuffer(tile.AsBuffer(), BitmapPixelFormat.Bgra8, tw, th, BitmapAlphaMode.Premultiplied))
                        {
                            var ocr = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
                            foreach (var line in ocr.Lines)
                            {
                                var words = new List<OcrWord>();
                                foreach (var word in line.Words)
                                {
                                    var b = word.BoundingRect;
                                    words.Add(new OcrWord(word.Text, new Box(rect.X + (tx + b.X) / sx, rect.Y + (ty + b.Y) / sy, b.Width / sx, b.Height / sy)));
                                }
                                if (words.Count > 0) pass.Lines.Add(words);
                            }
                        }
                        if (tx + tw >= sw) break;
                    }
                    if (ty + th >= sh) break;
                }
                pass.Ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                return pass;
            }
            catch (Exception e)
            {
                AppStorage.Log("OCR pass " + name + " failed: " + e.GetType().Name);
                return null;
            }
        }

        // ---------------- Image preparation (BGRA byte arrays) ----------------

        private static byte[] Crop(FrameSource src, Rectangle r) => Crop(src.Bgra, src.W, r);

        private static byte[] Crop(byte[] bgra, int stridePx, Rectangle r)
        {
            var o = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++)
                Buffer.BlockCopy(bgra, ((r.Y + y) * stridePx + r.X) * 4, o, y * r.Width * 4, r.Width * 4);
            return o;
        }

        private static byte[] Scale(byte[] bgra, int w, int h, double scale, out int sw, out int sh)
        {
            sw = Math.Max(1, (int)Math.Round(w * scale));
            sh = Math.Max(1, (int)Math.Round(h * scale));
            using (var src = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
                src.UnlockBits(d);
                using (var dst = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(dst))
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // no dark fringe at the edges
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.DrawImage(src, new Rectangle(0, 0, sw, sh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                    }
                    var o = new byte[sw * sh * 4];
                    var dd = dst.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < sh; y++) Marshal.Copy(dd.Scan0 + y * dd.Stride, o, y * sw * 4, sw * 4);
                    dst.UnlockBits(dd);
                    for (int i = 3; i < o.Length; i += 4) o[i] = 255;
                    return o;
                }
            }
        }

        /// <summary>
        /// Grey image with dark text on a light background everywhere: per block (3x3 neighbourhood mean of the
        /// luminance), dark areas use 255 - max(R,G,B) and light areas use min(R,G,B); then a 1%-99% contrast stretch.
        /// </summary>
        public static byte[] Normalize(byte[] bgra, int w, int h, int block)
        {
            int bw = (w + block - 1) / block, bh = (h + block - 1) / block;
            var sum = new double[bw * bh];
            var cnt = new int[bw * bh];
            for (int y = 0; y < h; y++)
            {
                int row = (y / block) * bw;
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    double l = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                    sum[row + x / block] += l; cnt[row + x / block]++;
                }
            }
            var dark = new bool[bw * bh];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    double s = 0; int c = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = bx + dx, y = by + dy;
                            if (x < 0 || y < 0 || x >= bw || y >= bh) continue;
                            s += sum[y * bw + x]; c += cnt[y * bw + x];
                        }
                    dark[by * bw + bx] = c > 0 && s / c < 110;
                }
            var g = new byte[w * h];
            var hist = new int[256];
            for (int y = 0; y < h; y++)
            {
                int row = (y / block) * bw;
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte b = bgra[i], gg = bgra[i + 1], r = bgra[i + 2];
                    byte v = dark[row + x / block] ? (byte)(255 - Math.Max(r, Math.Max(gg, b))) : Math.Min(r, Math.Min(gg, b));
                    g[y * w + x] = v; hist[v]++;
                }
            }
            int total = w * h, lo = 0, hi = 255, acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= total * 0.01) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= total * 0.01) { hi = i; break; } }
            if (hi - lo < 16) { lo = 0; hi = 255; }
            var lut = new byte[256];
            for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Max(0, Math.Min(255, (i - lo) * 255 / Math.Max(1, hi - lo)));
            var o = new byte[w * h * 4];
            for (int p = 0, i = 0; p < g.Length; p++, i += 4) { byte v = lut[g[p]]; o[i] = o[i + 1] = o[i + 2] = v; o[i + 3] = 255; }
            return o;
        }

        /// <summary>Black where a pixel is clearly darker than its neighbourhood mean (integral image), else white.</summary>
        public static byte[] Binarize(byte[] grayBgra, int w, int h, int win)
        {
            var ii = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += grayBgra[(y * w + x) * 4];
                    ii[(y + 1) * (w + 1) + x + 1] = ii[y * (w + 1) + x + 1] + row;
                }
            }
            int r = win / 2;
            var o = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                    long s = ii[y1 * (w + 1) + x1] - ii[y0 * (w + 1) + x1] - ii[y1 * (w + 1) + x0] + ii[y0 * (w + 1) + x0];
                    double mean = (double)s / ((x1 - x0) * (y1 - y0));
                    int i = (y * w + x) * 4;
                    byte v = grayBgra[i] < mean - 12 ? (byte)0 : (byte)255;
                    o[i] = o[i + 1] = o[i + 2] = v; o[i + 3] = 255;
                }
            }
            return o;
        }
    }
}
