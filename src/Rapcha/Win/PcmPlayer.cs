using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rapcha.Win
{
    /// <summary>Plays one block of 16-bit mono PCM at a time through waveOut and reports how far it got.</summary>
    public interface IPcmPlayer : IDisposable
    {
        bool Open(int sampleRate, out string error);
        void Play(byte[] pcm);
        double Fraction { get; }
        bool Done { get; }
        void Pause();
        void Resume();
        void Reset();
    }

    public sealed class WaveOutPlayer : IPcmPlayer
    {
        private IntPtr _wo;
        private int _rate;
        private IntPtr _hdr, _data;
        private int _length;
        private bool _paused;

        public bool Open(int sampleRate, out string error)
        {
            error = "";
            if (_wo != IntPtr.Zero && _rate == sampleRate) return true;
            Close();
            var fmt = new Native.WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = sampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = sampleRate * 2 };
            int r = Native.waveOutOpen(out _wo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { _wo = IntPtr.Zero; error = "waveOutOpen=" + r; return false; }
            _rate = sampleRate;
            return true;
        }

        public void Play(byte[] pcm)
        {
            Reset();
            if (_wo == IntPtr.Zero || pcm == null || pcm.Length == 0) return;
            _length = pcm.Length;
            _data = Marshal.AllocHGlobal(pcm.Length);
            Marshal.Copy(pcm, 0, _data, pcm.Length);
            int size = Marshal.SizeOf(typeof(Native.WAVEHDR));
            _hdr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new Native.WAVEHDR { lpData = _data, dwBufferLength = pcm.Length }, _hdr, false);
            Native.waveOutPrepareHeader(_wo, _hdr, size);
            if (_paused) Native.waveOutPause(_wo);
            Native.waveOutWrite(_wo, _hdr, size);
        }

        public double Fraction
        {
            get
            {
                if (_wo == IntPtr.Zero || _length == 0) return 0;
                var t = new Native.MMTIME { wType = Native.TIME_BYTES };
                if (Native.waveOutGetPosition(_wo, ref t, Marshal.SizeOf(typeof(Native.MMTIME))) != 0 || t.wType != Native.TIME_BYTES) return 0;
                return Math.Min(1.0, t.u / (double)_length);
            }
        }

        public bool Done => _hdr == IntPtr.Zero || (Marshal.ReadInt32(_hdr, Marshal.OffsetOf(typeof(Native.WAVEHDR), "dwFlags").ToInt32()) & Native.WHDR_DONE) != 0;

        public void Pause() { _paused = true; if (_wo != IntPtr.Zero) Native.waveOutPause(_wo); }
        public void Resume() { _paused = false; if (_wo != IntPtr.Zero) Native.waveOutRestart(_wo); }

        public void Reset()
        {
            if (_wo != IntPtr.Zero) Native.waveOutReset(_wo); // returns the buffer and sets the position to 0
            if (_hdr != IntPtr.Zero)
            {
                Native.waveOutUnprepareHeader(_wo, _hdr, Marshal.SizeOf(typeof(Native.WAVEHDR)));
                Marshal.FreeHGlobal(_hdr); _hdr = IntPtr.Zero;
            }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
            _length = 0;
            if (_wo != IntPtr.Zero && _paused) Native.waveOutPause(_wo);
        }

        private void Close()
        {
            Reset();
            if (_wo != IntPtr.Zero) { Native.waveOutClose(_wo); _wo = IntPtr.Zero; }
        }

        public void Dispose() => Close();
    }

    /// <summary>Self-test player: no sound card needed, plays in real time on a stopwatch.</summary>
    public sealed class ClockPlayer : IPcmPlayer
    {
        private readonly Stopwatch _sw = new Stopwatch();
        private double _seconds;
        private int _rate;
        private bool _paused;
        public long BytesPlayed { get; private set; }

        public bool Open(int sampleRate, out string error) { _rate = sampleRate; error = ""; return true; }
        public void Play(byte[] pcm) { _seconds = pcm.Length / 2.0 / _rate; BytesPlayed += pcm.Length; _sw.Reset(); if (!_paused) _sw.Start(); }
        public double Fraction => _seconds <= 0 ? 0 : Math.Min(1.0, _sw.Elapsed.TotalSeconds / _seconds);
        public bool Done => Fraction >= 1.0;
        public void Pause() { _paused = true; _sw.Stop(); }
        public void Resume() { _paused = false; if (_seconds > 0) _sw.Start(); }
        public void Reset() { _sw.Reset(); _seconds = 0; }
        public void Dispose() { }
    }
}
