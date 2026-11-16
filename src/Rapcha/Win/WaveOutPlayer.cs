using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rapcha.Win
{
    /// <summary>
    /// Plays one WAV file from memory through winmm waveOut (no callbacks: position and end are polled),
    /// with pause/resume. Silent mode (self-test) only runs a clock of the same length.
    /// </summary>
    public sealed class WaveOutPlayer : IDisposable
    {
        private const uint WAVE_MAPPER = 0xFFFFFFFF, WHDR_DONE = 1, TIME_BYTES = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MMTIME { public uint wType; public uint cb; public uint pad; }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, uint device, IntPtr format, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr h, ref MMTIME t, int size);

        private readonly bool _silent;
        private readonly int _bytesPerSec;
        private readonly int _dataLength;
        private IntPtr _wave, _hdr, _data, _fmt;
        private readonly Stopwatch _clock = new Stopwatch();
        private bool _paused;

        public double DurationMs { get; }
        public int SampleRate { get; }

        /// <summary>Parses a RIFF/WAVE file (any format waveOut takes, normally 16-bit PCM).</summary>
        public WaveOutPlayer(byte[] wav, bool silent)
        {
            _silent = silent;
            if (!Parse(wav, out int fmtAt, out int fmtLen, out int dataAt, out int dataLen)) throw new InvalidOperationException("not a WAV stream");
            SampleRate = BitConverter.ToInt32(wav, fmtAt + 4);
            _bytesPerSec = Math.Max(1, BitConverter.ToInt32(wav, fmtAt + 8));
            _dataLength = dataLen;
            DurationMs = dataLen * 1000.0 / _bytesPerSec;
            if (silent) return;

            _fmt = Marshal.AllocHGlobal(Math.Max(fmtLen, 20));
            var fmt = new byte[Math.Max(fmtLen, 20)];
            Array.Copy(wav, fmtAt, fmt, 0, fmtLen);
            Marshal.Copy(fmt, 0, _fmt, fmt.Length);
            int r = waveOutOpen(out _wave, WAVE_MAPPER, _fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { Free(); throw new InvalidOperationException("waveOutOpen " + r); }
            _data = Marshal.AllocHGlobal(dataLen);
            Marshal.Copy(wav, dataAt, _data, dataLen);
            _hdr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
            Marshal.StructureToPtr(new WAVEHDR { lpData = _data, dwBufferLength = (uint)dataLen }, _hdr, false);
            waveOutPrepareHeader(_wave, _hdr, Marshal.SizeOf(typeof(WAVEHDR)));
        }

        public static bool Parse(byte[] b, out int fmtAt, out int fmtLen, out int dataAt, out int dataLen)
        {
            fmtAt = fmtLen = dataAt = dataLen = 0;
            if (b == null || b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return false;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;
                if (id == "fmt ") { fmtAt = body; fmtLen = Math.Min(len, b.Length - body); }
                else if (id == "data") { dataAt = body; dataLen = Math.Max(0, Math.Min(len < 0 ? int.MaxValue : len, b.Length - body)); break; }
                p = body + len + (len & 1);
                if (len < 0) break;
            }
            return fmtLen >= 16 && dataAt > 0;
        }

        public void Play()
        {
            _clock.Restart();
            if (!_silent) waveOutWrite(_wave, _hdr, Marshal.SizeOf(typeof(WAVEHDR)));
        }

        public void Pause()
        {
            if (_paused) return;
            _paused = true;
            _clock.Stop();
            if (!_silent) waveOutPause(_wave);
        }

        public void Resume()
        {
            if (!_paused) return;
            _paused = false;
            _clock.Start();
            if (!_silent) waveOutRestart(_wave);
        }

        /// <summary>Milliseconds of audio played so far.</summary>
        public double PositionMs
        {
            get
            {
                if (_silent || _wave == IntPtr.Zero) return Math.Min(DurationMs, _clock.Elapsed.TotalMilliseconds);
                var t = new MMTIME { wType = TIME_BYTES };
                if (waveOutGetPosition(_wave, ref t, Marshal.SizeOf(typeof(MMTIME))) != 0 || t.wType != TIME_BYTES)
                    return Math.Min(DurationMs, _clock.Elapsed.TotalMilliseconds);
                return Math.Min(DurationMs, t.cb * 1000.0 / _bytesPerSec);
            }
        }

        public bool Done
        {
            get
            {
                if (_silent || _hdr == IntPtr.Zero) return _clock.Elapsed.TotalMilliseconds >= DurationMs;
                var h = (WAVEHDR)Marshal.PtrToStructure(_hdr, typeof(WAVEHDR));
                return (h.dwFlags & WHDR_DONE) != 0;
            }
        }

        private void Free()
        {
            if (_hdr != IntPtr.Zero) { Marshal.FreeHGlobal(_hdr); _hdr = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
            if (_fmt != IntPtr.Zero) { Marshal.FreeHGlobal(_fmt); _fmt = IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_wave != IntPtr.Zero)
            {
                waveOutReset(_wave);
                if (_hdr != IntPtr.Zero) waveOutUnprepareHeader(_wave, _hdr, Marshal.SizeOf(typeof(WAVEHDR)));
                waveOutClose(_wave);
                _wave = IntPtr.Zero;
            }
            Free();
        }
    }
}
