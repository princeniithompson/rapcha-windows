using System;
using System.Globalization;

namespace Rapcha.Core
{
    /// <summary>
    /// Hold-to-snooze geometry: the "middle" of the screen (well away from every edge, where the ring never docks)
    /// and where the Snooze circle goes. All values are physical pixels.
    /// </summary>
    public static class SnoozeZone
    {
        /// <summary>The middle area: the work area with 22% cut off each side.</summary>
        public const double Inset = 0.22;

        public static IntRect Middle(IntRect work)
        {
            int dx = (int)Math.Round(work.Width * Inset), dy = (int)Math.Round(work.Height * Inset);
            return new IntRect(work.X + dx, work.Y + dy, Math.Max(0, work.Width - dx * 2), Math.Max(0, work.Height - dy * 2));
        }

        public static bool InMiddle(IntRect work, int centerX, int centerY)
        {
            var m = Middle(work);
            return m.Width > 0 && m.Height > 0 && centerX >= m.X && centerX <= m.Right && centerY >= m.Y && centerY <= m.Bottom;
        }

        /// <summary>
        /// Where the circle's centre goes: the middle of the work area, unless the held ring already covers that spot;
        /// then just below the ring (or above it if there's no room), so the user has to move onto it on purpose.
        /// </summary>
        public static void PlaceCircle(IntRect work, int ringCenterX, int ringCenterY, int ringSize, int circleSize, out int x, out int y)
        {
            x = work.X + work.Width / 2;
            y = work.Y + work.Height / 2;
            int clear = (ringSize + circleSize) / 2 + ringSize / 2;
            if (Math.Abs(ringCenterX - x) < clear && Math.Abs(ringCenterY - y) < clear)
            {
                x = ringCenterX;
                int below = ringCenterY + clear + ringSize / 2;
                y = below + circleSize / 2 <= work.Bottom ? below : ringCenterY - clear - ringSize / 2;
            }
            int half = circleSize / 2;
            x = Math.Max(work.X + half, Math.Min(work.Right - half, x));
            y = Math.Max(work.Y + half, Math.Min(work.Bottom - half, y));
        }

        /// <summary>Is the dropped ring on the circle? Its centre must be inside the circle, give or take a quarter ring.</summary>
        public static bool Hit(int circleX, int circleY, int circleSize, int ringCenterX, int ringCenterY, int ringSize)
        {
            double r = circleSize / 2.0 + ringSize / 4.0;
            double dx = ringCenterX - circleX, dy = ringCenterY - circleY;
            return dx * dx + dy * dy <= r * r;
        }
    }

    /// <summary>
    /// The hold: the ring is being dragged, sits in the middle area and stays within a few pixels of one spot for
    /// 5.5 s. Any bigger movement, or leaving the middle, starts the wait again, so a quick drag across the middle can
    /// never arm it. Once armed it stays armed until the drag ends (the user then moves onto the circle).
    /// </summary>
    public sealed class SnoozeHold
    {
        public const int HoldMs = 5500;
        public const int ToleranceDip = 6;

        private readonly int _tolerance;
        private bool _holding;
        private int _ax, _ay;
        private double _since;

        public SnoozeHold(int tolerancePx) { _tolerance = Math.Max(1, tolerancePx); }

        public bool Armed { get; private set; }
        public int TolerancePx => _tolerance;

        /// <summary>How long the ring has been held still so far (0 when not holding).</summary>
        public double HeldMs(double nowMs) => _holding ? Math.Max(0, nowMs - _since) : 0;

        /// <summary>One sample while dragging. Returns true only on the sample that arms it.</summary>
        public bool Update(bool inMiddle, int x, int y, double nowMs)
        {
            if (Armed) return false;
            if (!inMiddle) { _holding = false; return false; }
            if (!_holding || Math.Abs(x - _ax) > _tolerance || Math.Abs(y - _ay) > _tolerance)
            {
                _holding = true;
                _ax = x; _ay = y;
                _since = nowMs;
                return false;
            }
            if (nowMs - _since >= HoldMs) { Armed = true; return true; }
            return false;
        }

        public void Reset() { Armed = false; _holding = false; }
    }

    /// <summary>Settings → Snooze: how long a snooze lasts. 19 minutes by default; 1 hour, 2 hours, or any number of minutes.</summary>
    public static class SnoozeDuration
    {
        public const int DefaultMinutes = 19;
        public const int MinMinutes = 1;
        public const int MaxMinutes = 24 * 60;
        public static readonly int[] Presets = { 19, 60, 120 };

        public static int Clamp(int minutes) => Math.Max(MinMinutes, Math.Min(MaxMinutes, minutes));

        /// <summary>Reads the saved value ("19"); anything missing or broken is the default.</summary>
        public static int Parse(string text)
        {
            if (int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0) return Clamp(m);
            return DefaultMinutes;
        }

        public static string Serialize(int minutes) => Clamp(minutes).ToString(CultureInfo.InvariantCulture);

        public static bool IsPreset(int minutes) => Array.IndexOf(Presets, minutes) >= 0;

        /// <summary>"19 minutes", "1 hour", "2 hours", "1 hour 30 minutes".</summary>
        public static string Label(int minutes)
        {
            minutes = Clamp(minutes);
            int h = minutes / 60, m = minutes % 60;
            string hs = h == 1 ? "1 hour" : h + " hours";
            string ms = m == 1 ? "1 minute" : m + " minutes";
            if (h == 0) return ms;
            return m == 0 ? hs : hs + " " + ms;
        }
    }

    /// <summary>When a snooze ends, and how much is left ("18 min left").</summary>
    public struct SnoozeClock
    {
        public DateTime UntilUtc;

        public static SnoozeClock Start(DateTime nowUtc, int minutes) => new SnoozeClock { UntilUtc = nowUtc.AddMinutes(SnoozeDuration.Clamp(minutes)) };

        public bool Active(DateTime nowUtc) => UntilUtc > nowUtc;

        /// <summary>The snooze is over and the ring should come back (also true after the laptop slept past the time).</summary>
        public bool Due(DateTime nowUtc) => UntilUtc != default(DateTime) && UntilUtc <= nowUtc;

        public TimeSpan Remaining(DateTime nowUtc) => UntilUtc > nowUtc ? UntilUtc - nowUtc : TimeSpan.Zero;

        /// <summary>"1 h 5 min left", "18 min left", "less than a minute left".</summary>
        public static string Left(TimeSpan t)
        {
            int min = (int)Math.Ceiling(t.TotalMinutes - 1e-9);
            if (t <= TimeSpan.Zero) return "0 min left";
            if (t.TotalSeconds < 60) return "less than a minute left";
            int h = min / 60, m = min % 60;
            if (h == 0) return m + " min left";
            return m == 0 ? h + " h left" : h + " h " + m + " min left";
        }

        public static string Serialize(DateTime untilUtc) => untilUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

        public static SnoozeClock Parse(string text)
        {
            if (long.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                return new SnoozeClock { UntilUtc = new DateTime(ticks, DateTimeKind.Utc) };
            return new SnoozeClock();
        }
    }
}
