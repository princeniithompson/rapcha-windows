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

        public sealed class Selection { public string Text = ""; public bool HasTextPattern; }

        /// <summary>Selected text of the focused element, via TextPattern.GetSelection only.</summary>
        public static async Task<Selection> SelectedTextAsync(int timeoutMs, StringBuilder why)
        {
            var sel = new Selection();
            string text = await RunMta(() =>
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
                        sel.HasTextPattern = true;
                        return sb.ToString();
                    }
                    el = TreeWalker.ControlViewWalker.GetParent(el);
                }
                why.Append(" uia_no_textpattern");
                return "";
            }, timeoutMs, why, "uia_select");
            sel.Text = text ?? "";
            return sel;
        }

        public sealed class PointRead
        {
            public string Text = "";
            public string Source = "none";
            public string ControlType = "";
            public bool Own;
        }

        /// <summary>
        /// What to read for a click at a screen point (physical pixels): the element under it and up to three
        /// parents, plus TextPattern.RangeFromPoint expanded to the paragraph (or line) for documents and
        /// web pages. The choice itself is made by PointText.Choose.
        /// </summary>
        public static async Task<PointRead> TextAtPointAsync(int x, int y, int timeoutMs, StringBuilder why)
        {
            var result = await RunMta(() =>
            {
                var r = new PointRead();
                var pt = new System.Windows.Point(x, y);
                var el = AutomationElement.FromPoint(pt);
                if (el == null) { why.Append(" uia_no_element"); return r; }
                try { r.Own = el.Current.ProcessId == System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
                if (r.Own) { r.Source = "own"; return r; }

                var chain = new System.Collections.Generic.List<Rapcha.Core.PointText.Node>();
                var walker = TreeWalker.ControlViewWalker;
                var cur = el;
                for (int i = 0; cur != null && i < 4; i++)
                {
                    chain.Add(NodeOf(cur));
                    try { cur = walker.GetParent(cur); } catch { cur = null; }
                    if (cur != null && cur == AutomationElement.RootElement) break;
                }
                r.ControlType = chain.Count > 0 ? chain[0].ControlType : "";

                string range = null;
                if (chain.Count > 0 && !chain[0].IsPassword)
                {
                    cur = el;
                    for (int depth = 0; cur != null && depth < 12; depth++)
                    {
                        if (cur.TryGetCurrentPattern(TextPattern.Pattern, out var p))
                        {
                            try
                            {
                                var tr = ((TextPattern)p).RangeFromPoint(pt);
                                var para = tr.Clone();
                                para.ExpandToEnclosingUnit(TextUnit.Paragraph);
                                range = para.GetText(Rapcha.Core.PointText.MaxChars + 1);
                                if (string.IsNullOrWhiteSpace(range) || range.Length > 1500)
                                {
                                    var line = tr.Clone();
                                    line.ExpandToEnclosingUnit(TextUnit.Line);
                                    var l = line.GetText(Rapcha.Core.PointText.MaxChars + 1);
                                    if (!string.IsNullOrWhiteSpace(l)) range = l;
                                }
                                why.Append(" uia_range_depth=" + depth);
                            }
                            catch (Exception e) { why.Append(" uia_range_error=" + e.GetType().Name); }
                            break;
                        }
                        try { cur = walker.GetParent(cur); } catch { cur = null; }
                    }
                }
                var choice = Rapcha.Core.PointText.Choose(chain, range);
                r.Text = choice.Text;
                r.Source = choice.Source;
                return r;
            }, timeoutMs, why, "uia_point");
            return result ?? new PointRead { Source = "timeout" };
        }

        private static Rapcha.Core.PointText.Node NodeOf(AutomationElement e)
        {
            var n = new Rapcha.Core.PointText.Node();
            try
            {
                var c = e.Current;
                n.ControlType = (c.ControlType?.ProgrammaticName ?? "").Replace("ControlType.", "");
                n.Name = c.Name ?? "";
                n.IsPassword = c.IsPassword;
                var b = c.BoundingRectangle;
                n.Area = b.IsEmpty ? 0 : b.Width * b.Height;
                if (!n.IsPassword && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    n.Value = ((ValuePattern)vp).Current.Value ?? "";
            }
            catch { }
            return n;
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
