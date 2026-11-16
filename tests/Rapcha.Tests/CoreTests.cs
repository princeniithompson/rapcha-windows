using System;
using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class EdgeSnapTests
    {
        private static readonly IntRect Work = new IntRect(0, 0, 1920, 1040);
        private const int Ring = 56;

        [Theory]
        [InlineData(10, 500, DockEdge.Left)]
        [InlineData(1900, 500, DockEdge.Right)]
        [InlineData(900, 5, DockEdge.Top)]
        [InlineData(900, 1030, DockEdge.Bottom)]
        [InlineData(960, 520, DockEdge.Top)]      // dead centre still goes to an edge
        [InlineData(-300, 400, DockEdge.Left)]    // off screen is clamped
        [InlineData(3000, 400, DockEdge.Right)]
        public void SnapsToNearestEdge(int cx, int cy, DockEdge expected)
        {
            Assert.Equal(expected, EdgeSnap.Snap(Work, cx, cy, Ring).Edge);
        }

        [Fact]
        public void DropPositionAlongTheEdgeIsKept()
        {
            var pos = EdgeSnap.Snap(Work, 1500, 1030, Ring);            // dropped near the bottom, right of centre
            Assert.Equal(DockEdge.Bottom, pos.Edge);
            EdgeSnap.Place(Work, pos, Ring, 6, out int x, out int y);
            Assert.Equal(1040 - Ring - 6, y);
            Assert.InRange(x, 1500 - Ring / 2 - 1, 1500 - Ring / 2 + 1);
        }

        [Fact]
        public void PlacedRingAlwaysTouchesAnEdgeNeverTheMiddle()
        {
            var rnd = new Random(7);
            for (int i = 0; i < 2000; i++)
            {
                int cx = rnd.Next(-200, 2200), cy = rnd.Next(-200, 1300);
                var pos = EdgeSnap.Snap(Work, cx, cy, Ring);
                EdgeSnap.Place(Work, pos, Ring, 6, out int x, out int y);
                Assert.InRange(x, Work.X, Work.Right - Ring);
                Assert.InRange(y, Work.Y, Work.Bottom - Ring);
                bool onEdge = x == 6 || y == 6 || x == Work.Right - Ring - 6 || y == Work.Bottom - Ring - 6;
                Assert.True(onEdge, $"ring at {x},{y} is not on an edge");
            }
        }

        [Fact]
        public void KeepsPositionAlongTheEdge()
        {
            var pos = EdgeSnap.Snap(Work, 1910, 300, Ring);
            Assert.Equal(DockEdge.Right, pos.Edge);
            EdgeSnap.Place(Work, pos, Ring, 6, out int x, out int y);
            Assert.Equal(1920 - 56 - 6, x);
            Assert.InRange(y + Ring / 2, 298, 302);
        }

        [Fact]
        public void WorksOnASecondMonitorWithOffset()
        {
            var work2 = new IntRect(-1280, 200, 1280, 984);
            var pos = EdgeSnap.Snap(work2, -1270, 700, Ring);
            Assert.Equal(DockEdge.Left, pos.Edge);
            EdgeSnap.Place(work2, pos, Ring, 6, out int x, out int y);
            Assert.Equal(-1274, x);
            Assert.InRange(y, 200, 200 + 984 - Ring);
        }

        [Fact]
        public void FractionSurvivesResolutionChange()
        {
            var pos = new DockPosition(DockEdge.Left, 0.5);
            EdgeSnap.Place(new IntRect(0, 0, 1366, 728), pos, Ring, 6, out _, out int y);
            Assert.Equal((728 - 56) / 2, y);
        }
    }

    public class ProtocolTests
    {
        // Expected strings were produced from the Android logic in
        // GeminiLiveWebSocketClient.buildSetupMessage (org.json keeps insertion order).
        [Fact]
        public void SetupMessageMatchesAndroidSmartModeWithVocabulary()
        {
            var json = LiveProtocol.BuildSetupMessage(LiveProtocol.DefaultModel, true, new List<string> { "Prince Nii", " Rapcha " });
            var expected =
                "{\"setup\":{\"model\":\"models/gemini-3.5-transcribe-live\"," +
                "\"systemInstruction\":{\"parts\":[{\"text\":\"" + LiveProtocol.DictationSystemPrompt +
                " Use this vocabulary only to correct the spelling of words the speaker clearly says: Prince Nii, Rapcha. Never insert a term that was not spoken. If there is no clear speech, output nothing.\"}]}," +
                "\"generationConfig\":{\"responseModalities\":[\"TEXT\"]}," +
                "\"inputAudioTranscription\":{\"mode\":\"SMART\",\"customVocabulary\":[\"Prince Nii\",\"Rapcha\"]}," +
                "\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}}}}";
            Assert.Equal(expected, json);
            Assert.NotNull(MiniJson.Parse(json));
        }

        [Fact]
        public void SetupMessageWithoutVocabularyHasNoClauseOrArray()
        {
            var json = LiveProtocol.BuildSetupMessage(LiveProtocol.DefaultModel, true, new List<string>());
            Assert.Contains("\"inputAudioTranscription\":{\"mode\":\"SMART\"}", json);
            Assert.DoesNotContain("customVocabulary", json);
            Assert.DoesNotContain("Use this vocabulary", json);
            var root = MiniJson.Parse(json);
            var text = ((List<object>)MiniJson.Obj(MiniJson.Obj(root, "setup"), "systemInstruction")["parts"])
                .Select(p => MiniJson.Str(p, "text")).Single();
            Assert.Equal(LiveProtocol.DictationSystemPrompt, text);
        }

        [Fact]
        public void PromptIsVerbatimFromAndroid()
        {
            Assert.StartsWith("You are an ultra-accurate real-time voice dictation engine. The user is holding the phone and speaking to it", LiveProtocol.DictationSystemPrompt);
            Assert.EndsWith("only output the verbatim transcription of what was said.", LiveProtocol.DictationSystemPrompt);
            Assert.Equal("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent", LiveProtocol.WsBaseUrl);
        }

        [Fact]
        public void SpecialCharactersAreEscaped()
        {
            var json = LiveProtocol.BuildSetupMessage("m", true, new List<string> { "Ama \"Quote\"", "back\\slash" });
            var root = MiniJson.Parse(json);
            var vocab = (List<object>)MiniJson.Obj(MiniJson.Obj(root, "setup"), "inputAudioTranscription")["customVocabulary"];
            Assert.Equal(new object[] { "Ama \"Quote\"", "back\\slash" }, vocab.ToArray());
        }

        [Fact]
        public void AudioAndActivityMessages()
        {
            var pcm = new byte[] { 1, 2, 3, 4 };
            Assert.Equal("{\"realtimeInput\":{\"audio\":{\"mimeType\":\"audio/pcm;rate=16000\",\"data\":\"AQIDBA==\"}}}",
                LiveProtocol.BuildAudioMessage(pcm, 0, pcm.Length));
            Assert.Equal("{\"realtimeInput\":{\"activityStart\":{}}}", LiveProtocol.ActivityStartMessage);
            Assert.Equal("{\"realtimeInput\":{\"activityEnd\":{}}}", LiveProtocol.ActivityEndMessage);
        }

        [Fact]
        public void ParsesServerFrames()
        {
            Assert.True(LiveProtocol.Parse("{\"setupComplete\":{}}").SetupComplete);
            var m = LiveProtocol.Parse("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hello there.\"},\"turnComplete\":true}}");
            Assert.True(m.HasServerContent);
            Assert.Equal("Hello there.", m.InputTranscriptionText);
            Assert.True(m.TurnComplete);
            var e = LiveProtocol.Parse("{\"error\":{\"code\":400,\"status\":\"INVALID_ARGUMENT\",\"message\":\"API key not valid. Please pass a valid API key.\"}}");
            Assert.True(e.HasError);
            Assert.True(LiveProtocol.IsApiKeyError(e.ErrorCode, e.ErrorStatus + " " + e.ErrorMessage));
            var t = LiveProtocol.Parse("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"text\":\"Hi \"},{\"text\":\"you\"}]}}}");
            Assert.Equal("Hi you", t.ModelText);
            Assert.Equal("\u00e9\n", MiniJson.Str(MiniJson.Parse("{\"a\":\"\\u00e9\\n\"}"), "a"));
        }
    }

    public class VocabularyTests
    {
        [Fact]
        public void ClauseMatchesAndroid()
        {
            Assert.Equal("", LiveProtocol.VocabularyClause(new[] { " ", "" }));
            Assert.Equal(" Use this vocabulary only to correct the spelling of words the speaker clearly says: Prince Nii. Never insert a term that was not spoken. If there is no clear speech, output nothing.",
                LiveProtocol.VocabularyClause(new[] { " Prince Nii " }));
        }

        [Fact]
        public void AddRejectsBlanksAndCaseInsensitiveDuplicatesNewestFirst()
        {
            var list = new List<string>();
            Assert.True(VocabularyList.TryAdd(list, "Prince Nii"));
            Assert.False(VocabularyList.TryAdd(list, "prince nii"));
            Assert.False(VocabularyList.TryAdd(list, "   "));
            Assert.True(VocabularyList.TryAdd(list, " Rapcha "));
            Assert.Equal(new[] { "Rapcha", "Prince Nii" }, list);
            Assert.Equal(list, VocabularyList.FromText(VocabularyList.ToText(list)));
        }
    }

    public class TranscriptTests
    {
        private static byte[] Tone(int amplitude, int bytes = 3200)
        {
            var b = new byte[bytes];
            for (int i = 0; i < bytes / 2; i++)
            {
                short s = (short)(amplitude * Math.Sin(i * 2 * Math.PI * 440 / 16000));
                b[i * 2] = (byte)(s & 0xFF);
                b[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return b;
        }

        private static ServerMessage Content(string input = "", string interim = "", string model = "", bool turnComplete = false)
            => new ServerMessage { HasServerContent = true, InputTranscriptionText = input, InterimText = interim, ModelText = model, TurnComplete = turnComplete };

        [Fact]
        public void FinalInputTranscriptionAfterActivityEndWins()
        {
            var st = new LiveTurnState();
            st.OnAudioSent(Tone(4000));
            Assert.Null(st.OnServerContent(Content(interim: "hello wor")));
            Assert.Null(st.OnServerContent(Content(input: "Hello wor")));  // preview only before activityEnd
            Assert.Equal("Hello wor", st.LastInterimText);
            st.MarkActivityEnded();
            var completed = st.OnServerContent(Content(input: "Hello world."));
            Assert.Equal("Hello world.", completed);
            Assert.Equal("Hello world.", st.Settle(completed, false, new List<string>()));
        }

        [Fact]
        public void TurnCompleteWithoutTranscriptionFallsBackToModelTextThenInterim()
        {
            var st = new LiveTurnState();
            st.OnAudioSent(Tone(4000));
            st.MarkActivityEnded();
            st.OnServerContent(Content(model: "Typed by model."));
            Assert.Equal("Typed by model.", st.OnServerContent(Content(turnComplete: true)));

            var st2 = new LiveTurnState();
            st2.OnAudioSent(Tone(4000));
            st2.OnServerContent(Content(interim: "only interim"));
            st2.MarkActivityEnded();
            Assert.Equal("only interim", st2.OnServerContent(Content(turnComplete: true)));
        }

        [Fact]
        public void TimeoutUsesLastInterim()
        {
            var st = new LiveTurnState();
            st.OnAudioSent(Tone(4000));
            st.OnServerContent(Content(interim: "almost there"));
            st.MarkActivityEnded();
            Assert.Equal("almost there", st.Settle(st.LastInterimText, true, new List<string>()));
            Assert.True(st.TimedOut);
        }

        [Fact]
        public void NearSilentAudioIsDropped()
        {
            var st = new LiveTurnState();
            for (int i = 0; i < 10; i++) st.OnAudioSent(Tone(100));
            Assert.True(st.IsNearSilent);
            Assert.Null(st.OnServerContent(Content(interim: "Prince Nii")));
            Assert.Equal("", st.LastInterimText);
            st.MarkActivityEnded();
            Assert.Equal("", st.Settle("Prince Nii", false, new List<string> { "Prince Nii" }));
        }

        [Fact]
        public void QuietVocabularyOnlyHallucinationIsDropped()
        {
            var vocab = new List<string> { "Prince Nii" };
            Assert.Equal("", TranscriptAcceptance.Accept("Prince Nii.", 300, 600, vocab));
            Assert.Equal("Call Prince Nii.", TranscriptAcceptance.Accept("Call Prince Nii.", 300, 600, vocab));
            Assert.Equal("Prince Nii.", TranscriptAcceptance.Accept("Prince Nii.", 2000, 5000, vocab));
        }

        [Fact]
        public void SpeechGainLiftsQuietSpeechButNeverClips()
        {
            var g = new SpeechGain();
            g.Process(Tone(50));               // room noise first, like a real recording
            var quiet = Tone(600);
            g.Process(quiet);
            var outQuiet = g.Process(quiet);
            Assert.True(SpeechGain.Rms(outQuiet, outQuiet.Length) > SpeechGain.Rms(quiet, quiet.Length) * 1.5);
            var loud = Tone(32000);
            var outLoud = new SpeechGain().Process(loud);
            Assert.True(SpeechGain.Peak(outLoud, outLoud.Length) <= 32767);
            Assert.Same(loud, outLoud); // loud speech is left alone
        }

        [Fact]
        public void RechunkerMakes100msPacketsAndFlushesTail()
        {
            var packets = new List<byte[]>();
            var r = new PcmRechunker(p => packets.Add(p));
            var data = new byte[1600];
            for (int i = 0; i < 5; i++) r.Write(data, 0, data.Length);   // 8000 bytes
            Assert.Equal(2, packets.Count);
            Assert.All(packets, p => Assert.Equal(3200, p.Length));
            r.FlushTail();
            Assert.Equal(3, packets.Count);
            Assert.Equal(1600, packets[2].Length);
            r.Write(new byte[100], 0, 100);
            r.FlushTail();                    // < 320 bytes is dropped, like AudioRecorder.tailPacket
            Assert.Equal(3, packets.Count);
        }

        [Fact]
        public void SeamJoinsPieces()
        {
            Assert.Equal("a b", TranscriptSeam.Append(" a ", " b "));
            Assert.Equal("a", TranscriptSeam.Append("a", "  "));
        }
    }
}
