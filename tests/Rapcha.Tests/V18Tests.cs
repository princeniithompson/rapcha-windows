using System;
using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>v18: the audio path to the offline engines, the memory guard and "a stop never restarts by itself".</summary>
    public class V18ConversionTests
    {
        /// <summary>Interleaved 32-bit float, like a 48 kHz stereo laptop microphone driver delivers.</summary>
        private static byte[] FloatStereo(int rate, double seconds, Func<double, double> left, Func<double, double> right)
        {
            int n = (int)(rate * seconds);
            var b = new byte[n * 8];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                Buffer.BlockCopy(BitConverter.GetBytes((float)left(t)), 0, b, i * 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((float)right(t)), 0, b, i * 8 + 4, 4);
            }
            return b;
        }

        private static byte[] Int16Mono(int rate, double seconds, Func<double, double> f)
        {
            int n = (int)(rate * seconds);
            var b = new byte[n * 2];
            for (int i = 0; i < n; i++) { short s = (short)Math.Round(f(i / (double)rate) * 32767); b[2 * i] = (byte)s; b[2 * i + 1] = (byte)(s >> 8); }
            return b;
        }

        /// <summary>Feeds the resampler in driver-sized buffers (50 ms, plus an odd split to test frame alignment).</summary>
        private static short[] Run(PcmResampler r, byte[] input, int step)
        {
            var o = new List<byte>();
            int off = 0;
            bool odd = true;
            while (off < input.Length)
            {
                int n = Math.Min(odd ? step + 3 : step - 3, input.Length - off);   // deliberately not frame-aligned
                odd = !odd;
                var part = new byte[n];
                Buffer.BlockCopy(input, off, part, 0, n);
                o.AddRange(r.Process(part, n));
                off += n;
            }
            return WavFile.ToSamples(o.ToArray());
        }

        private static double Rms(short[] s, int from = 0) { double a = 0; for (int i = from; i < s.Length; i++) a += (double)s[i] * s[i]; return Math.Sqrt(a / Math.Max(1, s.Length - from)); }

        /// <summary>Strength of one frequency in the signal (Goertzel), relative to full scale.</summary>
        private static double Tone(short[] s, double freq, int rate = 16000, int from = 1000)
        {
            double w = 2 * Math.PI * freq / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            int n = s.Length - from;
            for (int i = from; i < s.Length; i++) { double v = s[i] / 32768.0 + c * s1 - s2; s2 = s1; s1 = v; }
            double power = s1 * s1 + s2 * s2 - c * s1 * s2;
            return Math.Sqrt(Math.Max(0, power)) * 2 / n;
        }

        [Fact]
        public void Float48kStereoBecomes16kMonoAtTheRightSpeed()
        {
            // 1 kHz at half scale in both channels, 2 s
            var input = FloatStereo(48000, 2.0, t => 0.5 * Math.Sin(2 * Math.PI * 1000 * t), t => 0.5 * Math.Sin(2 * Math.PI * 1000 * t));
            var r = new PcmResampler(48000, 2, SampleKind.Float32);
            Assert.False(r.IsPassThrough);
            var o = Run(r, input, 48000 * 8 / 20);
            Assert.InRange(o.Length, 31950, 32000);                  // 2 s at 16 kHz: not 3x too long (48k fed as 16k)
            Assert.InRange(Tone(o, 1000), 0.45, 0.52);               // still 1 kHz (not 333 Hz or 3 kHz), level kept
            Assert.True(Tone(o, 333.3) < 0.01 && Tone(o, 3000) < 0.01);
            Assert.InRange(Rms(o, 1000), 0.5 * 32767 / Math.Sqrt(2) * 0.95, 0.5 * 32767 / Math.Sqrt(2) * 1.03);   // scaled, not static
        }

        [Fact]
        public void Int16At44kBecomes16k()
        {
            var input = Int16Mono(44100, 1.5, t => 0.3 * Math.Sin(2 * Math.PI * 440 * t));
            var o = Run(new PcmResampler(44100, 1, SampleKind.Int16), input, 44100 * 2 / 20);
            Assert.InRange(o.Length, 23950, 24000);
            Assert.InRange(Tone(o, 440), 0.27, 0.31);
        }

        [Fact]
        public void HighFrequenciesDoNotFoldBackIntoSpeech()
        {
            // 12 kHz can't exist at 16 kHz; a plain decimator would fold it to 4 kHz, right in the speech band.
            var input = FloatStereo(48000, 1.0, t => 0.5 * Math.Sin(2 * Math.PI * 12000 * t), t => 0.5 * Math.Sin(2 * Math.PI * 12000 * t));
            var o = Run(new PcmResampler(48000, 2, SampleKind.Float32), input, 19200);
            Assert.True(Tone(o, 4000) < 0.005, "aliasing " + Tone(o, 4000));
        }

        [Fact]
        public void DownmixAveragesTheChannels()
        {
            // speech only in the left channel (one live mic, one dead): half level, never cancelled or doubled
            var input = FloatStereo(48000, 1.0, t => 0.6 * Math.Sin(2 * Math.PI * 500 * t), t => 0.0);
            var o = Run(new PcmResampler(48000, 2, SampleKind.Float32), input, 19200);
            Assert.InRange(Tone(o, 500), 0.28, 0.31);
            // opposite phase in the two channels cancels: that is what a mono mix does, and the test documents it
            var r2 = new PcmResampler(48000, 2, SampleKind.Float32);
            var frame = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(0.25f), 0, frame, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(0.75f), 0, frame, 4, 4);
            Assert.Equal(0.5f, r2.Frame(frame, 0), 5);
        }

        [Fact]
        public void ScalingAndClipping()
        {
            Assert.Equal(32767, PcmResampler.ToPcm16(1.0f));
            Assert.Equal(32767, PcmResampler.ToPcm16(1.7f));          // over-range floats clip, never wrap to negative
            Assert.Equal(-32768, PcmResampler.ToPcm16(-1.5f));
            Assert.Equal(0, PcmResampler.ToPcm16(0f));
            Assert.InRange((int)PcmResampler.ToPcm16(0.5f), 16383, 16384);
            var b = new byte[4];
            b[0] = 0x00; b[1] = 0x80;                                  // little-endian -32768
            Assert.Equal(-1f, PcmResampler.Decode(b, 0, SampleKind.Int16));
            var i24 = new byte[] { 0x00, 0x00, 0x40 };                 // 0x400000 = +0.5
            Assert.Equal(0.5f, PcmResampler.Decode(i24, 0, SampleKind.Int24), 5);
            var f = BitConverter.GetBytes(-0.25f);
            Assert.Equal(-0.25f, PcmResampler.Decode(f, 0, SampleKind.Float32));
            Assert.Equal(0f, PcmResampler.Decode(BitConverter.GetBytes(float.NaN), 0, SampleKind.Float32));
        }

        [Fact]
        public void PassThroughStaysExactAndOddBuffersKeepAlignment()
        {
            var input = Int16Mono(16000, 0.5, t => 0.4 * Math.Sin(2 * Math.PI * 300 * t));
            var r = new PcmResampler(16000, 1);
            Assert.True(r.IsPassThrough);
            var o = new List<byte>();
            o.AddRange(r.Process(input.Take(1001).ToArray(), 1001));     // an odd byte count
            var rest = input.Skip(1001).ToArray();
            o.AddRange(r.Process(rest, rest.Length));
            Assert.Equal(input, o.ToArray());
        }

        [Fact]
        public void SixteenKStereoFloatIsConvertedNotPassedThrough()
        {
            var input = FloatStereo(16000, 1.0, t => 0.5 * Math.Sin(2 * Math.PI * 700 * t), t => 0.5 * Math.Sin(2 * Math.PI * 700 * t));
            var r = new PcmResampler(16000, 2, SampleKind.Float32);
            Assert.False(r.IsPassThrough);
            var o = Run(r, input, 6400);
            Assert.Equal(16000, o.Length);
            Assert.InRange(Tone(o, 700), 0.48, 0.51);
        }
    }

    public class V18VadTests
    {
        private static short[] Make(int n, Func<int, double> f) => Enumerable.Range(0, n).Select(i => (short)Math.Max(-32768, Math.Min(32767, f(i)))).ToArray();

        [Fact]
        public void SilenceIsNotSpeech()
        {
            Assert.False(SpeechVad.Analyze(new short[32000]).HasSpeech);
            Assert.Null(SpeechVad.Trim(new short[32000], out _));
        }

        [Fact]
        public void SteadyNoiseIsNotSpeech()
        {
            var rnd = new Random(3);
            var noise = Make(48000, i => (rnd.NextDouble() - 0.5) * 2000);   // fan / hum, -30 dBFS
            Assert.False(SpeechVad.Analyze(noise).HasSpeech);
        }

        [Fact]
        public void AClickIsNotSpeech()
        {
            var rnd = new Random(4);
            var s = Make(32000, i => (rnd.NextDouble() - 0.5) * 100 + (i >= 16000 && i < 16000 + 900 ? 12000 * Math.Sin(i) : 0));  // 56 ms tap
            Assert.False(SpeechVad.Analyze(s).HasSpeech);
        }

        [Fact]
        public void SpeechIsFoundAndTrimmedWithPadding()
        {
            var rnd = new Random(5);
            // 1 s quiet, 1.2 s "speech" (a modulated tone) at a quiet laptop level, 1 s quiet
            var s = Make(51200, i => (rnd.NextDouble() - 0.5) * 120 + (i >= 16000 && i < 35200 ? 1500 * Math.Sin(i * 0.12) * (0.6 + 0.4 * Math.Sin(i * 0.002)) : 0));
            var t = SpeechVad.Trim(s, out var r);
            Assert.True(r.HasSpeech);
            Assert.NotNull(t);
            Assert.InRange(r.Start, 16000 - 16 * 320, 16000 - 10 * 320);    // ~300 ms before
            Assert.InRange(r.End, 35200 + 10 * 320, 35200 + 22 * 320);      // ~400 ms after
            Assert.InRange(r.SpeechSeconds, 1.0, 1.25);
        }

        [Fact]
        public void QuietSpeechIsBroughtUpButNeverClipped()
        {
            var s = Make(16000, i => 400 * Math.Sin(i * 0.1));
            var o = SpeechVad.Normalize(s, 283, out var g);
            Assert.InRange(g, 9.9, 10.0);                                    // capped at 10x
            Assert.True(o.Max(x => Math.Abs((int)x)) <= 29500);
            var loud = Make(16000, i => 20000 * Math.Sin(i * 0.1));
            var o2 = SpeechVad.Normalize(loud, 14000, out var g2);
            Assert.Equal(1.0, g2);
            Assert.Same(loud, o2);
        }

        [Fact]
        public void ShortSpeechIsPaddedForWhisper()
        {
            var p = SpeechVad.PadForWhisper(new short[8000]);
            Assert.Equal(24000, p.Length);
            Assert.Equal(48000, SpeechVad.PadForWhisper(new short[48000]).Length);
        }
    }

    public class V18WhisperTextTests
    {
        [Theory]
        [InlineData("my name is Prince and I", "and I live in Accra.", "live in Accra.")]
        [InlineData("we went to the market", "the market was busy", "was busy")]
        [InlineData("Hello there.", "Something new.", "Something new.")]
        [InlineData("", "Fresh start", "Fresh start")]
        [InlineData("I said a", "a big thing", "a big thing")]          // one short word: chance, kept
        [InlineData("Rapcha is great", "great tool", "tool")]          // one 4+ letter word: overlap, removed
        public void OverlapIsRemovedOnce(string prev, string next, string want) => Assert.Equal(want, WhisperText.Dedupe(prev, next));

        [Fact]
        public void StockInventionsAreDroppedOnlyWithLittleSpeech()
        {
            Assert.True(WhisperText.IsLikelyInvention("Thanks for watching!", 0.8));
            Assert.True(WhisperText.IsLikelyInvention("[BLANK_AUDIO]", 3));
            Assert.True(WhisperText.IsLikelyInvention("", 1));
            Assert.True(WhisperText.IsLikelyInvention("you", 0.3));
            Assert.False(WhisperText.IsLikelyInvention("Thank you.", 1.2));      // a real, short "thank you"
            Assert.False(WhisperText.IsLikelyInvention("My name is Prince.", 1.0));
            Assert.False(WhisperText.IsLikelyInvention("Thanks for watching", 4.0));
        }

        [Fact]
        public void OverlappingChunksKeepEverySampleOnceAtTheJoin()
        {
            var c = new OfflineChunker(overlapMs: 1000);
            var pieces = new List<short[]>();
            for (int t = 0; t < 650; t++)    // 65 s
            {
                var b = new byte[3200];
                for (int i = 0; i < 1600; i++) { short v = (short)(t * 7 + i % 13); b[2 * i] = (byte)v; b[2 * i + 1] = (byte)(v >> 8); }
                pieces.AddRange(c.Add(b));
            }
            var last = c.Flush();
            Assert.Equal(2, pieces.Count);
            Assert.NotNull(last);
            int total = pieces.Sum(p => p.Length) + last.Length;
            Assert.Equal(65 * 16000 + 2 * 16000, total);                  // each join repeats exactly 1 s
            // the second piece starts with the last second of the first
            Assert.Equal(pieces[0].Skip(pieces[0].Length - 16000).Take(50), pieces[1].Take(50));
            Assert.Null(c.Flush());
        }

        [Fact]
        public void FlushAfterACutWithNothingNewGivesNothing()
        {
            var c = new OfflineChunker(targetSeconds: 2, windowSeconds: 1, overlapMs: 500);
            var got = c.Add(new byte[2 * 16000 * 2]);
            Assert.Single(got);
            // only the overlap is left (and maybe a little after the cut); never a piece that is just the overlap again
            var rest = c.Flush();
            Assert.True(rest == null || rest.Length > 8000);
        }
    }

    public class V18MemoryGuardTests
    {
        [Fact]
        public void FourGigLaptopWithLittleFreeMemoryKeepsDictating()
        {
            // What the user's laptop looked like: ~250 MB free (75-90 % in use), Rapcha ~520 MB with Whisper loaded.
            var g = new MemoryGuard();
            for (int t = 0; t < 120; t++)
            {
                Assert.False(g.ShouldStop(t, SpeechEngine.Whisper, 520, 250, 200000, out var why), why);
            }
            var b = new MemoryGuard();
            for (int t = 0; t < 300; t++) Assert.False(b.ShouldStop(t, SpeechEngine.BuiltIn, 180, 40, 64000, out _));   // built-in: never for Windows' memory
        }

        [Fact]
        public void OldThresholdsNoLongerStop()
        {
            Assert.False(SessionCaps.ShouldStop(1, 120, SpeechEngine.Whisper, 250, out _));     // the v16-v17 false stop at ~1 s
            Assert.False(SessionCaps.ShouldStop(1, 300, SpeechEngine.BuiltIn, 150, out _));
            Assert.True(SessionCaps.ShouldStop(120, 120, SpeechEngine.Whisper, 250, out var r)); Assert.Equal("time_limit", r);
        }

        [Fact]
        public void RapchasOwnGrowthStops()
        {
            var g = new MemoryGuard();
            Assert.True(g.ShouldStop(5, SpeechEngine.Whisper, 1700, 2000, 0, out var why)); Assert.Equal("rapcha_memory", why);
            Assert.True(new MemoryGuard().ShouldStop(5, SpeechEngine.BuiltIn, 1100, 2000, 0, out why)); Assert.Equal("rapcha_memory", why);
            Assert.True(new MemoryGuard().ShouldStop(5, SpeechEngine.BuiltIn, 200, 2000, MemoryGuard.HeldAudioLimitBytes + 1, out why)); Assert.Equal("audio_buffer", why);
            Assert.False(new MemoryGuard().ShouldStop(5, SpeechEngine.Gemini, 5000, 10, long.MaxValue, out _));
        }

        [Fact]
        public void SystemFloorNeedsFiveSustainedSeconds()
        {
            var g = new MemoryGuard();
            Assert.False(g.ShouldStop(10, SpeechEngine.Whisper, 500, 80, 0, out _));
            Assert.False(g.ShouldStop(12, SpeechEngine.Whisper, 500, 70, 0, out _));
            Assert.False(g.ShouldStop(13, SpeechEngine.Whisper, 500, 150, 0, out _));   // recovered: the count restarts
            Assert.False(g.ShouldStop(14, SpeechEngine.Whisper, 500, 90, 0, out _));
            Assert.False(g.ShouldStop(18, SpeechEngine.Whisper, 500, 90, 0, out _));
            Assert.True(g.ShouldStop(19, SpeechEngine.Whisper, 500, 90, 0, out var why));
            Assert.Equal("system_memory_floor", why);
        }
    }

    public class V18StartGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 17, 5, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AnAutomaticStopNeverOpensANewDictationFromALateTap()
        {
            var g = new StartGate();
            g.Stopped(automatic: true, T0);                     // memory guard / time limit at ~1 s
            g.BackToIdle(T0.AddMilliseconds(1800));             // words typed
            Assert.False(g.Allow(T0.AddMilliseconds(1900)));    // the user's own "stop" tap arriving late
            Assert.False(g.Allow(T0.AddMilliseconds(4500)));
            Assert.Equal(2, g.Ignored);
            Assert.True(g.Allow(T0.AddMilliseconds(1800 + StartGate.AfterAutoStopMs)));   // a fresh tap later works
        }

        [Fact]
        public void YourOwnStopLetsTheNextTapStartAtOnce()
        {
            var g = new StartGate();
            g.Stopped(automatic: false, T0);
            g.BackToIdle(T0.AddMilliseconds(600));
            Assert.True(g.Allow(T0.AddMilliseconds(601)));
            Assert.Equal(0, g.Ignored);
        }

        [Fact]
        public void ALaterUserStopDoesNotShortenTheQuietTime()
        {
            var g = new StartGate();
            g.Stopped(true, T0);
            g.Stopped(false, T0.AddMilliseconds(100));
            Assert.False(g.Allow(T0.AddMilliseconds(500)));
        }
    }

    public class V18WavAndCheckTests
    {
        [Fact]
        public void WavHeaderIs16kMono16Bit()
        {
            var w = WavFile.Build(new byte[3200]);
            Assert.Equal(3244, w.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(w, 0, 4));
            Assert.Equal(16000, BitConverter.ToInt32(w, 24));
            Assert.Equal(1, BitConverter.ToInt16(w, 22));
            Assert.Equal(16, BitConverter.ToInt16(w, 34));
            Assert.Equal(3200, BitConverter.ToInt32(w, 40));
        }

        [Fact]
        public void MicCheckSaysWhatItHeard()
        {
            Assert.Contains("Silence", MicCheck.Of(new short[48000], 0).Verdict);
            var rnd = new Random(1);
            var speech = Enumerable.Range(0, 48000).Select(i => (short)((rnd.NextDouble() - 0.5) * 60 + (i > 12000 && i < 36000 ? 6000 * Math.Sin(i * 0.1) : 0))).ToArray();
            var c = MicCheck.Of(speech, 0);
            Assert.True(c.SpeechSeconds > 1);
            Assert.Contains("clearly", c.Verdict);
            Assert.Contains("missing", MicCheck.Of(speech, 400).Verdict);
        }
    }
}
