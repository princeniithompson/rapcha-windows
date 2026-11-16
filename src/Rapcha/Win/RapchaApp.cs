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

        public RapchaApp(string selfTestOcr = null)
        {
            _selfTestOcr = selfTestOcr;
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

            _ = VoiceSettings.RemovePiperAsync(); // older versions downloaded a voice here; it is no longer used
            if (_selfTestOcr != null) { RunOcrSelfTest(); return; }
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
        /// 5 close failed, 6 OCR unavailable, 1 other/timeout.
        /// </summary>
        private void RunOcrSelfTest()
        {
            var expected = _selfTestOcr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int voices = SpeechReader.CountVoices();
            AppStorage.Log("Selftest ocr: sapi_voices=" + voices);
            if (voices == 0) { SelfTestExit(2); return; }
            int stage = 0;
            OcrLine line = null;
            var poll = new Timer { Interval = 150 };
            var deadline = new Timer { Interval = 120000 };
            deadline.Tick += (s, e) => { deadline.Stop(); poll.Stop(); AppStorage.Log("Selftest ocr: timed out at stage " + stage); SelfTestExit(1); };
            void Fail(int code, string why) { poll.Stop(); deadline.Stop(); AppStorage.Log("Selftest ocr: FAIL " + why); SelfTestExit(code); }
            bool Done() => _overlay.Reader != null && _overlay.Reader.Status == ReadState.Finished;
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
                        line = r.Layout.Lines.FirstOrDefault(l => l.Words.Any(w => string.Equals(w.Text.Trim('.', ','), expected[0], StringComparison.OrdinalIgnoreCase)));
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
                        var layout = _overlay.Ocr.Layout;
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
                        AppStorage.Log("Selftest ocr: closed=" + closed + " piper_folder_gone=" + piperGone + " ok=" + (closed && piperGone));
                        poll.Stop(); deadline.Stop();
                        SelfTestExit(closed && piperGone ? 0 : 5);
                        break;
                    }
                }
            };
            var go = new Timer { Interval = 2000 };
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
            CloseOverlay();
            _glow.Dispose();
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
