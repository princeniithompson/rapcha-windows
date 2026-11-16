using System;

namespace Rapcha.Core
{
    public enum DockEdge { Left = 0, Right = 1, Top = 2, Bottom = 3 }

    public struct IntRect
    {
        public int X, Y, Width, Height;
        public IntRect(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public override string ToString() => $"{X},{Y},{Width}x{Height}";
    }

    public struct DockPosition
    {
        public DockEdge Edge;
        /// <summary>0 = start of the edge (top or left), 1 = end.</summary>
        public double Fraction;
        public DockPosition(DockEdge edge, double fraction) { Edge = edge; Fraction = fraction; }
    }

    /// <summary>
    /// Keeps the ring on a screen edge, never in the middle. All values are physical pixels.
    /// </summary>
    public static class EdgeSnap
    {
        /// <summary>Which side edge (left or right) is nearest to the ring's centre, and where along it.</summary>
        public static DockPosition Snap(IntRect workArea, int centerX, int centerY, int ringSize)
        {
            int cx = Clamp(centerX, workArea.X, workArea.Right);
            int cy = Clamp(centerY, workArea.Y, workArea.Bottom);
            int dLeft = cx - workArea.X;
            int dRight = workArea.Right - cx;
            // Side edges only (left or right), never the middle; top and bottom would cover tabs and the taskbar.
            DockEdge edge = dLeft <= dRight ? DockEdge.Left : DockEdge.Right;

            double fraction;
            if (edge == DockEdge.Left || edge == DockEdge.Right)
                fraction = Fraction(cy - ringSize / 2, workArea.Y, workArea.Height - ringSize);
            else
                fraction = Fraction(cx - ringSize / 2, workArea.X, workArea.Width - ringSize);
            return new DockPosition(edge, fraction);
        }

        /// <summary>Top-left corner of the ring (not the window) for a dock position.</summary>
        public static void Place(IntRect workArea, DockPosition pos, int ringSize, int gap, out int x, out int y)
        {
            double f = Math.Max(0.0, Math.Min(1.0, double.IsNaN(pos.Fraction) ? 0.5 : pos.Fraction));
            int alongY = workArea.Y + (int)Math.Round(f * Math.Max(0, workArea.Height - ringSize));
            int alongX = workArea.X + (int)Math.Round(f * Math.Max(0, workArea.Width - ringSize));
            switch (pos.Edge)
            {
                case DockEdge.Left: x = workArea.X + gap; y = alongY; break;
                case DockEdge.Right: x = workArea.Right - ringSize - gap; y = alongY; break;
                case DockEdge.Top: x = alongX; y = workArea.Y + gap; break;
                default: x = alongX; y = workArea.Bottom - ringSize - gap; break;
            }
        }

        public static DockPosition Default => new DockPosition(DockEdge.Right, 0.35);

        private static double Fraction(int value, int start, int range)
        {
            if (range <= 0) return 0.5;
            return Math.Max(0.0, Math.Min(1.0, (value - start) / (double)range));
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
