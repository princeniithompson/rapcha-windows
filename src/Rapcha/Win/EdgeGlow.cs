using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// The select-to-speak "mode is on" glow: thin click-through strips along every screen edge,
    /// a soft breathing olive/cream base with two bright light trails running around the edge.
    /// Pixels are written straight into a DIB section (no GDI+), about 30 frames per second,
    /// and the timer only runs while the glow is shown.
    /// </summary>
    public sealed class EdgeGlow : IDisposable
    {
        private const int ThicknessDip = 9;
        private readonly List<Strip> _strips = new List<Strip>();
        private readonly Timer _timer = new Timer { Interval = 33 };
        private readonly DateTime _born = DateTime.UtcNow;

        public bool Active { get; private set; }
        public int WindowCount => _strips.Count;

        public EdgeGlow()
        {
            _timer.Tick += (s, e) => RenderAll();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public void Show()
        {
            if (Active) return;
            Active = true;
            Build();
            RenderAll();
            foreach (var s in _strips) { s.Show(); s.KeepOnTop(); }
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
            Show();
        }

        private void Build()
        {
            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;
                int dpi = Native.DpiAt(b.X + b.Width / 2, b.Y + b.Height / 2);
                int t = Math.Max(4, (int)Math.Round(ThicknessDip * dpi / 96.0));
                int per = 2 * (b.Width + b.Height);
                // Perimeter runs clockwise from the top-left corner: top, right, bottom, left.
                _strips.Add(new Strip(b.X, b.Y, b.Width, t, Side.Top, 0, per));
                _strips.Add(new Strip(b.Right - t, b.Y + t, t, b.Height - 2 * t, Side.Right, b.Width + t, per));
                _strips.Add(new Strip(b.X, b.Bottom - t, b.Width, t, Side.Bottom, b.Width + b.Height, per));
                _strips.Add(new Strip(b.X, b.Y + t, t, b.Height - 2 * t, Side.Left, 2 * b.Width + b.Height + t, per));
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
            private readonly int[] _pixels;
            private readonly float[] _along;
            private IntPtr _dib, _bits, _memDc, _oldObj;

            public Strip(int x, int y, int w, int h, Side side, int perimeterStart, int perimeter) : base(true)
            {
                _x = x; _y = y; _w = Math.Max(1, w); _h = Math.Max(1, h);
                _side = side; _perimeterStart = perimeterStart; _perimeter = Math.Max(1, perimeter);
                _pixels = new int[_w * _h];
                _along = new float[side == Side.Top || side == Side.Bottom ? _w : _h];
            }

            public void Render(double time)
            {
                // Intensity along the strip: breathing base plus two light trails 180 degrees apart.
                double breath = 0.5 + 0.5 * Math.Sin(time * 2 * Math.PI / 2.4);
                double head = (time * 0.11 % 1.0) * _perimeter;
                double trail = _perimeter * 0.16;
                int n = _along.Length;
                for (int i = 0; i < n; i++)
                {
                    int local = (_side == Side.Bottom || _side == Side.Left) ? n - 1 - i : i; // clockwise
                    double p = _perimeterStart + local;
                    double v = 0.30 + 0.14 * breath;
                    for (int k = 0; k < 2; k++)
                    {
                        double h = head + k * _perimeter / 2.0;
                        double d = (h - p) % _perimeter; if (d < 0) d += _perimeter; // distance behind the head
                        if (d < trail) { double f = 1 - d / trail; v += 0.75 * f * f; }
                    }
                    _along[i] = (float)Math.Min(1.0, v);
                }

                // Pixel colours: olive at rest, warming to cream where the trail passes; fade inward.
                for (int y = 0; y < _h; y++)
                {
                    for (int x = 0; x < _w; x++)
                    {
                        float a = (_side == Side.Top || _side == Side.Bottom) ? _along[x] : _along[y];
                        int depth = _side == Side.Top ? y : _side == Side.Bottom ? _h - 1 - y : _side == Side.Left ? x : _w - 1 - x;
                        int thick = (_side == Side.Top || _side == Side.Bottom) ? _h : _w;
                        float fade = 1f - depth / (float)thick;
                        fade *= fade;
                        float alpha = a * fade;
                        float warm = Math.Max(0f, (a - 0.45f) / 0.55f);
                        int r = (int)(150 + (255 - 150) * warm), g = (int)(190 + (241 - 190) * warm), b = (int)(60 + (201 - 60) * warm);
                        int A = (int)(alpha * 235);
                        // premultiplied BGRA
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
