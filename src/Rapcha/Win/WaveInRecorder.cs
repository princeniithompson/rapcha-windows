using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Rapcha.Core;

namespace Rapcha.Win
{
    public enum MicProblem { None, NoMicrophone, PrivacyBlocked, InUse, Other }

    /// <summary>
    /// 16 kHz mono 16-bit PCM from the default microphone using the classic waveIn API
    /// (built into Windows, so no extra DLL). Delivers 100 ms packets in capture order,
    /// and on stop keeps reading a short tail before flushing the last partial packet.
    /// </summary>
    public sealed class WaveInRecorder
    {
        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_EVENT = 0x00050000;
        private const int WHDR_DONE = 0x00000001;
        private const int BufferCount = 8;
        private const int BufferBytes = 1600; // 50 ms

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

        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
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
        private Thread _thread;
        private PcmRechunker _rechunker;
        private long _stopAtTicks = long.MaxValue;
        private volatile bool _hardStop;
        private long _bytesCaptured;
        private bool _sawNonZero;

        public WaveInRecorder(Action<byte[]> onChunk, Action<float> onLevel)
        {
            _onChunk = onChunk;
            _onLevel = onLevel;
        }

        /// <summary>True after ~1 s of capture with every sample exactly zero (usually privacy blocked).</summary>
        public bool LooksSilentlyBlocked => !_sawNonZero && Interlocked.Read(ref _bytesCaptured) >= 32000;

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

        public MicProblem Start(out string detail)
        {
            detail = "";
            if (IsMicPrivacyDenied()) return MicProblem.PrivacyBlocked;
            if (waveInGetNumDevs() == 0) return MicProblem.NoMicrophone;

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = 16000, wBitsPerSample = 16,
                nBlockAlign = 2, nAvgBytesPerSec = 32000, cbSize = 0
            };
            _event = new AutoResetEvent(false);
            int rc = waveInOpen(out _handle, WAVE_MAPPER, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (rc != 0)
            {
                detail = "waveInOpen error " + rc;
                _event.Dispose();
                _handle = IntPtr.Zero;
                if (rc == 2 || rc == 6) return MicProblem.NoMicrophone;     // BADDEVICEID / NODRIVER
                if (rc == 4) return MicProblem.InUse;                        // ALLOCATED
                return IsMicPrivacyDenied() ? MicProblem.PrivacyBlocked : MicProblem.Other;
            }

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            _headers = new IntPtr[BufferCount];
            _data = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(BufferBytes);
                _headers[i] = Marshal.AllocHGlobal(hdrSize);
                var hdr = new WAVEHDR { lpData = _data[i], dwBufferLength = BufferBytes };
                Marshal.StructureToPtr(hdr, _headers[i], false);
                waveInPrepareHeader(_handle, _headers[i], hdrSize);
                waveInAddBuffer(_handle, _headers[i], hdrSize);
            }
            _rechunker = new PcmRechunker(_onChunk);
            _stopAtTicks = long.MaxValue;
            _hardStop = false;
            _bytesCaptured = 0;
            _sawNonZero = false;
            rc = waveInStart(_handle);
            if (rc != 0)
            {
                detail = "waveInStart error " + rc;
                Cleanup();
                return MicProblem.Other;
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "Rapcha mic", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return MicProblem.None;
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
            var copy = new byte[BufferBytes];
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
                        if (hdr.dwBytesRecorded > 0) Deliver(hdr, copy);
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
                Cleanup();
                try { _onLevel?.Invoke(0f); } catch { }
            }
        }

        private void Deliver(WAVEHDR hdr, byte[] copy)
        {
            int n = (int)Math.Min(hdr.dwBytesRecorded, (uint)copy.Length);
            Marshal.Copy(hdr.lpData, copy, 0, n);
            Interlocked.Add(ref _bytesCaptured, n);
            if (!_sawNonZero)
            {
                for (int i = 0; i < n; i++) if (copy[i] != 0) { _sawNonZero = true; break; }
            }
            try
            {
                double rms = SpeechGain.Rms(copy, n - (n % 2));
                _onLevel?.Invoke((float)Math.Min(1.0, rms / 3200.0));
            }
            catch { }
            _rechunker.Write(copy, 0, n);
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
