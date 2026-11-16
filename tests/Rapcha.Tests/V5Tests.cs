using System;
using System.IO;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
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
}
