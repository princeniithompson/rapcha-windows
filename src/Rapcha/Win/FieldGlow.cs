using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// The voice-typing field outline: a rounded rectangle (radius ~10 px at 100 %, scaled for DPI) just outside
    /// the text field, a crisp line in the user's glow colour with a soft glow fading outwards and a slow bright
    /// sweep. Drawn into four click-through strips around the field only (never over it), so it stays light even
    /// for a big document. The per-pixel distance to the rounded shape is computed once per show; each frame only
    /// mixes the colour.
    /// </summary>
    public sealed class FieldGlow : IDisposable
    {
        public const int RadiusDip = 10;
        public const int GlowDip = 9;

        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly Timer _timer;
        private readonly DateTime _born = DateTime.UtcNow;
        private Color _color = Color.FromArgb(0xA0, 0xBE, 0x3C);

        public bool Active { get; private set; }
        public Rectangle Field { get; private set; }
        public int RadiusPx { get; private set; }
        public int WindowCount => _pieces.Count;

        public Color Color
        {
            get => _color;
            set { _color = Color.FromArgb(255, value); if (Active) RenderAll(); }
        }

        public FieldGlow(int intervalMs = 50)
        {
            _timer = new Timer { Interval = intervalMs };
            _timer.Tick += (s, e) => RenderAll();
        }

        public void Show(Rectangle field)
        {
            Hide();
            if (field.Width < 2 || field.Height < 2) return;
            Active = true;
            Field = field;
            int dpi = Native.DpiAt(field.X + field.Width / 2, field.Y + field.Height / 2);
            double s = dpi / 96.0;
            int m = Math.Max(5, (int)Math.Round(GlowDip * s));
            var shape = RectangleF.FromLTRB(field.Left - 1.5f, field.Top - 1.5f, field.Right + 1.5f, field.Bottom + 1.5f);
            float r = (float)Math.Min(Math.Round(RadiusDip * s), Math.Min(shape.Width, shape.Height) / 2);
            RadiusPx = (int)Math.Round(r);
            var outer = Rectangle.FromLTRB(field.Left - m - 2, field.Top - m - 2, field.Right + m + 2, field.Bottom + m + 2);
            int band = m + 2 + RadiusPx + 2;                       // rows/columns that can hold any glow pixel
            if (outer.Height <= 2 * band || outer.Width <= 2 * band)
            {
                // Small field: two halves cover everything.
                int top = outer.Height / 2;
                _pieces.Add(new Piece(new Rectangle(outer.X, outer.Y, outer.Width, top), shape, r, m));
                _pieces.Add(new Piece(new Rectangle(outer.X, outer.Y + top, outer.Width, outer.Height - top), shape, r, m));
            }
            else
            {
                _pieces.Add(new Piece(new Rectangle(outer.X, outer.Y, outer.Width, band), shape, r, m));
                _pieces.Add(new Piece(new Rectangle(outer.X, outer.Bottom - band, outer.Width, band), shape, r, m));
                _pieces.Add(new Piece(new Rectangle(outer.X, outer.Y + band, band, outer.Height - 2 * band), shape, r, m));
                _pieces.Add(new Piece(new Rectangle(outer.Right - band, outer.Y + band, band, outer.Height - 2 * band), shape, r, m));
            }
            RenderAll();
            foreach (var p in _pieces) { p.Show(); p.KeepOnTop(); }
            _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            Active = false;
            foreach (var p in _pieces) { p.Hide(); p.Dispose(); }
            _pieces.Clear();
        }

        /// <summary>The colour drawn at a screen pixel (alpha un-premultiplied); transparent if none.</summary>
        public Color PixelAt(int x, int y)
        {
            foreach (var p in _pieces) if (p.Area.Contains(x, y)) return p.PixelAt(x, y);
            return Color.Transparent;
        }

        private void RenderAll()
        {
            if (!Active) return;
            double t = (DateTime.UtcNow - _born).TotalSeconds;
            foreach (var p in _pieces) p.Render(t, _color);
        }

        public void Dispose()
        {
            Hide();
            _timer.Dispose();
        }

        private sealed class Piece : LayeredWindow
        {
            public Rectangle Area { get; }
            private readonly int[] _pixels;
            private readonly float[] _line, _glow, _angle;   // per pixel: crisp line weight, outer glow weight, position around the shape (0..1)
            private IntPtr _dib, _bits, _memDc, _oldObj;

            public Piece(Rectangle area, RectangleF shape, float r, int m) : base(true)
            {
                Area = new Rectangle(area.X, area.Y, Math.Max(1, area.Width), Math.Max(1, area.Height));
                int n = Area.Width * Area.Height;
                _pixels = new int[n]; _line = new float[n]; _glow = new float[n]; _angle = new float[n];
                float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Height / 2;
                float hx = shape.Width / 2 - r, hy = shape.Height / 2 - r;
                double sigma = m * 0.42;
                for (int y = 0; y < Area.Height; y++)
                    for (int x = 0; x < Area.Width; x++)
                    {
                        float px = Area.X + x + 0.5f - cx, py = Area.Y + y + 0.5f - cy;
                        float qx = Math.Abs(px) - hx, qy = Math.Abs(py) - hy;
                        float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                        float d = (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r; // signed distance to the rounded rect
                        int i = y * Area.Width + x;
                        _line[i] = Math.Max(0f, 1f - Math.Abs(d) / 1.6f);
                        _glow[i] = d <= 0 ? 0f : (float)(Math.Exp(-(d * d) / (2 * sigma * sigma)) * 0.75);
                        _angle[i] = (float)((Math.Atan2(py, px) / (2 * Math.PI) + 1.25) % 1.0);
                    }
            }

            public void Render(double t, Color c)
            {
                double sweep = t * 0.22 % 1.0;
                float breath = (float)(0.82 + 0.18 * Math.Sin(t * 2 * Math.PI / 2.4));
                for (int i = 0; i < _pixels.Length; i++)
                {
                    float line = _line[i], glow = _glow[i];
                    if (line <= 0f && glow <= 0.004f) { _pixels[i] = 0; continue; }
                    double dd = sweep - _angle[i]; if (dd < 0) dd += 1;
                    float boost = dd < 0.18 ? (float)(1 - dd / 0.18) : 0f;
                    float a = Math.Max(line * 0.95f, glow * breath * (1f + 0.35f * boost));
                    if (a > 1f) a = 1f;
                    float white = line * (0.18f + 0.25f * boost);
                    int R = (int)(c.R + (255 - c.R) * white), G = (int)(c.G + (255 - c.G) * white), B = (int)(c.B + (255 - c.B) * white);
                    int A = (int)(a * 250);
                    _pixels[i] = (A << 24) | ((R * A / 255) << 16) | ((G * A / 255) << 8) | (B * A / 255);
                }
                Push();
            }

            public Color PixelAt(int x, int y)
            {
                int v = _pixels[(y - Area.Y) * Area.Width + (x - Area.X)];
                int a = (v >> 24) & 255;
                if (a == 0) return Color.Transparent;
                return Color.FromArgb(a, Math.Min(255, ((v >> 16) & 255) * 255 / a), Math.Min(255, ((v >> 8) & 255) * 255 / a), Math.Min(255, (v & 255) * 255 / a));
            }

            private void Push()
            {
                if (!IsHandleCreated) CreateHandle();
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                try
                {
                    if (_dib == IntPtr.Zero)
                    {
                        var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = Area.Width, biHeight = -Area.Height, biPlanes = 1, biBitCount = 32 };
                        _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
                        _memDc = Native.CreateCompatibleDC(screen);
                        _oldObj = Native.SelectObject(_memDc, _dib);
                    }
                    if (_bits == IntPtr.Zero) return;
                    Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
                    var size = new Native.SIZE(Area.Width, Area.Height);
                    var src = new Native.POINT(0, 0);
                    var dst = new Native.POINT(Area.X, Area.Y);
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
