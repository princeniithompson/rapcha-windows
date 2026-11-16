using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Owns the ring, the Words bubble, the label, the tray icon and one dictation at a time.</summary>
    public sealed class RapchaApp : ApplicationContext
    {
        private enum Phase { Idle, Recording, Finishing }

        private const int CompleteTailMs = 180;          // FloatingDictationSessionManager.COMPLETE_TAIL_MS
        private const int MaxRecordingSeconds = 195;     // DictationPiecePlanner.FORCE_AFTER_MS (one piece per session)
        private const int MicHintAfterMs = 2000;
        private const int MicHintPeak = 120;             // peak sample below this = "near zero"
        private const int EscHotkeyId = 0x5241;

        private readonly RingWindow _ring = new RingWindow();
        private readonly LabelWindow _label = new LabelWindow();
        private readonly WordsBubble _bubble = new WordsBubble();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly HotkeyWindow _hotkeys = new HotkeyWindow();
        private readonly Timer _watch = new Timer { Interval = 250 };

        private Phase _phase = Phase.Idle;
        private int _generation;
        private int _sessionNumber;
        private LiveSession _session;
        private WaveInRecorder _recorder;
        private IntPtr _target;
        private DateTime _startedAt;
        private bool _gotPreview;
        private bool _micHintShown;
        private SettingsForm _settings;

        public RapchaApp()
        {
            _menu.Items.Add("Settings", null, (s, e) => OpenSettings(null));
            _menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray.Icon = Program.SmallIcon;
            _tray.Text = "Rapcha - click the ring to talk";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => OpenSettings(null);

            _ring.Clicked += OnRingClicked;
            _ring.RightClicked += OnRingRightClicked;
            _ring.Moved += () => { if (_label.Visible) _label.HideNow(); };
            _bubble.CompleteClicked += () => { if (_phase == Phase.Recording) _ = FinishAsync(); };
            _bubble.CancelClicked += () => { if (_phase == Phase.Recording) Cancel("Cancelled"); };
            _bubble.FixMicClicked += SettingsForm.OpenMicrophoneSettings;
            _hotkeys.HotkeyPressed += id => { if (id == EscHotkeyId) Cancel("Cancelled"); };
            _watch.Tick += OnWatchTick;

            _ring.ShowDocked();
            AppStorage.Log("Rapcha " + typeof(RapchaApp).Assembly.GetName().Version + " started on " + Environment.OSVersion +
                           (Environment.Is64BitOperatingSystem ? " x64" : " x86") + ", mics=[" + string.Join("; ", WaveInRecorder.ListDevices()) + "]");

            if (LiveSession.IsPlaceholder(AppStorage.LoadApiKey()))
                OpenSettings("Welcome to Rapcha! Paste your Gemini API key, check that the microphone bar moves when you talk, then press Save.");
        }

        private int Dpi => (int)Math.Round(96.0 * _ring.RingSize / RingWindow.RingDip);

        private void OnRingClicked()
        {
            switch (_phase)
            {
                case Phase.Idle: StartRecording(); break;
                case Phase.Recording: _ = FinishAsync(); break;
            }
        }

        private void OnRingRightClicked(Point p)
        {
            if (_phase == Phase.Recording) { Cancel("Cancelled"); return; }
            if (_phase == Phase.Idle) _menu.Show(p);
        }

        private void StartRecording()
        {
            var key = AppStorage.LoadApiKey();
            if (LiveSession.IsPlaceholder(key))
            {
                ShowLabel(Messages.NoKey, 4000, true);
                OpenSettings(Messages.NoKey);
                return;
            }

            _settings?.PauseMeter();
            _target = TextDelivery.CaptureTarget();
            var vocab = AppStorage.LoadVocabulary();
            int gen = ++_generation;
            int number = ++_sessionNumber;
            var session = new LiveSession(key, vocab, levels: SilenceLevels.Windows);
            session.Log += line => AppStorage.Log("[s" + number + "] " + line);
            session.Failed += err =>
            {
                try { _ring.BeginInvoke(new Action(() => OnSessionFailed(gen, err))); } catch { }
            };
            session.Preview += words =>
            {
                try { _ring.BeginInvoke(new Action(() => { if (gen == _generation && _phase == Phase.Recording) { _gotPreview = true; _bubble.SetWords(words); } })); } catch { }
            };

            var recorder = new WaveInRecorder(
                chunk => session.SendAudio(chunk),
                level => _ring.Level = level);
            int device = WaveInRecorder.ResolveDevice(AppStorage.LoadMicDevice());
            var problem = recorder.Start(device, out var detail);
            AppStorage.Log("[s" + number + "] start: device=\"" + recorder.DeviceName + "\" format=" + recorder.FormatDescription +
                           " attempts=[" + recorder.OpenAttempts + "] result=" + problem + (detail.Length > 0 ? " " + detail : ""));
            if (problem != MicProblem.None)
            {
                session.Dispose();
                ShowLabel(SettingsForm.MicHelp(problem), 8000, true);
                _settings?.ResumeMeter();
                return;
            }

            _session = session;
            _recorder = recorder;
            _phase = Phase.Recording;
            _startedAt = DateTime.UtcNow;
            _gotPreview = false;
            _micHintShown = false;
            _label.HideNow();
            _ring.State = RingState.Recording;
            _bubble.Open(_ring.RingBounds, _ring.Edge, Dpi);
            _hotkeys.Register(EscHotkeyId, Native.VK_ESCAPE);
            _watch.Start();
            _ = session.ConnectAsync();
        }

        private void OnWatchTick(object sender, EventArgs e)
        {
            if (_phase != Phase.Recording) { _watch.Stop(); return; }
            var elapsed = DateTime.UtcNow - _startedAt;
            if (!_gotPreview && elapsed.TotalMilliseconds >= MicHintAfterMs && _recorder != null)
            {
                bool cantHear = _recorder.BytesCaptured == 0 || _recorder.LooksSilentlyBlocked || _recorder.RecentPeak < MicHintPeak;
                if (cantHear && !_micHintShown)
                {
                    _micHintShown = true;
                    AppStorage.Log("[s" + _sessionNumber + "] mic hint shown: bytes=" + _recorder.BytesCaptured + " recent_peak=" + _recorder.RecentPeak +
                                   " all_zero=" + _recorder.LooksSilentlyBlocked);
                }
                _bubble.MicHint = cantHear;
            }
            if (elapsed.TotalSeconds >= MaxRecordingSeconds) _ = FinishAsync();
        }

        private async Task FinishAsync()
        {
            if (_phase != Phase.Recording) return;
            _phase = Phase.Finishing;
            int gen = _generation;
            int number = _sessionNumber;
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _ring.State = RingState.Polishing;
            _bubble.Set(WordsBubble.Mode.Polishing, _gotPreview ? _session.Turn.LastInterimText : "");

            var recorder = _recorder;
            var session = _session;
            string text = "";
            try
            {
                await Task.Run(() => recorder.StopAndJoin(CompleteTailMs));
                text = await session.FinishAsync();
            }
            catch (Exception ex)
            {
                AppStorage.Log("[s" + number + "] finish failed: " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                session.Dispose();
            }
            text = (text ?? "").Trim();
            LogSessionSummary(number, recorder, session, text);
            if (gen != _generation || _phase != Phase.Finishing) return; // cancelled or failed meanwhile
            ResetToIdle();

            if (text.Length == 0)
            {
                var reason = EmptyReason(recorder, session);
                AppStorage.Log("[s" + number + "] empty result shown as: " + reason);
                _bubble.Set(WordsBubble.Mode.Warning, reason);
                await Task.Delay(reason == Messages.NothingHeard ? 2000 : 5000);
                if (gen == _generation && _phase == Phase.Idle) _bubble.Close2();
                return;
            }

            _bubble.Set(WordsBubble.Mode.Final, text);
            var result = TextDelivery.Deliver(text, _target);
            AppStorage.Log("[s" + number + "] delivered: " + result);
            await Task.Delay(900);
            if (gen != _generation) return;
            switch (result)
            {
                case TextDelivery.Result.Typed: _bubble.Set(WordsBubble.Mode.Done, "Typed ✓"); break;
                case TextDelivery.Result.Copied: _bubble.Set(WordsBubble.Mode.Done, "Copied ✓  Press Ctrl+V to paste"); break;
                default: _bubble.Set(WordsBubble.Mode.Warning, "Couldn't use the clipboard. Try again."); break;
            }
            await Task.Delay(result == TextDelivery.Result.Typed ? 1300 : 2000);
            if (gen == _generation && _phase == Phase.Idle) _bubble.Close2();
        }

        /// <summary>The real reason behind an empty result.</summary>
        private static string EmptyReason(WaveInRecorder recorder, LiveSession session)
        {
            var turn = session.Turn;
            if (recorder.BytesCaptured == 0 || session.FinishReason == "no_audio_sent") return Messages.NoSound;
            if (recorder.LooksSilentlyBlocked) return Messages.NoSound;
            if (session.FinishReason == "near_silent") return Messages.TooQuiet;
            if (session.FinishReason == "setup_not_complete") return Messages.GeminiSlow;
            bool hadSignal = !turn.IsNearSilent && turn.RmsSamples > 0;
            bool serverSaidAnything = turn.InterimFrames + turn.InputFrames + turn.ModelTextFrames + turn.OutputFrames > 0;
            if (session.FinishReason == "final_timeout" && !serverSaidAnything) return Messages.GeminiSlow;
            if (hadSignal && !serverSaidAnything) return Messages.GeminiNothing;
            return Messages.NothingHeard;
        }

        /// <summary>One line per session for rapcha.log. Counts and levels only, never words or the key.</summary>
        private static void LogSessionSummary(int number, WaveInRecorder r, LiveSession s, string text)
        {
            var t = s.Turn;
            AppStorage.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[s{0}] summary: device=\"{1}\" format={2} bytes_captured={3} buffers={4} empty_buffers={5} all_zero={6} " +
                "chunks_sent={7} rms_mean={8:0} rms_max={9:0} peak={10} max_gain={11:0.00} setup_ms={12} " +
                "server: interim={13} input={14} model_text={15} output={16} turn_complete={17} finish={18} timed_out={19} near_silent={20} " +
                "result_chars={21}",
                number, r.DeviceName, r.FormatDescription, r.BytesCaptured, r.BuffersReturned, r.EmptyBuffers, r.LooksSilentlyBlocked,
                s.ChunksSent, t.RmsMean, t.RmsMax, t.Peak, s.MaxGain, s.SetupMs,
                t.InterimFrames, t.InputFrames, t.ModelTextFrames, t.OutputFrames, t.TurnCompleteFrames, s.FinishReason, t.TimedOut, t.IsNearSilent,
                text.Length));
        }

        private void Cancel(string message)
        {
            if (_phase != Phase.Recording) return;
            _generation++;
            AppStorage.Log("[s" + _sessionNumber + "] cancelled");
            var recorder = _recorder;
            var session = _session;
            ResetToIdle();
            _bubble.Close2();
            Task.Run(() => { try { recorder?.Abort(); } catch { } try { session?.Dispose(); } catch { } });
            if (message != null) ShowLabel(message, 1200);
            else _label.HideNow();
        }

        private void OnSessionFailed(int gen, LiveError err)
        {
            if (gen != _generation || _phase == Phase.Idle) return;
            AppStorage.Log("[s" + _sessionNumber + "] session failed: " + err.Kind);
            _generation++;
            var recorder = _recorder;
            var session = _session;
            ResetToIdle();
            _bubble.Close2();
            Task.Run(() => { try { recorder?.Abort(); } catch { } try { session?.Dispose(); } catch { } });
            var msg = Messages.For(err);
            ShowLabel(msg, 6000, true);
            if (err.Kind == LiveErrorKind.BadKey || err.Kind == LiveErrorKind.MissingKey) OpenSettings(msg);
        }

        private void ResetToIdle()
        {
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _phase = Phase.Idle;
            _session = null;
            _recorder = null;
            _ring.State = RingState.Idle;
            _settings?.ResumeMeter();
        }

        private void ShowLabel(string text, int hideAfterMs, bool warning = false)
        {
            _label.ShowText(text, _ring.RingBounds, _ring.Edge, Dpi, hideAfterMs, warning);
        }

        private void OpenSettings(string message)
        {
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(message);
                _settings.FormClosed += (s, e) => _settings = null;
                if (_phase != Phase.Idle) _settings.PauseMeter();
                _settings.Show();
            }
            else if (message != null)
            {
                _settings.ShowMessage(message);
            }
            if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
        }

        private void ExitApp()
        {
            Cancel(null);
            _tray.Visible = false;
            _tray.Dispose();
            _hotkeys.Dispose();
            _settings?.Close();
            _bubble.Close();
            _label.Close();
            _ring.Close();
            ExitThread();
        }
    }

    /// <summary>Hidden message window for the Esc hotkey (only registered while recording).</summary>
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly System.Collections.Generic.HashSet<int> _registered = new System.Collections.Generic.HashSet<int>();
        public event Action<int> HotkeyPressed;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public void Register(int id, ushort vk)
        {
            if (_registered.Contains(id)) return;
            if (Native.RegisterHotKey(Handle, id, Native.MOD_NOREPEAT, vk)) _registered.Add(id);
        }

        public void Unregister(int id)
        {
            if (_registered.Remove(id)) Native.UnregisterHotKey(Handle, id);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) HotkeyPressed?.Invoke((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (var id in new System.Collections.Generic.List<int>(_registered)) Native.UnregisterHotKey(Handle, id);
            _registered.Clear();
            DestroyHandle();
        }
    }
}
