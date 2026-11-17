using System;
using System.Runtime.InteropServices;

namespace Rapcha.Win
{
    /// <summary>
    /// v18: what Windows says about the default microphone (Core Audio / MMDevice API, part of Windows): its full name,
    /// whether the default "Communications" microphone is a different one, and the format the driver really runs at
    /// (the shared-mode mix format, e.g. 48 kHz stereo 32-bit float). Read-only: it never opens a stream. waveIn's
    /// WAVE_MAPPER (what Rapcha records from) follows the Windows default recording device.
    /// </summary>
    public static class AudioEndpoints
    {
        public struct Info
        {
            public bool Found;
            public string Name;            // the default recording device
            public string CommsName;       // the default communications recording device
            public string NativeFormat;    // "48000 Hz, 2 ch, 32-bit float"
            public int Rate, Channels, Bits;
            public bool Float;
            public string Error;
        }

        private const int eCapture = 1, eConsole = 0, eCommunications = 2;
        private const int CLSCTX_ALL = 23;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int i, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid FloatSub = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static Info Default()
        {
            var info = new Info { Name = "", CommsName = "", NativeFormat = "", Error = "" };
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(eCapture, eConsole, out var dev) != 0 || dev == null) { info.Error = "no default microphone"; return info; }
                info.Found = true;
                info.Name = NameOf(dev);
                try { if (en.GetDefaultAudioEndpoint(eCapture, eCommunications, out var comms) == 0 && comms != null) info.CommsName = NameOf(comms); } catch { }
                try
                {
                    var iid = IID_IAudioClient;
                    if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o) == 0 && o is IAudioClient client)
                    {
                        if (client.GetMixFormat(out var fmt) == 0 && fmt != IntPtr.Zero)
                        {
                            try { Parse(fmt, ref info); }
                            finally { Marshal.FreeCoTaskMem(fmt); }
                        }
                        Marshal.ReleaseComObject(client);
                    }
                }
                catch (Exception e) { info.Error = "format unknown (" + e.GetType().Name + ")"; }
            }
            catch (Exception e) { info.Error = e.GetType().Name; }
            return info;
        }

        private static void Parse(IntPtr fmt, ref Info info)
        {
            int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
            info.Channels = Marshal.ReadInt16(fmt, 2);
            info.Rate = Marshal.ReadInt32(fmt, 4);
            info.Bits = Marshal.ReadInt16(fmt, 14);
            info.Float = tag == 3;
            if (tag == 0xFFFE)
            {
                var b = new byte[16];
                Marshal.Copy(IntPtr.Add(fmt, 24), b, 0, 16);
                info.Float = new Guid(b) == FloatSub;
            }
            info.NativeFormat = info.Rate + " Hz, " + info.Channels + (info.Channels == 1 ? " channel, " : " channels, ") + info.Bits + "-bit " + (info.Float ? "float" : "integer");
        }

        private static string NameOf(IMMDevice dev)
        {
            try
            {
                if (dev.OpenPropertyStore(0, out var store) != 0 || store == null) return "";
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                if (store.GetValue(ref key, out var v) != 0) return "";
                try { return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) ?? "" : ""; }
                finally { PropVariantClear(ref v); }
            }
            catch { return ""; }
        }
    }
}
