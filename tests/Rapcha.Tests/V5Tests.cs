using System;
using System.IO;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class SelectionGestureTests
    {
        private static SelectionGesture.Result Click(SelectionGesture g, int x, int y, int jitter = 0)
        {
            g.OnDown(x, y, false, true);
            if (jitter != 0) g.OnMove(x + jitter, y);
            return g.OnUp(x + jitter, y, false);
        }

        [Fact]
        public void PlainClickIsNotASelection()
        {
            var g = new SelectionGesture();
            var r = Click(g, 500, 300);
            Assert.Equal(SelectionGesture.Kind.None, r.Kind);
            Assert.Equal("click", r.Why);
        }

        [Fact]
        public void DoubleAndTripleClicksAreNotSelections()
        {
            var g = new SelectionGesture();
            for (int i = 0; i < 3; i++) Assert.Equal(SelectionGesture.Kind.None, Click(g, 500, 300, jitter: i).Kind);
        }

        [Fact]
        public void SmallHandJitterIsStillAClick()
        {
            var g = new SelectionGesture(8);
            Assert.Equal(SelectionGesture.Kind.None, Click(g, 100, 100, jitter: 7).Kind);
        }

        [Fact]
        public void RealDragIsASelection()
        {
            var g = new SelectionGesture(8);
            g.OnDown(100, 100, false, true);
            g.OnMove(140, 102);
            g.OnMove(260, 104);
            var r = g.OnUp(260, 104, false);
            Assert.Equal(SelectionGesture.Kind.Drag, r.Kind);
            Assert.True(r.TextCursorAtDown);
            Assert.Equal(100, r.DownX);
        }

        [Fact]
        public void DragThatComesBackToTheStartStillCounts()
        {
            var g = new SelectionGesture(8);
            g.OnDown(100, 100, false, false);
            g.OnMove(100, 160);
            Assert.Equal(SelectionGesture.Kind.Drag, g.OnUp(102, 100, false).Kind);
        }

        [Fact]
        public void TitleBarOrResizeDragIsIgnored()
        {
            var g = new SelectionGesture(8);
            g.OnDown(100, 10, false, false);
            g.OnMove(400, 200);
            var r = g.OnUp(400, 200, windowMovedOrResized: true);
            Assert.Equal(SelectionGesture.Kind.None, r.Kind);
            Assert.Equal("window_drag", r.Why);
        }

        [Fact]
        public void DragStartingOnRapchaIsIgnored()
        {
            var g = new SelectionGesture(8);
            g.OnDown(100, 10, true, false);
            Assert.Equal(SelectionGesture.Kind.None, g.OnUp(400, 200, false).Kind);
        }

        [Fact]
        public void ReleaseWithoutPressIsIgnored()
        {
            var g = new SelectionGesture(8);
            Assert.Equal(SelectionGesture.Kind.None, g.OnUp(400, 200, false).Kind);
            Click(g, 1, 1);
            Assert.Equal(SelectionGesture.Kind.None, g.OnUp(400, 200, false).Kind); // second up after a click
        }
    }

    public class PhraseTimingTests
    {
        [Fact]
        public void FractionMapsAcrossThePhraseInOrder()
        {
            var p = ReadingPlan.Create("Put the food in the container, open the container.");
            int end = p.SegmentEnd(0);
            Assert.Equal(6, end);
            Assert.Equal(0, PhraseTiming.WordAt(p, 0, end, 0));
            Assert.Equal(5, PhraseTiming.WordAt(p, 0, end, 1));
            int last = -1;
            for (double f = 0; f <= 1.0001; f += 0.02)
            {
                int w = PhraseTiming.WordAt(p, 0, end, f);
                Assert.InRange(w, 0, end - 1);
                Assert.True(w >= last);
                last = w;
            }
            // "container," is the longest word: it should own the end of the phrase.
            Assert.Equal(5, PhraseTiming.WordAt(p, 0, end, 0.8));
        }

        [Fact]
        public void SegmentCanStartMidPhraseAfterASkip()
        {
            var p = ReadingPlan.Create("Put the food in the container, open the container.");
            Assert.Equal(6, p.SegmentEnd(3));
            Assert.Equal("in the container,", p.TextRange(3, p.SegmentEnd(3)));
            Assert.Equal(3, PhraseTiming.WordAt(p, 3, 6, 0.01));
            Assert.Equal(9, p.SegmentEnd(7));
            Assert.Equal("open the container.", p.TextRange(6, 9));
        }
    }

    public class WavDataTests
    {
        private static byte[] Wav(short[] samples, int rate = 22050)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write("RIFF".ToCharArray()); w.Write(36 + samples.Length * 2); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }

        [Fact]
        public void ParsesPiperStyleWav()
        {
            var samples = Enumerable.Range(0, 1000).Select(i => (short)(i * 10)).ToArray();
            Assert.True(WavData.TryParse(Wav(samples), out var wav));
            Assert.Equal(22050, wav.SampleRate);
            Assert.Equal(1, wav.Channels);
            Assert.Equal(2000, wav.Pcm.Length);
        }

        [Fact]
        public void RejectsGarbage()
        {
            Assert.False(WavData.TryParse(new byte[] { 1, 2, 3 }, out _));
            Assert.False(WavData.TryParse(System.Text.Encoding.ASCII.GetBytes("RIFF....WAVEjunkjunkjunk"), out _));
        }

        [Fact]
        public void TrimsQuietEdgesButKeepsPadding()
        {
            var samples = new short[3000];
            for (int i = 1000; i < 2000; i++) samples[i] = 5000;
            var pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            var trimmed = WavData.TrimSilence(pcm, 1000, 300, 40); // 40 samples of padding at 1 kHz
            Assert.Equal((1000 + 80) * 2, trimmed.Length);
        }
    }
}
