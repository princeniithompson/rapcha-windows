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
        private static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\Rapcha.Windows.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("Rapcha is already running. Look for the lifebuoy at the edge of your screen.", "Rapcha",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // .NET 4.8 defaults to SystemDefault, which lets Windows pick TLS 1.2 or 1.3.
                // If something forced an explicit list, make sure TLS 1.2 is in it.
                if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Report(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);

                LoadIcons();
                Application.Run(new RapchaApp());
                GC.KeepAlive(mutex);
            }
        }

        private static void LoadIcons()
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                    AppIcon = new Icon(s);
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("Rapcha.rapcha.ico"))
                    SmallIcon = new Icon(s, SystemInformation.SmallIconSize);
            }
            catch
            {
                AppIcon = SmallIcon = SystemIcons.Application;
            }
        }

        private static void Report(Exception ex)
        {
            if (ex == null) return;
            AppStorage.Log("Unhandled: " + ex);
        }
    }
}
