using System;
using System.IO;
using System.Runtime.InteropServices;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Measures the laptop at startup: installed and free memory, cores, free disk, AVX/AVX2. The self-test (and
    /// RAPCHA_FAKE_DEVICE="ram=4096,free=1500,cores=4,disk=20000") can fake the values.
    /// </summary>
    public static class DeviceProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);
        [DllImport("kernel32.dll")] private static extern bool IsProcessorFeaturePresent(int feature);
        private const int PF_AVX = 39, PF_AVX2 = 40;

        private static DeviceFacts _facts;
        private static DeviceFacts _override;

        /// <summary>Self-test: pretend to be another laptop (null = measure this one).</summary>
        public static DeviceFacts Override
        {
            get => _override;
            set { _override = value; Changed?.Invoke(); }
        }

        public static event Action Changed;

        public static DeviceFacts Facts
        {
            get
            {
                if (_override != null) return _override;
                if (_facts == null)
                {
                    _facts = DeviceFacts.Parse(Environment.GetEnvironmentVariable("RAPCHA_FAKE_DEVICE")) ?? Measure();
                }
                return _facts;
            }
        }

        public static DeviceTier Tier => DeviceTiers.Classify(Facts);

        /// <summary>Free memory right now (MB); the fake value when faking.</summary>
        public static long FreeRamMb()
        {
            if (_override != null) return _override.FreeRamMb;
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.ullAvailPhys / (1024 * 1024)) : -1;
        }

        public static long FreeDiskMb()
        {
            if (_override != null) return _override.FreeDiskMb;
            try { return new DriveInfo(Path.GetPathRoot(AppStorage.Folder)).AvailableFreeSpace / (1024 * 1024); } catch { return -1; }
        }

        public static DeviceFacts Measure()
        {
            var f = new DeviceFacts { Cores = Environment.ProcessorCount };
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m))
            {
                f.TotalRamMb = (long)(m.ullTotalPhys / (1024 * 1024));
                f.FreeRamMb = (long)(m.ullAvailPhys / (1024 * 1024));
            }
            try { if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0) f.TotalRamMb = Math.Max(f.TotalRamMb, (long)(kb / 1024)); } catch { }
            try { f.FreeDiskMb = new DriveInfo(Path.GetPathRoot(AppStorage.Folder)).AvailableFreeSpace / (1024 * 1024); } catch { f.FreeDiskMb = -1; }
            try { f.Avx = IsProcessorFeaturePresent(PF_AVX); f.Avx2 = IsProcessorFeaturePresent(PF_AVX2); } catch { }
            return f;
        }

        /// <summary>Windows in S mode (only Store apps and Microsoft-signed code may run).</summary>
        public static bool IsSMode()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
                {
                    var v = k?.GetValue("SkuPolicyRequired");
                    return v is int i && i == 1;
                }
            }
            catch { return false; }
        }
    }
}
