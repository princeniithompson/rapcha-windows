using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Rapcha.Win
{
    internal static class Program
    {
        public static Icon AppIcon { get; private set; }
        public static Icon SmallIcon { get; private set; }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--selftest-paste") { Environment.ExitCode = SelfTest.Paste(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--selftest-piper")
            {
                try { Environment.ExitCode = SelfTest.Piper(args[1]); }
                catch (Exception e) { AppStorage.Log("Selftest piper: " + e); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-click")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(selfTestClick: args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-speak")
            {
                // Runs beside any other copy (no single-instance hand-off, no shortcuts).
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(args[1]));
                return;
            }

            // Same mutex name as v1/v2, so an old copy is always detected.
            var mutex = new Mutex(false, InstanceManager.MutexName);
            bool owned = false;
            try
            {
                owned = InstanceManager.AcquireOrHandOff(mutex);
                if (!owned) return; // same build already running: it was asked to show itself

                // .NET 4.8 defaults to SystemDefault, which lets Windows pick TLS 1.2 or 1.3.
                // If something forced an explicit list, make sure TLS 1.2 is in it.
                if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Report(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);

                LoadIcons();
                Shortcuts.Ensure();
                Application.Run(new RapchaApp());
            }
            finally
            {
                if (owned) { try { mutex.ReleaseMutex(); } catch { } }
                mutex.Dispose();
            }
        }

        private static void LoadIcons()
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                    AppIcon = new Icon(s, 32, 32);
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                    SmallIcon = new Icon(s, SystemInformation.SmallIconSize);
            }
            catch (Exception e)
            {
                AppStorage.Log("Embedded icon failed (" + e.Message + "), using the exe icon");
                try { AppIcon = SmallIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { AppIcon = SmallIcon = SystemIcons.Application; }
            }
        }

        private static void Report(Exception ex)
        {
            if (ex == null) return;
            AppStorage.Log("Unhandled: " + ex);
        }
    }
}
