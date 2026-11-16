using System.Collections.Generic;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>
    /// Makes code tokens sayable: ":app:testDebugUnitTest" becomes "app test Debug Unit Test".
    /// Only tokens that look like code are changed (a colon, underscore or slash inside the token, or a camelCase
    /// name with at least two humps, or dotted names together with one of those); ordinary words, "iPhone",
    /// "e.g.", "3.14", "example.com" and a trailing "Note:" stay as they are.
    /// </summary>
    public static class Speakable
    {
        public static bool LooksLikeCode(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 3) return false;
            string core = t.TrimEnd('.', ',', ';', '!', '?', ')', ']', '"', '\'').TrimStart('(', '[', '"', '\'');
            if (core.Length < 2) return false;
            bool sep = false;
            for (int i = 0; i < core.Length; i++)
            {
                char c = core[i];
                if (c == '_' && i > 0 && i < core.Length - 1) sep = true;
                if (c == ':' && i < core.Length - 1 && char.IsLetter(core[i + 1])) sep = true;
                if (c == '/' && i > 0 && i < core.Length - 1 && char.IsLetter(core[i - 1]) && char.IsLetter(core[i + 1])) sep = true;
            }
            if (core.StartsWith("http", System.StringComparison.OrdinalIgnoreCase) || core.Contains("://")) return false;
            return sep || Humps(core) >= 2;
        }

        private static int Humps(string t)
        {
            int n = 0;
            for (int i = 1; i < t.Length; i++) if (char.IsLower(t[i - 1]) && char.IsUpper(t[i])) n++;
            return n;
        }

        /// <summary>The words to say for one OCR word (the word itself unless it looks like code).</summary>
        public static string For(string t)
        {
            if (!LooksLikeCode(t)) return t;
            var parts = new List<string>();
            var cur = new StringBuilder();
            void Flush() { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } }
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == ':' || c == '_' || c == '.' || c == '/' || c == '\\' || c == '-' || c == '(' || c == ')' || c == '[' || c == ']' || c == '"' || c == '\'' || c == ',' || c == ';')
                {
                    Flush();
                    continue;
                }
                if (cur.Length > 0)
                {
                    char p = cur[cur.Length - 1];
                    bool hump = char.IsLower(p) && char.IsUpper(c);
                    bool acronymEnd = char.IsUpper(p) && char.IsUpper(c) && i + 1 < t.Length && char.IsLower(t[i + 1]) && cur.Length > 1; // "HTTPServer" -> HTTP Server

                    if (hump || acronymEnd) Flush();
                }
                cur.Append(c);
            }
            Flush();
            if (parts.Count == 0) return t;
            var s = string.Join(" ", parts);
            // Keep a sentence end so the voice still pauses.
            char last = t[t.Length - 1];
            if (last == '.' || last == '!' || last == '?' || last == ';' || last == ',') s += last;
            return s;
        }
    }
}
