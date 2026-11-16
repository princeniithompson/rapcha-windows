using System;
using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>v15: Previous / Next segments, the word maps they use, and the outline / number-badge layout.</summary>
    public class V15SegmentTests
    {
        internal static List<OcrWord> Words(string text, double x, double y, double h, double charW = 8, double space = 5)
        {
            var list = new List<OcrWord>();
            foreach (var t in text.Split(' '))
            {
                list.Add(new OcrWord(t, new Box(x, y, t.Length * charW, h)));
                x += t.Length * charW + space;
            }
            return list;
        }

        /// <summary>A heading, a two-line paragraph, a numbered list of two items, another paragraph.</summary>
        internal static OcrLayout Page()
        {
            var l = new OcrLayout();
            l.AddLine(Words("Getting started with Rapcha", 40, 40, 26, 13, 8));
            l.AddLine(Words("Rapcha reads any text on the screen aloud when you", 40, 100, 16));
            l.AddLine(Words("drag a box around it or tap a word in a paragraph.", 40, 122, 16));
            l.AddLine(Words("1. Double-click the ring to start select to speak.", 40, 180, 16));
            l.AddLine(Words("2. Drag a box or tap the text you want to hear.", 40, 220, 16));
            l.AddLine(Words("Press Escape to close it again when you are done.", 40, 280, 16));
            l.AddLine(Words("It also has a second line that wraps here.", 40, 302, 16));
            return l;
        }

        [Fact]
        public void ALineWithoutDescendersStaysInItsParagraph()
        {
            // OCR boxes: a line with no g, p, q, y or j is shorter (its bottom is the baseline), so the gap to the
            // next line looks bigger and the heights look different. It is still one paragraph per column.
            var l = new OcrLayout();
            l.AddLine(Words("Left column text sits close", 80, 413, 11, 7, 4));
            l.AddLine(Words("Right column text beside it", 300, 413, 15, 7, 4));
            l.AddLine(Words("to the right column here.", 80, 437, 15, 7, 4));
            l.AddLine(Words("keeps its own outline too.", 300, 437, 15, 7, 4));
            var page = PageLayout.Analyze(l);
            Assert.True(page.Blocks.Count == 2, string.Join(" | ", page.Blocks.Select(b => string.Join(" / ", b.Lines.Select(x => x.Text + "@" + x.Box.X + "," + x.Box.Y + "," + x.Box.H)))));
            Assert.All(page.Blocks, b => Assert.Equal(2, b.Lines.Count));
            // A real heading (much bigger text, a clear gap) is still its own block.
            var h = new OcrLayout();
            h.AddLine(Words("Settings", 40, 40, 30, 14, 8));
            h.AddLine(Words("Choose the voice that reads to you.", 40, 84, 15));
            Assert.Equal(2, PageLayout.Analyze(h).Blocks.Count);
        }

        [Fact]
        public void OneSegmentPerHeadingParagraphAndListItem()
        {
            var page = PageLayout.Analyze(Page());
            var words = PageLayout.WordsOf(page.Snap(new Box(30, 30, 600, 300))).ToList();
            var segs = Segments.Build(words);
            var texts = segs.Select(s => string.Join(" ", words.Skip(s.First).Take(s.Count).Select(w => w.Text))).ToList();
            Assert.Equal(5, segs.Count);
            Assert.Equal("Getting started with Rapcha", texts[0]);
            Assert.StartsWith("Rapcha reads", texts[1]); Assert.EndsWith("paragraph.", texts[1]);
            Assert.StartsWith("1.", texts[2]);
            Assert.StartsWith("2.", texts[3]);
            Assert.StartsWith("Press Escape", texts[4]); Assert.EndsWith("here.", texts[4]);
            // Segments cover every word once, in order.
            Assert.Equal(0, segs[0].First);
            for (int i = 1; i < segs.Count; i++) Assert.Equal(segs[i - 1].End, segs[i].First);
            Assert.Equal(words.Count, segs[segs.Count - 1].End);
        }

        [Fact]
        public void WordsWithoutABlockSplitByLine()
        {
            var a = Words("one two", 0, 0, 16); foreach (var w in a) { w.Line = 0; w.Block = -1; }
            var b = Words("three four", 0, 30, 16); foreach (var w in b) { w.Line = 1; w.Block = -1; }
            var segs = Segments.Build(a.Concat(b).ToList());
            Assert.Equal(2, segs.Count);
            Assert.Equal(2, segs[1].First);
            Assert.Empty(Segments.Build(new List<OcrWord>()));
        }

        [Fact]
        public void SpokenWordMapsToTheVoicesWordEvenForCodeTokens()
        {
            // ":app:testDebugUnitTest" is said as several words; segment starts must still map exactly.
            var w = Words("Run :app:testDebugUnitTest now", 0, 0, 16); foreach (var x in w) { x.Line = 0; x.Block = 0; }
            var w2 = Words("Next paragraph here", 0, 40, 16); foreach (var x in w2) { x.Line = 1; x.Block = 1; }
            var all = w.Concat(w2).ToList();
            var spoken = new SpokenText(all);
            var plan = ReadingPlan.Create(spoken.Text);
            Assert.True(plan.Words.Count > all.Count);                 // the token became several voice words
            int seg2 = Segments.Build(all)[1].First;                    // OCR word 3: "Next"
            int planWord = plan.WordAtChar(spoken.StartOf(seg2));
            Assert.Equal("Next", plan.WordText(planWord));
            // And back: every voice word inside the token maps to the token.
            int tokenPlanStart = plan.WordAtChar(spoken.StartOf(1));
            Assert.Equal(1, spoken.WordAtChar(plan.Words[tokenPlanStart + 1].Start));
        }
    }

    public class V15NavTests
    {
        private long _now;
        private ReadingNav Nav(int segments, bool canLeave = false, bool next = false, bool prev = false)
        {
            var nav = new ReadingNav(() => _now);
            var segs = Enumerable.Range(0, segments).Select(i => new Segment(i * 10, i * 10 + 10)).ToList();
            nav.Reset(segs, canLeave);
            nav.HasNextBlock = next; nav.HasPreviousBlock = prev;
            return nav;
        }

        [Fact]
        public void NextGoesToTheStartOfTheNextSegmentAndFinishesAtTheEnd()
        {
            var nav = Nav(3);
            nav.OnWord(4);
            var m = nav.Next();
            Assert.Equal(ReadingNav.Kind.Jump, m.Kind); Assert.Equal(10, m.Word);
            m = nav.Next(); Assert.Equal(20, m.Word);
            Assert.Equal(ReadingNav.Kind.Finished, nav.Next().Kind);   // last segment: Finished, no error
        }

        [Fact]
        public void PreviousRestartsUnlessPressedWithinOneAndAHalfSeconds()
        {
            var nav = Nav(3);
            nav.OnWord(12);                       // segment 1 starts at t=0
            _now = 4000;                          // 4 s into it
            var m = nav.Previous();
            Assert.Equal(10, m.Word);             // restart segment 1
            _now += 800;                          // pressed again 0.8 s after that restart
            m = nav.Previous();
            Assert.Equal(0, m.Word);              // the segment before
            _now += 500;
            m = nav.Previous();
            Assert.Equal(0, m.Word);              // first segment: restarts
            _now += 3000;
            Assert.Equal(0, nav.Previous().Word);
        }

        [Fact]
        public void TheWindowStartsWhenTheVoiceEntersASegment()
        {
            var nav = Nav(3);
            _now = 10000; nav.OnWord(21);         // the voice reached segment 2 just now
            _now = 10600;
            Assert.Equal(10, nav.Previous().Word);   // within 1.5 s of its start: the previous segment
            _now = 13000; nav.OnWord(11); nav.OnWord(12);
            _now = 14600;
            Assert.Equal(10, nav.Previous().Word);   // 1.6 s in: restart
        }

        [Fact]
        public void AfterATapNextAndPreviousContinueIntoNeighbouringBlocks()
        {
            var nav = Nav(1, canLeave: true, next: true, prev: true);
            Assert.Equal(ReadingNav.Kind.NextBlock, nav.Next().Kind);
            _now = 100;
            nav.Reset(new List<Segment> { new Segment(0, 5) }, true);
            nav.HasPreviousBlock = true;
            _now = 600;
            Assert.Equal(ReadingNav.Kind.PreviousBlock, nav.Previous().Kind);   // early: the block before
            _now = 5000;
            Assert.Equal(ReadingNav.Kind.Jump, nav.Previous().Kind);            // later: restart
            // A box selection never leaves its blocks.
            var box = Nav(1, canLeave: false, next: true, prev: true);
            Assert.Equal(ReadingNav.Kind.Finished, box.Next().Kind);
            Assert.Equal(ReadingNav.Kind.Jump, box.Previous().Kind);
        }

        [Fact]
        public void NothingToReadMeansNoMove()
        {
            var nav = new ReadingNav(() => 0);
            nav.Reset(new List<Segment>(), false);
            Assert.Equal(ReadingNav.Kind.None, nav.Next().Kind);
            Assert.Equal(ReadingNav.Kind.None, nav.Previous().Kind);
        }
    }

    public class V15BadgeTests
    {
        private static (List<Box> blocks, List<Box> first, List<Box> text) Parts(PageLayout page, Box sel)
        {
            var parts = page.Snap(sel);
            var blocks = parts.Select(p => p.Box).ToList();
            var first = parts.Select(p => p.Words.Where(w => w.Line == p.Words[0].Line).Aggregate(new Box(), (u, w) => u.Union(w.Box))).ToList();
            var text = page.Lines.SelectMany(l => l.Words).Select(w => w.Box).ToList();
            return (blocks, first, text);
        }

        private static void AssertClean(BadgeLayout.Result r, List<Box> text)
        {
            for (int i = 0; i < r.Outlines.Count; i++)
                for (int j = i + 1; j < r.Outlines.Count; j++)
                    Assert.False(BadgeLayout.Overlap(r.Outlines[i], r.Outlines[j]), "outlines " + i + " and " + j + " overlap");
            for (int i = 0; i < r.Badges.Count; i++)
            {
                Assert.False(BadgeLayout.Hits(r.Badges[i], text, 0), "badge " + (i + 1) + " covers text");
                Assert.False(BadgeLayout.Hits(r.Badges[i], r.Outlines, 0), "badge " + (i + 1) + " is on an outline");
                for (int j = i + 1; j < r.Badges.Count; j++) Assert.False(BadgeLayout.Overlap(r.Badges[i], r.Badges[j]), "badges overlap");
            }
        }

        [Fact]
        public void BadgesSitOutsideTheBlocksInReadingOrderAndNeverOverlap()
        {
            var page = PageLayout.Analyze(V15SegmentTests.Page());
            var (blocks, first, text) = Parts(page, new Box(30, 30, 600, 300));
            var r = BadgeLayout.Place(blocks, first, text, new Box(0, 0, 1024, 768), 5, 15, 3);
            Assert.Equal(5, r.Badges.Count);
            AssertClean(r, text);
            for (int i = 0; i < r.Badges.Count; i++)
            {
                Assert.True(r.Badges[i].Right <= r.Outlines[i].X || r.Badges[i].Bottom <= r.Outlines[i].Y, "badge " + (i + 1) + " is outside its block");
                Assert.True(Math.Abs(r.Badges[i].CenterY - first[i].CenterY) < 20, "badge " + (i + 1) + " is beside its first line");
            }
            // Reading order: badge n belongs to the nth block (top to bottom here).
            for (int i = 1; i < r.Badges.Count; i++) Assert.True(r.Badges[i].Y > r.Badges[i - 1].Y);
        }

        [Fact]
        public void TightNeighboursKeepSeparateOutlinesAndBadgesMoveToAFreeSpot()
        {
            // Two columns 6 px apart and text at the screen's left edge: the left badge has no room on the left.
            var l = new OcrLayout();
            l.AddLine(V15SegmentTests.Words("Left column first line of text", 2, 60, 16));
            l.AddLine(V15SegmentTests.Words("left column second line too", 2, 82, 16));
            l.AddLine(V15SegmentTests.Words("Right column first line of text", 260, 60, 16));
            l.AddLine(V15SegmentTests.Words("right column second line too", 260, 82, 16));
            var page = PageLayout.Analyze(l);
            var blocks = new List<Box> { new Box(2, 60, 248, 38), new Box(256, 60, 250, 38) };   // 6 px gutter
            var first = new List<Box> { new Box(2, 60, 240, 16), new Box(256, 60, 240, 16) };
            var text = page.Lines.SelectMany(x => x.Words).Select(w => w.Box).ToList();
            var r = BadgeLayout.Place(blocks, first, text, new Box(0, 0, 1024, 768), 5, 15, 3);
            AssertClean(r, text);
            Assert.True(r.Outlines[0].Right < r.Outlines[1].X, "the gutter stays visible between the outlines");
            Assert.True(r.Badges.All(b => b.X >= 0 && b.Y >= 0));
        }

        [Fact]
        public void BadgesOffChangesNothingButTheBadges()
        {
            var page = PageLayout.Analyze(V15SegmentTests.Page());
            var (blocks, first, text) = Parts(page, new Box(30, 30, 600, 300));
            var on = BadgeLayout.Place(blocks, first, text, new Box(0, 0, 1024, 768), 5, 15, 3, badges: true);
            var off = BadgeLayout.Place(blocks, first, text, new Box(0, 0, 1024, 768), 5, 15, 3, badges: false);
            Assert.Empty(off.Badges);
            Assert.Equal(on.Outlines.Count, off.Outlines.Count);
            for (int i = 0; i < on.Outlines.Count; i++) Assert.Equal(on.Outlines[i].X, off.Outlines[i].X);
            // Same reading order and segments whatever the setting: they come from the words, not the badges.
            var words = PageLayout.WordsOf(page.Snap(new Box(30, 30, 600, 300))).ToList();
            Assert.Equal(5, Segments.Build(words).Count);
        }

        [Fact]
        public void OneBlockHasNoBadge()
        {
            var r = BadgeLayout.Place(new List<Box> { new Box(100, 100, 200, 40) }, new List<Box> { new Box(100, 100, 200, 16) }, new List<Box>(), new Box(0, 0, 800, 600), 5, 15, 3);
            Assert.Empty(r.Badges);
            Assert.Single(r.Outlines);
        }
    }

    public class V15KeyTests
    {
        private static readonly int[] NavKeys = { 0x21, 0x22, 0x25, 0x27 };   // Page Up, Page Down, Left, Right

        [Fact]
        public void DefaultShortcutsNeverUseTheNavigationKeys()
        {
            var d = ShortcutSet.Defaults();
            foreach (var s in new[] { d.Hold, d.Tap, d.Read })
            {
                Assert.DoesNotContain(s.Key, NavKeys);
                Assert.DoesNotContain(s.ChordKey, NavKeys);
            }
        }

        [Fact]
        public void TheKeyboardHookLetsNavigationKeysThrough()
        {
            // The app's global hook (Space+V style chords): Page Up / Down and the arrows always pass through.
            var m = new ChordMachine();
            m.SetChords(new List<int[]> { new[] { KeyNames.Space, 0x56, 0 } });
            long t = 1000;
            foreach (var vk in NavKeys)
            {
                var down = m.OnKey(vk, true, t, false); t += 60;
                var up = m.OnKey(vk, false, t, false); t += 60;
                Assert.False(down.Swallow); Assert.False(up.Swallow);
                Assert.Empty(down.Inject); Assert.Equal(-1, down.Trigger);
            }
        }
    }
}
