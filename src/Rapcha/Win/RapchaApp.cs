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

        // Select to speak
        private readonly EdgeGlow _glow = new EdgeGlow();
        private readonly SelectionWatcher _watcher = new SelectionWatcher();
        private readonly ReadingCard _card = new ReadingCard();
        private IReadAloud _reader;
        private bool _piperBroken;
        private readonly bool _selfTestNoDownload;
        private readonly Timer _readerIdle = new Timer { Interval = 5 * 60 * 1000 }; // free piper after 5 idle minutes
        private bool _selectMode;
        private int _captureGeneration;
        private readonly string _selfTestSpeak;

        private readonly string _selfTestClick;

        public RapchaApp(string selfTestSpeak = null, string selfTestClick = null)
        {
            _selfTestSpeak = selfTestSpeak;
            _selfTestClick = selfTestClick;
            _selfTestNoDownload = selfTestSpeak != null || selfTestClick != null;
            _menu.Items.Add("Settings", null, (s, e) => OpenSettings(null));
            _menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray.Icon = Program.SmallIcon;
            _tray.Text = "Rapcha - click the ring to talk";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => OpenSettings(null);

            _ring.Clicked += OnRingClicked;
            _ring.RightClicked += OnRingRightClicked;
            _ring.DoubleClicked += () => SetSelectMode(!_selectMode);
            _watcher.DragSelection += OnDragSelection;
            _watcher.PointClicked += OnPointClicked;
            _readerIdle.Tick += (s, e) =>
            {
                if (_reader == null) { _readerIdle.Stop(); return; }
                if (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Preparing) return; // check again later
                _readerIdle.Stop();
                AppStorage.Log("Voice idle for 5 minutes: engine freed");
                _card.Close2();
                DisposeReader();
            };
            PiperVoice.StatusChanged += OnVoiceStatusChanged;
            PiperVoice.Refresh();
            _watcher.EscapePressed += () => { if (_selectMode && _phase == Phase.Idle) SetSelectMode(false); };
            _card.ButtonClicked += OnCardButton;
            _ring.Moved += () => { if (_label.Visible) _label.HideNow(); };
            _bubble.CompleteClicked += () => { if (_phase == Phase.Recording) _ = FinishAsync(); };
            _bubble.CancelClicked += () => { if (_phase == Phase.Recording) Cancel("Cancelled"); };
            _bubble.FixMicClicked += SettingsForm.OpenMicrophoneSettings;
            _hotkeys.HotkeyPressed += id => { if (id == EscHotkeyId) Cancel("Cancelled"); };
            _watch.Tick += OnWatchTick;

            _ring.ShowDocked();
            InstanceManager.Listen(
                onQuit: () => { try { _ring.BeginInvoke(new Action(() => { AppStorage.Log("Quit requested by a newer Rapcha"); ExitApp(); })); } catch { } },
                onShow: () => { try { _ring.BeginInvoke(new Action(() => { _ring.ShowDocked(); OpenSettings(null); })); } catch { } });
            AppStorage.Log("Rapcha " + typeof(RapchaApp).Assembly.GetName().Version + " started on " + Environment.OSVersion +
                           (Environment.Is64BitOperatingSystem ? " x64" : " x86") + ", mics=[" + string.Join("; ", WaveInRecorder.ListDevices()) + "]");

            if (_selfTestSpeak != null) { RunSpeakSelfTest(); return; }
            if (_selfTestClick != null) { RunClickSelfTest(); return; }
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

            StopReading(); // voice typing always wins over reading aloud
            _watcher.Stop(); // no input hooks while dictating and pasting (they must never be starved)
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
                ResumeSelectWatch();
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
            ResetToIdle(resumeWatch: false);

            if (text.Length == 0)
            {
                var reason = EmptyReason(recorder, session);
                AppStorage.Log("[s" + number + "] empty result shown as: " + reason);
                ResumeSelectWatch();
                _bubble.Set(WordsBubble.Mode.Warning, reason);
                await Task.Delay(reason == Messages.NothingHeard ? 2000 : 5000);
                if (gen == _generation && _phase == Phase.Idle) _bubble.Close2();
                return;
            }

            _bubble.Set(WordsBubble.Mode.Final, text);
            var result = TextDelivery.Deliver(text, _target);
            AppStorage.Log("[s" + number + "] delivered: " + result);
            ResumeSelectWatch();
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

        private void ResumeSelectWatch()
        {
            if (_selectMode && _phase == Phase.Idle) _watcher.Start();
        }

        private void ResetToIdle(bool resumeWatch = true)
        {
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _phase = Phase.Idle;
            _session = null;
            _recorder = null;
            _ring.State = RingState.Idle;
            _settings?.ResumeMeter();
            if (resumeWatch) ResumeSelectWatch();
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

        // ---------------- Select to speak ----------------

        private void SetSelectMode(bool on)
        {
            if (on == _selectMode) return;
            _selectMode = on;
            _ring.SelectMode = on;
            if (on)
            {
                _glow.Show();
                _watcher.Start();
                _ring.KeepOnTop();
                AppStorage.Log("Select to speak on: screens=" + Screen.AllScreens.Length + " glow_windows=" + _glow.WindowCount +
                               " voice=" + PiperVoice.Status + (PiperVoice.UnsupportedReason() != null ? " (" + PiperVoice.UnsupportedReason() + ")" : ""));
                string msg = "Select to speak is on. Click any text or icon to hear it (clicks only read while this is on). Double-click the ring or press Esc to stop.";
                if (!_selfTestNoDownload && !PiperVoice.IsInstalled && PiperVoice.UnsupportedReason() == null)
                {
                    _ = PiperVoice.EnsureInstalledAsync();
                    msg += " Downloading the natural voice, one time, " + PiperVoice.DownloadSizeText + ".";
                }
                ShowLabel(msg, 5000);
            }
            else
            {
                _captureGeneration++;
                _watcher.Stop();
                _glow.Hide();
                StopReading();
                DisposeReader(); // frees the voice engine (and piper's memory) while the mode is off
                AppStorage.Log("Select to speak off");
                ShowLabel("Select to speak is off", 1500);
            }
        }

        private void DisposeReader()
        {
            _readerIdle.Stop();
            _reader?.Dispose();
            _reader = null;
            _card.Attach(null);
        }

        /// <summary>Piper when it is installed (and not known broken this session), otherwise the Windows voice.</summary>
        private IReadAloud Reader()
        {
            bool wantPiper = PiperVoice.IsInstalled && !_piperBroken;
            if (_reader != null && (_reader is PiperReader) != wantPiper) DisposeReader();
            if (_reader == null)
            {
                if (wantPiper)
                {
                    var pr = new PiperReader(_selfTestNoDownload ? new ClockPlayer() : null);
                    pr.Failed += OnPiperFailed;
                    _reader = pr;
                }
                else _reader = new SpeechReader(_selfTestNoDownload);
                _card.Attach(_reader);
            }
            return _reader;
        }

        private void OnPiperFailed(string why, int atWord)
        {
            _piperBroken = true; // for this session; the next start tries Piper again
            var text = _reader?.Plan?.Text;
            DisposeReader();
            if (string.IsNullOrEmpty(text) || !_selectMode) return;
            _card.Note = "Natural voice stopped working. Using the Windows voice.";
            StartReading(text, atWord);
        }

        private string _lastReadText = "";

        private async void OnDragSelection(SelectionWatcher.Drag drag)
        {
            if (!_selectMode || _phase != Phase.Idle) return;
            int gen = ++_captureGeneration;
            await Task.Delay(120); // let the app finish updating its selection
            if (gen != _captureGeneration || !_selectMode || _phase != Phase.Idle) return;

            var fg = Native.GetForegroundWindow();
            if (!TextDelivery.IsUsableTarget(fg)) return; // our own windows, the desktop, the taskbar
            // A drag that started on a title bar, border, menu or scroll bar is not a selection.
            if (drag.DownWindow != IntPtr.Zero && Native.IsWindow(drag.DownWindow))
            {
                var pt = new Native.POINT(drag.Gesture.DownX, drag.Gesture.DownY);
                try { Native.PhysicalToLogicalPointForPerMonitorDPI(drag.DownWindow, ref pt); } catch { }
                var lp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
                if (Native.SendMessageTimeout(drag.DownWindow, Native.WM_NCHITTEST, IntPtr.Zero, lp, Native.SMTO_ABORTIFHUNG, 200, out var hit) != IntPtr.Zero)
                {
                    long h = hit.ToInt64();
                    bool frame = h == 2 || h == 3 || (h >= 5 && h <= 17) || h == 20 || h == 21; // caption, sysmenu, menu, scroll bars, buttons, borders, close, help
                    if (frame) { AppStorage.Log("Selection: ignored drag on window frame (hit=" + h + ")"); return; }
                }
            }

            string text;
            try { text = await SelectionReader.GetAsync(fg, allowClipboard: drag.TextCursor); }
            catch (Exception e) { AppStorage.Log("Selection failed: " + e.GetType().Name); return; }
            if (gen != _captureGeneration || !_selectMode || _phase != Phase.Idle) return;
            if (string.IsNullOrWhiteSpace(text)) return; // nothing selected: stay quiet
            bool stillReading = _reader != null && (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Preparing || _reader.Status == ReadState.Paused);
            if (text == _lastReadText && stillReading) return; // same selection already being read
            StartReading(text);
        }

        /// <summary>A swallowed click in select-to-speak mode: read whatever is under it.</summary>
        private async void OnPointClicked(int x, int y)
        {
            if (!_selectMode || _phase != Phase.Idle) return;
            int gen = ++_captureGeneration;
            var why = new System.Text.StringBuilder();
            Uia.PointRead r;
            try { r = await Uia.TextAtPointAsync(x, y, 1500, why); }
            catch (Exception e) { AppStorage.Log("Point read failed: " + e.GetType().Name); r = new Uia.PointRead(); }
            if (gen != _captureGeneration || !_selectMode || _phase != Phase.Idle || r.Own) return;
            LastPointRead = r;
            AppStorage.Log("Point: type=" + r.ControlType + " source=" + r.Source + " chars=" + r.Text.Length + " " + why.ToString().Trim());
            StartReading(r.Text.Length > 0 ? r.Text : PointText.NoText);
        }

        internal Uia.PointRead LastPointRead { get; private set; }

        private void StartReading(string text, int startWord = 0)
        {
            _lastReadText = text;
            _label.HideNow();
            _readerIdle.Stop();
            _readerIdle.Start();
            _card.Open(_ring.RingBounds, _ring.Edge, Dpi);
            try
            {
                var r = Reader();
                if (!(r is PiperReader)) _card.Note = FallbackNote();
                else if (_card.Note.StartsWith("Natural voice") || _card.Note.StartsWith("Downloading")) _card.Note = "";
                r.Read(text, startWord);
                AppStorage.Log("Reading: words=" + r.Plan.Words.Count + " phrases=" + r.Plan.Phrases.Count + " engine=" + (r is PiperReader ? "piper" : "sapi") + " voice=\"" + r.VoiceName + "\"");
            }
            catch (Exception e)
            {
                AppStorage.Log("Speech start failed: " + e.GetType().Name + " " + e.Message);
                bool wasPiper = _reader is PiperReader;
                DisposeReader();
                if (wasPiper) { OnPiperFailedAtStart(text, startWord); return; }
                _card.ShowStatus(SpeechReader.CountVoices() == 0 ? "No Windows voice is installed." : "No speaker found. Check your sound output.");
            }
        }

        private void OnPiperFailedAtStart(string text, int startWord)
        {
            _piperBroken = true;
            _card.Note = "Natural voice couldn't start. Using the Windows voice.";
            StartReading(text, startWord);
        }

        private string FallbackNote()
        {
            switch (PiperVoice.Status)
            {
                case PiperVoice.State.Downloading: return "Downloading voice, one time, " + PiperVoice.DownloadSizeText + " · " + PiperVoice.Percent + "% · Windows voice for now";
                case PiperVoice.State.Failed: return "No internet for the natural voice. Using the Windows voice.";
                case PiperVoice.State.Unsupported: return "Using the Windows voice (natural voice not supported here).";
                default: return _piperBroken ? "Using the Windows voice." : "";
            }
        }

        private void OnVoiceStatusChanged()
        {
            try
            {
                _ring.BeginInvoke(new Action(() =>
                {
                    if (_card.Visible && _reader != null && !(_reader is PiperReader)) _card.Note = FallbackNote();
                    _settings?.UpdateVoiceStatus();
                }));
            }
            catch { }
        }

        private void StopReading()
        {
            _reader?.Stop();
            _card.Close2();
        }

        private void OnCardButton(ReadingCard.Button b)
        {
            if (_reader == null) { if (b == ReadingCard.Button.Stop) _card.Close2(); return; }
            switch (b)
            {
                case ReadingCard.Button.PlayPause: _reader.TogglePause(); break;
                case ReadingCard.Button.Back: _reader.Skip(-ReadingPlan.SkipWords); break;
                case ReadingCard.Button.Forward: _reader.Skip(ReadingPlan.SkipWords); break;
                case ReadingCard.Button.Stop: StopReading(); break;
            }
        }

        /// <summary>
        /// CI hook (--selftest-speak "text"): toggles the mode on like a double-click, reads the text with whichever
        /// voice the app would choose (Piper if installed, else the Windows voice) into a silent clock instead of the
        /// speakers, checks word progress, toggles off, exits. Exit codes: 0 ok, 2 no voice at all, 1 failure.
        /// </summary>
        private void RunSpeakSelfTest()
        {
            int voices = SpeechReader.CountVoices();
            AppStorage.Log("Selftest speak: sapi_voices=" + voices + " piper_installed=" + PiperVoice.IsInstalled);
            if (voices == 0 && !PiperVoice.IsInstalled) { SelfTestExit(2); return; }
            var deadline = new Timer { Interval = 90000 };
            deadline.Tick += (s, e) => { deadline.Stop(); AppStorage.Log("Selftest speak: timed out"); SelfTestExit(1); };
            deadline.Start();
            var go = new Timer { Interval = 1500 };
            go.Tick += (s, e) =>
            {
                go.Stop();
                _ring.SimulateDoubleClick();
                if (!_selectMode || !_glow.Active || !_watcher.Running) { AppStorage.Log("Selftest speak: mode did not turn on"); SelfTestExit(1); return; }
                StartReading(_selfTestSpeak);
                var r = _reader;
                if (r == null) { SelfTestExit(1); return; }
                bool skipped = false;
                r.Changed += () =>
                {
                    // Exercise Forward 10 once mid-way on long texts.
                    if (!skipped && r.Plan.Words.Count > 25 && r.CurrentWord >= 3 && r.Status == ReadState.Playing) { skipped = true; r.Skip(ReadingPlan.SkipWords); AppStorage.Log("Selftest speak: forward 10 -> word " + r.CurrentWord); }
                    if (r.Status != ReadState.Finished) return;
                    deadline.Stop();
                    var pr = r as PiperReader;
                    bool ok = r.ProgressEvents > 0 && r.CurrentWord == r.Plan.Words.Count - 1 && (pr == null || pr.PcmBytes > 0);
                    AppStorage.Log("Selftest speak: finished engine=" + (pr != null ? "piper" : "sapi") + " words=" + r.Plan.Words.Count + " phrases=" + r.Plan.Phrases.Count +
                                   " progress_events=" + r.ProgressEvents + (pr != null ? " segments=" + pr.Segments + " pcm_bytes=" + pr.PcmBytes : "") +
                                   " voice=\"" + r.VoiceName + "\" ok=" + ok);
                    _ring.SimulateDoubleClick();
                    bool off = !_selectMode && !_glow.Active && !_watcher.Running;
                    AppStorage.Log("Selftest speak: mode off=" + off);
                    SelfTestExit(ok && off ? 0 : 1);
                };
            };
            go.Start();
        }

        /// <summary>
        /// CI hook (--selftest-click "expected words"): with Notepad open on a known file, turns the mode on, clicks
        /// (synthetic) on Notepad's first line and checks that the line was read while Notepad's text and caret
        /// did not change (the click was swallowed). Then minimizes all windows, clicks blank desktop space and
        /// expects "No text found at that location." Exit: 0 ok, 3 Notepad check failed, 4 desktop check failed, 1 other.
        /// </summary>
        private void RunClickSelfTest()
        {
            int stage = 0, notepadResult = -1;
            IntPtr edit = IntPtr.Zero;
            string textBefore = "";
            long selBefore = 0;
            DateTime stageAt = DateTime.UtcNow;
            var poll = new Timer { Interval = 200 };
            var deadline = new Timer { Interval = 60000 };
            deadline.Tick += (s, e) => { deadline.Stop(); poll.Stop(); AppStorage.Log("Selftest click: timed out at stage " + stage); SelfTestExit(stage <= 1 ? 3 : 4); };

            void Click(int x, int y) { LastPointRead = null; stageAt = DateTime.UtcNow; SelectionWatcher.SendTestClick(x, y); }
            bool ReadDone() => LastPointRead != null && _reader != null && _reader.Status == ReadState.Finished && !_card.Visible;

            poll.Tick += (s, e) =>
            {
                switch (stage)
                {
                    case 0:
                    {
                        _ring.SimulateDoubleClick();
                        SelectionWatcher.AcceptInjectedForTest = true;
                        IntPtr np = IntPtr.Zero;
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad")) if (p.MainWindowHandle != IntPtr.Zero) { np = p.MainWindowHandle; break; }
                        edit = np == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(np, IntPtr.Zero, "Edit", null);
                        if (edit == IntPtr.Zero) { AppStorage.Log("Selftest click: no classic Notepad Edit window"); poll.Stop(); SelfTestExit(3); return; }
                        Native.SetForegroundWindow(np);
                        textBefore = GetWindowText(edit);
                        Native.SendMessage(edit, Native.EM_SETSEL, (IntPtr)textBefore.Length, (IntPtr)textBefore.Length);
                        selBefore = Native.SendMessage(edit, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
                        Native.GetWindowRect(edit, out var r);
                        AppStorage.Log("Selftest click: mode=" + _selectMode + " notepad chars=" + textBefore.Length + " sel=" + selBefore + " clicking first line");
                        Click(r.Left + 40, r.Top + 12);
                        stage = 1;
                        break;
                    }
                    case 1:
                        if (!ReadDone()) return;
                    {
                        string read = _reader.Plan?.Text ?? "";
                        bool readOk = read.IndexOf(_selfTestClick, StringComparison.OrdinalIgnoreCase) >= 0;
                        string textAfter = GetWindowText(edit);
                        long selAfter = Native.SendMessage(edit, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
                        bool untouched = textAfter == textBefore && selAfter == selBefore;
                        AppStorage.Log("Selftest click: notepad read_ok=" + readOk + " read_chars=" + read.Length + " type=" + LastPointRead.ControlType +
                                       " source=" + LastPointRead.Source + " notepad_untouched=" + untouched + " sel_after=" + selAfter +
                                       " swallowed=" + _watcher.Swallowed + " card_closed=" + !_card.Visible);
                        notepadResult = readOk && untouched ? 0 : 3;
                        var tray = Native.FindWindow("Shell_TrayWnd", null);
                        if (tray != IntPtr.Zero) Native.PostMessage(tray, Native.WM_COMMAND, (IntPtr)419, IntPtr.Zero); // minimize all windows
                        stageAt = DateTime.UtcNow;
                        stage = 2;
                        break;
                    }
                    case 2:
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 1500) return;
                    {
                        var wa = Screen.PrimaryScreen.WorkingArea;
                        int x = wa.X + wa.Width / 2, y = wa.Bottom - 120;
                        AppStorage.Log("Selftest click: clicking blank desktop at " + x + "," + y);
                        Click(x, y);
                        stage = 3;
                        break;
                    }
                    case 3:
                        if (!ReadDone()) return;
                    {
                        string read = _reader.Plan?.Text ?? "";
                        bool ok = read == PointText.NoText;
                        AppStorage.Log("Selftest click: desktop no_text_ok=" + ok + " type=" + LastPointRead.ControlType + " source=" + LastPointRead.Source +
                                       " read_chars=" + read.Length + " card_closed=" + !_card.Visible);
                        poll.Stop(); deadline.Stop();
                        SelectionWatcher.AcceptInjectedForTest = false;
                        _ring.SimulateDoubleClick();
                        AppStorage.Log("Selftest click: mode off=" + (!_selectMode && !_watcher.Running));
                        SelfTestExit(notepadResult != 0 ? notepadResult : ok ? 0 : 4);
                        break;
                    }
                }
            };
            var go = new Timer { Interval = 1500 };
            go.Tick += (s, e) => { go.Stop(); deadline.Start(); poll.Start(); };
            go.Start();
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            int len = (int)Native.SendMessage(hwnd, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            var sb = new System.Text.StringBuilder(Math.Max(1, len + 1));
            Native.SendMessage(hwnd, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }

        private void SelfTestExit(int code)
        {
            Environment.ExitCode = code;
            var t = new Timer { Interval = 50 }; // never exit from inside the constructor or an event
            t.Tick += (s, e) => { t.Stop(); ExitApp(); };
            t.Start();
        }

        private void ExitApp()
        {
            _watcher.Dispose();
            _glow.Dispose();
            PiperVoice.StatusChanged -= OnVoiceStatusChanged;
            DisposeReader();
            _card.Close();
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
