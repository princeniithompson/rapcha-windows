using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Rapcha.Win
{
    /// <summary>
    /// The "Snooze" drop target (v17): a clean vector circle with a crescent moon and the word "Snooze", shown in
    /// the middle of the screen after the ring has been held still there for about 5.5 s. It never takes the mouse
    /// (click-through); the ring is dropped on it. It fills in with the user's colour while the ring is over it.
    /// </summary>
    public sealed class SnoozeCircle : LayeredWindow
    {
        public const int SizeDip = 120;
        private int _dpi = 96;
        private bool _hover;
        private Point _center;

        public SnoozeCircle() : base(true) { }

        public int SizePx => (int)Math.Round(SizeDip * _dpi / 96.0);
        public Point Center => _center;
        public bool Hover => _hover;
        public bool Showing { get; private set; }

        public void ShowAt(Point center, int dpi)
        {
            _center = center;
            _dpi = dpi > 0 ? dpi : 96;
            _hover = false;
            Render();
            if (!Visible) Show();
            KeepOnTop();
            Showing = true;
        }

        public void SetHover(bool hover)
        {
            if (hover == _hover || !Showing) return;
            _hover = hover;
            Render();
        }

        public void HideNow()
        {
            Showing = false;
            _hover = false;
            if (Visible) Hide();
        }

        private void Render()
        {
            int pad = (int)Math.Round(10 * _dpi / 96.0);
            using (var bmp = Compose(SizePx, pad, _hover, _dpi))
                Present(bmp, _center.X - SizePx / 2 - pad, _center.Y - SizePx / 2 - pad);
        }

        /// <summary>The circle as a bitmap (also used for the screenshot).</summary>
        public static Bitmap Compose(int size, int pad, bool hover, int dpi)
        {
            int full = size + pad * 2;
            var bmp = new Bitmap(full, full, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float s = dpi / 96f;
                var accent = Accent.Color;
                var disc = new RectangleF(pad, pad, size, size);

                // soft shadow
                using (var path = new GraphicsPath())
                {
                    var sh = disc; sh.Inflate(pad * 0.6f, pad * 0.6f); sh.Offset(0, 2 * s);
                    path.AddEllipse(sh);
                    using (var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(60, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } })
                        g.FillPath(pg, path);
                }
                var fill = hover ? Accent.Mix(accent, Color.White, 0.15) : Color.FromArgb(250, 252, 248, 236);
                using (var b = new SolidBrush(Color.FromArgb(hover ? 245 : 236, fill))) g.FillEllipse(b, disc);
                using (var pen = new Pen(Color.FromArgb(230, hover ? Accent.Darker(0.25) : accent), Math.Max(2f, 3f * s)) { DashStyle = hover ? DashStyle.Solid : DashStyle.Dash })
                {
                    var r = disc; r.Inflate(-1.5f * s, -1.5f * s);
                    g.DrawEllipse(pen, r);
                }

                // crescent moon (two circles, the second cut out)
                var ink = hover ? Accent.TextOn(fill) : Color.FromArgb(60, 62, 48);
                float mr = size * 0.17f;
                float mx = pad + size / 2f, my = pad + size * 0.38f;
                using (var moon = new GraphicsPath())
                using (var cut = new GraphicsPath())
                {
                    moon.AddEllipse(mx - mr, my - mr, mr * 2, mr * 2);
                    cut.AddEllipse(mx - mr * 0.35f, my - mr * 1.25f, mr * 2, mr * 2);
                    using (var region = new Region(moon))
                    {
                        region.Exclude(cut);
                        using (var b = new SolidBrush(Color.FromArgb(235, ink))) g.FillRegion(b, region);
                    }
                }
                // two small "z"
                using (var zf = new Font(Theme.Sans, 8.5f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var zb = new SolidBrush(Color.FromArgb(200, ink)))
                {
                    g.DrawString("z", zf, zb, mx + mr * 0.55f, my - mr * 1.45f);
                    using (var zs = new Font(Theme.Sans, 6.5f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                        g.DrawString("z", zs, zb, mx + mr * 1.15f, my - mr * 1.95f);
                }
                using (var f = new Font(Theme.Sans, 15f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(ink))
                {
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("Snooze", f, b, new RectangleF(pad, pad + size * 0.56f, size, size * 0.2f), fmt);
                }
            }
            return bmp;
        }
    }
}
