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

        private static List<TextBlock> GroupBlocks(List<TextLine> region)
        {
            // Rows: lines that share most of their height, left to right.
            var rows = new List<List<TextLine>>();
            foreach (var l in region.OrderBy(l => l.Box.Y))
            {
                var row = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (row != null)
                {
                    var rb = row.Aggregate(new Box(), (s, x) => s.Union(x.Box));
                    double overlap = Math.Min(rb.Bottom, l.Box.Bottom) - Math.Max(rb.Y, l.Box.Y);
                    if (overlap >= 0.5 * Math.Min(rb.H, l.Box.H)) { row.Add(l); continue; }
                }
                rows.Add(new List<TextLine> { l });
            }
            foreach (var r in rows) r.Sort((x, y) => x.Box.X.CompareTo(y.Box.X));

            var blocks = new List<TextBlock>();
            TextBlock cur = null;
            double lastGap = -1, lastH = 0;
            Box lastRow = new Box();
            foreach (var r in rows)
            {
                var rb = r.Aggregate(new Box(), (s, x) => s.Union(x.Box));
                bool join = false;
                if (cur != null)
                {
                    double hPrev = lastH;
                    double hNow = Median(r.Select(x => x.Box.H));
                    double h = Math.Min(hPrev, hNow);
                    double gap = rb.Y - lastRow.Bottom;
                    bool similar = Math.Max(hPrev, hNow) <= HeightRatio * Math.Min(hPrev, hNow);
                    bool aligned = Math.Abs(rb.X - cur.Box.X) <= AlignSlop * h || Math.Abs(rb.CenterX - cur.Box.CenterX) <= AlignSlop * h;
                    double allowed = Math.Max(BlockGap * h, lastGap >= 0 ? lastGap * 1.4 : 0);
                    allowed = Math.Min(allowed, 1.5 * h);
                    join = gap <= allowed && similar && aligned;
                    if (join) lastGap = Math.Max(0, gap);
                }
                if (!join) { cur = new TextBlock(); blocks.Add(cur); lastGap = -1; }
                cur.Lines.AddRange(r);
                cur.Box = cur.Box.Union(rb);
                lastRow = rb;
                lastH = Median(r.Select(x => x.Box.H));
            }
            return blocks;
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
        // Selection rules (v13, see Snap).
        public const double LineVertical = 0.3;   // a line counts when the box covers at least 30% of its height
        public const double WordHorizontal = 0.4; // a word counts when 40% of its width (or its centre) is inside
        public const double CompleteLine = 0.75;  // a line whose text is 75% inside is completed

        /// <summary>
        /// What a user's box reads, in reading order (blocks in page order: columns left to right, top to bottom;
        /// lines top to bottom inside each block; words left to right).
        /// - Never outside the box: a line takes part only when the box covers at least <see cref="LineVertical"/> of
        ///   its height, and on such a line only words whose centre is inside the box's width (with a small
        ///   tolerance) or that are at least <see cref="WordHorizontal"/> inside are read. A separate block above or
        ///   below (a title) is never pulled in.
        /// - Never a dropped line: every line that meets the rule above is read, however little of its block is in
        ///   the box.
        /// - Snap only expands inside the box's own lines: a cut word is read whole, and a line whose text is at
        ///   least <see cref="CompleteLine"/> inside is read whole.
        /// </summary>
        public List<SnapPart> Snap(Box sel)
        {
            var parts = new List<SnapPart>();
            if (sel.IsEmpty) return parts;
            foreach (var b in Blocks)
            {
                if (b.Box.Intersect(sel.Inflate(2)).IsEmpty) continue;
                SnapPart part = null;
                int fullLines = 0;
                double blockArea = 0, insideArea = 0;
                foreach (var l in b.Lines) // already rows top to bottom, left to right
                {
                    foreach (var w in l.Words) { blockArea += w.Box.Area; insideArea += w.Box.Intersect(sel).Area; }
                    double vo = Math.Min(l.Box.Bottom, sel.Bottom) - Math.Max(l.Box.Y, sel.Y);
                    if (l.Box.H <= 0 || vo < LineVertical * l.Box.H) continue;
                    double tol = Math.Max(3, 0.3 * l.Box.H);
                    var ws = l.Words.Where(w =>
                        (w.Box.CenterX >= sel.X - tol && w.Box.CenterX <= sel.Right + tol) ||
                        (w.Box.W > 0 && (Math.Min(w.Box.Right, sel.Right) - Math.Max(w.Box.X, sel.X)) >= WordHorizontal * w.Box.W)).ToList();
                    if (ws.Count == 0) continue;
                    double all = l.Words.Sum(w => w.Box.W), got = ws.Sum(w => w.Box.W);
                    if (ws.Count < l.Words.Count && all > 0 && got >= CompleteLine * all) ws = l.Words.ToList();
                    if (ws.Count == l.Words.Count) fullLines++;
                    if (part == null) part = new SnapPart { Block = b };
                    part.Words.AddRange(ws.OrderBy(w => w.Box.X));
                }
                if (part == null) continue;
                part.Kind = fullLines == b.Lines.Count ? SnapKind.Whole : part.Words.Count > 0 && fullLines > 0 ? SnapKind.Lines : SnapKind.Words;
                part.AreaCoverage = blockArea > 0 ? insideArea / blockArea : 0;
                part.LineCoverage = (double)fullLines / b.Lines.Count;
                part.Box = part.Kind == SnapKind.Whole ? b.Box : part.Words.Aggregate(new Box(), (s, w) => s.Union(w.LineBox.IsEmpty ? w.Box : new Box(w.Box.X, w.LineBox.Y, w.Box.W, w.LineBox.H)));
                parts.Add(part);
            }
            return parts;
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
                    double d = Math.Sqrt(dx * dx + dy * dy);
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
