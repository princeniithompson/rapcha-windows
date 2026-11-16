using System;
using System.Globalization;

namespace Rapcha.Core
{
    /// <summary>How much this laptop can do offline. Low: under 6 GB of memory; Medium: 6 to 8 GB; Strong: over 8 GB.</summary>
    public enum DeviceTier { Low, Medium, Strong }

    /// <summary>What Rapcha measures at startup (numbers only; never logged with anything else).</summary>
    public sealed class DeviceFacts
    {
        public long TotalRamMb;     // installed memory
        public long FreeRamMb;      // available right now
        public int Cores;
        public long FreeDiskMb;     // on the drive that holds %AppData%
        public bool Avx, Avx2;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "ram_total_mb={0} ram_free_mb={1} cores={2} disk_free_mb={3} avx={4} avx2={5}", TotalRamMb, FreeRamMb, Cores, FreeDiskMb, Avx, Avx2);

        /// <summary>
        /// Test override, e.g. "ram=4096,free=1500,cores=4,disk=20000" (missing values: ram 8192, free half, cores 4,
        /// disk 50000, avx2 on). Returns null for an empty or unreadable text.
        /// </summary>
        public static DeviceFacts Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var f = new DeviceFacts { TotalRamMb = 8192, FreeRamMb = -1, Cores = 4, FreeDiskMb = 50000, Avx = true, Avx2 = true };
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return null;
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "ram": f.TotalRamMb = v; break;
                    case "free": f.FreeRamMb = v; break;
                    case "cores": f.Cores = (int)v; break;
                    case "disk": f.FreeDiskMb = v; break;
                    case "avx": f.Avx = v != 0; break;
                    case "avx2": f.Avx2 = v != 0; break;
                    default: return null;
                }
            }
            if (f.FreeRamMb < 0) f.FreeRamMb = f.TotalRamMb / 2;
            return f;
        }
    }

    public static class DeviceTiers
    {
        public const long LowBelowMb = 6 * 1024;      // under 6 GB: Low
        public const long StrongAboveMb = 8 * 1024;   // over 8 GB: Strong

        /// <summary>
        /// Installed memory decides. A machine sold as 8 GB can report a little less (memory kept for graphics), so
        /// the total is rounded to the nearest whole GB first: 7.8 GB counts as 8 (Medium), 5.9 GB as 6 (Medium).
        /// </summary>
        public static DeviceTier Classify(DeviceFacts f)
        {
            if (f == null) return DeviceTier.Low;
            long gb = (long)Math.Round(f.TotalRamMb / 1024.0, MidpointRounding.AwayFromZero);
            if (gb * 1024 < LowBelowMb) return DeviceTier.Low;
            if (gb * 1024 > StrongAboveMb) return DeviceTier.Strong;
            return DeviceTier.Medium;
        }

        public static string Name(DeviceTier t) => t == DeviceTier.Low ? "Low" : t == DeviceTier.Medium ? "Medium" : "Strong";

        /// <summary>"4 GB memory, 4 cores, 21 GB free" for Settings.</summary>
        public static string Describe(DeviceFacts f)
        {
            if (f == null) return "";
            long gb = (long)Math.Round(f.TotalRamMb / 1024.0, MidpointRounding.AwayFromZero);
            long disk = f.FreeDiskMb / 1024;
            return gb + " GB memory, " + f.Cores + (f.Cores == 1 ? " core, " : " cores, ") + disk + " GB free on the disk";
        }
    }
}
