using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class V13Tests
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

        private static string Read(PageLayout page, Box sel) => string.Join(" ", PageLayout.WordsOf(page.Snap(sel)).Select(w => w.Text));

        /// <summary>The video's screen: an all-caps title line, a smaller monospaced code line, then a list.</summary>
        private static OcrLayout Video()
        {
            var l = new OcrLayout();
            l.AddLine(Words("CRITICAL CRASH REPORT - DIAGNOSE AND FIX IN ONE SINGLE TURN (DO NOT ASK FOR APPROVAL FIRST, AND DO NOT", 40, 100, 18, 10, 6));
            l.AddLine(Words("RUN :app:testDebugUnitTest OR :app:packageDebug SO THIS TURN DOES NOT GET CANCELED).", 40, 126, 14, 8.4, 8.4));
            l.AddLine(Words("Critical crash report", 40, 200, 22, 11, 6));
            l.AddLine(Words("1. Just installed the updated app on my Samsung phone", 60, 250, 16));
            l.AddLine(Words("and it crashes on start.", 80, 272, 16));
            l.AddLine(Words("1. Before opening the app, clear its cache.", 60, 310, 16));
            return l;
        }

        [Fact]
        public void TwoLineBoxReadsBothLinesInOrder()
        {
            var page = PageLayout.Analyze(Video());
            // The video box: the top edge cuts through the title line's top (60% of it inside), bottom past line 2.
            var text = Read(page, new Box(30, 107, 1100, 40));
            Assert.StartsWith("CRITICAL CRASH REPORT", text);
            Assert.EndsWith("GET CANCELED).", text);
            Assert.True(text.IndexOf("CRITICAL") < text.IndexOf("RUN"));
        }

        [Fact]
        public void ListBoxNeverReadsTheTitleAndKeepsEveryItemInOrder()
        {
            var page = PageLayout.Analyze(Video());
            // Box from just under the title (its bottom edge is at 222; the box starts at 218) to below the list.
            var text = Read(page, new Box(30, 218, 700, 120));
            Assert.DoesNotContain("Critical", text);
            Assert.StartsWith("1. Just installed", text);
            Assert.Contains("and it crashes on start.", text);
            Assert.EndsWith("1. Before opening the app, clear its cache.", text);
            Assert.True(text.IndexOf("Just") < text.IndexOf("Before"));
        }

        [Fact]
        public void WordsOutsideTheBoxSidewaysAreNotRead()
        {
            var page = PageLayout.Analyze(Video());
            // Half of the list's first line: only its words inside (with their cut word completed).
            var text = Read(page, new Box(55, 245, 150, 25));
            Assert.Equal("1. Just installed the", text);
        }

        [Fact]
        public void NearestLineFindsAWordWithin24Px()
        {
            var page = PageLayout.Analyze(Video());
            Assert.NotNull(page.NearestLine(150, 133, 24));                 // on the code line
            Assert.Contains("RUN", page.NearestLine(150, 150, 24).Text);   // 10 px below it
            Assert.Null(page.NearestLine(1500, 600, 24));
        }

        [Fact]
        public void MergeKeepsTheUnionAndDedupesByOverlap()
        {
            var a = new OcrPass { Name = "colour" };
            a.Lines.Add(Words("CRITICAL CRASH REPORT", 40, 100, 18, 10, 6));
            // The colour pass missed the green code line; the norm pass found it and also the title.
            var b = new OcrPass { Name = "norm", Priority = 1 };
            b.Lines.Add(Words("CRITICAL CRASH REPORT", 41, 101, 18, 10, 6));
            b.Lines.Add(Words("RUN :app:testDebugUnitTest OR", 40, 126, 14, 8.4, 8.4));
            var c = new OcrPass { Name = "bin", Priority = 2 };
            c.Lines.Add(Words("CRITICAI CRASH REP0RT", 40, 100, 18, 10, 6)); // a worse reading at the same place
            c.Lines.Add(new List<OcrWord> { new OcrWord("'", new Box(500, 500, 3, 5)) });  // threshold noise
            var merged = OcrMerge.Merge(new[] { a, b, c });
            var lines = merged.Lines.Select(l => string.Join(" ", l.Words.Select(w => w.Text))).ToList();
            Assert.Equal(new[] { "CRITICAL CRASH REPORT", "RUN :app:testDebugUnitTest OR" }, lines);
        }

        [Fact]
        public void SplitCodeTokensAreJoinedButRealSpacesStay()
        {
            // OCR split ":app:testDebugUnitTest" into ":app:" and "testDebugUnitTest" with a 1 px gap.
            var row = new List<OcrWord>
            {
                new OcrWord("RUN", new Box(40, 126, 25, 14)),
                new OcrWord(":app:", new Box(73, 126, 42, 14)),      // 8 px (a monospaced space) after RUN
                new OcrWord("testDebugUnitTest", new Box(116, 126, 143, 14)),
                new OcrWord("OR", new Box(267, 126, 17, 14)),
            };
            var joined = OcrMerge.JoinCodeTokens(row).Select(w => w.Text).ToArray();
            Assert.Equal(new[] { "RUN", ":app:testDebugUnitTest", "OR" }, joined);
        }

        [Fact]
        public void SentenceEndsAndListNumbersAreNotJoined()
        {
            var row = new List<OcrWord>
            {
                new OcrWord("1.", new Box(56, 166, 9, 10)), new OcrWord("Just", new Box(68, 166, 24, 10)),     // 3 px gap
                new OcrWord("start.", new Box(93, 186, 28, 10)), new OcrWord("Nothing", new Box(124, 186, 45, 13)),
                new OcrWord("com.", new Box(200, 186, 24, 10)), new OcrWord("example", new Box(224, 186, 40, 10)), // touching
            };
            Assert.Equal(new[] { "1.", "Just", "start.", "Nothing", "com.example" }, OcrMerge.JoinCodeTokens(row).Select(w => w.Text).ToArray());
        }

        [Fact]
        public void ALongerReadingInTheSameBoxWinsOverAShorterOne()
        {
            OcrPass P(string name, string t, double w) { var p = new OcrPass { Name = name }; p.Lines.Add(new List<OcrWord> { new OcrWord(t, new Box(70, 304, w, 6)) }); return p; }
            var merged = OcrMerge.Merge(new[] { P("a", "an", 14), P("b", "an", 14), P("c", "an", 14), P("d", "can", 15), P("e", "can", 15) });
            Assert.Equal("can", merged.Lines.Single().Words.Single().Text);
        }

        [Fact]
        public void JoinedReadingLosesWhenMorePassesSawTheWordsApart()
        {
            OcrPass Joined(string n) { var p = new OcrPass { Name = n }; p.Lines.Add(new List<OcrWord> { new OcrWord("supportpageor", new Box(147, 556, 92, 12)) }); return p; }
            OcrPass Apart(string n) { var p = new OcrPass { Name = n }; p.Lines.Add(Words("support page or", 147, 556, 12, 6.2, 4)); return p; }
            var merged = OcrMerge.Merge(new[] { Joined("a"), Apart("b"), Apart("c"), Apart("d") });
            Assert.Equal("support page or", string.Join(" ", merged.Lines.Single().Words.Select(w => w.Text)));
        }

        [Fact]
        public void ADashStaysInItsLine()
        {
            var p = new OcrPass();
            p.Lines.Add(Words("CRITICAL CRASH REPORT", 40, 44, 10, 10, 6));
            p.Lines.Add(new List<OcrWord> { new OcrWord("-", new Box(183, 48, 5, 2)) });
            p.Lines.Add(Words("DIAGNOSE AND FIX", 192, 44, 10, 10, 6));
            var merged = OcrMerge.Merge(new[] { p });
            Assert.Single(merged.Lines);
        }

        [Theory]
        [InlineData(":app:testDebugUnitTest", "app test Debug Unit Test")]
        [InlineData(":app:packageDebug", "app package Debug")]
        [InlineData("my_variable_name", "my variable name")]
        [InlineData("getHTTPResponseCode", "get HTTP Response Code")]
        [InlineData("com.example.MainActivity:onCreate", "com example Main Activity on Create")]
        [InlineData("iPhone", "iPhone")]
        [InlineData("JavaScript", "JavaScript")]
        [InlineData("Note:", "Note:")]
        [InlineData("e.g.", "e.g.")]
        [InlineData("3.14", "3.14")]
        [InlineData("example.com", "example.com")]
        [InlineData("https://example.com/a_b", "https://example.com/a_b")]
        public void CodeTokensBecomeSayable(string token, string said)
        {
            Assert.Equal(said, Speakable.For(token));
        }

        [Fact]
        public void SpokenCodeMapsBackToTheOriginalWord()
        {
            var words = Words("RUN :app:testDebugUnitTest OR", 40, 126, 14);
            var s = new SpokenText(words);
            Assert.Equal("RUN app test Debug Unit Test OR", s.Text);
            var plan = ReadingPlan.Create(s.Text);
            Assert.Equal(7, plan.Words.Count);
            // Every spoken word of the token highlights the token's own box.
            for (int i = 1; i <= 5; i++) Assert.Equal(1, s.WordAtChar(plan.Words[i].Start));
            Assert.Equal(2, s.WordAtChar(plan.Words[6].Start));
            Assert.Equal(words[1].Box.X, s.HighlightFor(s.WordAtChar(plan.Words[3].Start)).X);
        }
    }
}
