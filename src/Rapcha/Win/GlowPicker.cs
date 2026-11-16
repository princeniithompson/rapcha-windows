using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Glow colour swatches: Olive, Pink, Blue, Purple, Teal, Orange and Custom… (a colour dialog).</summary>
    public sealed class GlowPicker : Control
    {
        private const int Cell = 78, Swatch = 36, H = 84;
        private int _selected = GlowColors.Default;
        private int _hot = -1;

        public event Action<int> Changed;

        public GlowPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            AutoSize = true;
            Margin = new Padding(0, 2, 0, 2);
        }

        private float K => DeviceDpi / 96f;
        public int Selected { get => _selected; set { _selected = value & 0xFFFFFF; Invalidate(); } }
        public string SelectedName => GlowColors.NameOf(_selected);

        /// <summary>Chooses a colour as if clicked (and saves it through Changed).</summary>
        public void Choose(int rgb)
        {
            Selected = rgb;
            Changed?.Invoke(_selected);
        }

        public bool ChoosePreset(string name)
        {
            foreach (var p in GlowColors.Presets) if (p.Key == name) { Choose(p.Value); return true; }
            return false;
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            new Size((int)((GlowColors.Presets.Count + 1) * Cell * K), (int)(H * K));

        private Rectangle CellRect(int i) => new Rectangle((int)(i * Cell * K), 0, (int)(Cell * K), (int)(H * K));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = K;
            using (var font = new Font(Theme.Sans, 12.5f * k, GraphicsUnit.Pixel))
            using (var bold = new Font(Theme.Sans, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                int n = GlowColors.Presets.Count;
                bool custom = GlowColors.NameOf(_selected) == "Custom";
                for (int i = 0; i <= n; i++)
                {
                    var cell = CellRect(i);
                    int sw = (int)(Swatch * k);
                    var r = new Rectangle(cell.X + (cell.Width - sw) / 2, cell.Y + (int)(8 * k), sw, sw);
                    bool sel;
                    string name;
                    if (i < n)
                    {
                        var p = GlowColors.Presets[i];
                        name = p.Key;
                        sel = p.Value == _selected;
                        using (var b = new SolidBrush(FromRgb(p.Value))) g.FillEllipse(b, r);
                    }
                    else
                    {
                        name = "Custom…";
                        sel = custom;
                        if (custom) using (var b = new SolidBrush(FromRgb(_selected))) g.FillEllipse(b, r);
                        else
                        {
                            using (var path = new GraphicsPath())
                            {
                                path.AddEllipse(r);
                                using (var pg = new PathGradientBrush(path) { CenterColor = Color.White, SurroundColors = new[] { Color.FromArgb(255, 120, 120), Color.FromArgb(255, 220, 90), Color.FromArgb(90, 200, 120), Color.FromArgb(90, 150, 255), Color.FromArgb(190, 110, 255) } })
                                    g.FillEllipse(pg, r);
                            }
                            TextRenderer.DrawText(g, "+", bold, r, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        }
                    }
                    if (sel || _hot == i)
                    {
                        var ring = Rectangle.Inflate(r, (int)(4 * k), (int)(4 * k));
                        using (var pen = new Pen(sel ? Theme.Ink : Theme.ButtonBorder, Math.Max(1.5f, 2f * k))) g.DrawEllipse(pen, ring);
                    }
                    if (sel && i < n || sel && custom)
                        TextRenderer.DrawText(g, "✓", bold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    var tr = new Rectangle(cell.X, r.Bottom + (int)(8 * k), cell.Width, (int)(22 * k));
                    TextRenderer.DrawText(g, name, sel ? bold : font, tr, sel ? Theme.Ink : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        public static Color FromRgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        public static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

        private int Hit(Point p)
        {
            for (int i = 0; i <= GlowColors.Presets.Count; i++) if (CellRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            Cursor = h < 0 ? Cursors.Default : Cursors.Hand;
            if (h != _hot) { _hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = Hit(e.Location);
            if (h < 0) return;
            if (h < GlowColors.Presets.Count) { Choose(GlowColors.Presets[h].Value); return; }
            using (var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = FromRgb(_selected) })
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Choose(ToRgb(dlg.Color));
            }
        }
    }
}
