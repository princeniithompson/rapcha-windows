using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Select to speak, modelled on Android's Select to Speak with Circle to Search: the monitor is frozen
    /// as a slightly darkened screenshot, the edges glow, and the user draws a bright green box around text
    /// (drag, or click two corners) or taps a word to read its line. Text comes from the built-in Windows OCR,
    /// so exactly what is visible is read. While reading, a green box moves from word to word.
    /// </summary>
    public sealed class ScreenOverlay : Form
    {
        public const string NoTextThere = "No text found there.";

        public enum Control { None, Back, PlayPause, Forward, Stop, Close }

        private static readonly Color Green = Color.FromArgb(70, 235, 120);

        private readonly Bitmap _dark;
        private readonly int _dpi;
        private readonly BoxSelector _selector = new BoxSelector();
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer { Interval = 33 };
        private readonly DateTime _born = DateTime.UtcNow;
        private readonly bool _silentVoice;

        private OcrService.Result _ocr;
        private string _status = "Reading the screen…";
        private SpeechReader _reader;
        private SpokenText _spoken;
        private Box _hl, _hlFrom, _hlTarget;
        private DateTime _hlMoved;
        private bool _hlVisible;
        private Rectangle _lastBoxRect, _lastHlRect;
        private (double x, double y)? _pendingTap;
        private readonly Dictionary<Control, Rectangle> _controls = new Dictionary<Control, Rectangle>();
        private Control _pressed = Control.None;
        private Font _font, _small;

        public string LastReadText { get; private set; } = "";
        public int HighlightMoves { get; private set; }
        public SpeechReader Reader => _reader;
        public OcrService.Result Ocr => _ocr;

        public event Action<Control> ControlUsed;

        public ScreenOverlay(Rectangle bounds, Bitmap screenshot, int dpi, bool silentVoice)
        {
            _dpi = dpi;
            _silentVoice = silentVoice;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Text = "Rapcha select to speak";
            Bounds = bounds;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            // The frozen screen, slightly darkened (kept in premultiplied format for fast drawing).
            _dark = new Bitmap(screenshot.Width, screenshot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dark))
            {
                g.DrawImageUnscaled(screenshot, 0, 0);
                using (var shade = new SolidBrush(Color.FromArgb(72, 8, 10, 20))) g.FillRectangle(shade, 0, 0, _dark.Width, _dark.Height);
            }
            _font = new Font("Segoe UI", S(15), FontStyle.Regular, GraphicsUnit.Pixel);
            _small = new Font("Segoe UI Semibold", S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _anim.Tick += (s, e) => Animate();
        }

        private float S(double dip) => (float)(dip * _dpi / 96.0);
        private int Si(double dip) => (int)Math.Round(dip * _dpi / 96.0);

        // ---------------- OCR ----------------

        public void SetOcr(OcrService.Result r)
        {
            _ocr = r;
            if (r.Problem.Length > 0) _status = r.Problem;
            else if (r.Layout.WordCount == 0) _status = "No text found on this screen.";
            else _status = "";
            InvalidateBottom();
            if (_pendingTap.HasValue)
            {
                var (x, y) = _pendingTap.Value;
                _pendingTap = null;
                var line = r.Layout.LineAt(x, y);
                if (line != null) { _selector.Cancel(); ReadWords(line.Words.ToList()); }
            }
        }

        // ---------------- Reading ----------------

        private SpeechReader Voice()
        {
            if (_reader == null)
            {
                _reader = new SpeechReader(_silentVoice);
                _reader.Changed += OnReaderChanged;
            }
            return _reader;
        }

        public void ReadBox(Box box)
        {
            if (_ocr == null) { Say("Still reading the screen. Try again in a moment."); return; }
            ReadWords(_ocr.Layout.Select(box));
        }

        private void ReadWords(List<OcrWord> words)
        {
            if (words.Count == 0) { Say(NoTextThere); return; }
            _spoken = new SpokenText(words);
            LastReadText = _spoken.Text;
            _status = "";
            try
            {
                Voice().Read(_spoken.Text);
                AppStorage.Log("Overlay read: words=" + words.Count + " lines=" + words.Select(w => w.Line).Distinct().Count() + " voice=\"" + _reader.VoiceName + "\"");
            }
            catch (Exception e) { VoiceFailed(e); }
            InvalidateBottom();
        }

        private void Say(string message)
        {
            _spoken = null;
            LastReadText = message;
            _status = message;
            HideHighlight();
            try { Voice().Read(message); } catch (Exception e) { VoiceFailed(e); }
            InvalidateBottom();
        }

        private void VoiceFailed(Exception e)
        {
            AppStorage.Log("Voice failed: " + e.GetType().Name + " " + e.Message);
            _reader?.Dispose(); _reader = null;
            _status = SpeechReader.CountVoices() == 0 ? "No Windows voice is installed." : "No speaker found. Check your sound output.";
        }

        private void OnReaderChanged()
        {
            if (_reader == null) return;
            if (_spoken != null && (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Paused))
            {
                var target = _spoken.HighlightFor(_reader.CurrentWord);
                if (!_hlVisible || !SameBox(target, _hlTarget))
                {
                    _hlFrom = _hlVisible ? _hl : target;
                    _hlTarget = target;
                    _hlMoved = DateTime.UtcNow;
                    _hlVisible = true;
                    HighlightMoves++;
                }
                _anim.Start();
            }
            else if (_reader.Status == ReadState.Finished || _reader.Status == ReadState.Idle)
            {
                HideHighlight();
                if (_reader.Status == ReadState.Finished && _status == LastReadText) _status = ""; // a spoken message is done
            }
            InvalidateBottom();
        }

        private static bool SameBox(Box a, Box b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.W - b.W) < 0.5;

        private void HideHighlight()
        {
            if (!_hlVisible) return;
            _hlVisible = false;
            Invalidate(_lastHlRect);
        }

        public void Use(Control c)
        {
            switch (c)
            {
                case Control.Back: _reader?.Skip(-ReadingPlan.SkipWords); break;
                case Control.Forward: _reader?.Skip(ReadingPlan.SkipWords); break;
                case Control.PlayPause: _reader?.TogglePause(); break;
                case Control.Stop: _reader?.Stop(); HideHighlight(); break;
                case Control.Close: Close(); break;
            }
            ControlUsed?.Invoke(c);
            InvalidateBottom();
        }

        private bool Reading => _reader != null && (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Paused);

        // ---------------- Input ----------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (_selector.WaitingForSecondCorner) { _selector.Cancel(); InvalidateBox(); }
                    else Close();
                    break;
                case Keys.Space: Use(Control.PlayPause); break;
                case Keys.Left: Use(Control.Back); break;
                case Keys.Right: Use(Control.Forward); break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { _selector.Cancel(); InvalidateBox(); return; }
            if (e.Button != MouseButtons.Left) return;
            _pressed = HitControl(e.Location);
            if (_pressed != Control.None) return;
            _selector.OnDown(e.X, e.Y);
            InvalidateBox();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = HitControl(e.Location) != Control.None ? Cursors.Hand : Cursors.Cross;
            if (_pressed != Control.None) return;
            _selector.OnMove(e.X, e.Y);
            if (_selector.HasLiveBox) InvalidateBox();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_pressed != Control.None)
            {
                var c = _pressed;
                _pressed = Control.None;
                if (HitControl(e.Location) == c) Use(c);
                return;
            }
            bool? word = _ocr == null ? (bool?)null : _ocr.Layout.LineAt(e.X, e.Y) != null;
            Selection(_selector.OnUp(e.X, e.Y, word));
        }

        private void Selection(BoxSelector.Result r)
        {
            InvalidateBox();
            switch (r.Kind)
            {
                case BoxSelector.Kind.Box:
                    AppStorage.Log("Overlay box: " + (int)r.Box.W + "x" + (int)r.Box.H);
                    ReadBox(r.Box);
                    break;
                case BoxSelector.Kind.TapWord:
                    ReadWords(_ocr.Layout.LineAt(r.X, r.Y).Words.ToList());
                    break;
                case BoxSelector.Kind.CornerSet:
                    if (_ocr == null) _pendingTap = (r.X, r.Y); // decide once OCR is ready: a word means "read its line"
                    break;
            }
        }

        // Self-test helpers: the same paths real input takes.
        public void SimulateDrag(Box b)
        {
            _selector.OnDown(b.X, b.Y);
            _selector.OnMove(b.X + b.W / 2, b.Y + b.H / 2);
            _selector.OnMove(b.Right, b.Bottom);
            Selection(_selector.OnUp(b.Right, b.Bottom, _ocr?.Layout.LineAt(b.Right, b.Bottom) != null));
        }

        public void SimulateTwoClicks(Box b)
        {
            _selector.Cancel();
            _selector.OnDown(b.X, b.Y);
            var first = _selector.OnUp(b.X, b.Y, false);
            if (first.Kind != BoxSelector.Kind.CornerSet) { Selection(first); return; }
            _selector.OnMove(b.Right, b.Bottom);
            _selector.OnDown(b.Right, b.Bottom);
            Selection(_selector.OnUp(b.Right, b.Bottom, false));
        }

        public void SimulateTap(double x, double y)
        {
            _selector.Cancel();
            _selector.OnDown(x, y);
            Selection(_selector.OnUp(x, y, _ocr?.Layout.LineAt(x, y) != null));
        }

        // ---------------- Painting ----------------

        private void Animate()
        {
            bool moving = _hlVisible && (DateTime.UtcNow - _hlMoved).TotalMilliseconds < 160;
            if (_hlVisible)
            {
                double t = Math.Min(1, (DateTime.UtcNow - _hlMoved).TotalMilliseconds / 140.0);
                t = 1 - Math.Pow(1 - t, 3);
                _hl = new Box(Lerp(_hlFrom.X, _hlTarget.X, t), Lerp(_hlFrom.Y, _hlTarget.Y, t), Lerp(_hlFrom.W, _hlTarget.W, t), Lerp(_hlFrom.H, _hlTarget.H, t));
                var r = HighlightRect();
                Invalidate(Rectangle.Union(r, _lastHlRect));
            }
            if (!moving && !_hlVisible) _anim.Stop();
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private Rectangle HighlightRect()
        {
            var b = _hl.Inflate(S(4));
            var r = Rectangle.FromLTRB((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
            r.Inflate(Si(8), Si(8));
            return r;
        }

        private Rectangle BoxRect(Box b)
        {
            var r = Rectangle.FromLTRB((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
            r.Inflate(Si(10), Si(10));
            return r;
        }

        private void InvalidateBox()
        {
            var now = _selector.HasLiveBox ? BoxRect(_selector.LiveBox) : Rectangle.Empty;
            if (!_lastBoxRect.IsEmpty) Invalidate(_lastBoxRect);
            if (!now.IsEmpty) Invalidate(now);
            _lastBoxRect = now;
        }

        private void InvalidateBottom()
        {
            Invalidate(new Rectangle(0, ClientSize.Height - Si(150), ClientSize.Width, Si(150)));
            Invalidate(new Rectangle(ClientSize.Width - Si(90), 0, Si(90), Si(90)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(_dark, clip, clip, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (_selector.HasLiveBox)
            {
                var b = _selector.LiveBox;
                var r = new RectangleF((float)b.X, (float)b.Y, (float)Math.Max(1, b.W), (float)Math.Max(1, b.H));
                using (var path = Rounded(r, S(14)))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(22, Green))) g.FillPath(fill, path);
                    using (var glow = new Pen(Color.FromArgb(70, Green), S(8))) g.DrawPath(glow, path);
                    using (var pen = new Pen(Green, S(3))) g.DrawPath(pen, path);
                }
                _lastBoxRect = BoxRect(b);
            }

            if (_hlVisible)
            {
                double pulse = 0.5 + 0.5 * Math.Sin((DateTime.UtcNow - _born).TotalSeconds * Math.PI * 2 / 1.2);
                var b = _hl.Inflate(S(4));
                var r = new RectangleF((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                using (var path = Rounded(r, S(10)))
                {
                    using (var glow = new Pen(Color.FromArgb((int)(60 + 50 * pulse), Green), S(7))) g.DrawPath(glow, path);
                    using (var pen = new Pen(Color.FromArgb(255, 120, 255, 150), S(2.6))) g.DrawPath(pen, path);
                }
                _lastHlRect = HighlightRect();
            }

            DrawBottom(g);
            DrawClose(g);
        }

        private void DrawBottom(Graphics g)
        {
            _controls.Remove(Control.Back); _controls.Remove(Control.PlayPause); _controls.Remove(Control.Forward); _controls.Remove(Control.Stop);
            string text = _status.Length > 0 ? _status
                : _selector.WaitingForSecondCorner ? "Now click the other corner of the box."
                : "Drag a box around text to read it (or click two corners, or tap a word). Esc to close.";
            bool controls = Reading || (_reader != null && _reader.Status == ReadState.Paused);
            float pad = S(18), h = S(48), btn = S(36), gap = S(10);
            var size = g.MeasureString(text, _font);
            float w = Math.Min(ClientSize.Width - S(40), size.Width + pad * 2 + (controls ? 4 * btn + 4 * gap : 0));
            var pill = new RectangleF((ClientSize.Width - w) / 2f, ClientSize.Height - S(40) - h, w, h);
            using (var path = Rounded(pill, h))
            {
                using (var bg = new SolidBrush(Color.FromArgb(235, 22, 24, 30))) g.FillPath(bg, path);
                using (var rim = new Pen(Color.FromArgb(90, Green), S(1.2f))) g.DrawPath(rim, path);
            }
            float x = pill.X + pad;
            if (controls)
            {
                foreach (var c in new[] { Control.Back, Control.PlayPause, Control.Forward, Control.Stop })
                {
                    var r = new RectangleF(x, pill.Y + (h - btn) / 2, btn, btn);
                    _controls[c] = Rectangle.Round(r);
                    using (var b = new SolidBrush(c == Control.PlayPause ? Green : Color.FromArgb(55, 255, 255, 255))) g.FillEllipse(b, r);
                    DrawGlyph(g, c, r, c == Control.PlayPause ? Color.FromArgb(20, 30, 20) : Color.White, _reader.Status == ReadState.Playing);
                    x += btn + gap;
                }
                x += gap;
            }
            using (var b = new SolidBrush(Color.FromArgb(240, 240, 244)))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
                g.DrawString(controls ? "Reading…" : text, _font, b, new RectangleF(x, pill.Y, pill.Right - x - pad, h), sf);
        }

        private void DrawClose(Graphics g)
        {
            float d = S(40);
            var r = new RectangleF(ClientSize.Width - d - S(28), S(28), d, d);
            _controls[Control.Close] = Rectangle.Round(r);
            using (var b = new SolidBrush(Color.FromArgb(220, 22, 24, 30))) g.FillEllipse(b, r);
            using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), S(1))) g.DrawEllipse(rim, r);
            float c = S(8);
            using (var p = new Pen(Color.White, S(2.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - c, cy - c, cx + c, cy + c);
                g.DrawLine(p, cx - c, cy + c, cx + c, cy - c);
            }
        }

        private void DrawGlyph(Graphics g, Control b, RectangleF r, Color ink, bool playing)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 32f;
            using (var brush = new SolidBrush(ink))
            using (var pen = new Pen(ink, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var tiny = new Font("Segoe UI", 8f * u, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                switch (b)
                {
                    case Control.PlayPause:
                        if (playing)
                        {
                            g.FillRectangle(brush, cx - 6 * u, cy - 7 * u, 4 * u, 14 * u);
                            g.FillRectangle(brush, cx + 2 * u, cy - 7 * u, 4 * u, 14 * u);
                        }
                        else g.FillPolygon(brush, new[] { new PointF(cx - 4 * u, cy - 8 * u), new PointF(cx + 8 * u, cy), new PointF(cx - 4 * u, cy + 8 * u) });
                        break;
                    case Control.Stop:
                        g.FillRectangle(brush, cx - 5.5f * u, cy - 5.5f * u, 11 * u, 11 * u);
                        break;
                    default:
                        bool back = b == Control.Back;
                        g.DrawArc(pen, new RectangleF(cx - 9 * u, cy - 9 * u, 18 * u, 18 * u), back ? 200 : -20, back ? 250 : -250);
                        float ax = back ? cx - 8.5f * u : cx + 8.5f * u, ay = cy - 5 * u;
                        g.FillPolygon(brush, new[] { new PointF(ax - 3 * u, ay - 1 * u), new PointF(ax + 3 * u, ay - 1 * u), new PointF(ax, ay + 4 * u) });
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("10", tiny, brush, new RectangleF(cx - 8 * u, cy - 6 * u, 16 * u, 14 * u), sf);
                        break;
                }
            }
        }

        private Control HitControl(Point p)
        {
            foreach (var kv in _controls)
            {
                var r = kv.Value; r.Inflate(Si(4), Si(4));
                if (r.Contains(p)) return kv.Key;
            }
            return Control.None;
        }

        private static GraphicsPath Rounded(RectangleF r, float diameter)
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _anim.Stop();
            if (_reader != null) { _reader.Changed -= OnReaderChanged; _reader.Dispose(); _reader = null; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Dispose();
                _dark.Dispose();
                _font.Dispose();
                _small.Dispose();
                _ocr = null;
                _spoken = null;
            }
            base.Dispose(disposing);
        }
    }
}
