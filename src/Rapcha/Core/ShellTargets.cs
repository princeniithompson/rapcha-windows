using System;

namespace Rapcha.Core
{
    /// <summary>What kind of window has the focus, for voice typing.</summary>
    public enum ShellSurface
    {
        /// <summary>A normal app window.</summary>
        None,
        /// <summary>The Start menu or Windows Search (StartMenuExperienceHost / SearchHost / SearchApp / SearchUI).</summary>
        StartSearch,
        /// <summary>The desktop, the taskbar, Action Center and other shell flyouts: no text field to type into.</summary>
        OtherShell,
    }

    /// <summary>
    /// Start and Search run as their own small apps (StartMenuExperienceHost.exe, SearchHost.exe on Windows 11,
    /// SearchApp.exe / SearchUI.exe on Windows 10) in a "Windows.UI.Core.CoreWindow". Older Rapcha treated every
    /// CoreWindow as the desktop, so the Start search box was never a target. Now Start and Search are targets;
    /// the desktop, taskbar and other flyouts still are not.
    /// </summary>
    public static class ShellTargets
    {
        private static readonly string[] StartSearchExes = { "startmenuexperiencehost", "searchhost", "searchapp", "searchui" };
        private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland" };

        /// <summary>"C:\Windows\...\SearchHost.exe" or "SearchHost" → "searchhost".</summary>
        public static string ExeName(string pathOrName)
        {
            var s = (pathOrName ?? "").Trim();
            int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }

        public static bool IsStartOrSearchExe(string pathOrName) => Array.IndexOf(StartSearchExes, ExeName(pathOrName)) >= 0;

        public static ShellSurface Classify(string rootClass, string exePathOrName)
        {
            rootClass = rootClass ?? "";
            bool shellClass = Array.IndexOf(ShellClasses, rootClass) >= 0;
            // The taskbar itself is never the target, even though explorer draws a search box on it (typing there opens Search).
            if (rootClass == "Shell_TrayWnd" || rootClass == "Shell_SecondaryTrayWnd" || rootClass == "Progman" || rootClass == "WorkerW") return ShellSurface.OtherShell;
            if (IsStartOrSearchExe(exePathOrName)) return ShellSurface.StartSearch;
            return shellClass ? ShellSurface.OtherShell : ShellSurface.None;
        }

        /// <summary>
        /// The Start search box is one line, and Enter there opens the top result. Words are typed as one line:
        /// line breaks become spaces.
        /// </summary>
        public static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new System.Text.StringBuilder(text.Length);
            bool space = false;
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Should Rapcha move the ring and words box out from under a Start / Search flyout? Only when the flyout
        /// leaves room (it covers less than 85% of the work area); a full-screen Start can't be avoided.
        /// </summary>
        public static bool CanAvoid(IntRect work, IntRect flyout)
        {
            long wa = (long)Math.Max(1, work.Width) * Math.Max(1, work.Height);
            int x1 = Math.Max(work.X, flyout.X), y1 = Math.Max(work.Y, flyout.Y);
            int x2 = Math.Min(work.Right, flyout.Right), y2 = Math.Min(work.Bottom, flyout.Bottom);
            long inter = (long)Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            return inter > 0 && inter < wa * 0.85;
        }
    }
}
