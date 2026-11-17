using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// v19: builds the Diagnostics report (header, devices, microphone now, timeline) and starts the rolling file
    /// %AppData%\Rapcha\diagnostics\diagnostics.log (5 MB x 3). Never API keys; words only with "Include transcribed text".
    /// </summary>
    public static class DiagnosticsInfo
    {
        private static RollingFile _file;
        public static string Folder => Path.Combine(AppStorage.Folder, "diagnostics");
        public static RollingFile File => _file;

        /// <summary>Called once at startup (the real app and the v19 self-test).</summary>
        public static void Start(string why)
        {
            if (_file != null) return;
            _file = new RollingFile(Folder);
            DiagLog.Sink = line => _file.Append(line);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { DiagLog.Error("unobserved task error", e.Exception?.GetBaseException()); };
            DiagLog.Add("start", "Rapcha started (" + why + ")");
            try { foreach (var l in Header().Split('\n')) if (l.Trim().Length > 0) DiagLog.Add("start", l.TrimEnd('\r')); } catch (Exception e) { DiagLog.Error("header", e); }
            try { foreach (var l in Devices().Split('\n')) if (l.Trim().Length > 0) DiagLog.Add("start", l.TrimEnd('\r')); } catch (Exception e) { DiagLog.Error("devices", e); }
        }

        public static string Version => typeof(DiagnosticsInfo).Assembly.GetName().Version.ToString();

        public static string Build
        {
            get
            {
                try
                {
                    var a = typeof(DiagnosticsInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                    int plus = a.IndexOf('+');
                    var sha = plus >= 0 ? a.Substring(plus + 1) : "";
                    var built = System.IO.File.GetLastWriteTime(typeof(DiagnosticsInfo).Assembly.Location);
                    return (sha.Length >= 7 ? "commit " + sha.Substring(0, 7) + ", " : "") + "exe date " + built.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
                catch { return "?"; }
            }
        }

        private static string Reg(string path, string name)
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(path)) return k?.GetValue(name)?.ToString() ?? ""; } catch { return ""; }
        }

        public static string WindowsText()
        {
            const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var product = Reg(nt, "ProductName");
            var build = Reg(nt, "CurrentBuild");
            int b; int.TryParse(build, out b);
            if (b >= 22000 && product.Contains("Windows 10")) product = product.Replace("Windows 10", "Windows 11");
            var display = Reg(nt, "DisplayVersion");
            if (display.Length == 0) display = Reg(nt, "ReleaseId");
            var ubr = Reg(nt, "UBR");
            return product + " " + display + " (build " + build + (ubr.Length > 0 ? "." + ubr : "") + "), edition " + Reg(nt, "EditionID") +
                   ", S mode: " + (DeviceProbe.IsSMode() ? "yes" : "no") + ", " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + " Windows, Rapcha runs " +
                   (Environment.Is64BitProcess ? "64-bit" : "32-bit");
        }

        public static string Header()
        {
            var sb = new StringBuilder();
            var f = DeviceProbe.Facts;
            long priv = -1; TimeSpan up = TimeSpan.Zero;
            try { using (var p = Process.GetCurrentProcess()) { priv = p.PrivateMemorySize64 / (1024 * 1024); up = DateTime.Now - p.StartTime; } } catch { }
            sb.AppendLine("Rapcha for Windows " + Version + " (" + Build + ")");
            sb.AppendLine("Report made: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (PC local time, UTC" + TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).ToString(@"hh\:mm") + ")");
            sb.AppendLine("Windows: " + WindowsText());
            sb.AppendLine("CPU: " + Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim() + ", " + Environment.ProcessorCount + " logical cores, AVX=" + f.Avx + " AVX2=" + f.Avx2);
            try
            {
                var cf = CpuProbe.Features;
                sb.AppendLine("CPU features: " + cf + (CpuProbe.CpuidNote.Length > 0 ? " (" + CpuProbe.CpuidNote + ")" : ""));
                sb.AppendLine("Rapcha process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " on " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + " Windows");
                var wb = WhisperNative.ForceVariant != null ? (WhisperNative.ForceVariant == "avx2" ? WhisperBuild.Avx2 : WhisperBuild.Sse) : CpuFeatures.Choose(cf);
                string v = wb == WhisperBuild.Avx2 ? "avx2" : "sse";
                sb.AppendLine("Whisper build for this CPU: " + (wb == WhisperBuild.None ? "none - " + CpuFeatures.Why(cf) : v + " (" + WhisperNative.BuildFlags(v) + ")") +
                              "; loaded: " + WhisperNative.Status + (WhisperNative.Status == WhisperNative.State.Ready ? " " + WhisperNative.Variant + " system_info=" + WhisperNative.SystemInfo() : ""));
            }
            catch (Exception e) { sb.AppendLine("CPU features: failed " + e.GetType().Name); }
            sb.AppendLine("Mic boost for offline typing: " + MicBoostSetting.Label(AppStorage.LoadMicBoost()));
            sb.AppendLine("Memory: total " + f.TotalRamMb + " MB, free now " + DeviceProbe.FreeRamMb() + " MB; Rapcha uses " + priv + " MB (private bytes); free disk " + DeviceProbe.FreeDiskMb() + " MB");
            sb.AppendLine("Device level (tier): " + DeviceTiers.Name(DeviceProbe.Tier) + (DeviceProbe.Override != null ? " (FAKED for a test)" : ""));
            sb.AppendLine("Rapcha running for: " + (int)up.TotalHours + " h " + up.Minutes + " min");
            sb.AppendLine("Internet: " + (Connectivity.LikelyOnline ? "online" : "offline") + "; offline typing " + (OfflineSettings.Fallback ? "on" : "off"));
            var i = OfflineEngines.Inputs(false);
            var choice = EnginePolicy.ChooseOffline(i);
            var model = WhisperModels.Choose(i.Tier, OfflineSettings.ChosenModel);
            sb.AppendLine("Engine when online: Gemini. Engine when offline: " + (choice.Ok ? choice.Engine.ToString() : "none (" + choice.Why + ")") +
                          " - whisper library " + WhisperNative.Status + ", model " + (model?.Id ?? "none") + " installed=" + i.WhisperModelReady + " fits_memory=" + i.WhisperFitsMemory +
                          ", built-in Windows speech available=" + i.BuiltInAvailable);
            sb.AppendLine("Last dictation engine: " + RapchaApp.LastEngineWhy);
            return sb.ToString();
        }

        public static string Devices()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Microphone privacy: " + PrivacyText());
            var chosen = AppStorage.LoadMicDevice();
            sb.AppendLine("Microphone chosen in Rapcha Settings: " + (chosen.Length == 0 ? "Windows default microphone (WAVE_MAPPER)" : chosen));
            var list = AudioEndpoints.All(out var err);
            sb.AppendLine("Recording devices (Windows sound settings): " + list.Count + (err.Length > 0 ? " (" + err + ")" : ""));
            foreach (var e in list)
                sb.AppendLine("  - " + (e.Name.Length > 0 ? e.Name : "(no name)") + " | " + e.State + (e.IsDefault ? " | DEFAULT" : "") + (e.IsDefaultComms ? " | DEFAULT COMMUNICATIONS" : "") +
                              " | waveIn index " + (e.WaveInIndex >= 0 ? e.WaveInIndex.ToString() : "-") + " | Windows runs it at " + (e.MixFormat.Length > 0 ? e.MixFormat : "?") +
                              " | exclusive mode " + e.Exclusive);
            var wave = WaveInRecorder.ListDevices();
            sb.AppendLine("waveIn devices (what Rapcha records from): " + wave.Count + (wave.Count > 0 ? " - " + string.Join("; ", wave.Select((w, n) => "#" + n + " " + w)) : ""));
            sb.AppendLine("Last voice-typing microphone open: " + WaveInRecorder.LastOpen);
            sb.AppendLine("Rapcha asks for 16000 Hz, 1 channel, 16-bit (shared mode; Windows converts). It never uses exclusive mode.");
            return sb.ToString();
        }

        /// <summary>Windows privacy switches plus what Windows itself records about microphone use (CapabilityAccessManager).</summary>
        public static string PrivacyText()
        {
            const string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            string V(RegistryKey root, string p) { try { using (var k = root.OpenSubKey(p)) return k?.GetValue("Value") as string ?? "not set"; } catch { return "?"; } }
            var sb = new StringBuilder();
            sb.Append("whole PC=" + V(Registry.LocalMachine, basePath) + ", this user=" + V(Registry.CurrentUser, basePath) + ", desktop apps=" + V(Registry.CurrentUser, basePath + @"\NonPackaged"));
            sb.Append(WaveInRecorder.IsMicPrivacyDenied() ? " -> BLOCKED" : " -> allowed");
            try
            {
                using (var np = Registry.CurrentUser.OpenSubKey(basePath + @"\NonPackaged"))
                {
                    if (np != null)
                    {
                        var inUse = new List<string>();
                        string rapcha = "";
                        foreach (var name in np.GetSubKeyNames())
                        {
                            using (var k = np.OpenSubKey(name))
                            {
                                long start = ToLong(k?.GetValue("LastUsedTimeStart")), stop = ToLong(k?.GetValue("LastUsedTimeStop"));
                                var exe = name.Substring(name.LastIndexOf('#') + 1);
                                bool now = start > 0 && stop == 0;
                                if (now) inUse.Add(exe);
                                if (exe.IndexOf("rapcha", StringComparison.OrdinalIgnoreCase) >= 0)
                                    rapcha += (rapcha.Length > 0 ? "; " : "") + exe + " last started " + FileTime(start) + ", last stopped " + (stop == 0 ? "NOT STOPPED (in use)" : FileTime(stop));
                            }
                        }
                        sb.Append("\n  Windows' own record for Rapcha: " + (rapcha.Length > 0 ? rapcha : "none"));
                        sb.Append("\n  Desktop apps Windows says are using the microphone now: " + (inUse.Count > 0 ? string.Join(", ", inUse) : "none"));
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static long ToLong(object o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }
        private static string FileTime(long ft) { try { return ft <= 0 ? "never" : DateTime.FromFileTime(ft).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { return "?"; } }

        /// <summary>The whole report as saved to the .txt file.</summary>
        public static string Report(bool includeText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RAPCHA DIAGNOSTICS");
            sb.AppendLine("This file has no API key. " + (includeText ? "It INCLUDES the words Rapcha heard (you switched this on)." : "It has none of your words."));
            sb.AppendLine();
            sb.AppendLine("== About this PC ==");
            try { sb.Append(Header()); } catch (Exception e) { sb.AppendLine("header failed: " + e.GetType().Name); }
            sb.AppendLine();
            sb.AppendLine("== Microphones ==");
            try { sb.Append(Devices()); } catch (Exception e) { sb.AppendLine("devices failed: " + e.GetType().Name); }
            sb.AppendLine();
            sb.AppendLine("== Windows modern speech recogniser ==");
            try { sb.AppendLine(ModernSpeech.Probe()); } catch (Exception e) { sb.AppendLine("probe failed: " + e.GetType().Name); }
            sb.AppendLine();
            sb.AppendLine("== Microphone right now ==");
            sb.AppendLine(MicTracker.NowText());
            sb.AppendLine("Opened " + MicTracker.Opens + " times and closed " + MicTracker.Closes + " times since Rapcha started.");
            sb.AppendLine();
            var lines = DiagLog.ForExport(includeText);
            sb.AppendLine("== Timeline (" + lines.Count + " lines, oldest first) ==");
            foreach (var l in lines) sb.AppendLine(l);
            return DiagLog.RedactKeys(sb.ToString());
        }
    }
}
