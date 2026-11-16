using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Rapcha.Win
{
    /// <summary>
    /// Notices that a key was pressed (only the time, never which key) through a low-level keyboard hook on its
    /// own thread. Nothing is swallowed or delayed: the hook only stores a tick count and passes the key on.
    /// </summary>
    public sealed class TypingWatcher : IDisposable
    {
        private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        private static readonly IntPtr OwnMarker = (IntPtr)0x52504348; // keys Rapcha re-injects for chords
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private Native.HookProc _proc;
        private IntPtr _hook;
        private uint _threadId;
        private int _lastKey;     // Environment.TickCount of the latest key press
        private int _count;

        public int LastKeyTick => Volatile.Read(ref _lastKey);
        public int KeyCount => Volatile.Read(ref _count);
        public bool Installed => _hook != IntPtr.Zero;

        public TypingWatcher()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Rapcha typing watcher" };
            _thread.Start();
            _ready.Wait(2000);
        }

        private void Run()
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = Proc;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) AppStorage.Log("Typing watcher: hook failed (" + Marshal.GetLastWin32Error() + ")");
            _ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        }

        private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var extra = Marshal.ReadIntPtr(lParam, 16 /* dwExtraInfo after vkCode, scanCode, flags, time */ );
                    if (extra != OwnMarker)
                    {
                        Volatile.Write(ref _lastKey, Environment.TickCount);
                        Interlocked.Increment(ref _count);
                    }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }
    }

    /// <summary>
    /// Keeps the ring awake while the user types into a real text field (focus + caret, the same rules as the
    /// outline): a key press wakes a resting ring, and while awake it only restarts the idle countdown.
    /// The field check is cached briefly and dropped whenever focus moves, so typing costs no UIA calls.
    /// </summary>
    public sealed class TypingWaker : IDisposable
    {
        public const int CacheMs = 2000;
        private readonly TypingWatcher _watch = new TypingWatcher();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 120 };
        private readonly RingWindow _ring;
        private readonly FieldIndicator _field;
        private int _seen;
        private bool _probing;
        private bool? _cached;
        private DateTime _cachedAt = DateTime.MinValue;

        /// <summary>Only when this says yes (idle: no dictation, no select to speak).</summary>
        public Func<bool> Allowed;
        public int Wakes { get; private set; }
        public int Pokes { get; private set; }
        public int Probes { get; private set; }
        public bool Installed => _watch.Installed;

        public TypingWaker(RingWindow ring, FieldIndicator field)
        {
            _ring = ring;
            _field = field;
            _seen = _watch.LastKeyTick;
            _field.FocusMoved += () => _cached = null;
            _timer.Tick += async (s, e) => await OnTick();
            _timer.Start();
        }

        private async Task OnTick()
        {
            int k = _watch.LastKeyTick;
            if (k == _seen) return;
            _seen = k;
            if (Allowed != null && !Allowed()) return;
            bool fresh = _cached.HasValue && (DateTime.UtcNow - _cachedAt).TotalMilliseconds < CacheMs;
            if (fresh)
            {
                if (_cached == true) Act();
                return;
            }
            if (_probing) return;
            _probing = true;
            FieldInfo info;
            try { info = await _field.ProbeAsync(); }
            catch { info = null; }
            finally { _probing = false; }
            if (info == null) return;   // UIA busy or slow: the next key press asks again
            Probes++;
            _cached = info.Editable && !info.OwnApp;
            _cachedAt = DateTime.UtcNow;
            if (_cached == true && (Allowed == null || Allowed())) Act();
        }

        private void Act()
        {
            if (_ring.Resting)
            {
                Wakes++;
                AppStorage.Log("Ring: woke (typing in a text field)");
                _ring.Wake();
            }
            else
            {
                Pokes++;
                _ring.Poke();   // already awake: just restart the idle countdown (no animation restart)
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _watch.Dispose();
        }
    }
}
