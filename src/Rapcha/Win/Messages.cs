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
        public const string MicPrivacy = "Windows is blocking the microphone. Turn on Settings > Privacy > Microphone > Let desktop apps access your microphone.";
        public const string MicInUse = "The microphone is being used by another app. Close it and try again.";
        public const string MicOther = "Rapcha could not open the microphone. Check it in Windows sound settings.";
        public const string ConnectionLost = "Connection to Gemini was lost. Please try again.";
        public const string NothingHeard = "Didn't catch that. Try again.";

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
