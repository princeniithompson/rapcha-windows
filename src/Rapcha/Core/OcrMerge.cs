using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>The words one OCR pass found (one preprocessed image), in screen coordinates.</summary>
    public sealed class OcrPass
    {
        public string Name = "";
        public int Priority;                 // tie-break: higher wins (0 = plain colour pass)
        public Box Area;                     // the part of the screen this pass looked at
        public int Ms;
        public readonly List<List<OcrWord>> Lines = new List<List<OcrWord>>();
        public int WordCount => Lines.Sum(l => l.Count);
    }

    /// <summary>
    /// Combines several OCR passes over the same screen into one set of lines.
    /// Every word of every pass is a candidate; a candidate's score is how many other passes found the same text
    /// at the same place (votes), then how much text it has, then the pass priority. Candidates are accepted
    /// best first, skipping any that overlap an accepted word by half of the smaller box, so each spot on the
    /// screen is read once but nothing a single pass found is lost (the union). Accepted words are regrouped
    /// into lines, and code tokens split by OCR (":app:" "testDebugUnitTest") are joined again.
    /// </summary>
    public static class OcrMerge
    {
        public const double SameSpotIoU = 0.4;
        public const double Conflict = 0.5;

        private sealed class Cand
        {
            public OcrWord Word;
            public int Pass, Priority, Votes, Alnum;
            public string Key;
            public double Score;
        }

        public static OcrLayout Merge(IEnumerable<OcrPass> passes)
        {
            var list = passes.Where(p => p != null).ToList();
            var cands = new List<Cand>();
            for (int p = 0; p < list.Count; p++)
                foreach (var line in list[p].Lines)
                    foreach (var w in line)
                    {
                        var t = OcrLayout.Clean(w.Text);
                        if (t.Length == 0 || w.Box.IsEmpty) continue;
                        cands.Add(new Cand { Word = new OcrWord(t, w.Box), Pass = p, Priority = list[p].Priority, Key = Key(t), Alnum = t.Count(char.IsLetterOrDigit) });
                    }

            // Spatial buckets by row so votes and conflicts stay cheap on a full screen.
            const double Cell = 24;
            var grid = new Dictionary<int, List<Cand>>();
            foreach (var c in cands)
                for (int r = (int)Math.Floor(c.Word.Box.Y / Cell); r <= (int)Math.Floor(c.Word.Box.Bottom / Cell); r++)
                {
                    if (!grid.TryGetValue(r, out var l)) grid[r] = l = new List<Cand>();
                    l.Add(c);
                }
            IEnumerable<Cand> Near(Box b)
            {
                var seen = new HashSet<Cand>();
                for (int r = (int)Math.Floor(b.Y / Cell); r <= (int)Math.Floor(b.Bottom / Cell); r++)
                    if (grid.TryGetValue(r, out var l))
                        foreach (var c in l) if (seen.Add(c)) yield return c;
            }

            foreach (var c in cands)
            {
                var passesAgreeing = new HashSet<int>();
                foreach (var o in Near(c.Word.Box))
                    if (o.Pass != c.Pass && IoU(o.Word.Box, c.Word.Box) >= SameSpotIoU &&
                        (o.Key == c.Key || (c.Key.Length > o.Key.Length && o.Key.Length >= 2 && c.Key.Contains(o.Key) && IoU(o.Word.Box, c.Word.Box) >= 0.6)))
                        passesAgreeing.Add(o.Pass); // "can" is supported by a pass that read "an" in the same box
                c.Votes = passesAgreeing.Count;
                c.Score = c.Votes * 100 + c.Alnum * 3 + c.Word.Text.Length + c.Priority;
            }

            var accepted = new List<Cand>();
            var acceptedGrid = new Dictionary<int, List<Cand>>();
            foreach (var c in cands.OrderByDescending(c => c.Score).ThenBy(c => c.Pass))
            {
                // Lone punctuation that no other pass saw is noise from thresholding.
                if (c.Alnum == 0 && c.Votes == 0 && c.Priority > 0) continue;
                // Two words read as one ("supportpageor"): if more passes saw separate words there than saw this
                // joined reading, leave the spot to the separate words.
                if (c.Alnum >= 6 && SplitBy(c, Near(c.Word.Box)) > c.Votes + 1) continue;
                bool clash = false;
                for (int r = (int)Math.Floor(c.Word.Box.Y / Cell); r <= (int)Math.Floor(c.Word.Box.Bottom / Cell) && !clash; r++)
                    if (acceptedGrid.TryGetValue(r, out var l))
                        foreach (var a in l)
                            if (OverlapOfSmaller(a.Word.Box, c.Word.Box) >= Conflict) { clash = true; break; }
                if (clash) continue;
                accepted.Add(c);
                for (int r = (int)Math.Floor(c.Word.Box.Y / Cell); r <= (int)Math.Floor(c.Word.Box.Bottom / Cell); r++)
                {
                    if (!acceptedGrid.TryGetValue(r, out var l)) acceptedGrid[r] = l = new List<Cand>();
                    l.Add(c);
                }
            }

            var layout = new OcrLayout();
            foreach (var row in Rows(accepted.Select(a => a.Word).ToList()))
                layout.AddLine(JoinCodeTokens(row));
            return layout;
        }

        /// <summary>How many other passes have two or more words that lie mostly inside this candidate's box.</summary>
        private static int SplitBy(Cand c, IEnumerable<Cand> near)
        {
            return near.Where(o => o.Pass != c.Pass && o.Alnum > 0 && o.Word.Box.W < 0.8 * c.Word.Box.W && OverlapOfSmaller(o.Word.Box, c.Word.Box) >= 0.8)
                       .GroupBy(o => o.Pass).Count(g => g.Count() >= 2);
        }

        /// <summary>Groups words into visual lines: same row when they share at least half of the smaller height.</summary>
        public static List<List<OcrWord>> Rows(List<OcrWord> words)
        {
            var rows = new List<(Box box, List<OcrWord> words)>();
            foreach (var w in words.OrderBy(w => w.Box.CenterY))
            {
                int best = -1; double bestOverlap = 0;
                for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 12; i--)
                {
                    var rb = rows[i].box;
                    double overlap = Math.Min(rb.Bottom, w.Box.Bottom) - Math.Max(rb.Y, w.Box.Y);
                    double minH = Math.Min(rb.H, w.Box.H);
                    // Punctuation ("-", ".") is short: it joins the row it sits in whatever its height.
                    bool punct = !w.Text.Any(char.IsLetterOrDigit) && w.Box.CenterY >= rb.Y && w.Box.CenterY <= rb.Bottom;
                    bool similar = punct || Math.Max(rb.H, w.Box.H) <= 2.2 * minH;
                    if (similar && overlap >= 0.5 * minH && overlap > bestOverlap) { best = i; bestOverlap = overlap; }
                }
                if (best < 0) rows.Add((w.Box, new List<OcrWord> { w }));
                else
                {
                    var r = rows[best];
                    r.words.Add(w);
                    // The row's band follows its median word, not the union, so one tall word can't swallow the next line.
                    var hs = r.words.Select(x => x.Box).OrderBy(b => b.H).ToList();
                    var mid = hs[hs.Count / 2];
                    rows[best] = (new Box(Math.Min(r.box.X, w.Box.X), mid.Y, Math.Max(r.box.Right, w.Box.Right) - Math.Min(r.box.X, w.Box.X), mid.H), r.words);
                }
            }
            return rows.Select(r => r.words.OrderBy(w => w.Box.X).ToList()).ToList();
        }

        private static readonly string JoinAfter = ":_/\\(";
        private static readonly string JoinBefore = ":_/\\)";

        /// <summary>
        /// Joins pieces of one token that OCR split: nearly touching words (gap under 0.18 of the line height), or a
        /// word ending in ':' '_' '.' '/' '-' followed closely by the rest (gap under 0.3). A real space between words
        /// is about half a line height (wider in monospaced code), so separate words stay separate.
        /// </summary>
        public static List<OcrWord> JoinCodeTokens(List<OcrWord> row)
        {
            if (row.Count < 2) return row;
            var hs = row.Select(w => w.Box.H).OrderBy(h => h).ToList();
            double h = hs[hs.Count / 2];
            var result = new List<OcrWord> { row[0] };
            for (int i = 1; i < row.Count; i++)
            {
                var prev = result[result.Count - 1];
                var cur = row[i];
                double gap = cur.Box.X - prev.Box.Right;
                char last = prev.Text[prev.Text.Length - 1], first = cur.Text[0];
                bool touching = gap <= 0.18 * h && (char.IsLetterOrDigit(last) || JoinAfter.IndexOf(last) >= 0) && (char.IsLetterOrDigit(first) || JoinBefore.IndexOf(first) >= 0)
                                && !(char.IsLetterOrDigit(last) && char.IsLetterOrDigit(first) && gap > 0.08 * h);
                // "com." + "example": a dot joins only when the pieces touch and the next starts in lower case.
                bool dotted = (last == '.' || last == '-') && gap <= 0.08 * h && char.IsLower(first) && prev.Text.Length >= 2 && char.IsLetter(prev.Text[prev.Text.Length - 2]);
                bool joiner = gap <= 0.3 * h && (JoinAfter.IndexOf(last) >= 0 && last != '(' && char.IsLetterOrDigit(first) || JoinBefore.IndexOf(first) >= 0 && first != ')' && char.IsLetterOrDigit(last) && gap <= 0.12 * h);
                if (gap >= -0.5 * h && (touching || joiner || dotted) && Math.Abs(cur.Box.CenterY - prev.Box.CenterY) <= 0.5 * h)
                    result[result.Count - 1] = new OcrWord(prev.Text + cur.Text, prev.Box.Union(cur.Box));
                else result.Add(cur);
            }
            return result;
        }

        private static string Key(string t)
        {
            var sb = new StringBuilder(t.Length);
            foreach (char c in t) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.Length > 0 ? sb.ToString() : t;
        }

        public static double IoU(Box a, Box b)
        {
            double i = a.Intersect(b).Area;
            double u = a.Area + b.Area - i;
            return u > 0 ? i / u : 0;
        }

        private static double OverlapOfSmaller(Box a, Box b)
        {
            double m = Math.Min(a.Area, b.Area);
            return m > 0 ? a.Intersect(b).Area / m : 0;
        }
    }
}
