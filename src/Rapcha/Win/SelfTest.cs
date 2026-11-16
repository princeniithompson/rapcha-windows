using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Rapcha.Win
{
    /// <summary>CI-only checks. Nothing here runs in normal use.</summary>
    internal static class SelfTest
    {
        /// <summary>
        /// --selftest-paste "text": pastes into an open Notepad through the real delivery path, then reads
        /// Notepad's text back. 0 = typed and found, 3 = only copied, 1 = failed / not found.
        /// </summary>
        public static int Paste(string text)
        {
            IntPtr notepad = IntPtr.Zero;
            foreach (var p in Process.GetProcessesByName("notepad"))
                if (p.MainWindowHandle != IntPtr.Zero) { notepad = p.MainWindowHandle; break; }
            if (notepad == IntPtr.Zero) { AppStorage.Log("Selftest paste: no Notepad window"); return 1; }

            IntPtr edit = FindEditor(notepad);
            var result = TextDelivery.Deliver(text, notepad);
            Thread.Sleep(800);
            string back = edit != IntPtr.Zero ? GetText(edit) : "";
            bool found = back.Contains(text);
            AppStorage.Log("Selftest paste: result=" + result + " editor=" + (edit != IntPtr.Zero ? TextDelivery.ClassOf(edit) : "none") +
                           " found=" + found + " reason=" + TextDelivery.LastReason);
            if (result == TextDelivery.Result.Typed && found) return 0;
            return result == TextDelivery.Result.Copied ? 3 : 1;
        }

        internal static IntPtr FindEditor(IntPtr top)
        {
            foreach (var cls in new[] { "Edit", "RichEditD2DPT", "RICHEDIT50W" })
            {
                var h = Native.FindWindowEx(top, IntPtr.Zero, cls, null);
                if (h != IntPtr.Zero) return h;
            }
            // Windows 11 Notepad nests the editor one level down.
            for (IntPtr child = Native.FindWindowEx(top, IntPtr.Zero, null, null); child != IntPtr.Zero; child = Native.FindWindowEx(top, child, null, null))
            {
                var h = Native.FindWindowEx(child, IntPtr.Zero, "RichEditD2DPT", null);
                if (h != IntPtr.Zero) return h;
            }
            return IntPtr.Zero;
        }

        internal static string GetText(IntPtr hwnd)
        {
            int len = (int)Native.SendMessage(hwnd, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            var sb = new StringBuilder(Math.Max(1, len + 1));
            Native.SendMessage(hwnd, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }
    }
}
