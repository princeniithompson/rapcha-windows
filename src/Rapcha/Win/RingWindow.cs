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
        public event Action<Point> RightClicked;
        public event Action Moved;

        public RingWindow() : base(false)
        {
            _anim.Tick += (s, e) => Render();
            _topmost.Tick += (s, e) => KeepOnTop();
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
                _anim.Enabled = value != RingState.Idle;
                if (value == RingState.Idle) _level = 0;
                Render();
            }
        }

        public float Level { set { _level = Math.Max(0f, Math.Min(1f, value)); } }

        public void ShowDocked()
        {
            _dock = AppStorage.LoadRing(out _device);
            PlaceFromDock();
            Show();
            KeepOnTop();
            _topmost.Start();
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

                if (Lifebuoy != null) g.DrawImage(Lifebuoy, new Rectangle(glow, glow, ring, ring));
                else
                {
                    using (var pen = new Pen(Color.FromArgb(220, 40, 40), ring * 0.22f)) g.DrawEllipse(pen, glow + ring * 0.15f, glow + ring * 0.15f, ring * 0.7f, ring * 0.7f);
                }

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
            if (e.Button != MouseButtons.Left) return;
            _mouseDown = true;
            _dragging = false;
            _downScreen = Cursor.Position;
            _downRing = new Point(_ringX, _ringY);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown) return;
            var p = Cursor.Position;
            int dx = p.X - _downScreen.X, dy = p.Y - _downScreen.Y;
            var drag = SystemInformation.DragSize;
            if (!_dragging && (Math.Abs(dx) > drag.Width || Math.Abs(dy) > drag.Height)) _dragging = true;
            if (_dragging)
            {
                _ringX = _downRing.X + dx;
                _ringY = _downRing.Y + dy;
                Render();
                Moved?.Invoke();
            }
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
            _mouseDown = false;
            if (_dragging)
            {
                _dragging = false;
                SnapToNearestEdge();
            }
            else
            {
                Clicked?.Invoke();
            }
        }

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
