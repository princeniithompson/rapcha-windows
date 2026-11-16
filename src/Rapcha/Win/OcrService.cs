using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Rapcha.Core;
using OcrWord = Rapcha.Core.OcrWord;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Rapcha.Win
{
    /// <summary>
    /// Offline text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr).
    /// Nothing is downloaded. Uses the user's profile languages, else English; if no OCR language is
    /// installed, says so in plain words. Coordinates come back in the screenshot's own pixels.
    /// </summary>
    public static class OcrService
    {
        public sealed class Result
        {
            public OcrLayout Layout = new OcrLayout();
            public PageLayout Page = new PageLayout();
            public string Problem = "";
            public string Language = "";
            public int Ms;
            public double Scale = 1;
        }

        public const string NoLanguage = "Windows text recognition isn't available for your language. Add English in Settings > Time & language > Language & region, then try again.";

        /// <summary>Runs OCR on a copy of the screenshot on a background thread.</summary>
        public static Task<Result> RecognizeAsync(Bitmap screenshot, int dpi)
        {
            // Small text on 100% screens reads better slightly enlarged.
            double scale = dpi >= 120 ? 1.0 : 1.5;
            byte[] pixels;
            int sw, sh;
            using (var src = Scaled(screenshot, scale, out sw, out sh))
                pixels = Bgra(src);
            return Task.Run(async () =>
            {
                var r = new Result { Scale = scale };
                var t0 = DateTime.UtcNow;
                try
                {
                    OcrEngine engine = null;
                    try { engine = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
                    if (engine == null)
                    {
                        try { engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); } catch { }
                    }
                    if (engine == null) { r.Problem = NoLanguage; return r; }
                    r.Language = engine.RecognizerLanguage?.LanguageTag ?? "";
                    uint max = OcrEngine.MaxImageDimension;
                    if (sw > max || sh > max) { r.Problem = "The screen is too large to read in one go."; return r; }

                    using (var bmp = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, sw, sh, BitmapAlphaMode.Premultiplied))
                    {
                        pixels = null;
                        var ocr = await engine.RecognizeAsync(bmp).AsTask().ConfigureAwait(false);
                        foreach (var line in ocr.Lines)
                        {
                            var words = new System.Collections.Generic.List<OcrWord>();
                            foreach (var word in line.Words)
                            {
                                var b = word.BoundingRect;
                                words.Add(new OcrWord(word.Text, new Box(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale)));
                            }
                            r.Layout.AddLine(words);
                        }
                        r.Page = PageLayout.Analyze(r.Layout); // lines, columns and blocks in reading order
                    }
                }
                catch (Exception e)
                {
                    r.Problem = e is COMException || e is TypeLoadException || e is PlatformNotSupportedException
                        ? "Windows text recognition isn't available on this PC." : "Couldn't read the screen (" + e.GetType().Name + ").";
                    AppStorage.Log("OCR failed: " + e.GetType().Name + " " + e.Message);
                }
                r.Ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                return r;
            });
        }

        private static Bitmap Scaled(Bitmap src, double scale, out int w, out int h)
        {
            w = (int)Math.Round(src.Width * scale);
            h = (int)Math.Round(src.Height * scale);
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = scale == 1.0 ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        private static byte[] Bgra(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[b.Width * b.Height * 4];
                for (int y = 0; y < b.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * b.Width * 4, b.Width * 4);
                for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255; // opaque
                return bytes;
            }
            finally { b.UnlockBits(data); }
        }
    }
}
