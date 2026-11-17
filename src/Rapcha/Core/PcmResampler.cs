using System;

namespace Rapcha.Core
{
    /// <summary>How the microphone driver hands over each sample.</summary>
    public enum SampleKind { Int16, Int24, Int32, Float32 }

    /// <summary>
    /// v18: turns whatever the microphone delivers (16/24/32-bit integer or 32-bit float, any rate, mono or
    /// stereo, little-endian like every Windows audio buffer) into 16 kHz mono 16-bit PCM, the one format both
    /// offline engines and Gemini get. Channels are averaged (downmix), samples are scaled to -1..1 and back to
    /// 16-bit with clipping, and the rate is changed with a windowed-sinc low-pass filter (cut-off 7.2 kHz) so
    /// nothing above 8 kHz folds back into the speech band. 16 kHz mono 16-bit input passes straight through.
    /// The filter keeps its history between buffers, so buffer boundaries never click or drop samples.
    /// </summary>
    public sealed class PcmResampler
    {
        public const int TargetRate = 16000;
        private readonly int _channels;
        private readonly SampleKind _kind;
        private readonly int _bytesPerSample;
        private readonly double _ratio;      // input samples per output sample
        private readonly double _fc;         // cut-off in cycles per input sample
        private readonly int _half;          // filter half-width in input samples
        private float[] _buf = new float[4096];
        private int _count;
        private double _pos;
        private readonly byte[] _partial;    // a frame split across two buffers
        private int _partialBytes;

        public int InputRate { get; }
        public int Channels => _channels;
        public SampleKind Kind => _kind;
        public bool IsPassThrough => InputRate == TargetRate && _channels == 1 && _kind == SampleKind.Int16;
        public int FrameBytes => _bytesPerSample * _channels;

        public PcmResampler(int inputRate, int channels) : this(inputRate, channels, SampleKind.Int16) { }

        public PcmResampler(int inputRate, int channels, SampleKind kind)
        {
            if (inputRate < 8000) throw new ArgumentOutOfRangeException(nameof(inputRate));
            InputRate = inputRate;
            _channels = Math.Max(1, channels);
            _kind = kind;
            _bytesPerSample = BytesPerSample(kind);
            _ratio = inputRate / (double)TargetRate;
            // Low-pass at 0.45 x 16 kHz = 7.2 kHz (relative to the input rate); never above the input's own Nyquist.
            _fc = Math.Min(0.5, 0.45 * TargetRate / inputRate);
            _half = Math.Max(8, (int)Math.Ceiling(8 * Math.Max(1.0, _ratio)));
            _partial = new byte[_bytesPerSample * _channels];
            // Start with silence as history so the first output sample lines up with the first input sample.
            _count = _half;
            _pos = _half;
        }

        public static int BytesPerSample(SampleKind k) => k == SampleKind.Int16 ? 2 : k == SampleKind.Int24 ? 3 : 4;

        /// <summary>One sample at byte offset i, scaled to -1..1.</summary>
        public static float Decode(byte[] d, int i, SampleKind k)
        {
            switch (k)
            {
                case SampleKind.Int16: return (short)(d[i] | (d[i + 1] << 8)) / 32768f;
                case SampleKind.Int24: { int v = (d[i] << 8) | (d[i + 1] << 16) | (d[i + 2] << 24); return (v >> 8) / 8388608f; }
                case SampleKind.Int32: return (d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)) / 2147483648f;
                default:
                {
                    int bits = d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24);
                    float f = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                    return float.IsNaN(f) || float.IsInfinity(f) ? 0f : f;
                }
            }
        }

        /// <summary>-1..1 to 16-bit, rounded and clipped (floats above 1.0 do happen with some drivers).</summary>
        public static short ToPcm16(float f)
        {
            double v = Math.Round(f * 32767.0);
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            return (short)v;
        }

        /// <summary>The average of the frame's channels (downmix), -1..1.</summary>
        public float Frame(byte[] d, int at)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += Decode(d, at + c * _bytesPerSample, _kind);
            return sum / _channels;
        }

        public byte[] Process(byte[] data, int count)
        {
            if (IsPassThrough && _partialBytes == 0)
            {
                var copy = new byte[count - (count % 2)];
                Buffer.BlockCopy(data, 0, copy, 0, copy.Length);
                if (count % 2 == 1) { _partial[0] = data[count - 1]; _partialBytes = 1; }
                return copy;
            }
            int fb = FrameBytes;
            int off = 0;
            // finish a frame that was split across two buffers
            if (_partialBytes > 0)
            {
                int need = fb - _partialBytes;
                int n = Math.Min(need, count);
                Buffer.BlockCopy(data, 0, _partial, _partialBytes, n);
                _partialBytes += n; off = n;
                if (_partialBytes < fb) return new byte[0];
                Append(Frame(_partial, 0));
                _partialBytes = 0;
            }
            int frames = (count - off) / fb;
            for (int f = 0; f < frames; f++) Append(Frame(data, off + f * fb));
            int rest = (count - off) - frames * fb;
            if (rest > 0) { Buffer.BlockCopy(data, off + frames * fb, _partial, 0, rest); _partialBytes = rest; }
            return Drain();
        }

        private void Append(float v)
        {
            if (_count == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_count++] = v;
        }

        private byte[] Drain()
        {
            if (InputRate == TargetRate)
            {
                // Same rate (stereo or float or 24-bit at 16 kHz): no filtering needed, just convert.
                int n = _count - _half;
                var o = new byte[Math.Max(0, n) * 2];
                for (int i = 0; i < n; i++) { short s = ToPcm16(_buf[_half + i]); o[2 * i] = (byte)(s & 0xFF); o[2 * i + 1] = (byte)((s >> 8) & 0xFF); }
                Array.Copy(_buf, _count - _half, _buf, 0, _half);
                _count = _half;
                return o;
            }
            int max = (int)((_count - _pos) / _ratio) + 2;
            var output = new byte[Math.Max(0, max) * 2];
            int w = 0;
            while (_pos + _half < _count && w + 2 <= output.Length)
            {
                int centre = (int)Math.Floor(_pos);
                double frac = _pos - centre;
                double acc = 0, norm = 0;
                for (int k = -_half + 1; k <= _half; k++)
                {
                    int idx = centre + k;
                    double x = k - frac;                       // distance from the output instant, in input samples
                    double h = Kernel(x);
                    norm += h;
                    if (idx >= 0) acc += _buf[idx] * h;
                }
                float y = (float)(norm != 0 ? acc / norm : 0);
                short s = ToPcm16(y);
                output[w++] = (byte)(s & 0xFF);
                output[w++] = (byte)((s >> 8) & 0xFF);
                _pos += _ratio;
            }
            // keep enough history for the filter, drop the rest
            int keepFrom = Math.Max(0, (int)Math.Floor(_pos) - _half);
            if (keepFrom > 0)
            {
                Array.Copy(_buf, keepFrom, _buf, 0, _count - keepFrom);
                _count -= keepFrom;
                _pos -= keepFrom;
            }
            if (w == output.Length) return output;
            var exact = new byte[w];
            Buffer.BlockCopy(output, 0, exact, 0, w);
            return exact;
        }

        /// <summary>Blackman-windowed sinc low-pass at _fc (cycles per input sample).</summary>
        private double Kernel(double x)
        {
            double ax = Math.Abs(x);
            if (ax >= _half) return 0;
            double sinc = ax < 1e-9 ? 2 * _fc : Math.Sin(2 * Math.PI * _fc * x) / (Math.PI * x);
            double t = (x + _half) / (2.0 * _half);           // 0..1 across the window
            double win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * t) + 0.08 * Math.Cos(4 * Math.PI * t);
            return sinc * win;
        }
    }
}
