using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rapcha.Win
{
    /// <summary>
    /// One Rapcha at a time. Starting the same build again just shows the running one.
    /// Starting a different build (an update) asks the running one to quit, and if it does
    /// not exit within 3 s (v1 and v2 do not listen), closes Rapcha processes by name.
    /// </summary>
    internal static class InstanceManager
    {
        public const string MutexName = @"Local\Rapcha.Windows.SingleInstance";
        public const string QuitEventName = @"Local\Rapcha.Windows.Quit";
        public const string ShowEventName = @"Local\Rapcha.Windows.Show";

        // "Rapcha.exe", plus the browser's duplicate names "Rapcha (1).exe", "Rapcha (2).exe"...
        private static readonly Regex RapchaExeName = new Regex(@"^Rapcha( \(\d+\))?$", RegexOptions.IgnoreCase);

        public static bool AcquireOrHandOff(Mutex mutex)
        {
            if (TryWait(mutex, 0)) return true;

            string myPath = CurrentExePath();
            string myVersion = VersionOf(myPath);
            var others = OtherRapchaProcesses();
            bool sameBuild = others.Length > 0 && others.All(p =>
            {
                var path = PathOf(p);
                return path != null && string.Equals(Path.GetFullPath(path), myPath, StringComparison.OrdinalIgnoreCase) &&
                       VersionOf(path) == myVersion;
            });
            foreach (var p in others) AppStorage.Log("Startup: found running Rapcha pid=" + p.Id + " path=" + (PathOf(p) ?? "?") + " version=" + VersionOf(PathOf(p)));

            if (sameBuild)
            {
                // This launch came from the user (it owns the foreground), so let the running copy take it.
                foreach (var p in others) { try { Native.AllowSetForegroundWindow(p.Id); } catch { } }
                Signal(ShowEventName);
                AppStorage.Log("Startup: same build already running; asked it to show the main window");
                return false;
            }

            AppStorage.Log("Startup: a different Rapcha is running; asking it to quit");
            Signal(QuitEventName);
            if (TryWait(mutex, 3000)) return true;

            foreach (var p in OtherRapchaProcesses())
            {
                try
                {
                    AppStorage.Log("Startup: closing old Rapcha pid=" + p.Id);
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch (Exception e) { AppStorage.Log("Startup: could not close pid=" + p.Id + ": " + e.Message); }
            }
            if (TryWait(mutex, 3000)) return true;
            AppStorage.Log("Startup: old Rapcha still holds the lock; starting anyway");
            return true; // never leave the user with nothing; a stale lock must not block the update
        }

        /// <summary>Background listener in the running app.</summary>
        public static void Listen(Action onQuit, Action onShow)
        {
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(new WaitHandle[] { quit, show });
                    if (i == 0) { onQuit(); return; }
                    onShow();
                }
            }) { IsBackground = true, Name = "Rapcha instance listener" };
            t.Start();
        }

        private static bool TryWait(Mutex m, int ms)
        {
            try { return m.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; } // previous owner was killed: we own it now
        }

        private static void Signal(string name)
        {
            try
            {
                using (var e = new EventWaitHandle(false, EventResetMode.AutoReset, name)) e.Set();
            }
            catch (Exception ex) { AppStorage.Log("Startup: signal failed: " + ex.Message); }
        }

        private static Process[] OtherRapchaProcesses()
        {
            int me = Process.GetCurrentProcess().Id;
            return Process.GetProcesses().Where(p =>
            {
                try
                {
                    if (p.Id == me || !RapchaExeName.IsMatch(p.ProcessName)) return false;
                    var path = PathOf(p);
                    if (path == null) return p.ProcessName.Equals("Rapcha", StringComparison.OrdinalIgnoreCase);
                    var info = FileVersionInfo.GetVersionInfo(path);
                    // Only our app: product "Rapcha" (v1+ set this), never some other program with a similar name.
                    return string.Equals(info.ProductName, "Rapcha", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }).ToArray();
        }

        private static string PathOf(Process p)
        {
            try { return p.MainModule.FileName; } catch { return null; }
        }

        private static string VersionOf(string path)
        {
            try { return path == null ? "?" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?"; } catch { return "?"; }
        }

        public static string CurrentExePath() => Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
    }
}
