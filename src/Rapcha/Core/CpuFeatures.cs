namespace Rapcha.Core
{
    public enum WhisperBuild { None, Sse, Avx2 }

    /// <summary>
    /// v20: what the processor can run, from CPUID (and whether Windows saves AVX registers, XGETBV). Decides which
    /// embedded Whisper engine may be loaded: the AVX2 build needs AVX2 + FMA + F16C with OS support; the SSE build
    /// (no AVX of any kind) needs SSSE3, which every Intel Atom/Celeron/Pentium since 2008 has (the Celeron N3350 does).
    /// With neither, Whisper is not loaded at all and Rapcha says so instead of crashing.
    /// </summary>
    public sealed class CpuFeatures
    {
        public bool Sse2, Sse3, Ssse3, Sse41, Sse42, Avx, Avx2, Fma, F16c, OsAvx;
        public string Name = "";

        public static WhisperBuild Choose(CpuFeatures f)
        {
            if (f == null) return WhisperBuild.None;
            if (f.Avx && f.Avx2 && f.Fma && f.F16c && f.OsAvx && f.Ssse3) return WhisperBuild.Avx2;
            if (f.Sse2 && f.Sse3 && f.Ssse3) return WhisperBuild.Sse;
            return WhisperBuild.None;
        }

        public static string Why(CpuFeatures f) =>
            Choose(f) == WhisperBuild.None ? "Whisper needs a processor with SSE2, SSE3 and SSSE3; this one has " + f : "";

        private static string Y(bool b) => b ? "yes" : "no";

        public override string ToString() =>
            "SSE2=" + Y(Sse2) + " SSE3=" + Y(Sse3) + " SSSE3=" + Y(Ssse3) + " SSE4.1=" + Y(Sse41) + " SSE4.2=" + Y(Sse42) +
            " AVX=" + Y(Avx) + " AVX2=" + Y(Avx2) + " FMA=" + Y(Fma) + " F16C=" + Y(F16c) + " OS-AVX=" + Y(OsAvx);
    }
}
