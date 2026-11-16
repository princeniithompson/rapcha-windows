using System;
using System.Globalization;

namespace Rapcha.Core
{
    /// <summary>Which recogniser turns the voice into text for one dictation.</summary>
    public enum SpeechEngine { Gemini, BuiltIn, Whisper }

    /// <summary>
    /// How long one dictation may run, so memory stays flat and the laptop never struggles.
    /// Online (Gemini Live): Google's Live API docs say an audio-only session is limited to 15 minutes and one
    /// connection lasts "around 10 minutes" (https://ai.google.dev/gemini-api/docs/live-session). Rapcha keeps one
    /// connection per dictation and still needs it for the polished text after you stop, so it stops listening at
    /// 9:30 and leaves room to finish inside the 10 minutes. Offline: built-in Windows speech 5 min; Whisper 2 min
    /// on a Low (downloaded anyway) or Medium laptop, 5 min on a Strong one.
    /// </summary>
    public static class SessionCaps
    {
        public const int GeminiSeconds = 9 * 60 + 30;
        public const int BuiltInSeconds = 5 * 60;
        public const int WhisperMediumSeconds = 2 * 60;
        /// <summary>v17: a Low laptop running Whisper Tiny anyway: 2 minutes, plus the free-memory guard.</summary>
        public const int WhisperLowSeconds = 2 * 60;
        public const int WhisperStrongSeconds = 5 * 60;
        public const int CountdownSeconds = 20;
        /// <summary>Offline engines stop (keeping the words) when free memory falls below this.</summary>
        public const long MinFreeMbBuiltIn = 200;
        public const long MinFreeMbWhisper = 300;
        /// <summary>Whisper turns the audio into text about every 30 seconds, so only one piece of audio is held.</summary>
        public const int ChunkSeconds = 30;

        public static int MaxSeconds(SpeechEngine engine, DeviceTier tier)
        {
            switch (engine)
            {
                case SpeechEngine.Gemini: return GeminiSeconds;
                case SpeechEngine.BuiltIn: return BuiltInSeconds;
                default: return tier == DeviceTier.Strong ? WhisperStrongSeconds : tier == DeviceTier.Low ? WhisperLowSeconds : WhisperMediumSeconds;
            }
        }

        public static long MinFreeMb(SpeechEngine engine) =>
            engine == SpeechEngine.Whisper ? MinFreeMbWhisper : engine == SpeechEngine.BuiltIn ? MinFreeMbBuiltIn : 0;

        /// <summary>
        /// The limit after switching engines part-way (Gemini lost the internet at 6:00 and built-in speech takes
        /// over): the new engine's limit, but never less than the countdown from now, so it never stops abruptly.
        /// </summary>
        public static int AfterSwitch(int elapsedSeconds, int newMaxSeconds) => Math.Max(newMaxSeconds, elapsedSeconds + CountdownSeconds);

        /// <summary>Should the dictation stop now? (time used up, or free memory too low for an offline engine)</summary>
        public static bool ShouldStop(double elapsedSeconds, int maxSeconds, SpeechEngine engine, long freeRamMb, out string reason)
        {
            reason = "";
            if (elapsedSeconds >= maxSeconds) { reason = "time_limit"; return true; }
            long min = MinFreeMb(engine);
            if (min > 0 && freeRamMb >= 0 && freeRamMb < min) { reason = "low_memory"; return true; }
            return false;
        }
    }

    /// <summary>The small recording timer in the Words box: m:ss going up, then a calm countdown for the last 20 s.</summary>
    public struct TimerText
    {
        public string Text;
        public bool Countdown;

        public static string Clock(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static TimerText For(double elapsedSeconds, int maxSeconds)
        {
            int elapsed = (int)Math.Floor(Math.Max(0, elapsedSeconds));
            int left = (int)Math.Ceiling(Math.Max(0, maxSeconds - elapsedSeconds));
            if (maxSeconds > 0 && left <= SessionCaps.CountdownSeconds)
                return new TimerText { Text = Clock(left) + " left", Countdown = true };
            return new TimerText { Text = Clock(elapsed), Countdown = false };
        }
    }
}
