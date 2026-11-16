using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Rapcha.Core
{
    /// <summary>
    /// Select-to-speak text model: words with their character positions in the original text,
    /// grouped into short phrases (about 4 to 10 words) split at punctuation, so the reading card
    /// can highlight "put the food in the container," then "open the container."
    /// </summary>
    public sealed class ReadingPlan
    {
        public struct Word
        {
            public int Start, Length;
            public Word(int start, int length) { Start = start; Length = length; }
            public int End => Start + Length;
        }

        public struct Phrase
        {
            public int FirstWord, WordCount;
            public Phrase(int first, int count) { FirstWord = first; WordCount = count; }
            public int EndWord => FirstWord + WordCount; // exclusive
        }

        public const int SkipWords = 10;
        public const int MaxChars = 20000;

        private static readonly Regex WordRegex = new Regex(@"\S+", RegexOptions.Compiled);
        private static readonly Regex SentenceEnd = new Regex(@"[.!?;:…]+[""'’”)\]]*$", RegexOptions.Compiled);
        private static readonly Regex SoftEnd = new Regex(@"[,，、—–\-]+[""'’”)\]]*$", RegexOptions.Compiled);

        public string Text { get; }
        public List<Word> Words { get; } = new List<Word>();
        public List<Phrase> Phrases { get; } = new List<Phrase>();

        private ReadingPlan(string text) { Text = text; }

        public static ReadingPlan Create(string text, int minWords = 4, int maxWords = 10)
        {
            text = text ?? "";
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            var plan = new ReadingPlan(text);
            foreach (Match m in WordRegex.Matches(text)) plan.Words.Add(new Word(m.Index, m.Length));

            int first = 0;
            for (int i = 0; i < plan.Words.Count; i++)
            {
                int n = i - first + 1;
                string w = text.Substring(plan.Words[i].Start, plan.Words[i].Length);
                bool sentence = SentenceEnd.IsMatch(w);
                bool soft = SoftEnd.IsMatch(w);
                if (sentence || (soft && n >= minWords) || n >= maxWords)
                {
                    plan.Phrases.Add(new Phrase(first, n));
                    first = i + 1;
                }
            }
            if (first < plan.Words.Count) plan.Phrases.Add(new Phrase(first, plan.Words.Count - first));

            // A lone trailing word joins the phrase before it, unless that phrase ended a sentence.
            int last = plan.Phrases.Count - 1;
            if (last >= 1 && plan.Phrases[last].WordCount == 1)
            {
                var prev = plan.Phrases[last - 1];
                var prevWord = plan.Words[prev.EndWord - 1];
                if (!SentenceEnd.IsMatch(text.Substring(prevWord.Start, prevWord.Length)))
                {
                    plan.Phrases[last - 1] = new Phrase(prev.FirstWord, prev.WordCount + 1);
                    plan.Phrases.RemoveAt(last);
                }
            }
            return plan;
        }

        public bool IsEmpty => Words.Count == 0;

        /// <summary>The word that contains (or comes just before) a character position.</summary>
        public int WordAtChar(int charIndex)
        {
            if (Words.Count == 0) return 0;
            int lo = 0, hi = Words.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Words[mid].Start <= charIndex) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }

        public int PhraseOfWord(int word)
        {
            if (Phrases.Count == 0) return 0;
            word = Clamp(word, 0, Words.Count - 1);
            int lo = 0, hi = Phrases.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Phrases[mid].FirstWord <= word) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }

        /// <summary>Back or forward by delta words, kept inside the text.</summary>
        public int Skip(int currentWord, int delta) => Clamp(currentWord + delta, 0, Math.Max(0, Words.Count - 1));

        /// <summary>Exclusive end word of the stretch read in one go from a word: the rest of its phrase.</summary>
        public int SegmentEnd(int word) => Phrases.Count == 0 ? 0 : Phrases[PhraseOfWord(word)].EndWord;

        /// <summary>Text of words [first, end).</summary>
        public string TextRange(int first, int end)
        {
            if (Words.Count == 0 || end <= first) return "";
            first = Clamp(first, 0, Words.Count - 1);
            end = Clamp(end, first + 1, Words.Count);
            return Text.Substring(Words[first].Start, Words[end - 1].End - Words[first].Start);
        }

        public string WordText(int word) => Text.Substring(Words[word].Start, Words[word].Length);

        public string PhraseText(int phrase)
        {
            var p = Phrases[phrase];
            int start = Words[p.FirstWord].Start;
            int end = Words[p.EndWord - 1].End;
            return Text.Substring(start, end - start);
        }

        /// <summary>The text to hand to the voice when reading starts at a word.</summary>
        public string TextFrom(int word) => Words.Count == 0 ? "" : Text.Substring(Words[Clamp(word, 0, Words.Count - 1)].Start);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
