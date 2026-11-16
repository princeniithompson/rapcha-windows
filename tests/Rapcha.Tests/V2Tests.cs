using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class ResamplerTests
    {
        private static byte[] Sine(int rate, int channels, double seconds, int amp, double hz = 440)
        {
            int frames = (int)(rate * seconds);
            var b = new byte[frames * channels * 2];
            for (int f = 0; f < frames; f++)
            {
                short s = (short)(amp * Math.Sin(2 * Math.PI * hz * f / rate));
                for (int c = 0; c < channels; c++)
                {
                    int i = (f * channels + c) * 2;
                    b[i] = (byte)s; b[i + 1] = (byte)(s >> 8);
                }
            }
            return b;
        }

        [Theory]
        [InlineData(48000, 1)]
        [InlineData(44100, 1)]
        [InlineData(48000, 2)]
        [InlineData(44100, 2)]
        public void ConvertsTo16kMonoKeepingLevel(int rate, int channels)
        {
            var input = Sine(rate, channels, 1.0, 10000);
            var r = new PcmResampler(rate, channels);
            var output = new List<byte>();
            // Feed in odd-sized 50 ms buffers, like waveIn does.
            int step = rate * channels * 2 / 20;
            for (int off = 0; off < input.Length; off += step)
            {
                int n = Math.Min(step, input.Length - off);
                var part = new byte[n];
                Buffer.BlockCopy(input, off, part, 0, n);
                output.AddRange(r.Process(part, n));
            }
            int samples = output.Count / 2;
            Assert.InRange(samples, 15990, 16000);
            double rmsIn = 10000 / Math.Sqrt(2);
            double rmsOut = SpeechGain.Rms(output.ToArray(), output.Count);
            Assert.InRange(rmsOut, rmsIn * 0.9, rmsIn * 1.05);
        }

        [Fact]
        public void PassThroughAt16kMono()
        {
            var input = Sine(16000, 1, 0.1, 5000);
            var r = new PcmResampler(16000, 1);
            Assert.True(r.IsPassThrough);
            Assert.Equal(input, r.Process(input, input.Length));
        }
    }

    public class LaptopMicLevelTests
    {
        private static byte[] Tone(int amplitude)
        {
            var b = new byte[3200];
            for (int i = 0; i < 1600; i++)
            {
                short s = (short)(amplitude * Math.Sin(i * 2 * Math.PI * 300 / 16000));
                b[i * 2] = (byte)s; b[i * 2 + 1] = (byte)(s >> 8);
            }
            return b;
        }

        [Fact]
        public void QuietLaptopSpeechWasDroppedByAndroidBarsButIsKeptOnWindows()
        {
            // Quiet laptop array mic: speech peaks ~280 (RMS ~200 per 100 ms), room ~30.
            var android = new LiveTurnState(SilenceLevels.Android);
            var windows = new LiveTurnState(SilenceLevels.Windows);
            foreach (var st in new[] { android, windows })
            {
                for (int i = 0; i < 10; i++) st.OnAudioSent(Tone(40));
                for (int i = 0; i < 20; i++) st.OnAudioSent(Tone(280));
            }
            Assert.True(android.IsNearSilent);     // v1 behaviour: never even asked Gemini for a final
            Assert.False(windows.IsNearSilent);
            windows.MarkActivityEnded();
            Assert.Equal("Hello there.", windows.Settle("Hello there.", false, new List<string>()));
        }

        [Fact]
        public void DigitalSilenceIsStillSilentOnWindows()
        {
            var st = new LiveTurnState(SilenceLevels.Windows);
            for (int i = 0; i < 20; i++) st.OnAudioSent(new byte[3200]);
            Assert.True(st.IsNearSilent);
        }

        [Fact]
        public void CountsServerFramesAndUsesOutputTranscriptionAsLastResort()
        {
            var st = new LiveTurnState(SilenceLevels.Windows);
            st.OnAudioSent(Tone(3000));
            st.MarkActivityEnded();
            st.OnServerContent(new ServerMessage { HasServerContent = true, OutputTranscriptionText = "From output." });
            var completed = st.OnServerContent(new ServerMessage { HasServerContent = true, TurnComplete = true });
            Assert.Equal(1, st.OutputFrames);
            Assert.Equal(1, st.TurnCompleteFrames);
            Assert.Equal("From output.", st.Settle(completed, false, new List<string>()));
            Assert.Equal("x", LiveProtocol.Parse("{\"serverContent\":{\"outputTranscription\":{\"text\":\"x\"}}}").OutputTranscriptionText);
        }
    }
}
