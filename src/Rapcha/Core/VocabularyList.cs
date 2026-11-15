using System;
using System.Collections.Generic;
using System.Linq;

namespace Rapcha.Core
{
    /// <summary>
    /// Custom Vocabulary rules from CustomVocabularyRepository.addTerm: trimmed, no blanks,
    /// no case-insensitive duplicates, newest first. Stored as one term per line.
    /// </summary>
    public static class VocabularyList
    {
        public static bool TryAdd(List<string> terms, string term)
        {
            var trimmed = (term ?? "").Trim();
            if (trimmed.Length == 0) return false;
            if (terms.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase))) return false;
            terms.Insert(0, trimmed);
            return true;
        }

        public static List<string> FromText(string text)
        {
            var list = new List<string>();
            foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (list.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(t);
            }
            return list;
        }

        public static string ToText(IEnumerable<string> terms) =>
            string.Join("\r\n", terms.Select(t => t.Trim()).Where(t => t.Length > 0)) + "\r\n";
    }
}
