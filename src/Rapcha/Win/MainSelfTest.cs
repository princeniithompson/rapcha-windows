using System;
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
    /// CI self-test for the main window (--selftest-main outDir): history save/load and word count, Home grouping,
    /// Copy (a real click) with its toast, Delete, Vocabulary add/remove persisting, Glow colour persisting and
    /// drawn on a real rounded field outline, the window disposed on close. Saves PNG screenshots to outDir.
    /// The user's own files are backed up first and put back afterwards.
    /// Exit codes: 0 ok, 21 history, 22 Home page, 23 copy, 24 delete, 25 vocabulary, 26 glow colour saved,
    /// 27 outline colour/shape, 28 close/dispose, 1 crash.
    /// </summary>
    internal sealed class MainSelfTest : ApplicationContext
    {
        private readonly string _out;
        private readonly string[] _files = { "history.json", "vocabulary.txt", "glow.txt" };
        private readonly byte[][] _backup;

        public MainSelfTest(string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            _backup = _files.Select(f => { var p = Path.Combine(AppStorage.Folder, f); return File.Exists(p) ? File.ReadAllBytes(p) : null; }).ToArray();
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Run(); };
            t.Start();
        }

        private static void Log(string s) => AppStorage.Log("Selftest main: " + s);

        private async void Run()
        {
            int code;
            try { code = await Steps(); }
            catch (Exception e) { Log("crash " + e); code = 1; }
            Restore();
            Log("exit=" + code);
            Environment.ExitCode = code;
            ExitThread();
        }

        private void Restore()
        {
            for (int i = 0; i < _files.Length; i++)
            {
                var p = Path.Combine(AppStorage.Folder, _files[i]);
                try { if (_backup[i] != null) File.WriteAllBytes(p, _backup[i]); else if (File.Exists(p)) File.Delete(p); } catch { }
            }
            AppStorage.ReloadGlowColor();
        }

        private static readonly string[] Samples =
        {
            "Remind me to send the invoice to Ama before Friday, and copy Kofi on the email.",
            "The meeting has moved to Thursday at 3 pm. Please bring the printed slides and the budget sheet.",
            "Rapcha now keeps everything you dictate on the Home page, grouped by day, so nothing gets lost.",
            "Buy kelewele, plantain chips and two bottles of water on the way home.",
            "Prince Nii will review the design notes tonight and send comments in the morning.",
            "Thank you for the warm welcome in Accra. It was a pleasure meeting the whole team.",
        };

        private async Task<int> Steps()
        {
            // 1. History file: save, load, word count.
            var today = DateTime.Today;
            var h = new TranscriptHistory();
            h.Add(Samples[5], today.AddDays(-3).AddHours(12).ToUniversalTime());
            h.Add(Samples[4], today.AddDays(-1).AddHours(16.3).ToUniversalTime());
            h.Add(Samples[3], today.AddDays(-1).AddHours(18.75).ToUniversalTime());
            h.Add(Samples[2], today.AddHours(8.1).ToUniversalTime());
            h.Add(Samples[1], today.AddHours(9.7).ToUniversalTime());
            h.Add(Samples[0], today.AddHours(11.25).ToUniversalTime());
            int words = Samples.Sum(TranscriptHistory.CountWords);
            AppStorage.SaveHistory(h);
            var back = AppStorage.LoadHistory();
            bool json = File.ReadAllText(Path.Combine(AppStorage.Folder, "history.json")).Contains("\"items\"");
            Log("history saved=" + back.Items.Count + " words=" + back.TotalWords + " expected_words=" + words + " json=" + json + " newest_first=" + (back.Items[0].Text == Samples[0]));
            if (back.Items.Count != 6 || back.TotalWords != words || !json || back.Items[0].Text != Samples[0]) return 21;

            // 2. Home page.
            var w = new MainWindow(MainWindow.Page.Home);
            w.Show();
            w.BringUp();
            await Task.Delay(1200);
            var found = Native.FindWindow(null, MainWindow.WindowTitle);
            var heads = w.Home.Headings;
            string third = today.AddDays(-3).ToString(today.AddDays(-3).Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            bool headsOk = heads.Length == 3 && heads[0] == "Today" && heads[1] == "Yesterday" && heads[2] == third;
            Log("home visible=" + w.Visible + " found_by_title=" + (found == w.Handle) + " shown_words=" + w.Home.ShownTotalWords + " headings=[" + string.Join(" | ", heads) + "] cards=" + w.Home.ItemCount);
            if (!w.Visible || found != w.Handle || w.Home.ShownTotalWords != words || !headsOk || w.Home.ItemCount != 6) return 22;
            Shot(w, "home.png");

            // 3. Copy (a real click on the first card's Copy button).
            try { Clipboard.Clear(); } catch { }
            var first = w.Home.ItemAt(0);
            var copyRect = w.Home.ButtonScreenRect(0, delete: false);
            RapchaApp.SendClick(new Point(copyRect.X + copyRect.Width / 2, copyRect.Y + copyRect.Height / 2));
            await Task.Delay(500);
            string clip = "";
            try { clip = Clipboard.GetText(); } catch { }
            bool copied = clip == first.Text, toast = w.Home.ToastVisible && w.Home.ToastText == "Copied";
            Log("copy clipboard_matches=" + copied + " toast=" + toast);
            if (toast) Shot(w, "home-copied.png");
            if (!copied || !toast) return 23;
            await Task.Delay(1800); // toast gone

            // 4. Delete the first card (real click).
            var delRect = w.Home.ButtonScreenRect(0, delete: true);
            RapchaApp.SendClick(new Point(delRect.X + delRect.Width / 2, delRect.Y + delRect.Height / 2));
            await Task.Delay(500);
            var afterDel = AppStorage.LoadHistory();
            bool deleted = afterDel.Items.Count == 5 && afterDel.Items.All(t => t.Id != first.Id) && w.Home.ItemCount == 5 &&
                           w.Home.ShownTotalWords == words - TranscriptHistory.CountWords(first.Text);
            Log("delete saved=" + afterDel.Items.Count + " shown=" + w.Home.ItemCount + " words=" + w.Home.ShownTotalWords + " ok=" + deleted);
            if (!deleted) return 24;

            // 5. Vocabulary: add (typed + Add button) and remove, persisted in the store voice typing reads.
            try { File.Delete(Path.Combine(AppStorage.Folder, "vocabulary.txt")); } catch { }
            w.ShowPage(MainWindow.Page.Vocabulary);
            await Task.Delay(300);
            w.Vocabulary.Input.Text = "Prince Nii";
            w.Vocabulary.AddButton.PerformClick();
            foreach (var t in new[] { "Kelewele", "Osu Castle", "AI Studio", "Kwame Nkrumah", "Rapcha", "Adwoa Mensah", "Labadi Beach" }) w.Vocabulary.Add(t);
            bool dup = w.Vocabulary.Add("prince nii");
            bool removed = w.Vocabulary.Remove("Osu Castle");
            var stored = AppStorage.LoadVocabulary();
            bool vocabOk = !dup && removed && stored.Contains("Prince Nii") && stored.Contains("Kelewele") && !stored.Contains("Osu Castle") && stored.Count == 7 &&
                           w.Vocabulary.Input.Text.Length == 0;
            Log("vocabulary stored=" + stored.Count + " duplicate_rejected=" + !dup + " removed=" + removed + " ok=" + vocabOk);
            await Task.Delay(300);
            Shot(w, "vocabulary.png");
            if (!vocabOk) return 25;

            // 6. Glow colour: choose Pink, it is saved and read back by a fresh Settings page.
            w.ShowPage(MainWindow.Page.Settings);
            await Task.Delay(400);
            int pink = GlowColors.Presets.First(p => p.Key == "Pink").Value;
            w.Settings.Glow.ChoosePreset("Pink");
            AppStorage.ReloadGlowColor();
            int savedGlow = AppStorage.LoadGlowColor();
            string fileText = File.ReadAllText(Path.Combine(AppStorage.Folder, "glow.txt")).Trim();
            int reread;
            using (var fresh = new SettingsPage()) reread = fresh.Glow.Selected;
            Log("glow saved=" + GlowColors.Format(savedGlow) + " file=" + fileText + " fresh_page=" + GlowColors.Format(reread));
            Shot(w, "settings.png");
            if (savedGlow != pink || fileText != "#F0559B" || reread != pink) return 26;

            // 7. The field outline uses that colour, with rounded corners: outline the Vocabulary text box.
            w.ShowPage(MainWindow.Page.Vocabulary);
            w.Vocabulary.Input.Focus();
            await Task.Delay(500);
            using (var fi = new FieldIndicator())
            {
                fi.Color = GlowPicker.FromRgb(AppStorage.LoadGlowColor());   // what RapchaApp does on GlowChanged
                var box = w.Vocabulary.Input.RectangleToScreen(w.Vocabulary.Input.ClientRectangle);
                fi.Glow.Show(box);   // Rapcha's own windows are never a paste target; detection is covered by the v11 self-test
                await Task.Delay(400);
                var field = fi.Glow.Field;
                int cx = field.X + field.Width / 2;
                var line = fi.Glow.PixelAt(cx, field.Top - 2);
                var corner = fi.Glow.PixelAt(field.Left - 2, field.Top - 2);   // where a square outline's corner would be
                var glowOut = fi.Glow.PixelAt(cx, field.Top - 6);
                double best = double.MaxValue;
                for (double k = 0.0; k <= 0.5; k += 0.01)
                {
                    double r = 240 + (255 - 240) * k, g = 0x55 + (255 - 0x55) * k, b = 0x9B + (255 - 0x9B) * k;
                    best = Math.Min(best, Math.Sqrt((line.R - r) * (line.R - r) + (line.G - g) * (line.G - g) + (line.B - b) * (line.B - b)));
                }
                int dpi = Native.DpiAt(cx, field.Y);
                int rMin = (int)Math.Floor(8 * dpi / 96.0), rMax = (int)Math.Ceiling(12 * dpi / 96.0);
                bool colourOk = line.A > 180 && best < 24;
                bool rounded = corner.A < line.A * 0.7 && fi.Glow.RadiusPx >= rMin && fi.Glow.RadiusPx <= rMax;
                bool soft = glowOut.A > 10 && glowOut.A < line.A;
                Log("outline line=" + line + " colour_distance=" + best.ToString("0.0") + " corner_alpha=" + corner.A + " radius_px=" + fi.Glow.RadiusPx +
                    " soft_glow_alpha=" + glowOut.A + " colour_ok=" + colourOk + " rounded=" + rounded + " soft=" + soft);
                ShotScreen(Rectangle.Inflate(field, 120, 60), "outline.png");
                fi.Hide();
                if (!colourOk || !rounded || !soft) return 27;
            }

            // 8. Close: the window is disposed (the app keeps running in real use).
            w.Close();
            await Task.Delay(300);
            bool gone = w.IsDisposed && Native.FindWindow(null, MainWindow.WindowTitle) == IntPtr.Zero;
            Log("closed disposed=" + w.IsDisposed + " gone=" + gone);
            if (!gone) return 28;
            Log("ok=True");
            return 0;
        }

        private void Shot(Form w, string name)
        {
            try
            {
                w.BringUp2();
                Application.DoEvents();
                ShotScreen(w.Bounds, name);
                using (var bmp = new Bitmap(w.Width, w.Height, PixelFormat.Format32bppArgb))
                {
                    w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
                    bmp.Save(Path.Combine(_out, Path.GetFileNameWithoutExtension(name) + "-render.png"), ImageFormat.Png);
                }
            }
            catch (Exception e) { Log("screenshot " + name + " failed: " + e.Message); }
        }

        private void ShotScreen(Rectangle r, string name)
        {
            try
            {
                r.Intersect(SystemInformation.VirtualScreen);
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
                Log("screenshot " + name + " " + r.Width + "x" + r.Height);
            }
            catch (Exception e) { Log("screenshot " + name + " failed: " + e.Message); }
        }
    }

    internal static class FormExtensions
    {
        public static void BringUp2(this Form f)
        {
            if (f is MainWindow m) m.BringUp(); else f.Activate();
        }
    }
}
