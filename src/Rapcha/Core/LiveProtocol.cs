using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>
    /// Gemini Live wire format, ported from the Android app
    /// (rapcha-lite: websocket/GeminiLiveWebSocketClient.kt and websocket/TranscriptAcceptance.kt).
    /// Field order matches what org.json produces on Android (insertion order).
    /// </summary>
    public static class LiveProtocol
    {
        public const string DefaultModel = "models/gemini-3.5-transcribe-live";

        public const string WsBaseUrl =
            "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

        /// <summary>Copied verbatim from GeminiLiveWebSocketClient.DICTATION_SYSTEM_PROMPT.</summary>
        public const string DictationSystemPrompt =
            "You are an ultra-accurate real-time voice dictation engine. The user is holding the phone and " +
            "speaking to it, often softly, quietly or in a near-whisper because other people are nearby. " +
            "Transcribe every word the user closest to the microphone says, even a single quiet word or a " +
            "short two-word phrase; never drop soft or short speech as background. " +
            "Only leave out speech that is clearly farther away than the user's voice, and non-speech sounds " +
            "such as music, clatter or traffic. Output only the transcribed text with proper capitalization " +
            "and punctuation. Do not reply conversationally, do not answer questions, and do not add " +
            "commentary; only output the verbatim transcription of what was said.";

        public const string AudioMimeType = "audio/pcm;rate=16000";

        /// <summary>FINAL_TRANSCRIPT_TIMEOUT_MS on Android.</summary>
        public const int FinalTranscriptTimeoutMs = 8000;

        /// <summary>PING_INTERVAL_SECONDS on Android (OkHttp ping interval).</summary>
        public const int PingIntervalSeconds = 45;

        /// <summary>Port of TranscriptAcceptance.vocabularyClause.</summary>
        public static string VocabularyClause(IEnumerable<string> terms)
        {
            var listed = (terms ?? Enumerable.Empty<string>())
                .Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).ToList();
            if (listed.Count == 0) return "";
            return " Use this vocabulary only to correct the spelling of words the speaker clearly says: " +
                   string.Join(", ", listed) +
                   ". Never insert a term that was not spoken. If there is no clear speech, output nothing.";
        }

        public static string SystemPromptText(IList<string> customVocabulary)
        {
            return DictationSystemPrompt + VocabularyClause(customVocabulary);
        }

        /// <summary>Port of GeminiLiveWebSocketClient.buildSetupMessage.</summary>
        public static string BuildSetupMessage(string model, bool smartMode, IList<string> customVocabulary)
        {
            customVocabulary = customVocabulary ?? new List<string>();
            var transcription = new List<string>();
            if (smartMode) transcription.Add("\"mode\":\"SMART\"");
            if (customVocabulary.Count > 0)
            {
                var terms = customVocabulary.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0)
                    .Select(MiniJson.Quote);
                transcription.Add("\"customVocabulary\":[" + string.Join(",", terms) + "]");
            }

            var sb = new StringBuilder();
            sb.Append("{\"setup\":{");
            sb.Append("\"model\":").Append(MiniJson.Quote(model));
            sb.Append(",\"systemInstruction\":{\"parts\":[{\"text\":")
              .Append(MiniJson.Quote(SystemPromptText(customVocabulary))).Append("}]}");
            sb.Append(",\"generationConfig\":{\"responseModalities\":[\"TEXT\"]}");
            sb.Append(",\"inputAudioTranscription\":{").Append(string.Join(",", transcription)).Append("}");
            sb.Append(",\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}}");
            sb.Append("}}");
            return sb.ToString();
        }

        public static string BuildAudioMessage(byte[] pcm, int offset, int count)
        {
            var b64 = Convert.ToBase64String(pcm, offset, count);
            return "{\"realtimeInput\":{\"audio\":{\"mimeType\":\"" + AudioMimeType + "\",\"data\":\"" + b64 + "\"}}}";
        }

        public const string ActivityStartMessage = "{\"realtimeInput\":{\"activityStart\":{}}}";
        public const string ActivityEndMessage = "{\"realtimeInput\":{\"activityEnd\":{}}}";

        /// <summary>Port of GeminiLiveWebSocketClient.isNonRetryableApiKeyError.</summary>
        public static bool IsApiKeyError(int code, string statusOrMessage)
        {
            if (code == 401 || code == 403) return true;
            var lower = (statusOrMessage ?? "").ToLowerInvariant();
            return lower.Contains("api key") || lower.Contains("apikey") || lower.Contains("api_key_invalid") ||
                   lower.Contains("invalid_api_key") || lower.Contains("expired") || lower.Contains("unauthenticated") ||
                   lower.Contains("permission_denied") || lower.Contains("permission denied");
        }

        public static ServerMessage Parse(string rawJson)
        {
            var root = MiniJson.Parse(rawJson);
            var msg = new ServerMessage();
            if (MiniJson.Has(root, "setupComplete")) { msg.SetupComplete = true; return msg; }
            msg.GoAway = MiniJson.Obj(root, "goAway") != null;
            if (MiniJson.Has(root, "error"))
            {
                var err = MiniJson.Obj(root, "error");
                msg.HasError = true;
                msg.ErrorCode = MiniJson.Int(err, "code");
                msg.ErrorStatus = MiniJson.Str(err, "status");
                msg.ErrorMessage = err != null ? MiniJson.Str(err, "message") : MiniJson.Str(root, "error");
                return msg;
            }
            var sc = MiniJson.Obj(root, "serverContent");
            if (sc == null) return msg;
            msg.HasServerContent = true;
            var modelTurn = MiniJson.Obj(sc, "modelTurn");
            if (modelTurn != null && modelTurn.TryGetValue("parts", out var partsObj) && partsObj is List<object> parts)
            {
                var sb = new StringBuilder();
                foreach (var p in parts) sb.Append(MiniJson.Str(p, "text"));
                msg.ModelText = sb.ToString();
            }
            msg.InterimText = MiniJson.Str(MiniJson.Obj(sc, "interimInputTranscription"), "text");
            msg.InputTranscriptionText = MiniJson.Str(MiniJson.Obj(sc, "inputTranscription"), "text");
            msg.OutputTranscriptionText = MiniJson.Str(MiniJson.Obj(sc, "outputTranscription"), "text");
            msg.TurnComplete = MiniJson.Bool(sc, "turnComplete");
            msg.Interrupted = MiniJson.Bool(sc, "interrupted");
            return msg;
        }
    }

    public sealed class ServerMessage
    {
        public bool SetupComplete;
        public bool GoAway;
        public bool HasError;
        public int ErrorCode;
        public string ErrorStatus = "";
        public string ErrorMessage = "";
        public bool HasServerContent;
        public string ModelText = "";
        public string InterimText = "";
        public string InputTranscriptionText = "";
        public string OutputTranscriptionText = "";
        public bool TurnComplete;
        public bool Interrupted;

        public string ErrorDescription
        {
            get
            {
                var sb = new StringBuilder("Gemini Live server error");
                if (ErrorCode != 0) sb.Append(" (code ").Append(ErrorCode).Append(')');
                if (!string.IsNullOrWhiteSpace(ErrorStatus)) sb.Append(" [").Append(ErrorStatus).Append(']');
                sb.Append(": ").Append(ErrorMessage);
                return sb.ToString();
            }
        }
    }
}
