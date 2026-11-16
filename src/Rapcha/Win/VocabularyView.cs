using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Vocabulary: add and remove the custom words voice typing uses (vocabulary.txt, the same store as before).
    /// Saved as soon as a word is added or removed.
    /// </summary>
    internal sealed class VocabularyView : UserControl
    {
        private readonly TextBox _input = new TextBox();
        private readonly PillButton _add = new PillButton { Text = "Add", Primary = true };
        private readonly Label _status = new Label();
        private readonly ChipCard _chips = new ChipCard();
        private List<string> _terms = AppStorage.LoadVocabulary();

        public VocabularyView()
        {
            BackColor = Theme.Cream;
            DoubleBuffered = true;
            Padding = new Padding(32, 26, 32, 24);

            var title = new Label { Text = "Vocabulary", AutoSize = true, Font = new Font(Theme.Serif, 20f), ForeColor = Theme.Ink, Margin = new Padding(2, 0, 0, 4) };
            var help = new Label
            {
                Text = "Names and words you want spelled your way, like \"Prince Nii\". Voice typing uses them every time.",
                AutoSize = true, Font = new Font(Theme.Sans, 10.5f), ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 14)
            };
            _input.Font = new Font(Theme.Sans, 12f);
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.Dock = DockStyle.Fill;
            _input.Margin = new Padding(0, 4, 10, 4);
            _input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddFromInput(); } };
            _add.Click += (s, e) => AddFromInput();
            _add.Margin = new Padding(0, 0, 0, 0);
            _status.AutoSize = true;
            _status.Font = new Font(Theme.Sans, 10f);
            _status.ForeColor = Theme.Danger;
            _status.Margin = new Padding(2, 6, 0, 6);
            _chips.Dock = DockStyle.Fill;
            _chips.Removed += term => Remove(term);

            var addRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0), BackColor = Theme.Cream };
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            addRow.Controls.Add(_input, 0, 0);
            addRow.Controls.Add(_add, 1, 0);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Cream };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(title);
            layout.Controls.Add(help);
            layout.Controls.Add(addRow);
            layout.Controls.Add(_status);
            layout.Controls.Add(_chips);
            layout.RowCount = 5;
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            _chips.SetTerms(_terms);
            help.MaximumSize = new Size(760, 0);
        }

        public IReadOnlyList<string> Terms => _terms;
        public TextBox Input => _input;
        public PillButton AddButton => _add;

        public bool Add(string term)
        {
            _terms = AppStorage.LoadVocabulary();           // the same store voice typing reads
            if (!VocabularyList.TryAdd(_terms, term))
            {
                _status.Text = (term ?? "").Trim().Length == 0 ? "" : "That word is already in the list.";
                return false;
            }
            AppStorage.SaveVocabulary(_terms);
            _status.Text = "";
            _chips.SetTerms(_terms);
            AppStorage.Log("Vocabulary: added a word (" + _terms.Count + " in the list)");
            return true;
        }

        public bool Remove(string term)
        {
            _terms = AppStorage.LoadVocabulary();
            if (_terms.RemoveAll(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            AppStorage.SaveVocabulary(_terms);
            _chips.SetTerms(_terms);
            AppStorage.Log("Vocabulary: removed a word (" + _terms.Count + " in the list)");
            return true;
        }

        public void Reload() { _terms = AppStorage.LoadVocabulary(); _chips.SetTerms(_terms); }

        private void AddFromInput()
        {
            if (Add(_input.Text)) _input.Clear();
            _input.Focus();
        }

        /// <summary>The white card with one chip per word; × removes it.</summary>
        private sealed class ChipCard : DrawnControl
        {
            private List<string> _terms = new List<string>();
            private readonly List<KeyValuePair<string, Rectangle>> _chipRects = new List<KeyValuePair<string, Rectangle>>();
            private readonly List<KeyValuePair<string, Rectangle>> _xRects = new List<KeyValuePair<string, Rectangle>>();
            private string _hotX;
            public event Action<string> Removed;

            public void SetTerms(IEnumerable<string> terms) { _terms = terms.ToList(); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Cream);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                _chipRects.Clear(); _xRects.Clear();
                var card = new Rectangle(0, Px(6), Math.Min(Width - 1, Px(780)), Height - Px(8));
                if (card.Height < Px(40)) return;
                Theme.FillCard(g, card, Px(18), Theme.Card, Theme.CardBorder);
                var head = Fonts.Get(Theme.Serif, 18);
                string count = _terms.Count == 0 ? "Your words" : _terms.Count + (_terms.Count == 1 ? " word" : " words");
                Theme.Text(g, count, head, Theme.Ink, new Rectangle(card.X + Px(22), card.Y + Px(18), card.Width - Px(44), Px(28)), TextFormatFlags.Left);
                int x0 = card.X + Px(22), x = x0, y = card.Y + Px(60), right = card.Right - Px(22);
                if (_terms.Count == 0)
                {
                    Theme.Text(g, "No words yet. Type one above and press Add.", Fonts.Get(Theme.Sans, 14), Theme.Muted, new Rectangle(x0, y, right - x0, Px(30)), TextFormatFlags.Left);
                    return;
                }
                var f = Fonts.Get(Theme.Sans, 14.5f);
                var xf = Fonts.Get(Theme.Sans, 14f, FontStyle.Bold);
                int h = Px(36), gap = Px(10);
                foreach (var t in _terms)
                {
                    var ts = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    int w = Math.Min(right - x0, ts.Width + Px(16) + Px(12) + Px(22) + Px(6));
                    if (x + w > right && x > x0) { x = x0; y += h + gap; }
                    if (y + h > card.Bottom - Px(12)) break;
                    var r = new Rectangle(x, y, w, h);
                    Theme.FillCard(g, r, h / 2f, Theme.Chip, Theme.ButtonBorder);
                    Theme.Text(g, t, f, Theme.Ink, new Rectangle(r.X + Px(16), r.Y, w - Px(16) - Px(34), h), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    var xr = new Rectangle(r.Right - Px(32), r.Y + (h - Px(24)) / 2, Px(24), Px(24));
                    if (_hotX == t) using (var hb = new SolidBrush(Theme.Pill)) g.FillEllipse(hb, xr);
                    Theme.Text(g, "×", xf, Theme.Muted, xr, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    _chipRects.Add(new KeyValuePair<string, Rectangle>(t, r));
                    _xRects.Add(new KeyValuePair<string, Rectangle>(t, xr));
                    x += w + gap;
                }
            }

            private string HitX(Point p)
            {
                foreach (var kv in _xRects) { var r = kv.Value; r.Inflate(Px(3), Px(3)); if (r.Contains(p)) return kv.Key; }
                return null;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var hit = HitX(e.Location);
                Cursor = hit == null ? Cursors.Default : Cursors.Hand;
                if (hit != _hotX) { _hotX = hit; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hotX != null) { _hotX = null; Invalidate(); } }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                var hit = HitX(e.Location);
                if (hit != null && e.Button == MouseButtons.Left) Removed?.Invoke(hit);
            }
        }
    }

    /// <summary>A rounded, custom-drawn button (olive "primary" or outlined).</summary>
    internal sealed class PillButton : Button
    {
        public bool Primary { get; set; }
        private bool _hot, _down;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font(Theme.Sans, 10.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.Cream;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var s = TextRenderer.MeasureText(Text, Font);
            float k = DeviceDpi / 96f;
            return new Size(Math.Max((int)(84 * k), s.Width + (int)(36 * k)), Math.Max((int)(36 * k), s.Height + (int)(16 * k)));
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Cream);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = Primary ? (_down ? Color.FromArgb(206, 192, 92) : _hot ? Color.FromArgb(236, 224, 130) : Theme.Pill)
                                 : (_down ? Theme.Pill : _hot ? Theme.PillHover : Color.White);
            Theme.FillCard(g, r, r.Height / 2f, Enabled ? fill : Color.FromArgb(236, 232, 214), Primary ? Color.FromArgb(196, 182, 86) : Theme.ButtonBorder);
            TextRenderer.DrawText(g, Text, Font, r, Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Olive) { DashStyle = DashStyle.Dot }) g.DrawPath(p, Theme.Round(Rectangle.Inflate(r, -3, -3), r.Height / 2f - 3));
        }
    }
}
