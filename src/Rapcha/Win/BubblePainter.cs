using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Draws the Words bubble (also offscreen, for the CI screenshots). Layout comes from Core/BubbleLayout.</summary>
    public sealed class BubblePainter
    {
        public BubbleLayout Layout { get; private set; } = BubbleLayout.Compute(96, 1.0);
        public int Dpi { get; private set; } = 96;
        public double TextScale { get; private set; } = 1.0;
        public WordsBubble.Mode Mode { get; private set; } = WordsBubble.Mode.Listening;
        public string Text { get; private set; } = "";
        public bool MicHint;
        public bool? Field;
        public Rectangle CancelRect, CompleteRect, FixRect, IconRect;
        /// <summary>The small recording timer ("1:05", then "0:19 left") and the "Offline" tag in the bottom row's gaps.</summary>
        public string Timer = "";
        public bool TimerCountdown;
        public bool Offline;
        /// <summary>v21: offline Windows voice typing - a real text box sits over the words area, so it is left empty here.</summary>
        public bool EditorMode;
        public float TextPx => Layout.TextPx;
        public static Color CardColor => Accent.Mix(Color.FromArgb(24, 27, 24), Accent.Color, 0.06);
        public Rectangle TimerRect, OfflineRect, TextRect;
        public int LineCount { get; private set; }       // wrapped lines of the current text
        public int VisibleLines { get; private set; }    // lines fully visible in the text area
        public float ButtonPx { get; private set; }
        private float _scroll, _scrollTarget;            // in lines
        private readonly DateTime _born = DateTime.UtcNow;

        public void Setup(int dpi, double textScale)
        {
            Dpi = dpi; TextScale = textScale;
            Layout = BubbleLayout.Compute(dpi, textScale);
        }

        public void SetMode(WordsBubble.Mode mode, string text)
        {
            Mode = mode;
            Text = (text ?? "").Trim();
            _scroll = _scrollTarget = -1; // jump to the end
        }

        public void SetText(string text) => Text = (text ?? "").Trim();

        /// <summary>Animation step: eases the auto-scroll toward the newest line.</summary>
        public void Step()
        {
            if (_scroll < 0) return;
            _scroll += (_scrollTarget - _scroll) * 0.35f;
            if (Math.Abs(_scrollTarget - _scroll) < 0.01f) _scroll = _scrollTarget;
        }

        /// <summary>Windows "Make text bigger" (Settings → Accessibility → Text size), 1.0 – 2.25.</summary>
        public static double ReadTextScale()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility"))
                {
                    var v = k?.GetValue("TextScaleFactor");
                    if (v is int i && i >= 100 && i <= 225) return i / 100.0;
                }
            }
            catch { }
            return 1.0;
        }

        private int S(double dip) => (int)Math.Round(dip * Layout.Scale);

        public Bitmap Render()
        {
            var L = Layout;
            var bmp = new Bitmap(L.Width, L.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Draw(g);
            }
            return bmp;
        }

        private void Draw(Graphics g)
        {
            var L = Layout;
            var accent = Accent.Color;
            double t = (DateTime.UtcNow - _born).TotalSeconds;
            bool live = Mode == WordsBubble.Mode.Listening || Mode == WordsBubble.Mode.Polishing;
            var card = new RectangleF(0.5f, 0.5f, L.Width - 1.5f, L.Height - 1.5f);
            using (var path = Theme.Round(card, L.Corner))
            {
                // A dark card, faintly tinted with the glow colour.
                using (var bg = new SolidBrush(Color.FromArgb(246, Accent.Mix(Color.FromArgb(24, 27, 24), accent, 0.06)))) g.FillPath(bg, path);
                if (live)
                {
                    double period = Mode == WordsBubble.Mode.Listening ? 2.6 : 1.3;
                    double breath = 0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / period); // eased 0..1
                    var glowRect = new RectangleF(0, L.Height * 0.42f, L.Width, L.Height * 0.58f + 1);
                    g.SetClip(path);
                    using (var glow = new LinearGradientBrush(glowRect, Color.FromArgb(0, accent), Color.FromArgb((int)(70 + 90 * breath), accent), LinearGradientMode.Vertical))
                        g.FillRectangle(glow, glowRect);
                    g.ResetClip();
                }
                using (var border = new Pen(live ? Color.FromArgb(150, Accent.Lighter(0.2)) : Color.FromArgb(70, 255, 255, 255), Math.Max(1f, S(1.2))))
                    g.DrawPath(border, path);
            }

            CancelRect = CompleteRect = FixRect = IconRect = TimerRect = OfflineRect = Rectangle.Empty;
            var textRect = new Rectangle(L.PadX, L.TextTop, L.Width - L.PadX * 2, L.TextHeight);
            TextRect = textRect;
            using (var font = new Font(Theme.Sans, L.TextPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font(Theme.Sans, L.TextPx, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                switch (Mode)
                {
                    case WordsBubble.Mode.Listening:
                        if (MicHint && Text.Length == 0)
                        {
                            DrawLines(g, new List<string> { "I can't hear your microphone" }, bold, Color.FromArgb(255, 200, 190), textRect, 0);
                            using (var link = new Font(Theme.Sans, L.TextPx, FontStyle.Bold | FontStyle.Underline, GraphicsUnit.Pixel))
                            {
                                var size = g.MeasureString("Fix microphone", link);
                                FixRect = new Rectangle(textRect.X, (int)(textRect.Y + L.LineHeight * 1.3f), (int)size.Width + S(4), (int)L.LineHeight + S(2));
                                using (var b = new SolidBrush(Color.FromArgb(150, 200, 255))) g.DrawString("Fix microphone", link, b, FixRect.X, FixRect.Y);
                            }
                        }
                        else if (EditorMode) { }
                        else if (Text.Length == 0)
                            DrawLines(g, new List<string> { "Listening… start talking" }, font, Color.FromArgb(170, 176, 166), textRect, 0);
                        else DrawLive(g, font, Color.White, textRect);
                        DrawRow(g, buttons: true);
                        break;
                    case WordsBubble.Mode.Polishing:
                        if (Text.Length > 0) DrawLive(g, font, Color.FromArgb(205, 210, 200), textRect);
                        else DrawLines(g, new List<string> { "Polishing…" }, font, Color.FromArgb(205, 210, 200), textRect, 0);
                        DrawRow(g, buttons: false);
                        break;
                    case WordsBubble.Mode.Final:
                        DrawLive(g, font, Color.White, textRect);
                        DrawRow(g, buttons: false, label: "");
                        break;
                    case WordsBubble.Mode.Done:
                        using (var big = new Font(Theme.Sans, L.TextPx * 1.15f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                        using (var b = new SolidBrush(Accent.Lighter(0.45)))
                            g.DrawString(Text, big, b, new RectangleF(L.PadX, 0, L.Width - 2 * L.PadX, L.Height), sf);
                        break;
                    case WordsBubble.Mode.Warning:
                        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                        using (var b = new SolidBrush(Color.FromArgb(255, 200, 190)))
                            g.DrawString(Text, bold, b, new RectangleF(L.PadX, S(8), L.Width - 2 * L.PadX, L.Height - S(16)), sf);
                        break;
                }
            }
        }

        // ---- text: own word wrap, tight line height, four lines, eased auto-scroll ----

        private static readonly StringFormat Typo = MakeTypo();
        private static StringFormat MakeTypo()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        public static List<string> Wrap(Graphics g, string text, Font f, float width)
        {
            var lines = new List<string>();
            var line = "";
            foreach (var word in (text ?? "").Replace("\r", "").Replace('\n', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var cand = line.Length == 0 ? word : line + " " + word;
                if (g.MeasureString(cand, f, PointF.Empty, Typo).Width <= width) { line = cand; continue; }
                if (line.Length > 0) lines.Add(line);
                // a single word wider than the line is split by characters
                var w = word;
                while (g.MeasureString(w, f, PointF.Empty, Typo).Width > width && w.Length > 1)
                {
                    int n = w.Length - 1;
                    while (n > 1 && g.MeasureString(w.Substring(0, n), f, PointF.Empty, Typo).Width > width) n--;
                    lines.Add(w.Substring(0, n));
                    w = w.Substring(n);
                }
                line = w;
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        private void DrawLive(Graphics g, Font f, Color c, Rectangle r)
        {
            var lines = Wrap(g, Text, f, r.Width);
            LineCount = lines.Count;
            float target = Math.Max(0, lines.Count - BubbleLayout.Lines);
            if (_scroll < 0) _scroll = target;
            _scrollTarget = target;
            DrawLines(g, lines, f, c, r, _scroll);
        }

        private void DrawLines(Graphics g, List<string> lines, Font f, Color c, Rectangle r, float scroll)
        {
            var L = Layout;
            float lh = L.LineHeight;
            // GDI+ puts the glyphs inside a 1.33 em box; centre them in our tighter line.
            float shift = (f.GetHeight(g) - lh) / 2f;
            var state = g.Save();
            g.SetClip(new RectangleF(r.X - 2, r.Y - 1, r.Width + 4, r.Height + 2));
            int visible = 0;
            using (var b = new SolidBrush(c))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    float y = r.Y + (i - scroll) * lh;
                    if (y + lh < r.Y - 1 || y > r.Bottom) continue;
                    // the line scrolling away fades out
                    int alpha = y < r.Y ? (int)Math.Max(0, 255 * (1 - (r.Y - y) / lh)) : 255;
                    if (y >= r.Y - 0.5f && y + lh <= r.Bottom + 0.5f) visible++;
                    using (var fb = new SolidBrush(Color.FromArgb(alpha * c.A / 255, c)))
                        g.DrawString(lines[i], f, fb, r.X, y - shift, Typo);
                }
            }
            g.Restore(state);
            VisibleLines = visible;
        }

        // ---- the bottom row: Cancel · keyboard icon · Complete ----

        private void DrawRow(Graphics g, bool buttons, string label = "Polishing…")
        {
            var L = Layout;
            int y = L.RowY, h = L.RowHeight, x0 = L.PadX - S(4), x1 = L.Width - L.PadX + S(4);
            float px = L.ButtonPx;
            Size cancel, complete;
            int iconW = (int)(h * 1.25f);
            while (true)
            {
                using (var f = new Font(Theme.Sans, px, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    cancel = Size.Ceiling(g.MeasureString("Cancel", f, PointF.Empty, Typo));
                    complete = Size.Ceiling(g.MeasureString("Complete", f, PointF.Empty, Typo));
                }
                int need = cancel.Width + S(24) + complete.Width + S(24) + (int)(px * 1.1f) + iconW + S(24);
                if (need <= x1 - x0 || px <= 8) break;
                px -= 0.5f; // never clip the buttons: shrink the label a little instead
            }
            ButtonPx = px;
            IconRect = new Rectangle((L.Width - iconW) / 2, y + (h - (int)(h * 0.86f)) / 2, iconW, (int)(h * 0.86f));
            DrawKeyboard(g, IconRect, Field);
            using (var f = new Font(Theme.Sans, px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                if (buttons)
                {
                    CancelRect = new Rectangle(x0, y, cancel.Width + S(24), h);
                    using (var p = Theme.Round(CancelRect, h / 2f))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(34, 255, 255, 255))) g.FillPath(b, p);
                        using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                    }
                    Centered(g, "Cancel", f, Color.FromArgb(255, 150, 140), CancelRect);

                    int checkW = (int)(px * 1.1f);
                    int cw = complete.Width + S(24) + checkW;
                    CompleteRect = new Rectangle(x1 - cw, y, cw, h);
                    var fill = Accent.Color;
                    var ink = Accent.TextOn(fill);
                    using (var p = Theme.Round(CompleteRect, h / 2f))
                    using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                    var textArea = new Rectangle(CompleteRect.X + S(12) + checkW, y, complete.Width + S(2), h);
                    float cy = y + h / 2f, cx = CompleteRect.X + S(12) + checkW * 0.45f, u = px * 0.32f;
                    using (var pen = new Pen(ink, Math.Max(1.5f, px * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(cx - u, cy), new PointF(cx - u * 0.25f, cy + u * 0.75f), new PointF(cx + u * 1.1f, cy - u * 0.8f) });
                    Centered(g, "Complete", f, ink, textArea);
                }
                else if (!string.IsNullOrEmpty(label))
                {
                    using (var b = new SolidBrush(Accent.Lighter(0.4)))
                    using (var sf = new StringFormat(Typo) { LineAlignment = StringAlignment.Center })
                        g.DrawString(label, f, b, new RectangleF(x0 + S(4), y, IconRect.X - x0, h), sf);
                }
            }
            DrawStatus(g, buttons, x0, x1, y, h, px);
        }

        /// <summary>
        /// The timer sits in the gap between Cancel and the keyboard icon, the "Offline" tag in the gap between the
        /// icon and Complete: small, muted, in the glow colour, never over the words or a button. Either one hides
        /// itself if the gap is too narrow (very large text).
        /// </summary>
        private void DrawStatus(Graphics g, bool buttons, int x0, int x1, int y, int h, float px)
        {
            int gap = S(6);
            int leftEdge = buttons ? CancelRect.Right + gap : -1;      // no buttons: the left gap holds "Polishing…"
            int leftEnd = IconRect.X - gap;
            int rightStart = IconRect.Right + gap;
            int rightEnd = (buttons ? CompleteRect.X : x1) - gap;
            float small = Math.Max(7f, px * 0.86f);
            if (buttons && !string.IsNullOrEmpty(Timer) && leftEdge > 0)
            {
                for (float sz = small; sz >= 6.5f && TimerRect.IsEmpty; sz -= 0.5f)
                    using (var f = new Font(Theme.Sans, sz, TimerCountdown ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        var size = Size.Ceiling(g.MeasureString(Timer, f, PointF.Empty, Typo));
                        if (size.Width > leftEnd - leftEdge) continue;
                        // centred in its gap, on the row's middle line
                        int x = leftEdge + (leftEnd - leftEdge - size.Width) / 2;
                        TimerRect = new Rectangle(x, y + (h - size.Height) / 2, size.Width, size.Height);
                        var c = TimerCountdown ? Accent.Lighter(0.35) : Color.FromArgb(175, Accent.Mix(Color.FromArgb(190, 196, 186), Accent.Color, 0.35));
                        using (var b = new SolidBrush(c)) g.DrawString(Timer, f, b, TimerRect.X, TimerRect.Y, Typo);
                    }
            }
            if (Offline)
            {
                const string tag = "Offline";
                for (float sz = small; sz >= 6.5f && OfflineRect.IsEmpty; sz -= 0.5f)
                    using (var f = new Font(Theme.Sans, sz, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        var size = Size.Ceiling(g.MeasureString(tag, f, PointF.Empty, Typo));
                        int w = size.Width + S(12), th = size.Height + S(3);
                        if (w > rightEnd - rightStart) continue;
                        int x = rightStart + (rightEnd - rightStart - w) / 2;
                        OfflineRect = new Rectangle(x, y + (h - th) / 2, w, th);
                        using (var path = Theme.Round(OfflineRect, th / 2f))
                        using (var pen = new Pen(Color.FromArgb(120, Accent.Lighter(0.3)), 1f))
                            g.DrawPath(pen, path);
                        using (var b = new SolidBrush(Color.FromArgb(200, Accent.Mix(Color.FromArgb(200, 205, 196), Accent.Color, 0.3))))
                        using (var sf = new StringFormat(Typo) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(tag, f, b, OfflineRect, sf);
                    }
            }
        }

        private static void Centered(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(Typo) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(s, f, b, r, sf);
        }

        /// <summary>A small vector keyboard; a check badge when a text field has focus, a slash when there is none.</summary>
        public static void DrawKeyboard(Graphics g, Rectangle r, bool? field)
        {
            float h = r.Height, kbH = h * 0.62f, kbW = Math.Min(r.Width * 0.82f, kbH * 1.6f);
            var kb = new RectangleF(r.X + (r.Width - kbW) / 2f - h * 0.08f, r.Y + (h - kbH) / 2f, kbW, kbH);
            var ink = field == null ? Color.FromArgb(150, 210, 214, 204) : Color.FromArgb(225, 230, 232, 224);
            float stroke = Math.Max(1.1f, h * 0.055f);
            using (var p = Theme.Round(kb, kbH * 0.2f))
            using (var pen = new Pen(ink, stroke)) g.DrawPath(pen, p);
            using (var b = new SolidBrush(ink))
            {
                float pad = kbH * 0.17f, gap = kbH * 0.08f;
                float keyH = (kbH - pad * 2 - gap * 2) / 3f;
                int cols = 6;
                float keyW = (kbW - pad * 2 - gap * (cols - 1)) / cols;
                for (int row = 0; row < 2; row++)
                    for (int c = 0; c < cols - row; c++)
                    {
                        float x = kb.X + pad + c * (keyW + gap) + row * keyW * 0.5f;
                        using (var kp = Theme.Round(new RectangleF(x, kb.Y + pad + row * (keyH + gap), keyW, keyH), keyH * 0.25f)) g.FillPath(b, kp);
                    }
                using (var sp = Theme.Round(new RectangleF(kb.X + pad + keyW + gap, kb.Y + pad + 2 * (keyH + gap), kbW - 2 * pad - 2 * (keyW + gap), keyH), keyH * 0.3f)) g.FillPath(b, sp);
            }
            if (field == null) return;
            float br = h * 0.25f;
            var badge = new RectangleF(kb.Right - br * 1.1f, kb.Bottom - br * 1.15f, br * 2, br * 2);
            if (field == false)
            {
                // strike through the keyboard
                using (var dark = new Pen(Color.FromArgb(230, 24, 27, 24), stroke * 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(dark, kb.X + kbW * 0.1f, kb.Bottom + kbH * 0.05f, kb.Right - kbW * 0.12f, kb.Y - kbH * 0.08f);
                using (var red = new Pen(Color.FromArgb(240, 240, 120, 108), stroke * 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(red, kb.X + kbW * 0.1f, kb.Bottom + kbH * 0.05f, kb.Right - kbW * 0.12f, kb.Y - kbH * 0.08f);
            }
            using (var ring = new SolidBrush(Color.FromArgb(255, 24, 27, 24))) g.FillEllipse(ring, RectangleF.Inflate(badge, stroke, stroke));
            var fill = field == true ? Accent.Color : Color.FromArgb(214, 88, 76);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, badge);
            var mark = field == true ? Accent.TextOn(fill) : Color.White;
            float cx = badge.X + br, cy = badge.Y + br, u = br * 0.5f;
            using (var pen = new Pen(mark, Math.Max(1.2f, br * 0.24f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (field == true) g.DrawLines(pen, new[] { new PointF(cx - u, cy), new PointF(cx - u * 0.2f, cy + u * 0.75f), new PointF(cx + u, cy - u * 0.7f) });
                else g.DrawLine(pen, cx - u * 0.75f, cy + u * 0.75f, cx + u * 0.75f, cy - u * 0.75f);
            }
        }
    }
}
