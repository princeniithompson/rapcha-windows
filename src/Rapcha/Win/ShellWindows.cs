using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Start menu / Windows Search awareness (v17). Which app owns a window (by process image name, cached), whether
    /// the foreground window is Start or Search, its rectangle, and a WinEvent hook that reports every foreground
    /// change, so Rapcha can put its windows back on top, move them out from under the flyout, and re-check the
    /// text field. Windows draws Start and Search in a higher "band" than any normal always-on-top window, so
    /// Rapcha can't draw over them; see README "Start menu and Windows Search".
    /// </summary>
    public static class ShellWindows
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Native.RECT rect, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

        private static readonly Dictionary<uint, string> Exes = new Dictionary<uint, string>();
        private static readonly object ExeLock = new object();
        private static WinEventProc _proc;   // kept alive for the hook
        private static IntPtr _hook;

        /// <summary>The foreground window changed (UI thread; the hook is out-of-context on the thread that set it).</summary>
        public static event Action<IntPtr> ForegroundChanged;
        public static bool Hooked => _hook != IntPtr.Zero;

        /// <summary>The process image path of the window's owner ("" if Windows won't say).</summary>
        public static string ExeOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            lock (ExeLock)
            {
                if (Exes.TryGetValue(pid, out var known)) return known;
            }
            string path = "";
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            lock (ExeLock)
            {
                if (Exes.Count > 256) Exes.Clear();
                Exes[pid] = path;
            }
            return path;
        }

        public static ShellSurface SurfaceOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return ShellSurface.None;
            var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            return ShellTargets.Classify(TextDelivery.ClassOf(root), ExeOf(root));
        }

        /// <summary>The Start menu or Windows Search is the foreground window right now.</summary>
        public static bool StartOrSearchInFront(out IntPtr root)
        {
            var fg = Native.GetForegroundWindow();
            root = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOT);
            return root != IntPtr.Zero && SurfaceOf(root) == ShellSurface.StartSearch && !IsCloaked(root);
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0; } catch { return false; }
        }

        /// <summary>Where the window is drawn (DWM frame bounds; falls back to the window rectangle).</summary>
        public static Rectangle BoundsOf(IntPtr hwnd)
        {
            try
            {
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf(typeof(Native.RECT))) == 0 && r.Right > r.Left)
                    return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { }
            return Native.GetWindowRect(hwnd, out var w) ? Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom) : Rectangle.Empty;
        }

        /// <summary>Starts the foreground WinEvent hook (call once on the UI thread).</summary>
        public static void Hook()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = (hook, ev, hwnd, obj, child, thread, time) =>
            {
                try { ForegroundChanged?.Invoke(hwnd); } catch (Exception e) { AppStorage.Log("Foreground hook error " + e.GetType().Name); }
            };
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            AppStorage.Log("Foreground hook: " + (_hook != IntPtr.Zero ? "on" : "unavailable"));
        }

        public static void Unhook()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
