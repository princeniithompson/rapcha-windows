using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>A small click-through pill next to the ring: "Listening", "Polishing…", "Typed ✓", errors.</summary>
    public sealed class LabelWindow : LayeredWindow
    {
        private readonly Timer _hideTimer = new Timer();

        public LabelWindow() : base(true)
        {
            _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); Hide(); };
        }

        public void ShowText(string text, Rectangle ring, DockEdge edge, int dpi, int hideAfterMs = 0, bool warning = false)
        {
            _hideTimer.Stop();
            float scale = dpi / 96f;
            int pad = (int)(10 * scale), gap = (int)(10 * scale), maxW = (int)(300 * scale);
            // Pixel units so DPI scaling is ours (the window is per-monitor DPI aware).
            using (var f = new Font("Segoe UI", 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                Size textSize;
                using (var probe = new Bitmap(1, 1))
                using (var pg = Graphics.FromImage(probe))
                {
                    var sz = pg.MeasureString(text, f, maxW, StringFormat.GenericTypographic);
                    textSize = new Size((int)Math.Ceiling(sz.Width) + 2, (int)Math.Ceiling(sz.Height) + 2);
                }
                int w = textSize.Width + pad * 2 + (int)(14 * scale);
                int h = Math.Max(textSize.Height + pad, (int)(30 * scale));
                int x, y;
                switch (edge)
                {
                    case DockEdge.Left: x = ring.Right + gap; y = ring.Y + ring.Height / 2 - h / 2; break;
                    case DockEdge.Right: x = ring.X - gap - w; y = ring.Y + ring.Height / 2 - h / 2; break;
                    case DockEdge.Top: x = ring.X + ring.Width / 2 - w / 2; y = ring.Bottom + gap; break;
                    default: x = ring.X + ring.Width / 2 - w / 2; y = ring.Y - gap - h; break;
                }
                var wa = Screen.FromRectangle(ring).WorkingArea;
                x = Math.Max(wa.X, Math.Min(x, wa.Right - w));
                y = Math.Max(wa.Y, Math.Min(y, wa.Bottom - h));
                using (var bmp = Draw(text, f, w, h, textSize, pad, scale, warning)) Present(bmp, x, y);
            }
            if (!Visible) Show();
            KeepOnTop();
            if (hideAfterMs > 0) { _hideTimer.Interval = hideAfterMs; _hideTimer.Start(); }
        }

        private static Bitmap Draw(string text, Font f, int w, int h, Size textSize, int pad, float scale, bool warning)
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int r = Math.Min(h, (int)(30 * scale));
                using (var path = Rounded(new Rectangle(0, 0, w - 1, h - 1), r))
                using (var bg = new SolidBrush(warning ? Color.FromArgb(240, 120, 30, 30) : Color.FromArgb(235, 33, 38, 30)))
                    g.FillPath(bg, path);
                using (var dot = new SolidBrush(warning ? Color.FromArgb(255, 255, 200, 200) : Color.FromArgb(255, 170, 200, 70)))
                    g.FillEllipse(dot, pad, h / 2 - (int)(4 * scale), (int)(8 * scale), (int)(8 * scale));
                using (var fg = new SolidBrush(Color.White))
                    g.DrawString(text, f, fg, new RectangleF(pad + (int)(14 * scale), (h - textSize.Height) / 2f + 1, textSize.Width, textSize.Height), StringFormat.GenericTypographic);
            }
            return bmp;
        }

        public void HideNow() { _hideTimer.Stop(); Hide(); }

        private static GraphicsPath Rounded(Rectangle r, int diameter)
        {
            var p = new GraphicsPath();
            diameter = Math.Max(2, Math.Min(diameter, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
            p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
            p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hideTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
