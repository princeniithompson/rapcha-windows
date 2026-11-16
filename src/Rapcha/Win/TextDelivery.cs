using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// Puts the text on the clipboard (always, as the guaranteed fallback), brings back the window that was
    /// active before recording, makes sure no modifier keys are stuck, and pastes with a scancode Ctrl+V.
    /// Every step's outcome is logged as a short reason (never the text itself).
    /// </summary>
    public static class TextDelivery
    {
        public enum Result { Typed, Copied, Failed }

        public static string LastReason { get; private set; } = "";

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
            var why = new StringBuilder();
            var result = DeliverCore(text, target, why);
            LastReason = why.ToString().Trim();
            AppStorage.Log("Delivery: result=" + result + " " + LastReason);
            return result;
        }

        private static Result DeliverCore(string text, IntPtr target, StringBuilder why)
        {
            if (!SetClipboard(text, why)) { why.Append(" clipboard_failed"); return Result.Failed; }
            Thread.Sleep(60); // let clipboard viewers / the target see the new data before pasting
            if (!IsUsableTarget(target)) { why.Append(" no_target"); return Result.Copied; }
            why.Append(" target=" + ClassOf(Native.GetAncestor(target, Native.GA_ROOT)));

            bool front = BringToFront(target, why);
            uint thread = Native.GetWindowThreadProcessId(target, out _);
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            bool haveInfo = Native.GetGUIThreadInfo(thread, ref info);
            IntPtr focus = haveInfo ? info.hwndFocus : IntPtr.Zero;
            if (!haveInfo || (info.hwndFocus == IntPtr.Zero && info.hwndCaret == IntPtr.Zero))
                why.Append(" no_focus_info"); // UWP/WebView apps (e.g. WhatsApp) often report none; paste anyway

            if (!front)
            {
                // Could not get the window back. Only if the focused field is empty and writable,
                // fill it through UI Automation; otherwise leave the text on the clipboard.
                try
                {
                    if (Uia.SetValueIfEmptyAsync(focus, text, 1200, why).GetAwaiter().GetResult()) return Result.Typed;
                }
                catch (Exception e) { why.Append(" uia_error=" + e.GetType().Name); }
                return Result.Copied;
            }

            ReleaseModifiers(why);
            if (Native.GetForegroundWindow() != target && Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT) != Native.GetAncestor(target, Native.GA_ROOT))
            {
                why.Append(" lost_foreground");
                return Result.Copied;
            }
            if (!SendChord(0x1D, 0x2F)) { why.Append(" sendinput_blocked"); return Result.Copied; } // Ctrl + V
            why.Append(" pasted");
            return Result.Typed;
        }

        /// <summary>Makes the target the foreground window; true when it is (or already was).</summary>
        public static bool BringToFront(IntPtr hwnd, StringBuilder why)
        {
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            if (IsForeground(root)) { why.Append(" fg=already"); return true; }
            if (Native.IsIconic(root)) Native.ShowWindow(root, Native.SW_RESTORE);

            if (Native.SetForegroundWindow(root) && WaitForeground(root, 200)) { why.Append(" fg=direct"); return true; }

            // Share input state with the current foreground thread and the target thread, then ask again.
            uint me = Native.GetCurrentThreadId();
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            uint targetThread = Native.GetWindowThreadProcessId(root, out _);
            bool a1 = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            bool a2 = targetThread != 0 && targetThread != me && targetThread != fgThread && Native.AttachThreadInput(me, targetThread, true);
            try
            {
                Native.BringWindowToTop(root);
                Native.SetForegroundWindow(root);
            }
            finally
            {
                if (a1) Native.AttachThreadInput(me, fgThread, false);
                if (a2) Native.AttachThreadInput(me, targetThread, false);
            }
            if (WaitForeground(root, 250)) { why.Append(" fg=attached"); return true; }

            // Last try: a harmless unassigned key press counts as user input, which unlocks SetForegroundWindow.
            var nudge = new[] { VkInput(0xE8, false), VkInput(0xE8, true) };
            Native.SendInput(2, nudge, Marshal.SizeOf(typeof(Native.INPUT)));
            Native.SetForegroundWindow(root);
            if (WaitForeground(root, 300)) { why.Append(" fg=nudged"); return true; }
            why.Append(" fg=failed");
            return false;
        }

        private static bool IsForeground(IntPtr root)
        {
            var fg = Native.GetForegroundWindow();
            return fg == root || Native.GetAncestor(fg, Native.GA_ROOT) == root;
        }

        private static bool WaitForeground(IntPtr root, int ms)
        {
            for (int waited = 0; waited <= ms; waited += 25)
            {
                if (IsForeground(root)) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        /// <summary>Waits briefly for held Shift/Ctrl/Alt/Win keys to be let go, then lifts any that still look stuck.</summary>
        public static void ReleaseModifiers(StringBuilder why)
        {
            int[] keys = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };
            for (int i = 0; i < 20; i++)
            {
                if (!AnyDown(keys)) return;
                Thread.Sleep(25);
            }
            why?.Append(" modifiers_released");
            var ups = new System.Collections.Generic.List<Native.INPUT>();
            foreach (var k in keys)
                if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) ups.Add(VkInput((ushort)k, true));
            if (ups.Count > 0) Native.SendInput((uint)ups.Count, ups.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public static bool ModifiersDown() => AnyDown(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C });

        private static bool AnyDown(int[] keys)
        {
            foreach (var k in keys) if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return true;
            return false;
        }

        /// <summary>Left Ctrl + one key, sent as hardware scancodes (works with apps that ignore virtual keys).</summary>
        public static bool SendChord(ushort ctrlScan, ushort keyScan)
        {
            var inputs = new[]
            {
                ScanInput(ctrlScan, false), ScanInput(keyScan, false),
                ScanInput(keyScan, true), ScanInput(ctrlScan, true)
            };
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            return sent == inputs.Length;
        }

        private static Native.INPUT ScanInput(ushort scan, bool up)
        {
            return new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = Native.KEYEVENTF_SCANCODE | (up ? Native.KEYEVENTF_KEYUP : 0) } }
            };
        }

        private static Native.INPUT VkInput(ushort vk, bool up)
        {
            return new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
            };
        }

        private static bool SetClipboard(string text, StringBuilder why)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true, 5, 50);
                    if (attempt > 0) why.Append(" clipboard_retries=" + attempt);
                    return true;
                }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        public static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
