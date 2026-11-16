using System;
using System.IO;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Hold-to-snooze (v17): the ring is hidden until <see cref="UntilUtc"/>, then comes back by itself.
    /// The duration (Settings → Snooze, 19 minutes by default) and the end time are saved in %AppData%\Rapcha, so a
    /// restart while snoozed (e.g. autostart after a reboot) stays snoozed until the time is up.
    /// </summary>
    public static class Snooze
    {
        private static string MinutesPath => Path.Combine(AppStorage.Folder, "snooze-minutes.txt");
        private static string UntilPath => Path.Combine(AppStorage.Folder, "snooze-until.txt");
        private static int? _minutes;
        private static SnoozeClock _clock;
        private static bool _loaded;

        /// <summary>Snoozed or woken (UI thread).</summary>
        public static event Action Changed;
        public static event Action DurationChanged;

        public static int Minutes
        {
            get
            {
                if (_minutes.HasValue) return _minutes.Value;
                int m = SnoozeDuration.DefaultMinutes;
                try { if (File.Exists(MinutesPath)) m = SnoozeDuration.Parse(File.ReadAllText(MinutesPath)); } catch { }
                _minutes = m;
                return m;
            }
            set
            {
                int m = SnoozeDuration.Clamp(value);
                _minutes = m;
                try { File.WriteAllText(MinutesPath, SnoozeDuration.Serialize(m)); } catch { }
                AppStorage.Log("Settings: snooze duration " + m + " min");
                DurationChanged?.Invoke();
            }
        }

        internal static void Reload() { _minutes = null; _loaded = false; }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try { _clock = File.Exists(UntilPath) ? SnoozeClock.Parse(File.ReadAllText(UntilPath)) : new SnoozeClock(); } catch { _clock = new SnoozeClock(); }
        }

        public static DateTime UntilUtc { get { Load(); return _clock.UntilUtc; } }
        public static bool Active { get { Load(); return _clock.Active(DateTime.UtcNow); } }
        public static TimeSpan Remaining { get { Load(); return _clock.Remaining(DateTime.UtcNow); } }
        public static string LeftText => SnoozeClock.Left(Remaining);
        /// <summary>The time is up but the ring hasn't been brought back yet.</summary>
        public static bool Due { get { Load(); return _clock.Due(DateTime.UtcNow); } }

        public static void Start(int minutes, string how)
        {
            _clock = SnoozeClock.Start(DateTime.UtcNow, minutes);
            _loaded = true;
            try { File.WriteAllText(UntilPath, SnoozeClock.Serialize(_clock.UntilUtc)); } catch { }
            AppStorage.Log("Snooze: on for " + SnoozeDuration.Clamp(minutes) + " min (" + how + ")");
            Changed?.Invoke();
        }

        /// <summary>Self-test: a snooze of a few seconds (to watch it come back by itself).</summary>
        internal static void StartFor(TimeSpan t, string how)
        {
            _clock = new SnoozeClock { UntilUtc = DateTime.UtcNow + t };
            _loaded = true;
            try { File.WriteAllText(UntilPath, SnoozeClock.Serialize(_clock.UntilUtc)); } catch { }
            AppStorage.Log("Snooze: on for " + (int)t.TotalSeconds + " s (" + how + ")");
            Changed?.Invoke();
        }

        /// <summary>Ends the snooze now (the time is up, "Wake Rapcha now", a shortcut, a new launch).</summary>
        public static void Wake(string why)
        {
            Load();
            if (_clock.UntilUtc == default(DateTime)) return;
            _clock = new SnoozeClock();
            try { if (File.Exists(UntilPath)) File.Delete(UntilPath); } catch { }
            AppStorage.Log("Snooze: off (" + why + ")");
            Changed?.Invoke();
        }
    }
}
