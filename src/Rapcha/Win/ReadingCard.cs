using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Select-to-speak reading card next to the ring. The phrase being read sits on a glowing olive band
    /// that rises in when the phrase changes and has a light shimmer moving across it; the word being spoken
    /// glows cream-yellow. The previous and next phrases are shown dimmed above and below.
    /// Controls: Back 10 words, Play/Pause, Forward 10 words, Stop (Back/Forward repeat while held).
    /// </summary>
    public sealed class ReadingCard : LayeredWindow
    {
        public enum Button { None, Back, PlayPause, Forward, Stop }

        private const int WidthDip = 460, HeightDip = 188;
        private readonly Timer _anim = new Timer { Interval = 33 };
        private readonly Timer _repeat = new Timer();
        private readonly Timer _autoHide = new Timer { Interval = 1500 };
        private readonly DateTime _born = DateTime.UtcNow;

        private SpeechReader _reader;
        private string _status = "";
        private int _dpi = 96, _x, _y, _w, _h;
        private int _shownPhrase = -1;
        private DateTime _phraseSince = DateTime.UtcNow;
        private Bitmap _bmp;
        private Font _phraseFont, _wordFont, _dimFont, _smallFont;
        private readonly Dictionary<Button, Rectangle> _buttons = new Dictionary<Button, Rectangle>();
        private Button _pressed = Button.None, _hover = Button.None;

        public event Action<Button> ButtonClicked;

        public ReadingCard() : base(false)
        {
            _anim.Tick += (s, e) => Render();
            _repeat.Tick += (s, e) =>
            {
                _repeat.Interval = 280;
                if (_pressed == Button.Back || _pressed == Button.Forward) ButtonClicked?.Invoke(_pressed);
            };
            _autoHide.Tick += (s, e) => { _autoHide.Stop(); Close2(); };
        }

        public void Attach(SpeechReader reader)
        {
            if (_reader != null) _reader.Changed -= OnReaderChanged;
            _reader = reader;
            if (_reader != null) _reader.Changed += OnReaderChanged;
        }

        public void Open(Rectangle ring, DockEdge edge, int dpi)
        {
            if (_dpi != dpi || _w == 0) { _dpi = dpi; MakeResources(); }
            int gap = S(12);
            switch (edge)
            {
                case DockEdge.Left: _x = ring.Right + gap; _y = ring.Y + ring.Height / 2 - _h / 2; break;
                case DockEdge.Right: _x = ring.X - gap - _w; _y = ring.Y + ring.Height / 2 - _h / 2; break;
                case DockEdge.Top: _x = ring.X + ring.Width / 2 - _w / 2; _y = ring.Bottom + gap; break;
                default: _x = ring.X + ring.Width / 2 - _w / 2; _y = ring.Y - gap - _h; break;
            }
            var wa = Screen.FromRectangle(ring).WorkingArea;
            _x = Math.Max(wa.X, Math.Min(_x, wa.Right - _w));
            _y = Math.Max(wa.Y, Math.Min(_y, wa.Bottom - _h));
            _autoHide.Stop();
            _status = "";
            _shownPhrase = -1;
            Render();
            if (!Visible) Show();
            KeepOnTop();
            _anim.Start();
        }

        /// <summary>A short message in place of the text (e.g. "No text selected").</summary>
        public void ShowStatus(string status)
        {
            _status = status ?? "";
            Render();
        }

        public void Close2()
        {
            _anim.Stop();
            _repeat.Stop();
            _autoHide.Stop();
            _pressed = Button.None;
            if (Visible) Hide();
            _bmp?.Dispose(); _bmp = null; // keep memory low while hidden
        }

        private void OnReaderChanged()
        {
            if (_reader == null || !Visible) return;
            if (_reader.Status == SpeechReader.State.Finished) _autoHide.Start();
            else _autoHide.Stop();
            Render();
        }

        private int S(double dip) => (int)Math.Round(dip * _dpi / 96.0);

        private void MakeResources()
        {
            _w = S(WidthDip); _h = S(HeightDip);
            _phraseFont?.Dispose(); _wordFont?.Dispose(); _dimFont?.Dispose(); _smallFont?.Dispose();
            _phraseFont = new Font("Segoe UI", S(19), FontStyle.Regular, GraphicsUnit.Pixel);
            _wordFont = new Font("Segoe UI Semibold", S(19), FontStyle.Regular, GraphicsUnit.Pixel);
            _dimFont = new Font("Segoe UI", S(14), FontStyle.Regular, GraphicsUnit.Pixel);
            _smallFont = new Font("Segoe UI", S(12), FontStyle.Bold, GraphicsUnit.Pixel);
            _bmp?.Dispose(); _bmp = null;
        }

        private void Render()
        {
            if (IsDisposed || _w <= 0) return;
            if (_bmp == null) _bmp = new Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            double t = (DateTime.UtcNow - _born).TotalSeconds;
            using (var g = Graphics.FromImage(_bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var card = new Rectangle(0, 0, _w - 1, _h - 1);
                using (var path = Rounded(card, S(24)))
                {
                    using (var bg = new SolidBrush(Color.FromArgb(246, 24, 27, 23))) g.FillPath(bg, path);
                    using (var border = new Pen(Color.FromArgb(60, 226, 232, 190), 1f)) g.DrawPath(border, path);
                }

                int pad = S(18);
                var plan = _reader?.Plan;
                bool playing = _reader != null && _reader.Status == SpeechReader.State.Playing;

                // Header: a small pulsing dot and where we are.
                double pulse = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 1.6);
                int dot = S(8);
                using (var b = new SolidBrush(Color.FromArgb(playing ? (int)(140 + 115 * pulse) : 120, 170, 200, 70)))
                    g.FillEllipse(b, pad, S(14), dot, dot);
                string head = _reader == null ? "Select to speak" :
                    _reader.Status == SpeechReader.State.Paused ? "Paused" :
                    _reader.Status == SpeechReader.State.Finished ? "Done" : "Reading aloud";
                using (var b = new SolidBrush(Color.FromArgb(200, 214, 220, 190)))
                    g.DrawString(head, _smallFont, b, pad + dot + S(6), S(9));

                if (_status.Length > 0 || plan == null || plan.IsEmpty)
                {
                    using (var b = new SolidBrush(Color.FromArgb(220, 230, 232, 220)))
                        g.DrawString(_status.Length > 0 ? _status : "Select text in any app to hear it.", _phraseFont, b,
                            new RectangleF(pad, S(54), _w - 2 * pad, S(60)));
                    _buttons.Clear();
                    DrawButtons(g, false, plan != null && !plan.IsEmpty);
                    Present(_bmp, _x, _y);
                    return;
                }

                int word = Math.Max(0, Math.Min(_reader.CurrentWord, plan.Words.Count - 1));
                int phrase = plan.PhraseOfWord(word);
                if (phrase != _shownPhrase) { _shownPhrase = phrase; _phraseSince = DateTime.UtcNow; }
                string pos = (phrase + 1) + " / " + plan.Phrases.Count;
                var posSize = g.MeasureString(pos, _smallFont);
                using (var b = new SolidBrush(Color.FromArgb(140, 214, 220, 190)))
                    g.DrawString(pos, _smallFont, b, _w - pad - posSize.Width, S(9));

                // Previous / next phrases, dimmed, one line each.
                var dimFmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisWord };
                using (var dim = new SolidBrush(Color.FromArgb(105, 220, 224, 210)))
                {
                    if (phrase > 0) g.DrawString(plan.PhraseText(phrase - 1), _dimFont, dim, new RectangleF(pad, S(30), _w - 2 * pad, S(20)), dimFmt);
                    if (phrase + 1 < plan.Phrases.Count) g.DrawString(plan.PhraseText(phrase + 1), _dimFont, dim, new RectangleF(pad, S(118), _w - 2 * pad, S(20)), dimFmt);
                }

                // The glowing band for the current phrase: rises and fades in when the phrase changes.
                double since = (DateTime.UtcNow - _phraseSince).TotalSeconds;
                double rise = Math.Min(1.0, since / 0.22);
                double ease = 1 - Math.Pow(1 - rise, 3);
                var band = new Rectangle(pad - S(8), S(52) + (int)((1 - ease) * S(10)), _w - 2 * pad + S(16), S(62));
                DrawBand(g, band, t, ease, playing);

                // Lay the phrase's words out by hand so the spoken word can glow.
                var ph = plan.Phrases[phrase];
                var fmt = StringFormat.GenericTypographic;
                float space = g.MeasureString(" ", _phraseFont, 1000, StringFormat.GenericDefault).Width * 0.55f;
                float lineH = _phraseFont.GetHeight(g);
                float maxW = band.Width - S(24);
                var lines = new List<List<(int idx, float w)>> { new List<(int, float)>() };
                float lineW = 0;
                for (int i = ph.FirstWord; i < ph.EndWord; i++)
                {
                    var f = i == word ? _wordFont : _phraseFont;
                    float ww = g.MeasureString(plan.WordText(i), f, 4000, fmt).Width;
                    if (lineW > 0 && lineW + space + ww > maxW) { lines.Add(new List<(int, float)>()); lineW = 0; }
                    lines[lines.Count - 1].Add((i, ww));
                    lineW += (lineW > 0 ? space : 0) + ww;
                }
                if (lines.Count > 2) lines.RemoveRange(2, lines.Count - 2); // band holds two lines; phrases are short
                float y0 = band.Y + (band.Height - lines.Count * lineH) / 2f;
                int textAlpha = (int)(255 * ease);
                for (int li = 0; li < lines.Count; li++)
                {
                    float x = band.X + S(12);
                    foreach (var (idx, ww) in lines[li])
                    {
                        string s = plan.WordText(idx);
                        float y = y0 + li * lineH;
                        if (idx == word && _reader.Status != SpeechReader.State.Finished)
                        {
                            // soft glow behind the spoken word
                            var glowRect = new RectangleF(x - S(5), y + S(2), ww + S(10), lineH - S(2));
                            using (var gp = Rounded(Rectangle.Round(glowRect), S(12)))
                            using (var gb = new SolidBrush(Color.FromArgb((int)(70 + 40 * pulse), 255, 236, 160)))
                                g.FillPath(gb, gp);
                            using (var wb = new SolidBrush(Color.FromArgb(textAlpha, 255, 244, 196)))
                                g.DrawString(s, _wordFont, wb, x, y, fmt);
                        }
                        else
                        {
                            bool done = idx < word;
                            using (var wb = new SolidBrush(Color.FromArgb(textAlpha, done ? 228 : 250, done ? 232 : 250, done ? 214 : 244)))
                                g.DrawString(s, _phraseFont, wb, x, y, fmt);
                        }
                        x += ww + space;
                    }
                }

                // Thin progress line across the whole selection.
                float progress = plan.Words.Count <= 1 ? 1f : word / (float)(plan.Words.Count - 1);
                var track = new RectangleF(pad, S(140), _w - 2 * pad, S(2));
                using (var tb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(tb, track);
                using (var pb = new SolidBrush(Color.FromArgb(200, 180, 205, 80))) g.FillRectangle(pb, track.X, track.Y, track.Width * progress, track.Height);

                DrawButtons(g, playing, true);
            }
            Present(_bmp, _x, _y);
        }

        private void DrawBand(Graphics g, Rectangle band, double t, double ease, bool playing)
        {
            double breath = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 2.2);
            using (var path = Rounded(band, S(18)))
            {
                int a = (int)((playing ? 120 + 70 * breath : 110) * ease);
                using (var lg = new LinearGradientBrush(band, Color.FromArgb(a, 112, 140, 40), Color.FromArgb((int)(a * 0.55), 70, 92, 30), LinearGradientMode.Vertical))
                    g.FillPath(lg, path);

                if (playing)
                {
                    // a light shimmer sweeping across the band
                    double sweep = (t % 2.4) / 2.4;
                    int sw = S(110);
                    int sx = band.X - sw + (int)((band.Width + sw) * sweep);
                    var sr = new Rectangle(sx, band.Y, sw, band.Height);
                    var blend = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 246, 210), Color.FromArgb((int)(70 * ease), 255, 246, 210), Color.FromArgb(0, 255, 246, 210) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    using (var shimmer = new LinearGradientBrush(sr, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal) { InterpolationColors = blend })
                    {
                        g.SetClip(path);
                        g.FillRectangle(shimmer, sr);
                        g.ResetClip();
                    }
                }
                // glowing rim: two strokes, wide and faint, then thin and bright
                using (var outer = new Pen(Color.FromArgb((int)((40 + 40 * breath) * ease), 200, 230, 110), S(4))) g.DrawPath(outer, path);
                using (var inner = new Pen(Color.FromArgb((int)((150 + 80 * breath) * ease), 226, 240, 170), Math.Max(1, S(1.2)))) g.DrawPath(inner, path);
            }
        }

        private void DrawButtons(Graphics g, bool playing, bool enabled)
        {
            _buttons.Clear();
            int size = S(32), gap = S(16), y = _h - S(42);
            var order = new[] { Button.Back, Button.PlayPause, Button.Forward, Button.Stop };
            int total = order.Length * size + (order.Length - 1) * gap;
            int x = (_w - total) / 2;
            foreach (var b in order)
            {
                var r = new Rectangle(x, y, size, size);
                _buttons[b] = r;
                bool hot = b == _hover || b == _pressed;
                int alpha = enabled || b == Button.Stop ? 255 : 90;
                using (var fill = new SolidBrush(Color.FromArgb(b == Button.PlayPause ? (hot ? 255 : 225) : (hot ? 90 : 45),
                    b == Button.PlayPause ? 176 : 255, b == Button.PlayPause ? 200 : 255, b == Button.PlayPause ? 76 : 255)))
                    g.FillEllipse(fill, r);
                var ink = Color.FromArgb(alpha, b == Button.PlayPause ? 24 : 240, b == Button.PlayPause ? 28 : 242, b == Button.PlayPause ? 20 : 230);
                DrawGlyph(g, b, r, ink, playing);
                x += size + gap;
            }
        }

        private void DrawGlyph(Graphics g, Button b, Rectangle r, Color ink, bool playing)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 32f;
            using (var brush = new SolidBrush(ink))
            using (var pen = new Pen(ink, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var tiny = new Font("Segoe UI", 8f * u, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                switch (b)
                {
                    case Button.PlayPause:
                        if (playing)
                        {
                            g.FillRectangle(brush, cx - 6 * u, cy - 7 * u, 4 * u, 14 * u);
                            g.FillRectangle(brush, cx + 2 * u, cy - 7 * u, 4 * u, 14 * u);
                        }
                        else g.FillPolygon(brush, new[] { new PointF(cx - 4 * u, cy - 8 * u), new PointF(cx + 8 * u, cy), new PointF(cx - 4 * u, cy + 8 * u) });
                        break;
                    case Button.Stop:
                        g.FillRectangle(brush, cx - 5.5f * u, cy - 5.5f * u, 11 * u, 11 * u);
                        break;
                    case Button.Back:
                    case Button.Forward:
                        bool back = b == Button.Back;
                        var arc = new RectangleF(cx - 9 * u, cy - 9 * u, 18 * u, 18 * u);
                        g.DrawArc(pen, arc, back ? 200 : -20, back ? 250 : -250);
                        float ax = back ? cx - 8.5f * u : cx + 8.5f * u, ay = cy - 5 * u;
                        g.FillPolygon(brush, back
                            ? new[] { new PointF(ax - 3 * u, ay - 1 * u), new PointF(ax + 3 * u, ay - 1 * u), new PointF(ax, ay + 4 * u) }
                            : new[] { new PointF(ax - 3 * u, ay - 1 * u), new PointF(ax + 3 * u, ay - 1 * u), new PointF(ax, ay + 4 * u) });
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("10", tiny, brush, new RectangleF(cx - 8 * u, cy - 6 * u, 16 * u, 14 * u), sf);
                        break;
                }
            }
        }

        private Button HitTest(Point p)
        {
            foreach (var kv in _buttons)
            {
                var r = kv.Value; r.Inflate(S(4), S(4));
                if (r.Contains(p)) return kv.Key;
            }
            return Button.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == Button.None ? Cursors.Default : Cursors.Hand; if (!_anim.Enabled) Render(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = Button.None;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = HitTest(e.Location);
            if (_pressed == Button.Back || _pressed == Button.Forward)
            {
                ButtonClicked?.Invoke(_pressed); // act at once; holding repeats
                _repeat.Interval = 450;
                _repeat.Start();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _repeat.Stop();
            var was = _pressed;
            _pressed = Button.None;
            if (e.Button == MouseButtons.Right) { ButtonClicked?.Invoke(Button.Stop); return; }
            if (was != Button.None && was == HitTest(e.Location) && was != Button.Back && was != Button.Forward)
                ButtonClicked?.Invoke(was);
        }

        private static GraphicsPath Rounded(Rectangle r, int diameter)
        {
            var p = new GraphicsPath();
            diameter = Math.Max(2, Math.Min(diameter, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
            p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
            p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Dispose(); _repeat.Dispose(); _autoHide.Dispose();
                _bmp?.Dispose(); _phraseFont?.Dispose(); _wordFont?.Dispose(); _dimFont?.Dispose(); _smallFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
