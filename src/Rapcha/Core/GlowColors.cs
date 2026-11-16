using System;
using System.Collections.Generic;
using System.Globalization;

namespace Rapcha.Core
{
    /// <summary>The one glow colour (field outline and select-to-speak screen edge), stored as "#RRGGBB".</summary>
    public static class GlowColors
    {
        /// <summary>The ring's olive/yellow.</summary>
        public const int Default = 0xA0BE3C;

        public static readonly IReadOnlyList<KeyValuePair<string, int>> Presets = new[]
        {
            new KeyValuePair<string, int>("Olive", 0xA0BE3C),
            new KeyValuePair<string, int>("Pink", 0xF0559B),
            new KeyValuePair<string, int>("Blue", 0x4285F4),
            new KeyValuePair<string, int>("Purple", 0x9B5CFF),
            new KeyValuePair<string, int>("Teal", 0x1AA99A),
            new KeyValuePair<string, int>("Orange", 0xFF8A1F),
        };

        public static string Format(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        public static int Parse(string text)
        {
            var t = (text ?? "").Trim().TrimStart('#');
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return v & 0xFFFFFF;
            return Default;
        }

        /// <summary>The preset's name, or "Custom".</summary>
        public static string NameOf(int rgb)
        {
            foreach (var p in Presets) if (p.Value == (rgb & 0xFFFFFF)) return p.Key;
            return "Custom";
        }
    }
}
