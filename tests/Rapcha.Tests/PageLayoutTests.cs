using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class PageLayoutTests
    {
        // Words are 8 px per letter with 5 px spaces, like a screen font.
        private static List<OcrWord> Words(string text, double x, double y, double h)
        {
            var list = new List<OcrWord>();
            foreach (var t in text.Split(' '))
            {
                list.Add(new OcrWord(t, new Box(x, y, t.Length * 8, h)));
                x += t.Length * 8 + 5;
            }
            return list;
        }

        private static void Line(OcrLayout l, string text, double x, double y, double h) => l.AddLine(Words(text, x, y, h));

        /// <summary>
        /// Video case (b): left column (header, paragraph, four list items) and template cards on the right
        /// whose text sits level with the list items. Windows OCR joined item 2 with a card line.
        /// </summary>
        private static OcrLayout MainArea()
        {
            var l = new OcrLayout();
            Line(l, "Build with Gemini 4 Hydrogen", 40, 100, 28);
            Line(l, "Start building with our newest models and tools", 40, 150, 16);
            Line(l, "for code, data, documents and more. Try one of", 40, 172, 16);
            Line(l, "the ideas below to get going.", 40, 194, 16);
            Line(l, "Review my code before I ship it", 40, 250, 16);
            // One OCR line across both columns: item 2 + card A's title.
            l.AddLine(Words("Find the errors in my hospital bill", 40, 300, 16).Concat(Words("Interactive data visualizations", 640, 300, 16)));
            Line(l, "built to remix & share", 640, 322, 16);
            Line(l, "Should my next car be an EV or a hybrid", 40, 350, 16);
            Line(l, "Spot the red flags in my apartment lease", 40, 400, 16);
            Line(l, "E-commerce Landing Page", 640, 400, 16);
            Line(l, "Sell your products online", 640, 422, 16);
            return l;
        }

        private static string Read(PageLayout page, Box sel) => string.Join(" ", PageLayout.WordsOf(page.Snap(sel)).Select(w => w.Text));

        [Fact]
        public void ColumnsAreReadLikeABook()
        {
            var page = PageLayout.Analyze(MainArea());
            var text = Read(page, new Box(30, 90, 800, 345)); // the user's big box, cutting the cards in half
            Assert.Equal(
                "Build with Gemini 4 Hydrogen " +
                "Start building with our newest models and tools for code, data, documents and more. Try one of the ideas below to get going. " +
                "Review my code before I ship it " +
                "Find the errors in my hospital bill " +
                "Should my next car be an EV or a hybrid " +
                "Spot the red flags in my apartment lease " +
                "Interactive data visualizations built to remix & share " +
                "E-commerce Landing Page Sell your products online", text);
        }

        [Fact]
        public void BlocksAreHeaderParagraphItemsCards()
        {
            var page = PageLayout.Analyze(MainArea());
            var blocks = page.Blocks.Select(b => b.Lines.Count).ToArray();
            Assert.Equal(new[] { 1, 3, 1, 1, 1, 1, 2, 2 }, blocks);
            Assert.StartsWith("Interactive", page.Blocks[6].Lines[0].Text);
            Assert.StartsWith("E-commerce", page.Blocks[7].Lines[0].Text);
        }

        [Fact]
        public void SpokenTextPausesBetweenBlocksButKeepsWordsAligned()
        {
            var page = PageLayout.Analyze(MainArea());
            var words = PageLayout.WordsOf(page.Snap(new Box(30, 90, 560, 130)));
            var s = new SpokenText(words);
            Assert.StartsWith("Build with Gemini 4 Hydrogen.\nStart building", s.Text);
            var plan = ReadingPlan.Create(s.Text);
            Assert.Equal(words.Count, plan.Words.Count);
            for (int i = 0; i < words.Count; i++) Assert.Equal(s.StartOf(i), plan.Words[i].Start);
        }

        [Fact]
        public void TightBoxAroundTheHeadingReadsOnlyTheHeading()
        {
            var page = PageLayout.Analyze(MainArea());
            // Box hugs the heading; its bottom edge just touches the top of the paragraph.
            Assert.Equal("Build with Gemini 4 Hydrogen", Read(page, new Box(35, 95, 260, 58)));
        }

        /// <summary>Video case (a): right sidebar, title + model dropdown (same words), then a 4-line paragraph.</summary>
        private static OcrLayout Sidebar()
        {
            var l = new OcrLayout();
            Line(l, "Gemini 4 Hydrogen", 1100, 100, 20);
            Line(l, "Gemini 4 Hydrogen", 1100, 140, 14);
            Line(l, "Our most capable model for complex", 1100, 180, 14);
            Line(l, "reasoning, coding and long documents", 1100, 200, 14);
            Line(l, "with a large context window and fast", 1100, 220, 14);
            Line(l, "responses at a lower price.", 1100, 240, 14);
            return l;
        }

        [Fact]
        public void BoxEndingAboveTheLastLineNeverReadsIt()
        {
            // v13: nothing outside the box. The box ends above the paragraph's last line: every line it covers is read,
            // in order, and the last line is not (v12 snapped to the whole paragraph and read it too).
            var page = PageLayout.Analyze(Sidebar());
            var parts = page.Snap(new Box(1090, 90, 320, 146));
            var text = string.Join(" ", PageLayout.WordsOf(parts).Select(w => w.Text));
            Assert.Equal("Gemini 4 Hydrogen Gemini 4 Hydrogen Our most capable model for complex reasoning, coding and long documents with a large context window and fast", text);
            Assert.Equal("Gemini 4 Hydrogen Gemini 4 Hydrogen Our most capable model for complex reasoning, coding and long documents with a large context window and fast responses at a lower price.",
                         string.Join(" ", PageLayout.WordsOf(page.SnapLegacy(new Box(1090, 90, 320, 146))).Select(w => w.Text)));
        }

        [Fact]
        public void BoxAroundOneLineOfAParagraphReadsThatLine()
        {
            var page = PageLayout.Analyze(Sidebar());
            Assert.Equal("reasoning, coding and long documents", Read(page, new Box(1095, 197, 320, 19)));
        }

        [Fact]
        public void BarelyTouchedParagraphIsDropped()
        {
            var page = PageLayout.Analyze(Sidebar());
            // Around the dropdown, the bottom edge 3 px into the paragraph's first line.
            Assert.Equal("Gemini 4 Hydrogen", Read(page, new Box(1095, 135, 200, 48)));
        }

        [Fact]
        public void SmallBoxInsideAParagraphReadsItsWholeLine()
        {
            // v14 (like Android): a line the box touches is read whole, never cut at the box edge.
            var page = PageLayout.Analyze(Sidebar());
            Assert.Equal("Our most capable model for complex", Read(page, new Box(1150, 178, 100, 18)));
        }

        [Fact]
        public void RepeatsAreKeptUnlessTheyOverlap()
        {
            var l = new OcrLayout();
            Line(l, "Gemini 4 Hydrogen", 100, 100, 20);
            Line(l, "Gemini 4 Hydrogen", 101, 101, 20); // OCR read the same line twice
            Line(l, "Gemini 4 Hydrogen", 100, 140, 14); // the dropdown below: a real repeat
            var page = PageLayout.Analyze(l);
            Assert.Equal(2, page.Lines.Count);
        }

        [Fact]
        public void FullWidthHeaderThenTwoColumnsWithLevelGaps()
        {
            var l = new OcrLayout();
            Line(l, "Weekly report for the whole team and partners across regions", 40, 40, 20);
            // Left column: two paragraphs; right column: two paragraphs whose gap is level with the left gap.
            Line(l, "Left one first line here", 40, 100, 16);
            Line(l, "Left one second line", 40, 120, 16);
            Line(l, "Left two first line", 40, 170, 16);
            Line(l, "Right one first line", 400, 100, 16);
            Line(l, "Right one second line", 400, 120, 16);
            Line(l, "Right two first line", 400, 170, 16);
            var page = PageLayout.Analyze(l);
            var text = Read(page, new Box(0, 0, 800, 300));
            Assert.Equal("Weekly report for the whole team and partners across regions Left one first line here Left one second line Left two first line Right one first line Right one second line Right two first line", text);
        }

        [Fact]
        public void BulletsAreNotReadAndStayWithTheirItem()
        {
            var l = new OcrLayout();
            l.AddLine(new[] { new OcrWord("•", new Box(40, 100, 6, 16)) }.Concat(Words("Milk and bread", 80, 100, 16)));
            l.AddLine(new[] { new OcrWord("•", new Box(40, 140, 6, 16)) });
            Line(l, "Eggs", 80, 140, 16);
            var page = PageLayout.Analyze(l);
            Assert.Equal("Milk and bread Eggs", Read(page, new Box(0, 0, 400, 300)));
        }

        [Fact]
        public void TightBoxAroundOneLineOfTwoReadsOnlyThatLine()
        {
            var l = new OcrLayout();
            Line(l, "The quick brown fox jumps over the lazy dog.", 10, 10, 16);
            Line(l, "This second line must not be read.", 10, 32, 16);
            var page = PageLayout.Analyze(l);
            Assert.Single(page.Blocks);
            Assert.Equal("The quick brown fox jumps over the lazy dog.", Read(page, new Box(6, 6, 375, 24)));
            // A loose box over the same line still never reads the line below it (v12 read the whole paragraph).
            Assert.Equal("The quick brown fox jumps over the lazy dog.", Read(page, new Box(0, 0, 600, 27)));
            // The box clips the top of the second line by 4 px (25% of its height): still only the first line.
            Assert.Equal("The quick brown fox jumps over the lazy dog.", Read(page, new Box(0, 0, 600, 36)));
            // The box covers half of the second line: both lines, in order.
            Assert.Equal("The quick brown fox jumps over the lazy dog. This second line must not be read.", Read(page, new Box(0, 0, 600, 40)));
        }

        [Fact]
        public void EmptyAreaReadsNothing()
        {
            var page = PageLayout.Analyze(MainArea());
            Assert.Empty(page.Snap(new Box(1200, 600, 100, 50)));
        }
    }
}
