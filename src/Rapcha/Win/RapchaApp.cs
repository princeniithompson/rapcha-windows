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
        private readonly string _selfTestOcr;
        private readonly bool _selfTestRing;

        // Voice typing: the text field the words will go into.
        private readonly FieldIndicator _field = new FieldIndicator();

        // Ring and Words bubble move together while the bubble is up.
        private Point _bubbleOffset;
        private bool _bubbleFollows;

        public RapchaApp(string selfTestOcr = null, bool selfTestRing = false)
        {
            _selfTestOcr = selfTestOcr;
            _selfTestRing = selfTestRing;
            _menu.Items.Add("Settings", null, (s, e) => OpenSettings(null));
            _menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray.Icon = Program.SmallIcon;
            _tray.Text = "Rapcha - click the ring to talk";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => OpenSettings(null);

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
            _bubble.CompleteClicked += () => { if (_phase == Phase.Recording) _ = FinishAsync(); };
            _bubble.CancelClicked += () => { if (_phase == Phase.Recording) Cancel("Cancelled"); };
            _bubble.FixMicClicked += SettingsForm.OpenMicrophoneSettings;
            _hotkeys.HotkeyPressed += id => { if (id == EscHotkeyId) Cancel("Cancelled"); };
            _watch.Tick += OnWatchTick;

            _ring.ShowDocked();
            InstanceManager.Listen(
                onQuit: () => { try { _ring.BeginInvoke(new Action(() => { AppStorage.Log("Quit requested by a newer Rapcha"); ExitApp(); })); } catch { } },
                onShow: () => { try { _ring.BeginInvoke(new Action(() => { AppStorage.Log("Show requested by a second launch: opening Settings"); _ring.ShowDocked(); OpenSettings(null); })); } catch { } });
            AppStorage.Log("Rapcha " + typeof(RapchaApp).Assembly.GetName().Version + " started on " + Environment.OSVersion +
                           (Environment.Is64BitOperatingSystem ? " x64" : " x86") + ", mics=[" + string.Join("; ", WaveInRecorder.ListDevices()) + "]");

            _ = VoiceSettings.RemovePiperAsync(); // older versions downloaded a voice here; it is no longer used
            if (_selfTestOcr != null) { RunOcrSelfTest(); return; }
            if (_selfTestRing) { RunRingSelfTest(); return; }
            if (LiveSession.IsPlaceholder(AppStorage.LoadApiKey()))
                OpenSettings("Welcome to Rapcha! Paste your Gemini API key, check that the microphone bar moves when you talk, then press Save.");
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

            IntPtr fromOverlay = CloseOverlay(); // voice typing always wins over reading aloud
            _settings?.PauseMeter();
            _target = fromOverlay != IntPtr.Zero && TextDelivery.IsUsableTarget(fromOverlay) ? fromOverlay : TextDelivery.CaptureTarget();
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
            _ = ShowTargetFieldAsync(gen, number);
            _hotkeys.Register(EscHotkeyId, Native.VK_ESCAPE);
            _watch.Start();
            _ = session.ConnectAsync();
        }

        /// <summary>Outlines the text field the words will go into, or says there isn't one.</summary>
        private async Task ShowTargetFieldAsync(int gen, int number)
        {
            FieldInfo info;
            try { info = await _field.BeginAsync(_target); }
            catch (Exception e) { AppStorage.Log("[s" + number + "] field lookup failed: " + e.GetType().Name); return; }
            if (gen != _generation || _phase == Phase.Idle) return; // a newer session or the end already reset the outline
            ApplyField(info);
            AppStorage.Log("[s" + number + "] field: " + DescribeField(info));
        }

        private void ApplyField(FieldInfo info)
        {
            _bubble.Note = info.Editable ? "" : Messages.NoTextBox;
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
            ResetToIdle(keepField: text.Length > 0);

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
            _ring.State = RingState.Polishing; // keep spinning until the words are in
            TextDelivery.Result result;
            try { result = TextDelivery.Deliver(text, _target); }
            finally
            {
                if (_phase == Phase.Idle) _ring.State = RingState.Idle;
                _field.Hide();
            }
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

        private void ResetToIdle(bool keepField = false)
        {
            if (!keepField) _field.Hide();
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

        // ---------------- Select to speak (screen overlay) ----------------

        private ScreenOverlay _overlay;
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

            Task<OcrService.Result> ocr;
            try { ocr = OcrService.RecognizeAsync(shot, dpi); } // copies the pixels before returning
            finally { shot.Dispose(); }
            ocr.ContinueWith(t =>
            {
                var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new OcrService.Result { Problem = "Couldn't read the screen. Try again." };
                try
                {
                    _ring.BeginInvoke(new Action(() =>
                    {
                        if (gen != _overlayGeneration || _overlay != overlay || overlay.IsDisposed) return;
                        overlay.SetOcr(r);
                        _ring.KeepOnTop();
                    }));
                }
                catch { }
            });
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
        /// 5 close failed, 6 OCR unavailable, 7 two-column page read out of order, 1 other/timeout.
        /// </summary>
        private void RunOcrSelfTest()
        {
            var expected = _selfTestOcr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int voices = SpeechReader.CountVoices();
            AppStorage.Log("Selftest ocr: sapi_voices=" + voices);
            if (voices == 0) { SelfTestExit(2); return; }
            int stage = 0;
            DateTime stageAt = DateTime.UtcNow;
            TextLine line = null;
            ColumnsTestForm columns = null;
            var poll = new Timer { Interval = 150 };
            var deadline = new Timer { Interval = 120000 };
            deadline.Tick += (s, e) => { deadline.Stop(); poll.Stop(); AppStorage.Log("Selftest ocr: timed out at stage " + stage); SelfTestExit(1); };
            void Fail(int code, string why) { poll.Stop(); deadline.Stop(); AppStorage.Log("Selftest ocr: FAIL " + why); SelfTestExit(code); }
            bool Done() => !_overlay.Previewing && _overlay.Reader != null && _overlay.Reader.Status == ReadState.Finished;
            bool Check(string how)
            {
                var text = _overlay.LastReadText;
                bool all = expected.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                bool second = text.IndexOf("second", StringComparison.OrdinalIgnoreCase) >= 0;
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
                        _overlay.SimulateTap(w.CenterX, w.CenterY);
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
                        if (_overlay != null) return;
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
                        Native.PostMessage(_overlay.Handle, Native.WM_KEYDOWN, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
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
            ColumnsTestForm plain = null;
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
            bool Docked() { var r = _ring.RingBounds; var wa = Screen.FromRectangle(r).WorkingArea; return (_ring.Edge == DockEdge.Left && r.X - wa.X < r.Width) || (_ring.Edge == DockEdge.Right && wa.Right - r.Right < r.Width); }
            bool BesideRing()
            {
                var b = _bubble.Area; var r = _ring.RingBounds; var wa = Screen.FromRectangle(r).WorkingArea;
                bool onScreen = wa.Contains(b);
                bool beside = (b.X >= r.Right || b.Right <= r.X) && b.Y < r.Bottom && b.Bottom > r.Y;
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
                        _bubble.SimulateDrag(0, 0, release: true);
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
                        var info = await _field.BeginAsync(notepad);
                        ApplyField(info);
                        var edit = Native.FindWindowEx(notepad, IntPtr.Zero, "Edit", null);
                        Native.GetWindowRect(edit, out var er);
                        var editRect = Rectangle.FromLTRB(er.Left, er.Top, er.Right, er.Bottom);
                        var o = _field.Outline;
                        bool near = Math.Abs(o.X - editRect.X) <= 12 && Math.Abs(o.Y - editRect.Y) <= 12 && Math.Abs(o.Right - editRect.Right) <= 12 && Math.Abs(o.Bottom - editRect.Bottom) <= 12;
                        AppStorage.Log("Selftest ring: notepad " + DescribeField(info) + " edit=" + editRect.Width + "x" + editRect.Height +
                                       " outline_matches_edit=" + near + " glow_active=" + _field.Active + " note_empty=" + (_bubble.Note.Length == 0));
                        if (!info.Editable || !_field.Active || !near || _bubble.Note.Length != 0) { Fail(16, "Notepad field"); return; }
                        _field.Hide();
                        bool hidden = !_field.Active;
                        AppStorage.Log("Selftest ring: outline hidden=" + hidden);
                        if (!hidden) { Fail(16, "outline did not hide"); return; }
                        // A window with no text field at all.
                        plain = new ColumnsTestForm();
                        plain.Show();
                        plain.Activate();
                        Next(7);
                        break;
                    }
                    case 7:
                    {
                        if (Since() < 1000) return;
                        stage = 70;
                        var info = await _field.BeginAsync(plain.Handle);
                        ApplyField(info);
                        await Task.Delay(200);
                        bool note = _bubble.Note == Messages.NoTextBox;
                        AppStorage.Log("Selftest ring: plain window " + DescribeField(info) + " note_shown=" + note);
                        _field.Hide();
                        _bubble.Close2();
                        plain.Close();
                        if (info.Editable || _field.Active || !note) { Fail(17, "no-field note"); return; }
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

        private static void SendClick(Point p)
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
