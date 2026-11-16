using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Settings → Offline voice typing: the Offline fallback switch, what this laptop can do (its level), built-in
    /// Windows speech (with "Get Windows speech" when it's missing), the "Download Whisper model" button (always
    /// shown; greyed out on a Low laptop), and a placeholder for an optional offline polishing model.
    /// </summary>
    internal sealed class OfflineCard
    {
        public readonly CardPanel Card = new CardPanel();
        private readonly CheckBox _fallback = new CheckBox { Text = "Offline fallback", AutoSize = true };
        private readonly Label _fallbackHint = Note("When there's no internet (or it drops while you talk), Rapcha keeps typing with speech on this laptop and keeps every word. A small \"Offline\" tag shows in the words box. It goes back to Gemini when you're online.");
        private readonly Label _device = Note("");
        private readonly Label _speech = Note("");
        private readonly PillButton _getSpeech = new PillButton { Text = "Get Windows speech" };
        private readonly Label _speechHelp = Note(BuiltInSpeech.Instructions);
        private readonly Label _whisperTitle = new Label { Text = "Whisper (better offline accuracy)", AutoSize = true, Font = new Font(Theme.Sans, 10f, FontStyle.Bold), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 10, 0, 2) };
        private readonly ComboBox _model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Font = new Font(Theme.Sans, 10f) };
        private readonly PillButton _download = new PillButton { Text = "Download Whisper model", Primary = true };
        private readonly PillButton _cancel = new PillButton { Text = "Cancel" };
        private readonly PillButton _delete = new PillButton { Text = "Delete model" };
        private readonly ProgressBar _progress = new ProgressBar { Width = 330, Height = 14, Maximum = 1000, Style = ProgressBarStyle.Continuous };
        private readonly Label _whisperNote = Note("");
        private readonly PillButton _anyway = new PillButton { Text = "Download lightest model anyway" };
        private readonly FlowLayoutPanel _anywayRow;
        private readonly CheckBox _polish = new CheckBox { Text = "Offline smart polish (small model)", AutoSize = true, Enabled = false };
        private readonly Label _polishNote = Note("");
        private readonly FlowLayoutPanel _modelRow, _speechRow, _progressRow;
        private CancellationTokenSource _cts;
        private bool _loading;

        // self-test access
        internal PillButton DownloadButton => _download;
        internal PillButton GetSpeechButton => _getSpeech;
        internal PillButton CancelButton => _cancel;
        internal PillButton DeleteButton => _delete;
        internal CheckBox FallbackToggle => _fallback;
        internal Label WhisperNote => _whisperNote;
        internal ComboBox ModelPicker => _model;
        internal CheckBox PolishToggle => _polish;
        internal bool Downloading => _cts != null;
        internal int DownloadClicks { get; private set; }
        internal PillButton AnywayButton => _anyway;
        internal int AnywayClicks { get; private set; }
        internal int Warnings { get; private set; }
        internal int ProgressEvents;
        internal string LastSpeechUri { get; private set; } = "";
        /// <summary>Self-test: don't really open Windows Settings.</summary>
        internal static bool DontOpenSettings;
        internal event Action<ModelStore.Outcome> DownloadFinished;

        public OfflineCard()
        {
            Card.Controls.Add(new Label { Text = "Offline voice typing", AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Theme.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) });
            _fallback.Font = new Font(Theme.Sans, 10f);
            _fallback.ForeColor = Theme.Ink;
            _fallback.BackColor = Color.White;
            _fallback.Checked = OfflineSettings.Fallback;
            _fallback.CheckedChanged += (s, e) => { if (!_loading) OfflineSettings.Fallback = _fallback.Checked; };
            Card.Controls.Add(_fallback);
            _fallbackHint.Margin = new Padding(20, 0, 0, 8);
            Card.Controls.Add(_fallbackHint);
            Card.Controls.Add(_device);

            _speechRow = Row(_getSpeech);
            _getSpeech.Click += (s, e) => OpenSpeechSettings();
            Card.Controls.Add(_speech);
            Card.Controls.Add(_speechRow);
            Card.Controls.Add(_speechHelp);

            Card.Controls.Add(_whisperTitle);
            _modelRow = Row(_model, _download, _delete);
            _model.Margin = new Padding(0, 4, 10, 0);
            _model.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || !(_model.SelectedItem is ModelItem mi)) return;
                OfflineSettings.ChosenModel = mi.Model.Id;
                Refresh();
            };
            Card.Controls.Add(_modelRow);
            _progressRow = Row(_progress, _cancel);
            _progress.Margin = new Padding(0, 8, 10, 0);
            Card.Controls.Add(_progressRow);
            Card.Controls.Add(_whisperNote);
            _anywayRow = Row(_anyway);
            _anywayRow.Margin = new Padding(0, 0, 0, 6);
            Card.Controls.Add(_anywayRow);
            _anyway.Click += (s, e) => DownloadAnyway();
            _download.Click += (s, e) => StartDownload();
            _cancel.Click += (s, e) => _cts?.Cancel();
            _delete.Click += (s, e) =>
            {
                var m = Current();
                if (m == null || _cts != null) return;
                ModelStore.Delete(m);
                AppStorage.Log("Settings: Whisper model deleted (" + m.Id + "); offline voice typing uses built-in Windows speech from now on");
                Refresh();
                if (!ModelStore.IsInstalled(m)) _whisperNote.Text = "Deleted. Offline voice typing uses built-in Windows speech from now on.";
            };

            _polish.Font = new Font(Theme.Sans, 10f);
            _polish.BackColor = Color.White;
            _polish.Margin = new Padding(0, 10, 0, 0);
            Card.Controls.Add(_polish);
            Card.Controls.Add(_polishNote);

            Card.RowCount = Card.Controls.Count;
            for (int i = 0; i < Card.RowCount; i++) Card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            DeviceProbe.Changed += OnExternalChange;
            ModelStore.Changed += OnExternalChange;
            Card.Disposed += (s, e) => { DeviceProbe.Changed -= OnExternalChange; ModelStore.Changed -= OnExternalChange; _cts?.Cancel(); };
            Refresh();
        }

        private void OnExternalChange()
        {
            if (Card.IsDisposed) return;
            if (Card.InvokeRequired) { try { Card.BeginInvoke(new Action(Refresh)); } catch { } return; }
            Refresh();
        }

        private sealed class ModelItem
        {
            public WhisperModel Model;
            public override string ToString() => Model.Label + (ModelStore.IsInstalled(Model) ? "  ✓ downloaded" : "");
        }

        private WhisperModel Current() => (_model.SelectedItem as ModelItem)?.Model ?? WhisperModels.Choose(DeviceProbe.Tier, OfflineSettings.ChosenModel);

        /// <summary>Shows the state for this laptop's level (also after the self-test fakes another laptop).</summary>
        public void Refresh()
        {
            _loading = true;
            try
            {
                var facts = DeviceProbe.Facts;
                var tier = DeviceTiers.Classify(facts);
                _fallback.Checked = OfflineSettings.Fallback;
                _device.Text = "This laptop: " + DeviceTiers.Describe(facts) + ". Level: " + DeviceTiers.Name(tier) +
                               (tier == DeviceTier.Low ? (LowHasWhisper() ? " (you chose Whisper Tiny anyway: up to 2 minutes at a time offline)." : " (built-in Windows speech, up to 5 minutes at a time).") :
                                tier == DeviceTier.Medium ? " (Whisper Tiny, up to 2 minutes at a time)." : " (Whisper Base or Small, up to 5 minutes at a time).");

                bool speech = BuiltInSpeech.Available;
                _speech.Text = speech ? "Built-in Windows speech: ready (" + BuiltInSpeech.RecognizerName + "). Works offline, also in Windows S mode."
                                      : "Built-in Windows speech: not installed on this laptop.";
                _speech.ForeColor = speech ? Theme.Muted : Color.FromArgb(170, 70, 50);
                _speechRow.Visible = !speech;
                _speechHelp.Visible = !speech;

                var allowed = WhisperModels.Usable(tier);
                var chosen = WhisperModels.Choose(tier, OfflineSettings.ChosenModel);
                _model.Items.Clear();
                foreach (var m in allowed) _model.Items.Add(new ModelItem { Model = m });
                int idx = Enumerable.Range(0, _model.Items.Count).FirstOrDefault(i => ((ModelItem)_model.Items[i]).Model == chosen);
                if (_model.Items.Count > 0) _model.SelectedIndex = Math.Max(0, idx);
                bool low = tier == DeviceTier.Low;
                bool busy = _cts != null;
                var cur = Current();
                bool installed = cur != null && ModelStore.IsInstalled(cur);
                _download.Visible = true;                        // always visible…
                _download.Enabled = !low && !busy && !installed;  // …greyed out on a Low laptop (a click does nothing)
                _anywayRow.Visible = low && !busy && !installed;  // v17: …but a Low laptop may download the lightest model anyway, after a warning
                _download.Text = installed ? "Downloaded ✓" : "Download Whisper model";   // short, so Delete model always fits beside it
                _model.Enabled = !low && !busy && allowed.Count > 1;
                _delete.Visible = installed && !busy;
                _progressRow.Visible = busy;
                if (low && installed)
                    _whisperNote.Text = WhisperNative.SimulateBlocked || WhisperNative.Status == WhisperNative.State.Blocked ? WhisperNative.BlockedMessage :
                        "Whisper Tiny is downloaded, so Rapcha uses it when you're offline (up to 2 minutes at a time). Your laptop's memory is low: " +
                        "if it gets slow or hot, press Delete model and Rapcha goes straight back to built-in Windows speech.";
                else if (low && busy)
                    _whisperNote.Text = "Your laptop's memory is too low for this, but you chose to try it. Downloading Whisper Tiny from the official whisper.cpp models on Hugging Face…";
                else if (low)
                    _whisperNote.Text = "Your laptop's memory is too low for this. Rapcha will use built-in Windows speech instead. You can still try the lightest Whisper model at your own risk.";
                else if (busy)
                    _whisperNote.Text = "Downloading from the official whisper.cpp models on Hugging Face…";
                else if (installed)
                    _whisperNote.Text = WhisperStatus(cur);
                else
                    _whisperNote.Text = cur.Label + " from the official whisper.cpp models on Hugging Face. Checked after download, kept in %AppData%\\Rapcha\\models. It loads only while you talk.";
                _whisperNote.ForeColor = low ? Color.FromArgb(150, 90, 40) : Theme.Muted;
                _delete.Text = "Delete model";

                _polish.Visible = _polishNote.Visible = tier == DeviceTier.Strong;
                _polish.Checked = false;
                _polishNote.Text = "Coming later (off). Offline, Rapcha tidies your words with simple rules: capitals, full stops, no \"um\" or \"uh\", and your Custom Vocabulary. " +
                                   "A small polishing model doesn't fit inside Rapcha's single app file yet. Online, Gemini's smart polish is unchanged.";
            }
            finally { _loading = false; }
        }

        private static bool LowHasWhisper() => ModelStore.IsInstalled(WhisperModels.Lightest);

        private static string WhisperStatus(WhisperModel m)
        {
            var st = WhisperNative.Status;
            if (WhisperNative.SimulateBlocked || st == WhisperNative.State.Blocked) return WhisperNative.BlockedMessage;
            if (!WhisperNative.IsBundled || st == WhisperNative.State.NotBundled) return "Downloaded, but this copy of Rapcha has no Whisper engine inside; built-in Windows speech is used.";
            if (st == WhisperNative.State.Failed) return "Downloaded, but Whisper couldn't start on this laptop; built-in Windows speech is used.";
            return "Ready: " + m.Label + ". Used when you're offline; it loads only while you talk and is freed right after.";
        }

        /// <summary>Low laptop: warn (hot, slow, memory, battery), then download Whisper Tiny only if the user says so.</summary>
        private void DownloadAnyway()
        {
            AnywayClicks++;
            if (_cts != null || DeviceProbe.Tier != DeviceTier.Low) return;
            Warnings++;
            bool yes = LowMemoryWarning.Confirm(Card.FindForm(), DeviceProbe.Facts);
            AppStorage.Log("Settings: Low laptop, download lightest Whisper anyway: " + (yes ? "confirmed" : "cancelled"));
            if (!yes) return;
            OfflineSettings.ChosenModel = WhisperModels.Lightest.Id;
            StartDownload(true);
        }

        private async void StartDownload(bool anyway = false)
        {
            if (!anyway) DownloadClicks++;
            var m = anyway ? WhisperModels.Lightest : Current();
            if (m == null || (DeviceProbe.Tier == DeviceTier.Low && !anyway) || _cts != null) return;
            _cts = new CancellationTokenSource();
            _progress.Value = 0;
            Refresh();
            var cts = _cts;
            var outcome = await ModelStore.DownloadAsync(m, (got, total) =>
            {
                Interlocked.Increment(ref ProgressEvents);
                try { Card.BeginInvoke(new Action(() => { if (total > 0 && !Card.IsDisposed) _progress.Value = (int)Math.Min(1000, got * 1000 / total); })); } catch { }
            }, cts.Token);
            _cts = null;
            if (Card.IsDisposed) return;
            Refresh();
            switch (outcome)
            {
                case ModelStore.Outcome.Cancelled: _whisperNote.Text = "Download cancelled. Nothing was kept."; break;
                case ModelStore.Outcome.BadChecksum: _whisperNote.Text = "The download didn't match the official file, so it was deleted. Try again."; break;
                case ModelStore.Outcome.NoDisk: _whisperNote.Text = "Not enough free disk space for this model."; break;
                case ModelStore.Outcome.Failed: _whisperNote.Text = "The download didn't finish. Check your internet and try again."; break;
                default:
                    var _ = System.Threading.Tasks.Task.Run(() => WhisperNative.EnsureLoaded()).ContinueWith(t => OnExternalChange());
                    break;
            }
            DownloadFinished?.Invoke(outcome);
        }

        internal void OpenSpeechSettings()
        {
            LastSpeechUri = BuiltInSpeech.SettingsUri;
            AppStorage.Log("Settings: Get Windows speech pressed");
            if (DontOpenSettings) return;
            try { Process.Start(new ProcessStartInfo(BuiltInSpeech.SettingsUri) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo(BuiltInSpeech.LanguageUri) { UseShellExecute = true }); LastSpeechUri = BuiltInSpeech.LanguageUri; } catch { }
            }
        }

        private static Label Note(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Theme.Muted, BackColor = Color.White, Margin = new Padding(0, 2, 0, 6), UseMnemonic = false
        };

        private static FlowLayoutPanel Row(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            foreach (var c in items) { if (c.Margin == Padding.Empty || c is PillButton) c.Margin = new Padding(0, 0, 10, 0); f.Controls.Add(c); }
            return f;
        }
    }
}
