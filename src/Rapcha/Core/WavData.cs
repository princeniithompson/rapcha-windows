using System;

namespace Rapcha.Core
{
    /// <summary>Minimal RIFF/WAVE reader for 16-bit PCM (what Piper writes), plus silence trimming.</summary>
    public sealed class WavData
    {
        public int SampleRate, Channels, BitsPerSample;
        public byte[] Pcm = new byte[0];

        public static bool TryParse(byte[] file, out WavData wav)
        {
            wav = null;
            if (file == null || file.Length < 12) return false;
            if (Ascii(file, 0) != "RIFF" || Ascii(file, 8) != "WAVE") return false;
            var w = new WavData();
            bool fmt = false;
            int pos = 12;
            while (pos + 8 <= file.Length)
            {
                string id = Ascii(file, pos);
                int size = BitConverter.ToInt32(file, pos + 4);
                int body = pos + 8;
                if (size < 0) return false;
                if (id == "fmt " && body + 16 <= file.Length)
                {
                    int format = BitConverter.ToInt16(file, body);
                    w.Channels = BitConverter.ToInt16(file, body + 2);
                    w.SampleRate = BitConverter.ToInt32(file, body + 4);
                    w.BitsPerSample = BitConverter.ToInt16(file, body + 14);
                    fmt = format == 1 || format == unchecked((short)0xFFFE);
                }
                else if (id == "data")
                {
                    // Streams written before the size was known may say 0 or too much: take what is there.
                    int len = size <= 0 || body + size > file.Length ? file.Length - body : size;
                    len -= len % 2;
                    w.Pcm = new byte[Math.Max(0, len)];
                    Buffer.BlockCopy(file, body, w.Pcm, 0, w.Pcm.Length);
                    break;
                }
                pos = body + size + (size & 1);
            }
            if (!fmt || w.SampleRate <= 0 || w.Channels <= 0 || w.BitsPerSample != 16) return false;
            wav = w;
            return true;
        }

        /// <summary>
        /// Trims leading and trailing quiet from 16-bit mono PCM, keeping keepMs of padding,
        /// so the word-timing estimate lines up with the actual speech.
        /// </summary>
        public static byte[] TrimSilence(byte[] pcm, int sampleRate, int threshold = 300, int keepMs = 40)
        {
            if (pcm == null || pcm.Length < 4) return pcm ?? new byte[0];
            int n = pcm.Length / 2;
            int first = -1, last = -1;
            for (int i = 0; i < n; i++) if (Math.Abs((int)BitConverter.ToInt16(pcm, i * 2)) >= threshold) { first = i; break; }
            if (first < 0) return pcm; // all quiet: leave it alone
            for (int i = n - 1; i >= 0; i--) if (Math.Abs((int)BitConverter.ToInt16(pcm, i * 2)) >= threshold) { last = i; break; }
            int keep = sampleRate * keepMs / 1000;
            first = Math.Max(0, first - keep);
            last = Math.Min(n - 1, last + keep);
            var result = new byte[(last - first + 1) * 2];
            Buffer.BlockCopy(pcm, first * 2, result, 0, result.Length);
            return result;
        }

        private static string Ascii(byte[] b, int at) => at + 4 <= b.Length ? new string(new[] { (char)b[at], (char)b[at + 1], (char)b[at + 2], (char)b[at + 3] }) : "";
    }
}
