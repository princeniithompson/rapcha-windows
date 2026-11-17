using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// v19: the Diagnostics tab. Shows whether the microphone is open right now (and by what), a live timeline, and
    /// buttons to save it all as a .txt file, copy it, clear it, and save the last dictation's audio as a WAV file.
    /// No API key ever; words only when "Include transcribed text" is ticked (off by default).
    /// </summary>
    internal sealed class DiagnosticsView : UserControl
    {
        private readonly Label _micNow = new Label { AutoSize = true, Font = new Font(Theme.Sans, 12f, FontStyle.Bold), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 2, 0, 6) };
        private readonly Label _summary = new Label { AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.White, MaximumSize = new Size(820, 0) };
        private readonly PillButton _save = new PillButton { Text = "Save as .txt", Primary = true };
        private readonly PillButton _copy = new PillButton { Text = "Copy" };
        private readonly PillButton _clear = new PillButton { Text = "Clear" };
        private readonly PillButton _wav = new PillButton { Text = "Save last session audio (WAV)" };
        private readonly CheckBox _words = new CheckBox { Text = "Include transcribed text (for debugging)", AutoSize = true, BackColor = Color.White, Margin = new Padding(0, 8, 0, 2) };
        private readonly Label _status = new Label { AutoSize = true, BackColor = Color.White, ForeColor = Color.FromArgb(30, 110, 40), MaximumSize = new Size(820, 0) };
        private readonly TextBox _live = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(252, 251, 246), BorderStyle = BorderStyle.FixedSingle
        };
        private readonly Timer _tick = new Timer { Interval = 500 };
        private readonly object _pendingLock = new object();
        private readonly List<string> _pending = new List<string>();
        private readonly Action<string> _onLine;
        private bool _shown;

        internal TextBox Live => _live;
        internal CheckBox IncludeText => _words;
        internal Label MicNow => _micNow;
        internal Label Status => _status;
        /// <summary>Tests: pick this path instead of showing the Save dialog.</summary>
        internal Func<string, string> PickPath;

        public DiagnosticsView()
        {
            BackColor = Theme.Cream;
            Font = new Font(Theme.Sans, 10.5f);
            DoubleBuffered = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 26, 32, 24), ColumnCount = 1, RowCount = 3, BackColor = Theme.Cream };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new Label { Text = "Diagnostics", AutoSize = true, Font = new Font(Theme.Serif, 20f), ForeColor = Theme.Ink, Margin = new Padding(2, 0, 0, 8) };
            layout.Controls.Add(title, 0, 0);

            var card = new CardPanel();
            card.Controls.Add(new Label { Text = "What Rapcha is doing with your microphone", AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Theme.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) });
            card.Controls.Add(_micNow);
            card.Controls.Add(_summary);
            var help = new Label
            {
                AutoSize = true, BackColor = Color.White, ForeColor = Theme.Ink, MaximumSize = new Size(820, 0), Margin = new Padding(0, 6, 0, 0),
                Text = "Use Rapcha as normal. When something goes wrong, press Save as .txt and send that file. It has no API key and none of your words unless you tick the box below."
            };
            card.Controls.Add(help);
            card.Controls.Add(_words);
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 6, 0, 2), WrapContents = true };
            foreach (var b in new Button[] { _save, _copy, _clear, _wav }) { b.Margin = new Padding(0, 0, 10, 0); b.BackColor = Color.White; row.Controls.Add(b); }
            card.Controls.Add(row);
            card.Controls.Add(_status);
            card.RowCount = card.Controls.Count;
            for (int i = 0; i < card.RowCount; i++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(card, 0, 1);
            layout.Controls.Add(_live, 0, 2);
            Controls.Add(layout);

            _words.Checked = DiagLog.IncludeText;
            _words.CheckedChanged += (s, e) =>
            {
                DiagLog.IncludeText = _words.Checked;
                AppStorage.Log("Diagnostics: include transcribed text " + (_words.Checked ? "ON" : "off"));
                Reload();
            };
            _save.Click += (s, e) => SaveTxt();
            _copy.Click += (s, e) => Copy();
            _clear.Click += (s, e) => { DiagLog.Clear(); AppStorage.Log("Diagnostics: cleared by you"); Reload(); SetStatus("Cleared. New events will appear below.", false); };
            _wav.Click += (s, e) => SaveWav();
            _onLine = line => { lock (_pendingLock) _pending.Add(line); };
            DiagLog.Added += _onLine;
            _tick.Tick += (s, e) => Flush();
            Refresh2();
        }

        public void SetShown(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            if (shown) { Reload(); _tick.Start(); } else _tick.Stop();
        }

        private bool ShowsLine(string line) => _words.Checked || !line.Contains(" [text] ");

        internal void Reload()
        {
            lock (_pendingLock) _pending.Clear();
            var lines = DiagLog.Snapshot().Where(ShowsLine).ToList();
            if (lines.Count > 3000) lines = lines.Skip(lines.Count - 3000).ToList();   // the saved file has everything
            _live.Text = string.Join(Environment.NewLine, lines) + (lines.Count > 0 ? Environment.NewLine : "");
            ScrollToEnd();
            Refresh2();
        }

        private void Flush()
        {
            List<string> add;
            lock (_pendingLock) { add = _pending.Where(ShowsLine).ToList(); _pending.Clear(); }
            if (add.Count > 0)
            {
                if (_live.TextLength > 600000) { Reload(); return; }
                _live.AppendText(string.Join(Environment.NewLine, add) + Environment.NewLine);
            }
            Refresh2();
        }

        private void ScrollToEnd() { _live.SelectionStart = _live.TextLength; _live.ScrollToCaret(); }

        private void Refresh2()
        {
            var now = MicTracker.NowText();
            _micNow.Text = now;
            _micNow.ForeColor = MicTracker.Count > 0 ? Color.FromArgb(150, 30, 30) : Color.FromArgb(30, 110, 40);
            var la = RapchaApp.LastSessionAudio.Bytes;
            _summary.Text = "Opened " + MicTracker.Opens + " times, closed " + MicTracker.Closes + " times since Rapcha started.  Timeline: " + DiagLog.Count + " lines.  " +
                            "Last dictation audio: " + (la > 0 ? (la / 32000.0).ToString("0.0") + " s" : "none") + ".  Also saved all the time in " + DiagnosticsInfo.Folder;
            _wav.Enabled = la > 0;
        }

        private void SetStatus(string text, bool bad)
        {
            _status.Text = text;
            _status.ForeColor = bad ? Color.FromArgb(150, 30, 30) : Color.FromArgb(30, 110, 40);
        }

        internal static string DefaultName(DateTime t) => "Rapcha-diagnostics-" + t.ToString("yyyy-MM-dd-HHmm") + ".txt";

        private string Pick(string defaultName, string filter, string folder = null)
        {
            if (PickPath != null) return PickPath(defaultName);
            using (var d = new SaveFileDialog
            {
                FileName = defaultName, Filter = filter, OverwritePrompt = true,
                InitialDirectory = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            })
                return d.ShowDialog(FindForm()) == DialogResult.OK ? d.FileName : null;
        }

        internal string LastSavedPath { get; private set; }

        internal void SaveTxt()
        {
            try
            {
                var path = Pick(DefaultName(DateTime.Now), "Text file (*.txt)|*.txt");
                if (path == null) return;
                File.WriteAllText(path, DiagnosticsInfo.Report(_words.Checked), new UTF8Encoding(false));
                LastSavedPath = path;
                AppStorage.Log("Diagnostics: saved a report (" + new FileInfo(path).Length / 1024 + " KB, words " + (_words.Checked ? "included" : "not included") + ")");
                SetStatus("Saved: " + path + "   Send this file.", false);
            }
            catch (Exception e) { DiagLog.Error("save diagnostics", e); SetStatus("Could not save the file (" + e.GetType().Name + "). Try the Documents folder.", true); }
        }

        private void Copy()
        {
            try
            {
                Clipboard.SetText(DiagnosticsInfo.Report(_words.Checked));
                SetStatus("Copied. You can paste it into an email or a message.", false);
            }
            catch (Exception e) { SetStatus("Could not copy (" + e.GetType().Name + "). Use Save as .txt instead.", true); }
        }

        internal void SaveWav()
        {
            try
            {
                var pcm = RapchaApp.LastSessionAudio.ToArray();
                if (pcm.Length == 0) { SetStatus("There is no dictation audio yet. Do one dictation first.", true); return; }
                string defaultName = LastSavedPath != null ? Path.ChangeExtension(Path.GetFileName(LastSavedPath), ".wav") : "Rapcha-last-session-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".wav";
                var path = Pick(defaultName, "WAV audio (*.wav)|*.wav", LastSavedPath != null ? Path.GetDirectoryName(LastSavedPath) : null);
                if (path == null) return;
                File.WriteAllBytes(path, WavFile.Build(pcm, 16000));
                AppStorage.Log("Diagnostics: saved last session audio (" + (pcm.Length / 32000.0).ToString("0.0") + " s)");
                SetStatus("Saved: " + path + "   This file has your voice in it. Send it only if you want to.", false);
            }
            catch (Exception e) { DiagLog.Error("save wav", e); SetStatus("Could not save the audio (" + e.GetType().Name + ").", true); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { DiagLog.Added -= _onLine; _tick.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
