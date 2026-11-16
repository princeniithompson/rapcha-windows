using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rapcha.Win
{
    /// <summary>
    /// Is there internet for Gemini? Quick and cheap: Windows' own "no network" is instant; otherwise one TCP
    /// connection to Gemini's host with a short timeout. A known "offline" is remembered for a little while and
    /// forgotten as soon as Windows says the network changed, so Rapcha goes back to Gemini when you're online.
    /// </summary>
    public static class Connectivity
    {
        public const string Host = "generativelanguage.googleapis.com";
        public const int ProbeTimeoutMs = 1500;
        public const int RememberOfflineMs = 20000;

        private static DateTime _offlineSince = DateTime.MinValue;
        private static bool _hooked;

        /// <summary>Self-test: pretend there is no internet (true) or ignore (null).</summary>
        public static bool? ForceOffline;

        public static event Action Changed;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                NetworkChange.NetworkAvailabilityChanged += (s, e) => { _offlineSince = DateTime.MinValue; Changed?.Invoke(); };
                NetworkChange.NetworkAddressChanged += (s, e) => { _offlineSince = DateTime.MinValue; Changed?.Invoke(); };
            }
            catch { }
        }

        /// <summary>Instant: false only when Rapcha knows there is no internet right now.</summary>
        public static bool LikelyOnline
        {
            get
            {
                if (ForceOffline == true) return false;
                try { if (!NetworkInterface.GetIsNetworkAvailable()) return false; } catch { }
                return (DateTime.UtcNow - _offlineSince).TotalMilliseconds > RememberOfflineMs;
            }
        }

        public static void MarkOffline() => _offlineSince = DateTime.UtcNow;
        public static void MarkOnline() => _offlineSince = DateTime.MinValue;

        /// <summary>Can we reach Gemini's host within the timeout? (DNS + TCP connect, no data sent)</summary>
        public static async Task<bool> ProbeAsync(int timeoutMs = ProbeTimeoutMs)
        {
            if (ForceOffline == true) { MarkOffline(); return false; }
            try { if (!NetworkInterface.GetIsNetworkAvailable()) { MarkOffline(); return false; } } catch { }
            using (var c = new TcpClient())
            {
                try
                {
                    var connect = c.ConnectAsync(Host, 443);
                    var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    bool ok = done == connect && !connect.IsFaulted && c.Connected;
                    if (ok) MarkOnline(); else MarkOffline();
                    if (done != connect) _ = connect.ContinueWith(t => { var _ = t.Exception; }, TaskScheduler.Default);
                    return ok;
                }
                catch { MarkOffline(); return false; }
            }
        }
    }
}
