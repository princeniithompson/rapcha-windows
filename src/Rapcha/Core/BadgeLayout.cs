using System;
using System.Collections.Generic;
using System.Linq;

namespace Rapcha.Core
{
    /// <summary>
    /// Where the outlines of a multi-block selection and their number badges go (v15). Outlines get a padding
    /// around each block, trimmed between neighbours so two outlines never overlap. Each badge is a small circle
    /// outside its block: at the outer left margin beside the first line, else at the top-left outside corner,
    /// else the nearest free spot around the block; never on text, another outline or another badge.
    /// </summary>
    public static class BadgeLayout
    {
        public sealed class Result
        {
            public readonly List<Box> Outlines = new List<Box>();
            public readonly List<Box> Badges = new List<Box>();   // in reading order: badge i shows i + 1
        }

        /// <param name="blocks">The blocks' boxes in reading order.</param>
        /// <param name="firstLines">Each block's first line box (the badge sits beside it).</param>
        /// <param name="text">Every word box on the screen (badges must not cover any).</param>
        /// <param name="pad">Outline padding around a block.</param>
        /// <param name="d">Badge diameter.</param>
        /// <param name="gap">Space between a badge and what it sits next to.</param>
        public static Result Place(IList<Box> blocks, IList<Box> firstLines, IList<Box> text, Box screen, double pad, double d, double gap, bool badges = true)
        {
            var res = new Result();
            int n = blocks.Count;
            var l = new double[n]; var t = new double[n]; var r = new double[n]; var b = new double[n];
            for (int i = 0; i < n; i++) { l[i] = t[i] = r[i] = b[i] = pad; }
            // Trim the padding between neighbours (half the gap each, minus a pixel) so outlines never touch.
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    Box a = blocks[i], c = blocks[j];
                    bool overlapY = Math.Min(a.Bottom, c.Bottom) - Math.Max(a.Y, c.Y) > -2 * pad;
                    bool overlapX = Math.Min(a.Right, c.Right) - Math.Max(a.X, c.X) > -2 * pad;
                    double gx = c.X - a.Right, gy = c.Y - a.Bottom;   // c to the right of / below a
                    if (overlapY && gx >= 0 && gx < 2 * pad + 2 && (gy < 0 || gx >= gy)) { double h = Math.Max(0, (gx - 2) / 2); r[i] = Math.Min(r[i], h); l[j] = Math.Min(l[j], h); }
                    if (overlapX && gy >= 0 && gy < 2 * pad + 2 && (gx < 0 || gy > gx)) { double h = Math.Max(0, (gy - 2) / 2); b[i] = Math.Min(b[i], h); t[j] = Math.Min(t[j], h); }
                }
            for (int i = 0; i < n; i++)
            {
                var a = blocks[i];
                res.Outlines.Add(new Box(a.X - l[i], a.Y - t[i], a.W + l[i] + r[i], a.H + t[i] + b[i]));
            }
            if (!badges || n < 2) return res;

            var placed = new List<Box>();
            for (int i = 0; i < n; i++)
            {
                var o = res.Outlines[i];
                var fl = i < firstLines.Count && !firstLines[i].IsEmpty ? firstLines[i] : blocks[i];
                var cands = new List<Box>
                {
                    new Box(o.X - gap - d, fl.CenterY - d / 2, d, d),          // outer left margin, beside the first line
                    new Box(o.X - gap - d, o.Y - gap - d, d, d),               // top-left outside corner
                    new Box(o.X, o.Y - gap - d, d, d),                         // above the first word
                    new Box(o.X - gap - d, o.Y, d, d),                         // left, level with the outline's top
                };
                // Then the nearest free spots around the block (left side downwards, above rightwards, right, below).
                for (int k = 1; k <= 12; k++)
                {
                    cands.Add(new Box(o.X - gap - d, fl.CenterY - d / 2 + k * d * 0.75, d, d));
                    cands.Add(new Box(o.X - gap - d - k * d * 0.75, fl.CenterY - d / 2, d, d));
                    cands.Add(new Box(o.X + k * d, o.Y - gap - d, d, d));
                    cands.Add(new Box(o.Right + gap, o.Y + (k - 1) * d * 0.75, d, d));
                    cands.Add(new Box(o.X + (k - 1) * d, o.Bottom + gap, d, d));
                }
                Box best = cands[0]; bool found = false;
                foreach (var c in cands)
                {
                    if (Free(c, screen, text, res.Outlines, placed, gap * 0.5)) { best = c; found = true; break; }
                }
                if (!found)
                    foreach (var c in cands)   // no fully free spot: at least never on text or another badge
                        if (Inside(c, screen) && !Hits(c, text, 0) && !Hits(c, placed, 1)) { best = c; break; }
                placed.Add(best);
            }
            res.Badges.AddRange(placed);
            return res;
        }

        private static bool Inside(Box c, Box s) => s.IsEmpty || (c.X >= s.X && c.Y >= s.Y && c.Right <= s.Right && c.Bottom <= s.Bottom);

        private static bool Free(Box c, Box screen, IList<Box> text, IList<Box> outlines, IList<Box> badges, double m)
            => Inside(c, screen) && !Hits(c, text, m) && !Hits(c, outlines, 1) && !Hits(c, badges, m);

        public static bool Hits(Box c, IEnumerable<Box> others, double margin)
        {
            var g = c.Inflate(margin);
            foreach (var o in others)
                if (Math.Min(g.Right, o.Right) - Math.Max(g.X, o.X) > 0 && Math.Min(g.Bottom, o.Bottom) - Math.Max(g.Y, o.Y) > 0) return true;
            return false;
        }

        public static bool Overlap(Box a, Box b)
            => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 0 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 0;
    }
}
