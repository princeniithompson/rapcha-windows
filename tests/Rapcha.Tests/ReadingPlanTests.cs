using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class ReadingPlanTests
    {
        private static string[] Phrases(ReadingPlan p) => Enumerable.Range(0, p.Phrases.Count).Select(p.PhraseText).ToArray();

        [Fact]
        public void SplitsAtCommasAndSentences()
        {
            var p = ReadingPlan.Create("Put the food in the container, open the container. Then close it.");
            Assert.Equal(new[] { "Put the food in the container,", "open the container.", "Then close it." }, Phrases(p));
        }

        [Fact]
        public void ShortCommaRunsStayTogether()
        {
            var p = ReadingPlan.Create("Yes, I know, but we should go now, before it rains.");
            Assert.Equal(new[] { "Yes, I know, but we should go now,", "before it rains." }, Phrases(p));
        }

        [Fact]
        public void LongRunsWithoutPunctuationAreCutAtTenWords()
        {
            var text = string.Join(" ", Enumerable.Range(1, 23).Select(i => "w" + i));
            var p = ReadingPlan.Create(text);
            Assert.All(p.Phrases, ph => Assert.InRange(ph.WordCount, 1, 10));
            Assert.Equal(23, p.Phrases.Sum(ph => ph.WordCount));
            Assert.Equal(new[] { 10, 10, 3 }, p.Phrases.Select(ph => ph.WordCount).ToArray());
        }

        [Fact]
        public void LoneTrailingWordJoinsPreviousPhrase()
        {
            var p = ReadingPlan.Create("one two three four five six seven eight nine ten eleven");
            Assert.Single(p.Phrases);
            Assert.Equal(11, p.Phrases[0].WordCount);
        }

        [Fact]
        public void PhrasesCoverEveryWordInOrder()
        {
            var p = ReadingPlan.Create("Hello.\r\n  This is   a test; with   odd spacing, and new\nlines!  ");
            int next = 0;
            foreach (var ph in p.Phrases) { Assert.Equal(next, ph.FirstWord); next = ph.EndWord; }
            Assert.Equal(p.Words.Count, next);
            Assert.Equal("lines!", p.WordText(p.Words.Count - 1));
        }

        [Fact]
        public void CharToWordAndWordToPhrase()
        {
            var p = ReadingPlan.Create("Put the food in the container, open the container.");
            int idx = p.Text.IndexOf("open");
            Assert.Equal(6, p.WordAtChar(idx));
            Assert.Equal(6, p.WordAtChar(idx + 2));
            Assert.Equal(5, p.WordAtChar(idx - 1));     // the space belongs to the word before
            Assert.Equal(1, p.PhraseOfWord(6));
            Assert.Equal(0, p.PhraseOfWord(5));
            Assert.Equal(0, p.WordAtChar(-5));
        }

        [Fact]
        public void SkipTenWordsIsClampedAndRepeatable()
        {
            var text = string.Join(" ", Enumerable.Range(0, 35).Select(i => "w" + i));
            var p = ReadingPlan.Create(text);
            int w = 0;
            w = p.Skip(w, ReadingPlan.SkipWords); Assert.Equal(10, w);
            w = p.Skip(w, ReadingPlan.SkipWords); Assert.Equal(20, w);
            w = p.Skip(w, ReadingPlan.SkipWords); Assert.Equal(30, w);
            w = p.Skip(w, ReadingPlan.SkipWords); Assert.Equal(34, w);   // stops at the last word
            w = p.Skip(w, -ReadingPlan.SkipWords); Assert.Equal(24, w);
            Assert.Equal(0, p.Skip(3, -ReadingPlan.SkipWords));
            Assert.StartsWith("w24 w25", p.TextFrom(24));
        }

        [Fact]
        public void EmptyAndWhitespaceTextIsSafe()
        {
            var p = ReadingPlan.Create("   \n ");
            Assert.True(p.IsEmpty);
            Assert.Empty(p.Phrases);
            Assert.Equal(0, p.Skip(0, 10));
            Assert.Equal("", p.TextFrom(3));
        }
    }
}
