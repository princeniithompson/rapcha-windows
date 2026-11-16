using System;
using System.Runtime.InteropServices;
using System.Threading;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// While select-to-speak is on: watches (never blocks, never changes) the mouse and keyboard with
    /// low-level hooks that live on their OWN thread, so Rapcha's drawing can never delay a click.
    /// Only a real drag-selection in another app is reported (see SelectionGesture); clicks, double clicks
    /// and window drags are ignored. Esc is reported so the mode can be turned off.
    /// </summary>
    public sealed class SelectionWatcher : IDisposable
    {
        public sealed class Drag
        {
            public SelectionGesture.Result Gesture;
            public IntPtr DownWindow;
            public bool TextCursor;
        }

        private readonly Native.HookProc _mouseProc;
        private readonly Native.HookProc _keyProc;
        private readonly uint _myPid = Native.GetCurrentProcessId();
        private readonly IntPtr _ibeam = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.IDC_IBEAM);
        private IntPtr _mouseHook, _keyHook;
        private Thread _thread;
        private uint _threadId;
        private SynchronizationContext _ui;

        private SelectionGesture _gesture;
        private bool _down;
        private IntPtr _downWindow, _downRoot;
        private Native.RECT _downRect;
        private bool _ibeamAtDown;

        public event Action<Drag> DragSelection;
        public event Action EscapePressed;

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
            _gesture = new SelectionGesture(threshold);
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
            _down = false;
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
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            }
            finally
            {
                if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
                if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
                _mouseHook = _keyHook = IntPtr.Zero;
            }
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Only watching: every event is always passed on unchanged.
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP || (msg == Native.WM_MOUSEMOVE && _down))
                    {
                        var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        if ((info.flags & Native.LLMHF_INJECTED) == 0) OnMouse(msg, info.pt);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void OnMouse(int msg, Native.POINT pt)
        {
            if (msg == Native.WM_MOUSEMOVE) { _gesture.OnMove(pt.X, pt.Y); return; }
            if (msg == Native.WM_LBUTTONDOWN)
            {
                _down = true;
                _downWindow = Native.WindowFromPoint(pt);
                _downRoot = Native.GetAncestor(_downWindow, Native.GA_ROOT);
                Native.GetWindowRect(_downRoot, out _downRect);
                Native.GetWindowThreadProcessId(_downWindow, out var pid);
                _ibeamAtDown = IsTextCursor();
                _gesture.OnDown(pt.X, pt.Y, pid == _myPid || _downWindow == IntPtr.Zero, _ibeamAtDown);
                return;
            }
            _down = false;
            var r = _gesture.OnUp(pt.X, pt.Y, WindowMovedOrResized());
            if (r.Kind != SelectionGesture.Kind.Drag) return;
            var d = new Drag { Gesture = r, DownWindow = _downWindow, TextCursor = _ibeamAtDown || IsTextCursor() };
            var h = DragSelection;
            if (h != null) _ui?.Post(_ => h(d), null);
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
