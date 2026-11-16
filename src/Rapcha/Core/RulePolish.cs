using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rapcha.Core
{
    /// <summary>
    /// Light offline clean-up of recognised text (no model): removes "um", "uh" and similar fillers, writes your
    /// Custom Vocabulary the way you spelled it (also when the recogniser split it, e.g. "rap cha" → "Rapcha", or
    /// was one letter off on a long word), "i" → "I", a capital at the start of every sentence, tidy spaces and
    /// commas, and a full stop at the end. Online, Gemini's smart polish is used instead.
    /// </summary>
    public static class RulePolish
    {
        private static readonly Regex Fillers = new Regex(
            @"(?<![\w'])(?:u+m+|u+h+m*|e+r+m+|e+r+|a+h+|h+m+|mm+)(?![\w'])\s*,?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LoneI = new Regex(@"(?<![\w'])i(?=(?:'(?:m|ll|ve|d|s))?(?![\w']))", RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(@"[ \t]{2,}", RegexOptions.CultureInvariant);
        private static readonly Regex SpaceBeforePunct = new Regex(@"\s+([,.!?;:])", RegexOptions.CultureInvariant);
        private static readonly Regex DoubleComma = new Regex(@",\s*,+", RegexOptions.CultureInvariant);
        private static readonly Regex CommaBeforeStop = new Regex(@",\s*([.!?])", RegexOptions.CultureInvariant);
        private static readonly Regex LeadingComma = new Regex(@"^[\s,;:]+", RegexOptions.CultureInvariant);
        private static readonly Regex NoSpaceAfterPunct = new Regex(@"([,.!?;:])(?=[A-Za-z])", RegexOptions.CultureInvariant);

        public static string Clean(string text, IList<string> vocabulary = null)
        {
            var s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "";
            s = Fillers.Replace(s, " ");
            s = ApplyVocabulary(s, vocabulary);
            s = LoneI.Replace(s, "I");
            s = Spaces.Replace(s, " ");
            s = SpaceBeforePunct.Replace(s, "$1");
            s = DoubleComma.Replace(s, ",");
            s = CommaBeforeStop.Replace(s, "$1");
            s = LeadingComma.Replace(s, "");
            s = NoSpaceAfterPunct.Replace(s, m => IsDecimalOrAbbrev(s, m.Index) ? m.Value : m.Value + " ");
            s = Spaces.Replace(s, " ").Trim().TrimEnd(',', ';', ':').Trim();
            if (s.Length == 0) return "";
            s = Capitalise(s);
            char last = s[s.Length - 1];
            if (char.IsLetterOrDigit(last) || last == '"' || last == '\'' || last == ')') s += ".";
            return s;
        }

        private static bool IsDecimalOrAbbrev(string s, int i)
        {
            // "3.5", "e.g", "U.S" stay together: a dot between two single letters or digits.
            if (s[i] != '.') return false;
            bool before = i > 0 && char.IsLetterOrDigit(s[i - 1]) && (i < 2 || !char.IsLetterOrDigit(s[i - 2]) || char.IsDigit(s[i - 1]));
            return before;
        }

        private static string Capitalise(string s)
        {
            var sb = new StringBuilder(s);
            bool start = true;
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (start && char.IsLetter(c)) { sb[i] = char.ToUpperInvariant(c); start = false; }
                else if (start && char.IsDigit(c)) start = false;
                if (c == '.' || c == '!' || c == '?')
                {
                    // a decimal point (3.5) or an abbreviation dot inside a word doesn't start a sentence
                    bool inside = i + 1 < sb.Length && char.IsLetterOrDigit(sb[i + 1]);
                    if (!inside) start = true;
                }
            }
            return sb.ToString();
        }

        private static readonly HashSet<string> Suffixes = new HashSet<string> { "s", "es", "d", "ed", "ing", "ly", "er", "ers" };

        /// <summary>Writes vocabulary terms exactly as the user saved them.</summary>
        public static string ApplyVocabulary(string s, IList<string> vocabulary)
        {
            if (vocabulary == null || vocabulary.Count == 0 || string.IsNullOrEmpty(s)) return s;
            foreach (var raw in vocabulary.OrderByDescending(v => (v ?? "").Length))
            {
                var term = (raw ?? "").Trim();
                if (term.Length < 2) continue;
                // 1) the same words in any case, or with the spaces/hyphens the recogniser put in or left out
                var parts = Regex.Split(term, @"[\s\-]+").Where(p => p.Length > 0).Select(Regex.Escape).ToArray();
                string loose = string.Join(@"[\s\-]*", parts);
                if (parts.Length == 1 && term.Length >= 4)
                {
                    // a single word split by the recogniser: "rap cha" → "Rapcha"
                    var letters = term.Where(char.IsLetterOrDigit).Select(c => Regex.Escape(c.ToString())).ToArray();
                    if (letters.Length == term.Length) loose = string.Join(@"[\s\-]?", letters);
                }
                s = Regex.Replace(s, @"(?<![\w])" + loose + @"(?![\w])", term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                // 2) one letter off on a long single word (two on a very long one): "Rapchar" → "Rapcha"
                if (parts.Length == 1 && term.Length >= 6 && term.All(char.IsLetter))
                {
                    int maxEdits = term.Length >= 10 ? 2 : 1;
                    s = Regex.Replace(s, @"(?<![\w'])[A-Za-z]{" + (term.Length - maxEdits) + "," + (term.Length + maxEdits) + @"}(?![\w'])", m =>
                    {
                        var w = m.Value;
                        if (string.Equals(w, term, StringComparison.Ordinal)) return w;
                        if (char.ToLowerInvariant(w[0]) != char.ToLowerInvariant(term[0])) return w; // same first letter
                        // a longer or shorter form of the same word ("Mondays", "Monda") is a different word
                        if (term.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return w;
                        if (w.StartsWith(term, StringComparison.OrdinalIgnoreCase) && Suffixes.Contains(w.Substring(term.Length).ToLowerInvariant())) return w;
                        return Edits(w.ToLowerInvariant(), term.ToLowerInvariant(), maxEdits) <= maxEdits ? term : w;
                    }, RegexOptions.CultureInvariant);
                }
            }
            return s;
        }

        /// <summary>Levenshtein distance, stopping early above max.</summary>
        public static int Edits(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    rowMin = Math.Min(rowMin, cur[j]);
                }
                if (rowMin > max) return max + 1;
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
