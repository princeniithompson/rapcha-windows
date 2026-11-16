using System;
using System.Drawing;
using System.Linq;
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
        // Offline voice typing (v16): the audio router, the offline engine when there is no internet, the limit.
        private DictationRouter _router;
        private IOfflineEngine _offline;
        private SpeechEngine _engine = SpeechEngine.Gemini;
        private int _maxSeconds = SessionCaps.GeminiSeconds;
        private DateTime _lastMemCheck;
        private string _offlinePreview = "";
        private System.Collections.Generic.List<string> _vocab = new System.Collections.Generic.List<string>();
        private IntPtr _target;
        private DateTime _startedAt;
        private bool _gotPreview;
        private bool _micHintShown;
        private MainWindow _main;

        // Select to speak
        private readonly EdgeGlow _glow = new EdgeGlow();
        private readonly string _selfTestOcr;
        private readonly bool _selfTestRing;

        // Voice typing: the text field the words will go into.
        private readonly FieldIndicator _field = new FieldIndicator();
        private readonly ShortcutManager _shortcuts;
        private readonly TypingWaker _typing;

        // Ring and Words bubble move together while the bubble is up.
        private Point _bubbleOffset;
        private bool _bubbleFollows;

        // Snooze (v17) and the Start menu / Windows Search (v17).
        private readonly ToolStripMenuItem _wakeItem = new ToolStripMenuItem("Wake Rapcha now") { Visible = false };
        private readonly ToolStripSeparator _wakeSep = new ToolStripSeparator { Visible = false };
        private readonly Timer _snoozeTimer = new Timer { Interval = 1000 };
        private bool _startInFront;
        private bool _movedAside;
        private Icon _listeningIcon;
        private const string TrayIdle = "Rapcha - click the ring to talk";

        /// <param name="showMain">A manual launch (double-click, shortcut): show the main window. Autostart stays quiet.</param>
        public RapchaApp(string selfTestOcr = null, bool selfTestRing = false, bool showMain = false, string selfTestV17 = null)
        {
            _selfTestOcr = selfTestOcr;
            _selfTestRing = selfTestRing;
            var open = new ToolStripMenuItem("Open Rapcha", null, (s, e) => OpenMain(MainWindow.Page.Home, null)) { Font = new Font(_menu.Font, FontStyle.Bold) };
            _wakeItem.Click += (s, e) => Snooze.Wake("tray: Wake Rapcha now");
            _menu.Items.Add(_wakeItem);
            _menu.Items.Add(_wakeSep);
            _menu.Items.Add(open);
            _menu.Opening += (s, e) => UpdateSnoozeUi();
            _menu.Items.Add("Settings", null, (s, e) => OpenSettings(null));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray.Icon = Program.SmallIcon;
            _tray.Text = TrayIdle;
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenMain(MainWindow.Page.Home, null); };

            ApplyGlowColor();
            AppStorage.GlowChanged += ApplyGlowColor;

            // The ring wakes when focus enters a real text field; the words box shows whether there is one.
            _field.EnableFocusEvents();
            _field.WantFocusChecks = () => _phase == Phase.Idle && _ring.Resting;
            _field.FocusEnteredField += () =>
            {
                if (!_ring.Resting) return;
                AppStorage.Log("Ring: woke (focus entered a text field)");
                _ring.Wake();
            };
            _field.Changed += info =>
            {
                _bubble.FieldValid = _field.HasField;
                AppStorage.Log("[s" + _sessionNumber + "] field: " + DescribeField(info) + " moves=" + _field.Moves);
            };
            // Typing into a text field wakes the ring and keeps it awake (only key times are seen, never keys).
            _typing = new TypingWaker(_ring, _field) { Allowed = () => _phase == Phase.Idle && _overlay == null && !_selfTestRing };
            _shortcuts = new ShortcutManager();
            _shortcuts.Triggered += OnShortcut;
            ApplyShortcuts();
            AppStorage.ShortcutsChanged += ApplyShortcuts;

            _ring.Clicked += OnRingClicked;
            _ring.RightClicked += OnRingRightClicked;
            _ring.DoubleClicked += ToggleOverlay;
            _ring.Moved += OnRingMoved;
            _ring.DragStarted += () =>
            {
                _bubbleFollows = _bubble.Visible;
                if (_bubbleFollows) _bubbleOffset = new Point(_bubble.Area.X - _ring.RingBounds.X, _bubble.Area.Y - _ring.RingBounds.Y);
            };
            _ring.DragEnded += () =>
            {
                _bubbleFollows = false;
                if (_bubble.Visible) _bubble.PlaceBeside(_ring.RingBounds, _ring.Edge);
            };
            _bubble.Dragged += (dx, dy) => _ring.MoveRingTo(_ring.RingBounds.X + dx, _ring.RingBounds.Y + dy);
            _bubble.DragEnded += () => _ring.DockNow();
            _ring.RestAllowed = () => _phase == Phase.Idle && _overlay == null && !_bubble.Visible && !_ring.IsDragging;
            // Hold-to-snooze: only while nothing is going on (not while voice typing or reading aloud).
            _ring.SnoozeAllowed = () => _phase == Phase.Idle && _overlay == null && !_bubble.Visible;
            _ring.SnoozeDropped += () => Snooze.Start(Snooze.Minutes, "dropped on the Snooze circle");
            Snooze.Changed += ApplySnooze;
            _snoozeTimer.Tick += (s, e) => OnSnoozeTick();
            _bubble.CompleteClicked += () => { if (_phase == Phase.Recording) _ = FinishAsync(); };
            _bubble.CancelClicked += () => { if (_phase == Phase.Recording) Cancel("Cancelled"); };
            _bubble.FixMicClicked += SettingsPage.OpenMicrophoneSettings;
            _hotkeys.HotkeyPressed += id => { if (id == EscHotkeyId) Cancel("Cancelled"); };
            _watch.Tick += OnWatchTick;

            _ring.ShowDocked();
            if (Snooze.Due || (Snooze.Active && (showMain || selfTestOcr != null || selfTestRing || selfTestV17 != null))) Snooze.Wake(Snooze.Due ? "time was up while Rapcha was closed" : "Rapcha was opened");
            else if (Snooze.Active) { AppStorage.Log("Snooze: still on after a restart (" + Snooze.LeftText + ")"); ApplySnooze(); }
            ShellWindows.Hook();
            ShellWindows.ForegroundChanged += OnForegroundChanged;
            InstanceManager.Listen(
                onQuit: () => { try { _ring.BeginInvoke(new Action(() => { AppStorage.Log("Quit requested by a newer Rapcha"); ExitApp(); })); } catch { } },
                onShow: () => { try { _ring.BeginInvoke(new Action(() => { AppStorage.Log("Show requested by a second launch: opening the main window"); Snooze.Wake("Rapcha was opened again"); _ring.ShowDocked(); OpenForLaunch(); })); } catch { } });
            AppStorage.Log("Rapcha " + typeof(RapchaApp).Assembly.GetName().Version + " started on " + Environment.OSVersion +
                           (Environment.Is64BitOperatingSystem ? " x64" : " x86") + ", mics=[" + string.Join("; ", WaveInRecorder.ListDevices()) + "]");

            Uia.WarmUp();
            Connectivity.Hook();
            Task.Run(() =>
            {
                var facts = DeviceProbe.Facts;
                AppStorage.Log("Device: " + facts + " tier=" + DeviceTiers.Name(DeviceTiers.Classify(facts)) + " s_mode=" + DeviceProbe.IsSMode() +
                               " offline_fallback=" + OfflineSettings.Fallback);
                BuiltInSpeech.Refresh();
                var model = WhisperModels.Choose(DeviceTiers.Classify(facts), OfflineSettings.ChosenModel);
                if (model != null && ModelStore.IsInstalled(model)) WhisperNative.EnsureLoaded();   // the engine only; the model loads while you talk
            });
            OcrService.WarmUp();                  // the first overlay's first look doesn't pay for starting the OCR engine
            _ = VoiceSettings.RemovePiperAsync(); // older versions downloaded a voice here; it is no longer used
            // Select to speak keeps one primed voice for the app's lifetime, so no selection waits for an engine to start.
            var prime = new Timer { Interval = 2500 };
            prime.Tick += (s, e) => { prime.Stop(); prime.Dispose(); Voices.PrimeShared(_selfTestOcr != null); };
            prime.Start();
            if (_selfTestOcr != null) { RunOcrSelfTest(); return; }
            if (_selfTestRing) { RunRingSelfTest(); return; }
            if (selfTestV17 != null) { new V17SelfTest(this, selfTestV17).Start(); return; }
            if (showMain || LiveSession.IsPlaceholder(AppStorage.LoadApiKey())) OpenForLaunch();
        }

        private const string Welcome = "Welcome to Rapcha! Paste your Gemini API key, check that the microphone bar moves when you talk, then press Save.";

        /// <summary>A manual launch: the main window, on Settings with a welcome note if there is no key yet.</summary>
        private void OpenForLaunch()
        {
            if (LiveSession.IsPlaceholder(AppStorage.LoadApiKey())) OpenMain(MainWindow.Page.Settings, Welcome);
            else OpenMain(MainWindow.Page.Home, null);
            AppStorage.Log("Main window shown (" + _main?.Current + ")");
        }

        private void ApplyGlowColor()
        {
            var c = GlowPicker.FromRgb(AppStorage.LoadGlowColor());
            _glow.Color = c;
            _field.Color = c;
            Accent.Set(c);   // ring, words box, field outline and screen glow share one colour
        }

        private void ApplyShortcuts() => _shortcuts.Apply(AppStorage.LoadShortcuts());

        private bool _holdSession;

        private void OnShortcut(ShortcutAction a, bool down)
        {
            // While snoozed the shortcuts still work: using one ends the snooze (the ring comes back) and does its job.
            if (down && Snooze.Active) Snooze.Wake("shortcut " + a);
            switch (a)
            {
                case ShortcutAction.TapToTalk:
                    if (!down) return;
                    if (_phase == Phase.Idle) StartRecording();
                    else if (_phase == Phase.Recording) _ = FinishAsync();
                    break;
                case ShortcutAction.HoldToTalk:
                    if (down)
                    {
                        if (_phase != Phase.Idle) return;
                        StartRecording();
                        _holdSession = _phase == Phase.Recording;
                    }
                    else
                    {
                        if (_holdSession && _phase == Phase.Recording) _ = FinishAsync();
                        _holdSession = false;
                    }
                    break;
                case ShortcutAction.SelectToSpeak:
                    if (down) ToggleOverlay();
                    break;
            }
        }

        private void OnRingMoved()
        {
            if (_label.Visible) _label.HideNow();
            if (_ring.IsDragging && _bubbleFollows && _bubble.Visible)
                _bubble.MoveTo(_ring.RingBounds.X + _bubbleOffset.X, _ring.RingBounds.Y + _bubbleOffset.Y);
        }

        private int Dpi => (int)Math.Round(96.0 * _ring.RingSize / RingWindow.RingDip);

        private int _ringClicks;

        private void OnRingClicked()
        {
            _ringClicks++;
            if (_selfTestRing) return; // the ring self-test only counts taps
            // Select to speak is open (previewing or reading), or just closed: a tap only stops reading.
            if (_phase == Phase.Idle && (_overlay != null || _ring.LastTapAt < _overlayClosedAt.AddMilliseconds(OverlayGraceMs)))
            {
                bool open = _overlay != null;
                CloseOverlay();
                AppStorage.Log("Ring tap during select to speak: " + (open ? "stopped reading" : "ignored (reading just ended)") + "; voice typing not started");
                return;
            }
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
            RecordingStarts++;
            var key = AppStorage.LoadApiKey();
            bool hasKey = !LiveSession.IsPlaceholder(key);
            bool online = Connectivity.LikelyOnline;
            var choice = online ? new EngineChoice { Ok = true, Engine = SpeechEngine.Gemini } : EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false));
            if (online && !hasKey)
            {
                ShowLabel(Messages.NoKey, 4000, true);
                OpenSettings(Messages.NoKey);
                return;
            }
            if (!choice.Ok)
            {
                // Offline with nothing to type with: say why (no internet and fallback off, or no Windows speech).
                var msg = choice.Why == NoEngineReason.NoRecognizer ? Messages.OfflineNoSpeech : Messages.OfflineOff;
                AppStorage.Log("Start: offline and no engine (" + choice.Why + ")");
                ShowLabel(msg, 6000, true);
                if (choice.Why == NoEngineReason.NoRecognizer) OpenSettings(msg);
                return;
            }

            IntPtr fromOverlay = CloseOverlay(); // voice typing always wins over reading aloud
            _main?.Settings.PauseMeter();
            _target = fromOverlay != IntPtr.Zero && TextDelivery.IsUsableTarget(fromOverlay) ? fromOverlay : TextDelivery.CaptureTarget();
            var vocab = AppStorage.LoadVocabulary();
            _vocab = vocab;
            int gen = ++_generation;
            int number = ++_sessionNumber;
            LiveSession session = null;
            IOfflineEngine offline = null;
            DictationRouter router;
            string note = "";
            if (choice.Engine == SpeechEngine.Gemini)
            {
                session = new LiveSession(key, vocab, levels: SilenceLevels.Windows);
                var s = session;
                session.Log += line => AppStorage.Log("[s" + number + "] " + line);
                session.Failed += err =>
                {
                    try { _ring.BeginInvoke(new Action(() => OnSessionFailed(gen, err))); } catch { }
                };
                session.Preview += words =>
                {
                    try { _ring.BeginInvoke(new Action(() => { if (gen == _generation && _phase == Phase.Recording && _session == s) { _gotPreview = true; _router?.OnOnlineWords(words); _bubble.SetWords(words); } })); } catch { }
                };
                session.GoAway += () =>
                {
                    try { _ring.BeginInvoke(new Action(() => { if (gen == _generation && _phase == Phase.Recording && _session == s) { AppStorage.Log("[s" + number + "] finishing early: Gemini goAway"); _ = FinishAsync(); } })); } catch { }
                };
                router = new DictationRouter(chunk => s.SendAudio(chunk));
            }
            else
            {
                offline = OfflineEngines.Start(choice, vocab, out note);
                if (offline == null)
                {
                    AppStorage.Log("[s" + number + "] offline engine could not start");
                    ShowLabel(Messages.OfflineNoSpeech, 6000, true);
                    OpenSettings(Messages.OfflineNoSpeech);
                    _main?.Settings.ResumeMeter();
                    return;
                }
                router = DictationRouter.Offline(offline.Feed);
                HookOffline(offline, gen);
            }

            var recorder = new WaveInRecorder(
                chunk => router.Feed(chunk),
                level => _ring.Level = level);
            int device = WaveInRecorder.ResolveDevice(AppStorage.LoadMicDevice());
            var problem = recorder.Start(device, out var detail);
            AppStorage.Log("[s" + number + "] start: device=\"" + recorder.DeviceName + "\" format=" + recorder.FormatDescription +
                           " attempts=[" + recorder.OpenAttempts + "] result=" + problem + (detail.Length > 0 ? " " + detail : ""));
            if (problem != MicProblem.None)
            {
                session?.Dispose();
                offline?.Dispose();
                ShowLabel(SettingsPage.MicHelp(problem), 8000, true);
                _main?.Settings.ResumeMeter();
                return;
            }

            _session = session;
            _offline = offline;
            _router = router;
            _engine = offline?.Kind ?? SpeechEngine.Gemini;
            _maxSeconds = SessionCaps.MaxSeconds(_engine, DeviceProbe.Tier);
            _offlinePreview = "";
            _recorder = recorder;
            _phase = Phase.Recording;
            _startedAt = DateTime.UtcNow;
            _gotPreview = false;
            _micHintShown = false;
            _label.HideNow();
            _ring.State = RingState.Recording;
            _bubble.Open(_ring.RingBounds, _ring.Edge, Dpi);
            _bubble.Offline = offline != null;
            _bubble.SetTimer(TimerText.For(0, _maxSeconds));
            if (ShellWindows.StartOrSearchInFront(out var flyout)) { AppStorage.Log("[s" + number + "] started over Start / Search"); SetStartInFront(true, flyout); }
            _field.Start(_target);           // follows focus every 300 ms (and on UIA focus events) until the words are in
            _bubble.FieldValid = null;
            _hotkeys.Register(EscHotkeyId, Native.VK_ESCAPE);
            _watch.Start();
            AppStorage.Log("[s" + number + "] engine=" + _engine + " tier=" + DeviceTiers.Name(DeviceProbe.Tier) + " max_s=" + _maxSeconds + (online ? "" : " (offline at start)"));
            if (note.Length > 0) ShowLabel(note, 6000, true);
            if (session != null)
            {
                _ = session.ConnectAsync();
                _ = ProbeWhileConnectingAsync(gen, session);
            }
        }

        /// <summary>Gets the offline engine's words into the Words box (after anything Gemini had already shown).</summary>
        private void HookOffline(IOfflineEngine engine, int gen)
        {
            engine.Preview += raw =>
            {
                try
                {
                    _ring.BeginInvoke(new Action(() =>
                    {
                        if (gen != _generation || _offline != engine) return;
                        _offlinePreview = EnginePolicy.Join(_router?.Captured, raw);
                        if (_phase == Phase.Recording) { _gotPreview = true; _bubble.SetWords(_offlinePreview); }
                    }));
                }
                catch { }
            };
        }

        /// <summary>A quick internet check while Gemini connects: no internet means switch to offline right away.</summary>
        private async Task ProbeWhileConnectingAsync(int gen, LiveSession session)
        {
            bool ok = await Connectivity.ProbeAsync();
            if (ok || gen != _generation || _phase != Phase.Recording || _session != session || session.IsSetupComplete) return;
            AppStorage.Log("[s" + _sessionNumber + "] internet check failed while connecting");
            if (!OfflineSettings.Fallback || !TrySwitchToOffline(gen, "no_internet")) OnSessionFailed(gen, new LiveError(LiveErrorKind.NoInternet, "probe"));
        }

        /// <summary>
        /// Gemini can't be reached (or the connection dropped part-way): keep recording with the offline engine and
        /// keep every word captured so far. False when offline typing isn't possible here.
        /// </summary>
        private bool TrySwitchToOffline(int gen, string why)
        {
            if (gen != _generation || _phase != Phase.Recording || _offline != null || _router == null) return false;
            Connectivity.MarkOffline();
            var choice = EnginePolicy.ChooseOffline(OfflineEngines.Inputs(false));
            if (!choice.Ok) { AppStorage.Log("[s" + _sessionNumber + "] offline switch not possible (" + choice.Why + ")"); return false; }
            var engine = OfflineEngines.Start(choice, _vocab, out var note);
            if (engine == null) { AppStorage.Log("[s" + _sessionNumber + "] offline switch: engine could not start"); return false; }
            var session = _session;
            string captured = session?.Turn.LastInterimText ?? "";
            HookOffline(engine, gen);
            _offline = engine;
            _router.SwitchToOffline(engine.Feed, captured);
            _engine = engine.Kind;
            _session = null;
            Task.Run(() => { try { session?.Dispose(); } catch { } });
            int elapsed = (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
            _maxSeconds = SessionCaps.AfterSwitch(elapsed, SessionCaps.MaxSeconds(_engine, DeviceProbe.Tier));
            _offlinePreview = _router.Captured;
            _bubble.Offline = true;
            if (_offlinePreview.Length > 0) _bubble.SetWords(_offlinePreview);
            AppStorage.Log("[s" + _sessionNumber + "] offline: switched to " + _engine + " reason=" + why + " captured_chars=" + _router.Captured.Length +
                           " at_s=" + elapsed + " max_s=" + _maxSeconds);
            if (note.Length > 0) ShowLabel(note, 6000, true);
            return true;
        }

        /// <summary>The field focused right now if it is a real text field; no field means clipboard only.</summary>
        private async Task<IntPtr> FinalTargetAsync(int number)
        {
            try
            {
                var waitAt = DateTime.UtcNow;
                while (_field.Busy && (DateTime.UtcNow - waitAt).TotalMilliseconds < 1500) await Task.Delay(40);
                await _field.CheckNowAsync();
            }
            catch (Exception e) { AppStorage.Log("[s" + number + "] final field check failed: " + e.GetType().Name); }
            var has = _field.HasField;
            var t = has == true ? _field.Target : has == false ? IntPtr.Zero : _target;
            AppStorage.Log("[s" + number + "] paste target: " + (has == true ? "field " + DescribeField(_field.Last ?? new FieldInfo()) : has == false ? "none (clipboard only)" : "unknown (window at start)") +
                           " checks=" + _field.Checks + " moves=" + _field.Moves);
            return t;
        }

        private string DescribeField(FieldInfo info) =>
            "type=" + info.Type + " editable=" + info.Editable + " rule=" + info.Reason + " rect=" + info.Bounds.Width + "x" + info.Bounds.Height +
            " outline=" + (_field.Active ? _field.Outline.Width + "x" + _field.Outline.Height : "none");

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
            _bubble.SetTimer(TimerText.For(elapsed.TotalSeconds, _maxSeconds));
            CheckStartMenu(false);   // backup for a missed foreground event
            long free = -1;
            if (_offline != null && (DateTime.UtcNow - _lastMemCheck).TotalMilliseconds >= 1000)
            {
                _lastMemCheck = DateTime.UtcNow;
                free = DeviceProbe.FreeRamMb();
            }
            if (SessionCaps.ShouldStop(elapsed.TotalSeconds, _maxSeconds, _engine, free, out var why))
            {
                AppStorage.Log("[s" + _sessionNumber + "] stopping: " + why + " at_s=" + (int)elapsed.TotalSeconds + (free >= 0 ? " free_mb=" + free : ""));
                _ = FinishAsync(why == "low_memory" ? Messages.LowMemory : Messages.TimeLimit);
            }
        }

        private async Task FinishAsync(string stopNote = null)
        {
            if (_phase != Phase.Recording) return;
            _phase = Phase.Finishing;
            int gen = _generation;
            int number = _sessionNumber;
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _ring.State = RingState.Polishing;
            var recorder = _recorder;
            var session = _session;
            var offline = _offline;
            var router = _router;
            var vocab = _vocab;
            _bubble.Set(WordsBubble.Mode.Polishing, offline != null ? _offlinePreview : _gotPreview && session != null ? session.Turn.LastInterimText : "");
            string text = "";
            try
            {
                await Task.Run(() => recorder.StopAndJoin(CompleteTailMs));
                if (offline != null)
                {
                    // Offline: the engine's words get the light clean-up (capitals, full stops, no um/uh, your vocabulary).
                    var raw = await offline.FinishAsync();
                    text = EnginePolicy.Join(router?.Captured, RulePolish.Clean(raw, vocab));
                }
                else text = await session.FinishAsync();
            }
            catch (Exception ex)
            {
                AppStorage.Log("[s" + number + "] finish failed: " + ex.GetType().Name);
            }
            finally
            {
                session?.Dispose();
                offline?.Dispose();   // Whisper frees its model here
            }
            text = (text ?? "").Trim();
            if (offline != null)
                AppStorage.Log("[s" + number + "] summary: offline " + offline.Summary + " captured_chars=" + (router?.Captured.Length ?? 0) +
                               " bytes_captured=" + recorder.BytesCaptured + " result_chars=" + text.Length);
            else LogSessionSummary(number, recorder, session, text);
            if (gen != _generation || _phase != Phase.Finishing) return; // cancelled or failed meanwhile
            var target = IntPtr.Zero;
            if (text.Length > 0)
            {
                target = await FinalTargetAsync(number);
                if (gen != _generation || _phase != Phase.Finishing) return;
            }
            ResetToIdle(keepField: text.Length > 0);

            if (text.Length == 0)
            {
                var reason = offline != null
                    ? (recorder.BytesCaptured == 0 || recorder.LooksSilentlyBlocked ? Messages.NoSound : Messages.NothingHeard)
                    : EmptyReason(recorder, session);
                AppStorage.Log("[s" + number + "] empty result shown as: " + reason);
                _bubble.Set(WordsBubble.Mode.Warning, reason);
                await Task.Delay(reason == Messages.NothingHeard ? 2000 : 5000);
                if (gen == _generation && _phase == Phase.Idle) _bubble.Close2();
                return;
            }

            _bubble.Set(WordsBubble.Mode.Final, text);
            _ring.State = RingState.Polishing; // keep spinning until the words are in
            TextDelivery.Result result;
            try { result = TextDelivery.Deliver(text, target); }
            finally
            {
                if (_phase == Phase.Idle) _ring.State = RingState.Idle;
                _field.Hide();
            }
            AppStorage.Log("[s" + number + "] delivered: " + result);
            if (result == TextDelivery.Result.Typed || result == TextDelivery.Result.Copied) AppStorage.AddToHistory(text); // Home page history; never logged
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
            if (stopNote != null && gen == _generation) ShowLabel(stopNote, 4000);
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
            var offline = _offline;
            ResetToIdle();
            _bubble.Close2();
            Task.Run(() => { try { recorder?.Abort(); } catch { } try { session?.Dispose(); } catch { } try { offline?.Dispose(); } catch { } });
            if (message != null) ShowLabel(message, 1200);
            else _label.HideNow();
        }

        private void OnSessionFailed(int gen, LiveError err)
        {
            if (gen != _generation || _phase == Phase.Idle) return;
            if (_offline != null) return;   // already switched to offline: Gemini's late errors don't matter
            AppStorage.Log("[s" + _sessionNumber + "] session failed: " + err.Kind);
            if (err.Kind == LiveErrorKind.NoInternet) Connectivity.MarkOffline();
            if (_phase == Phase.Recording && EnginePolicy.SwitchesOffline(err.Kind) && OfflineSettings.Fallback && TrySwitchToOffline(gen, err.Kind.ToString())) return;
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

        private void ResetToIdle(bool keepField = false)
        {
            if (!keepField) _field.Hide();
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _phase = Phase.Idle;
            _session = null;
            _recorder = null;
            _offline = null;
            _router = null;
            _ring.State = RingState.Idle;
            _main?.Settings.ResumeMeter();
            UpdateTrayForStart();
        }

        // ---------------- Snooze (v17) ----------------

        /// <summary>Snoozed: the ring (and anything beside it) hides; woken: it comes back where it was docked.</summary>
        private void ApplySnooze()
        {
            if (Snooze.Active)
            {
                _label.HideNow();
                _ring.Hide();
                _snoozeTimer.Start();
            }
            else
            {
                _snoozeTimer.Stop();
                if (!_ring.Visible) _ring.ShowDocked();
                _ring.Wake();
            }
            UpdateSnoozeUi();
        }

        private void OnSnoozeTick()
        {
            if (Snooze.Due) { Snooze.Wake("time is up"); return; }
            if (!Snooze.Active) { _snoozeTimer.Stop(); UpdateSnoozeUi(); return; }
            UpdateSnoozeUi();
        }

        private void UpdateSnoozeUi()
        {
            bool on = Snooze.Active;
            _wakeItem.Visible = _wakeSep.Visible = on;
            _wakeItem.Text = on ? "Wake Rapcha now (" + Snooze.LeftText + ")" : "Wake Rapcha now";
            if (on) SetTrayText("Rapcha is snoozed - " + Snooze.LeftText);
            else if (!(_phase == Phase.Recording && _startInFront)) SetTrayText(TrayIdle);
        }

        private void SetTrayText(string t)
        {
            if (t.Length > 63) t = t.Substring(0, 63);   // NotifyIcon.Text limit
            if (_tray.Text != t) _tray.Text = t;
        }

        internal ContextMenuStrip TrayMenu => _menu;
        internal ToolStripMenuItem WakeItem => _wakeItem;
        internal bool StartInFront => _startInFront;
        internal bool MovedAside => _movedAside;
        internal bool IsRecording => _phase != Phase.Idle;
        internal MainWindow Main => _main;
        internal void SimulateShortcut(ShortcutAction a, bool down) => OnShortcut(a, down);
        internal void CancelForTest() => Cancel(null);
        internal void ExitForTest() => ExitApp();
        internal string TrayText => _tray.Text;
        internal RingWindow Ring => _ring;
        internal bool TrayListening => _tray.Icon == _listeningIcon && _listeningIcon != null;

        // ---------------- Start menu / Windows Search (v17) ----------------

        private void OnForegroundChanged(IntPtr hwnd)
        {
            // A new foreground window: put Rapcha's windows back on top of normal always-on-top windows. (Start and
            // Search are in a higher Windows band; nothing a normal app does can put a window over them.)
            _ring.KeepOnTop();
            if (_bubble.Visible) _bubble.KeepOnTop();
            if (_label.Visible) _label.KeepOnTop();
            CheckStartMenu(true);
            if (_phase == Phase.Recording && _field.Tracking) _ = _field.CheckNowAsync();   // e.g. focus went into the Start search box
        }

        private void CheckStartMenu(bool fromEvent)
        {
            bool now = ShellWindows.StartOrSearchInFront(out var root);
            if (now != _startInFront) SetStartInFront(now, root);
        }

        private void SetStartInFront(bool open, IntPtr root)
        {
            _startInFront = open;
            if (open)
            {
                var r = ShellWindows.BoundsOf(root);
                AppStorage.Log("Start / Search in front: " + ShellTargets.ExeName(ShellWindows.ExeOf(root)) + " " + r.Width + "x" + r.Height + " phase=" + _phase);
                AvoidFlyout(r);
            }
            else
            {
                AppStorage.Log("Start / Search closed");
                if (_movedAside) { _movedAside = false; _ring.RestoreSavedDock(); if (_bubble.Visible) _bubble.PlaceBeside(_ring.RingBounds, _ring.Edge); }
            }
            UpdateTrayForStart();
        }

        /// <summary>
        /// Start or Search covers the ring or the words box: move them along the edge (or to another edge) so they
        /// stay visible beside it. Not saved; they go back when Start closes. Skipped when Start covers nearly all
        /// of the screen (nowhere to go) or while the ring is being dragged or snoozed.
        /// </summary>
        private void AvoidFlyout(Rectangle fly)
        {
            if (fly.IsEmpty || _ring.IsDragging || !_ring.Visible) return;
            var wa = Screen.FromRectangle(_ring.RingBounds).WorkingArea;
            var work = new IntRect(wa.X, wa.Y, wa.Width, wa.Height);
            Rectangle Used() => _bubble.Visible ? Rectangle.Union(_ring.RingBounds, _bubble.Area) : _ring.RingBounds;
            if (!Used().IntersectsWith(fly)) return;
            if (!ShellTargets.CanAvoid(work, new IntRect(fly.X, fly.Y, fly.Width, fly.Height))) { AppStorage.Log("Start / Search covers the screen: can't move out from under it"); return; }
            var start = _ring.DockSpot;
            var edges = new[] { start.Edge, Opposite(start.Edge), DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom }.Distinct();
            var fractions = new[] { start.Fraction, 0.15, 0.85, 0.5, 0.3, 0.7, 0.05, 0.95 };
            foreach (var edge in edges)
                foreach (var f in fractions)
                {
                    _ring.PlaceTemporarily(new DockPosition(edge, f));
                    if (_bubble.Visible) _bubble.PlaceBeside(_ring.RingBounds, _ring.Edge);
                    if (!Used().IntersectsWith(fly))
                    {
                        _movedAside = true;
                        AppStorage.Log("Start / Search: moved the ring" + (_bubble.Visible ? " and words box" : "") + " beside it (" + edge + " " + f.ToString("0.00") + ")");
                        return;
                    }
                }
            _ring.RestoreSavedDock();
            if (_bubble.Visible) _bubble.PlaceBeside(_ring.RingBounds, _ring.Edge);
            AppStorage.Log("Start / Search: no free spot beside it");
        }

        private static DockEdge Opposite(DockEdge e) => e == DockEdge.Left ? DockEdge.Right : e == DockEdge.Right ? DockEdge.Left : e == DockEdge.Top ? DockEdge.Bottom : DockEdge.Top;

        /// <summary>While voice typing with Start / Search in front, the tray icon shows a listening dot (the taskbar stays visible).</summary>
        private void UpdateTrayForStart()
        {
            bool listening = _phase == Phase.Recording && _startInFront;
            if (listening)
            {
                if (_listeningIcon == null) _listeningIcon = MakeListeningIcon();
                if (_listeningIcon != null && _tray.Icon != _listeningIcon) _tray.Icon = _listeningIcon;
                SetTrayText("Rapcha is listening - press your shortcut again to finish");
            }
            else
            {
                if (_tray.Icon != Program.SmallIcon) _tray.Icon = Program.SmallIcon;
                UpdateSnoozeUi();
            }
        }

        private static Icon MakeListeningIcon()
        {
            try
            {
                int size = SystemInformation.SmallIconSize.Width;
                using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        if (Program.SmallIcon != null) g.DrawIcon(Program.SmallIcon, new Rectangle(0, 0, size, size));
                        float d = size * 0.5f;
                        using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, size - d - 0.5f, size - d - 0.5f, d + 0.5f, d + 0.5f);
                        using (var dot = new SolidBrush(Color.FromArgb(220, 40, 40))) g.FillEllipse(dot, size - d + 1, size - d + 1, d - 2, d - 2);
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { return null; }
        }

        private void ShowLabel(string text, int hideAfterMs, bool warning = false)
        {
            _label.ShowText(text, _ring.RingBounds, _ring.Edge, Dpi, hideAfterMs, warning);
        }

        private void OpenSettings(string message) => OpenMain(MainWindow.Page.Settings, message);

        /// <summary>Opens (or brings to the front) the main window on a page.</summary>
        private void OpenMain(MainWindow.Page page, string message)
        {
            if (_main == null || _main.IsDisposed)
            {
                _main = new MainWindow(page, message);
                var w = _main;
                w.FormClosed += (s, e) => { if (_main == w) _main = null; };   // a closed modeless form is disposed; the ring keeps running
                if (_phase != Phase.Idle) w.Settings.PauseMeter();
                w.Show();
            }
            else
            {
                _main.ShowPage(page);
                if (message != null) _main.ShowMessage(message);
            }
            _main.BringUp();
        }

        // ---------------- Select to speak (screen overlay) ----------------

        private ScreenOverlay _overlay;
        private const int OverlayGraceMs = 400;
        private DateTime _overlayClosedAt = DateTime.MinValue.AddDays(1);
        internal int RecordingStarts { get; private set; }
        private IntPtr _overlayPrevious;
        private int _overlayGeneration;

        private void ToggleOverlay()
        {
            if (_overlay != null) { CloseOverlay(); return; }
            if (_phase != Phase.Idle) { AppStorage.Log("Overlay not opened: dictation is running"); return; }
            OpenOverlay();
        }

        private void OpenOverlay()
        {
            var screen = Screen.FromRectangle(_ring.RingBounds);
            var bounds = screen.Bounds;
            int dpi = Native.DpiAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            _label.HideNow();
            _overlayPrevious = Native.GetForegroundWindow();
            var started = DateTime.UtcNow;
            Bitmap shot;
            try
            {
                shot = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot))
                {
                    // BitBlt with CAPTUREBLT so layered windows are included (Graphics.CopyFromScreen rejects that flag).
                    IntPtr dst = g.GetHdc(), src = Native.GetDC(IntPtr.Zero);
                    try
                    {
                        if (!Native.BitBlt(dst, 0, 0, bounds.Width, bounds.Height, src, bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                            throw new System.ComponentModel.Win32Exception();
                    }
                    finally { Native.ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
                }
            }
            catch (Exception e)
            {
                AppStorage.Log("Screen capture failed: " + e.GetType().Name + " " + e.Message + " bounds=" + bounds);
                ShowLabel("Couldn't capture the screen. Try again.", 3000, true);
                return;
            }

            int gen = ++_overlayGeneration;
            var overlay = new ScreenOverlay(bounds, shot, dpi, _selfTestOcr != null);
            _overlay = overlay;
            overlay.FormClosed += (s, e) => OnOverlayClosed(overlay);
            overlay.Show();
            overlay.Activate();
            _glow.Show(bounds, overlay);
            _ring.SelectMode = true;
            _ring.KeepOnTop();
            AppStorage.Log("Overlay open: " + bounds.Width + "x" + bounds.Height + " dpi=" + dpi + " screens=" + Screen.AllScreens.Length +
                           " glow_windows=" + _glow.WindowCount + " capture_ms=" + (int)(DateTime.UtcNow - started).TotalMilliseconds);

            // The index starts now (fast first look, then the rest in the background) while the user positions.
            try { overlay.StartIndex(); }   // the overlay keeps its own copy of the pixels
            finally { shot.Dispose(); }
            _ring.KeepOnTop();
        }

        /// <summary>Closes the overlay (if open) and gives focus back. Returns the window that had focus before it.</summary>
        private IntPtr CloseOverlay()
        {
            if (_overlay == null) return IntPtr.Zero;
            var prev = _overlayPrevious;
            _overlay.Close(); // OnOverlayClosed does the cleanup
            return prev;
        }

        private void OnOverlayClosed(ScreenOverlay overlay)
        {
            if (_overlay != overlay) return;
            _overlay = null;
            _overlayClosedAt = DateTime.UtcNow;
            _overlayGeneration++;
            _glow.Hide();
            _ring.SelectMode = false;
            var prev = _overlayPrevious;
            _overlayPrevious = IntPtr.Zero;
            if (prev != IntPtr.Zero && Native.IsWindow(prev)) Native.SetForegroundWindow(prev);
            overlay.Dispose();
            // The screenshot copies and OCR results are large: give the memory back now (4 GB laptops).
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            AppStorage.Log("Overlay closed: managed_mb=" + (GC.GetTotalMemory(false) / (1024 * 1024)));
            _ring.KeepOnTop();
        }

        /// <summary>
        /// CI hook (--selftest-ocr "quick brown fox"): with Notepad showing a known first line (and a second line
        /// that must not be read), opens the overlay like a double-click on the ring, waits for OCR, checks the
        /// words were found, then selects the first line with a drag, with two corner clicks, and with a tap on a
        /// word, each read with the Windows voice into a silent stream. Finally presses Esc and checks the
        /// overlay and glow are gone. Exit: 0 ok, 2 no voice, 3 OCR/words missing, 4 selection/reading wrong,
        /// 5 close failed, 6 OCR unavailable, 7 two-column page read out of order, 8 a ring tap while reading started voice typing, 1 other/timeout.
        /// </summary>
        private void RunOcrSelfTest()
        {
            var expected = _selfTestOcr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int voices = SpeechReader.CountVoices();
            AppStorage.Log("Selftest ocr: sapi_voices=" + voices);
            if (voices == 0) { SelfTestExit(2); return; }
            int stage = 0, closeWait = 0, startsBefore = 0;
            bool tapFromStart = false;
            Rectangle area0 = Rectangle.Empty;
            Box columnsBox = new Box();
            DateTime stageAt = DateTime.UtcNow;
            TextLine line = null;
            ColumnsTestForm columns = null;
            var poll = new Timer { Interval = 150 };
            var deadline = new Timer { Interval = 120000 };
            deadline.Tick += (s, e) => { deadline.Stop(); poll.Stop(); AppStorage.Log("Selftest ocr: timed out at stage " + stage); SelfTestExit(1); };
            void Fail(int code, string why) { poll.Stop(); deadline.Stop(); AppStorage.Log("Selftest ocr: FAIL " + why); OcrService.DebugDump(_overlay?.Ocr, "selftest-ocr-fail"); SelfTestExit(code); }
            bool Done() => !_overlay.Previewing && _overlay.Reader != null && _overlay.Reader.Status == ReadState.Finished;
            bool Check(string how)
            {
                var text = _overlay.LastReadText;
                bool all = expected.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                // v14: a tap reads the tapped paragraph from its start (both Notepad lines are one paragraph);
                // a box reads only the lines it touches, so the second line must not be read then.
                bool tap = how == "tap";
                bool second = !tap && text.IndexOf("second", StringComparison.OrdinalIgnoreCase) >= 0;
                if (tap && !tapFromStart) second = true;   // a tap reads its paragraph from the paragraph's first word
                var r = _overlay.Reader;
                AppStorage.Log("Selftest ocr: " + how + " expected_found=" + all + " second_line_read=" + second + " words=" + r.Plan.Words.Count +
                               " progress_events=" + r.ProgressEvents + " last_word=" + r.CurrentWord + " highlight_moves=" + _overlay.HighlightMoves + " voice=\"" + r.VoiceName + "\"");
                return all && !second && r.ProgressEvents > 0 && r.CurrentWord == r.Plan.Words.Count - 1 && _overlay.HighlightMoves > 0;
            }

            poll.Tick += (s, e) =>
            {
                switch (stage)
                {
                    case 0:
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad"))
                            if (p.MainWindowHandle != IntPtr.Zero) { Native.SetForegroundWindow(p.MainWindowHandle); break; }
                        _ring.SimulateDoubleClick();
                        if (_overlay == null || !_glow.Active) { Fail(1, "overlay did not open"); return; }
                        stage = 1;
                        break;
                    case 1:
                    {
                        var r = _overlay.Ocr;
                        if (r == null) return;
                        AppStorage.Log("Selftest ocr: ocr_ms=" + r.Ms + " lang=" + r.Language + " scale=" + r.Scale + " lines=" + r.Layout.Lines.Count + " words=" + r.Layout.WordCount +
                                       (r.Problem.Length > 0 ? " problem=yes" : ""));
                        if (r.Problem == OcrService.NoLanguage) { Fail(6, "no OCR language"); return; }
                        line = r.Page.Lines.FirstOrDefault(l => l.Words.Any(w => string.Equals(w.Text.Trim('.', ','), expected[0], StringComparison.OrdinalIgnoreCase)));
                        bool found = line != null && expected.All(x => line.Words.Any(w => string.Equals(w.Text.Trim('.', ','), x, StringComparison.OrdinalIgnoreCase)));
                        AppStorage.Log("Selftest ocr: expected words on one line=" + found + (line != null ? " line_box=" + line.Box : ""));
                        if (!found) { Fail(3, "expected words not recognised"); return; }
                        _overlay.SimulateDrag(line.Box.Inflate(4));
                        stage = 2;
                        break;
                    }
                    case 2:
                        if (!Done()) return;
                        if (!Check("drag")) { Fail(4, "drag"); return; }
                        _overlay.SimulateTwoClicks(line.Box.Inflate(4));
                        stage = 3;
                        break;
                    case 3:
                        if (!Done()) return;
                        if (!Check("two_clicks")) { Fail(4, "two clicks"); return; }
                    {
                        var w = line.Words[Math.Min(1, line.Words.Count - 1)].Box;
                        var para = _overlay.Ocr.Page.ParagraphAt(w.CenterX, w.CenterY, 24);
                        _overlay.SimulateTap(w.CenterX, w.CenterY);
                        var first = _overlay.LastReadWords.FirstOrDefault();
                        tapFromStart = para != null && first != null && para.Words.Count > 0 &&
                                       Math.Abs(first.Box.X - para.Words[0].Box.X) < 2 && Math.Abs(first.Box.Y - para.Words[0].Box.Y) < 2;
                        AppStorage.Log("Selftest ocr: tap paragraph lines=" + (para?.Words.Select(x => x.Line).Distinct().Count() ?? 0) + " from_start=" + tapFromStart);
                    }
                        stage = 4;
                        break;
                    case 4:
                        if (!Done()) return;
                        if (!Check("tap")) { Fail(4, "tap"); return; }
                        // Empty space far from any text: "No text found there."
                    {
                        // Find a spot with no recognised text at all.
                        var layout = _overlay.Ocr.Page;
                        Box empty = new Box();
                        for (int y = 60; y + 40 < _overlay.ClientSize.Height - 140 && empty.IsEmpty; y += 20)
                            for (int x = 40; x + 160 < _overlay.ClientSize.Width - 40 && empty.IsEmpty; x += 20)
                            {
                                var b = new Box(x, y, 160, 40);
                                if (layout.Lines.All(l => l.Box.Inflate(6).Intersect(b).IsEmpty)) empty = b;
                            }
                        AppStorage.Log("Selftest ocr: empty spot=" + (empty.IsEmpty ? "none" : empty.ToString()));
                        if (empty.IsEmpty) { Fail(4, "no empty spot on screen"); return; }
                        _overlay.SimulateDrag(empty);
                    }
                        stage = 5;
                        break;
                    case 5:
                        if (!Done()) return;
                    {
                        bool none = _overlay.LastReadText == ScreenOverlay.NoTextThere;
                        AppStorage.Log("Selftest ocr: empty box no_text=" + none);
                        if (!none) { Fail(4, "empty box"); return; }
                        var ov = _overlay;
                        Native.PostMessage(ov.Handle, Native.WM_KEYDOWN, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                        stage = 6;
                        break;
                    }
                    case 6:
                    {
                        if (_overlay != null || (_glow.Active && closeWait++ < 12)) return; // the glow fades out (~0.2 s)
                        bool closed = !_glow.Active && Application.OpenForms.OfType<ScreenOverlay>().Count() == 0;
                        bool piperGone = !System.IO.Directory.Exists(VoiceSettings.PiperFolder);
                        AppStorage.Log("Selftest ocr: closed=" + closed + " piper_folder_gone=" + piperGone);
                        if (!closed || !piperGone) { Fail(5, "close/cleanup"); return; }
                        // Part 2: a two-column page (header, paragraph, list on the left; cards level with it on the right).
                        columns = new ColumnsTestForm();
                        columns.Show();
                        columns.Activate();
                        stageAt = DateTime.UtcNow;
                        stage = 7;
                        break;
                    }
                    case 7:
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 1200) return;
                        _ring.SimulateDoubleClick();
                        if (_overlay == null) { Fail(1, "overlay did not open again"); return; }
                        stage = 8;
                        break;
                    case 8:
                    {
                        var r = _overlay.Ocr;
                        if (r == null) return;
                        var area = columns.RectangleToScreen(columns.ClientRectangle);
                        var origin = _overlay.Bounds.Location;
                        area0 = new Rectangle(area.X - origin.X + 4, area.Y - origin.Y + 4, area.Width - 8, area.Height - 8);
                        AppStorage.Log("Selftest ocr: columns page lines=" + r.Page.Lines.Count + " blocks=" + r.Page.Blocks.Count);
                        _overlay.SimulateDrag(new Box(area.X - origin.X + 4, area.Y - origin.Y + 4, area.Width - 8, area.Height - 8));
                        stage = 9;
                        break;
                    }
                    case 9:
                    {
                        if (!Done()) return;
                        var text = _overlay.LastReadText;
                        var order = ColumnsTestForm.OrderTokens.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).ToArray();
                        bool all = order.All(i => i >= 0);
                        bool inOrder = all && order.Zip(order.Skip(1), (a, b) => a < b).All(x => x);
                        AppStorage.Log("Selftest ocr: columns tokens_found=" + order.Count(i => i >= 0) + "/" + order.Length + " in_book_order=" + inOrder +
                                       " preview_blocks=" + _overlay.PreviewParts + " progress_events=" + _overlay.Reader.ProgressEvents);
                        if (!inOrder)
                        {
                            AppStorage.Log("Selftest ocr: columns token order=" + string.Join(",", order));
                            Fail(7, "column order"); return;
                        }
                        // Part 3: a ring tap while reading stops reading and never starts voice typing.
                        columnsBox = new Box(area0.X, area0.Y, area0.Width, area0.Height);
                        // v14 reads at once and the self-test voice speaks into memory far faster than real time, so the
                        // ring is clicked in the same tick the reading starts (the click lands while it is still playing).
                        _overlay.SimulateDrag(columnsBox);
                        if (_overlay.Reader == null || _overlay.Reader.Status != ReadState.Playing) { Fail(8, "reading did not start again"); return; }
                        startsBefore = RecordingStarts;
                        SendClick(Center(_ring.VisibleRingBounds));
                        AppStorage.Log("Selftest ocr: ring clicked while reading");
                        stageAt = DateTime.UtcNow;
                        stage = 12;
                        break;
                    }
                    case 11:
                        if (!_overlay.Previewing && (_overlay.Reader == null || _overlay.Reader.Status != ReadState.Playing))
                        {
                            if ((DateTime.UtcNow - stageAt).TotalMilliseconds > 8000) { Fail(8, "reading did not start again"); return; }
                            return;
                        }
                        startsBefore = RecordingStarts;
                        SendClick(Center(_ring.VisibleRingBounds));
                        AppStorage.Log("Selftest ocr: ring clicked while " + (_overlay.Previewing ? "previewing" : "reading"));
                        stageAt = DateTime.UtcNow;
                        stage = 12;
                        break;
                    case 12:
                    {
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 1500) return;
                        bool ok = _overlay == null && _phase == Phase.Idle && RecordingStarts == startsBefore;
                        AppStorage.Log("Selftest ocr: single tap while reading: overlay_closed=" + (_overlay == null) + " phase=" + _phase + " voice_typing_started=" + (RecordingStarts != startsBefore));
                        if (!ok) { Fail(8, "a ring tap while reading started voice typing or did not stop reading"); return; }
                        _ring.SimulateDoubleClick();
                        if (_overlay == null) { Fail(1, "overlay did not open (part 3)"); return; }
                        stageAt = DateTime.UtcNow;
                        stage = 13;
                        break;
                    }
                    case 13:
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 1000) return;
                        SendClick(Center(_ring.VisibleRingBounds));   // a real double-click on the ring while select to speak is open
                        stageAt = DateTime.UtcNow;
                        stage = 14;
                        break;
                    case 14:
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 140) return;
                        SendClick(Center(_ring.VisibleRingBounds));
                        stageAt = DateTime.UtcNow;
                        stage = 15;
                        break;
                    case 15:
                    {
                        if ((DateTime.UtcNow - stageAt).TotalMilliseconds < 1500) return;
                        bool ok = _overlay == null && _phase == Phase.Idle && RecordingStarts == startsBefore;
                        AppStorage.Log("Selftest ocr: double tap while open: overlay_closed=" + (_overlay == null) + " reopened=" + (_overlay != null) + " phase=" + _phase +
                                       " voice_typing_started=" + (RecordingStarts != startsBefore));
                        if (!ok) { Fail(8, "a double tap while select to speak was open leaked into voice typing or reopened it"); return; }
                        stage = 10;
                        break;
                    }
                    case 10:
                        if (_overlay != null) return;
                        columns.Close();
                        poll.Stop(); deadline.Stop();
                        AppStorage.Log("Selftest ocr: ok=True");
                        SelfTestExit(0);
                        break;
                }
            };
            var go = new Timer { Interval = 2000 };
            go.Tick += (s, e) => { go.Stop(); deadline.Start(); poll.Start(); };
            go.Start();
        }

        /// <summary>
        /// CI hook (--selftest-ring): the ring's hole takes clicks, resting after the idle time (shortened to 1.5 s),
        /// the first real click on a resting ring only wakes it, resting again, the spinner state, the ring and
        /// Words bubble moving together (both ways) and docking to a side edge, the text field outline on an open
        /// Notepad, and the "No text box" note on a window with no text field.
        /// Exit: 0 ok, 11 hole not clickable, 12 resting, 13 wake, 14 spinner, 15 move together, 16 field, 17 no-field note, 1 timeout.
        /// </summary>
        private void RunRingSelfTest()
        {
            int stage = 0;
            DateTime at = DateTime.UtcNow;
            Point offset = Point.Empty;
            IntPtr notepad = IntPtr.Zero;
            var poll = new Timer { Interval = 100 };
            var deadline = new Timer { Interval = 90000 };
            deadline.Tick += (s, e) => { deadline.Stop(); poll.Stop(); AppStorage.Log("Selftest ring: timed out at stage " + stage); SelfTestExit(1); };
            void Fail(int code, string why) { poll.Stop(); deadline.Stop(); AppStorage.Log("Selftest ring: FAIL " + why); SelfTestExit(code); }
            void Next(int st) { stage = st; at = DateTime.UtcNow; }
            double Since() => (DateTime.UtcNow - at).TotalMilliseconds;
            bool HitsRing(Point p)
            {
                var h = Native.WindowFromPoint(new Native.POINT(p.X, p.Y));
                return h == _ring.Handle || Native.GetAncestor(h, Native.GA_ROOT) == _ring.Handle;
            }
            Point Center(Rectangle r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            bool Docked()
            {
                var r = _ring.RingBounds; var wa = Screen.FromRectangle(r).WorkingArea;
                switch (_ring.Edge)
                {
                    case DockEdge.Left: return r.X - wa.X < r.Width;
                    case DockEdge.Right: return wa.Right - r.Right < r.Width;
                    case DockEdge.Top: return r.Y - wa.Y < r.Height;
                    default: return wa.Bottom - r.Bottom < r.Height;
                }
            }
            bool BesideRing()
            {
                var b = _bubble.Area; var r = _ring.RingBounds; var wa = Screen.FromRectangle(r).WorkingArea;
                bool onScreen = wa.Contains(b);
                bool beside = (_ring.Edge == DockEdge.Left || _ring.Edge == DockEdge.Right)
                    ? (b.X >= r.Right || b.Right <= r.X) && b.Y < r.Bottom && b.Bottom > r.Y
                    : (b.Y >= r.Bottom || b.Bottom <= r.Y) && b.X < r.Right && b.Right > r.X;
                return onScreen && beside;
            }

            poll.Tick += async (s, e) =>
            {
                switch (stage)
                {
                    case 0:
                    {
                        _ring.RestDelayMs = 1500;
                        var c = Center(_ring.RingBounds);
                        bool hole = HitsRing(c);
                        AppStorage.Log("Selftest ring: hole_hits_ring=" + hole + " ring=" + _ring.RingBounds);
                        if (!hole) { Fail(11, "hole not clickable"); return; }
                        Next(1);
                        break;
                    }
                    case 1:
                        if (Since() > 6000) { Fail(12, "did not rest"); return; }
                        if (!_ring.Resting || _ring.RestAmount < 1) return;
                    {
                        var v = _ring.VisibleRingBounds;
                        bool small = v.Width <= _ring.RingSize * 0.65;
                        bool hole = HitsRing(Center(v));
                        AppStorage.Log("Selftest ring: resting after_ms=" + (int)Since() + " visible=" + v.Width + "/" + _ring.RingSize + " hole_hits_ring=" + hole);
                        if (!small || !hole) { Fail(12, "resting ring size/hit"); return; }
                        // A real click (SendInput) in the middle of the resting ring: wakes it and does nothing else.
                        _ringClicks = 0;
                        int downs = _ring.MouseDowns;
                        SendClick(Center(v));
                        offset = new Point(downs, 0);
                        Next(2);
                        break;
                    }
                    case 2:
                        if (Since() < 900) return;
                    {
                        bool woke = !_ring.Resting && _ring.RestAmount < 0.01;
                        bool gotClick = _ring.MouseDowns > offset.X;
                        AppStorage.Log("Selftest ring: real_click_reached_ring=" + gotClick + " woke=" + woke + " taps_counted=" + _ringClicks);
                        if (!gotClick || !woke || _ringClicks != 0) { Fail(13, "wake tap"); return; }
                        Next(3);
                        break;
                    }
                    case 3:
                        if (Since() > 6000) { Fail(12, "did not rest again"); return; }
                        if (!_ring.Resting) return;
                        AppStorage.Log("Selftest ring: rested again after_ms=" + (int)Since());
                        _ring.RestDelayMs = 10000;
                        _ring.Wake();
                        _ring.State = RingState.Polishing;
                        offset = new Point((int)_ring.SpinAngle, 0);
                        Next(4);
                        break;
                    case 4:
                        if (Since() < 500) return;
                    {
                        bool spun = _ring.Spinning && Math.Abs(_ring.SpinAngle - offset.X) > 1;
                        _ring.State = RingState.Idle;
                        bool stopped = !_ring.Spinning;
                        AppStorage.Log("Selftest ring: spinning=" + spun + " stopped_after=" + stopped);
                        if (!spun || !stopped) { Fail(14, "spinner"); return; }
                        // Move together: open the Words bubble, drag the ring, then drag the bubble.
                        stage = 40; // busy while awaiting
                        _bubble.Open(_ring.RingBounds, _ring.Edge, Dpi);
                        await Task.Delay(300);
                        var off = new Point(_bubble.Area.X - _ring.RingBounds.X, _bubble.Area.Y - _ring.RingBounds.Y);
                        int dx = _ring.Edge == DockEdge.Right ? -350 : 350;
                        _ring.SimulateDrag(dx, 120, release: false);
                        var off2 = new Point(_bubble.Area.X - _ring.RingBounds.X, _bubble.Area.Y - _ring.RingBounds.Y);
                        bool followed = off2 == off;
                        _ring.SimulateRelease();
                        bool docked1 = Docked(), beside1 = BesideRing();
                        AppStorage.Log("Selftest ring: bubble_follows_ring=" + followed + " ring_docked=" + docked1 + " edge=" + _ring.Edge + " bubble_beside=" + beside1);
                        var r0 = _ring.RingBounds; var b0 = _bubble.Area;
                        _bubble.SimulateDrag(-200, -90, release: false);
                        bool ringFollowed = _ring.RingBounds.X - r0.X == _bubble.Area.X - b0.X && _ring.RingBounds.Y - r0.Y == _bubble.Area.Y - b0.Y && _bubble.Area.X != b0.X;
                        _bubble.SimulateRelease();
                        bool docked2 = Docked(), beside2 = BesideRing();
                        AppStorage.Log("Selftest ring: ring_follows_bubble=" + ringFollowed + " ring_docked=" + docked2 + " edge=" + _ring.Edge + " bubble_beside=" + beside2);
                        // Dragging the ring across to the other side flips the bubble to stay on screen.
                        var wa = Screen.FromRectangle(_ring.RingBounds).WorkingArea;
                        int across = _ring.Edge == DockEdge.Left ? wa.Width - 100 : -(wa.Width - 100);
                        _ring.SimulateDrag(across, 0, release: true);
                        bool beside3 = BesideRing();
                        AppStorage.Log("Selftest ring: other_side edge=" + _ring.Edge + " bubble_beside=" + beside3);
                        _bubble.Close2();
                        if (!followed || !docked1 || !beside1 || !ringFollowed || !docked2 || !beside2 || !beside3) { Fail(15, "move together"); return; }
                        // Four edges: dropped near the top or the bottom, the ring docks there, where it was dropped
                        // along that edge (never the middle), and the bubble flips to stay on screen.
                        bool four = true;
                        foreach (var drop in new[] { new { Edge = DockEdge.Top, F = 0.28 }, new { Edge = DockEdge.Bottom, F = 0.72 }, new { Edge = DockEdge.Left, F = 0.2 }, new { Edge = DockEdge.Right, F = 0.8 } })
                        {
                            _ring.Wake();
                            var wa4 = Screen.FromRectangle(_ring.RingBounds).WorkingArea;
                            var rb = _ring.RingBounds;
                            bool horizontal = drop.Edge == DockEdge.Top || drop.Edge == DockEdge.Bottom;
                            int tx = horizontal ? wa4.X + (int)(wa4.Width * drop.F) : drop.Edge == DockEdge.Left ? wa4.X + rb.Width / 2 + 6 : wa4.Right - rb.Width / 2 - 6;
                            int ty = !horizontal ? wa4.Y + (int)(wa4.Height * drop.F) : drop.Edge == DockEdge.Top ? wa4.Y + rb.Height / 2 + 6 : wa4.Bottom - rb.Height / 2 - 6;
                            _ring.SimulateDrag(tx - (rb.X + rb.Width / 2), ty - (rb.Y + rb.Height / 2), release: true);
                            var nb = _ring.RingBounds;
                            int along = horizontal ? nb.X + nb.Width / 2 : nb.Y + nb.Height / 2;
                            int want = horizontal ? tx : ty;
                            bool edgeOk = _ring.Edge == drop.Edge && Docked();
                            bool kept = Math.Abs(along - want) <= nb.Width / 2;
                            _bubble.Open(_ring.RingBounds, _ring.Edge, Dpi);
                            await Task.Delay(150);
                            bool beside4 = BesideRing();
                            _bubble.Close2();
                            AppStorage.Log("Selftest ring: dock " + drop.Edge + " -> edge=" + _ring.Edge + " docked=" + edgeOk + " drop_at=" + want + " ring_at=" + along +
                                           " kept_drop_position=" + kept + " bubble_beside_on_screen=" + beside4);
                            four &= edgeOk && kept && beside4;
                        }
                        if (!four) { Fail(18, "four-edge docking"); return; }
                        Next(5);
                        break;
                    }
                    case 5:
                    {
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad")) if (p.MainWindowHandle != IntPtr.Zero) { notepad = p.MainWindowHandle; break; }
                        if (notepad == IntPtr.Zero) { Fail(16, "no Notepad"); return; }
                        Native.SetForegroundWindow(notepad);
                        Next(6);
                        break;
                    }
                    case 6:
                    {
                        if (Since() < 800) return;
                        stage = 60; // busy
                        _bubble.Open(_ring.RingBounds, _ring.Edge, Dpi);
                        _field.Start(notepad);
                        var waitAt = DateTime.UtcNow;
                        while ((_field.HasField == null || _field.Busy) && (DateTime.UtcNow - waitAt).TotalMilliseconds < 4000) await Task.Delay(100);
                        var info = _field.Last ?? new FieldInfo();
                        var edit = Native.FindWindowEx(notepad, IntPtr.Zero, "Edit", null);
                        Native.GetWindowRect(edit, out var er);
                        var editRect = Rectangle.FromLTRB(er.Left, er.Top, er.Right, er.Bottom);
                        var o = _field.Outline;
                        bool near = Math.Abs(o.X - editRect.X) <= 12 && Math.Abs(o.Y - editRect.Y) <= 12 && Math.Abs(o.Right - editRect.Right) <= 12 && Math.Abs(o.Bottom - editRect.Bottom) <= 12;
                        AppStorage.Log("Selftest ring: notepad " + DescribeField(info) + " edit=" + editRect.Width + "x" + editRect.Height +
                                       " outline_matches_edit=" + near + " glow_active=" + _field.Active + " bubble_field=" + _bubble.FieldValid);
                        if (!info.Editable || !_field.Active || !near || _bubble.FieldValid != true) { Fail(16, "Notepad field"); return; }
                        _field.Stop();
                        await Task.Delay(400); // the outline fades
                        bool hidden = !_field.Active;
                        AppStorage.Log("Selftest ring: outline hidden=" + hidden);
                        _bubble.Close2();
                        if (!hidden) { Fail(16, "outline did not hide"); return; }
                        poll.Stop(); deadline.Stop();
                        AppStorage.Log("Selftest ring: ok=True");
                        SelfTestExit(0);
                        break;
                    }
                }
            };
            var go = new Timer { Interval = 1500 };
            go.Tick += (s, e) => { go.Stop(); deadline.Start(); poll.Start(); at = DateTime.UtcNow; };
            go.Start();
        }

        private static Point Center(Rectangle r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        internal static void SendClick(Point p)
        {
            var vs = SystemInformation.VirtualScreen;
            int ax = (int)Math.Round((p.X - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1));
            int ay = (int)Math.Round((p.Y - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1));
            uint abs = Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            var inputs = new[]
            {
                Mouse(ax, ay, Native.MOUSEEVENTF_MOVE | abs),
                Mouse(ax, ay, Native.MOUSEEVENTF_LEFTDOWN | abs),
                Mouse(ax, ay, Native.MOUSEEVENTF_LEFTUP | abs),
            };
            Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT Mouse(int x, int y, uint flags) =>
            new Native.INPUT { type = 0, u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = x, dy = y, dwFlags = flags } } };

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
            CloseOverlay();
            _glow.Dispose();
            _field.Dispose();
            _shortcuts.Dispose();
            _typing.Dispose();
            Cancel(null);
            ShellWindows.Unhook();
            _snoozeTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _hotkeys.Dispose();
            _main?.Close();
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
