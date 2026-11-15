using System;

namespace Rapcha.Core
{
    /// <summary>
    /// Port of rapcha-lite audio/SpeechGain.kt: light adaptive gain applied only to the
    /// copy of the audio that is sent to Gemini. 16-bit signed little-endian mono PCM.
    /// </summary>
    public sealed class SpeechGain
    {
        public const double TargetRms = 2500.0;
        public const double MaxGain = 6.0;
        public const double PeakCeiling = 29000.0;
        public const double Knee = 28000.0;
        private const double MinSpeechRms = 150.0;
        private const double SpeechOverFloor = 2.0;
        private const double Rise = 0.25;

        private double _noiseFloor = -1.0;
        private double _speechLevel = -1.0;
        private double _previousApplied = 1.0;
        public double CurrentGain { get; private set; } = 1.0;

        public static int Sample(byte[] pcm, int i) => (short)((pcm[i + 1] << 8) | pcm[i]);

        public static double Rms(byte[] pcm, int length)
        {
            int n = length / 2;
            if (n <= 0) return 0.0;
            double sum = 0;
            for (int i = 0; i + 1 < length; i += 2) { double s = Sample(pcm, i); sum += s * s; }
            return Math.Sqrt(sum / n);
        }

        public static int Peak(byte[] pcm, int length)
        {
            int p = 0;
            for (int i = 0; i + 1 < length; i += 2) { int s = Math.Abs(Sample(pcm, i)); if (s > p) p = s; }
            return p;
        }

        public static double SoftLimit(double x)
        {
            double a = Math.Abs(x);
            if (a <= Knee) return x;
            double over = a - Knee;
            double limited = Math.Min(32767.0, Knee + over / (1.0 + over / (32767.0 - Knee)));
            return x < 0 ? -limited : limited;
        }

        public byte[] Process(byte[] pcm, bool adapt = true)
        {
            int len = pcm.Length - (pcm.Length % 2);
            if (len <= 0) return pcm;
            double r = Rms(pcm, len);
            int p = Peak(pcm, len);

            if (adapt)
            {
                if (_noiseFloor < 0) _noiseFloor = Math.Max(30.0, r);
                else if (r < _noiseFloor) _noiseFloor = Math.Max(30.0, _noiseFloor * 0.7 + r * 0.3);
                else _noiseFloor = _noiseFloor * 0.995 + r * 0.005;
                bool isSpeech = r >= Math.Max(MinSpeechRms, _noiseFloor * SpeechOverFloor);
                if (isSpeech)
                {
                    bool firstSpeech = _speechLevel < 0;
                    _speechLevel = firstSpeech ? r : _speechLevel * 0.8 + r * 0.2;
                    double target = Math.Min(MaxGain, Math.Max(1.0, TargetRms / Math.Max(_speechLevel, 1.0)));
                    if (firstSpeech) CurrentGain = target;
                    else if (target < CurrentGain) CurrentGain = target;
                    else CurrentGain = CurrentGain + (target - CurrentGain) * Rise;
                }
            }

            double cap = p > 0 ? PeakCeiling / p : MaxGain;
            double end = Math.Max(1.0, Math.Min(CurrentGain, cap));
            double start = Math.Max(1.0, Math.Min(_previousApplied, cap));
            _previousApplied = end;
            if (start == 1.0 && end == 1.0) return pcm;

            var output = new byte[pcm.Length];
            int n = len / 2;
            int k = 0;
            for (int i = 0; i + 1 < len; i += 2, k++)
            {
                double g = start + (end - start) * ((double)k / n);
                double y = SoftLimit(Sample(pcm, i) * g);
                int v = Math.Max(-32768, Math.Min(32767, (int)y));
                output[i] = (byte)(v & 0xFF);
                output[i + 1] = (byte)((v >> 8) & 0xFF);
            }
            if (pcm.Length > len) output[len] = pcm[len];
            return output;
        }
    }
}
