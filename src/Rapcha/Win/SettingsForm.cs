using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Gemini API key, Microphone (test + choose + Windows permission) and Custom Vocabulary.</summary>
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _key = new TextBox();
        private readonly CheckBox _show = new CheckBox();
        private readonly ComboBox _mic = new ComboBox();
        private readonly ProgressBar _level = new ProgressBar();
        private readonly Label _micStatus = new Label();
        private readonly Button _micSettings = new Button();
        private readonly TextBox _newWord = new TextBox();
        private readonly ListBox _words = new ListBox();
        private readonly Button _add = new Button();
        private readonly Button _remove = new Button();
        private readonly Button _save = new Button();
        private readonly Button _copyLog = new Button();
        private readonly Label _status = new Label();
        private readonly Label _voiceStatus = new Label();
        private readonly TrackBar _speed = new TrackBar();
        private readonly Label _speedLabel = new Label();
        private readonly List<string> _terms;
        private readonly Timer _meterCheck = new Timer { Interval = 500 };

        private WaveInRecorder _meter;
        private DateTime _meterStarted;
        private bool _meterPaused;
        private float _lastLevel;

        public event Action Saved;

        public SettingsForm(string message = null)
        {
            Text = "Rapcha settings";
            Icon = Program.AppIcon;
            Font = new Font("Segoe UI", 10.5f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(540, 940);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, AutoScroll = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // API key
            _key.UseSystemPasswordChar = true;
            _key.Dock = DockStyle.Fill;
            _key.Text = AppStorage.LoadApiKey();
            _show.Text = "Show key";
            _show.AutoSize = true;
            _show.CheckedChanged += (s, e) => _key.UseSystemPasswordChar = !_show.Checked;

            // Microphone
            _mic.DropDownStyle = ComboBoxStyle.DropDownList;
            _mic.Dock = DockStyle.Fill;
            _mic.Items.Add("Windows default microphone");
            foreach (var d in WaveInRecorder.ListDevices()) _mic.Items.Add(d);
            var saved = AppStorage.LoadMicDevice();
            int idx = saved.Length == 0 ? 0 : _mic.Items.IndexOf(saved);
            _mic.SelectedIndex = idx < 0 ? 0 : idx;
            _mic.SelectedIndexChanged += (s, e) =>
            {
                AppStorage.SaveMicDevice(_mic.SelectedIndex <= 0 ? "" : (string)_mic.SelectedItem);
                RestartMeter();
            };
            _level.Dock = DockStyle.Fill;
            _level.Height = 22;
            _level.Style = ProgressBarStyle.Continuous;
            _level.Maximum = 100;
            _micStatus.AutoSize = true;
            _micStatus.MaximumSize = new Size(490, 0);
            _micStatus.Text = "Say something: the bar should move when you talk.";
            _micSettings.Text = "Open microphone settings";
            _micSettings.AutoSize = true;
            _micSettings.Padding = new Padding(14, 6, 14, 6);
            _micSettings.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            _micSettings.Click += (s, e) => OpenMicrophoneSettings();
            _meterCheck.Tick += (s, e) => CheckMeter();

            // Vocabulary
            var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _newWord.Dock = DockStyle.Fill;
            _add.Text = "Add";
            _add.AutoSize = true;
            _add.Click += (s, e) => AddWord();
            _newWord.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddWord(); } };
            addRow.Controls.Add(_newWord, 0, 0);
            addRow.Controls.Add(_add, 1, 0);

            _terms = AppStorage.LoadVocabulary();
            _words.Dock = DockStyle.Fill;
            _words.IntegralHeight = false;
            _words.Height = 110;
            foreach (var t in _terms) _words.Items.Add(t);
            _words.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveWord(); };
            _remove.Text = "Remove selected";
            _remove.AutoSize = true;
            _remove.Click += (s, e) => RemoveWord();

            // Read-aloud voice
            _voiceStatus.AutoSize = true;
            _voiceStatus.MaximumSize = new Size(490, 0);
            var speedRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _speed.Minimum = 7; _speed.Maximum = 15; _speed.TickFrequency = 1; _speed.SmallChange = 1; _speed.LargeChange = 1;
            _speed.Dock = DockStyle.Fill;
            _speed.Value = (int)Math.Round(VoiceSettings.Speed * 10);
            _speedLabel.AutoSize = true;
            _speedLabel.Anchor = AnchorStyles.Left;
            _speed.ValueChanged += (s, e) => { VoiceSettings.Speed = _speed.Value / 10.0; ShowSpeed(); };
            ShowSpeed();
            speedRow.Controls.Add(_speed, 0, 0);
            speedRow.Controls.Add(_speedLabel, 1, 0);
            _voiceStatus.Text = VoiceText();

            // Bottom
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            _save.Text = "Save";
            _save.AutoSize = true;
            _save.Padding = new Padding(18, 4, 18, 4);
            _save.Click += async (s, e) => await SaveAsync();
            _copyLog.Text = "Copy log";
            _copyLog.AutoSize = true;
            _copyLog.Click += (s, e) => CopyLog();
            bottom.Controls.Add(_save);
            bottom.Controls.Add(_copyLog);

            _status.AutoSize = true;
            _status.MaximumSize = new Size(490, 0);
            _status.Text = message ?? "";
            ShowMessage(message);

            layout.Controls.Add(Title("Gemini API key"));
            layout.Controls.Add(Help("Paste your key from aistudio.google.com/apikey. It is saved encrypted on this PC only."));
            layout.Controls.Add(_key);
            layout.Controls.Add(_show);
            layout.Controls.Add(Title("Microphone"));
            layout.Controls.Add(_mic);
            layout.Controls.Add(_level);
            layout.Controls.Add(_micStatus);
            layout.Controls.Add(_micSettings);
            layout.Controls.Add(Title("Custom Vocabulary"));
            layout.Controls.Add(Help("Add names and words you want spelled your way, like \"Prince Nii\"."));
            layout.Controls.Add(addRow);
            layout.Controls.Add(_words);
            layout.Controls.Add(_remove);
            layout.Controls.Add(Title("Select to speak"));
            layout.Controls.Add(Help("Double-click the ring, then drag a box around any text on the screen (or tap a word) to hear it. Esc closes."));
            layout.Controls.Add(_voiceStatus);
            layout.Controls.Add(speedRow);
            layout.Controls.Add(_status);
            layout.Controls.Add(bottom);
            layout.RowCount = layout.Controls.Count;
            for (int i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height - 20) Height = wa.Height - 20;
            CenterToScreen();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RestartMeter();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopMeter();
            _meterCheck.Dispose();
            base.OnFormClosed(e);
        }

        private void ShowSpeed() => _speedLabel.Text = "Speed " + (_speed.Value / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";

        private static string VoiceText()
        {
            try
            {
                using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                {
                    var v = synth.Voice?.Name;
                    return string.IsNullOrEmpty(v) ? "No Windows voice is installed." : "Voice: " + v + " (built into Windows, works offline)";
                }
            }
            catch { return "No Windows voice is installed."; }
        }

        public static void OpenMicrophoneSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
            catch (Exception ex) { AppStorage.Log("Could not open microphone settings: " + ex.Message); }
        }

        /// <summary>The ring is about to record: free the microphone.</summary>
        public void PauseMeter() { _meterPaused = true; StopMeter(); }

        public void ResumeMeter()
        {
            _meterPaused = false;
            if (Visible && !IsDisposed) RestartMeter();
        }

        private void RestartMeter()
        {
            StopMeter();
            if (_meterPaused || IsDisposed) return;
            int device = WaveInRecorder.ResolveDevice(_mic.SelectedIndex <= 0 ? "" : (string)_mic.SelectedItem);
            _meter = new WaveInRecorder(_ => { }, level =>
            {
                _lastLevel = level;
                try { BeginInvoke(new Action(() => { if (!IsDisposed) _level.Value = (int)(level * 100); })); } catch { }
            });
            var problem = _meter.Start(device, out var detail);
            AppStorage.Log("Mic test: device=\"" + _meter.DeviceName + "\" result=" + problem + " format=" + _meter.FormatDescription + " attempts=[" + _meter.OpenAttempts + "] " + detail);
            if (problem != MicProblem.None)
            {
                _meter = null;
                SetMicStatus(MicHelp(problem), true);
                return;
            }
            _meterStarted = DateTime.UtcNow;
            SetMicStatus("Say something: the bar should move when you talk.", false);
            _meterCheck.Start();
        }

        private void StopMeter()
        {
            _meterCheck.Stop();
            var m = _meter;
            _meter = null;
            if (m != null) Task.Run(() => m.Abort());
            if (!IsDisposed) _level.Value = 0;
        }

        private void CheckMeter()
        {
            var m = _meter;
            if (m == null) return;
            if ((DateTime.UtcNow - _meterStarted).TotalSeconds < 1.5) return;
            if (m.BytesCaptured == 0)
                SetMicStatus("No sound is coming from this microphone. " + Messages.MicSteps, true);
            else if (m.LooksSilentlyBlocked)
                SetMicStatus("Windows is giving Rapcha silence. " + Messages.MicSteps, true);
            else if (m.MaxPeak > 300)
                SetMicStatus("Your microphone works ✓", false, true);
        }

        private void SetMicStatus(string text, bool bad, bool good = false)
        {
            _micStatus.Text = text;
            _micStatus.ForeColor = bad ? Color.FromArgb(150, 30, 30) : good ? Color.FromArgb(30, 110, 40) : SystemColors.ControlText;
        }

        public static string MicHelp(MicProblem p)
        {
            switch (p)
            {
                case MicProblem.NoMicrophone: return Messages.NoMic;
                case MicProblem.PrivacyBlocked: return Messages.MicPrivacy;
                case MicProblem.InUse: return Messages.MicInUse;
                default: return Messages.MicOther;
            }
        }

        private void CopyLog()
        {
            try
            {
                Clipboard.SetText(AppStorage.ReadLogTail());
                ShowMessage("Log copied. Paste it into a message to us. It has no key and none of your words.", true);
            }
            catch (Exception ex) { ShowMessage("Could not copy the log: " + ex.Message); }
        }

        public void ShowMessage(string text, bool good = false)
        {
            _status.ForeColor = good ? Color.FromArgb(30, 100, 40) : Color.FromArgb(150, 30, 30);
            _status.Text = text ?? "";
        }

        private static Label Title(string text) => new Label
        {
            Text = text, AutoSize = true, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2)
        };

        private static Label Help(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(490, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 6)
        };

        private void AddWord()
        {
            if (VocabularyList.TryAdd(_terms, _newWord.Text))
            {
                _words.Items.Insert(0, _terms[0]);
                _newWord.Clear();
                ShowMessage("");
            }
            else if (_newWord.Text.Trim().Length > 0)
            {
                ShowMessage("That word is already in the list.");
            }
            _newWord.Focus();
        }

        private void RemoveWord()
        {
            int i = _words.SelectedIndex;
            if (i < 0) return;
            var term = (string)_words.Items[i];
            _terms.RemoveAll(t => t == term);
            _words.Items.RemoveAt(i);
            if (_words.Items.Count > 0) _words.SelectedIndex = Math.Min(i, _words.Items.Count - 1);
        }

        private async Task SaveAsync()
        {
            if (_newWord.Text.Trim().Length > 0) AddWord();
            var key = _key.Text.Trim();
            try
            {
                AppStorage.SaveApiKey(key);
                AppStorage.SaveVocabulary(_terms);
                AppStorage.SaveMicDevice(_mic.SelectedIndex <= 0 ? "" : (string)_mic.SelectedItem);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not save settings: " + ex.Message);
                return;
            }
            Saved?.Invoke();
            if (LiveSession.IsPlaceholder(key))
            {
                ShowMessage("Saved. Rapcha needs a Gemini API key before it can type for you.");
                return;
            }
            ShowMessage("Saved. Checking your key…", true);
            _save.Enabled = false;
            var problem = await KeyCheck.RunAsync(key, _terms);
            _save.Enabled = true;
            if (IsDisposed) return;
            if (problem == null) ShowMessage("Saved. Your key works ✓  Click the ring and start talking.", true);
            else ShowMessage("Saved, but: " + Messages.For(problem));
        }
    }

    /// <summary>Opens one Live session and waits for setupComplete, without sending audio.</summary>
    public static class KeyCheck
    {
        public static async Task<LiveError> RunAsync(string key, IList<string> vocab)
        {
            var session = new LiveSession(key, vocab);
            LiveError failure = null;
            var failed = new TaskCompletionSource<bool>();
            session.Failed += e => { failure = e; failed.TrySetResult(true); };
            try
            {
                await session.ConnectAsync();
                var deadline = Task.Delay(12000);
                while (!session.IsSetupComplete && failure == null)
                {
                    var done = await Task.WhenAny(failed.Task, deadline, Task.Delay(100));
                    if (done == deadline) return failure ?? new LiveError(LiveErrorKind.NoInternet, "Timed out waiting for Gemini");
                }
                AppStorage.Log("Key check: " + (failure == null ? "ok, setupComplete after " + session.SetupMs + " ms" : failure.Kind.ToString()));
                return failure;
            }
            finally { session.Dispose(); }
        }
    }
}
