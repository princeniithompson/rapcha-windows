using System;
using System.IO;
using System.Reflection;

namespace Rapcha.Win
{
    /// <summary>
    /// Desktop and Start Menu shortcuts named "Rapcha" (with the lifebuoy icon). Created on the first run;
    /// afterwards existing ones are only re-pointed if the exe was moved, so a deleted shortcut stays deleted.
    /// </summary>
    internal static class Shortcuts
    {
        // v1–v10 used shortcuts.txt as this flag; from v11 that file holds the keyboard shortcuts (Settings → Shortcuts).
        private static string FlagPath => Path.Combine(AppStorage.Folder, "desktop-shortcut.txt");
        private static string OldFlagPath => Path.Combine(AppStorage.Folder, "shortcuts.txt");

        public static void Ensure()
        {
            try
            {
                string exe = InstanceManager.CurrentExePath();
                bool firstRun = !File.Exists(FlagPath) && !File.Exists(OldFlagPath);
                var places = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Rapcha.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Rapcha.lnk")
                };
                foreach (var lnk in places)
                {
                    bool exists = File.Exists(lnk);
                    if (!exists && !firstRun) continue;
                    if (exists && string.Equals(TargetOf(lnk), exe, StringComparison.OrdinalIgnoreCase)) continue;
                    Write(lnk, exe);
                    AppStorage.Log("Shortcut " + (exists ? "updated" : "created") + ": " + lnk);
                }
                if (!File.Exists(FlagPath)) File.WriteAllText(FlagPath, DateTime.Now.ToString("s"));
            }
            catch (Exception e)
            {
                AppStorage.Log("Shortcuts failed: " + e.Message);
            }
        }

        private static object Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));

        private static string TargetOf(string lnk)
        {
            var sc = Invoke(Shell(), "CreateShortcut", lnk);
            return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
        }

        private static void Write(string lnk, string exe)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var sc = Invoke(Shell(), "CreateShortcut", lnk);
            Set(sc, "TargetPath", exe);
            Set(sc, "WorkingDirectory", Path.GetDirectoryName(exe));
            Set(sc, "IconLocation", exe + ",0");
            Set(sc, "Description", "Rapcha - voice typing for Windows");
            sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        private static object Invoke(object o, string method, params object[] args) =>
            o.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, o, args);

        private static void Set(object o, string prop, object value) =>
            o.GetType().InvokeMember(prop, BindingFlags.SetProperty, null, o, new[] { value });
    }
}
