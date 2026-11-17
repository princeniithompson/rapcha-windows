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
            Unblock.Self(); // drop the browser's "downloaded from the internet" mark so Windows stops warning on every start
            if (args.Length >= 1 && args[0] == "--selftest-unblock") return;
            if (args.Length >= 1 && args[0] == "--selftest-ring")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(selfTestRing: true));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-main")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new MainSelfTest(args[1]));
                return;
            }
            if (args.Length >= 1 && args[0] == "--testform")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new FieldTestForms());
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v11")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V11SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v18")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(selfTestV18: args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v17")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(selfTestV17: args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v16")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V16SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v15")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V15SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v14")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V14SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v13")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V13SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-v12")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new V12SelfTest(args[1]));
                return;
            }
            if (args.Length >= 2 && args[0] == "--selftest-paste") { Environment.ExitCode = SelfTest.Paste(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--selftest-ocr")
            {
                // Runs beside any other copy (no single-instance hand-off, no shortcuts).
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Report(e.Exception); Environment.Exit(1); };
                LoadIcons();
                Application.Run(new RapchaApp(selfTestOcr: args[1]));
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
                // Every manual launch (double-click, Desktop/Start shortcut, a downloaded "Rapcha (4).exe") ends with the
                // main window visible. "--autostart" (for a future sign-in start) stays quiet with just the ring.
                bool autostart = Array.IndexOf(args, "--autostart") >= 0;
                Application.Run(new RapchaApp(showMain: !autostart));
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
