using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Rapcha.Core;
using Shortcut = Rapcha.Core.Shortcut;

namespace Rapcha.Win
{
    /// <summary>Settings → Shortcuts: three bindable actions, each with Change (records the next combo) and Clear.</summary>
    internal sealed class ShortcutsRows : TableLayoutPanel
    {
        private sealed class Row
        {
            public ShortcutAction Action;
            public ShortcutBox Box;
            public PillButton Change, Clear;
            public Label Note;
        }

        private readonly List<Row> _rows = new List<Row>();

        public ShortcutsRows()
        {
            ColumnCount = 4;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            Margin = new Padding(0, 4, 0, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Add(ShortcutAction.HoldToTalk, "Voice typing — hold to talk", "Hold the keys, speak, let go to finish and paste.");
            Add(ShortcutAction.TapToTalk, "Voice typing — tap to start/stop", "Press once to start, again to complete.");
            Add(ShortcutAction.SelectToSpeak, "Select to speak", "Opens or closes the read-aloud overlay.");
            Refresh2();
        }

        public ShortcutBox BoxFor(ShortcutAction a) => _rows.Find(r => r.Action == a)?.Box;
        public string NoteFor(ShortcutAction a) => _rows.Find(r => r.Action == a)?.Note.Text ?? "";

        private void Add(ShortcutAction a, string title, string help)
        {
            var row = new Row { Action = a };
            var name = new Label { Text = title, AutoSize = true, Font = new Font(Theme.Sans, 10.5f, FontStyle.Bold), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 10, 8, 0) };
            var sub = new Label { Text = help, AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.White, Margin = new Padding(0, 0, 8, 4) };
            var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.White, Margin = new Padding(0), WrapContents = false, Dock = DockStyle.Fill };
            left.Controls.Add(name); left.Controls.Add(sub);
            row.Box = new ShortcutBox { Margin = new Padding(0, 10, 8, 0), Anchor = AnchorStyles.Left };
            row.Change = new PillButton { Text = "Change", Margin = new Padding(0, 8, 6, 0) };
            row.Clear = new PillButton { Text = "Clear", Margin = new Padding(0, 8, 0, 0) };
            row.Note = new Label { AutoSize = true, BackColor = Color.White, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 6) };
            row.Change.Click += (s, e) => BeginChange(row);
            row.Clear.Click += (s, e) => Save(row, new Shortcut(), null);
            row.Box.Recorded += (sc, err) => OnRecorded(row, sc, err);
            row.Box.Cancelled += () => { ShortcutManager.Current?.Resume(); Refresh2(); };
            int r = RowCount;
            Controls.Add(left, 0, r);
            Controls.Add(row.Box, 1, r);
            Controls.Add(row.Change, 2, r);
            Controls.Add(row.Clear, 3, r);
            Controls.Add(row.Note, 0, r + 1);
            SetColumnSpan(row.Note, 4);
            RowCount = r + 2;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rows.Add(row);
        }

        private void BeginChange(Row row)
        {
            ShortcutManager.Current?.Suspend();
            row.Note.ForeColor = Theme.Ink;
            row.Note.Text = "Press the new combo now: Ctrl/Alt/Shift/Win + a key (Ctrl+Alt+V), or hold a key then press another (Space+V). Esc cancels.";
            row.Box.BeginCapture();
        }

        private void OnRecorded(Row row, Shortcut sc, string error)
        {
            if (sc == null)
            {
                ShortcutManager.Current?.Resume();
                Refresh2();
                row.Note.ForeColor = Theme.Danger;
                row.Note.Text = error;
                return;
            }
            Save(row, sc, null);
        }

        private void Save(Row row, Shortcut sc, string extra)
        {
            var set = AppStorage.LoadShortcuts();
            string moved = null;
            var clash = set.Clash(row.Action, sc);
            if (clash.HasValue) { set.Set(clash.Value, new Shortcut()); moved = sc + " was used by \"" + Title(clash.Value) + "\"; that one is now cleared."; }
            set.Set(row.Action, sc);
            ShortcutManager.Current?.Resume();
            AppStorage.SaveShortcuts(set);   // RapchaApp applies it (registers the hotkeys) right away
            Refresh2();
            if (moved != null) { row.Note.ForeColor = Theme.Danger; row.Note.Text = (row.Note.Text.Length > 0 ? row.Note.Text + " " : "") + moved; }
        }

        public static string Title(ShortcutAction a) =>
            a == ShortcutAction.HoldToTalk ? "Voice typing — hold to talk" : a == ShortcutAction.TapToTalk ? "Voice typing — tap to start/stop" : "Select to speak";

        /// <summary>Shows the saved combos and any warning (a common shortcut, or one another app owns).</summary>
        public void Refresh2()
        {
            var set = AppStorage.LoadShortcuts();
            foreach (var row in _rows)
            {
                var sc = set.Get(row.Action);
                row.Box.Value = sc;
                row.Clear.Enabled = !sc.IsEmpty;
                var notes = new List<string>();
                var warn = ShortcutRules.Warning(sc);
                if (warn != null) notes.Add("⚠ " + warn);
                var problem = ShortcutManager.Current?.ProblemFor(row.Action);
                if (problem != null) notes.Add("⚠ " + problem);
                if (sc.IsChord && warn == null)
                    notes.Add("Hold " + KeyNames.Name(sc.ChordKey) + ", then press " + KeyNames.Name(sc.Key) + ". " + KeyNames.Name(sc.ChordKey) + " on its own still types normally.");
                row.Note.ForeColor = warn != null || problem != null ? Color.FromArgb(150, 90, 0) : Theme.Muted;
                row.Note.Text = string.Join("  ", notes);
            }
        }
    }

    /// <summary>
    /// Shows a combo as key caps, and records a new one: every key the user holds (including Win and plain
    /// keys like Space) is collected until all are released; Esc alone cancels.
    /// </summary>
    internal sealed class ShortcutBox : Control
    {
        private Shortcut _value = new Shortcut();
        private bool _capturing;
        private readonly List<int> _order = new List<int>();
        private readonly HashSet<int> _held = new HashSet<int>();
        private List<int> _best = new List<int>();

        public event Action<Shortcut, string> Recorded;
        public event Action Cancelled;

        public ShortcutBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            TabStop = true;
        }

        public bool Capturing => _capturing;
        public Shortcut Value { get => _value; set { _value = value ?? new Shortcut(); Invalidate(); } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float k = DeviceDpi / 96f;
            return new Size((int)(190 * k), (int)(34 * k));
        }

        public void BeginCapture()
        {
            _capturing = true;
            _order.Clear(); _held.Clear(); _best = new List<int>();
            Focus();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (_capturing) { _capturing = false; Invalidate(); Cancelled?.Invoke(); }
        }

        public override bool PreProcessMessage(ref Message msg)
        {
            // While recording, every key goes to WndProc: no Tab navigation, no button clicks on Space/Enter, no Alt menu.
            if (_capturing && msg.Msg >= 0x100 && msg.Msg <= 0x109) return false;
            return base.PreProcessMessage(ref msg);
        }

        protected override bool IsInputKey(Keys keyData) => _capturing || base.IsInputKey(keyData);

        protected override void WndProc(ref Message m)
        {
            if (_capturing)
            {
                switch (m.Msg)
                {
                    case 0x100: case 0x104: KeyDownVk(Normalize((int)m.WParam, m.LParam)); return;   // WM_KEYDOWN / WM_SYSKEYDOWN
                    case 0x101: case 0x105: KeyUpVk(Normalize((int)m.WParam, m.LParam)); return;     // WM_KEYUP / WM_SYSKEYUP
                    case 0x102: case 0x106: case 0x103: case 0x107: return;                         // chars: swallow
                }
            }
            base.WndProc(ref m);
        }

        private static int Normalize(int vk, IntPtr lParam)
        {
            if (vk == 0x10) return 0xA0; if (vk == 0x11) return 0xA2; if (vk == 0x12) return 0xA4;
            return vk;
        }

        /// <summary>Feeds a key (also used by the self-test).</summary>
        public void KeyDownVk(int vk)
        {
            if (!_capturing) return;
            if (_held.Contains(vk)) return; // auto-repeat
            _held.Add(vk);
            if (!_order.Contains(vk)) _order.Add(vk);
            if (_held.Count >= _best.Count) _best = new List<int>(_order.FindAll(_held.Contains));
            Invalidate();
        }

        public void KeyUpVk(int vk)
        {
            if (!_capturing) return;
            _held.Remove(vk);
            if (_held.Count > 0) return;
            _capturing = false;
            Invalidate();
            if (_best.Count == 1 && _best[0] == 0x1B) { Cancelled?.Invoke(); return; }
            var sc = ShortcutRules.FromKeys(_best, out var error);
            Recorded?.Invoke(sc, error);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = DeviceDpi / 96f;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Theme.FillCard(g, r, 8 * k, _capturing ? Theme.PillHover : Theme.Cream, _capturing ? Theme.Olive : Theme.ButtonBorder);
            var parts = new List<string>();
            if (_capturing)
            {
                foreach (var vk in _order.FindAll(_held.Contains)) parts.Add(KeyNames.IsModifier(vk) ? KeyNames.ModOf(vk).ToString() : KeyNames.Name(vk));
                if (parts.Count == 0) { TextRenderer.DrawText(g, "Press keys…", Font, r, Theme.Olive, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            }
            else if (_value.IsEmpty) { TextRenderer.DrawText(g, "Not set", Font, r, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            else parts.AddRange(_value.ToString().Split('+'));
            using (var f = new Font(Theme.Sans, 9.5f, FontStyle.Bold))
            {
                var sizes = parts.ConvertAll(p => TextRenderer.MeasureText(p, f));
                int gap = (int)(5 * k), total = 0;
                foreach (var s in sizes) total += s.Width + (int)(10 * k) + gap;
                int x = r.X + Math.Max((int)(6 * k), (r.Width - total + gap) / 2);
                int capH = (int)(24 * k), y = r.Y + (r.Height - capH) / 2;
                for (int i = 0; i < parts.Count; i++)
                {
                    var cap = new Rectangle(x, y, sizes[i].Width + (int)(10 * k), capH);
                    Theme.FillCard(g, cap, 5 * k, Color.White, Theme.ButtonBorder);
                    TextRenderer.DrawText(g, parts[i], f, cap, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    x = cap.Right + gap;
                }
            }
        }
    }
}
