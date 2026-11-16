using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rapcha.Core
{
    /// <summary>
    /// Silence bars. Android's values were measured on phone mics. Laptop mics (with Windows
    /// noise suppression) are much quieter, so Windows uses lower bars; see <see cref="Windows"/>.
    /// </summary>
    public sealed class SilenceLevels
    {
        public double NearSilentMean, NearSilentMax, QuietMean, QuietMax;

        public SilenceLevels(double nearSilentMean, double nearSilentMax, double quietMean, double quietMax)
        {
            NearSilentMean = nearSilentMean; NearSilentMax = nearSilentMax; QuietMean = quietMean; QuietMax = quietMax;
        }

        /// <summary>The Android values (TranscriptAcceptance.kt v25).</summary>
        public static readonly SilenceLevels Android = new SilenceLevels(160, 320, 400, 800);

        /// <summary>
        /// Windows: only near-digital silence counts as silent. A quiet laptop array mic can have
        /// speech peaks per 100 ms well under Android's 320 bar, which made v1 throw real speech away.
        /// </summary>
        public static readonly SilenceLevels Windows = new SilenceLevels(25, 60, 150, 300);
    }

    /// <summary>Port of rapcha-lite websocket/TranscriptAcceptance.kt.</summary>
    public static class TranscriptAcceptance
    {
        public static bool IsNearSilent(double rmsMean, double rmsMax, int samples, SilenceLevels l)
        {
            if (samples <= 0) return false;
            return rmsMean < l.NearSilentMean && rmsMax < l.NearSilentMax;
        }

        public static string Accept(string text, double rmsMean, double rmsMax, IList<string> vocabulary, int samples, SilenceLevels l)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return "";
            if (IsNearSilent(rmsMean, rmsMax, samples, l)) return "";
            if (rmsMean < l.QuietMean && rmsMax < l.QuietMax && IsOnlyVocabulary(trimmed, vocabulary)) return "";
            return trimmed;
        }

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
