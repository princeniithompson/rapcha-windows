using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Plain-English messages shown to the user.</summary>
    public static class Messages
    {
        public const string NoKey = "Add your Gemini API key in Settings first.";
        public const string BadKey = "Your Gemini API key was not accepted. Check it in Settings.";
        public const string NoInternet = "No internet connection. Check your Wi-Fi and try again.";
        public const string NoMic = "No microphone found. Plug one in or turn it on, then try again.";
        public const string MicSteps = "Turn on Microphone access and Let desktop apps access your microphone, then try again.";
        public const string MicPrivacy = "Windows is blocking the microphone. " + MicSteps;
        public const string MicInUse = "The microphone is being used by another app. Close it and try again.";
        public const string MicOther = "Rapcha could not open the microphone. " + MicSteps;
        public const string ConnectionLost = "Connection to Gemini was lost. Please try again.";
        public const string NothingHeard = "Didn't catch that. Try again.";
        public const string NoSound = "No sound came from the microphone. " + MicSteps;
        public const string TooQuiet = "I couldn't hear you. Speak closer to the microphone, or choose another one in Settings.";
        public const string GeminiNothing = "Gemini returned nothing for your speech. Try again and speak a little longer.";
        public const string GeminiSlow = "Gemini didn't answer in time. Check your internet and try again.";
        public const string TimeLimit = "Time limit reached, so Rapcha stopped. Your words are kept.";
        public const string LowMemory = "Stopped early because your laptop is low on memory. Your words are kept.";
        public const string OfflineOff = "No internet connection. Turn on Offline fallback in Settings to keep typing without internet.";
        public const string OfflineNoSpeech = "You're offline and this laptop has no Windows speech yet. In Settings, press Get Windows speech.";

        public static string For(LiveError e)
        {
            switch (e.Kind)
            {
                case LiveErrorKind.MissingKey: return NoKey;
                case LiveErrorKind.BadKey: return BadKey;
                case LiveErrorKind.NoInternet: return NoInternet;
                case LiveErrorKind.ConnectionLost: return ConnectionLost;
                default:
                    var d = (e.Detail ?? "").Trim();
                    if (d.Length > 140) d = d.Substring(0, 140) + "…";
                    return "Gemini had a problem: " + d;
            }
        }
    }
}
