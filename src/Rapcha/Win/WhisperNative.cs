using System;
using Rapcha.Core;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Rapcha.Win
{
    /// <summary>
    /// The whisper.cpp engine inside Rapcha.exe. The release build embeds two 32-bit builds of one self-contained
    /// DLL (rapcha-whisper.dll: whisper.cpp + ggml, static C runtime): one for CPUs with AVX2, one SSE-only. On first
    /// use the right one is written to %AppData%\Rapcha\engine\&lt;hash&gt;\ and loaded with LoadLibrary; nothing else is
    /// installed and no other program is started. Windows in S mode (and other code-integrity policies) refuses
    /// unsigned DLLs: that shows up as a load error here, and Rapcha says so and uses built-in Windows speech.
    /// </summary>
    public static class WhisperNative
    {
        public enum State { NotTried, Ready, NotBundled, Blocked, Failed, CpuUnsupported }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InfoFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate IntPtr LoadFn([MarshalAs(UnmanagedType.LPWStr)] string path, int threads);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TranscribeFn(IntPtr ctx, float[] samples, int n, int threads, byte[] prompt, byte[] lang, byte[] output, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StatsFn([Out] float[] output, int cap);
        private static StatsFn _stats;

        private static readonly object Gate = new object();
        private static IntPtr _module;
        private static LoadFn _load;
        private static TranscribeFn _transcribe;
        private static FreeFn _free;
        private static InfoFn _info;

        public static State Status { get; private set; } = State.NotTried;
        public static int LastError { get; private set; }
        public static string Variant { get; private set; } = "";
        /// <summary>Self-test: act as if Windows refused the DLL (S mode).</summary>
        public static bool SimulateBlocked;

        public const int ERROR_INVALID_IMAGE_HASH = 577;            // unsigned code refused by code integrity
        public const int ERROR_ACCESS_DISABLED_BY_POLICY = 1260;
        public const int ERROR_SYSTEM_INTEGRITY_POLICY = 4551;      // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION
        public const int ERROR_DRIVER_BLOCKED = 1275;

        public static bool IsPolicyError(int err) =>
            err == ERROR_INVALID_IMAGE_HASH || err == ERROR_ACCESS_DISABLED_BY_POLICY || err == ERROR_SYSTEM_INTEGRITY_POLICY || err == ERROR_DRIVER_BLOCKED ||
            err == 4552 /* ERROR_SYSTEM_INTEGRITY_ROLLBACK_DETECTED */ || err == 4553 /* POLICY_SIGNATURE */ || err == 4556 /* INVALID_POLICY */;

        /// <summary>v20: self-test only: load this build ("sse" or "avx2") whatever the processor says.</summary>
        public static string ForceVariant;
        public static string CpuMessage => "This processor can't run Whisper (it needs SSE2, SSE3 and SSSE3), so built-in Windows speech is used.";

        /// <summary>v20: what was compiled into each embedded build (for Diagnostics). Both are 32-bit, static, no OpenMP, no GPU.</summary>
        public static string BuildFlags(string variant) => variant == "avx2"
            ? "avx2 build: /arch:AVX2 with AVX, AVX2, FMA, F16C (only loaded when the processor has all of them)"
            : "sse build: /arch:SSE2 plus SSE3/SSSE3 code paths; no AVX, AVX2, FMA, F16C, BMI2 or AVX-512 anywhere (GGML_NATIVE=OFF)";

        public static string BlockedMessage => "Whisper can't run in Windows S mode; using built-in Windows speech.";

        /// <summary>The resource name for this CPU, or null when this build has no engine inside.</summary>
        public static string ResourceFor(bool avx2)
        {
            var asm = typeof(WhisperNative).Assembly;
            string want = avx2 ? "Rapcha.engine.rapcha-whisper-avx2.dll.gz" : "Rapcha.engine.rapcha-whisper-sse.dll.gz";
            foreach (var n in asm.GetManifestResourceNames()) if (n == want) return n;
            return null;
        }

        public static bool IsBundled => ResourceFor(false) != null || ResourceFor(true) != null;

        /// <summary>Loads the engine once (cheap after the first time). Never throws.</summary>
        public static State EnsureLoaded()
        {
            lock (Gate)
            {
                if (SimulateBlocked) { LastError = ERROR_INVALID_IMAGE_HASH; AppStorage.Log("Whisper engine: load refused (simulated S mode)"); return State.Blocked; }
                if (Status == State.Ready) return Status;
                if (Status == State.Blocked || Status == State.NotBundled || Status == State.CpuUnsupported) return Status;
                try
                {
                    // v20: check the processor before any Whisper code is loaded (an instruction it lacks would crash Rapcha).
                    var cpu = CpuProbe.Features;
                    var build = CpuFeatures.Choose(cpu);
                    if (ForceVariant == "sse") build = WhisperBuild.Sse; else if (ForceVariant == "avx2") build = WhisperBuild.Avx2;
                    if (build == WhisperBuild.None)
                    {
                        Status = State.CpuUnsupported;
                        AppStorage.Log("Whisper engine: not loaded, processor lacks SSSE3 (" + cpu + ")");
                        return Status;
                    }
                    var res = build == WhisperBuild.Avx2 ? ResourceFor(true) : ResourceFor(false);
                    if (res == null) { Status = State.NotBundled; AppStorage.Log("Whisper engine: not in this build (" + build + ")"); return Status; }
                    Variant = res.Contains("avx2") ? "avx2" : "sse";
                    byte[] dll;
                    using (var s = typeof(WhisperNative).Assembly.GetManifestResourceStream(res))
                    using (var gz = new GZipStream(s, CompressionMode.Decompress))
                    using (var ms = new MemoryStream()) { gz.CopyTo(ms); dll = ms.ToArray(); }
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(dll)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                    var dir = Path.Combine(AppStorage.Folder, "engine", hash);
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "rapcha-whisper.dll");
                    if (!File.Exists(path) || new FileInfo(path).Length != dll.Length) File.WriteAllBytes(path, dll);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _module = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (_module == IntPtr.Zero)
                    {
                        LastError = Marshal.GetLastWin32Error();
                        Status = IsPolicyError(LastError) || DeviceProbe.IsSMode() ? State.Blocked : State.Failed;
                        AppStorage.Log("Whisper engine: LoadLibrary failed error=" + LastError + " s_mode=" + DeviceProbe.IsSMode() + " -> " + Status);
                        return Status;
                    }
                    _load = Fn<LoadFn>("rw_load");
                    _transcribe = Fn<TranscribeFn>("rw_transcribe");
                    _free = Fn<FreeFn>("rw_free");
                    _info = Fn<InfoFn>("rw_system_info");
                    _stats = Fn<StatsFn>("rw_stats");   // v19 diagnostics (optional)
                    var version = Fn<VersionFn>("rw_version");
                    if (_load == null || _transcribe == null || _free == null || version == null || version() != 1)
                    {
                        Status = State.Failed;
                        AppStorage.Log("Whisper engine: functions missing");
                        return Status;
                    }
                    Status = State.Ready;
                    AppStorage.Log("Whisper engine: loaded variant=" + Variant + " in " + sw.ElapsedMilliseconds + " ms (" + (IntPtr.Size == 4 ? "32" : "64") + "-bit; cpu " + cpu + (ForceVariant != null ? "; forced for a test" : "") + ")");
                    return Status;
                }
                catch (Exception e)
                {
                    Status = State.Failed;
                    AppStorage.Log("Whisper engine: load failed " + e.GetType().Name);
                    return Status;
                }
            }
        }

        private static T Fn<T>(string name) where T : class
        {
            var p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero) p = GetProcAddress(_module, "_" + name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static string SystemInfo()
        {
            try { return EnsureLoaded() == State.Ready && _info != null ? Marshal.PtrToStringAnsi(_info()) ?? "" : ""; } catch { return ""; }
        }

        /// <summary>Loads a model (slow: reads the whole file). IntPtr.Zero when it can't.</summary>
        public static IntPtr LoadModel(string path, int threads)
        {
            if (EnsureLoaded() != State.Ready) return IntPtr.Zero;
            return _load(path, threads);
        }

        public static string Transcribe(IntPtr ctx, float[] samples, int threads, string prompt)
        {
            if (ctx == IntPtr.Zero || samples == null || samples.Length == 0) return "";
            var p = string.IsNullOrEmpty(prompt) ? null : Zero(Encoding.UTF8.GetBytes(prompt));
            var lang = Zero(Encoding.ASCII.GetBytes("en"));
            var buf = new byte[64 * 1024];
            int need = _transcribe(ctx, samples, samples.Length, threads, p, lang, buf, buf.Length);
            if (need < 0) return "";
            if (need >= buf.Length)
            {
                buf = new byte[need + 1];
                need = _transcribe(ctx, samples, samples.Length, threads, p, lang, buf, buf.Length);
                if (need < 0) return "";
            }
            return Encoding.UTF8.GetString(buf, 0, Math.Min(need, buf.Length - 1));
        }

        /// <summary>v19: per segment of the last Transcribe (same thread): no-speech prob, avg log-prob, tokens, kept, t0, t1 (cs).</summary>
        public struct Segment { public float NoSpeech, AvgLogProb; public int Tokens; public bool Kept; public float T0, T1; }

        public static Segment[] LastSegments()
        {
            if (_stats == null) return null;
            try
            {
                var buf = new float[64 * 6];
                int n = Math.Min(64, _stats(buf, buf.Length));
                var o = new Segment[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++)
                    o[i] = new Segment { NoSpeech = buf[i * 6], AvgLogProb = buf[i * 6 + 1], Tokens = (int)buf[i * 6 + 2], Kept = buf[i * 6 + 3] > 0.5f, T0 = buf[i * 6 + 4], T1 = buf[i * 6 + 5] };
                return o;
            }
            catch { return null; }
        }

        public static void FreeModel(IntPtr ctx) { if (ctx != IntPtr.Zero && _free != null) _free(ctx); }

        private static byte[] Zero(byte[] b) { var z = new byte[b.Length + 1]; Array.Copy(b, z, b.Length); return z; }
    }
}
