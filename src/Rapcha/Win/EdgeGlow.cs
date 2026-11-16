using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// The select-to-speak screen-edge glow (like Google's Circle to Search): a soft, multi-layer glow inside the
    /// edges of one monitor that follows rounded screen corners (radius ~20 px at 100 %, DPI-scaled), in the
    /// user's glow colour. It fades in, breathes slowly and a gentle highlight travels around the edge, all with
    /// smooth easing; it fades out when hidden. Four click-through strips owned by the overlay; the per-pixel
    /// shape is computed once per show and each frame only mixes intensity from lookup tables (~25 fps).
    /// </summary>
    public sealed class EdgeGlow : IDisposable
    {
        public const int RadiusDip = 20, BandDip = 44;
        public const double FadeInS = 0.35, FadeOutS = 0.22, BreathS = 3.6, TravelS = 8.0;

        private Rectangle _bounds;
        private Form _owner;
        private readonly List<Strip> _strips = new List<Strip>();
        private readonly Timer _timer;
        private DateTime _shownAt, _hideAt;
        private bool _hiding;

        public bool Active { get; private set; }
        public int WindowCount => _strips.Count;
        public Rectangle Bounds => _bounds;
        public Color Color { get; set; } = Color.FromArgb(0xA0, 0xBE, 0x3C);

        public EdgeGlow(int intervalMs = 40)
        {
            _timer = new Timer { Interval = intervalMs };
            _timer.Tick += (s, e) => RenderAll();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public void Show(Rectangle bounds, Form owner)
        {
            if (Active && _hiding) FinishHide();
            if (Active) return;
            Active = true;
            _hiding = false;
            _bounds = bounds;
            _owner = owner;
            _shownAt = DateTime.UtcNow;
            int dpi = Native.DpiAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var shape = new GlowShape(bounds, dpi / 96.0);
            var b = bounds; int t = shape.Band;
            _strips.Add(new Strip(new Rectangle(b.X, b.Y, b.Width, t), shape));
            _strips.Add(new Strip(new Rectangle(b.X, b.Bottom - t, b.Width, t), shape));
            _strips.Add(new Strip(new Rectangle(b.X, b.Y + t, t, b.Height - 2 * t), shape));
            _strips.Add(new Strip(new Rectangle(b.Right - t, b.Y + t, t, b.Height - 2 * t), shape));
            RenderAll();
            foreach (var s in _strips) { if (owner != null) s.Owner = owner; s.Show(); s.KeepOnTop(); }
            _timer.Start();
        }

        /// <summary>Fades out (~0.2 s), then removes the strips.</summary>
        public void Hide()
        {
            if (!Active || _hiding) return;
            _hiding = true;
            _hideAt = DateTime.UtcNow;
            foreach (var st in _strips) { try { st.Owner = null; } catch { } } // outlive the overlay while fading
        }

        /// <summary>Removes the strips immediately (also frees their pixel buffers).</summary>
        public void HideNow()
        {
            if (!Active) return;
            FinishHide();
        }

        private void FinishHide()
        {
            _timer.Stop();
            Active = false;
            _hiding = false;
            foreach (var s in _strips) { s.Hide(); s.Dispose(); }
            _strips.Clear();
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!Active || _hiding) return;
            var b = _bounds; var o = _owner;
            FinishHide();
            Show(b, o);
        }

        /// <summary>Overall intensity for the frame: eased fade in / out.</summary>
        private double Fade(DateTime now)
        {
            double fin = Math.Min(1, (now - _shownAt).TotalSeconds / FadeInS);
            double f = 1 - Math.Pow(1 - fin, 3); // ease-out cubic
            if (_hiding)
            {
                double fo = Math.Min(1, (now - _hideAt).TotalSeconds / FadeOutS);
                f *= 1 - fo * fo * (3 - 2 * fo); // smoothstep down
            }
            return f;
        }

        private void RenderAll()
        {
            if (!Active) return;
            var now = DateTime.UtcNow;
            double fade = Fade(now);
            if (_hiding && fade <= 0.001) { FinishHide(); return; }
            var frame = GlowShape.Frame((now - _shownAt).TotalSeconds, fade, Color);
            foreach (var s in _strips) s.Render(frame);
        }

        public void Dispose()
        {
            HideNow();
            _timer.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        /// <summary>Renders the glow for a screen of the given size over a background (CI screenshot).</summary>
        public static Bitmap RenderPreview(Bitmap background, double scale, Color color, double time)
        {
            var size = background.Size;
            var shape = new GlowShape(new Rectangle(0, 0, size.Width, size.Height), scale);
            var frame = GlowShape.Frame(time, 1.0, color);
            var px = new int[size.Width * size.Height];
            shape.Fill(new Rectangle(0, 0, size.Width, size.Height), frame, px);
            var bmp = new Bitmap(background);
            var data = bmp.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var dst = new int[px.Length];
            Marshal.Copy(data.Scan0, dst, 0, dst.Length);
            for (int i = 0; i < px.Length; i++)
            {
                int s = px[i], a = (s >> 24) & 255;
                if (a == 0) continue;
                int d = dst[i];
                int r = ((s >> 16) & 255) + (((d >> 16) & 255) * (255 - a) / 255);
                int g = ((s >> 8) & 255) + (((d >> 8) & 255) * (255 - a) / 255);
                int b = (s & 255) + ((d & 255) * (255 - a) / 255);
                dst[i] = (255 << 24) | (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, b);
            }
            Marshal.Copy(dst, 0, data.Scan0, dst.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        /// <summary>Alpha (0–255) the glow would have at a point of a screen (for the self-test).</summary>
        public static int AlphaAt(Size screen, double scale, int x, int y)
        {
            var shape = new GlowShape(new Rectangle(Point.Empty, screen), scale);
            var px = new int[1];
            shape.Fill(new Rectangle(x, y, 1, 1), GlowShape.Frame(0.0, 1.0, Color.White), px);
            return (px[0] >> 24) & 255;
        }

        /// <summary>The rounded-rectangle glow field of one monitor.</summary>
        internal sealed class GlowShape
        {
            public readonly Rectangle Screen;
            public readonly int Band;
            private readonly float _r, _s;

            public GlowShape(Rectangle screen, double scale)
            {
                Screen = screen;
                _s = (float)scale;
                _r = (float)Math.Round(RadiusDip * scale);
                Band = (int)Math.Ceiling(BandDip * scale);
            }

            /// <summary>Per frame: overall alpha, highlight position and the colour ramps.</summary>
            public sealed class FrameInfo
            {
                public double Intensity, Highlight;
                public Color Color;
                public float[] Bump = new float[512];   // highlight strength by distance along the edge (0..0.5)
            }

            public static FrameInfo Frame(double t, double fade, Color c)
            {
                var f = new FrameInfo { Color = c };
                double breath = 0.5 - 0.5 * Math.Cos(t * 2 * Math.PI / BreathS);     // eased 0..1
                f.Intensity = fade * (0.80 + 0.20 * breath);
                double h = t / TravelS % 1.0;
                f.Highlight = h;
                for (int i = 0; i < f.Bump.Length; i++)
                {
                    double du = i / (double)(f.Bump.Length - 1) * 0.5;
                    f.Bump[i] = (float)Math.Exp(-(du * du) / (2 * 0.07 * 0.07));
                }
                return f;
            }

            /// <summary>Base weights for one pixel: alpha (0..1), white core (0..1), position along the edge (0..1).</summary>
            public void Weights(float px, float py, out float alpha, out float white, out float along)
            {
                float cx = Screen.X + Screen.Width / 2f, cy = Screen.Y + Screen.Height / 2f;
                float hx = Screen.Width / 2f - _r, hy = Screen.Height / 2f - _r;
                float qx = Math.Abs(px - cx) - hx, qy = Math.Abs(py - cy) - hy;
                float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                float d = (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - _r; // < 0 inside
                along = (float)((Math.Atan2((py - cy) / Screen.Height, (px - cx) / Screen.Width) / (2 * Math.PI) + 1.0) % 1.0);
                if (d >= 1f) { alpha = 0; white = 0; return; }
                float x = Math.Max(0f, -d) / _s; // inward distance in dips
                double core = Math.Exp(-(x * x) / (2 * 1.2 * 1.2));
                double inner = Math.Exp(-(x * x) / (2 * 4.0 * 4.0));
                double mid = Math.Exp(-(x * x) / (2 * 9.0 * 9.0));
                double haze = Math.Exp(-(x * x) / (2 * 16.0 * 16.0));
                double a = 0.50 * core + 0.40 * inner + 0.30 * mid + 0.20 * haze;
                if (d > 0) a *= 1 - d;     // anti-aliased rounded edge
                alpha = (float)Math.Min(1.0, a);
                white = (float)(0.55 * core);
            }

            public void Fill(Rectangle area, FrameInfo f, int[] pixels)
            {
                var pre = Precompute(area);
                Mix(pre, f, pixels);
            }

            public sealed class Pre { public int[] Index; public float[] Alpha, White, Along; }

            public Pre Precompute(Rectangle area)
            {
                var idx = new List<int>(); var al = new List<float>(); var wh = new List<float>(); var po = new List<float>();
                for (int y = 0; y < area.Height; y++)
                    for (int x = 0; x < area.Width; x++)
                    {
                        Weights(area.X + x + 0.5f, area.Y + y + 0.5f, out float a, out float w, out float u);
                        if (a < 0.004f) continue;
                        idx.Add(y * area.Width + x); al.Add(a); wh.Add(w); po.Add(u);
                    }
                return new Pre { Index = idx.ToArray(), Alpha = al.ToArray(), White = wh.ToArray(), Along = po.ToArray() };
            }

            public static void Mix(Pre p, FrameInfo f, int[] pixels)
            {
                var c = f.Color;
                float h = (float)f.Highlight, inten = (float)f.Intensity;
                int n = f.Bump.Length - 1;
                for (int k = 0; k < p.Index.Length; k++)
                {
                    float du = Math.Abs(p.Along[k] - h); if (du > 0.5f) du = 1 - du;
                    float bump = f.Bump[(int)(du * 2 * n)];
                    float a = p.Alpha[k] * inten * (0.82f + 0.38f * bump);
                    if (a > 1) a = 1;
                    float w = Math.Min(1f, p.White[k] + 0.22f * bump * p.Alpha[k]);
                    int R = (int)(c.R + (255 - c.R) * w), G = (int)(c.G + (255 - c.G) * w), B = (int)(c.B + (255 - c.B) * w);
                    int A = (int)(a * 250);
                    pixels[p.Index[k]] = (A << 24) | ((R * A / 255) << 16) | ((G * A / 255) << 8) | (B * A / 255);
                }
            }
        }

        private sealed class Strip : LayeredWindow
        {
            private readonly Rectangle _area;
            private readonly int[] _pixels;
            private readonly GlowShape.Pre _pre;
            private IntPtr _dib, _bits, _memDc, _oldObj;

            public Strip(Rectangle area, GlowShape shape) : base(true)
            {
                _area = new Rectangle(area.X, area.Y, Math.Max(1, area.Width), Math.Max(1, area.Height));
                _pixels = new int[_area.Width * _area.Height];
                _pre = shape.Precompute(_area);
            }

            public void Render(GlowShape.FrameInfo f)
            {
                GlowShape.Mix(_pre, f, _pixels);
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
                        var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = _area.Width, biHeight = -_area.Height, biPlanes = 1, biBitCount = 32 };
                        _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
                        _memDc = Native.CreateCompatibleDC(screen);
                        _oldObj = Native.SelectObject(_memDc, _dib);
                    }
                    if (_bits == IntPtr.Zero) return;
                    Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
                    var size = new Native.SIZE(_area.Width, _area.Height);
                    var src = new Native.POINT(0, 0);
                    var dst = new Native.POINT(_area.X, _area.Y);
                    var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
                    Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                }
                finally { Native.ReleaseDC(IntPtr.Zero, screen); }
            }

            protected override void Dispose(bool disposing)
            {
                if (_memDc != IntPtr.Zero) { Native.SelectObject(_memDc, _oldObj); Native.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
                if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
                base.Dispose(disposing);
            }
        }
    }
}
