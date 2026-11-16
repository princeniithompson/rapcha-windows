using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// UI Automation helpers, always run on a background MTA thread with a timeout so a slow or
    /// hung app can never freeze Rapcha. The UIA assemblies only load the first time this is used.
    /// </summary>
    /// <summary>The element that has keyboard focus, and whether it is a real text field (see Core/FieldRules).</summary>
    public sealed class FieldInfo
    {
        public bool Found;          // a focused element outside Rapcha
        public bool Editable;       // a text field with keyboard focus and a caret (never a password box)
        public bool Password;
        public bool OwnApp;         // focus is in Rapcha itself (its window, the bubble): keep the last field
        public System.Drawing.Rectangle Bounds; // screen pixels, as reported by the app
        public IntPtr Window;       // the top-level window that has the focus (the paste target)
        public string Type = "";    // UIA control type, e.g. "Edit", "Document"
        public string Framework = "";
        public string Reason = "";  // which rule decided (for the log)
    }

    public static class Uia
    {
        public static bool WarmedUp { get; private set; }

        /// <summary>Loads UI Automation in the background once, so the first field lookup is quick.</summary>
        public static void WarmUp()
        {
            var t = new Thread(() =>
            {
                try { var _ = AutomationElement.FocusedElement; WarmedUp = true; } catch { }
            }) { IsBackground = true, Name = "Rapcha UIA warm-up", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

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
        /// The focused element and whether it is a real editable text field: UIA HasKeyboardFocus, an Edit / a
        /// Document with TextPattern / a writable ValuePattern, and a caret (a Win32 caret, a UIA text selection,
        /// or a Chromium editable element). Containers, the desktop and huge rectangles are rejected.
        /// </summary>
        public static Task<FieldInfo> FocusedFieldAsync(int timeoutMs, StringBuilder why)
        {
            return RunMta(() => Detect(), timeoutMs, why, "uia_focus");
        }

        private static FieldInfo Detect()
        {
            var info = new FieldInfo();
            var facts = new FieldFacts();
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr root = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOT);
            info.Window = root;
            uint fgPid = 0;
            uint fgThread = root == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(root, out fgPid);
            if (fgPid != 0 && fgPid == Native.GetCurrentProcessId()) { info.OwnApp = true; info.Reason = "own_app"; return info; }
            facts.IsDesktop = root == IntPtr.Zero || !TextDelivery.IsUsableTarget(root);

            var el = AutomationElement.FocusedElement;
            WarmedUp = true;
            if (el != null)
            {
                var cur = el.Current;
                if (cur.ProcessId == (int)Native.GetCurrentProcessId()) { info.OwnApp = true; info.Reason = "own_app"; return info; }
                facts.HasFocus = true;
                info.Found = true;
                info.Type = facts.ControlType = cur.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
                info.Framework = facts.FrameworkId = cur.FrameworkId ?? "";
                facts.ClassName = cur.ClassName ?? "";
                facts.HasKeyboardFocus = cur.HasKeyboardFocus;
                info.Password = facts.IsPassword = cur.IsPassword;
                var r = cur.BoundingRectangle;
                if (!r.IsEmpty && !double.IsInfinity(r.Width))
                    info.Bounds = new System.Drawing.Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                facts.Bounds = new IntRect(info.Bounds.X, info.Bounds.Y, info.Bounds.Width, info.Bounds.Height);
                var scr = System.Windows.Forms.Screen.FromRectangle(info.Bounds.Width > 0 ? info.Bounds : new System.Drawing.Rectangle(0, 0, 1, 1)).Bounds;
                facts.Screen = new IntRect(scr.X, scr.Y, scr.Width, scr.Height);
                if (!facts.IsPassword)
                {
                    facts.HasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo);
                    if (facts.HasValue) { try { facts.ValueReadOnly = ((ValuePattern)vpo).Current.IsReadOnly; } catch { facts.ValueReadOnly = true; } }
                    facts.HasText = el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo);
                    if (facts.HasText)
                    {
                        try { facts.TextCaret = ((TextPattern)tpo).GetSelection().Length > 0; } catch { }
                    }
                }
                // A Win32 caret in the focused window's thread (classic edits, rich edits, many editors).
                if (fgThread != 0 && cur.ProcessId == (int)fgPid)
                {
                    var gti = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                    if (Native.GetGUIThreadInfo(fgThread, ref gti)) facts.Win32Caret = gti.hwndCaret != IntPtr.Zero;
                }
            }
            info.Editable = FieldRules.Decide(facts, out var reason);
            info.Reason = reason;
            return info;
        }

        /// <summary>Calls <paramref name="onFocus"/> (on a UIA thread) whenever keyboard focus moves anywhere.</summary>
        public static void StartFocusEvents(Action onFocus)
        {
            var t = new Thread(() =>
            {
                try
                {
                    Automation.AddAutomationFocusChangedEventHandler((s, e) => { try { onFocus(); } catch { } });
                    AppStorage.Log("UIA focus events: on");
                }
                catch (Exception e) { AppStorage.Log("UIA focus events unavailable: " + e.GetType().Name); }
            }) { IsBackground = true, Name = "Rapcha UIA focus events" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public static void StopFocusEvents()
        {
            try { Automation.RemoveAllEventHandlers(); } catch { }
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
