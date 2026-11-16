using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// The Settings page of the main window: Gemini API key, Microphone (test, choose, Windows permission),
    /// Glow colour and Select to speak. (Custom vocabulary has its own page.)
    /// </summary>
    public sealed class SettingsPage : UserControl
    {
        private readonly TextBox _key = new TextBox();
        private readonly CheckBox _show = new CheckBox();
        private readonly ComboBox _mic = new ComboBox();
        private readonly ProgressBar _level = new ProgressBar();
        private readonly Label _micStatus = new Label();
        private readonly PillButton _micSettings = new PillButton();
        private readonly PillButton _save = new PillButton { Primary = true };
        private readonly PillButton _copyLog = new PillButton();
        private readonly Label _status = new Label();
        private readonly Label _voiceStatus = new Label();
        private readonly TrackBar _speed = new TrackBar();
        private readonly Label _speedLabel = new Label();
        private readonly ComboBox _voice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430, Font = new Font(Theme.Sans, 10f) };
        private readonly PillButton _previewVoice = new PillButton { Text = "Preview" };
        private readonly Label _voiceHint = new Label();
        private FlowLayoutPanel _voiceRow;
        private IReadAloud _preview;
        private bool _loadingVoices;
        /// <summary>Self-test: Preview speaks into memory instead of the speakers.</summary>
        internal static bool SilentPreview;
        internal ComboBox VoicePicker => _voice;
        internal PillButton PreviewButton => _previewVoice;
        internal IReadAloud LastPreview => _preview;
        private readonly GlowPicker _glow = new GlowPicker();
        private readonly ShortcutsRows _shortcuts = new ShortcutsRows();
        internal ShortcutsRows Shortcuts => _shortcuts;
        private readonly Timer _meterCheck = new Timer { Interval = 500 };
        private readonly TableLayoutPanel _layout;

        private WaveInRecorder _meter;
        private DateTime _meterStarted;
        private bool _meterPaused;
        private bool _pageShown;
        private float _lastLevel;

        public event Action Saved;
        public GlowPicker Glow => _glow;

        public SettingsPage(string message = null)
        {
            BackColor = Theme.Cream;
            Font = new Font(Theme.Sans, 10.5f);
            DoubleBuffered = true;

            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(32, 26, 32, 24), ColumnCount = 1, AutoScroll = true, BackColor = Theme.Cream
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // API key
            _key.UseSystemPasswordChar = true;
            _key.Dock = DockStyle.Fill;
            _key.BorderStyle = BorderStyle.FixedSingle;
            _key.Font = new Font(Theme.Sans, 11.5f);
            _key.Text = AppStorage.LoadApiKey();
            _show.Text = "Show key";
            _show.AutoSize = true;
            _show.BackColor = Color.Transparent;
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
            _level.Height = 18;
            _level.Style = ProgressBarStyle.Continuous;
            _level.Maximum = 100;
            _micStatus.AutoSize = true;
            _micStatus.BackColor = Color.Transparent;
            _micStatus.MaximumSize = new Size(700, 0);
            _micStatus.Text = "Say something: the bar should move when you talk.";
            _micSettings.Text = "Open microphone settings";
            _micSettings.Click += (s, e) => OpenMicrophoneSettings();
            _meterCheck.Tick += (s, e) => CheckMeter();

            // Glow colour
            _glow.Selected = AppStorage.LoadGlowColor();
            _glow.Changed += rgb => AppStorage.SaveGlowColor(rgb);

            // Read-aloud voice
            _voiceStatus.AutoSize = true;
            _voiceStatus.BackColor = Color.Transparent;
            _voiceStatus.MaximumSize = new Size(700, 0);
            var speedRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0), BackColor = Color.White };
            speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            speedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _speed.Minimum = 7; _speed.Maximum = 15; _speed.TickFrequency = 1; _speed.SmallChange = 1; _speed.LargeChange = 1;
            _speed.Dock = DockStyle.Fill;
            _speed.BackColor = Color.White;
            _speed.Value = Math.Max(7, Math.Min(15, (int)Math.Round(VoiceSettings.Speed * 10)));
            _speedLabel.AutoSize = true;
            _speedLabel.Anchor = AnchorStyles.Left;
            _speedLabel.BackColor = Color.White;
            _speed.ValueChanged += (s, e) => { VoiceSettings.Speed = _speed.Value / 10.0; ShowSpeed(); };
            ShowSpeed();
            speedRow.Controls.Add(_speed, 0, 0);
            speedRow.Controls.Add(_speedLabel, 1, 0);
            _voiceStatus.Text = VoiceText();
            SetupVoicePicker();

            _save.Text = "Save";
            _save.Click += async (s, e) => await SaveAsync();
            _copyLog.Text = "Copy log";
            _copyLog.Click += (s, e) => CopyLog();

            _status.AutoSize = true;
            _status.BackColor = Color.Transparent;
            _status.MaximumSize = new Size(700, 0);
            _status.Text = message ?? "";
            ShowMessage(message);

            var title = new Label { Text = "Settings", AutoSize = true, Font = new Font(Theme.Serif, 20f), ForeColor = Theme.Ink, Margin = new Padding(2, 0, 0, 8) };
            _layout.Controls.Add(title);
            _layout.Controls.Add(Card("Gemini API key",
                Help("Paste your key from aistudio.google.com/apikey. It is saved encrypted on this PC only."),
                _key, _show, Row(_save), _status));
            _layout.Controls.Add(Card("Microphone", _mic, _level, _micStatus, Row(_micSettings)));
            _layout.Controls.Add(Card("Glow colour",
                Help("Applies to the ring glow, the words box, the text-box outline and the select-to-speak screen glow."),
                _glow));
            _layout.Controls.Add(Card("Shortcuts",
                Help("Start voice typing or select to speak from the keyboard, in any app."),
                _shortcuts));
            _layout.Controls.Add(Card("Select to speak",
                Help("Double-click the ring, then drag a box around any text on the screen (or tap a word) to hear it. Esc closes."),
                _voiceRow, _voiceHint, speedRow));
            _layout.Controls.Add(Card("Help",
                Help("The log has no key and none of your words."), Row(_copyLog)));
            _layout.RowCount = _layout.Controls.Count;
            for (int i = 0; i < _layout.RowCount; i++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(_layout);
        }

        private static Control Card(string title, params Control[] children)
        {
            var c = new CardPanel();
            c.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Theme.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) });
            foreach (var ch in children) c.Controls.Add(ch);
            c.RowCount = c.Controls.Count;
            for (int i = 0; i < c.RowCount; i++) c.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return c;
        }

        private static Control Row(params Control[] buttons)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 6, 0, 2), WrapContents = false };
            foreach (var b in buttons) { b.Margin = new Padding(0, 0, 10, 0); f.Controls.Add(b); }
            return f;
        }

        /// <summary>Called when the page is shown or hidden in the main window: the mic test only runs while you can see it.</summary>
        public void SetPageShown(bool shown)
        {
            _pageShown = shown;
            if (shown) RestartMeter(); else StopMeter();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopMeter();
                _meterCheck.Dispose();
                try { _preview?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void SetupVoicePicker()
        {
            _voiceRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            _voice.Margin = new Padding(0, 4, 10, 0);
            _previewVoice.Margin = new Padding(0, 0, 0, 0);
            _voiceRow.Controls.Add(new Label { Text = "Voice", AutoSize = true, Font = new Font(Theme.Sans, 10f, FontStyle.Bold), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 8, 10, 0) });
            _voiceRow.Controls.Add(_voice);
            _voiceRow.Controls.Add(_previewVoice);
            _voiceHint.Text = "More voices: Windows Settings > Time & Language > Speech > Add voices.";
            _voiceHint.AutoSize = true;
            _voiceHint.UseMnemonic = false;   // show the "&" in "Time & Language"
            _voiceHint.ForeColor = Theme.Muted;
            _voiceHint.BackColor = Color.White;
            _voiceHint.Margin = new Padding(0, 2, 0, 6);
            LoadVoices();
            _voice.SelectedIndexChanged += (s, e) =>
            {
                if (_loadingVoices || !(_voice.SelectedItem is VoiceEntry v)) return;
                Voices.SavedId = v.Id;
                AppStorage.Log("Voices: chosen " + v.Label + (v.Desktop ? " [SAPI]" : " [OneCore]"));
            };
            _previewVoice.Click += (s, e) => PreviewVoice();
        }

        /// <summary>Fills the list (all OneCore + SAPI voices) and selects the saved one, or the Windows default.</summary>
        internal void LoadVoices()
        {
            _loadingVoices = true;
            try
            {
                var all = Voices.All();
                _voice.Items.Clear();
                foreach (var v in all) _voice.Items.Add(v);
                var want = Voices.SavedId;
                int i = all.FindIndex(v => v.Id == want);
                if (i < 0) { var def = Voices.DefaultSapiId(); i = all.FindIndex(v => v.Id == def); }
                if (i < 0 && all.Count > 0) i = 0;
                if (i >= 0) _voice.SelectedIndex = i;
                _voice.Enabled = _previewVoice.Enabled = all.Count > 0;
                if (all.Count == 0) _voiceHint.Text = "No Windows voice is installed. Add one in Windows Settings > Time & Language > Speech > Add voices.";
                AppStorage.Log("Voices: listed " + all.Count + " (onecore=" + all.FindAll(v => !v.Desktop).Count + ", sapi=" + all.FindAll(v => v.Desktop).Count + ")");
            }
            finally { _loadingVoices = false; }
        }

        private void PreviewVoice()
        {
            if (!(_voice.SelectedItem is VoiceEntry v)) return;
            try { _preview?.Dispose(); } catch { }
            _preview = Voices.CreateReader(SilentPreview, v.Id);
            _preview.Read("Hello, I'm " + VoiceLabels.ShortName(v.FullName) + ". This is how I sound when Rapcha reads to you.");
            AppStorage.Log("Voices: preview " + v.Label + " reader=" + _preview.GetType().Name);
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
            if (_pageShown && !IsDisposed) RestartMeter();
        }

        private void RestartMeter()
        {
            StopMeter();
            if (_meterPaused || !_pageShown || IsDisposed) return;
            int device = WaveInRecorder.ResolveDevice(_mic.SelectedIndex <= 0 ? "" : (string)_mic.SelectedItem);
            _meter = new WaveInRecorder(_ => { }, level =>
            {
                _lastLevel = level;
                try { BeginInvoke(new Action(() => { if (!IsDisposed) _level.Value = Math.Max(0, Math.Min(100, (int)(level * 100))); })); } catch { }
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
            if (!IsDisposed && _level.IsHandleCreated) _level.Value = 0;
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
            _micStatus.ForeColor = bad ? Color.FromArgb(150, 30, 30) : good ? Color.FromArgb(30, 110, 40) : Theme.Ink;
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

        private static Label Help(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8)
        };

        private async Task SaveAsync()
        {
            var key = _key.Text.Trim();
            try
            {
                AppStorage.SaveApiKey(key);
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
            var problem = await KeyCheck.RunAsync(key, AppStorage.LoadVocabulary());
            _save.Enabled = true;
            if (IsDisposed) return;
            if (problem == null) ShowMessage("Saved. Your key works ✓  Click the ring and start talking.", true);
            else ShowMessage("Saved, but: " + Messages.For(problem));
        }
    }

    /// <summary>A white rounded card on the cream page (a one-column table that sizes to its content).</summary>
    internal sealed class CardPanel : TableLayoutPanel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            Padding = new Padding(22, 18, 22, 18);
            Margin = new Padding(0, 6, 0, 10);
            BackColor = Color.White;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var s = base.GetPreferredSize(proposedSize);
            int max = (int)(820 * DeviceDpi / 96f);
            return new Size(Math.Min(s.Width, max), s.Height);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Cream);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float k = DeviceDpi / 96f;
            Theme.FillCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 18 * k, Theme.Card, Theme.CardBorder);
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
