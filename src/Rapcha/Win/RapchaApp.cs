using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Owns the ring, the label, the tray icon and one dictation at a time.</summary>
    public sealed class RapchaApp : ApplicationContext
    {
        private enum Phase { Idle, Recording, Finishing }

        private const int CompleteTailMs = 180;          // FloatingDictationSessionManager.COMPLETE_TAIL_MS
        private const int MaxRecordingSeconds = 195;     // DictationPiecePlanner.FORCE_AFTER_MS (phase 1: one piece)
        private const int EscHotkeyId = 0x5241;

        private readonly RingWindow _ring = new RingWindow();
        private readonly LabelWindow _label = new LabelWindow();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly HotkeyWindow _hotkeys = new HotkeyWindow();
        private readonly Timer _watch = new Timer { Interval = 500 };

        private Phase _phase = Phase.Idle;
        private int _generation;
        private LiveSession _session;
        private WaveInRecorder _recorder;
        private IntPtr _target;
        private DateTime _startedAt;
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
            _hotkeys.HotkeyPressed += id => { if (id == EscHotkeyId) Cancel("Cancelled"); };
            _watch.Tick += OnWatchTick;

            _ring.ShowDocked();
            AppStorage.Log("Rapcha started");

            if (LiveSession.IsPlaceholder(AppStorage.LoadApiKey()))
                OpenSettings("Welcome to Rapcha! Paste your Gemini API key below and press Save.");
        }

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

            _target = TextDelivery.CaptureTarget();
            var vocab = AppStorage.LoadVocabulary();
            int gen = ++_generation;
            var session = new LiveSession(key, vocab);
            session.Log += AppStorage.Log;
            session.Failed += err =>
            {
                try { _ring.BeginInvoke(new Action(() => OnSessionFailed(gen, err))); } catch { }
            };

            var recorder = new WaveInRecorder(
                chunk => session.SendAudio(chunk),
                level => _ring.Level = level);
            var problem = recorder.Start(out var detail);
            if (problem != MicProblem.None)
            {
                AppStorage.Log("Mic problem: " + problem + " " + detail);
                session.Dispose();
                ShowLabel(MicMessage(problem), 6000, true);
                return;
            }

            _session = session;
            _recorder = recorder;
            _phase = Phase.Recording;
            _startedAt = DateTime.UtcNow;
            _ring.State = RingState.Recording;
            ShowLabel("Listening", 0);
            _hotkeys.Register(EscHotkeyId, Native.VK_ESCAPE);
            _watch.Start();
            _ = session.ConnectAsync();
        }

        private void OnWatchTick(object sender, EventArgs e)
        {
            if (_phase != Phase.Recording) { _watch.Stop(); return; }
            var elapsed = DateTime.UtcNow - _startedAt;
            if (elapsed.TotalSeconds > 1.5 && _recorder != null && _recorder.LooksSilentlyBlocked)
            {
                Cancel(null);
                ShowLabel(Messages.MicPrivacy, 8000, true);
                return;
            }
            if (elapsed.TotalSeconds >= MaxRecordingSeconds) _ = FinishAsync();
        }

        private async Task FinishAsync()
        {
            if (_phase != Phase.Recording) return;
            _phase = Phase.Finishing;
            int gen = _generation;
            _watch.Stop();
            _hotkeys.Unregister(EscHotkeyId);
            _ring.State = RingState.Polishing;
            ShowLabel("Polishing…", 0);

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
                AppStorage.Log("Finish failed: " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                session.Dispose();
            }
            if (gen != _generation || _phase != Phase.Finishing) return; // cancelled or failed meanwhile
            ResetToIdle();

            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                ShowLabel(Messages.NothingHeard, 2000);
                return;
            }
            var result = TextDelivery.Deliver(text, _target);
            AppStorage.Log("Delivered: " + result + " (" + text.Split(' ').Length + " words)");
            switch (result)
            {
                case TextDelivery.Result.Typed: ShowLabel("Typed ✓", 1500); break;
                case TextDelivery.Result.Copied: ShowLabel("Copied ✓  Press Ctrl+V to paste", 1800); break;
                default: ShowLabel("Couldn't use the clipboard. Try again.", 3000, true); break;
            }
        }

        private void Cancel(string message)
        {
            if (_phase != Phase.Recording) return;
            _generation++;
            var recorder = _recorder;
            var session = _session;
            ResetToIdle();
            Task.Run(() => { try { recorder?.Abort(); } catch { } try { session?.Dispose(); } catch { } });
            if (message != null) ShowLabel(message, 1200);
            else _label.HideNow();
        }

        private void OnSessionFailed(int gen, LiveError err)
        {
            if (gen != _generation || _phase == Phase.Idle) return;
            AppStorage.Log("Session failed: " + err.Kind);
            _generation++;
            var recorder = _recorder;
            var session = _session;
            ResetToIdle();
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
        }

        private void ShowLabel(string text, int hideAfterMs, bool warning = false)
        {
            int dpi = (int)Math.Round(96.0 * _ring.RingSize / RingWindow.RingDip);
            _label.ShowText(text, _ring.RingBounds, _ring.Edge, dpi, hideAfterMs, warning);
        }

        private static string MicMessage(MicProblem p)
        {
            switch (p)
            {
                case MicProblem.NoMicrophone: return Messages.NoMic;
                case MicProblem.PrivacyBlocked: return Messages.MicPrivacy;
                case MicProblem.InUse: return Messages.MicInUse;
                default: return Messages.MicOther;
            }
        }

        private void OpenSettings(string message)
        {
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(message);
                _settings.FormClosed += (s, e) => _settings = null;
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
