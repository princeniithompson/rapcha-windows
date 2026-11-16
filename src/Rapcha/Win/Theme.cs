using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>Colours, fonts and shapes of the main window (like the Android app).</summary>
    internal static class Theme
    {
        public static readonly Color Cream = Color.FromArgb(255, 243, 200);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(236, 226, 186);
        public static readonly Color Ink = Color.FromArgb(38, 40, 30);
        public static readonly Color Muted = Color.FromArgb(118, 114, 96);
        public static readonly Color Teal = Color.FromArgb(24, 128, 106);
        public static readonly Color Pill = Color.FromArgb(226, 214, 120);      // active nav: olive/yellow
        public static readonly Color PillHover = Color.FromArgb(246, 234, 172);
        public static readonly Color Olive = Color.FromArgb(128, 140, 40);
        public static readonly Color Chip = Color.FromArgb(247, 240, 204);
        public static readonly Color ButtonBorder = Color.FromArgb(214, 204, 160);
        public static readonly Color Danger = Color.FromArgb(160, 52, 40);

        public static readonly string Serif = PickFamily("Georgia", "Cambria", "Times New Roman");
        public static readonly string Sans = PickFamily("Segoe UI", "Tahoma", "Arial");

        private static string PickFamily(params string[] names)
        {
            foreach (var n in names)
            {
                try { using (var f = new FontFamily(n)) return f.Name; } catch { }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillCard(Graphics g, Rectangle r, float radius, Color fill, Color border)
        {
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.A > 0) using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags) =>
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPrefix);

        public static Size Measure(string s, Font f, int width, TextFormatFlags flags) =>
            TextRenderer.MeasureText(s, f, new Size(Math.Max(1, width), int.MaxValue), flags | TextFormatFlags.NoPrefix);

        private static Image _lifebuoy;
        public static Image Lifebuoy
        {
            get
            {
                if (_lifebuoy != null) return _lifebuoy;
                try
                {
                    var s = typeof(Theme).Assembly.GetManifestResourceStream("Rapcha.lifebuoy.png");
                    _lifebuoy = s == null ? null : Image.FromStream(s);
                }
                catch { }
                return _lifebuoy;
            }
        }
    }

    /// <summary>Pixel fonts for one DPI, created on demand and disposed with the control.</summary>
    internal sealed class FontSet : IDisposable
    {
        private readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        public float Scale { get; private set; } = 1f;

        public void SetDpi(int dpi)
        {
            float s = Math.Max(1f, dpi) / 96f;
            if (Math.Abs(s - Scale) < 0.001f) return;
            Scale = s;
            Dispose();
        }

        public Font Get(string family, float px, FontStyle style = FontStyle.Regular)
        {
            var key = family + "|" + px + "|" + style;
            if (!_fonts.TryGetValue(key, out var f))
            {
                f = new Font(family, px * Scale, style, GraphicsUnit.Pixel);
                _fonts[key] = f;
            }
            return f;
        }

        public void Dispose()
        {
            foreach (var f in _fonts.Values) f.Dispose();
            _fonts.Clear();
        }
    }

    /// <summary>Custom-drawn, double-buffered base control.</summary>
    internal class DrawnControl : Control
    {
        protected readonly FontSet Fonts = new FontSet();

        public DrawnControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Cream;
        }

        protected float S => Fonts.Scale;
        protected int Px(float dip) => (int)Math.Round(dip * Fonts.Scale);

        protected override void OnHandleCreated(EventArgs e)
        {
            Fonts.SetDpi(DeviceDpi);
            base.OnHandleCreated(e);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            Fonts.SetDpi(DeviceDpi);
            base.OnDpiChangedAfterParent(e);
            OnLayoutChanged();
            Invalidate();
        }

        protected virtual void OnLayoutChanged() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Fonts.Dispose();
            base.Dispose(disposing);
        }
    }
}
