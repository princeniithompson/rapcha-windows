using System;
using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>v14: the gesture state machine, full-line snapping, paragraph taps, de-duplication, WAV trimming.</summary>
    public class V14GestureTests
    {
        private const int W = BoxSelector.TapWindowMs;

        [Fact]
        public void QuickTapReadsAfterTheWindowWithoutALongPress()
        {
            var b = new BoxSelector();
            Assert.Equal(BoxSelector.Kind.None, b.OnDown(100, 100, 0).Kind);
            var up = b.OnUp(100, 100, 60);                       // a touchpad tap-to-click: 60 ms down
            Assert.Equal(BoxSelector.Kind.TapPending, up.Kind);  // the overlay starts preparing speech here
            Assert.Equal(BoxSelector.Kind.None, b.Tick(60 + W - 1).Kind);
            var tap = b.Tick(60 + W);
            Assert.Equal(BoxSelector.Kind.Tap, tap.Kind);
            Assert.Equal(100, tap.X); Assert.Equal(100, tap.Y);
            Assert.False(b.Busy);
        }

        [Fact]
        public void JitterDuringATapIsIgnored()
        {
            var b = new BoxSelector();
            b.OnDown(200, 200, 0);
            b.OnMove(203, 198, 20); b.OnMove(206, 204, 40);      // < 10 px while held
            Assert.False(b.HasLiveBox);
            Assert.Equal(BoxSelector.Kind.TapPending, b.OnUp(206, 204, 70).Kind);
            Assert.Equal(BoxSelector.Kind.None, b.OnMove(208, 206, 120).Kind);   // < 10 px from the tap after release
            Assert.Equal(BoxSelector.Kind.Tap, b.Tick(70 + W).Kind);
            // The tap point is where the button went down, not where jitter left it.
            var b2 = new BoxSelector();
            b2.OnDown(50, 50, 0); b2.OnMove(55, 53, 10);
            var up = b2.OnUp(55, 53, 30);
            Assert.Equal(50, up.X); Assert.Equal(50, up.Y);
        }

        [Fact]
        public void TapThenMoveEntersTwoCornerModeAndWaitsForTheSecondClick()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0);
            Assert.Equal(BoxSelector.Kind.TapPending, b.OnUp(100, 100, 50).Kind);
            var m = b.OnMove(130, 120, 150);                     // > 10 px within the window
            Assert.Equal(BoxSelector.Kind.CornerSet, m.Kind);
            Assert.True(b.WaitingForSecondCorner);
            Assert.True(b.HasLiveBox);
            // No button held: the box follows, and a long pause never reads.
            b.OnMove(300, 200, 400);
            Assert.Equal(200, b.LiveBox.W); Assert.Equal(100, b.LiveBox.H);
            Assert.Equal(BoxSelector.Kind.None, b.Tick(60000).Kind);
            Assert.True(b.WaitingForSecondCorner);
            // The second click locks corner 2.
            Assert.Equal(BoxSelector.Kind.None, b.OnDown(320, 260, 61000).Kind);
            Assert.True(b.HasLiveBox);                           // still shown while the button is down (no blink)
            var r = b.OnUp(320, 260, 61080);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(100, r.Box.X); Assert.Equal(100, r.Box.Y); Assert.Equal(220, r.Box.W); Assert.Equal(160, r.Box.H);
            Assert.False(b.Busy);
        }

        [Fact]
        public void MovingAfterTheWindowDoesNotStartCorners()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0);
            b.OnUp(100, 100, 40);
            var late = b.OnMove(200, 200, 40 + W + 50);          // the timer was late: the tap is due first
            Assert.Equal(BoxSelector.Kind.Tap, late.Kind);
            Assert.False(b.WaitingForSecondCorner);
        }

        [Fact]
        public void DragReadsAtTheRelease()
        {
            var b = new BoxSelector();
            b.OnDown(10, 10, 0);
            b.OnMove(15, 12, 10);
            Assert.False(b.HasLiveBox);
            b.OnMove(60, 200, 30);
            Assert.Equal(BoxSelector.State.Dragging, b.Current);
            var r = b.OnUp(62, 300, 400);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(10, r.Box.X); Assert.Equal(52, r.Box.W); Assert.Equal(290, r.Box.H);
        }

        [Fact]
        public void TouchpadTapAndDragIsADragNotATap()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0); b.OnUp(100, 100, 40);         // first tap
            b.OnDown(101, 100, 150);                             // second touch held (within the window, same place)
            b.OnMove(180, 160, 200);
            var r = b.OnUp(240, 220, 500);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(101, r.Box.X);
        }

        [Fact]
        public void DoubleClickInPlaceIsOneTap()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0); b.OnUp(100, 100, 40);
            b.OnDown(101, 101, 150);
            Assert.Equal(BoxSelector.Kind.TapPending, b.OnUp(101, 101, 190).Kind);
            Assert.Equal(BoxSelector.Kind.None, b.Tick(40 + W).Kind);        // the first tap's window was restarted
            Assert.Equal(BoxSelector.Kind.Tap, b.Tick(190 + W).Kind);
            Assert.Equal(BoxSelector.Kind.None, b.Tick(5000).Kind);          // and only once
        }

        [Fact]
        public void EscCancelsCornerOneAndAPendingTap()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0); b.OnUp(100, 100, 40); b.OnMove(150, 150, 100);
            Assert.True(b.WaitingForSecondCorner);
            Assert.Equal(BoxSelector.Kind.Cancelled, b.Cancel().Kind);
            Assert.False(b.HasLiveBox);
            Assert.Equal(BoxSelector.Kind.None, b.OnUp(300, 300, 200).Kind); // a stray release reads nothing

            b.OnDown(10, 10, 1000); b.OnUp(10, 10, 1040);
            Assert.Equal(BoxSelector.Kind.Cancelled, b.Cancel().Kind);
            Assert.Equal(BoxSelector.Kind.None, b.Tick(1040 + W).Kind);      // the cancelled tap never reads
            Assert.Equal(BoxSelector.Kind.None, b.Cancel().Kind);            // nothing left: Esc would close the overlay
        }

        [Fact]
        public void SecondClickBackOnCornerOneCancels()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0); b.OnUp(100, 100, 40); b.OnMove(150, 150, 100);
            b.OnMove(102, 101, 900);
            b.OnDown(102, 101, 1000);
            Assert.Equal(BoxSelector.Kind.Cancelled, b.OnUp(102, 101, 1050).Kind);
        }

        [Fact]
        public void FastMoveAndClickWithoutMoveEventsIsCornerTwo()
        {
            var b = new BoxSelector();
            b.OnDown(100, 100, 0); b.OnUp(100, 100, 40);
            Assert.Equal(BoxSelector.Kind.CornerSet, b.OnDown(400, 300, 200).Kind);
            var r = b.OnUp(400, 300, 240);
            Assert.Equal(BoxSelector.Kind.Box, r.Kind);
            Assert.Equal(300, r.Box.W);
        }

        [Fact]
        public void ThresholdScalesWithDpi()
        {
            var b = new BoxSelector(15);                         // 144 dpi
            b.OnDown(0, 0, 0); b.OnUp(0, 0, 30);
            Assert.Equal(BoxSelector.Kind.None, b.OnMove(12, 0, 80).Kind);
            Assert.Equal(BoxSelector.Kind.CornerSet, b.OnMove(16, 0, 100).Kind);
        }
    }

    public class V14SnapTests
    {
        private static List<OcrWord> Words(string text, double x, double y, double h, double charW = 8, double space = 5)
        {
            var list = new List<OcrWord>();
            foreach (var t in text.Split(' '))
            {
                list.Add(new OcrWord(t, new Box(x, y, t.Length * charW, h)));
                x += t.Length * charW + space;
            }
            return list;
        }

        private static readonly string[] Para =
        {
            "Select to Speak reads the text you choose aloud.",
            "Drag a thin strip down the left edge of this",
            "paragraph and every line must be read whole,",
            "from its first word to its last word.",
        };

        private static OcrLayout Page()
        {
            var l = new OcrLayout();
            l.AddLine(Words("A title above the paragraph", 40, 60, 24, 11, 6));
            for (int i = 0; i < Para.Length; i++) l.AddLine(Words(Para[i], 40, 120 + i * 22, 16));
            l.AddLine(Words("Next paragraph after a gap.", 40, 240, 16));
            // A second column far to the right, level with the paragraph.
            l.AddLine(Words("Sidebar text that must stay out", 700, 120, 16));
            l.AddLine(Words("of a strip in the left column.", 700, 142, 16));
            return l;
        }

        private static string Read(PageLayout page, Box sel) => string.Join(" ", PageLayout.WordsOf(page.Snap(sel)).Select(w => w.Text));

        [Fact]
        public void ThinLeftStripReadsEveryLineWhole()
        {
            var page = PageLayout.Analyze(Page());
            // 12 px wide strip over the first letters of the four paragraph lines only.
            var parts = page.Snap(new Box(34, 116, 12, 4 * 22));
            var text = string.Join(" ", PageLayout.WordsOf(parts).Select(w => w.Text));
            Assert.Equal(string.Join(" ", Para), text);
            Assert.DoesNotContain("title", text);
            Assert.DoesNotContain("Sidebar", text);
            Assert.DoesNotContain("Next", text);
            // No fragments and no duplicates: exactly the OCR words of those lines, each once.
            var words = PageLayout.WordsOf(parts);
            Assert.Equal(words.Count, words.Distinct().Count());
            Assert.Equal(Para.Sum(p => p.Split(' ').Length), words.Count);
        }

        [Fact]
        public void ZeroWidthVerticalDragStillReadsWholeLines()
        {
            var page = PageLayout.Analyze(Page());
            Assert.Equal(string.Join(" ", Para.Take(2)), Read(page, new Box(44, 120, 0, 30)));
        }

        [Fact]
        public void StripInTheMiddleOfLinesReadsThemWhole()
        {
            var page = PageLayout.Analyze(Page());
            Assert.Equal(Para[1] + " " + Para[2], Read(page, new Box(200, 146, 30, 36)));
        }

        [Fact]
        public void FlatBoxAlongOneLineReadsThatLine()
        {
            var page = PageLayout.Analyze(Page());
            Assert.Equal(Para[2], Read(page, new Box(100, 170, 120, 0)));
        }

        [Fact]
        public void StripInTheGutterReadsNothing()
        {
            var page = PageLayout.Analyze(Page());
            Assert.Empty(page.Snap(new Box(520, 110, 20, 120)));
        }

        [Fact]
        public void TapReadsTheWholeParagraphFromItsStart()
        {
            var page = PageLayout.Analyze(Page());
            // A tap on "whole," in the third line.
            var w = page.Lines.SelectMany(l => l.Words).First(x => x.Text == "whole,");
            var part = page.ParagraphAt(w.Box.CenterX, w.Box.CenterY, 24);
            Assert.NotNull(part);
            Assert.Equal(string.Join(" ", Para), string.Join(" ", part.Words.Select(x => x.Text)));
            // A tap on the title reads only the title; a tap on the sidebar only the sidebar.
            Assert.Equal("A title above the paragraph", string.Join(" ", page.ParagraphAt(60, 70, 24).Words.Select(x => x.Text)));
            Assert.StartsWith("Sidebar", string.Join(" ", page.ParagraphAt(760, 150, 24).Words.Select(x => x.Text)));
            Assert.Null(page.ParagraphAt(1500, 900, 24));
        }

        [Fact]
        public void ParagraphsSplitOnLargerSpacingAndIndentStaysIn()
        {
            var l = new OcrLayout();
            l.AddLine(Words("1. First item text that wraps onto", 56, 100, 16));
            l.AddLine(Words("a second indented line.", 74, 120, 16));
            l.AddLine(Words("2. Second item after a gap.", 56, 156, 16));
            var page = PageLayout.Analyze(l);
            Assert.Equal("1. First item text that wraps onto a second indented line.", string.Join(" ", page.ParagraphAt(120, 126, 24).Words.Select(x => x.Text)));
            Assert.Equal("2. Second item after a gap.", string.Join(" ", page.ParagraphAt(120, 160, 24).Words.Select(x => x.Text)));
        }

        [Fact]
        public void TapParagraphStaysInItsColumnWhenColumnsCannotBeCut()
        {
            // The CI screen: Notepad over a console. Console fragments run down the left edge and the right side,
            // level with Notepad's lines, and one stray line bridges the gutter, so the XY-cut can't split columns.
            var l = new OcrLayout();
            for (int i = 0; i < 12; i++) l.AddLine(Words("frag" + i, 34, 80 + i * 19, 13));
            for (int i = 0; i < 12; i++) l.AddLine(Words("console output line " + i, 844, 80 + i * 19, 13));
            l.AddLine(Words("File Edit Format View Help", 93, 113, 12));
            l.AddLine(Words("The quick brown fox jumps over the lazy dog.", 120, 140, 16));
            l.AddLine(Words("This second line must not be read.", 120, 160, 16));
            l.AddLine(Words(", , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , , ,", 243, 400, 6, 3, 10));
            var page = PageLayout.Analyze(l);
            var part = page.ParagraphAt(200, 148, 24);
            Assert.Equal("The quick brown fox jumps over the lazy dog. This second line must not be read.", string.Join(" ", part.Words.Select(w => w.Text)));
        }

        [Fact]
        public void OverlappingDuplicatesFromPassesAndSlicesAreReadOnce()
        {
            // The same line seen twice (two passes / overlapping slices), one copy shifted 2 px and one word misread.
            var l = new OcrLayout();
            l.AddLine(Words("The quick brown fox jumps", 40, 100, 16));
            var copy = Words("The quick brovvn fox", 42, 101, 16);
            l.AddLine(copy);
            var page = PageLayout.Analyze(l);
            var text = Read(page, new Box(30, 95, 300, 25));
            Assert.Equal("The quick brown fox jumps", text);
        }

        [Fact]
        public void SliceBoundaryRepeatIsDroppedButRealRepeatsStay()
        {
            var words = new List<OcrWord>
            {
                new OcrWord("read", new Box(0, 0, 32, 16)),
                new OcrWord("the", new Box(37, 0, 24, 16)),
                new OcrWord("the", new Box(60, 0, 24, 16)),     // touches the first "the": a slice seam artifact
                new OcrWord("that", new Box(90, 0, 32, 16)),
                new OcrWord("that", new Box(127, 0, 32, 16)),   // a normal space apart: a real "that that"
            };
            Assert.Equal("read the that that", string.Join(" ", PageLayout.Dedupe(words).Select(w => w.Text)));
        }

        [Fact]
        public void NoTokenRepeatsInAMultiPassMerge()
        {
            // Three passes of the same line; one splits a word, one is offset (a tile); the merge + page say each once.
            var p1 = new OcrPass { Name = "full-colour", Priority = 0, Area = new Box(0, 0, 1000, 500) };
            p1.Lines.Add(Words("Read every word only once please", 40, 100, 16));
            var p2 = new OcrPass { Name = "full-norm", Priority = 1, Area = new Box(0, 0, 1000, 500) };
            p2.Lines.Add(Words("Read every word only once please", 41, 100, 16));
            var p3 = new OcrPass { Name = "full-norm-tiles", Priority = 2, Area = new Box(0, 0, 1000, 500) };
            p3.Lines.Add(Words("word only once please", 40 + 4 * 8 + 5 + 5 * 8 + 5 - 1, 101, 16));
            var page = PageLayout.Analyze(OcrMerge.Merge(new List<OcrPass> { p1, p2, p3 }));
            var words = PageLayout.WordsOf(page.Snap(new Box(0, 90, 600, 40))).Select(w => w.Text).ToList();
            Assert.Equal("Read every word only once please", string.Join(" ", words));
        }
    }

    public class V14WavTests
    {
        private static byte[] Wav(short[] samples, int rate = 16000)
        {
            var data = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            var o = new List<byte>();
            o.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF")); o.AddRange(BitConverter.GetBytes(36 + data.Length));
            o.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); o.AddRange(BitConverter.GetBytes(16));
            o.AddRange(BitConverter.GetBytes((short)1)); o.AddRange(BitConverter.GetBytes((short)1));
            o.AddRange(BitConverter.GetBytes(rate)); o.AddRange(BitConverter.GetBytes(rate * 2));
            o.AddRange(BitConverter.GetBytes((short)2)); o.AddRange(BitConverter.GetBytes((short)16));
            o.AddRange(System.Text.Encoding.ASCII.GetBytes("data")); o.AddRange(BitConverter.GetBytes(data.Length));
            o.AddRange(data);
            return o.ToArray();
        }

        [Fact]
        public void LeadingAndTrailingSilenceIsTrimmedToTheKeptMargins()
        {
            // 200 ms silence, 100 ms tone, 300 ms silence at 16 kHz.
            var s = new short[16000 * 600 / 1000];
            for (int i = 3200; i < 4800; i++) s[i] = (short)(i % 2 == 0 ? 8000 : -8000);
            var trimmed = WavTrim.Trim(Wav(s), true, 15, 40, out double lead);
            Assert.InRange(lead, 184, 186);                            // 200 - 15 ms
            int dataLen = BitConverter.ToInt32(trimmed, 40);
            Assert.Equal(trimmed.Length - 44, dataLen);
            Assert.InRange(dataLen / 2, (15 + 100 + 40) * 16 - 2, (15 + 100 + 40) * 16 + 2);
        }

        [Fact]
        public void SilenceOnlyOrNonPcmIsLeftAlone()
        {
            var quiet = Wav(new short[1600]);
            Assert.Same(quiet, WavTrim.Trim(quiet, true, 15, 40, out _));
            var junk = new byte[] { 1, 2, 3 };
            Assert.Same(junk, WavTrim.Trim(junk, true, 15, 40, out _));
        }
    }
}
