using System;

namespace Rapcha.Core
{
    /// <summary>Where the Words bubble goes next to the ring: on the screen side, flipped if it would not fit.</summary>
    public static class BubblePlacement
    {
        public static void Beside(IntRect ring, DockEdge edge, int w, int h, IntRect work, int gap, out int x, out int y)
        {
            int right = ring.Right + gap, left = ring.X - gap - w;
            switch (edge)
            {
                case DockEdge.Left: x = right + w <= work.Right ? right : left; y = ring.Y + ring.Height / 2 - h / 2; break;
                case DockEdge.Right: x = left >= work.X ? left : right; y = ring.Y + ring.Height / 2 - h / 2; break;
                case DockEdge.Top: x = ring.X + ring.Width / 2 - w / 2; y = ring.Bottom + gap + h <= work.Bottom ? ring.Bottom + gap : ring.Y - gap - h; break;
                default: x = ring.X + ring.Width / 2 - w / 2; y = ring.Y - gap - h >= work.Y ? ring.Y - gap - h : ring.Bottom + gap; break;
            }
            x = Math.Max(work.X, Math.Min(x, work.Right - w));
            y = Math.Max(work.Y, Math.Min(y, work.Bottom - h));
        }
    }
}
