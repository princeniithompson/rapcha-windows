using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// The select-to-speak overlay's glowing edge (like Circle to Search): click-through strips along the
    /// edges of one monitor, a blue / purple / green gradient flowing around the screen with a brighter sweep.
    /// Owned by the overlay so it always stays above it. Pixels go straight into a DIB section (no GDI+),
    /// at most ~30 frames per second, and only while shown.
    /// </summary>
    public sealed class EdgeGlow : IDisposable
    {
        private readonly int ThicknessDip;
        private readonly bool _outward;
        private Rectangle _bounds;
        private Form _owner;
        private readonly List<Strip> _strips = new List<Strip>();
        private readonly Timer _timer;
        private readonly DateTime _born = DateTime.UtcNow;

        public bool Active { get; private set; }
        public int WindowCount => _strips.Count;

        public Rectangle Bounds => _bounds;

        /// <param name="thicknessDip">Glow depth.</param>
        /// <param name="outward">false: glows inward from the edges of the bounds (the screen edge);
        /// true: an outline just outside the bounds (a text field), brightest at the field's border.</param>
        public EdgeGlow(int thicknessDip = 12, bool outward = false, int intervalMs = 33)
        {
            ThicknessDip = thicknessDip;
            _outward = outward;
            _timer = new Timer { Interval = intervalMs };
            _timer.Tick += (s, e) => RenderAll();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public void Show(Rectangle bounds, Form owner)
        {
            if (Active) return;
            Active = true;
            _bounds = bounds;
            _owner = owner;
            Build();
            RenderAll();
            foreach (var s in _strips) { if (owner != null) s.Owner = owner; s.Show(); s.KeepOnTop(); }
            _timer.Start();
        }

        public void Hide()
        {
            if (!Active) return;
            Active = false;
            _timer.Stop();
            foreach (var s in _strips) { s.Hide(); s.Dispose(); }
            _strips.Clear(); // free the pixel buffers while the mode is off
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!Active) return;
            Hide();
            Show(_bounds, _owner);
        }

        private void Build()
        {
            {
                var b = _bounds;
                int dpi = Native.DpiAt(b.X + b.Width / 2, b.Y + b.Height / 2);
                int t = Math.Max(4, (int)Math.Round(ThicknessDip * dpi / 96.0));
                if (_outward) b.Inflate(t, t);
                int per = 2 * (b.Width + b.Height);
                // Perimeter runs clockwise from the top-left corner: top, right, bottom, left.
                _strips.Add(new Strip(b.X, b.Y, b.Width, t, Side.Top, 0, per, _outward));
                _strips.Add(new Strip(b.Right - t, b.Y + t, t, b.Height - 2 * t, Side.Right, b.Width + t, per, _outward));
                _strips.Add(new Strip(b.X, b.Bottom - t, b.Width, t, Side.Bottom, b.Width + b.Height, per, _outward));
                _strips.Add(new Strip(b.X, b.Y + t, t, b.Height - 2 * t, Side.Left, 2 * b.Width + b.Height + t, per, _outward));
            }
        }

        private void RenderAll()
        {
            if (!Active) return;
            double time = (DateTime.UtcNow - _born).TotalSeconds;
            foreach (var s in _strips) s.Render(time);
        }

        public void Dispose()
        {
            Hide();
            _timer.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        private enum Side { Top, Right, Bottom, Left }

        private sealed class Strip : LayeredWindow
        {
            private readonly int _x, _y, _w, _h, _perimeterStart, _perimeter;
            private readonly Side _side;
            private readonly bool _outward;
            private readonly int[] _pixels;
            private readonly float[] _along;
            private int[] _rgb;
            private IntPtr _dib, _bits, _memDc, _oldObj;

            public Strip(int x, int y, int w, int h, Side side, int perimeterStart, int perimeter, bool outward) : base(true)
            {
                _outward = outward;
                _x = x; _y = y; _w = Math.Max(1, w); _h = Math.Max(1, h);
                _side = side; _perimeterStart = perimeterStart; _perimeter = Math.Max(1, perimeter);
                _pixels = new int[_w * _h];
                _along = new float[side == Side.Top || side == Side.Bottom ? _w : _h];
            }

            private static readonly int[][] Palette =
            {
                new[] { 66, 133, 244 },   // blue
                new[] { 160, 92, 255 },   // purple
                new[] { 52, 199, 120 },   // green
            };

            public void Render(double time)
            {
                int n = _along.Length;
                if (_rgb == null) _rgb = new int[n * 3];
                var rgb = _rgb;
                double sweep = (time * 0.18 % 1.0) * _perimeter;
                double breath = 0.5 + 0.5 * Math.Sin(time * 2 * Math.PI / 2.6);
                for (int i = 0; i < n; i++)
                {
                    int local = (_side == Side.Bottom || _side == Side.Left) ? n - 1 - i : i; // clockwise
                    double p = _perimeterStart + local;
                    // colour flows around the edge
                    double u = (p / _perimeter * 2.0 + time * 0.10) % 1.0; if (u < 0) u += 1;
                    double seg = u * 3; int k = (int)seg; double f = seg - k;
                    var c0 = Palette[k % 3]; var c1 = Palette[(k + 1) % 3];
                    rgb[i * 3] = (int)(c0[0] + (c1[0] - c0[0]) * f);
                    rgb[i * 3 + 1] = (int)(c0[1] + (c1[1] - c0[1]) * f);
                    rgb[i * 3 + 2] = (int)(c0[2] + (c1[2] - c0[2]) * f);
                    double d = (sweep - p) % _perimeter; if (d < 0) d += _perimeter;
                    double trail = _perimeter * 0.22;
                    double v = 0.55 + 0.15 * breath + (d < trail ? 0.45 * (1 - d / trail) : 0);
                    _along[i] = (float)Math.Min(1.0, v);
                }

                for (int y = 0; y < _h; y++)
                {
                    for (int x = 0; x < _w; x++)
                    {
                        bool horiz = _side == Side.Top || _side == Side.Bottom;
                        int i = horiz ? x : y;
                        int depth = _side == Side.Top ? y : _side == Side.Bottom ? _h - 1 - y : _side == Side.Left ? x : _w - 1 - x;
                        int thick = horiz ? _h : _w;
                        if (_outward) depth = thick - 1 - depth; // brightest against the field, fading outwards
                        float fade = 1f - depth / (float)thick;
                        fade *= fade;
                        float a = _along[i] * fade;
                        // a whiter core right at the edge
                        float white = depth < thick / 4 ? 0.35f * _along[i] : 0f;
                        int r = (int)(rgb[i * 3] + (255 - rgb[i * 3]) * white), g = (int)(rgb[i * 3 + 1] + (255 - rgb[i * 3 + 1]) * white), b = (int)(rgb[i * 3 + 2] + (255 - rgb[i * 3 + 2]) * white);
                        int A = (int)(a * 245);
                        _pixels[y * _w + x] = (A << 24) | ((r * A / 255) << 16) | ((g * A / 255) << 8) | (b * A / 255);
                    }
                }
                Push();
            }

            private void Push()
            {
                if (!IsHandleCreated) CreateHandle();
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                try
                {
                    if (_dib == IntPtr.Zero)
                    {
                        var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32 };
                        _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
                        _memDc = Native.CreateCompatibleDC(screen);
                        _oldObj = Native.SelectObject(_memDc, _dib);
                    }
                    if (_bits == IntPtr.Zero) return;
                    Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
                    var size = new Native.SIZE(_w, _h);
                    var src = new Native.POINT(0, 0);
                    var dst = new Native.POINT(_x, _y);
                    var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
                    Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                }
                finally { Native.ReleaseDC(IntPtr.Zero, screen); }
            }

            protected override void Dispose(bool disposing)
            {
                if (_memDc != IntPtr.Zero)
                {
                    Native.SelectObject(_memDc, _oldObj);
                    Native.DeleteDC(_memDc);
                    _memDc = IntPtr.Zero;
                }
                if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
                base.Dispose(disposing);
            }
        }
    }
}
