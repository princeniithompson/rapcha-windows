using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// The Words bubble (like the Android "Original" bubble): a dark rounded card next to the ring
    /// that shows the live words, a breathing olive glow, and Cancel / Complete. Never takes focus.
    /// </summary>
    public sealed class WordsBubble : LayeredWindow
    {
        public enum Mode { Listening, Polishing, Final, Done, Warning }

        private const int WidthDip = 420, HeightDip = 120;
        private readonly Timer _anim = new Timer { Interval = 33 };
        private readonly DateTime _born = DateTime.UtcNow;

        private Mode _mode = Mode.Listening;
        private string _text = "";
        private bool _micHint;
        private int _dpi = 96;
        private int _x, _y, _w, _h;
        private Rectangle _cancelRect, _completeRect, _fixRect;

        public event Action CancelClicked;
        public event Action CompleteClicked;
        public event Action FixMicClicked;
        public event Action<int, int> Dragged;   // moved by (dx, dy) while the user drags the bubble
        public event Action DragEnded;

        private bool _down, _dragging;
        private Point _downScreen, _lastScreen;
        private string _note = "";

        public Rectangle Area => new Rectangle(_x, _y, _w, _h);
        public bool IsDragging => _dragging;

        /// <summary>A small grey line in the bubble, e.g. "No text box — I'll copy it for you."</summary>
        public string Note { get => _note; set { _note = value ?? ""; Render(); } }

        public WordsBubble() : base(false)
        {
            _anim.Tick += (s, e) => Render();
        }

        public Mode CurrentMode => _mode;

        public void Open(Rectangle ring, DockEdge edge, int dpi)
        {
            _dpi = dpi;
            _w = S(WidthDip); _h = S(HeightDip);
            Place(ring, edge);
            _micHint = false;
            _note = "";
            Set(Mode.Listening, "");
            if (!Visible) Show();
            KeepOnTop();
        }

        /// <summary>Beside the ring on the screen side (flips to the other side if it would leave the screen).</summary>
        public void PlaceBeside(Rectangle ring, DockEdge edge)
        {
            if (_w <= 0) return;
            Place(ring, edge);
            Render();
        }

        private void Place(Rectangle ring, DockEdge edge)
        {
            var wa = Screen.FromRectangle(ring).WorkingArea;
            BubblePlacement.Beside(new IntRect(ring.X, ring.Y, ring.Width, ring.Height), edge, _w, _h,
                                   new IntRect(wa.X, wa.Y, wa.Width, wa.Height), S(12), out _x, out _y);
        }

        /// <summary>Moves the bubble (it follows the ring being dragged).</summary>
        public void MoveTo(int x, int y)
        {
            if (_x == x && _y == y) return;
            _x = x; _y = y;
            Render();
        }

        public void Set(Mode mode, string text)
        {
            _mode = mode;
            _text = text ?? "";
            _anim.Enabled = mode == Mode.Listening || mode == Mode.Polishing;
            Render();
        }

        public void SetWords(string words)
        {
            if (_mode != Mode.Listening) return;
            _text = words ?? "";
            if (_text.Length > 0) _micHint = false;
            Render();
        }

        public bool MicHint
        {
            get => _micHint;
            set { if (_micHint != value) { _micHint = value; Render(); } }
        }

        public void Close2() { _anim.Stop(); Hide(); }

        private int S(int dip) => (int)Math.Round(dip * _dpi / 96.0);

        private void Render()
        {
            if (IsDisposed || _w <= 0) return;
            using (var bmp = new Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var card = new Rectangle(0, 0, _w - 1, _h - 1);
                using (var path = Rounded(card, S(22)))
                {
                    using (var bg = new SolidBrush(Color.FromArgb(245, 28, 31, 27))) g.FillPath(bg, path);

                    if (_mode == Mode.Listening || _mode == Mode.Polishing)
                    {
                        double t = (DateTime.UtcNow - _born).TotalSeconds;
                        double breath = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / (_mode == Mode.Listening ? 1.8 : 0.9));
                        var olive = _mode == Mode.Listening ? Color.FromArgb(160, 190, 60) : Color.FromArgb(220, 190, 70);
                        var glowRect = new Rectangle(0, _h / 2, _w, _h / 2 + 1);
                        g.SetClip(path);
                        using (var glow = new LinearGradientBrush(glowRect, Color.FromArgb(0, olive), Color.FromArgb((int)(90 + 110 * breath), olive), LinearGradientMode.Vertical))
                            g.FillRectangle(glow, glowRect);
                        g.ResetClip();
                    }
                    using (var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawPath(border, path);
                }

                int pad = S(18);
                var textRect = new Rectangle(pad, S(12), _w - pad * 2, S(60));
                using (var big = new Font("Segoe UI", S(16), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bold = new Font("Segoe UI", S(16), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", S(14), FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    _cancelRect = _completeRect = _fixRect = Rectangle.Empty;
                    switch (_mode)
                    {
                        case Mode.Listening:
                            if (_micHint && _text.Length == 0)
                            {
                                DrawText(g, "I can't hear your microphone", bold, Color.FromArgb(255, 200, 190), new Rectangle(textRect.X, textRect.Y, textRect.Width, S(26)));
                                _fixRect = DrawLink(g, "Fix microphone", small, textRect.X, textRect.Y + S(30));
                            }
                            else if (_text.Length == 0)
                                DrawText(g, "Listening… start talking", big, Color.FromArgb(170, 175, 165), textRect);
                            else
                                DrawText(g, FitTail(g, _text, big, textRect), big, Color.White, textRect);
                            DrawButtons(g, small);
                            DrawNote(g);
                            break;
                        case Mode.Polishing:
                            DrawText(g, _text.Length > 0 ? FitTail(g, _text, big, textRect) : "Polishing…", big,
                                Color.FromArgb(200, 205, 195), textRect);
                            DrawText(g, "Polishing…", small, Color.FromArgb(230, 210, 120), new Rectangle(pad, _h - S(36), _w - pad * 2, S(24)));
                            if (_note.Length > 0)
                                using (var nf = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel))
                                using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                                using (var nb = new SolidBrush(Color.FromArgb(185, 190, 180)))
                                    g.DrawString(_note, nf, nb, new RectangleF(pad, _h - S(36), _w - pad * 2, S(24)), sf);
                            break;
                        case Mode.Final:
                            DrawText(g, FitTail(g, _text, big, textRect), big, Color.White, textRect);
                            break;
                        case Mode.Done:
                            DrawText(g, _text, bold, Color.FromArgb(190, 225, 120), new Rectangle(pad, 0, _w - pad * 2, _h), true);
                            break;
                        case Mode.Warning:
                            DrawText(g, _text, bold, Color.FromArgb(255, 200, 190), new Rectangle(pad, S(10), _w - pad * 2, _h - S(20)), true);
                            break;
                    }
                }
                Present(bmp, _x, _y);
            }
        }

        private void DrawButtons(Graphics g, Font f)
        {
            int bh = S(32), y = _h - bh - S(12);
            var cancelSize = TextRenderer.MeasureText("Cancel", f);
            _cancelRect = new Rectangle(S(12), y, cancelSize.Width + S(24), bh);
            using (var path = Rounded(_cancelRect, bh))
            using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillPath(b, path);
            DrawText(g, "Cancel", f, Color.FromArgb(255, 110, 100), _cancelRect, true, true);

            const string label = "✓  Complete";
            var completeSize = TextRenderer.MeasureText(label, f);
            int cw = completeSize.Width + S(28);
            _completeRect = new Rectangle(_w - cw - S(12), y, cw, bh);
            using (var path = Rounded(_completeRect, bh))
            using (var b = new SolidBrush(Color.FromArgb(255, 150, 175, 55))) g.FillPath(b, path);
            DrawText(g, label, f, Color.FromArgb(20, 25, 15), _completeRect, true, true);
        }

        private void DrawNote(Graphics g)
        {
            if (_note.Length == 0 || _cancelRect.IsEmpty) return;
            var r = Rectangle.FromLTRB(_cancelRect.Right + S(8), _cancelRect.Y - S(4), _completeRect.X - S(8), _cancelRect.Bottom + S(4));
            if (r.Width <= 0) return;
            using (var nf = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
            using (var nb = new SolidBrush(Color.FromArgb(185, 190, 180)))
                g.DrawString(_note, nf, nb, r, sf);
        }

        private Rectangle DrawLink(Graphics g, string text, Font f, int x, int y)
        {
            using (var u = new Font(f, FontStyle.Bold | FontStyle.Underline))
            {
                var size = TextRenderer.MeasureText(text, u);
                var r = new Rectangle(x, y, size.Width + S(6), size.Height + S(4));
                DrawText(g, text, u, Color.FromArgb(150, 200, 255), r);
                return r;
            }
        }

        private static void DrawText(Graphics g, string text, Font f, Color c, Rectangle r, bool centerV = false, bool centerH = false)
        {
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord })
            using (var b = new SolidBrush(c))
            {
                if (centerV) sf.LineAlignment = StringAlignment.Center;
                if (centerH) sf.Alignment = StringAlignment.Center;
                g.DrawString(text, f, b, r, sf);
            }
        }

        /// <summary>Shows the end of long text (the newest words), like a live caption.</summary>
        private static string FitTail(Graphics g, string text, Font f, Rectangle r)
        {
            text = text.Trim();
            if (g.MeasureString(text, f, r.Width).Height <= r.Height) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var candidate = "…" + text.Substring(mid);
                if (g.MeasureString(candidate, f, r.Width).Height <= r.Height) hi = mid; else lo = mid + 1;
            }
            return "…" + text.Substring(lo).TrimStart();
        }

        private bool OnButton(Point p) => _cancelRect.Contains(p) || _completeRect.Contains(p) || _fixRect.Contains(p);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || OnButton(e.Location)) return;
            _down = true;
            _dragging = false;
            _downScreen = _lastScreen = Cursor.Position;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = OnButton(e.Location) ? Cursors.Hand : _down ? Cursors.SizeAll : Cursors.Default;
            if (_down) DragTo(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_down)
            {
                _down = false;
                if (_dragging) { _dragging = false; DragEnded?.Invoke(); }
                return;
            }
            if (_completeRect.Contains(e.Location)) CompleteClicked?.Invoke();
            else if (_cancelRect.Contains(e.Location)) CancelClicked?.Invoke();
            else if (_fixRect.Contains(e.Location)) FixMicClicked?.Invoke();
        }

        private void DragTo(Point p)
        {
            var drag = SystemInformation.DragSize;
            if (!_dragging && (Math.Abs(p.X - _downScreen.X) > drag.Width || Math.Abs(p.Y - _downScreen.Y) > drag.Height)) _dragging = true;
            if (!_dragging) return;
            int dx = p.X - _lastScreen.X, dy = p.Y - _lastScreen.Y;
            _lastScreen = p;
            if (dx == 0 && dy == 0) return;
            MoveTo(_x + dx, _y + dy);
            Dragged?.Invoke(dx, dy);
        }

        /// <summary>Self-test: drags the bubble by (dx, dy) through the same code as the mouse, then releases.</summary>
        public void SimulateDrag(int dx, int dy, bool release)
        {
            var start = new Point(_x + _w / 2, _y + S(20));
            _down = true; _dragging = false; _downScreen = _lastScreen = start;
            DragTo(new Point(start.X + dx / 2, start.Y + dy / 2));
            DragTo(new Point(start.X + dx, start.Y + dy));
            if (release) { _down = false; if (_dragging) { _dragging = false; DragEnded?.Invoke(); } }
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
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
