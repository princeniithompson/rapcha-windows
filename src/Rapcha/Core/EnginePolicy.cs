using System;
using System.Collections.Generic;

namespace Rapcha.Core
{
    /// <summary>Why no engine can take this dictation.</summary>
    public enum NoEngineReason { None, OfflineFallbackOff, NoRecognizer }

    public sealed class EngineInputs
    {
        public bool Online = true;            // false only when Rapcha knows there is no internet
        public bool OfflineFallback = true;   // Settings → Offline fallback
        public DeviceTier Tier = DeviceTier.Low;
        public bool WhisperModelReady;        // a model for this tier is downloaded and checked
        public bool WhisperBlocked;           // the engine couldn't load here (Windows S mode / code integrity) or isn't bundled
        public bool WhisperFitsMemory = true; // enough free memory right now for the model
        public bool BuiltInAvailable;         // a Windows speech recogniser is installed
    }

    public sealed class EngineChoice
    {
        public SpeechEngine Engine;
        public bool Ok;
        public NoEngineReason Why;
        public override string ToString() => Ok ? Engine.ToString() : "none(" + Why + ")";
    }

    /// <summary>
    /// Picks the recogniser: Gemini when online; offline (if Offline fallback is on) Whisper when a model is downloaded
    /// (on a Low laptop only if the user chose "Download lightest model anyway"), it can load here and fits in free memory, otherwise built-in Windows speech; nothing when there is no recogniser at all
    /// (then Rapcha offers "Get Windows speech").
    /// </summary>
    public static class EnginePolicy
    {
        public static EngineChoice Choose(EngineInputs i)
        {
            if (i.Online) return new EngineChoice { Ok = true, Engine = SpeechEngine.Gemini };
            return ChooseOffline(i);
        }

        public static EngineChoice ChooseOffline(EngineInputs i)
        {
            if (!i.OfflineFallback) return new EngineChoice { Ok = false, Why = NoEngineReason.OfflineFallbackOff };
            if (i.WhisperModelReady && !i.WhisperBlocked && i.WhisperFitsMemory)   // v17: also on a Low laptop, if the user downloaded it anyway
                return new EngineChoice { Ok = true, Engine = SpeechEngine.Whisper };
            if (i.BuiltInAvailable) return new EngineChoice { Ok = true, Engine = SpeechEngine.BuiltIn };
            return new EngineChoice { Ok = false, Why = NoEngineReason.NoRecognizer };
        }

        /// <summary>A Gemini failure part-way that the offline engine should take over (the internet went away).</summary>
        public static bool SwitchesOffline(LiveErrorKind kind) => kind == LiveErrorKind.NoInternet || kind == LiveErrorKind.ConnectionLost;

        /// <summary>The words Gemini had shown before the switch, then the offline words, as one text.</summary>
        public static string Join(string captured, string offline)
        {
            captured = (captured ?? "").Trim();
            offline = (offline ?? "").Trim();
            if (captured.Length == 0) return offline;
            if (offline.Length == 0) return captured;
            char last = captured[captured.Length - 1];
            if (char.IsLetterOrDigit(last)) captured += ".";
            if (offline.Length > 0 && char.IsLower(offline[0])) offline = char.ToUpperInvariant(offline[0]) + offline.Substring(1);
            return captured + " " + offline;
        }
    }

    /// <summary>
    /// Sends the microphone audio to Gemini and, if the internet goes away part-way, to an offline recogniser
    /// instead, keeping everything already captured: the words Gemini had shown, plus the audio since Gemini's last
    /// words (up to 30 s), which the offline recogniser hears first. Thread-safe: audio arrives on the recorder thread.
    /// </summary>
    public sealed class DictationRouter
    {
        public const int ReplayMaxBytes = 30 * 16000 * 2;

        private readonly object _lock = new object();
        private readonly Action<byte[]> _online;
        private readonly LinkedList<byte[]> _replay = new LinkedList<byte[]>();
        private int _replayBytes;
        private Action<byte[]> _offline;

        public bool IsOffline { get { lock (_lock) return _offline != null; } }
        public string Captured { get; private set; } = "";
        public int ReplayBytes { get { lock (_lock) return _replayBytes; } }
        public long OnlineBytes { get; private set; }
        public long OfflineBytes { get; private set; }

        /// <param name="online">Gemini's audio sink; null when the dictation starts offline.</param>
        public DictationRouter(Action<byte[]> online) { _online = online; }

        /// <summary>Starts offline right away (Rapcha already knew there was no internet).</summary>
        public static DictationRouter Offline(Action<byte[]> offline)
        {
            var r = new DictationRouter(null);
            r._offline = offline;
            return r;
        }

        public void Feed(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            Action<byte[]> target;
            lock (_lock)
            {
                if (_offline != null) { target = _offline; OfflineBytes += pcm.Length; }
                else
                {
                    target = _online;
                    OnlineBytes += pcm.Length;
                    _replay.AddLast(pcm);
                    _replayBytes += pcm.Length;
                    while (_replayBytes > ReplayMaxBytes && _replay.Count > 1) { _replayBytes -= _replay.First.Value.Length; _replay.RemoveFirst(); }
                }
            }
            try { target?.Invoke(pcm); } catch { }
        }

        /// <summary>Gemini showed words: they are kept, and the audio before them no longer needs replaying.</summary>
        public void OnOnlineWords(string text)
        {
            lock (_lock)
            {
                if (_offline != null) return;
                Captured = (text ?? "").Trim();
                _replay.Clear();
                _replayBytes = 0;
            }
        }

        /// <summary>
        /// Moves to the offline recogniser: replays the unconfirmed audio into it first, then sends it everything new.
        /// Returns false if it was already offline.
        /// </summary>
        public bool SwitchToOffline(Action<byte[]> offline, string capturedNow = null)
        {
            List<byte[]> replay;
            lock (_lock)
            {
                if (_offline != null || offline == null) return false;
                if (!string.IsNullOrWhiteSpace(capturedNow)) Captured = capturedNow.Trim();
                replay = new List<byte[]>(_replay);
                _replay.Clear();
                _replayBytes = 0;
                // Feed the replay while holding the lock so new audio can't overtake it.
                foreach (var b in replay) { try { offline(b); } catch { } OfflineBytes += b.Length; }
                _offline = offline;
            }
            return true;
        }
    }
}
