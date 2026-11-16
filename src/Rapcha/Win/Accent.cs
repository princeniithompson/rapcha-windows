using System;
using System.Drawing;

namespace Rapcha.Win
{
    /// <summary>
    /// The user's glow colour (Settings → Glow colour), shared by the ring glow, the Words bubble, the text-field
    /// outline and the select-to-speak screen glow. The green reading-word box is not affected.
    /// </summary>
    public static class Accent
    {
        public static Color Color { get; private set; } = Color.FromArgb(0xA0, 0xBE, 0x3C);
        public static event Action Changed;

        public static void Set(Color c)
        {
            c = Color.FromArgb(255, c);
            if (c.ToArgb() == Color.ToArgb()) return;
            Color = c;
            Changed?.Invoke();
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Lighter(double t) => Mix(Color, Color.White, t);
        public static Color Darker(double t) => Mix(Color, Color.Black, t);

        /// <summary>Relative luminance 0..1.</summary>
        public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        /// <summary>Readable text on a fill of this colour.</summary>
        public static Color TextOn(Color fill) => Luma(fill) > 0.55 ? Color.FromArgb(20, 24, 16) : Color.White;
    }
}
