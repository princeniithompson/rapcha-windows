using System;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>
    /// Trims the silence a speech engine puts before and after each synthesized piece, so pieces played one after
    /// another sound like one continuous reading (no gap at every sentence boundary). 16-bit PCM WAV only; any
    /// other format is returned unchanged.
    /// </summary>
    public static class WavTrim
    {
        public const int Threshold = 300; // of 32767 (about -40 dB)

        public static byte[] Trim(byte[] wav, bool trimLead, int keepLeadMs, int keepTailMs, out double removedLeadMs)
        {
            removedLeadMs = 0;
            if (!Parse(wav, out int fmtAt, out int dataAt, out int dataLen)) return wav;
            int channels = BitConverter.ToInt16(wav, fmtAt + 2);
            int rate = BitConverter.ToInt32(wav, fmtAt + 4);
            int bits = BitConverter.ToInt16(wav, fmtAt + 14);
            int formatTag = BitConverter.ToInt16(wav, fmtAt);
            if (formatTag != 1 || bits != 16 || channels < 1 || rate <= 0) return wav;
            int frame = 2 * channels;
            int frames = dataLen / frame;
            if (frames == 0) return wav;

            int first = 0, last = frames - 1;
            while (first < frames && Quiet(wav, dataAt + first * frame, channels)) first++;
            if (first >= frames) return wav;                       // all silence: leave it
            while (last > first && Quiet(wav, dataAt + last * frame, channels)) last--;

            int keepLead = rate * keepLeadMs / 1000, keepTail = rate * keepTailMs / 1000;
            int start = trimLead ? Math.Max(0, first - keepLead) : 0;
            int end = Math.Min(frames, last + 1 + keepTail);
            if (start == 0 && end == frames) return wav;
            removedLeadMs = start * 1000.0 / rate;

            int newLen = (end - start) * frame;
            var o = new byte[dataAt + newLen];
            Array.Copy(wav, 0, o, 0, dataAt);
            Array.Copy(wav, dataAt + start * frame, o, dataAt, newLen);
            BitConverter.GetBytes(o.Length - 8).CopyTo(o, 4);       // RIFF size
            BitConverter.GetBytes(newLen).CopyTo(o, dataAt - 4);    // data size
            return o;
        }

        private static bool Quiet(byte[] b, int at, int channels)
        {
            for (int c = 0; c < channels; c++)
            {
                int v = BitConverter.ToInt16(b, at + 2 * c);
                if (v > Threshold || v < -Threshold) return false;
            }
            return true;
        }

        private static bool Parse(byte[] b, out int fmtAt, out int dataAt, out int dataLen)
        {
            fmtAt = dataAt = dataLen = 0;
            if (b == null || b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return false;
            int p = 12, fmtLen = 0;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;
                if (id == "fmt ") { fmtAt = body; fmtLen = Math.Min(len, b.Length - body); }
                else if (id == "data") { dataAt = body; dataLen = Math.Max(0, Math.Min(len < 0 ? int.MaxValue : len, b.Length - body)); break; }
                if (len < 0) break;
                p = body + len + (len & 1);
            }
            return fmtLen >= 16 && dataAt > 0;
        }
    }
}
