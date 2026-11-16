using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Rapcha.Core
{
    /// <summary>How a Windows voice is shown in Settings: "Zira, female, English (US)".</summary>
    public static class VoiceLabels
    {
        /// <summary>"Microsoft Hazel Desktop - English (Great Britain)" → "Hazel"; "Microsoft George" → "George".</summary>
        public static string ShortName(string name)
        {
            var s = (name ?? "").Trim();
            int dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) s = s.Substring(0, dash).Trim();
            if (s.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(10).Trim();
            foreach (var suffix in new[] { " Desktop", " Mobile", " OneCore" })
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - suffix.Length).Trim();
            return s.Length > 0 ? s : (name ?? "").Trim();
        }

        private static readonly Dictionary<string, string> Short = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "United States", "US" }, { "United Kingdom", "UK" }, { "Great Britain", "UK" },
        };

        /// <summary>"en-US" → "English (US)", "en-GB" → "English (UK)", "en-IN" → "English (India)".</summary>
        public static string Language(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            string name;
            try { name = CultureInfo.GetCultureInfo(tag.Trim()).EnglishName; }
            catch { return tag.Trim(); }
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            if (open > 0 && close > open)
            {
                var region = name.Substring(open + 1, close - open - 1).Trim();
                if (Short.TryGetValue(region, out var s)) region = s;
                name = name.Substring(0, open).Trim() + " (" + region + ")";
            }
            return name;
        }

        public static string Gender(string g)
        {
            var s = (g ?? "").Trim().ToLowerInvariant();
            return s == "male" || s == "female" ? s : "";
        }

        /// <param name="desktop">An older SAPI "Desktop" voice (shown so the two kinds of the same voice can be told apart).</param>
        public static string Label(string name, string gender, string languageTag, bool desktop)
        {
            var parts = new List<string> { ShortName(name) + (desktop ? " (desktop)" : "") };
            var g = Gender(gender);
            if (g.Length > 0) parts.Add(g);
            var lang = Language(languageTag);
            if (lang.Length > 0) parts.Add(lang);
            return string.Join(", ", parts);
        }

        /// <summary>Sort key: the user's language first, then by language, then modern before desktop, then name.</summary>
        public static string SortKey(string languageTag, bool desktop, string name, string preferredLanguage)
        {
            var lang = (languageTag ?? "").ToLowerInvariant();
            var pref = (preferredLanguage ?? "").ToLowerInvariant();
            string first = lang == pref ? "0" : (pref.Length >= 2 && lang.StartsWith(pref.Substring(0, 2), StringComparison.Ordinal) ? "1" : "2");
            return first + "|" + lang + "|" + (desktop ? "1" : "0") + "|" + ShortName(name).ToLowerInvariant();
        }

        /// <summary>
        /// Word timing when the voice gives no word marks: the audio duration spread over the words by length
        /// (each word gets its letters plus a little for the gap). Returns the start time (ms) of each word.
        /// </summary>
        public static double[] EstimateStarts(IList<int> wordLengths, double durationMs, double leadMs = 120)
        {
            var starts = new double[wordLengths.Count];
            if (wordLengths.Count == 0) return starts;
            double total = wordLengths.Sum(l => Math.Max(1, l) + 1.5);
            double usable = Math.Max(0, durationMs - leadMs * 2);
            double t = Math.Min(leadMs, durationMs / 4);
            for (int i = 0; i < wordLengths.Count; i++)
            {
                starts[i] = t;
                t += usable * (Math.Max(1, wordLengths[i]) + 1.5) / total;
            }
            return starts;
        }
    }
}
