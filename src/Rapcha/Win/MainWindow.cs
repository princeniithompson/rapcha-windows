using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// The main app window (Home, Vocabulary, Settings), styled like the Android app: cream background,
    /// white rounded cards, Georgia headings, a nav rail with the active page on an olive/yellow pill.
    /// Created on demand and disposed when closed; the ring keeps running.
    /// </summary>
    internal sealed class MainWindow : Form
    {
        public const string WindowTitle = "Rapcha for Windows";
        public enum Page { Home, Vocabulary, Settings, Diagnostics }

        private readonly NavRail _nav = new NavRail();
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Cream };
        private readonly HomeView _home = new HomeView { Dock = DockStyle.Fill };
        private readonly VocabularyView _vocab = new VocabularyView { Dock = DockStyle.Fill };
        private readonly SettingsPage _settings;
        private readonly DiagnosticsView _diag = new DiagnosticsView { Dock = DockStyle.Fill };
        private readonly Action _onHistory;
        private Page _page = (Page)(-1);

        // v17: "Rapcha is snoozed - 18 min left  [Wake Rapcha now]" on Home while snoozed.
        private readonly FlowLayoutPanel _snoozeBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Chip, Padding = new Padding(30, 10, 20, 10), WrapContents = false, Visible = false };
        private readonly Label _snoozeText = new Label { AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Chip, Margin = new Padding(0, 9, 16, 0), Font = new Font(Theme.Sans, 10.5f, FontStyle.Bold) };
        private readonly PillButton _wake = new PillButton { Text = "Wake Rapcha now", Primary = true };
        private readonly Timer _snoozeTick = new Timer { Interval = 1000 };
        private readonly Action _onSnooze;
        internal Control SnoozeBar => _snoozeBar;
        internal Label SnoozeText => _snoozeText;
        internal PillButton WakeButton => _wake;

        public MainWindow(Page page = Page.Home, string message = null)
        {
            Text = WindowTitle;
            Icon = Program.AppIcon;
            BackColor = Theme.Cream;
            Font = new Font(Theme.Sans, 10.5f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(640, 480);
            DoubleBuffered = true;

            _settings = new SettingsPage(message) { Dock = DockStyle.Fill };
            _nav.Dock = DockStyle.Left;
            _nav.Width = 224;
            _nav.PageClicked += p => ShowPage(p);
            _content.Controls.Add(_home);
            _content.Controls.Add(_vocab);
            _content.Controls.Add(_settings);
            _content.Controls.Add(_diag);
            _wake.BackColor = Theme.Chip;
            _wake.Click += (s, e) => Snooze.Wake("Home: Wake Rapcha now");
            _snoozeBar.Controls.Add(_snoozeText);
            _snoozeBar.Controls.Add(_wake);
            _content.Controls.Add(_snoozeBar);   // added last: docked first, at the top
            _snoozeTick.Tick += (s, e) => UpdateSnoozeBar();
            _onSnooze = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(UpdateSnoozeBar)); else UpdateSnoozeBar(); } catch { } };
            Snooze.Changed += _onSnooze;
            Controls.Add(_content);
            Controls.Add(_nav);

            _home.Reload();
            _onHistory = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { if (!IsDisposed) _home.Reload(); })); } catch { } };
            AppStorage.HistoryChanged += _onHistory;
            ShowPage(page);
        }

        public HomeView Home => _home;
        public VocabularyView Vocabulary => _vocab;
        public SettingsPage Settings => _settings;
        public DiagnosticsView Diagnostics => _diag;
        public Page Current => _page;

        public void ShowPage(Page page)
        {
            if (page == _page) return;
            _page = page;
            _home.Visible = page == Page.Home;
            _vocab.Visible = page == Page.Vocabulary;
            _settings.Visible = page == Page.Settings;
            _diag.Visible = page == Page.Diagnostics;
            _diag.SetShown(page == Page.Diagnostics);
            if (page == Page.Vocabulary) _vocab.Reload();
            _settings.SetPageShown(page == Page.Settings && Visible);
            _nav.Active = page;
            UpdateSnoozeBar();
        }

        private void UpdateSnoozeBar()
        {
            if (IsDisposed) return;
            bool show = Snooze.Active && _page == Page.Home;
            if (show) _snoozeText.Text = "Rapcha is snoozed - " + Snooze.LeftText + ". It comes back by itself.";
            if (_snoozeBar.Visible != show) _snoozeBar.Visible = show;
            _snoozeTick.Enabled = show;
        }

        public void ShowMessage(string message)
        {
            if (message == null) return;
            ShowPage(Page.Settings);
            _settings.ShowMessage(message);
        }

        /// <summary>Shows the window in front of everything (also from a second launch).</summary>
        public void BringUp()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;   // win the foreground even when another app has it
            TopMost = false;
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height - 20) Height = wa.Height - 20;
            if (Width > wa.Width - 20) Width = wa.Width - 20;
            CenterToScreen();
        }

        // v19: the Settings level meter only runs while this window is the active, non-minimised window.
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); _settings.SetWindowActive(true); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); _settings.SetWindowActive(false); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _settings != null) _settings.SetWindowActive(false);
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_settings != null) _settings.SetPageShown(Visible && _page == Page.Settings);
            if (_diag != null) _diag.SetShown(Visible && _page == Page.Diagnostics);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _settings.SetPageShown(_page == Page.Settings);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            AppStorage.HistoryChanged -= _onHistory;
            Snooze.Changed -= _onSnooze;
            _snoozeTick.Dispose();
            _settings.SetPageShown(false);
            _diag.SetShown(false);
            base.OnFormClosed(e);
        }

        /// <summary>Renders the window as it looks now into a PNG (CI screenshots).</summary>
        public void SavePng(string path)
        {
            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>The left nav rail: lifebuoy + "Rapcha", then Home / Vocabulary / Settings.</summary>
        private sealed class NavRail : DrawnControl
        {
            private static readonly string[] Names = { "Home", "Vocabulary", "Settings", "Diagnostics" };
            private Page _active;
            private int _hot = -1;
            public event Action<Page> PageClicked;

            public Page Active { get => _active; set { _active = value; Invalidate(); } }

            private Rectangle ItemRect(int i) => new Rectangle(Px(16), Px(112) + i * Px(54), Width - Px(32), Px(44));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Cream);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var logo = new Rectangle(Px(22), Px(26), Px(44), Px(44));
                if (Theme.Lifebuoy != null) g.DrawImage(Theme.Lifebuoy, logo);
                Theme.Text(g, "Rapcha", Fonts.Get(Theme.Serif, 27), Theme.Ink, new Rectangle(logo.Right + Px(10), logo.Y, Width - logo.Right - Px(10), logo.Height),
                           TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                for (int i = 0; i < Names.Length; i++)
                {
                    var r = ItemRect(i);
                    bool on = (int)_active == i;
                    if (on) Theme.FillCard(g, r, r.Height / 2f, Theme.Pill, Color.Empty);
                    else if (_hot == i) Theme.FillCard(g, r, r.Height / 2f, Theme.PillHover, Color.Empty);
                    DrawIcon(g, i, new Rectangle(r.X + Px(16), r.Y + (r.Height - Px(20)) / 2, Px(20), Px(20)), on ? Theme.Ink : Theme.Muted);
                    Theme.Text(g, Names[i], Fonts.Get(Theme.Sans, 15.5f, on ? FontStyle.Bold : FontStyle.Regular), on ? Theme.Ink : Color.FromArgb(70, 70, 56),
                               new Rectangle(r.X + Px(48), r.Y, r.Width - Px(52), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                using (var pen = new Pen(Theme.CardBorder)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                Theme.Text(g, "Click the ring to talk.\nDouble-click it to read the screen.", Fonts.Get(Theme.Sans, 12.5f), Theme.Muted,
                           new Rectangle(Px(22), Height - Px(70), Width - Px(30), Px(50)), TextFormatFlags.Left | TextFormatFlags.WordBreak);
            }

            private void DrawIcon(Graphics g, int i, Rectangle r, Color c)
            {
                using (var pen = new Pen(c, Math.Max(1.5f, 1.8f * S)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                    switch (i)
                    {
                        case 0: // house
                            g.DrawLines(pen, new[] { new PointF(x + w * 0.1f, y + h * 0.5f), new PointF(x + w * 0.5f, y + h * 0.12f), new PointF(x + w * 0.9f, y + h * 0.5f) });
                            g.DrawLines(pen, new[] { new PointF(x + w * 0.22f, y + h * 0.42f), new PointF(x + w * 0.22f, y + h * 0.88f), new PointF(x + w * 0.78f, y + h * 0.88f), new PointF(x + w * 0.78f, y + h * 0.42f) });
                            break;
                        case 1: // book
                            using (var p = Theme.Round(new RectangleF(x + w * 0.15f, y + h * 0.1f, w * 0.7f, h * 0.8f), 2 * S)) g.DrawPath(pen, p);
                            g.DrawLine(pen, x + w * 0.32f, y + h * 0.36f, x + w * 0.68f, y + h * 0.36f);
                            g.DrawLine(pen, x + w * 0.32f, y + h * 0.56f, x + w * 0.6f, y + h * 0.56f);
                            break;
                        case 3: // pulse line (diagnostics)
                            g.DrawLines(pen, new[] { new PointF(x + w * 0.05f, y + h * 0.55f), new PointF(x + w * 0.3f, y + h * 0.55f), new PointF(x + w * 0.42f, y + h * 0.2f),
                                new PointF(x + w * 0.58f, y + h * 0.88f), new PointF(x + w * 0.7f, y + h * 0.55f), new PointF(x + w * 0.95f, y + h * 0.55f) });
                            break;
                        default: // sliders
                            g.DrawLine(pen, x + w * 0.1f, y + h * 0.3f, x + w * 0.9f, y + h * 0.3f);
                            g.DrawLine(pen, x + w * 0.1f, y + h * 0.7f, x + w * 0.9f, y + h * 0.7f);
                            using (var b = new SolidBrush(Theme.Cream)) { g.FillEllipse(b, x + w * 0.56f, y + h * 0.18f, w * 0.24f, h * 0.24f); g.FillEllipse(b, x + w * 0.2f, y + h * 0.58f, w * 0.24f, h * 0.24f); }
                            g.DrawEllipse(pen, x + w * 0.56f, y + h * 0.18f, w * 0.24f, h * 0.24f);
                            g.DrawEllipse(pen, x + w * 0.2f, y + h * 0.58f, w * 0.24f, h * 0.24f);
                            break;
                    }
                }
            }

            private int Hit(Point p) { for (int i = 0; i < Names.Length; i++) if (ItemRect(i).Contains(p)) return i; return -1; }

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
                int h = Hit(e.Location);
                if (h >= 0 && e.Button == MouseButtons.Left) PageClicked?.Invoke((Page)h);
            }
        }
    }
}
