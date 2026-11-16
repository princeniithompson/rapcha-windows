using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// While select-to-speak is on: a low-level mouse hook spots the end of a text selection in another app
    /// (a drag release, or a double/triple click) and a low-level keyboard hook spots Esc. Input is never
    /// blocked or changed; the hooks only watch. Both are removed when the mode is turned off.
    /// </summary>
    public sealed class SelectionWatcher : IDisposable
    {
        private readonly Native.HookProc _mouseProc;
        private readonly Native.HookProc _keyProc;
        private IntPtr _mouseHook, _keyHook;
        private readonly Timer _settle = new Timer();
        private readonly uint _myPid = Native.GetCurrentProcessId();

        private Native.POINT _downPt, _lastUpPt;
        private uint _lastUpTime;
        private int _clicks;
        private bool _downInOtherApp;
        private IntPtr _downRoot;
        private Native.RECT _downRect;

        public event Action SelectionFinished;
        public event Action EscapePressed;

        public SelectionWatcher()
        {
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            _settle.Tick += (s, e) => { _settle.Stop(); SelectionFinished?.Invoke(); };
        }

        public bool Running => _mouseHook != IntPtr.Zero;

        public void Start()
        {
            if (Running) return;
            var mod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
            AppStorage.Log("Select-to-speak hooks: mouse=" + (_mouseHook != IntPtr.Zero) + " keyboard=" + (_keyHook != IntPtr.Zero) +
                           (_mouseHook == IntPtr.Zero ? " error=" + Marshal.GetLastWin32Error() : ""));
        }

        public void Stop()
        {
            _settle.Stop();
            if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            if (_keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        }

        private bool IsOurs(Native.POINT pt)
        {
            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == _myPid;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP)
                    {
                        var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        if ((info.flags & Native.LLMHF_INJECTED) == 0) OnButton(msg == Native.WM_LBUTTONDOWN, info.pt, info.time);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void OnButton(bool down, Native.POINT pt, uint time)
        {
            var dbl = SystemInformation.DoubleClickSize;
            if (down)
            {
                _settle.Stop(); // a new click supersedes a pending capture (double -> triple click)
                _downInOtherApp = !IsOurs(pt);
                _downRoot = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT);
                Native.GetWindowRect(_downRoot, out _downRect);
                bool near = Math.Abs(pt.X - _lastUpPt.X) <= dbl.Width && Math.Abs(pt.Y - _lastUpPt.Y) <= dbl.Height;
                _clicks = (time - _lastUpTime <= Native.GetDoubleClickTime() && near) ? _clicks + 1 : 1;
                _downPt = pt;
                return;
            }
            _lastUpPt = pt;
            _lastUpTime = time;
            if (!_downInOtherApp) return;
            var drag = SystemInformation.DragSize;
            bool dragged = Math.Abs(pt.X - _downPt.X) > Math.Max(4, drag.Width) || Math.Abs(pt.Y - _downPt.Y) > Math.Max(4, drag.Height);
            if (dragged && WindowMovedOrResized()) return; // dragging a window around is not a selection
            if (dragged || _clicks >= 2)
            {
                // Give the app a moment to finish the selection; a following click cancels this.
                _settle.Interval = _clicks >= 2 && !dragged ? (int)Math.Min(600, Native.GetDoubleClickTime()) : 150;
                _settle.Start();
            }
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
                        if (h != null) BeginInvokeOnUi(h);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        private void BeginInvokeOnUi(Action a)
        {
            // Hooks run on the UI thread already; defer so the hook returns immediately.
            var t = new Timer { Interval = 1 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        public void Dispose()
        {
            Stop();
            _settle.Dispose();
        }
    }
}
