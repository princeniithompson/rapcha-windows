using System;
using System.Globalization;

namespace Rapcha.Core
{
    public enum MicBoostMode { Auto, Off, Fixed }

    /// <summary>v20: the saved Mic boost setting: "auto", "off" or a fixed number of dB (6, 12, 18, 24).</summary>
    public static class MicBoostSetting
    {
        public static readonly string[] Choices = { "auto", "off", "6", "12", "18", "24" };

        public static string Label(string v)
        {
            v = Normalize(v);
            return v == "auto" ? "Auto (recommended)" : v == "off" ? "Off" : "+" + v + " dB always";
        }

        public static string Normalize(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant().TrimStart('+').Replace("db", "").Trim();
            if (v == "off") return "off";
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var db) && db > 0) return Math.Min(30, db).ToString(CultureInfo.InvariantCulture);
            return "auto";
        }

        public static MicAgc Create(string v)
        {
            v = Normalize(v);
            if (v == "auto") return new MicAgc(MicBoostMode.Auto);
            if (v == "off") return new MicAgc(MicBoostMode.Off);
            return new MicAgc(MicBoostMode.Fixed, fixedDb: int.Parse(v, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// v20: automatic microphone gain for the offline engines (built-in Windows speech and Whisper), applied once to the
    /// single 16 kHz mono stream Rapcha records. Quiet laptop microphones (speech peaks around -30 dBFS) are brought up
    /// so speech peaks sit near -11 dBFS, at most +24 dB (16x). Background hiss between words is not boosted (a noise
    /// gate holds the gain at no more than +6 dB when nobody is talking), gain changes are smooth (fast attack, slow
    /// release) and a soft limiter means it never clips. Works in 10 ms frames.
    /// </summary>
    public sealed class MicAgc
    {
        public const double TargetPeakDb = -11.0;
        public const double DefaultMaxGainDb = 24.0;
        public const double GateGainDb = 6.0;
        public const int Frame = 160;                  // 10 ms at 16 kHz
        public const double Knee = 24000.0;            // soft limiter starts here (-2.7 dBFS)
        private const int HangoverFrames = 30;         // 300 ms: word endings are not cut off by the gate
        private const double SpeechOverFloor = 2.8;    // +9 dB over the background
        private const double MinSpeechRms = 12.0;      // about -69 dBFS

        public MicBoostMode Mode { get; }
        public double MaxGain { get; }
        private readonly double _fixedGain;
        private readonly double _targetPeak = 32768.0 * Math.Pow(10, TargetPeakDb / 20);
        private readonly double _gateGain = Math.Pow(10, GateGainDb / 20);

        private double _gain = 1.0, _floor = -1, _env = -1;
        private int _hang;
        private readonly short[] _carry = new short[Frame];
        private int _carryN;

        // statistics
        public long Samples { get; private set; }
        public long SpeechFrames { get; private set; }
        public long Frames { get; private set; }
        public long LimitedSamples { get; private set; }
        public double MaxAppliedGain { get; private set; } = 1.0;
        public double CurrentGain => _gain;
        private double _secGainSum, _secGainMin = double.MaxValue, _secGainMax, _secInPeak, _secOutPeak;
        private int _secFrames, _secSpeech, _secLimited, _second;
        private double _speechGainDbSum; private long _speechGainN;
        public double AverageSpeechGainDb => _speechGainN == 0 ? 0 : _speechGainDbSum / _speechGainN;

        /// <summary>One line per second of audio for Diagnostics (no content).</summary>
        public Action<string> PerSecond;

        public MicAgc(MicBoostMode mode = MicBoostMode.Auto, double maxGainDb = DefaultMaxGainDb, double fixedDb = 0)
        {
            Mode = mode;
            MaxGain = Math.Pow(10, Math.Max(0, Math.Min(36, maxGainDb)) / 20);
            _fixedGain = Math.Pow(10, Math.Max(0, Math.Min(36, fixedDb)) / 20);
        }

        public static double Db(double gain) => 20 * Math.Log10(Math.Max(1e-9, gain));
        public static double DbFs(double amplitude) => amplitude <= 0 ? -96 : Math.Max(-96, 20 * Math.Log10(amplitude / 32768.0));

        public static double SoftLimit(double x)
        {
            double a = Math.Abs(x);
            if (a <= Knee) return x;
            double over = a - Knee, room = 32767.0 - Knee;
            double y = Knee + room * Math.Tanh(over / room);
            return x < 0 ? -y : y;
        }

        /// <summary>16-bit little-endian mono PCM in, the same length out (frames are carried across calls).</summary>
        public byte[] Process(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2 || Mode == MicBoostMode.Off) return pcm;
            int n = pcm.Length / 2;
            var input = new short[n];
            for (int i = 0; i < n; i++) input[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
            var output = Process(input);
            var o = new byte[pcm.Length];
            for (int i = 0; i < output.Length; i++) { o[2 * i] = (byte)output[i]; o[2 * i + 1] = (byte)(output[i] >> 8); }
            if ((pcm.Length & 1) == 1) o[pcm.Length - 1] = pcm[pcm.Length - 1];
            return o;
        }

        /// <summary>Samples in, the same number out. Gain is decided per 10 ms frame and ramped smoothly across it.</summary>
        public short[] Process(short[] input)
        {
            if (Mode == MicBoostMode.Off) return input;
            var o = new short[input.Length];
            // Gain for the samples is decided per frame; a partial frame at the end uses the current gain and is analysed
            // together with the next call's samples.
            int at = 0;
            while (at < input.Length)
            {
                int take = Math.Min(Frame - _carryN, input.Length - at);
                Array.Copy(input, at, _carry, _carryN, take);
                int startCarry = _carryN;
                _carryN += take;
                if (_carryN == Frame)
                {
                    double before = _gain;
                    Decide(_carry);
                    for (int i = 0; i < take; i++)
                    {
                        double t = (startCarry + i + 1) / (double)Frame;
                        double g = before + (_gain - before) * t;
                        o[at + i] = Apply(input[at + i], g);
                    }
                    _carryN = 0;
                }
                else
                    for (int i = 0; i < take; i++) o[at + i] = Apply(input[at + i], _gain);
                at += take;
            }
            Samples += input.Length;
            return o;
        }

        private short Apply(short x, double g)
        {
            double y = x * g;
            if (Math.Abs(y) > Knee) { y = SoftLimit(y); LimitedSamples++; _secLimited++; }
            double a = Math.Abs(y);
            if (a > _secOutPeak) _secOutPeak = a;
            return (short)Math.Round(Math.Max(-32767, Math.Min(32767, y)));
        }

        private void Decide(short[] f)
        {
            double sum = 0; int peak = 0;
            for (int i = 0; i < Frame; i++) { int v = f[i]; sum += (double)v * v; int a = Math.Abs(v); if (a > peak) peak = a; }
            double rms = Math.Sqrt(sum / Frame);
            if (peak > _secInPeak) _secInPeak = peak;
            Frames++;

            if (Mode == MicBoostMode.Fixed) { _gain = _fixedGain; Stats(false); return; }

            // background level: follows quiet frames down quickly and creeps up slowly
            if (_floor < 0) _floor = Math.Max(2.0, rms);
            else if (rms < _floor) _floor = Math.Max(2.0, _floor * 0.6 + rms * 0.4);
            else _floor = _floor * 0.998 + rms * 0.002;

            bool speechNow = rms >= Math.Max(MinSpeechRms, _floor * SpeechOverFloor);
            if (speechNow) _hang = HangoverFrames; else if (_hang > 0) _hang--;
            bool speech = speechNow || _hang > 0;

            if (speechNow)
            {
                // the level speech peaks reach: up at once, down slowly (about 1 s)
                if (_env < 0 || peak > _env) _env = peak; else _env += (peak - _env) * 0.01;
            }
            double want = _env > 0 ? Math.Max(1.0, Math.Min(MaxGain, _targetPeak / _env)) : 1.0;
            if (!speech) want = Math.Min(want, _gateGain);   // nobody talking: do not lift the hiss
            double rate = want < _gain ? (speech ? 0.5 : 0.06) : 0.5;   // fast attack and gate opening, slow gate closing
            _gain += (want - _gain) * rate;
            if (_gain > MaxAppliedGain) MaxAppliedGain = _gain;
            if (speech) { SpeechFrames++; _speechGainDbSum += Db(_gain); _speechGainN++; }
            Stats(speech);
        }

        private void Stats(bool speech)
        {
            _secFrames++;
            if (speech) _secSpeech++;
            _secGainSum += _gain;
            if (_gain < _secGainMin) _secGainMin = _gain;
            if (_gain > _secGainMax) _secGainMax = _gain;
            if (_secFrames < 100) return;
            _second++;
            var line = string.Format(CultureInfo.InvariantCulture,
                "agc second {0}: mode={1} gain_db avg={2:0.0} min={3:0.0} max={4:0.0} in_peak={5:0.0}dBFS out_peak={6:0.0}dBFS speech={7}% limited_samples={8} noise_floor={9:0.0}dBFS",
                _second, Mode, Db(_secGainSum / _secFrames), Db(_secGainMin), Db(_secGainMax), DbFs(_secInPeak), DbFs(_secOutPeak), _secSpeech, _secLimited, DbFs(_floor));
            _secFrames = 0; _secSpeech = 0; _secLimited = 0; _secGainSum = 0; _secGainMin = double.MaxValue; _secGainMax = 0; _secInPeak = 0; _secOutPeak = 0;
            try { PerSecond?.Invoke(line); } catch { }
        }

        public string Summary => string.Format(CultureInfo.InvariantCulture, "agc mode={0} max_gain_db={1:0.0} avg_speech_gain_db={2:0.0} speech_frames={3}/{4} limited_samples={5}",
            Mode, Db(MaxAppliedGain), AverageSpeechGainDb, SpeechFrames, Frames, LimitedSamples);
    }
}
