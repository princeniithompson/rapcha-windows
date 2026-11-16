using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// The Words bubble (like the Android "Original" bubble): a dark rounded card next to the ring with four lines
    /// of live words (auto-scrolling), a breathing glow in the user's glow colour, and one compact row:
    /// Cancel · a keyboard icon (check badge = the words will be typed into the focused text field, slash badge =
    /// no text field, they'll be copied) · Complete. Same outer size as before (420 x 120 logical px); fonts follow
    /// DPI and Windows "Make text bigger". Never takes focus.
    /// </summary>
    public sealed class WordsBubble : LayeredWindow
    {
        public enum Mode { Listening, Polishing, Final, Done, Warning }

        private readonly Timer _anim = new Timer { Interval = 33 };
        private readonly BubblePainter _p = new BubblePainter();

        private int _dpi = 96;
        private int _x, _y, _w, _h;

        public event Action CancelClicked;
        public event Action CompleteClicked;
        public event Action FixMicClicked;
        public event Action<int, int> Dragged;   // moved by (dx, dy) while the user drags the bubble
        public event Action DragEnded;

        private bool _down, _dragging;
        private Point _downScreen, _lastScreen;

        public Rectangle Area => new Rectangle(_x, _y, _w, _h);
        public bool IsDragging => _dragging;
        public BubblePainter Painter => _p;

        /// <summary>true: a text field has focus (check badge); false: none (slash badge, the words are copied); null: not known yet.</summary>
        public bool? FieldValid { get => _p.Field; set { if (_p.Field != value) { _p.Field = value; Render(); } } }

        public WordsBubble() : base(false)
        {
            _anim.Tick += (s, e) => { _p.Step(); Render(); };
            Accent.Changed += OnAccent;
        }

        private void OnAccent() { if (Visible) Render(); }

        public Mode CurrentMode => _p.Mode;

        public void Open(Rectangle ring, DockEdge edge, int dpi)
        {
            _dpi = dpi;
            _p.Setup(dpi, BubblePainter.ReadTextScale());
            _w = _p.Layout.Width; _h = _p.Layout.Height;
            Place(ring, edge);
            _p.MicHint = false;
            _p.Field = null;
            _p.Timer = ""; _p.TimerCountdown = false; _p.Offline = false;
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
            _p.SetMode(mode, text);
            _anim.Enabled = mode == Mode.Listening || mode == Mode.Polishing;
            Render();
        }

        public void SetWords(string words)
        {
            if (_p.Mode != Mode.Listening) return;
            _p.SetText(words);
            if (_p.Text.Length > 0) _p.MicHint = false;
            Render();
        }

        public bool MicHint
        {
            get => _p.MicHint;
            set { if (_p.MicHint != value) { _p.MicHint = value; Render(); } }
        }

        /// <summary>The small recording timer (empty = hidden); re-drawn only when the text changes.</summary>
        public void SetTimer(TimerText t)
        {
            if (_p.Timer == (t.Text ?? "") && _p.TimerCountdown == t.Countdown) return;
            _p.Timer = t.Text ?? ""; _p.TimerCountdown = t.Countdown;
            Render();
        }

        /// <summary>The "Offline" tag: this dictation is typed by speech on this laptop.</summary>
        public bool Offline
        {
            get => _p.Offline;
            set { if (_p.Offline != value) { _p.Offline = value; Render(); } }
        }

        public void Close2() { _anim.Stop(); Hide(); }

        private int S(int dip) => (int)Math.Round(dip * _dpi / 96.0);

        private void Render()
        {
            if (IsDisposed || _w <= 0) return;
            using (var bmp = _p.Render())
                Present(bmp, _x, _y);
        }

        private bool OnButton(Point p) => _p.CancelRect.Contains(p) || _p.CompleteRect.Contains(p) || _p.FixRect.Contains(p);

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
            if (_p.CompleteRect.Contains(e.Location)) CompleteClicked?.Invoke();
            else if (_p.CancelRect.Contains(e.Location)) CancelClicked?.Invoke();
            else if (_p.FixRect.Contains(e.Location)) FixMicClicked?.Invoke();
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
            if (release) SimulateRelease();
        }

        public void SimulateRelease() { _down = false; if (_dragging) { _dragging = false; DragEnded?.Invoke(); } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _anim.Dispose(); Accent.Changed -= OnAccent; }
            base.Dispose(disposing);
        }
    }
}
