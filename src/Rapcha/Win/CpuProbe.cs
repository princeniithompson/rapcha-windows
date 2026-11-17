using System;
using System.Runtime.InteropServices;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// v20: reads the processor's features before any Whisper code is loaded. Windows' IsProcessorFeaturePresent gives
    /// SSE2..SSE4.2, AVX and AVX2 (with OS support); a tiny CPUID routine adds FMA and F16C. If CPUID can't run, FMA and
    /// F16C are only assumed when AVX2 is there (every Intel and AMD AVX2 processor has them).
    /// </summary>
    public static class CpuProbe
    {
        [DllImport("kernel32.dll")] private static extern bool IsProcessorFeaturePresent(int feature);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint protect, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr addr, UIntPtr size);

        private const int PF_XMMI64 = 10, PF_SSE3 = 13, PF_SSSE3 = 36, PF_SSE4_1 = 37, PF_SSE4_2 = 38, PF_AVX = 39, PF_AVX2 = 40;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CpuidFn(int leaf, int sub, [Out] int[] regs);

        // cdecl void cpuid(int leaf, int subleaf, int* regs4)
        private static readonly byte[] X86 = { 0x55, 0x89, 0xE5, 0x53, 0x57, 0x8B, 0x45, 0x08, 0x8B, 0x4D, 0x0C, 0x0F, 0xA2, 0x8B, 0x7D, 0x10,
                                               0x89, 0x07, 0x89, 0x5F, 0x04, 0x89, 0x4F, 0x08, 0x89, 0x57, 0x0C, 0x5F, 0x5B, 0x5D, 0xC3 };
        // Windows x64: rcx=leaf, edx=subleaf, r8=regs4
        private static readonly byte[] X64 = { 0x53, 0x89, 0xC8, 0x89, 0xD1, 0x0F, 0xA2, 0x41, 0x89, 0x00, 0x41, 0x89, 0x58, 0x04, 0x41, 0x89,
                                               0x48, 0x08, 0x41, 0x89, 0x50, 0x0C, 0x5B, 0xC3 };

        private static CpuFeatures _cached;
        public static string CpuidNote { get; private set; } = "";

        public static CpuFeatures Features => _cached ?? (_cached = Detect());

        public static CpuFeatures Detect()
        {
            var f = new CpuFeatures();
            try
            {
                f.Sse2 = IsProcessorFeaturePresent(PF_XMMI64);
                f.Sse3 = IsProcessorFeaturePresent(PF_SSE3);
                f.Ssse3 = IsProcessorFeaturePresent(PF_SSSE3);
                f.Sse41 = IsProcessorFeaturePresent(PF_SSE4_1);
                f.Sse42 = IsProcessorFeaturePresent(PF_SSE4_2);
                f.Avx = IsProcessorFeaturePresent(PF_AVX);     // also means Windows saves the AVX registers
                f.Avx2 = IsProcessorFeaturePresent(PF_AVX2);
                f.OsAvx = f.Avx;
            }
            catch { }
            var r = Cpuid(1);
            if (r != null)
            {
                int ecx = r[2];
                f.Fma = (ecx & (1 << 12)) != 0;
                f.F16c = (ecx & (1 << 29)) != 0;
                CpuidNote = "cpuid";
                // Older Windows 10 builds may not report SSSE3/SSE4 through IsProcessorFeaturePresent: fill in from CPUID.
                f.Sse3 |= (ecx & 1) != 0;
                f.Ssse3 |= (ecx & (1 << 9)) != 0;
                f.Sse41 |= (ecx & (1 << 19)) != 0;
                f.Sse42 |= (ecx & (1 << 20)) != 0;
                f.Sse2 |= (r[3] & (1 << 26)) != 0;
            }
            else { f.Fma = f.F16c = f.Avx2; CpuidNote = "cpuid unavailable (FMA/F16C assumed from AVX2)"; }
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    f.Name = (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
            }
            catch { }
            return f;
        }

        /// <summary>EAX, EBX, ECX, EDX for a CPUID leaf, or null if it could not be run.</summary>
        public static int[] Cpuid(int leaf, int sub = 0)
        {
            var code = IntPtr.Size == 4 ? X86 : X64;
            IntPtr mem = IntPtr.Zero;
            try
            {
                mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000 /* commit|reserve */, 0x04 /* read-write */);
                if (mem == IntPtr.Zero) return null;
                Marshal.Copy(code, 0, mem, code.Length);
                if (!VirtualProtect(mem, (UIntPtr)4096, 0x20 /* execute-read */, out _)) return null;
                FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)4096);
                var fn = (CpuidFn)Marshal.GetDelegateForFunctionPointer(mem, typeof(CpuidFn));
                var regs = new int[4];
                fn(leaf, sub, regs);
                return regs;
            }
            catch { return null; }
            finally { if (mem != IntPtr.Zero) VirtualFree(mem, UIntPtr.Zero, 0x8000 /* release */); }
        }
    }
}
