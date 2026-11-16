using System;
using System.Collections.Generic;

namespace Rapcha.Core
{
    /// <summary>
    /// Converts 16-bit PCM at any rate / channel count to 16 kHz mono, for mic drivers that refuse
    /// 16 kHz mono. Channels are averaged; each output sample is the average of the input samples it
    /// covers (a simple low-pass that keeps speech clean when going from 48k or 44.1k down to 16k).
    /// </summary>
    public sealed class PcmResampler
    {
        public const int TargetRate = 16000;
        private readonly int _channels;
        private readonly double _ratio;
        private readonly List<int> _carry = new List<int>();
        private double _pos;

        public int InputRate { get; }
        public bool IsPassThrough => InputRate == TargetRate && _channels == 1;

        public PcmResampler(int inputRate, int channels)
        {
            if (inputRate < 8000) throw new ArgumentOutOfRangeException(nameof(inputRate));
            InputRate = inputRate;
            _channels = Math.Max(1, channels);
            _ratio = inputRate / (double)TargetRate;
        }

        public byte[] Process(byte[] data, int count)
        {
            if (IsPassThrough)
            {
                var copy = new byte[count - (count % 2)];
                Buffer.BlockCopy(data, 0, copy, 0, copy.Length);
                return copy;
            }
            int frameBytes = 2 * _channels;
            int frames = count / frameBytes;
            for (int f = 0; f < frames; f++)
            {
                int sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    int i = f * frameBytes + c * 2;
                    sum += (short)(data[i] | (data[i + 1] << 8));
                }
                _carry.Add(sum / _channels);
            }

            var output = new List<byte>((int)(frames / _ratio + 2) * 2);
            while (_pos + _ratio <= _carry.Count)
            {
                int start = (int)_pos;
                int end = Math.Max(start + 1, (int)(_pos + _ratio));
                long acc = 0;
                for (int i = start; i < end; i++) acc += _carry[i];
                int v = (int)(acc / (end - start));
                output.Add((byte)(v & 0xFF));
                output.Add((byte)((v >> 8) & 0xFF));
                _pos += _ratio;
            }
            int drop = Math.Min((int)_pos, _carry.Count);
            if (drop > 0) { _carry.RemoveRange(0, drop); _pos -= drop; }
            return output.ToArray();
        }
    }
}
