using System;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>v20: microphone AGC for the offline engines, and the Whisper CPU guard.</summary>
    public class V20AgcTests
    {
        /// <summary>Noise at noiseDb RMS, then syllable-like bursts whose peaks reach speechPeakDb, then noise again.</summary>
        internal static short[] QuietSpeech(double speechPeakDb, double noiseDb, double leadS, double speechS, double tailS, int seed = 1)
        {
            var rnd = new Random(seed);
            int n = (int)((leadS + speechS + tailS) * 16000);
            var s = new short[n];
            double noise = 32768 * Math.Pow(10, noiseDb / 20) * Math.Sqrt(3);   // uniform noise with that RMS
            double peak = 32768 * Math.Pow(10, speechPeakDb / 20);
            int a = (int)(leadS * 16000), b = (int)((leadS + speechS) * 16000);
            for (int i = 0; i < n; i++)
            {
                double v = (rnd.NextDouble() * 2 - 1) * noise;
                if (i >= a && i < b)
                {
                    double t = (i - a) / 16000.0;
                    double cyc = t % 0.4;                       // 250 ms syllable, 150 ms gap
                    if (cyc < 0.25)
                    {
                        double env = Math.Pow(Math.Sin(Math.PI * cyc / 0.25), 2);
                        double voice = 0.6 * Math.Sin(2 * Math.PI * 180 * t) + 0.3 * Math.Sin(2 * Math.PI * 360 * t) + 0.1 * Math.Sin(2 * Math.PI * 900 * t);
                        v += peak * env * voice;
                    }
                }
                s[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(v)));
            }
            return s;
        }

        private static double PeakDb(short[] s, int from, int to) { int p = 0; for (int i = from; i < to; i++) p = Math.Max(p, Math.Abs((int)s[i])); return MicAgc.DbFs(p); }
        private static double RmsDb(short[] s, int from, int to) { double sum = 0; for (int i = from; i < to; i++) sum += (double)s[i] * s[i]; return MicAgc.DbFs(Math.Sqrt(sum / Math.Max(1, to - from))); }

        private static short[] Run(MicAgc agc, short[] input, int packet = 1600)
        {
            var o = new short[input.Length];
            for (int at = 0; at < input.Length; at += packet)
            {
                int n = Math.Min(packet, input.Length - at);
                var part = new short[n]; Array.Copy(input, at, part, 0, n);
                Array.Copy(agc.Process(part), 0, o, at, n);
            }
            return o;
        }

        [Fact]
        public void QuietSpeechIsBroughtUpToAboutMinus11()
        {
            var input = QuietSpeech(-30, -62, 2, 6, 2);
            var agc = new MicAgc();
            var o = Run(agc, input);
            int a = 3 * 16000, b = 8 * 16000;   // after the first second of speech
            double outPeak = PeakDb(o, a, b);
            Assert.InRange(PeakDb(input, a, b), -33, -29);
            Assert.InRange(outPeak, -14.0, -8.0);
            Assert.True(o.All(x => Math.Abs((int)x) < 32767));
            Assert.InRange(MicAgc.Db(agc.MaxAppliedGain), 16, 24.01);
        }

        [Fact]
        public void NeverClipsEvenWhenSomeoneShouts()
        {
            var input = QuietSpeech(-30, -62, 1, 3, 0).Concat(QuietSpeech(-1, -62, 0, 2, 1, 2)).ToArray();
            var o = Run(new MicAgc(), input);
            Assert.True(o.All(x => Math.Abs((int)x) < 32767));
            Assert.True(PeakDb(o, 4 * 16000 + 8000, o.Length) < -0.1);
        }

        [Fact]
        public void SilenceIsNotBoosted()
        {
            var input = QuietSpeech(-30, -62, 3, 3, 4);
            var o = Run(new MicAgc(), input);
            // lead-in hiss (before anyone spoke): at most the gate's +6 dB
            Assert.True(RmsDb(o, 16000, 3 * 16000) - RmsDb(input, 16000, 3 * 16000) <= 6.1);
            // the hiss after the speech, once the gate has closed (1 s later): back to at most +6 dB
            int from = 7 * 16000, to = 10 * 16000;
            Assert.True(RmsDb(o, from, to) - RmsDb(input, from, to) <= 6.5, "tail boost " + (RmsDb(o, from, to) - RmsDb(input, from, to)));
        }

        [Fact]
        public void LoudSpeechIsLeftAlone()
        {
            var input = QuietSpeech(-8, -60, 1, 3, 1);
            var o = Run(new MicAgc(), input);
            Assert.InRange(PeakDb(o, 2 * 16000, 4 * 16000) - PeakDb(input, 2 * 16000, 4 * 16000), -0.5, 0.5);
        }

        [Fact]
        public void OffAndFixed()
        {
            var input = QuietSpeech(-30, -62, 1, 2, 1);
            Assert.Same(input, new MicAgc(MicBoostMode.Off).Process(input));
            var o = Run(new MicAgc(MicBoostMode.Fixed, fixedDb: 12), input);
            Assert.InRange(PeakDb(o, 0, o.Length) - PeakDb(input, 0, input.Length), 11.5, 12.5);
        }

        [Fact]
        public void BytesInBytesOutAndPerSecondLines()
        {
            var input = QuietSpeech(-30, -62, 1, 2, 1);
            var bytes = new byte[input.Length * 2];
            for (int i = 0; i < input.Length; i++) { bytes[2 * i] = (byte)input[i]; bytes[2 * i + 1] = (byte)(input[i] >> 8); }
            var agc = new MicAgc();
            int lines = 0; string last = "";
            agc.PerSecond = l => { lines++; last = l; };
            int outLen = 0;
            for (int at = 0; at < bytes.Length; at += 3200) { var p = new byte[Math.Min(3200, bytes.Length - at)]; Array.Copy(bytes, at, p, 0, p.Length); outLen += agc.Process(p).Length; }
            Assert.Equal(bytes.Length, outLen);
            Assert.Equal(4, lines);
            Assert.Contains("gain_db avg=", last);
            Assert.Contains("max_gain_db=", agc.Summary);
        }
    }

    public class V20CpuGuardTests
    {
        [Fact]
        public void PicksTheRightWhisperBuild()
        {
            var n3350 = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Sse41 = true, Sse42 = true };
            Assert.Equal(WhisperBuild.Sse, CpuFeatures.Choose(n3350));
            var haswell = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Sse41 = true, Sse42 = true, Avx = true, Avx2 = true, Fma = true, F16c = true, OsAvx = true };
            Assert.Equal(WhisperBuild.Avx2, CpuFeatures.Choose(haswell));
            var noOsAvx = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Avx = true, Avx2 = true, Fma = true, F16c = true, OsAvx = false };
            Assert.Equal(WhisperBuild.Sse, CpuFeatures.Choose(noOsAvx));
            var avx2NoFma = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Avx = true, Avx2 = true, OsAvx = true };
            Assert.Equal(WhisperBuild.Sse, CpuFeatures.Choose(avx2NoFma));
            var old = new CpuFeatures { Sse2 = true, Sse3 = true };
            Assert.Equal(WhisperBuild.None, CpuFeatures.Choose(old));
            Assert.Contains("SSSE3", CpuFeatures.Why(old));
        }

        [Fact]
        public void DescribesTheList()
        {
            var f = new CpuFeatures { Sse2 = true, Sse3 = true, Ssse3 = true, Sse41 = true, Sse42 = true };
            Assert.Equal("SSE2=yes SSE3=yes SSSE3=yes SSE4.1=yes SSE4.2=yes AVX=no AVX2=no FMA=no F16C=no OS-AVX=no", f.ToString());
        }
    }
}

namespace Rapcha.Tests
{
    public class V20BoostSettingTests
    {
        [Theory]
        [InlineData(null, "auto")]
        [InlineData("", "auto")]
        [InlineData("AUTO", "auto")]
        [InlineData("off", "off")]
        [InlineData("+12 dB", "12")]
        [InlineData("99", "30")]
        [InlineData("-3", "auto")]
        public void Normalizes(string input, string want) => Assert.Equal(want, Rapcha.Core.MicBoostSetting.Normalize(input));

        [Fact]
        public void CreatesTheRightMode()
        {
            Assert.Equal(Rapcha.Core.MicBoostMode.Auto, Rapcha.Core.MicBoostSetting.Create("auto").Mode);
            Assert.Equal(Rapcha.Core.MicBoostMode.Off, Rapcha.Core.MicBoostSetting.Create("off").Mode);
            Assert.Equal(Rapcha.Core.MicBoostMode.Fixed, Rapcha.Core.MicBoostSetting.Create("18").Mode);
            Assert.Equal("Auto (recommended)", Rapcha.Core.MicBoostSetting.Label(null));
        }
    }
}
