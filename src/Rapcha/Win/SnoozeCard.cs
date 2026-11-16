using System;
using System.Drawing;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Settings → Snooze (v17): how long the ring hides after it is dropped on the Snooze circle:
    /// 19 minutes (default), 1 hour, 2 hours, or a custom number of minutes. Saved at once in %AppData%\Rapcha.
    /// </summary>
    internal sealed class SnoozeCard
    {
        public readonly CardPanel Card = new CardPanel();
        private readonly RadioButton _m19 = Radio("19 minutes (default)");
        private readonly RadioButton _h1 = Radio("1 hour");
        private readonly RadioButton _h2 = Radio("2 hours (a whole movie)");
        private readonly RadioButton _custom = Radio("Custom:");
        private readonly NumericUpDown _minutes = new NumericUpDown { Minimum = SnoozeDuration.MinMinutes, Maximum = SnoozeDuration.MaxMinutes, Width = 80, Font = new Font(Theme.Sans, 10f), Margin = new Padding(4, 4, 6, 0) };
        private readonly Label _minutesLabel = new Label { Text = "minutes", AutoSize = true, ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 7, 0, 0) };
        private readonly Label _status = Note("");
        private bool _loading;

        internal RadioButton Option19 => _m19;
        internal RadioButton Option1h => _h1;
        internal RadioButton Option2h => _h2;
        internal RadioButton OptionCustom => _custom;
        internal NumericUpDown CustomMinutes => _minutes;
        internal Label Status => _status;

        public SnoozeCard()
        {
            Card.Controls.Add(new Label { Text = "Snooze", AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Theme.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) });
            Card.Controls.Add(Note("To hide Rapcha for a while, drag the ring to the middle of the screen and hold it still for about 5 seconds. " +
                                   "A Snooze circle appears: drop the ring on it. Dropping it anywhere else docks it at the edge as usual."));
            Card.Controls.Add(new Label { Text = "Snooze for:", AutoSize = true, Font = new Font(Theme.Sans, 10f, FontStyle.Bold), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 6, 0, 2) });
            Card.Controls.Add(_m19);
            Card.Controls.Add(_h1);
            Card.Controls.Add(_h2);
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            _custom.Margin = new Padding(0, 4, 0, 0);
            row.Controls.Add(_custom);
            row.Controls.Add(_minutes);
            row.Controls.Add(_minutesLabel);
            Card.Controls.Add(row);
            Card.Controls.Add(_status);
            Card.Controls.Add(Note("Rapcha comes back by itself when the time is up. To wake it early, use \"Wake Rapcha now\" in the tray icon menu or on the Home page. " +
                                   "Your voice-typing and select-to-speak shortcuts still work while snoozed: pressing one wakes Rapcha and starts as usual."));
            Card.RowCount = Card.Controls.Count;
            for (int i = 0; i < Card.RowCount; i++) Card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            foreach (var r in new[] { _m19, _h1, _h2, _custom }) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) Save(); };
            _minutes.ValueChanged += (s, e) => { if (_loading) return; if (!_custom.Checked) { _loading = true; _custom.Checked = true; _loading = false; } Save(); };
            Snooze.DurationChanged += OnExternal;
            Card.Disposed += (s, e) => Snooze.DurationChanged -= OnExternal;
            Load();
        }

        private void OnExternal() { if (!_loading && !Card.IsDisposed) Load(); }

        public void Load()
        {
            _loading = true;
            try
            {
                int m = Snooze.Minutes;
                _m19.Checked = m == 19;
                _h1.Checked = m == 60;
                _h2.Checked = m == 120;
                _custom.Checked = !SnoozeDuration.IsPreset(m);
                _minutes.Value = Math.Max(_minutes.Minimum, Math.Min(_minutes.Maximum, SnoozeDuration.IsPreset(m) ? 45 : m));
                _status.Text = "Snooze lasts " + SnoozeDuration.Label(m) + ".";
            }
            finally { _loading = false; }
        }

        private void Save()
        {
            if (_loading) return;
            int m = _m19.Checked ? 19 : _h1.Checked ? 60 : _h2.Checked ? 120 : (int)_minutes.Value;
            _loading = true;
            try { Snooze.Minutes = m; } finally { _loading = false; }
            _status.Text = "Snooze lasts " + SnoozeDuration.Label(m) + ".";
        }

        private static RadioButton Radio(string text) => new RadioButton
        {
            Text = text, AutoSize = true, Font = new Font(Theme.Sans, 10f), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 2, 0, 2)
        };

        private static Label Note(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Theme.Muted, BackColor = Color.White, Margin = new Padding(0, 2, 0, 6), UseMnemonic = false
        };
    }
}
