using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class OcrLayoutTests
    {
        private static OcrWord W(string t, double x, double y, double w = 40, double h = 16) => new OcrWord(t, new Box(x, y, w, h));

        private static OcrLayout Sample()
        {
            var l = new OcrLayout();
            l.AddLine(new[] { W("The", 10, 10, 30), W("quick", 45, 10, 45), W("brown", 95, 10, 50), W("fox", 150, 10, 30) });
            l.AddLine(new[] { W("Second", 10, 40, 60), W("line", 75, 40, 35) });
            l.AddLine(new[] { W("Menu", 400, 10, 50) }); // a second column on the same row as line 1
            return l;
        }

        [Fact]
        public void SelectsWordsMostlyInsideTheBox()
        {
            var sel = Sample().Select(new Box(40, 5, 150, 25)); // covers quick..fox of the first line only
            Assert.Equal(new[] { "quick", "brown", "fox" }, sel.Select(w => w.Text).ToArray());
        }

        [Fact]
        public void BarelyTouchedWordsAreLeftOut()
        {
            // "The" spans 10..40; a box starting at 33 covers less than half of it and not its centre.
            Assert.False(OcrLayout.IsSelected(new Box(10, 10, 30, 16), new Box(33, 0, 200, 100)));
            Assert.True(OcrLayout.IsSelected(new Box(10, 10, 30, 16), new Box(20, 0, 200, 100)));
        }

        [Fact]
        public void ReadingOrderIsTopToBottomThenLeftToRight()
        {
            var sel = Sample().Select(new Box(0, 0, 500, 100));
            Assert.Equal(new[] { "The", "quick", "brown", "fox", "Menu", "Second", "line" }, sel.Select(w => w.Text).ToArray());
        }

        [Fact]
        public void WordsInALineAreSortedEvenIfOcrGaveThemOutOfOrder()
        {
            var l = new OcrLayout();
            l.AddLine(new[] { W("b", 60, 0), W("a", 10, 0), W(" ", 100, 0), W("c", 110, 0) });
            Assert.Equal(new[] { "a", "b", "c" }, l.Lines[0].Words.Select(w => w.Text).ToArray());
        }

        [Fact]
        public void EmptyBoxSelectsNothing()
        {
            Assert.Empty(Sample().Select(new Box(600, 600, 50, 50)));
        }

        [Fact]
        public void TapFindsTheLineUnderTheWord()
        {
            var l = Sample();
            Assert.Same(l.Lines[0], l.LineAt(100, 15));
            Assert.Null(l.LineAt(300, 300));
        }
    }

    public class SpokenTextTests
    {
        [Fact]
        public void CharOffsetsMapBackToWords()
        {
            var layout = new OcrLayout();
            layout.AddLine(new[] { new OcrWord("Open", new Box(0, 0, 40, 10)), new OcrWord("the", new Box(45, 0, 30, 10)), new OcrWord("box.", new Box(80, 0, 40, 10)) });
            layout.AddLine(new[] { new OcrWord("Settings", new Box(0, 20, 60, 10)) });
            var s = new SpokenText(layout.Select(new Box(0, 0, 200, 40)));
            Assert.Equal("Open the box.\nSettings", s.Text);
            Assert.Equal(0, s.WordAtChar(0));
            Assert.Equal(0, s.WordAtChar(3));
            Assert.Equal(1, s.WordAtChar(5));
            Assert.Equal(1, s.WordAtChar(8));   // the space after "the"
            Assert.Equal(2, s.WordAtChar(9));
            Assert.Equal(3, s.WordAtChar(14));
            Assert.Equal(3, s.WordAtChar(999));
            // The voice's own word plan lines up 1:1 with the OCR words.
            var plan = ReadingPlan.Create(s.Text);
            Assert.Equal(s.Count, plan.Words.Count);
            for (int i = 0; i < s.Count; i++) Assert.Equal(s.StartOf(i), plan.Words[i].Start);
        }

        [Fact]
        public void HighlightIsTheWordWithItsLinesHeight()
        {
            var layout = new OcrLayout();
            layout.AddLine(new[] { new OcrWord("Ecommerce", new Box(0, 12, 80, 14)), new OcrWord("page", new Box(90, 10, 40, 20)) });
            var page = PageLayout.Analyze(layout);
            var s = new SpokenText(page.Blocks[0].Words.ToList());
            var h = s.HighlightFor(0);
            Assert.Equal(0, h.X); Assert.Equal(80, h.W);
            Assert.Equal(10, h.Y); Assert.Equal(20, h.H);   // line top and height, not the word's own
        }
    }

    public class BoxSelectorTests
    {
        [Fact]
        public void DragMakesABox()
        {
            var b = new BoxSelector();
            b.OnDown(10, 10); b.OnMove(40, 30);
            Assert.True(b.HasLiveBox);
            var r = b.OnUp(120, 60, false);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(10, r.Box.X); Assert.Equal(110, r.Box.W); Assert.Equal(50, r.Box.H);
        }

        [Fact]
        public void TinyMovementIsATap()
        {
            var b = new BoxSelector();
            b.OnDown(10, 10); b.OnMove(14, 13);
            Assert.False(b.HasLiveBox);
            Assert.Equal(BoxSelector.Kind.TapWord, b.OnUp(14, 13, true).Kind);
        }

        [Fact]
        public void TwoClicksMakeABoxWithoutHolding()
        {
            var b = new BoxSelector();
            b.OnDown(300, 300);
            Assert.Equal(BoxSelector.Kind.CornerSet, b.OnUp(300, 300, false).Kind);
            Assert.True(b.WaitingForSecondCorner);
            b.OnMove(420, 380);                       // the box follows the pointer between clicks
            Assert.Equal(120, b.LiveBox.W);
            b.OnDown(450, 400);
            var r = b.OnUp(450, 400, true);           // even if the second click lands on a word
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(300, r.Box.X); Assert.Equal(150, r.Box.W); Assert.Equal(100, r.Box.H);
        }

        [Fact]
        public void SecondClickOnTheSameSpotCancels()
        {
            var b = new BoxSelector();
            b.OnDown(50, 50); b.OnUp(50, 50, false);
            b.OnDown(52, 51);
            Assert.Equal(BoxSelector.Kind.Cancelled, b.OnUp(52, 51, false).Kind);
            Assert.False(b.HasLiveBox);
        }

        [Fact]
        public void UnknownWordTapCanBecomeACornerLater()
        {
            var b = new BoxSelector();
            b.OnDown(50, 50);
            var r = b.OnUp(50, 50, null);
            Assert.Equal(BoxSelector.Kind.CornerSet, r.Kind); // decided as a corner; the overlay may still read a word once OCR is ready
            b.Cancel();
            b.SetCorner(60, 60);
            Assert.True(b.WaitingForSecondCorner);
        }

        [Fact]
        public void DragStartedFromAPendingCornerStillWorks()
        {
            var b = new BoxSelector();
            b.OnDown(0, 0); b.OnUp(0, 0, false);      // corner set
            b.OnDown(10, 10); b.OnMove(200, 100);
            var r = b.OnUp(200, 100, false);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(0, r.Box.X); Assert.Equal(200, r.Box.W);
        }
    }
}
