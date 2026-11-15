using System.Collections.Generic;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>
    /// The single-piece transcript logic from GeminiLiveWebSocketClient.kt
    /// (handleIncomingMessage, noteLiveTranscription, notifyInterimTranscription, settleEndpoint)
    /// plus DictationPiecePlanner's per-piece RMS stats. Pure, so it can be unit tested.
    /// Phase 1 keeps one piece per session (no 2:30 piece splitting).
    /// </summary>
    public sealed class LiveTurnState
    {
        private readonly StringBuilder _activeTurnText = new StringBuilder();
        private double _rmsMax;
        private double _rmsSum;
        private int _rmsCount;

        public string LastInterimText { get; private set; } = "";
        public bool ActivityEnded { get; private set; }
        public bool Settled { get; private set; }
        public bool TimedOut { get; private set; }
        public string CommittedText { get; private set; } = "";

        public double RmsMean => _rmsCount == 0 ? 0.0 : _rmsSum / _rmsCount;
        public double RmsMax => _rmsCount == 0 ? 0.0 : _rmsMax;
        public int RmsSamples => _rmsCount;
        public bool IsNearSilent => TranscriptAcceptance.IsNearSilent(RmsMean, RmsMax, RmsSamples);

        /// <summary>Called for every raw (pre-gain) chunk that is sent, like planner.onAudio.</summary>
        public void OnAudioSent(byte[] rawPcm)
        {
            double rms = SpeechGain.Rms(rawPcm, rawPcm.Length - (rawPcm.Length % 2));
            _rmsCount++;
            _rmsSum += rms;
            if (rms > _rmsMax) _rmsMax = rms;
        }

        public void MarkActivityEnded() { ActivityEnded = true; }

        /// <summary>
        /// Handles one serverContent frame. Returns the completed text candidate when the
        /// authoritative transcript arrives after activityEnd, otherwise null.
        /// </summary>
        public string OnServerContent(ServerMessage m)
        {
            if (!string.IsNullOrEmpty(m.ModelText))
            {
                _activeTurnText.Append(m.ModelText);
                var currentTurn = _activeTurnText.ToString();
                if (currentTurn.Length > 0) NotifyInterim(currentTurn);
            }

            if (!string.IsNullOrEmpty(m.InterimText)) NoteLiveTranscription(m.InterimText);

            var finalizedText = m.InputTranscriptionText ?? "";
            bool turnFinished = m.TurnComplete || m.Interrupted;

            if (!ActivityEnded)
            {
                if (finalizedText.Length > 0) NoteLiveTranscription(finalizedText);
                if (turnFinished) _activeTurnText.Clear();
                return null;
            }
            if (finalizedText.Length > 0 || turnFinished)
            {
                var completed = finalizedText.Length > 0 ? finalizedText : _activeTurnText.ToString().Trim();
                _activeTurnText.Clear();
                return completed.Trim().Length == 0 ? LastInterimText : completed;
            }
            return null;
        }

        /// <summary>Port of settleEndpoint for a single piece. Returns the accepted text.</summary>
        public string Settle(string text, bool timedOut, IList<string> vocabulary)
        {
            if (Settled) return CommittedText;
            Settled = true;
            TimedOut = timedOut;
            var raw = (text ?? "").Trim();
            if (raw.Length == 0) raw = LastInterimText.Trim();
            var piece = TranscriptAcceptance.Accept(raw, RmsMean, RmsMax, vocabulary, RmsSamples);
            if (piece.Length > 0)
            {
                CommittedText = TranscriptSeam.Append(CommittedText, piece);
                LastInterimText = "";
            }
            return CommittedText;
        }

        private void NoteLiveTranscription(string text)
        {
            if (IsNearSilent) return;
            NotifyInterim(text);
        }

        private void NotifyInterim(string text)
        {
            if (!string.IsNullOrEmpty(text)) LastInterimText = text;
        }
    }

    /// <summary>Port of TranscriptSeam.append.</summary>
    public static class TranscriptSeam
    {
        public static string Append(string existing, string piece)
        {
            var next = (piece ?? "").Trim();
            if (next.Length == 0) return (existing ?? "").Trim();
            var previous = (existing ?? "").Trim();
            if (previous.Length == 0) return next;
            return previous + " " + next;
        }
    }
}
