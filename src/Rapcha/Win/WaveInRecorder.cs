using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Rapcha.Core;

namespace Rapcha.Win
{
    public enum MicProblem { None, NoMicrophone, PrivacyBlocked, InUse, Other }

    /// <summary>
    /// Microphone capture with the classic waveIn API (built into Windows, no extra DLL).
    /// Opens WAVE_MAPPER, which is the Windows default recording device (never a loopback / "Stereo Mix" unless
    /// that is the Windows default). Asks for 16 kHz mono 16-bit, which Windows' audio engine converts to from
    /// the driver's own format (shared mode); if refused, falls back to 48k / 44.1k (mono or stereo, 16-bit, then
    /// 32-bit float) and converts to 16 kHz mono itself (Core/PcmResampler). Delivers 100 ms packets in capture
    /// order, and on stop keeps reading a short tail before flushing the last partial packet.
    /// v18: 24 buffers of 50 ms (1.2 s) instead of 8 (0.4 s), so a busy 4 GB laptop never drops audio while an
    /// offline engine loads; dropped audio is measured (LostMs) and logged.
    /// </summary>
    public sealed class WaveInRecorder
    {
        public const int DefaultDevice = -1; // WAVE_MAPPER
        private const int CALLBACK_EVENT = 0x00050000;
        private const int WHDR_DONE = 0x00000001;
        private const int BufferCount = 24;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
        }

        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")]
        private static extern int waveInGetDevCaps(IntPtr uDeviceID, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hWaveIn);

        private readonly Action<byte[]> _onChunk;
        private readonly Action<float> _onLevel;
        private IntPtr _handle;
        private AutoResetEvent _event;
        private IntPtr[] _headers;
        private IntPtr[] _data;
        private int _bufferBytes;
        private Thread _thread;
        private PcmRechunker _rechunker;
        private PcmResampler _resampler;
        private long _stopAtTicks = long.MaxValue;
        private volatile bool _hardStop;
        private long _bytesCaptured;
        private bool _sawNonZero;
        private long _recentPeakTicks;
        private long _firstAudioTicks;
        private int _bytesPerSecond;

        // Diagnostics (no audio content).
        public string DeviceName { get; private set; } = "";
        public string FormatDescription { get; private set; } = "";
        public string OpenAttempts { get; private set; } = "";
        /// <summary>v18: the device id that was opened (-1 = WAVE_MAPPER, the Windows default) and its format, for logs.</summary>
        public int DeviceId { get; private set; } = DefaultDevice;
        public int OpenedRate { get; private set; }
        public int OpenedChannels { get; private set; }
        public SampleKind OpenedKind { get; private set; }
        /// <summary>Audio that should have arrived by now but didn't (the laptop was too busy), in ms.</summary>
        public double LostMs
        {
            get
            {
                long first = Interlocked.Read(ref _firstAudioTicks);
                if (first == 0 || _bytesPerSecond <= 0) return 0;
                double elapsed = (DateTime.UtcNow.Ticks - first) / (double)TimeSpan.TicksPerMillisecond;
                double got = BytesCaptured * 1000.0 / _bytesPerSecond;
                return Math.Max(0, elapsed - got - 150);   // 150 ms: the buffer still being filled
            }
        }
        /// <summary>Frozen when the recording stops.</summary>
        public double FinalLostMs { get; private set; }
        /// <summary>v18: every 16 kHz mono packet is also handed here (Test microphone keeps the 3 s it records).</summary>
        public Action<byte[]> Tap;
        public string FormatForLog => "id=" + DeviceId + " " + OpenedRate + "Hz/" + OpenedChannels + "ch/" + OpenedKind + (_resampler != null && _resampler.IsPassThrough ? "" : "->16000Hz/1ch");
        public long BuffersReturned { get; private set; }
        public long EmptyBuffers { get; private set; }
        public int MaxPeak { get; private set; }
        /// <summary>Peak sample seen in roughly the last 1.5 seconds.</summary>
        public int RecentPeak => DateTime.UtcNow.Ticks - Interlocked.Read(ref _recentPeakTicks) < TimeSpan.TicksPerMillisecond * 1500 ? _recentPeak : 0;
        private volatile int _recentPeak;

        public WaveInRecorder(Action<byte[]> onChunk, Action<float> onLevel)
        {
            _onChunk = onChunk;
            _onLevel = onLevel;
        }

        public long BytesCaptured => Interlocked.Read(ref _bytesCaptured);

        /// <summary>True after ~1 s of capture with every sample exactly zero (usually privacy blocked).</summary>
        public bool LooksSilentlyBlocked => !_sawNonZero && BytesCaptured >= Math.Max(32000, _bytesPerSecond);

        /// <summary>Level 0..1 on a -60..0 dBFS scale so quiet laptop mics still move the bar.</summary>
        public static float LevelFromRms(double rms)
        {
            if (rms <= 1) return 0f;
            double db = 20 * Math.Log10(rms / 32768.0);
            return (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
        }

        public static List<string> ListDevices()
        {
            var list = new List<string>();
            try
            {
                int n = waveInGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    var caps = new WAVEINCAPS();
                    if (waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                        list.Add(caps.szPname ?? ("Microphone " + (i + 1)));
                    else list.Add("Microphone " + (i + 1));
                }
            }
            catch { }
            return list;
        }

        /// <summary>Device id for a saved name, or the Windows default when it is gone.</summary>
        public static int ResolveDevice(string savedName)
        {
            if (string.IsNullOrWhiteSpace(savedName)) return DefaultDevice;
            var list = ListDevices();
            int i = list.FindIndex(d => string.Equals(d, savedName, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : DefaultDevice;
        }

        public static string ErrorName(int rc)
        {
            switch (rc)
            {
                case 0: return "MMSYSERR_NOERROR";
                case 1: return "MMSYSERR_ERROR";
                case 2: return "MMSYSERR_BADDEVICEID";
                case 4: return "MMSYSERR_ALLOCATED";
                case 5: return "MMSYSERR_INVALHANDLE";
                case 6: return "MMSYSERR_NODRIVER";
                case 7: return "MMSYSERR_NOMEM";
                case 8: return "MMSYSERR_NOTSUPPORTED";
                case 11: return "MMSYSERR_INVALPARAM";
                case 32: return "WAVERR_BADFORMAT";
                default: return "error " + rc;
            }
        }

        public static bool IsMicPrivacyDenied()
        {
            try
            {
                const string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
                if (IsDeny(Registry.LocalMachine, basePath)) return true;
                if (IsDeny(Registry.CurrentUser, basePath)) return true;
                if (IsDeny(Registry.CurrentUser, basePath + @"\NonPackaged")) return true;
            }
            catch { }
            return false;
        }

        private static bool IsDeny(RegistryKey root, string path)
        {
            using (var key = root.OpenSubKey(path))
            {
                var v = key?.GetValue("Value") as string;
                return string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
            }
        }

        public MicProblem Start(int deviceId, out string detail)
        {
            detail = "";
            if (IsMicPrivacyDenied()) { detail = "privacy setting is Deny"; return MicProblem.PrivacyBlocked; }
            int count = waveInGetNumDevs();
            if (count == 0) { detail = "waveInGetNumDevs=0"; return MicProblem.NoMicrophone; }
            if (deviceId >= count) deviceId = DefaultDevice;
            var names = ListDevices();
            DeviceName = deviceId >= 0 && deviceId < names.Count ? names[deviceId] : "Windows default (" + (names.Count > 0 ? names[0] : "?") + ")";

            _event = new AutoResetEvent(false);
            // 16-bit PCM first (Windows converts from the driver's format), then 32-bit float (WAVE_FORMAT_IEEE_FLOAT).
            int[][] formats = { new[] { 16000, 1, 16 }, new[] { 48000, 1, 16 }, new[] { 44100, 1, 16 }, new[] { 48000, 2, 16 }, new[] { 44100, 2, 16 },
                                new[] { 48000, 2, 32 }, new[] { 44100, 2, 32 }, new[] { 48000, 1, 32 } };
            int rc = -1;
            int rate = 0, channels = 0, bits = 16;
            var attempts = new List<string>();
            foreach (var f in formats)
            {
                rate = f[0]; channels = f[1]; bits = f[2];
                int bytes = bits / 8;
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = (ushort)(bits == 32 ? 3 : 1), nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = (ushort)bits,
                    nBlockAlign = (ushort)(bytes * channels), nAvgBytesPerSec = (uint)(rate * bytes * channels), cbSize = 0
                };
                rc = waveInOpen(out _handle, deviceId, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
                attempts.Add(rate + "Hz/" + channels + "ch/" + (bits == 32 ? "f32" : "s16") + "=" + ErrorName(rc));
                if (rc == 0) break;
                if (rc != 32 && rc != 11 && rc != 8) break; // only format errors are worth another format
            }
            OpenAttempts = string.Join(", ", attempts);
            if (rc != 0)
            {
                detail = OpenAttempts;
                _event.Dispose();
                _event = null;
                _handle = IntPtr.Zero;
                if (rc == 2 || rc == 6) return MicProblem.NoMicrophone;
                if (rc == 4) return IsMicPrivacyDenied() ? MicProblem.PrivacyBlocked : MicProblem.InUse;
                return IsMicPrivacyDenied() ? MicProblem.PrivacyBlocked : MicProblem.Other;
            }
            var kind = bits == 32 ? SampleKind.Float32 : SampleKind.Int16;
            Prepare(deviceId, rate, channels, kind);

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            _headers = new IntPtr[BufferCount];
            _data = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(_bufferBytes);
                _headers[i] = Marshal.AllocHGlobal(hdrSize);
                var hdr = new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)_bufferBytes };
                Marshal.StructureToPtr(hdr, _headers[i], false);
                int prc = waveInPrepareHeader(_handle, _headers[i], hdrSize);
                int arc = waveInAddBuffer(_handle, _headers[i], hdrSize);
                if (prc != 0 || arc != 0) detail += " prepare/add=" + ErrorName(prc) + "/" + ErrorName(arc);
            }
            rc = waveInStart(_handle);
            if (rc != 0)
            {
                detail = "waveInStart " + ErrorName(rc);
                Cleanup();
                return MicProblem.Other;
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "Rapcha mic", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return MicProblem.None;
        }

        private void Prepare(int deviceId, int rate, int channels, SampleKind kind)
        {
            DeviceId = deviceId;
            OpenedRate = rate; OpenedChannels = channels; OpenedKind = kind;
            _resampler = new PcmResampler(rate, channels, kind);
            FormatDescription = rate + "Hz/" + channels + "ch" + (kind == SampleKind.Float32 ? "/float" : "") + (_resampler.IsPassThrough ? "" : " -> resampled to 16000Hz/1ch");
            int frame = PcmResampler.BytesPerSample(kind) * channels;
            _bytesPerSecond = rate * frame;
            _bufferBytes = _bytesPerSecond / 20; // 50 ms
            _bufferBytes -= _bufferBytes % frame;
            _rechunker = new PcmRechunker(_onChunk);
            _stopAtTicks = long.MaxValue;
            _hardStop = false;
            _bytesCaptured = 0;
            _firstAudioTicks = 0;
            _sawNonZero = false;
            BuffersReturned = 0;
            EmptyBuffers = 0;
            MaxPeak = 0;
            FinalLostMs = 0;
        }

        /// <summary>
        /// v18 self-test and Test microphone simulation: the exact conversion path (resampler, levels, 100 ms packets)
        /// fed with buffers as a driver would hand them over, without a microphone. Call PushNative, then FinishNative.
        /// </summary>
        public void StartSimulated(int rate, int channels, SampleKind kind, string deviceName)
        {
            DeviceName = deviceName ?? "";
            OpenAttempts = "simulated";
            Prepare(-2, rate, channels, kind);
        }

        public void PushNative(byte[] buffer, int count) => Convert(buffer, count);

        public void FinishNative()
        {
            FinalLostMs = 0;
            _rechunker.FlushTail();
        }

        /// <summary>Keeps recording for tailMs, flushes everything captured, then releases the mic.</summary>
        public void StopAndJoin(int tailMs)
        {
            if (_thread == null) return;
            Interlocked.Exchange(ref _stopAtTicks, DateTime.UtcNow.AddMilliseconds(Math.Max(0, tailMs)).Ticks);
            try { _event?.Set(); } catch { }
            _thread.Join(3000);
            _thread = null;
        }

        /// <summary>Cancel: drop everything and release the mic now.</summary>
        public void Abort()
        {
            if (_thread == null) return;
            _hardStop = true;
            Interlocked.Exchange(ref _stopAtTicks, DateTime.UtcNow.Ticks);
            try { _event?.Set(); } catch { }
            _thread.Join(3000);
            _thread = null;
        }

        private void Loop()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            int next = 0;
            var copy = new byte[_bufferBytes];
            try
            {
                while (true)
                {
                    _event.WaitOne(60);
                    bool stopping = DateTime.UtcNow.Ticks >= Interlocked.Read(ref _stopAtTicks);
                    if (stopping)
                    {
                        waveInReset(_handle); // returns every pending buffer, partial ones included
                        if (!_hardStop)
                        {
                            for (int k = 0; k < BufferCount; k++)
                            {
                                var hdr = (WAVEHDR)Marshal.PtrToStructure(_headers[next], typeof(WAVEHDR));
                                if ((hdr.dwFlags & WHDR_DONE) != 0 && hdr.dwBytesRecorded > 0) Deliver(hdr, copy);
                                next = (next + 1) % BufferCount;
                            }
                            _rechunker.FlushTail();
                        }
                        break;
                    }
                    while (true)
                    {
                        var hdr = (WAVEHDR)Marshal.PtrToStructure(_headers[next], typeof(WAVEHDR));
                        if ((hdr.dwFlags & WHDR_DONE) == 0) break;
                        BuffersReturned++;
                        if (hdr.dwBytesRecorded > 0) Deliver(hdr, copy); else EmptyBuffers++;
                        hdr.dwBytesRecorded = 0;
                        hdr.dwFlags &= ~(uint)WHDR_DONE;
                        Marshal.StructureToPtr(hdr, _headers[next], false);
                        waveInAddBuffer(_handle, _headers[next], hdrSize);
                        next = (next + 1) % BufferCount;
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                FinalLostMs = LostMs;
                Cleanup();
                try { _onLevel?.Invoke(0f); } catch { }
            }
        }

        private void Deliver(WAVEHDR hdr, byte[] copy)
        {
            int n = (int)Math.Min(hdr.dwBytesRecorded, (uint)copy.Length);
            Marshal.Copy(hdr.lpData, copy, 0, n);
            Convert(copy, n);
        }

        /// <summary>Driver-format bytes in; 16 kHz mono 16-bit packets out (the same path for every engine).</summary>
        private void Convert(byte[] copy, int n)
        {
            if (n <= 0) return;
            if (Interlocked.Read(ref _firstAudioTicks) == 0)
                Interlocked.Exchange(ref _firstAudioTicks, DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond * n / Math.Max(1, _bytesPerSecond));
            Interlocked.Add(ref _bytesCaptured, n);
            var pcm16k = _resampler.Process(copy, n);
            if (pcm16k.Length == 0) return;
            if (!_sawNonZero)
            {
                for (int i = 0; i < pcm16k.Length; i++) if (pcm16k[i] != 0) { _sawNonZero = true; break; }
            }
            try
            {
                int peak = SpeechGain.Peak(pcm16k, pcm16k.Length);
                if (peak > MaxPeak) MaxPeak = peak;
                if (peak >= _recentPeak || DateTime.UtcNow.Ticks - Interlocked.Read(ref _recentPeakTicks) > TimeSpan.TicksPerMillisecond * 1500)
                {
                    _recentPeak = peak;
                    Interlocked.Exchange(ref _recentPeakTicks, DateTime.UtcNow.Ticks);
                }
                _onLevel?.Invoke(LevelFromRms(SpeechGain.Rms(pcm16k, pcm16k.Length)));
            }
            catch { }
            try { Tap?.Invoke(pcm16k); } catch { }
            _rechunker.Write(pcm16k, 0, pcm16k.Length);
        }

        private void Cleanup()
        {
            if (_handle != IntPtr.Zero)
            {
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                try { waveInReset(_handle); } catch { }
                if (_headers != null)
                    foreach (var h in _headers) if (h != IntPtr.Zero) waveInUnprepareHeader(_handle, h, hdrSize);
                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }
            if (_headers != null) { foreach (var h in _headers) if (h != IntPtr.Zero) Marshal.FreeHGlobal(h); _headers = null; }
            if (_data != null) { foreach (var d in _data) if (d != IntPtr.Zero) Marshal.FreeHGlobal(d); _data = null; }
            try { _event?.Dispose(); } catch { }
            _event = null;
        }
    }
}
