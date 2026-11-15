using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rapcha.Core
{
    /// <summary>Port of rapcha-lite websocket/TranscriptAcceptance.kt.</summary>
    public static class TranscriptAcceptance
    {
        public const double NearSilentMean = 160.0;
        public const double NearSilentMax = 320.0;
        public const double QuietMean = 400.0;
        public const double QuietMax = 800.0;

        public static bool IsNearSilent(double rmsMean, double rmsMax, int samples = 1)
        {
            if (samples <= 0) return false;
            return rmsMean < NearSilentMean && rmsMax < NearSilentMax;
        }

        public static bool IsQuiet(double rmsMean, double rmsMax) => rmsMean < QuietMean && rmsMax < QuietMax;

        public static string Accept(string text, double rmsMean, double rmsMax, IList<string> vocabulary, int samples = 1)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return "";
            if (IsNearSilent(rmsMean, rmsMax, samples)) return "";
            if (IsQuiet(rmsMean, rmsMax) && IsOnlyVocabulary(trimmed, vocabulary)) return "";
            return trimmed;
        }

        public static bool IsOnlyVocabulary(string text, IList<string> vocabulary)
        {
            var tokens = Tokenize(text);
            if (tokens.Count == 0) return false;
            var allowed = new HashSet<string>((vocabulary ?? new List<string>()).SelectMany(Tokenize));
            if (allowed.Count == 0) return false;
            return tokens.All(allowed.Contains);
        }

        private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        private static List<string> Tokenize(string text)
        {
            return NonWord.Split((text ?? "").ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        }
    }
}
