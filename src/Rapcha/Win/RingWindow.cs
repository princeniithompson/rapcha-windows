using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using Rapcha.Core;

namespace Rapcha.Win
{
    public enum RingState { Idle, Recording, Polishing }

    /// <summary>The floating lifebuoy. Always docked to a screen edge; drag it and it snaps to the nearest edge.</summary>
    public sealed class RingWindow : LayeredWindow
    {
        public const int RingDip = 56;
        public const int GlowDip = 12;
        public const int GapDip = 6;

        private static readonly Image Lifebuoy = LoadLifebuoy();
        private readonly Timer _anim = new Timer { Interval = 33 };
        private readonly Timer _topmost = new Timer { Interval = 2000 };
        private readonly DateTime _born = DateTime.UtcNow;

        private DockPosition _dock;
        private string _device = "";
        private int _dpi = 96;
        private int _ringX, _ringY;
        private RingState _state = RingState.Idle;
        private float _level;

        private bool _mouseDown;
        private bool _dragging;
        private Point _downScreen;
        private Point _downRing;

        public event Action Clicked;
        public event Action DoubleClicked;

        // Single vs double click: when idle, a click waits for the system double-click time before it counts.
        private readonly Timer _clickWait = new Timer();
        private bool _selectMode;
        public event Action<Point> RightClicked;
        public event Action Moved;
        public event Action DragStarted;
        public event Action DragEnded;

        // Resting: after a while with nothing to do the ring shrinks to 60% and dims, still docked at the edge.
        public const double RestScale = 0.6;
        public const double RestOpacity = 0.45;
        private const double RestAnimMs = 320;
        private readonly Timer _restTimer = new Timer { Interval = 10000 };
        private double _rest;          // 0 awake .. 1 resting (animated)
        private double _restTarget;
        private DateTime _lastTick = DateTime.UtcNow;
        private bool _wakeTap;
        private DateTime _swallowUntil;
        public Func<bool> RestAllowed;
        public bool Resting => _restTarget >= 1;
        public double RestAmount => _rest;
        public int MouseDowns { get; private set; }
        public int RestDelayMs { get => _restTimer.Interval; set { _restTimer.Interval = Math.Max(200, value); Poke(); } }

        // Spinner while polishing / pasting.
        public bool Spinning => _state == RingState.Polishing;
        public float SpinAngle { get; private set; }

        public RingWindow() : base(false)
        {
            _anim.Tick += (s, e) => { StepRest(); Render(); };
            _restTimer.Tick += (s, e) => OnRestTimer();
            _topmost.Tick += (s, e) => KeepOnTop();
            _clickWait.Tick += (s, e) => { _clickWait.Stop(); Clicked?.Invoke(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public int RingSize => Scale(RingDip);
        private int Glow => Scale(GlowDip);
        private int Scale(int dip) => (int)Math.Round(dip * _dpi / 96.0);

        public DockEdge Edge => _dock.Edge;
        public Rectangle RingBounds => new Rectangle(_ringX, _ringY, RingSize, RingSize);

        public RingState State
        {
            get => _state;
            set
            {
                _state = value;
                if (value != RingState.Idle) Wake();
                if (value == RingState.Idle) _level = 0;
                UpdateAnim();
                Poke();
                Render();
            }
        }

        /// <summary>Select-to-speak is on: the idle ring shows a slow cream halo.</summary>
        public bool SelectMode
        {
            get => _selectMode;
            set { _selectMode = value; if (value) Wake(); UpdateAnim(); Poke(); Render(); }
        }

        public float Level { set { _level = Math.Max(0f, Math.Min(1f, value)); } }

        public void ShowDocked()
        {
            _dock = AppStorage.LoadRing(out _device);
            if (_dock.Edge == DockEdge.Top || _dock.Edge == DockEdge.Bottom) // older versions could dock there
                _dock = new DockPosition(_dock.Fraction < 0.5 ? DockEdge.Left : DockEdge.Right, _dock.Edge == DockEdge.Top ? 0.1 : 0.9);
            PlaceFromDock();
            Show();
            KeepOnTop();
            _topmost.Start();
            Poke();
        }

        private void UpdateAnim() => _anim.Enabled = _state != RingState.Idle || _selectMode || Math.Abs(_rest - _restTarget) > 0.001;

        /// <summary>Something happened: restart the countdown to resting.</summary>
        public void Poke()
        {
            _restTimer.Stop();
            _restTimer.Start();
        }

        private void OnRestTimer()
        {
            bool idle = _state == RingState.Idle && !_selectMode && !_mouseDown && (RestAllowed?.Invoke() ?? true);
            if (!idle) return; // check again next interval
            _restTimer.Stop();
            if (_restTarget >= 1) return;
            _restTarget = 1;
            _lastTick = DateTime.UtcNow;
            AppStorage.Log("Ring resting");
            UpdateAnim();
        }

        /// <summary>Back to full size (a tap, a drag, or voice typing / select to speak starting).</summary>
        public void Wake()
        {
            if (_restTarget > 0)
            {
                _restTarget = 0;
                _lastTick = DateTime.UtcNow;
                UpdateAnim();
            }
            Poke();
        }

        private void StepRest()
        {
            var now = DateTime.UtcNow;
            double step = (now - _lastTick).TotalMilliseconds / RestAnimMs;
            _lastTick = now;
            if (_rest < _restTarget) _rest = Math.Min(_restTarget, _rest + step);
            else if (_rest > _restTarget) _rest = Math.Max(_restTarget, _rest - step);
            if (Math.Abs(_rest - _restTarget) <= 0.001 && _state == RingState.Idle && !_selectMode) _anim.Enabled = false;
        }

        /// <summary>The ring as drawn right now (smaller while resting, hugging its edge), in screen pixels.</summary>
        public Rectangle VisibleRingBounds
        {
            get
            {
                var r = DrawRect(RingSize, 0);
                return new Rectangle(_ringX + r.X, _ringY + r.Y, r.Width, r.Height);
            }
        }

        /// <summary>The ring's rectangle relative to the ring origin, scaled for resting and anchored to the dock edge.</summary>
        private Rectangle DrawRect(int ring, int offset)
        {
            double ease = _rest * _rest * (3 - 2 * _rest);
            int d = (int)Math.Round(ring * (1 - (1 - RestScale) * ease));
            int x = (ring - d) / 2, y = (ring - d) / 2;
            switch (_dock.Edge)
            {
                case DockEdge.Left: x = 0; break;
                case DockEdge.Right: x = ring - d; break;
                case DockEdge.Top: y = 0; break;
                case DockEdge.Bottom: y = ring - d; break;
            }
            return new Rectangle(offset + x, offset + y, d, d);
        }

        private Screen CurrentScreen()
        {
            return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _device) ?? Screen.PrimaryScreen;
        }

        private void PlaceFromDock()
        {
            var screen = CurrentScreen();
            _device = screen.DeviceName;
            var wa = screen.WorkingArea;
            _dpi = Native.DpiAt(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
            var work = new IntRect(wa.X, wa.Y, wa.Width, wa.Height);
            EdgeSnap.Place(work, _dock, RingSize, Scale(GapDip), out _ringX, out _ringY);
            Render();
        }

        private void SnapToNearestEdge()
        {
            int cx = _ringX + RingSize / 2, cy = _ringY + RingSize / 2;
            var screen = Screen.FromPoint(new Point(cx, cy));
            var wa = screen.WorkingArea;
            _dpi = Native.DpiAt(cx, cy);
            var work = new IntRect(wa.X, wa.Y, wa.Width, wa.Height);
            _dock = EdgeSnap.Snap(work, cx, cy, RingSize);
            _device = screen.DeviceName;
            EdgeSnap.Place(work, _dock, RingSize, Scale(GapDip), out _ringX, out _ringY);
            AppStorage.SaveRing(_dock, _device);
            Render();
            Moved?.Invoke();
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action(PlaceFromDock)); } catch { }
        }

        public void Render()
        {
            if (IsDisposed) return;
            int ring = RingSize, glow = Glow, size = ring + glow * 2;
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (_state != RingState.Idle)
                {
                    double t = (DateTime.UtcNow - _born).TotalSeconds;
                    double breath = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / (_state == RingState.Recording ? 1.6 : 0.9));
                    float strength = (float)(0.45 + 0.35 * breath + (_state == RingState.Recording ? 0.25 * _level : 0));
                    strength = Math.Min(1f, strength);
                    var core = _state == RingState.Recording ? Color.FromArgb(150, 190, 60) : Color.FromArgb(224, 190, 70);
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(0, 0, size - 1, size - 1);
                        using (var brush = new PathGradientBrush(path))
                        {
                            brush.CenterColor = Color.FromArgb((int)(235 * strength), core);
                            brush.SurroundColors = new[] { Color.FromArgb(0, core) };
                            var blend = new ColorBlend(3)
                            {
                                Colors = new[] { Color.FromArgb(0, core), Color.FromArgb((int)(200 * strength), core), Color.FromArgb((int)(235 * strength), core) },
                                Positions = new[] { 0f, 0.35f, 1f }
                            };
                            brush.InterpolationColors = blend;
                            g.FillPath(brush, path);
                        }
                    }
                }

                if (_selectMode && _state == RingState.Idle)
                {
                    double t = (DateTime.UtcNow - _born).TotalSeconds;
                    double breath = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 2.4);
                    float w = Math.Max(2f, glow * 0.45f);
                    float inset = glow * 0.35f;
                    using (var halo = new Pen(Color.FromArgb((int)(90 + 80 * breath), 200, 225, 120), w))
                        g.DrawEllipse(halo, inset, inset, size - inset * 2, size - inset * 2);
                    using (var arc = new Pen(Color.FromArgb(235, 255, 244, 200), w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(arc, inset, inset, size - inset * 2, size - inset * 2, (float)((t * 120) % 360), 70f);
                }

                var rr = DrawRect(ring, glow);
                // The hole in the middle stays see-through but must still take clicks: a layered window ignores
                // the mouse only where alpha is 0, so the whole disc gets an (invisible) alpha of 2.
                using (var hit = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillEllipse(hit, rr);
                double ease = _rest * _rest * (3 - 2 * _rest);
                float opacity = (float)(1 - (1 - RestOpacity) * ease), dark = (float)(1 - 0.35 * ease);
                var state = g.Save();
                if (_state == RingState.Polishing)
                {
                    // Spin the lifebuoy while polishing and pasting, like the Android app.
                    SpinAngle = (float)(((DateTime.UtcNow - _born).TotalSeconds * 300) % 360);
                    g.TranslateTransform(rr.X + rr.Width / 2f, rr.Y + rr.Height / 2f);
                    g.RotateTransform(SpinAngle);
                    g.TranslateTransform(-(rr.X + rr.Width / 2f), -(rr.Y + rr.Height / 2f));
                }
                if (Lifebuoy != null)
                {
                    if (ease > 0.001)
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix00 = dark, Matrix11 = dark, Matrix22 = dark, Matrix33 = opacity, Matrix44 = 1 };
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(Lifebuoy, rr, 0, 0, Lifebuoy.Width, Lifebuoy.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else g.DrawImage(Lifebuoy, rr);
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb((int)(220 * opacity), 220, 40, 40), rr.Width * 0.22f)) g.DrawEllipse(pen, rr.X + rr.Width * 0.15f, rr.Y + rr.Width * 0.15f, rr.Width * 0.7f, rr.Width * 0.7f);
                }
                g.Restore(state);

                if (_state == RingState.Polishing)
                {
                    double t = (DateTime.UtcNow - _born).TotalSeconds;
                    float sweep = 90f;
                    float start = (float)((t * 360) % 360);
                    using (var pen = new Pen(Color.FromArgb(230, 30, 40, 60), Math.Max(2f, ring * 0.06f)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        float inset = glow - Math.Max(2f, ring * 0.06f);
                        g.DrawArc(pen, inset, inset, size - inset * 2, size - inset * 2, start, sweep);
                    }
                }
                Present(bmp, _ringX - glow, _ringY - glow);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            MouseDowns++;
            if (e.Button != MouseButtons.Left) { Wake(); return; }
            _wakeTap = Resting || _rest > 0.05; // the first tap on a resting ring only wakes it
            Wake();
            BeginDrag(Cursor.Position);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown) return;
            DragTo(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                RightClicked?.Invoke(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left || !_mouseDown) return;
            if (EndDrag()) return;
            Tap();
        }

        private void Tap()
        {
            Poke();
            if (_wakeTap || DateTime.UtcNow < _swallowUntil)
            {
                // Woken by this tap (or a quick second tap of the same double-tap): nothing else happens.
                _wakeTap = false;
                _clickWait.Stop();
                _swallowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(200, Math.Min(500, Native.GetDoubleClickTime())));
                AppStorage.Log("Ring woke up");
                return;
            }
            if (_state != RingState.Idle)
            {
                _clickWait.Stop();
                Clicked?.Invoke(); // stopping a recording never waits
            }
            else if (_clickWait.Enabled)
            {
                _clickWait.Stop();
                DoubleClicked?.Invoke();
            }
            else
            {
                _clickWait.Interval = (int)Math.Max(200, Math.Min(500, Native.GetDoubleClickTime()));
                _clickWait.Start();
            }
        }

        public bool IsDragging => _dragging;

        private void BeginDrag(Point screen)
        {
            _mouseDown = true;
            _dragging = false;
            _downScreen = screen;
            _downRing = new Point(_ringX, _ringY);
        }

        private void DragTo(Point p)
        {
            int dx = p.X - _downScreen.X, dy = p.Y - _downScreen.Y;
            var drag = SystemInformation.DragSize;
            if (!_dragging && (Math.Abs(dx) > drag.Width || Math.Abs(dy) > drag.Height))
            {
                _dragging = true;
                _wakeTap = false;
                DragStarted?.Invoke();
            }
            if (_dragging)
            {
                _ringX = _downRing.X + dx;
                _ringY = _downRing.Y + dy;
                Render();
                Moved?.Invoke();
            }
        }

        /// <summary>Ends a press; true if it was a drag (then the ring docks to the nearest side edge).</summary>
        private bool EndDrag()
        {
            _mouseDown = false;
            if (!_dragging) return false;
            _dragging = false;
            SnapToNearestEdge();
            DragEnded?.Invoke();
            return true;
        }

        /// <summary>Moves the ring without docking (it follows the Words bubble being dragged).</summary>
        public void MoveRingTo(int x, int y)
        {
            _ringX = x; _ringY = y;
            Wake();
            Render();
            Moved?.Invoke();
        }

        /// <summary>Docks the ring to the nearest side edge (after the Words bubble was dragged).</summary>
        public void DockNow()
        {
            SnapToNearestEdge();
            DragEnded?.Invoke();
        }

        /// <summary>Self-test: a drag of the ring by (dx, dy) through the same code as the mouse; optionally released.</summary>
        public void SimulateDrag(int dx, int dy, bool release)
        {
            var c = new Point(_ringX + RingSize / 2, _ringY + RingSize / 2);
            BeginDrag(c);
            DragTo(new Point(c.X + dx / 2, c.Y + dy / 2));
            DragTo(new Point(c.X + dx, c.Y + dy));
            if (release) EndDrag();
        }

        public void SimulateRelease() { if (_mouseDown) EndDrag(); }

        /// <summary>For the self-test hook: behaves like a double-click on the ring.</summary>
        public void SimulateDoubleClick() => DoubleClicked?.Invoke();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                if (dpi > 0 && dpi != _dpi && !_dragging)
                {
                    _dpi = dpi;
                    Render();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                _anim.Dispose();
                _topmost.Dispose();
                _restTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Image LoadLifebuoy()
        {
            try
            {
                var s = typeof(RingWindow).Assembly.GetManifestResourceStream("Rapcha.lifebuoy.png");
                return s == null ? null : Image.FromStream(s);
            }
            catch { return null; }
        }
    }
}
