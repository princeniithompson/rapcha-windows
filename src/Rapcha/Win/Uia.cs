using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Rapcha.Win
{
    /// <summary>
    /// UI Automation helpers, always run on a background MTA thread with a timeout so a slow or
    /// hung app can never freeze Rapcha. The UIA assemblies only load the first time this is used.
    /// </summary>
    /// <summary>The element that had keyboard focus when voice typing started.</summary>
    public sealed class FieldInfo
    {
        public bool Found;          // a focused element in the target app
        public bool Editable;       // a text field we can type into (never a password box)
        public bool Password;
        public System.Drawing.Rectangle Bounds; // screen pixels, as reported by the app
        public string Type = "";    // UIA control type, e.g. "Edit", "Document"
        public string Reason = "";  // which rule decided (for the log)
    }

    public static class Uia
    {
        private static Task<T> RunMta<T>(Func<T> work, int timeoutMs, StringBuilder why, string what)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception e) { why.Append(" " + what + "_error=" + e.GetType().Name); tcs.TrySetResult(default(T)); }
            }) { IsBackground = true, Name = "Rapcha UIA" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(done =>
            {
                if (done.Result == tcs.Task) return tcs.Task.Result;
                why.Append(" " + what + "_timeout");
                return default(T);
            });
        }

        /// <summary>
        /// The focused element, if it belongs to the target window's process, and whether it is an editable text
        /// field: an Edit or Document, or anything with a writable ValuePattern, or a TextPattern element with
        /// keyboard focus. Read-only values (a browser page, a label) and password boxes do not count.
        /// </summary>
        public static Task<FieldInfo> FocusedFieldAsync(IntPtr target, int timeoutMs, StringBuilder why)
        {
            return RunMta(() =>
            {
                var info = new FieldInfo();
                var el = AutomationElement.FocusedElement;
                if (el == null) { info.Reason = "no_focus"; return info; }
                var cur = el.Current;
                if (target != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(target, out var pid);
                    if (pid != 0 && cur.ProcessId != (int)pid) { info.Reason = "focus_in_other_app"; return info; }
                }
                info.Found = true;
                info.Type = cur.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
                info.Password = cur.IsPassword;
                var r = cur.BoundingRectangle;
                if (!r.IsEmpty && !double.IsInfinity(r.Width))
                    info.Bounds = new System.Drawing.Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                if (info.Password) { info.Reason = "password"; return info; }

                bool hasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo);
                bool readOnly = hasValue && ((ValuePattern)vpo).Current.IsReadOnly;
                bool hasText = el.TryGetCurrentPattern(TextPattern.Pattern, out _);
                var ct = cur.ControlType;
                if (hasValue && !readOnly) { info.Editable = true; info.Reason = "value_writable"; }
                else if (hasValue && readOnly) { info.Reason = "value_readonly"; }
                else if (ct == ControlType.Edit) { info.Editable = true; info.Reason = "edit"; }
                else if (ct == ControlType.Document && (hasText || cur.HasKeyboardFocus)) { info.Editable = true; info.Reason = "document"; }
                else if (hasText && cur.HasKeyboardFocus) { info.Editable = true; info.Reason = "text_focus"; }
                else info.Reason = "not_text";
                return info;
            }, timeoutMs, why, "uia_focus");
        }

        /// <summary>
        /// Fallback insert: only into an EMPTY, writable field (ValuePattern.SetValue replaces the whole value,
        /// so it is never used on a field that already has text).
        /// </summary>
        public static Task<bool> SetValueIfEmptyAsync(IntPtr focusHwnd, string text, int timeoutMs, StringBuilder why)
        {
            return RunMta(() =>
            {
                if (focusHwnd == IntPtr.Zero) { why.Append(" uia_no_focus_hwnd"); return false; }
                var el = AutomationElement.FromHandle(focusHwnd);
                if (el == null || !el.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) { why.Append(" uia_no_valuepattern"); return false; }
                var vp = (ValuePattern)p;
                if (vp.Current.IsReadOnly) { why.Append(" uia_readonly"); return false; }
                if (!string.IsNullOrEmpty(vp.Current.Value)) { why.Append(" uia_field_not_empty"); return false; }
                vp.SetValue(text);
                why.Append(" uia_setvalue_ok");
                return true;
            }, timeoutMs, why, "uia_setvalue");
        }
    }
}
