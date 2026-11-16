using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Home: the "N words spoken so far" card, then the saved transcripts grouped by day (Today, Yesterday, dates),
    /// newest first, each with its time, Copy and Delete. Custom drawn; only what is on screen is painted.
    /// </summary>
    internal sealed class HomeView : DrawnControl
    {
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        private enum Kind { WordsCard, Heading, Item, Empty }

        private sealed class Block
        {
            public Kind Kind;
            public Rectangle Rect;          // content coordinates (before scrolling)
            public string Title;
            public Transcript Item;
            public Rectangle TextRect, Copy, Delete;
        }

        private readonly VScrollBar _bar = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        private readonly Timer _toastTimer = new Timer { Interval = 1600 };
        private readonly List<Block> _blocks = new List<Block>();
        private TranscriptHistory _history = new TranscriptHistory();
        private int _contentHeight;
        private string _toast;
        private Rectangle _hot = Rectangle.Empty;

        public HomeView()
        {
            SetStyle(ControlStyles.Selectable, true);
            Controls.Add(_bar);
            _bar.Scroll += (s, e) => Invalidate();
            _bar.ValueChanged += (s, e) => Invalidate();
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toast = null; Invalidate(); };
        }

        // ---- data ----
        public void SetHistory(TranscriptHistory h)
        {
            _history = h ?? new TranscriptHistory();
            OnLayoutChanged();
            Invalidate();
        }

        public void Reload() => SetHistory(AppStorage.LoadHistory());

        public int ShownTotalWords => _history.TotalWords;
        public int ItemCount => _history.Items.Count;
        public string[] Headings => _blocks.Where(b => b.Kind == Kind.Heading).Select(b => b.Title).ToArray();
        public bool ToastVisible => _toast != null;
        public string ToastText => _toast;
        public Transcript ItemAt(int index) => Items().ElementAtOrDefault(index)?.Item;

        private IEnumerable<Block> Items() => _blocks.Where(b => b.Kind == Kind.Item);

        /// <summary>Screen rectangle of the Copy (or Delete) button on the index-th card, as drawn now.</summary>
        public Rectangle ButtonScreenRect(int index, bool delete)
        {
            var b = Items().ElementAtOrDefault(index);
            if (b == null) return Rectangle.Empty;
            var r = delete ? b.Delete : b.Copy;
            r.Offset(0, -Scroll);
            return RectangleToScreen(r);
        }

        public void Copy(Transcript t)
        {
            try
            {
                Clipboard.SetDataObject(t.Text, true, 5, 100);
                ShowToast("Copied");
            }
            catch (Exception e) { AppStorage.Log("Home: copy failed (" + e.GetType().Name + ")"); ShowToast("Couldn't copy — try again"); }
        }

        public void Delete(Transcript t)
        {
            var h = AppStorage.LoadHistory();
            h.Remove(t.Id);
            AppStorage.SaveHistory(h);          // HistoryChanged reloads the view
            SetHistory(h);
            ShowToast("Deleted");
        }

        private void ShowToast(string text)
        {
            _toast = text;
            _toastTimer.Stop();
            _toastTimer.Start();
            Invalidate();
        }

        // ---- layout ----
        private int Scroll => _bar.Visible ? _bar.Value : 0;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            OnLayoutChanged();
        }

        protected override void OnLayoutChanged()
        {
            _blocks.Clear();
            if (!IsHandleCreated) return;
            int pad = Px(32), gap = Px(12);
            int width = Math.Min(Px(780), ClientSize.Width - pad * 2 - SystemInformation.VerticalScrollBarWidth);
            if (width < Px(200)) width = Math.Max(Px(120), ClientSize.Width - pad);
            int y = Px(26);

            _blocks.Add(new Block { Kind = Kind.WordsCard, Rect = new Rectangle(pad, y, width, Px(128)) });
            y += Px(128) + Px(8);

            var textFont = Fonts.Get(Theme.Sans, 15);
            if (_history.Items.Count == 0)
            {
                y += Px(18);
                _blocks.Add(new Block { Kind = Kind.Empty, Rect = new Rectangle(pad, y, width, Px(110)) });
                y += Px(110);
            }
            foreach (var group in TranscriptHistory.GroupByDay(_history.Items, DateTime.Now, t => t.ToLocalTime()))
            {
                y += Px(22);
                var hFont = Fonts.Get(Theme.Serif, 24);
                int hh = TextRenderer.MeasureText(group.Key, hFont).Height;
                _blocks.Add(new Block { Kind = Kind.Heading, Title = group.Key, Rect = new Rectangle(pad + Px(4), y, width, hh) });
                y += hh + Px(10);
                foreach (var t in group.Value)
                {
                    int inner = width - Px(18) * 2;
                    int th = Theme.Measure(t.Text, textFont, inner, Wrap).Height;
                    int h = Px(18) + th + Px(12) + Px(32) + Px(14);
                    var rect = new Rectangle(pad, y, width, h);
                    var b = new Block { Kind = Kind.Item, Item = t, Rect = rect };
                    b.TextRect = new Rectangle(rect.X + Px(18), rect.Y + Px(18), inner, th);
                    int by = rect.Bottom - Px(14) - Px(32);
                    b.Delete = new Rectangle(rect.Right - Px(18) - Px(80), by, Px(80), Px(32));
                    b.Copy = new Rectangle(b.Delete.X - Px(8) - Px(76), by, Px(76), Px(32));
                    _blocks.Add(b);
                    y += h + gap;
                }
            }
            _contentHeight = y + Px(40);
            bool need = _contentHeight > ClientSize.Height;
            _bar.Visible = need;
            if (need)
            {
                _bar.Minimum = 0;
                _bar.LargeChange = Math.Max(1, ClientSize.Height);
                _bar.SmallChange = Px(48);
                _bar.Maximum = _contentHeight;
                if (_bar.Value > _contentHeight - ClientSize.Height) _bar.Value = Math.Max(0, _contentHeight - ClientSize.Height);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_bar.Visible) return;
            int max = Math.Max(0, _contentHeight - ClientSize.Height);
            _bar.Value = Math.Max(0, Math.Min(max, _bar.Value - e.Delta * Px(48) / 120));
        }

        // ---- painting ----
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Cream);
            if (_blocks.Count == 0) OnLayoutChanged();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int scroll = Scroll;
            float radius = Px(18);
            foreach (var b in _blocks)
            {
                var r = b.Rect; r.Offset(0, -scroll);
                if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
                switch (b.Kind)
                {
                    case Kind.WordsCard: PaintWords(g, r, radius); break;
                    case Kind.Heading:
                        Theme.Text(g, b.Title, Fonts.Get(Theme.Serif, 24), Theme.Ink, r, TextFormatFlags.Left | TextFormatFlags.Top);
                        break;
                    case Kind.Empty:
                        Theme.FillCard(g, r, radius, Theme.Card, Theme.CardBorder);
                        Theme.Text(g, "No dictations yet", Fonts.Get(Theme.Serif, 19), Theme.Ink, new Rectangle(r.X + Px(22), r.Y + Px(22), r.Width - Px(44), Px(30)), TextFormatFlags.Left);
                        Theme.Text(g, "Click the ring, talk, and everything Rapcha types or copies for you is kept here.", Fonts.Get(Theme.Sans, 14), Theme.Muted,
                                   new Rectangle(r.X + Px(22), r.Y + Px(58), r.Width - Px(44), Px(44)), Wrap);
                        break;
                    case Kind.Item: PaintItem(g, b, scroll, radius); break;
                }
            }
            if (_toast != null) PaintToast(g);
        }

        private void PaintWords(Graphics g, Rectangle r, float radius)
        {
            Theme.FillCard(g, r, radius, Theme.Card, Theme.CardBorder);
            var numFont = Fonts.Get(Theme.Serif, 46);
            var num = _history.TotalWords.ToString("N0", CultureInfo.CurrentCulture);
            var ns = TextRenderer.MeasureText(g, num, numFont, Size.Empty, TextFormatFlags.NoPadding);
            int x = r.X + Px(26), y = r.Y + Px(20);
            Theme.Text(g, num, numFont, Theme.Teal, new Rectangle(x, y, ns.Width + 2, ns.Height), TextFormatFlags.NoPadding);
            var wFont = Fonts.Get(Theme.Sans, 19);
            var ws = TextRenderer.MeasureText(g, "words", wFont, Size.Empty, TextFormatFlags.NoPadding);
            Theme.Text(g, _history.TotalWords == 1 ? "word" : "words", wFont, Theme.Ink,
                       new Rectangle(x + ns.Width + Px(10), y + ns.Height - ws.Height - Px(7), ws.Width + Px(10), ws.Height), TextFormatFlags.NoPadding);
            Theme.Text(g, "spoken so far across your dictation sessions", Fonts.Get(Theme.Sans, 14.5f), Theme.Muted,
                       new Rectangle(x, y + ns.Height + Px(6), r.Width - Px(52), Px(24)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private void PaintItem(Graphics g, Block b, int scroll, float radius)
        {
            var r = b.Rect; r.Offset(0, -scroll);
            Theme.FillCard(g, r, radius, Theme.Card, Theme.CardBorder);
            var tr = b.TextRect; tr.Offset(0, -scroll);
            Theme.Text(g, b.Item.Text, Fonts.Get(Theme.Sans, 15), Theme.Ink, tr, Wrap);
            var copy = b.Copy; copy.Offset(0, -scroll);
            var del = b.Delete; del.Offset(0, -scroll);
            var time = b.Item.TimeUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            Theme.Text(g, time, Fonts.Get(Theme.Sans, 13.5f), Theme.Muted, new Rectangle(tr.X, copy.Y, Px(140), copy.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            PaintButton(g, copy, "Copy", Theme.Ink, b.Copy == _hot);
            PaintButton(g, del, "Delete", Theme.Danger, b.Delete == _hot);
        }

        private void PaintButton(Graphics g, Rectangle r, string text, Color color, bool hot)
        {
            Theme.FillCard(g, r, r.Height / 2f, hot ? Theme.PillHover : Theme.Cream, Theme.ButtonBorder);
            Theme.Text(g, text, Fonts.Get(Theme.Sans, 13.5f, FontStyle.Bold), color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void PaintToast(Graphics g)
        {
            var f = Fonts.Get(Theme.Sans, 14.5f, FontStyle.Bold);
            var size = TextRenderer.MeasureText(g, _toast, f);
            int w = size.Width + Px(40), h = Px(40);
            var r = new Rectangle((ClientSize.Width - w) / 2, ClientSize.Height - h - Px(26), w, h);
            Theme.FillCard(g, r, h / 2f, Color.FromArgb(235, 38, 40, 30), Color.Empty);
            Theme.Text(g, "✓ " + _toast, f, Color.White, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- mouse ----
        private Block HitButton(Point p, out bool delete)
        {
            delete = false;
            var q = new Point(p.X, p.Y + Scroll);
            foreach (var b in Items())
            {
                if (b.Copy.Contains(q)) return b;
                if (b.Delete.Contains(q)) { delete = true; return b; }
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var b = HitButton(e.Location, out bool del);
            var hot = b == null ? Rectangle.Empty : del ? b.Delete : b.Copy;
            Cursor = b == null ? Cursors.Default : Cursors.Hand;
            if (hot != _hot) { _hot = hot; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != Rectangle.Empty) { _hot = Rectangle.Empty; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var b = HitButton(e.Location, out bool del);
            if (b == null) return;
            if (del) Delete(b.Item); else Copy(b.Item);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!Focused) Focus(); // wheel scrolling
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _toastTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
