using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Rapcha.Win
{
    /// <summary>Reading speed (Windows voice Rate) and removal of the old Piper voice from v5/v6.</summary>
    public static class VoiceSettings
    {
        private static double _speed = double.NaN;
        private static string SpeedPath => Path.Combine(AppStorage.Folder, "voice-speed.txt");

        /// <summary>While Settings → Select to speak is being edited: the unsaved speed (Preview uses it).</summary>
        public static double? Override;

        public const double DefaultSpeed = 1.0;

        /// <summary>0.7x to 1.5x; mapped to SAPI Rate -3..+5 (Rate 0 = normal).</summary>
        public static double Speed
        {
            get
            {
                if (Override.HasValue) return Clamp(Override.Value);
                if (double.IsNaN(_speed))
                {
                    _speed = 1.0;
                    try
                    {
                        if (File.Exists(SpeedPath) && double.TryParse(File.ReadAllText(SpeedPath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _speed = Clamp(v);
                    }
                    catch { }
                }
                return _speed;
            }
            set
            {
                _speed = Clamp(value);
                try { File.WriteAllText(SpeedPath, _speed.ToString("0.00", CultureInfo.InvariantCulture)); } catch { }
            }
        }

        private static bool? _badges;
        private static string BadgesPath => Path.Combine(AppStorage.Folder, "number-badges.txt");
        public const bool DefaultShowBadges = true;

        /// <summary>Select to speak: number badges (1, 2, 3) beside the outlined blocks. Saved; on by default.</summary>
        public static bool ShowBadges
        {
            get
            {
                if (_badges.HasValue) return _badges.Value;
                bool v = DefaultShowBadges;
                try { if (File.Exists(BadgesPath)) v = File.ReadAllText(BadgesPath).Trim() != "0"; } catch { }
                _badges = v;
                return v;
            }
            set
            {
                _badges = value;
                try { File.WriteAllText(BadgesPath, value ? "1" : "0"); } catch { }
            }
        }

        /// <summary>Forget the cached value (the self-test checks what was saved on disk).</summary>
        internal static void ReloadBadges() => _badges = null;

        public static int SapiRate => Math.Max(-10, Math.Min(10, (int)Math.Round((Speed - 1.0) * 10)));

        private static double Clamp(double v) => Math.Max(0.7, Math.Min(1.5, v));

        public static string PiperFolder => Path.Combine(AppStorage.Folder, "piper");

        /// <summary>
        /// Piper is gone: stop any piper.exe that runs from Rapcha's folder and delete %AppData%\Rapcha\piper.
        /// Runs in the background at startup; logs what it did.
        /// </summary>
        public static Task RemovePiperAsync()
        {
            return Task.Run(() =>
            {
                string folder = PiperFolder;
                int killed = 0;
                try
                {
                    string prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                    foreach (var p in Process.GetProcessesByName("piper"))
                    {
                        try
                        {
                            var path = ImagePath(p);
                            if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(2000); killed++; }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
                if (!Directory.Exists(folder)) return;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(folder, true);
                        AppStorage.Log("Old Piper voice removed (processes stopped=" + killed + ")");
                        return;
                    }
                    catch (Exception e)
                    {
                        if (attempt == 4) AppStorage.Log("Could not remove the old Piper folder yet: " + e.GetType().Name);
                        System.Threading.Thread.Sleep(600);
                    }
                }
            });
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>Full exe path of another process, also for 64-bit processes seen from 32-bit Rapcha.</summary>
        private static string ImagePath(Process p)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }
    }
}
