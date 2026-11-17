using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Settings → Test microphone (v18). Records about 3 seconds through exactly the path voice typing uses (the same
    /// waveIn device, the same conversion to 16 kHz mono 16-bit), shows the microphone's name, the format Windows runs
    /// it at and the format Rapcha opened, a level meter, and what it heard (level, speech, missing audio), then plays
    /// the 16 kHz mono result back so you hear what the offline engines hear. Nothing is saved unless you press
    /// "Save as WAV" (then only to %AppData%\Rapcha\mic-test.wav on this laptop). Logs never contain the audio.
    /// </summary>
    internal sealed class MicTestCard
    {
        public const int RecordMs = 3000;
        public readonly CardPanel Card = new CardPanel();
        private readonly PillButton _test = new PillButton { Text = "Test microphone (3 s)", Primary = true };
        private readonly PillButton _play = new PillButton { Text = "Play back", Enabled = false };
        private readonly PillButton _save = new PillButton { Text = "Save as WAV", Enabled = false };
        private readonly ProgressBar _meter = new ProgressBar { Maximum = 100, Height = 18, Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
        private readonly Label _device = Note("");
        private readonly Label _format = Note("");
        private readonly Label _result = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Font = new Font(Theme.Sans, 10.5f), ForeColor = Theme.Ink, BackColor = Color.White, Margin = new Padding(0, 6, 0, 4), UseMnemonic = false };
        private readonly Timer _timer = new Timer { Interval = 100 };
        private readonly List<byte> _audio = new List<byte>();
        private WaveInRecorder _rec;
        private DateTime _startedAt;
        private byte[] _last;
        private WaveOutPlayer _player;

        public event Action Starting;   // Settings pauses its own level meter
        public event Action Finished;

        internal PillButton TestButton => _test;
        internal PillButton PlayButton => _play;
        internal PillButton SaveButton => _save;
        internal ProgressBar Meter => _meter;
        internal Label DeviceLabel => _device;
        internal Label FormatLabel => _format;
        internal Label ResultLabel => _result;
        internal byte[] LastAudio => _last;
        internal MicCheck LastCheck { get; private set; }
        internal bool Busy => _rec != null;
        internal static string SavePath => Path.Combine(AppStorage.Folder, "mic-test.wav");

        public MicTestCard()
        {
            Card.Controls.Add(new Label { Text = "Test microphone", AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Theme.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) });
            Card.Controls.Add(Note("Say a sentence such as \"My name is Prince\" during the 3 seconds. Rapcha records it the same way voice typing does, " +
                                   "then plays back exactly what its offline speech hears (16 kHz mono). It should sound clear, not fast, slow or crackly."));
            Card.Controls.Add(_device);
            Card.Controls.Add(_format);
            Card.Controls.Add(_meter);
            Card.Controls.Add(_result);
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            foreach (var b in new[] { _test, _play, _save }) { b.Margin = new Padding(0, 0, 10, 0); row.Controls.Add(b); }
            Card.Controls.Add(row);
            Card.RowCount = Card.Controls.Count;
            for (int i = 0; i < Card.RowCount; i++) Card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _test.Click += (s, e) => Start();
            _play.Click += (s, e) => Play(false);
            _save.Click += (s, e) => Save();
            _timer.Tick += (s, e) => Tick();
            Card.Disposed += (s, e) => { _timer.Dispose(); StopNow(); try { _player?.Dispose(); } catch { } };
            ShowDevice(AudioEndpoints.Default(), null);
            _result.Text = "Not tested yet.";
        }

        private void ShowDevice(AudioEndpoints.Info info, WaveInRecorder opened)
        {
            string name = info.Found && info.Name.Length > 0 ? info.Name : opened?.DeviceName ?? "";
            if (name.Length == 0) name = info.Error.Length > 0 ? "none found (" + info.Error + ")" : "Windows default";
            var comms = info.Found && info.CommsName.Length > 0 && info.CommsName != info.Name ? "   (Windows' communications microphone is a different one: " + info.CommsName + ")" : "";
            _device.Text = "Microphone: " + name + comms;
            var native = info.NativeFormat.Length > 0 ? info.NativeFormat : "unknown";
            _format.Text = "Windows runs it at: " + native + (opened != null ? "   ·   Rapcha opened: " + Describe(opened) + "   ·   engines get: 16000 Hz, 1 channel, 16-bit" : "");
        }

        internal static string Describe(WaveInRecorder r) =>
            r.OpenedRate + " Hz, " + r.OpenedChannels + (r.OpenedChannels == 1 ? " channel, " : " channels, ") + (r.OpenedKind == SampleKind.Float32 ? "32-bit float" : r.OpenedKind == SampleKind.Int16 ? "16-bit" : r.OpenedKind.ToString());

        private WaveInRecorder NewRecorder() => new WaveInRecorder(_ => { }, level =>
        {
            try { Card.BeginInvoke(new Action(() => { if (!Card.IsDisposed) _meter.Value = Math.Max(0, Math.Min(100, (int)(level * 100))); })); } catch { }
        });

        private void Start()
        {
            if (_rec != null) return;
            Starting?.Invoke();
            lock (_audio) _audio.Clear();
            var rec = NewRecorder();
            rec.Tap = pcm => { lock (_audio) _audio.AddRange(pcm); };
            int device = WaveInRecorder.ResolveDevice(AppStorage.LoadMicDevice());
            var problem = rec.Start(device, out var detail);
            AppStorage.Log("Test microphone: device " + rec.FormatForLog + " result=" + problem + " attempts=[" + rec.OpenAttempts + "]");
            if (problem != MicProblem.None)
            {
                _result.Text = SettingsPage.MicHelp(problem);
                _result.ForeColor = Color.FromArgb(150, 30, 30);
                Finished?.Invoke();
                return;
            }
            _rec = rec;
            ShowDevice(AudioEndpoints.Default(), rec);
            _startedAt = DateTime.UtcNow;
            _test.Enabled = false; _play.Enabled = false; _save.Enabled = false;
            _result.ForeColor = Theme.Ink;
            _result.Text = "Recording… talk now (3 s)";
            _timer.Start();
        }

        private void Tick()
        {
            var left = RecordMs - (DateTime.UtcNow - _startedAt).TotalMilliseconds;
            if (_rec == null) { _timer.Stop(); return; }
            if (left > 0) { _result.Text = "Recording… talk now (" + Math.Ceiling(left / 1000) + " s)"; return; }
            _timer.Stop();
            var rec = _rec;
            _result.Text = "Checking…";
            Task.Run(() => rec.StopAndJoin(100)).ContinueWith(_ =>
            {
                try { Card.BeginInvoke(new Action(() => Done(rec, rec.FinalLostMs))); } catch { }
            });
        }

        private void Done(WaveInRecorder rec, double lostMs)
        {
            _rec = null;
            byte[] pcm;
            lock (_audio) pcm = _audio.ToArray();
            _last = pcm;
            var check = MicCheck.Of(WavFile.ToSamples(pcm), lostMs);
            LastCheck = check;
            _meter.Value = 0;
            _test.Enabled = true;
            _play.Enabled = pcm.Length > 0;
            _save.Enabled = pcm.Length > 0;
            _result.ForeColor = check.SpeechSeconds > 0 && !check.Clipped ? Color.FromArgb(30, 110, 40) : Theme.Ink;
            _result.Text = check.Verdict + "\n" + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Recorded {0:0.0} s · loudest {1:0} dB · background {2:0} dB · speech {3:0.0} s · missing audio {4:0} ms",
                check.Seconds, check.PeakDb, check.NoiseDb, check.SpeechSeconds, check.LostMs);
            AppStorage.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Test microphone: seconds={0:0.0} peak_db={1:0} noise_db={2:0} speech_s={3:0.0} lost_ms={4:0} clipped={5}",
                check.Seconds, check.PeakDb, check.NoiseDb, check.SpeechSeconds, check.LostMs, check.Clipped));
            Finished?.Invoke();
            if (pcm.Length > 0 && !TestMode) Play(false);
        }

        /// <summary>Self-test: no sound card on the runner, so playback is checked without a speaker.</summary>
        internal static bool TestMode;
        internal double LastPlaybackMs { get; private set; }

        private void Play(bool silent)
        {
            if (_last == null || _last.Length == 0) return;
            try { _player?.Dispose(); } catch { }
            _player = null;
            try
            {
                _player = new WaveOutPlayer(WavFile.Build(_last), silent || TestMode);
                LastPlaybackMs = _player.DurationMs;
                _player.Play();
            }
            catch (Exception e) { AppStorage.Log("Test microphone: playback failed " + e.GetType().Name); _result.Text += "\nCouldn't play it back (no speaker?)."; }
        }

        internal void PlayForTest() => Play(true);

        private void Save()
        {
            if (_last == null) return;
            try
            {
                File.WriteAllBytes(SavePath, WavFile.Build(_last));
                _result.Text += "\nSaved on this laptop only: " + SavePath;
                AppStorage.Log("Test microphone: saved a WAV in the Rapcha folder (" + _last.Length + " bytes)");
            }
            catch (Exception e) { _result.Text += "\nCouldn't save (" + e.GetType().Name + ")."; }
        }

        private void StopNow()
        {
            var r = _rec;
            _rec = null;
            if (r != null) Task.Run(() => r.Abort());
        }

        /// <summary>
        /// Self-test (CI has no microphone): the same conversion path fed with a driver-style 48 kHz stereo 32-bit
        /// float recording, in 50 ms buffers, then the same checks and display as a real test.
        /// </summary>
        internal void RunSimulated(byte[] native, int rate, int channels, SampleKind kind, string name)
        {
            lock (_audio) _audio.Clear();
            var rec = NewRecorder();
            rec.Tap = pcm => { lock (_audio) _audio.AddRange(pcm); };
            rec.StartSimulated(rate, channels, kind, name);
            int step = rate * channels * PcmResampler.BytesPerSample(kind) / 20;
            for (int off = 0; off < native.Length; off += step)
            {
                int n = Math.Min(step, native.Length - off);
                var b = new byte[n];
                Buffer.BlockCopy(native, off, b, 0, n);
                rec.PushNative(b, n);
            }
            rec.FinishNative();
            var info = new AudioEndpoints.Info
            {
                Found = true, Name = name, CommsName = name, Error = "",
                NativeFormat = rate + " Hz, " + channels + " channels, " + (kind == SampleKind.Float32 ? "32-bit float" : "16-bit integer") + " (simulated)"
            };
            ShowDevice(info, rec);
            Done(rec, 0);
            _meter.Value = 62;   // show the meter as it looks while someone talks
        }

        private static Label Note(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Theme.Muted, BackColor = Color.White, Margin = new Padding(0, 2, 0, 6), UseMnemonic = false
        };
    }
}
