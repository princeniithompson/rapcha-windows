using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// Puts the text on the clipboard, then pastes it with Ctrl+V into the window that had focus
    /// before recording started, when that window still has a focused control. Never types per character.
    /// </summary>
    public static class TextDelivery
    {
        public enum Result { Typed, Copied, Failed }

        public static IntPtr CaptureTarget()
        {
            var fg = Native.GetForegroundWindow();
            return IsUsableTarget(fg) ? fg : IntPtr.Zero;
        }

        public static bool IsUsableTarget(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Native.GetCurrentProcessId()) return false;
            var cls = ClassOf(Native.GetAncestor(hwnd, Native.GA_ROOT));
            switch (cls)
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "NotifyIconOverflowWindow":
                case "Windows.UI.Core.CoreWindow": // Start menu / search flyouts
                    return false;
            }
            return true;
        }

        /// <summary>Must be called on the UI (STA) thread.</summary>
        public static Result Deliver(string text, IntPtr target)
        {
            if (!SetClipboard(text)) return Result.Failed;
            if (!IsUsableTarget(target) || Native.IsIconic(target)) return Result.Copied;

            uint thread = Native.GetWindowThreadProcessId(target, out _);
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (!Native.GetGUIThreadInfo(thread, ref info)) return Result.Copied;
            if (info.hwndFocus == IntPtr.Zero && info.hwndCaret == IntPtr.Zero) return Result.Copied;

            if (Native.GetForegroundWindow() != target)
            {
                if (!BringToFront(target)) return Result.Copied;
                Thread.Sleep(60);
                if (Native.GetForegroundWindow() != target) return Result.Copied;
            }

            WaitForModifiersReleased();
            return SendCtrlV() ? Result.Typed : Result.Copied;
        }

        private static bool BringToFront(IntPtr hwnd)
        {
            if (Native.SetForegroundWindow(hwnd)) return true;
            // Fallback: briefly share input state with the current foreground thread.
            var fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            if (fgThread == 0 || fgThread == me) return false;
            Native.AttachThreadInput(me, fgThread, true);
            try { return Native.SetForegroundWindow(hwnd); }
            finally { Native.AttachThreadInput(me, fgThread, false); }
        }

        private static void WaitForModifiersReleased()
        {
            int[] keys = { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, LWin, RWin
            for (int i = 0; i < 40; i++)
            {
                bool any = false;
                foreach (var k in keys) if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) any = true;
                if (!any) return;
                Thread.Sleep(25);
            }
        }

        private static bool SendCtrlV()
        {
            var inputs = new[]
            {
                Key(Native.VK_CONTROL, false), Key(Native.VK_V, false),
                Key(Native.VK_V, true), Key(Native.VK_CONTROL, true)
            };
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            return sent == inputs.Length;
        }

        private static Native.INPUT Key(ushort vk, bool up)
        {
            return new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)Native.MapVirtualKey(vk, 0),
                        dwFlags = up ? Native.KEYEVENTF_KEYUP : 0
                    }
                }
            };
        }

        private static bool SetClipboard(string text)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true, 5, 50);
                    return true;
                }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
