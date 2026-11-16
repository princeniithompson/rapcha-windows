using System;
using System.Runtime.InteropServices;

namespace Rapcha.Win
{
    /// <summary>
    /// A downloaded Rapcha.exe carries the browser's Mark-of-the-Web (the "Zone.Identifier" stream). Windows then
    /// shows "Open File - Security Warning" or SmartScreen before it starts, which looks like an installer
    /// running again. Once the user has chosen to run it, Rapcha removes the mark (like right-click > Properties >
    /// Unblock) so later starts from the Desktop or Start menu shortcut open straight away.
    /// </summary>
    internal static class Unblock
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);

        private const uint InvalidAttributes = 0xFFFFFFFF;

        public static void Self()
        {
            try
            {
                string exe = InstanceManager.CurrentExePath();
                string stream = exe + ":Zone.Identifier";
                if (GetFileAttributesW(stream) == InvalidAttributes) return; // not marked
                bool ok = DeleteFileW(stream);
                AppStorage.Log(ok ? "Startup: removed the downloaded-file mark (Zone.Identifier) so Windows stops showing a security prompt"
                                  : "Startup: could not remove the downloaded-file mark (error " + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception e) { AppStorage.Log("Startup: unblock check failed: " + e.GetType().Name); }
        }
    }
}
