using System;

namespace Rapcha.Core
{
    /// <summary>
    /// Estimates which word is being spoken inside a synthesized segment from how much of its audio has played.
    /// Each word gets a share of the time proportional to its letters (plus a small fixed part for the gap),
    /// with extra weight after punctuation for the pause the voice makes there.
    /// </summary>
    public static class PhraseTiming
    {
        public static double Weight(string word)
        {
            if (string.IsNullOrEmpty(word)) return 1;
            int letters = 0;
            foreach (char c in word) if (char.IsLetterOrDigit(c)) letters++;
            double w = 1.5 + Math.Max(1, letters);
            char last = word[word.Length - 1];
            if (last == ',' || last == ';' || last == ':') w += 2;
            else if (last == '.' || last == '!' || last == '?') w += 3;
            return w;
        }

        /// <summary>Word index in [firstWord, endWord) for a playback fraction in [0, 1].</summary>
        public static int WordAt(ReadingPlan plan, int firstWord, int endWord, double fraction)
        {
            if (plan == null || plan.IsEmpty) return 0;
            firstWord = Math.Max(0, Math.Min(firstWord, plan.Words.Count - 1));
            endWord = Math.Max(firstWord + 1, Math.Min(endWord, plan.Words.Count));
            if (double.IsNaN(fraction) || fraction <= 0) return firstWord;
            if (fraction >= 1) return endWord - 1;
            double total = 0;
            for (int i = firstWord; i < endWord; i++) total += Weight(plan.WordText(i));
            double target = fraction * total, acc = 0;
            for (int i = firstWord; i < endWord; i++)
            {
                acc += Weight(plan.WordText(i));
                if (target < acc) return i;
            }
            return endWord - 1;
        }
    }
}
