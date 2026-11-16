using System;
using System.Collections.Generic;
using System.Linq;

namespace Rapcha.Core
{
    /// <summary>One visual line of text: an OCR line, split where a wide horizontal gap separates columns or cards.</summary>
    public sealed class TextLine
    {
        public int Id;
        public readonly List<OcrWord> Words = new List<OcrWord>();
        public Box Box;
        public string Text => string.Join(" ", Words.Select(w => w.Text));
        public bool Tiny => Words.Count == 1 && Words[0].Text.Length <= 2 && Box.W <= 1.5 * Box.H;
    }

    /// <summary>A paragraph, heading, list item or card text: lines that belong together.</summary>
    public sealed class TextBlock
    {
        public int Order;                       // reading order, 0-based
        public readonly List<TextLine> Lines = new List<TextLine>();
        public Box Box;
        public IEnumerable<OcrWord> Words => Lines.SelectMany(l => l.Words);
    }

    public enum SnapKind { Whole, Lines, Words }

    /// <summary>One block's share of a selection.</summary>
    public sealed class SnapPart
    {
        public TextBlock Block;
        public SnapKind Kind;
        public double AreaCoverage, LineCoverage;
        public readonly List<OcrWord> Words = new List<OcrWord>();
        public Box Box;
    }

    /// <summary>
    /// Reads the OCR result like a page of a book.
    ///
    /// 1. Lines: each OCR line is split where two neighbouring words are more than <see cref="SplitGap"/> line
    ///    heights apart (Windows OCR sometimes joins text across columns or cards that happen to be level).
    ///    Bullet glyphs are dropped; identical lines that overlap on screen are kept once.
    /// 2. Regions (recursive XY-cut over the line boxes): a region with a vertical strip of whitespace at least
    ///    <see cref="ColumnGap"/> line heights wide from top to bottom is split into columns, read left to right.
    ///    Otherwise it is cut at horizontal whitespace of at least <see cref="BlockGap"/> line heights into bands,
    ///    read top to bottom, but neighbouring bands that continue the same column gutter are kept together so
    ///    a cut can never interleave two columns.
    /// 3. Blocks inside a region: rows are joined into one block while the gap to the previous row is at most
    ///    <see cref="BlockGap"/> line heights, the text heights are within <see cref="HeightRatio"/> of each other
    ///    and the left edges (or centres) line up within <see cref="AlignSlop"/> line heights.
    /// Words inside a block: line by line, left to right.
    /// </summary>
    public sealed class PageLayout
    {
        public const double SplitGap = 1.5;
        public const double ColumnGap = 1.0;
        public const double BlockGap = 0.85;
        public const double HeightRatio = 1.35;
        public const double AlignSlop = 1.5;

        // Snap rules (see Snap).
        public const double WholeByArea = 0.5;
        public const double WholeByLines = 0.6;
        public const double DropBelow = 0.25;
        public const double LineCovered = 0.6;
        public const double WordInside = 0.5;
        public const double TightBox = 0.5;
        public const double FullyCovered = 0.9;
        public const double TightLines = 0.5;

        public readonly List<TextLine> Lines = new List<TextLine>();
        public readonly List<TextBlock> Blocks = new List<TextBlock>(); // in reading order

        public int WordCount => Lines.Sum(l => l.Words.Count);

        private static readonly HashSet<string> Bullets = new HashSet<string> { "•", "·", "▪", "◦", "○", "●", "■", "□", "►", "▶", "▸", "‣", "⁃", "∙", "|", "¦" };

        public static PageLayout Analyze(OcrLayout ocr)
        {
            var page = new PageLayout();
            var lines = new List<TextLine>();
            foreach (var l in ocr.Lines) lines.AddRange(Split(l));
            // Identical text at (nearly) the same place is one line read twice by OCR; repeats elsewhere stay.
            var kept = new List<TextLine>();
            foreach (var l in lines)
            {
                if (kept.Any(k => string.Equals(k.Text, l.Text, StringComparison.OrdinalIgnoreCase) && IoU(k.Box, l.Box) >= 0.5)) continue;
                kept.Add(l);
            }
            // A whole second copy of a line (another pass or slice read the same spot, one word misread) goes, so a
            // box over it doesn't say the line twice. Only whole copies: a line is never cut into pieces here.
            {
                var byLength = kept.OrderByDescending(l => l.Words.Count).ThenByDescending(l => l.Box.W).ToList();
                var drop = new HashSet<TextLine>();
                for (int i = 0; i < byLength.Count; i++)
                {
                    var l = byLength[i];
                    if (l.Words.Count == 0) continue;
                    for (int j = 0; j < i; j++)
                    {
                        var k = byLength[j];
                        if (drop.Contains(k) || IoU(k.Box, l.Box) < 0.3 && k.Box.Intersect(l.Box).Area < 0.8 * l.Box.Area) continue;
                        int same = l.Words.Count(w => k.Words.Any(x => Overlaps(x.Box, w.Box)));
                        if (same >= Math.Max(1, (int)Math.Ceiling(0.8 * l.Words.Count))) { drop.Add(l); break; }
                    }
                }
                kept = kept.Where(l => !drop.Contains(l)).ToList();
            }
            for (int i = 0; i < kept.Count; i++) kept[i].Id = i;
            page.Lines.AddRange(kept);

            var regions = new List<List<TextLine>>();
            Cut(kept, regions, 0, true);
            foreach (var region in regions)
                foreach (var b in GroupBlocks(region))
                {
                    b.Order = page.Blocks.Count;
                    page.Blocks.Add(b);
                }
            // Words remember their line (for line breaks and the highlight) and block (for pauses).
            foreach (var b in page.Blocks)
                foreach (var l in b.Lines)
                    foreach (var w in l.Words) { w.Line = l.Id; w.Block = b.Order; w.LineBox = l.Box; }
            return page;
        }

        private static IEnumerable<TextLine> Split(OcrLine src)
        {
            var words = src.Words.Where(w => !Bullets.Contains(w.Text)).OrderBy(w => w.Box.X).ToList();
            if (words.Count == 0) yield break;
            double h = Median(words.Select(w => w.Box.H));
            var cur = new TextLine();
            foreach (var w in words)
            {
                if (cur.Words.Count > 0)
                {
                    var prev = cur.Words[cur.Words.Count - 1];
                    double gap = w.Box.X - prev.Box.Right;
                    bool lonelyMarker = cur.Words.Count == 1 && prev.Text.Length <= 2; // "1." or "a)" before an item
                    if (gap > SplitGap * h && !lonelyMarker) { yield return cur; cur = new TextLine(); }
                }
                var copy = new OcrWord(w.Text, w.Box);
                cur.Words.Add(copy);
                cur.Box = cur.Box.Union(w.Box);
            }
            yield return cur;
        }

        // ---------------- XY-cut ----------------

        private static void Cut(List<TextLine> lines, List<List<TextLine>> output, int depth, bool mergeBands)
        {
            if (lines.Count == 0) return;
            if (lines.Count == 1 || depth > 40) { output.Add(lines); return; }
            double h = Median(lines.Select(l => l.Box.H));

            // Columns: a vertical gutter through the whole region (tiny marks such as bullets don't block it).
            var gaps = XGaps(lines.Where(l => !l.Tiny).ToList(), ColumnGap * h);
            if (gaps.Count > 0)
            {
                var cols = SplitByX(lines, gaps);
                if (cols.Count > 1) { foreach (var c in cols) Cut(c, output, depth + 1, true); return; }
            }

            // Bands: horizontal whitespace; bands that share a gutter belong to the same column layout.
            var rows = lines.OrderBy(l => l.Box.Y).ToList();
            var bands = new List<List<TextLine>>();
            double bottom = double.MinValue;
            foreach (var l in rows)
            {
                if (bands.Count == 0 || l.Box.Y - bottom > BlockGap * h) bands.Add(new List<TextLine>());
                bands[bands.Count - 1].Add(l);
                bottom = Math.Max(bottom, l.Box.Bottom);
            }
            if (bands.Count <= 1) { output.Add(lines); return; }

            // Neighbouring bands that continue the same gutter are one column layout: keep them together so
            // the next level splits them into columns. If that doesn't work out, fall back to plain bands.
            var merged = new List<List<TextLine>> { new List<TextLine>(bands[0]) };
            var counts = new List<int> { 1 };
            for (int i = 1; i < bands.Count && mergeBands; i++)
            {
                if (ShareGutter(bands[i - 1], bands[i], ColumnGap * h)) { merged[merged.Count - 1].AddRange(bands[i]); counts[counts.Count - 1]++; }
                else { merged.Add(new List<TextLine>(bands[i])); counts.Add(1); }
            }
            if (!mergeBands || merged.Count == 1) { foreach (var b in bands) Cut(b, output, depth + 1, true); return; }
            for (int i = 0; i < merged.Count; i++) Cut(merged[i], output, depth + 1, counts[i] == 1);
        }

        /// <summary>Interior x-intervals free of any line box, at least minGap wide.</summary>
        private static List<(double a, double b)> XGaps(List<TextLine> lines, double minGap)
        {
            var result = new List<(double, double)>();
            if (lines.Count < 2) return result;
            var spans = lines.Select(l => (a: l.Box.X, b: l.Box.Right)).OrderBy(s => s.a).ToList();
            double reach = spans[0].b;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].a - reach >= minGap) result.Add((reach, spans[i].a));
                reach = Math.Max(reach, spans[i].b);
            }
            return result;
        }

        private static List<List<TextLine>> SplitByX(List<TextLine> lines, List<(double a, double b)> gaps)
        {
            var cols = new List<List<TextLine>>();
            for (int i = 0; i <= gaps.Count; i++) cols.Add(new List<TextLine>());
            foreach (var l in lines)
            {
                double x = l.Tiny ? l.Box.Right : l.Box.CenterX; // a bullet goes with the text to its right
                int i = 0;
                while (i < gaps.Count && x > (gaps[i].a + gaps[i].b) / 2) i++;
                cols[i].Add(l);
            }
            return cols.Where(c => c.Count > 0).ToList();
        }

        private static bool ShareGutter(List<TextLine> a, List<TextLine> b, double minGap)
        {
            var ga = XGaps(a.Where(l => !l.Tiny).ToList(), minGap);
            var gb = XGaps(b.Where(l => !l.Tiny).ToList(), minGap);
            foreach (var x in ga)
                foreach (var y in gb)
                    if (Math.Min(x.b, y.b) - Math.Max(x.a, y.a) >= minGap) return true;
            return false;
        }

        // ---------------- Blocks ----------------

        /// <summary>
        /// Paragraphs inside a region, column-aware: a line joins the block whose last line sits right above it
        /// (vertical gap at most <see cref="BlockGap"/> line heights, or up to 1.4x the block's own line spacing),
        /// with a similar text height (<see cref="HeightRatio"/>), overlapping it sideways and lined up on the left
        /// edge or the centre (<see cref="AlignSlop"/> line heights, so a wrapped list item's indent stays in).
        /// Lines side by side are never one block (they are separate columns or cards), even where the XY-cut could
        /// not separate the columns (a window overlapping another, a line bridging the gutter).
        /// Blocks are read top to bottom, then left to right.
        /// </summary>
        private static List<TextBlock> GroupBlocks(List<TextLine> region)
        {
            var blocks = new List<TextBlock>();
            var last = new List<TextLine>();
            var spacing = new List<double>();
            foreach (var l in region.OrderBy(x => x.Box.Y).ThenBy(x => x.Box.X))
            {
                int best = -1;
                double bestGap = double.MaxValue;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    var prev = last[i];
                    double hPrev = prev.Box.H, hNow = l.Box.H;
                    double h = Math.Max(1, Math.Min(hPrev, hNow));
                    double gap = l.Box.Y - prev.Box.Bottom;
                    if (gap < -0.5 * h) continue;                                   // side by side: not one under the other
                    bool similar = Math.Max(hPrev, hNow) <= HeightRatio * Math.Min(hPrev, hNow);
                    bool overlapX = Math.Min(l.Box.Right, b.Box.Right) - Math.Max(l.Box.X, b.Box.X) > 0;
                    bool aligned = overlapX && (Math.Abs(l.Box.X - b.Box.X) <= AlignSlop * h || Math.Abs(l.Box.CenterX - b.Box.CenterX) <= AlignSlop * h);
                    double allowed = Math.Max(BlockGap * h, spacing[i] >= 0 ? spacing[i] * 1.4 : 0);
                    allowed = Math.Min(allowed, 1.5 * h);
                    if (gap <= allowed && similar && aligned && gap < bestGap) { best = i; bestGap = gap; }
                }
                if (best >= 0 && Between(region, last[best], l)) best = -1;            // another line sits in between
                if (best < 0)
                {
                    var nb = new TextBlock();
                    nb.Lines.Add(l); nb.Box = l.Box;
                    blocks.Add(nb); last.Add(l); spacing.Add(-1);
                    continue;
                }
                blocks[best].Lines.Add(l);
                blocks[best].Box = blocks[best].Box.Union(l.Box);
                last[best] = l;
                spacing[best] = Math.Max(0, bestGap);
            }
            return blocks.OrderBy(b => b.Box.Y).ThenBy(b => b.Box.X).ToList();
        }

        /// <summary>Is there a line vertically between a and b that overlaps both sideways?</summary>
        private static bool Between(List<TextLine> region, TextLine a, TextLine b)
        {
            foreach (var l in region)
            {
                if (l == a || l == b) continue;
                if (l.Box.CenterY <= a.Box.Bottom || l.Box.CenterY >= b.Box.Y) continue;
                if (Math.Min(l.Box.Right, b.Box.Right) - Math.Max(l.Box.X, b.Box.X) > 0 &&
                    Math.Min(l.Box.Right, a.Box.Right) - Math.Max(l.Box.X, a.Box.X) > 0) return true;
            }
            return false;
        }

        // ---------------- Selection ----------------

        /// <summary>
        /// Smart snap of a user's box to the text they meant, in reading order.
        /// For each block: area coverage = share of its word area inside the box; line coverage = share of its
        /// lines with at least <see cref="LineCovered"/> of their word area inside.
        /// - Whole block when area coverage ≥ 50% or line coverage ≥ 60% (cut lines and words are completed).
        /// - Below 25% area coverage the block is dropped, unless the box is tight around some of its lines
        ///   (those lines are read) or tight around a few words (the words fill ≥ 50% of the box: those words).
        /// - In between: the lines that are mostly inside, plus words at least 50% inside on the other lines.
        /// If nothing qualifies but some words are at least 50% inside, those words are read.
        /// Exception first: a box drawn tightly around whole lines (lines at least 90% inside whose own area fills
        /// at least 50% of the box) reads exactly those lines, even if that is half of a short paragraph.
        /// </summary>
        // Selection rules (v14, see Snap).
        public const double LineVertical = 0.3;   // a line counts when the box covers at least 30% of its height

        /// <summary>
        /// What a user's box reads (v14, like Android Select to Speak), in reading order: blocks in page order
        /// (columns left to right, top to bottom), lines top to bottom inside each block, words left to right.
        /// - Every visual line the box touches is read whole: the box must cover at least <see cref="LineVertical"/>
        ///   of the line's height and reach its text sideways (a thin strip over the starts of a paragraph's lines is
        ///   enough). The line is never cut at the box edge, so no word fragments.
        /// - Nothing outside the box's lines: a block above or below (a title) is read only if one of its own lines is
        ///   touched. Lines are split at column gaps first, so a strip in one column never reads the next column.
        /// - Overlapping duplicates (the same word seen by several OCR passes or slices) are read once (<see cref="Dedupe"/>).
        /// </summary>
        public List<SnapPart> Snap(Box sel)
        {
            var parts = new List<SnapPart>();
            if (sel.W <= 0 && sel.H <= 0) return parts;   // a zero-width strip (a straight vertical drag) still counts
            foreach (var b in Blocks)
            {
                SnapPart part = null;
                int fullLines = 0;
                double blockArea = 0, insideArea = 0;
                foreach (var l in b.Lines) // already rows top to bottom, left to right
                {
                    foreach (var w in l.Words) { blockArea += w.Box.Area; insideArea += w.Box.Intersect(sel).Area; }
                    if (!Touches(l, sel)) continue;
                    fullLines++;
                    if (part == null) part = new SnapPart { Block = b };
                    part.Words.AddRange(l.Words.OrderBy(w => w.Box.X));
                    part.Box = part.Box.Union(l.Box);
                }
                if (part == null) continue;
                part.Kind = fullLines == b.Lines.Count ? SnapKind.Whole : SnapKind.Lines;
                part.AreaCoverage = blockArea > 0 ? insideArea / blockArea : 0;
                part.LineCoverage = (double)fullLines / b.Lines.Count;
                var kept = Dedupe(part.Words);
                part.Words.Clear(); part.Words.AddRange(kept);
                if (part.Kind == SnapKind.Whole) part.Box = b.Box;
                parts.Add(part);
            }
            return parts;
        }

        /// <summary>Does the box touch this line: enough of its height, and sideways anywhere over its text?</summary>
        public static bool Touches(TextLine l, Box sel)
        {
            if (l.Box.H <= 0) return false;
            double vo = Math.Min(l.Box.Bottom, sel.Bottom) - Math.Max(l.Box.Y, sel.Y);
            bool within = sel.Y >= l.Box.Y && sel.Bottom <= l.Box.Bottom;   // a flat box drawn along the line itself
            if (vo < LineVertical * l.Box.H && !within) return false;
            double tol = Math.Max(2, 0.25 * l.Box.H);   // a strip just left of the first letter still counts
            if (sel.Right < l.Box.X - tol || sel.X > l.Box.Right + tol) return false;
            // Within the line's span, the box must reach a word (not only the gap between two far-apart words).
            foreach (var w in l.Words)
                if (sel.Right >= w.Box.X - tol && sel.X <= w.Box.Right + tol) return true;
            return false;
        }

        /// <summary>
        /// The paragraph under a tap: the block holding the word nearest to the point (within maxDist), read from the
        /// block's first line. Null when no word is that close.
        /// </summary>
        public SnapPart ParagraphAt(double x, double y, double maxDist)
        {
            var line = NearestLine(x, y, maxDist);
            if (line == null) return null;
            var b = BlockOf(line);
            if (b == null) { var lone = new SnapPart { Kind = SnapKind.Lines, AreaCoverage = 1, LineCoverage = 1 }; lone.Words.AddRange(Dedupe(line.Words)); lone.Box = line.Box; return lone; }
            return PartOf(b);
        }

        /// <summary>A whole block, read as one part (a tap's paragraph, or a neighbouring block for Previous / Next).</summary>
        public SnapPart PartOf(TextBlock b)
        {
            var part = new SnapPart { Block = b, Kind = SnapKind.Whole, AreaCoverage = 1, LineCoverage = 1 };
            var words = new List<OcrWord>();
            foreach (var l in b.Lines) words.AddRange(l.Words.OrderBy(w => w.Box.X));
            part.Words.AddRange(Dedupe(words));
            part.Box = b.Box;
            return part;
        }

        /// <summary>The block after / before one in reading order (null at the ends).</summary>
        public TextBlock Neighbour(TextBlock b, int step)
        {
            if (b == null) return null;
            int i = Blocks.IndexOf(b) + step;
            return i >= 0 && i < Blocks.Count ? Blocks[i] : null;
        }

        /// <summary>
        /// Drops OCR artifacts before speech: a word whose box mostly overlaps an earlier kept word on the same line
        /// (the same word from two passes or two overlapping slices, even if read slightly differently), and a word
        /// repeated right next to itself where the two boxes overlap or touch (a slice boundary seen twice).
        /// A real repeat ("that that") has two separate boxes with a normal space between them and is kept.
        /// </summary>
        public static List<OcrWord> Dedupe(IEnumerable<OcrWord> words)
        {
            var kept = new List<OcrWord>();
            foreach (var w in words)
            {
                bool dup = kept.Any(k => Overlaps(k.Box, w.Box));
                if (!dup && kept.Count > 0)
                {
                    var k = kept[kept.Count - 1];
                    string a = Plain(k.Text), b = Plain(w.Text);
                    if (k.Line == w.Line && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    {
                        double gap = w.Box.X - k.Box.Right;
                        double h = Math.Max(1, Math.Min(k.Box.H, w.Box.H));
                        dup = gap < 0.12 * h;
                    }
                }
                if (!dup) kept.Add(w);
            }
            return kept;
        }

        /// <summary>Two word boxes are the same word when they overlap by at least half of the smaller one.</summary>
        public static bool Overlaps(Box a, Box b)
        {
            double inter = a.Intersect(b).Area;
            double small = Math.Min(a.Area, b.Area);
            return small > 0 && inter >= 0.5 * small;
        }

        private static string Plain(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>The v12 snap rules (kept only so the self-test can measure before/after recall).</summary>
        public List<SnapPart> SnapLegacy(Box sel)
        {
            var parts = new List<SnapPart>();
            if (sel.IsEmpty) return parts;

            var full = Lines.Where(l => Coverage(l, sel) >= FullyCovered).ToList();
            double fill = full.Sum(l => l.Box.Intersect(sel).Area);
            if (full.Count > 0 && fill >= TightLines * sel.Area)
            {
                foreach (var b in Blocks)
                {
                    var ls = b.Lines.Where(full.Contains).ToList();
                    if (ls.Count == 0) continue;
                    var part = new SnapPart { Block = b, Kind = ls.Count == b.Lines.Count ? SnapKind.Whole : SnapKind.Lines, LineCoverage = (double)ls.Count / b.Lines.Count, AreaCoverage = 1 };
                    foreach (var l in ls) { part.Words.AddRange(l.Words); part.Box = part.Box.Union(l.Box); }
                    parts.Add(part);
                }
                return parts;
            }

            foreach (var b in Blocks)
            {
                if (b.Box.Intersect(sel).IsEmpty) continue;
                double total = 0, inside = 0;
                int covered = 0;
                var lineCov = new Dictionary<TextLine, double>();
                foreach (var l in b.Lines)
                {
                    double lt = 0, li = 0;
                    foreach (var w in l.Words) { lt += w.Box.Area; li += w.Box.Intersect(sel).Area; }
                    double c = lt > 0 ? li / lt : 0;
                    lineCov[l] = c;
                    if (c >= LineCovered) covered++;
                    total += lt; inside += li;
                }
                if (inside <= 0) continue;
                var part = new SnapPart { Block = b, AreaCoverage = total > 0 ? inside / total : 0, LineCoverage = (double)covered / b.Lines.Count };

                if (part.AreaCoverage >= WholeByArea || part.LineCoverage >= WholeByLines)
                {
                    part.Kind = SnapKind.Whole;
                    part.Words.AddRange(b.Words);
                }
                else if (part.AreaCoverage < DropBelow)
                {
                    if (covered > 0)
                    {
                        part.Kind = SnapKind.Lines;
                        foreach (var l in b.Lines) if (lineCov[l] >= LineCovered) part.Words.AddRange(l.Words);
                    }
                    else
                    {
                        var ws = b.Words.Where(w => Inside(w, sel)).ToList();
                        var u = ws.Aggregate(new Box(), (s, w) => s.Union(w.Box));
                        if (ws.Count == 0 || u.Intersect(sel).Area < TightBox * sel.Area) continue;
                        part.Kind = SnapKind.Words;
                        part.Words.AddRange(ws);
                    }
                }
                else
                {
                    part.Kind = SnapKind.Lines;
                    foreach (var l in b.Lines)
                    {
                        if (lineCov[l] >= LineCovered) part.Words.AddRange(l.Words);
                        else part.Words.AddRange(l.Words.Where(w => Inside(w, sel)));
                    }
                }
                if (part.Words.Count == 0) continue;
                part.Box = part.Kind == SnapKind.Whole ? b.Box : part.Words.Aggregate(new Box(), (s, w) => s.Union(w.LineBox.IsEmpty ? w.Box : new Box(w.Box.X, w.LineBox.Y, w.Box.W, w.LineBox.H)));
                parts.Add(part);
            }
            if (parts.Count == 0)
            {
                // Nothing snapped (for example a small box inside a big paragraph): read the words inside it.
                foreach (var b in Blocks)
                {
                    var ws = b.Words.Where(w => Inside(w, sel)).ToList();
                    if (ws.Count == 0) continue;
                    var part = new SnapPart { Block = b, Kind = SnapKind.Words };
                    part.Words.AddRange(ws);
                    part.Box = ws.Aggregate(new Box(), (s, w) => s.Union(w.Box));
                    parts.Add(part);
                }
            }
            return parts;
        }

        private static double Coverage(TextLine l, Box sel)
        {
            double t = 0, i = 0;
            foreach (var w in l.Words) { t += w.Box.Area; i += w.Box.Intersect(sel).Area; }
            return t > 0 ? i / t : 0;
        }

        public static List<OcrWord> WordsOf(List<SnapPart> parts) => parts.SelectMany(p => p.Words).ToList();

        private static bool Inside(OcrWord w, Box sel) => w.Box.Area > 0 && w.Box.Intersect(sel).Area >= WordInside * w.Box.Area;

        /// <summary>The visual line under a point (a tap on a word), or null.</summary>
        public TextLine LineAt(double x, double y, double slop = 3)
        {
            foreach (var l in Lines)
                foreach (var w in l.Words)
                    if (w.Box.Inflate(slop).Contains(x, y)) return l;
            return null;
        }

        /// <summary>The line of the word nearest to a point (a tap), if that word is within maxDist; else null.</summary>
        public TextLine NearestLine(double x, double y, double maxDist)
        {
            TextLine best = null;
            double bestD = double.MaxValue;
            foreach (var l in Lines)
                foreach (var w in l.Words)
                {
                    double dx = Math.Max(0, Math.Max(w.Box.X - x, x - w.Box.Right));
                    double dy = Math.Max(0, Math.Max(w.Box.Y - y, y - w.Box.Bottom));
                    // Ties (a point inside two boxes) go to the word whose centre is nearer.
                    double d = Math.Sqrt(dx * dx + dy * dy) + 1e-3 * Math.Sqrt(Math.Pow(w.Box.CenterX - x, 2) + Math.Pow(w.Box.CenterY - y, 2));
                    if (d < bestD) { bestD = d; best = l; }
                }
            return bestD <= maxDist ? best : null;
        }

        public TextBlock BlockOf(TextLine line) => Blocks.FirstOrDefault(b => b.Lines.Contains(line));

        private static double IoU(Box a, Box b)
        {
            double i = a.Intersect(b).Area;
            double u = a.Area + b.Area - i;
            return u > 0 ? i / u : 0;
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.Where(x => x > 0).OrderBy(x => x).ToList();
            if (v.Count == 0) return 1;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
    }
}
