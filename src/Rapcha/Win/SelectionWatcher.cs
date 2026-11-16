using System;
using System.Runtime.InteropServices;
using System.Threading;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Select-to-speak input: low-level mouse and keyboard hooks on their OWN thread (Rapcha's drawing can never
    /// delay a click). While the mode is on, select to speak has the upper hand (see ClickArbiter):
    /// a left click outside Rapcha is swallowed and reported as "read at this point"; a drag gets its press
    /// replayed to the app so text selection still works, and is reported when released.
    /// Rapcha's own windows, right clicks, the wheel and the keyboard always pass through untouched.
    /// The hook callback only does arithmetic; reading happens on the UI thread.
    /// </summary>
    public sealed class SelectionWatcher : IDisposable
    {
        public sealed class Drag
        {
            public SelectionGesture.Result Gesture;
            public IntPtr DownWindow;
            public bool TextCursor;
        }

        private static readonly IntPtr ReplayMarker = (IntPtr)0x52415043; // "RAPC" in dwExtraInfo: our own replayed input
        private const int WM_REPLAY = Native.WM_APP + 0x2A;

        /// <summary>Self-test only: treat synthetic (SendInput) clicks like real ones.</summary>
        public static bool AcceptInjectedForTest;

        private readonly Native.HookProc _mouseProc;
        private readonly Native.HookProc _keyProc;
        private readonly uint _myPid = Native.GetCurrentProcessId();
        private readonly IntPtr _ibeam = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.IDC_IBEAM);
        private IntPtr _mouseHook, _keyHook;
        private Thread _thread;
        private uint _threadId;
        private SynchronizationContext _ui;

        private ClickArbiter _arbiter;
        private IntPtr _downWindow, _downRoot;
        private Native.RECT _downRect;
        private bool _ibeamAtDown;
        private Native.POINT _replayTo;

        public event Action<Drag> DragSelection;
        public event Action<int, int> PointClicked;
        public event Action EscapePressed;

        public int Swallowed { get; private set; }

        public SelectionWatcher()
        {
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
        }

        public bool Running => _thread != null;

        public void Start()
        {
            if (Running) return;
            _ui = SynchronizationContext.Current;
            int threshold = Math.Max(SelectionGesture.DefaultThresholdPx, System.Windows.Forms.SystemInformation.DragSize.Width);
            _arbiter = new ClickArbiter(threshold);
            using (var ready = new ManualResetEventSlim(false))
            {
                _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "Rapcha input watch" };
                _thread.Start();
                ready.Wait(2000);
            }
            AppStorage.Log("Select-to-speak hooks: mouse=" + (_mouseHook != IntPtr.Zero) + " keyboard=" + (_keyHook != IntPtr.Zero) + " threshold_px=" + threshold);
        }

        public void Stop()
        {
            var t = _thread;
            if (t == null) return;
            _thread = null;
            if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            t.Join(1500);
            _threadId = 0;
        }

        private void HookThread(ManualResetEventSlim ready)
        {
            _threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // create this thread's message queue
            var mod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
            ready.Set();
            try
            {
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_REPLAY) ReplayPress();
                }
            }
            finally
            {
                if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
                if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
                _mouseHook = _keyHook = IntPtr.Zero;
                _arbiter?.Reset();
            }
        }

        private static bool IsTouch(IntPtr extra) => ((long)extra & 0xFFFFFF00) == 0xFF515700;

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP || msg == Native.WM_MOUSEMOVE)
                    {
                        var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        bool ours = info.dwExtraInfo == ReplayMarker;
                        bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
                        if (!ours && (!injected || IsTouch(info.dwExtraInfo) || AcceptInjectedForTest))
                        {
                            if (OnMouse(msg, info.pt)) { Swallowed++; return (IntPtr)1; } // swallowed: the app never sees it
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        /// <summary>True = swallow this event.</summary>
        private bool OnMouse(int msg, Native.POINT pt)
        {
            if (msg == Native.WM_MOUSEMOVE)
            {
                var a = _arbiter.OnMove(pt.X, pt.Y);
                if (a != ClickArbiter.Action.SwallowAndReplayPress) return false;
                _replayTo = pt;
                Native.PostThreadMessage(_threadId, WM_REPLAY, IntPtr.Zero, IntPtr.Zero); // after this callback returns
                return true;
            }
            if (msg == Native.WM_LBUTTONDOWN)
            {
                _downWindow = Native.WindowFromPoint(pt);
                _downRoot = Native.GetAncestor(_downWindow, Native.GA_ROOT);
                Native.GetWindowRect(_downRoot, out _downRect);
                Native.GetWindowThreadProcessId(_downWindow, out var pid);
                _ibeamAtDown = IsTextCursor();
                bool own = pid == _myPid || _downWindow == IntPtr.Zero;
                return _arbiter.OnDown(pt.X, pt.Y, own, _ibeamAtDown) == ClickArbiter.Action.Swallow;
            }
            // WM_LBUTTONUP
            var action = _arbiter.OnUp(pt.X, pt.Y, WindowMovedOrResized(), out var outcome, out var drag);
            if (outcome == ClickArbiter.Outcome.ReadAtPoint)
            {
                var h = PointClicked;
                int x = pt.X, y = pt.Y;
                if (h != null) _ui?.Post(_ => h(x, y), null);
            }
            else if (outcome == ClickArbiter.Outcome.Drag)
            {
                var d = new Drag { Gesture = drag, DownWindow = _downWindow, TextCursor = _ibeamAtDown || IsTextCursor() };
                var h = DragSelection;
                if (h != null) _ui?.Post(_ => h(d), null);
            }
            return action == ClickArbiter.Action.Swallow;
        }

        /// <summary>A held press turned into a drag: give the app its press at the original point, then the move.</summary>
        private void ReplayPress()
        {
            var down = new Native.POINT(_arbiter.DownX, _arbiter.DownY);
            var inputs = new[]
            {
                MouseInput(down, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK),
                MouseInput(down, Native.MOUSEEVENTF_LEFTDOWN | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK | Native.MOUSEEVENTF_MOVE),
                MouseInput(_replayTo, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK),
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT MouseInput(Native.POINT p, uint flags)
        {
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
            int vw = Math.Max(2, Native.GetSystemMetrics(78)), vh = Math.Max(2, Native.GetSystemMetrics(79));
            return new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                u = new Native.InputUnion
                {
                    mi = new Native.MOUSEINPUT
                    {
                        dx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1)),
                        dy = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1)),
                        dwFlags = flags,
                        dwExtraInfo = ReplayMarker
                    }
                }
            };
        }

        /// <summary>Self-test: a synthetic left click at a screen point (goes through the hooks like a real one).</summary>
        public static void SendTestClick(int x, int y)
        {
            var p = new Native.POINT(x, y);
            uint abs = Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            var inputs = new[]
            {
                MouseInputNoMarker(p, Native.MOUSEEVENTF_MOVE | abs),
                MouseInputNoMarker(p, Native.MOUSEEVENTF_LEFTDOWN | abs | Native.MOUSEEVENTF_MOVE),
                MouseInputNoMarker(p, Native.MOUSEEVENTF_LEFTUP | abs | Native.MOUSEEVENTF_MOVE),
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT MouseInputNoMarker(Native.POINT p, uint flags)
        {
            var i = MouseInput(p, flags);
            i.u.mi.dwExtraInfo = IntPtr.Zero;
            return i;
        }

        private bool IsTextCursor()
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO)) };
            return Native.GetCursorInfo(ref ci) && ci.hCursor == _ibeam;
        }

        private bool WindowMovedOrResized()
        {
            if (_downRoot == IntPtr.Zero || !Native.GetWindowRect(_downRoot, out var r)) return false;
            return r.Left != _downRect.Left || r.Top != _downRect.Top || r.Right != _downRect.Right || r.Bottom != _downRect.Bottom;
        }

        private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam.ToInt32() == Native.WM_KEYDOWN || wParam.ToInt32() == Native.WM_SYSKEYDOWN))
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (k.vkCode == Native.VK_ESCAPE && (k.flags & Native.LLKHF_INJECTED) == 0)
                    {
                        var h = EscapePressed;
                        if (h != null) _ui?.Post(_ => h(), null);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        public void Dispose() => Stop();
    }
}
