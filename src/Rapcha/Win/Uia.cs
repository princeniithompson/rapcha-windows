using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Rapcha.Win
{
    /// <summary>
    /// UI Automation helpers, always run on a background MTA thread with a timeout so a slow or
    /// hung app can never freeze Rapcha. The UIA assemblies only load the first time this is used.
    /// </summary>
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

        /// <summary>Selected text of the focused element, via TextPattern.GetSelection only.</summary>
        public static Task<string> SelectedTextAsync(int timeoutMs, StringBuilder why)
        {
            return RunMta(() =>
            {
                var el = AutomationElement.FocusedElement;
                // Walk up a few levels: in browsers the focused node may be a child of the document.
                for (int depth = 0; el != null && depth < 6; depth++)
                {
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p))
                    {
                        var ranges = ((TextPattern)p).GetSelection();
                        var sb = new StringBuilder();
                        foreach (var r in ranges)
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(r.GetText(SelectionReader.MaxChars));
                            if (sb.Length >= SelectionReader.MaxChars) break;
                        }
                        why.Append(" uia_ranges=" + ranges.Length);
                        return sb.ToString();
                    }
                    el = TreeWalker.ControlViewWalker.GetParent(el);
                }
                why.Append(" uia_no_textpattern");
                return "";
            }, timeoutMs, why, "uia_select");
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
