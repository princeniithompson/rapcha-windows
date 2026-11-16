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
        private readonly Timer _anim = new Timer { Interval = 15 };
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
        public const double WakeAnimMs = 220, RestAnimMs = 320;
        private readonly Timer _restTimer = new Timer { Interval = 10000 };
        private double _rest;          // 0 awake .. 1 resting (already eased; drawn as is)
        private double _restTarget;
        // One continuous, time-based transition: from _animFrom at _animStart to _restTarget over _animMs.
        // A wake during a rest (or the other way) starts from wherever the ring is now, so it never jumps.
        private double _animFrom;
        private DateTime _animStart = DateTime.UtcNow;
        private double _animMs = WakeAnimMs;
        private DateTime _lastTick = DateTime.UtcNow;
        /// <summary>Self-test: ring diameters (px) of every frame drawn during a transition.</summary>
        public System.Collections.Generic.List<float> FrameLog;
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
            _anim.Interval = 15;
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
            PlaceFromDock();
            Show();
            KeepOnTop();
            _topmost.Start();
            Poke();
        }

        private void UpdateAnim() => _anim.Enabled = _state != RingState.Idle || Math.Abs(_rest - _restTarget) > 0.001;

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
            BeginTransition(1);
            AppStorage.Log("Ring resting");
        }

        /// <summary>Back to full size (a tap, a drag, or voice typing / select to speak starting).</summary>
        public void Wake()
        {
            if (_restTarget > 0) BeginTransition(0);   // already waking or awake: no restart, just the timer below
            Poke();
        }

        private void BeginTransition(double target)
        {
            _animFrom = _rest;
            _restTarget = target;
            _animStart = DateTime.UtcNow;
            double full = target < 0.5 ? WakeAnimMs : RestAnimMs;
            _animMs = Math.Max(90, full * Math.Abs(target - _rest));
            UpdateAnim();
            StepRest();
            Render();
        }

        /// <summary>Wake: ease-out cubic (quick start, soft landing). Rest: ease-in-out cubic.</summary>
        public static double Ease(double t, bool waking)
        {
            t = Math.Max(0, Math.Min(1, t));
            if (waking) return 1 - Math.Pow(1 - t, 3);
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private void StepRest()
        {
            double t = (DateTime.UtcNow - _animStart).TotalMilliseconds / _animMs;
            _rest = t >= 1 ? _restTarget : _animFrom + (_restTarget - _animFrom) * Ease(t, _restTarget < _animFrom);
            if (Math.Abs(_rest - _restTarget) <= 0.0005)
            {
                _rest = _restTarget;
                if (_state == RingState.Idle) _anim.Enabled = false;
            }
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
            var f = DrawRectF(ring, offset);
            return Rectangle.Round(f);
        }

        /// <summary>Sub-pixel version used for drawing, so the ring grows smoothly and stays anchored to its edge.</summary>
        private RectangleF DrawRectF(int ring, int offset)
        {
            float d = (float)(ring * (1 - (1 - RestScale) * _rest));
            float x = (ring - d) / 2f, y = (ring - d) / 2f;
            switch (_dock.Edge)
            {
                case DockEdge.Left: x = 0; break;
                case DockEdge.Right: x = ring - d; break;
                case DockEdge.Top: y = 0; break;
                case DockEdge.Bottom: y = ring - d; break;
            }
            return new RectangleF(offset + x, offset + y, d, d);
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
            int glow = Glow;
            using (var bmp = Compose()) Present(bmp, _ringX - glow, _ringY - glow);
        }

        /// <summary>The ring as it would be drawn right now (also used by the self-test to compare frames).</summary>
        /// <summary>Is the animation timer running (it should not be while select to speak is merely on).</summary>
        public bool Animating => _anim.Enabled;

        public Bitmap Compose()
        {
            int ring = RingSize, glow = Glow, size = ring + glow * 2;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
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
                    var core = _state == RingState.Recording ? Accent.Color : Accent.Lighter(0.28); // the user's glow colour
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

                // Select to speak: no ring decoration at all (the steady screen-edge glow says it is on).

                var rr = DrawRectF(ring, glow);
                FrameLog?.Add(rr.Width);
                // The hole in the middle stays see-through but must still take clicks: a layered window ignores
                // the mouse only where alpha is 0, so the whole disc gets an (invisible) alpha of 2.
                using (var hit = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillEllipse(hit, rr);
                double ease = _rest;
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
                    if (ease > 0)
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix00 = dark, Matrix11 = dark, Matrix22 = dark, Matrix33 = opacity, Matrix44 = 1 };
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            var dest = new[] { new PointF(rr.X, rr.Y), new PointF(rr.Right, rr.Y), new PointF(rr.X, rr.Bottom) };
                            g.DrawImage(Lifebuoy, dest, new RectangleF(0, 0, Lifebuoy.Width, Lifebuoy.Height), GraphicsUnit.Pixel, ia);
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
            }
            return bmp;
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
                _clickWait.Stop();
                if (_wakeTap) { _swallowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(200, Math.Min(500, Native.GetDoubleClickTime()))); AppStorage.Log("Ring woke up"); }
                else AppStorage.Log("Ring tap swallowed (second tap of a double-click)");
                _wakeTap = false;
                return;
            }
            LastTapAt = DateTime.UtcNow;
            if (_selectMode && _state == RingState.Idle)
            {
                // Reading aloud: one tap stops it straight away. The rest of a double-click (and any tap in the
                // next moments) is swallowed, so it can never fall through into voice typing.
                _clickWait.Stop();
                _swallowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(400, Math.Min(600, Native.GetDoubleClickTime() + 100)));
                Clicked?.Invoke();
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
        /// <summary>When the last tap (a click that was not a drag) happened.</summary>
        public DateTime LastTapAt { get; private set; } = DateTime.MinValue;

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
