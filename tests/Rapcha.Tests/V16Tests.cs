using System;
using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class V16TierTests
    {
        [Theory]
        [InlineData(4096, DeviceTier.Low)]
        [InlineData(3994, DeviceTier.Low)]       // "4 GB" laptop that reports a bit less
        [InlineData(5000, DeviceTier.Low)]
        [InlineData(6144, DeviceTier.Medium)]
        [InlineData(6000, DeviceTier.Medium)]    // 5.9 GB rounds to 6
        [InlineData(7987, DeviceTier.Medium)]    // "8 GB" with memory kept for graphics
        [InlineData(8192, DeviceTier.Medium)]
        [InlineData(12288, DeviceTier.Strong)]
        [InlineData(16384, DeviceTier.Strong)]
        public void TierFollowsInstalledMemory(long ramMb, DeviceTier want)
        {
            Assert.Equal(want, DeviceTiers.Classify(new DeviceFacts { TotalRamMb = ramMb, FreeRamMb = 1000, Cores = 4, FreeDiskMb = 10000 }));
        }

        [Fact]
        public void FakeDeviceTextIsReadForTests()
        {
            var f = DeviceFacts.Parse("ram=4096,free=1200,cores=2,disk=9000,avx2=0");
            Assert.Equal(4096, f.TotalRamMb); Assert.Equal(1200, f.FreeRamMb); Assert.Equal(2, f.Cores); Assert.Equal(9000, f.FreeDiskMb);
            Assert.False(f.Avx2);
            Assert.Equal(DeviceTier.Low, DeviceTiers.Classify(f));
            Assert.Equal(8192, DeviceFacts.Parse("cores=8").TotalRamMb);
            Assert.Equal(4096, DeviceFacts.Parse("ram=8192").FreeRamMb);
            Assert.Null(DeviceFacts.Parse("ram=lots"));
            Assert.Null(DeviceFacts.Parse(""));
            Assert.Equal("4 GB memory, 2 cores, 8 GB free on the disk", DeviceTiers.Describe(f));
        }

        [Fact]
        public void ModelsPerTier()
        {
            Assert.Empty(WhisperModels.ForTier(DeviceTier.Low));
            Assert.Null(WhisperModels.Recommended(DeviceTier.Low));
            Assert.Equal("tiny.en", WhisperModels.Recommended(DeviceTier.Medium).Id);
            Assert.Equal("base.en", WhisperModels.Recommended(DeviceTier.Strong).Id);
            Assert.Equal(new[] { "base.en", "small.en" }, WhisperModels.ForTier(DeviceTier.Strong).Select(m => m.Id));
            Assert.Equal("small.en", WhisperModels.Choose(DeviceTier.Strong, "small.en").Id);
            Assert.Equal("tiny.en", WhisperModels.Choose(DeviceTier.Medium, "small.en").Id);   // not allowed on Medium
            foreach (var m in WhisperModels.All)
            {
                Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-", m.Url);
                Assert.Equal(64, m.Sha256.Length);
                Assert.True(m.Bytes > 30_000_000);
            }
            Assert.InRange(WhisperModels.Tiny.Mb, 70, 80);
            Assert.InRange(WhisperModels.Base.Mb, 135, 150);
            Assert.True(WhisperModels.FitsInFreeMemory(WhisperModels.Tiny, 700));
            Assert.False(WhisperModels.FitsInFreeMemory(WhisperModels.Base, 500));
            Assert.False(WhisperModels.FitsOnDisk(WhisperModels.Small, 600));
        }
    }

    public class V16CapTests
    {
        [Fact]
        public void LimitsPerEngineAndTier()
        {
            Assert.Equal(570, SessionCaps.MaxSeconds(SpeechEngine.Gemini, DeviceTier.Low));
            Assert.True(SessionCaps.GeminiSeconds < 600);   // inside the Live API's ~10 minute connection
            Assert.Equal(300, SessionCaps.MaxSeconds(SpeechEngine.BuiltIn, DeviceTier.Low));
            Assert.Equal(120, SessionCaps.MaxSeconds(SpeechEngine.Whisper, DeviceTier.Medium));
            Assert.Equal(300, SessionCaps.MaxSeconds(SpeechEngine.Whisper, DeviceTier.Strong));
        }

        [Fact]
        public void StopsAtTheLimitOrWhenMemoryIsLow()
        {
            Assert.False(SessionCaps.ShouldStop(119.5, 120, SpeechEngine.Whisper, 2000, out _));
            Assert.True(SessionCaps.ShouldStop(120, 120, SpeechEngine.Whisper, 2000, out var r1)); Assert.Equal("time_limit", r1);
            Assert.True(SessionCaps.ShouldStop(10, 120, SpeechEngine.Whisper, 250, out var r2)); Assert.Equal("low_memory", r2);
            Assert.False(SessionCaps.ShouldStop(10, 300, SpeechEngine.BuiltIn, 250, out _));
            Assert.True(SessionCaps.ShouldStop(10, 300, SpeechEngine.BuiltIn, 150, out _));
            Assert.False(SessionCaps.ShouldStop(10, 570, SpeechEngine.Gemini, 50, out _));   // online doesn't hold audio
        }

        [Fact]
        public void SwitchingEnginesNeverCutsOffAbruptly()
        {
            Assert.Equal(300, SessionCaps.AfterSwitch(60, 300));
            Assert.Equal(380, SessionCaps.AfterSwitch(360, 300));   // already past the offline limit: 20 s countdown
        }

        [Theory]
        [InlineData(0, 570, "0:00", false)]
        [InlineData(7.9, 570, "0:07", false)]
        [InlineData(65, 570, "1:05", false)]
        [InlineData(549.9, 570, "9:09", false)]
        [InlineData(550, 570, "0:20 left", true)]
        [InlineData(561.2, 570, "0:09 left", true)]
        [InlineData(570, 570, "0:00 left", true)]
        [InlineData(100, 120, "0:20 left", true)]
        [InlineData(620, 900, "10:20", false)]
        public void TimerFormat(double elapsed, int max, string text, bool countdown)
        {
            var t = TimerText.For(elapsed, max);
            Assert.Equal(text, t.Text);
            Assert.Equal(countdown, t.Countdown);
        }
    }

    public class V16ChunkTests
    {
        private static byte[] Pcm(int samples, Func<int, short> f)
        {
            var b = new byte[samples * 2];
            for (int i = 0; i < samples; i++) { short v = f(i); b[2 * i] = (byte)v; b[2 * i + 1] = (byte)(v >> 8); }
            return b;
        }

        [Fact]
        public void PiecesOfAboutThirtySecondsAndMemoryStaysFlat()
        {
            var c = new OfflineChunker();
            var pieces = new List<short[]>();
            // 125 s of "speech" fed in 100 ms buffers, like the recorder does
            for (int t = 0; t < 1250; t++) pieces.AddRange(c.Add(Pcm(1600, i => (short)(8000 * Math.Sin((t * 1600 + i) * 0.05)))));
            var last = c.Flush();
            Assert.Equal(4, pieces.Count);
            Assert.All(pieces, p => Assert.InRange(p.Length, 25 * 16000, 30 * 16000));
            Assert.NotNull(last);
            Assert.Equal(125 * 16000, pieces.Sum(p => p.Length) + last.Length);   // nothing lost, nothing doubled
            Assert.InRange(c.PeakSamples, 30 * 16000, 31 * 16000);                 // never more than one piece held
            Assert.Null(c.Flush());
        }

        [Fact]
        public void CutsInThePauseNotInAWord()
        {
            var c = new OfflineChunker();
            // loud everywhere except a 300 ms pause at 27.0 s
            int pauseAt = 27 * 16000, pauseLen = 4800;
            var pieces = c.Add(Pcm(31 * 16000, i => i >= pauseAt && i < pauseAt + pauseLen ? (short)0 : (short)(9000 * Math.Sin(i * 0.07))));
            Assert.Single(pieces);
            Assert.InRange(pieces[0].Length, pauseAt, pauseAt + pauseLen);
        }

        [Fact]
        public void FloatsForWhisper()
        {
            var f = OfflineChunker.ToFloat(new short[] { 0, 16384, -32768 });
            Assert.Equal(0f, f[0]); Assert.Equal(0.5f, f[1]); Assert.Equal(-1f, f[2]);
        }
    }

    public class V16FallbackTests
    {
        private static EngineInputs Offline(DeviceTier tier, bool model = false, bool builtIn = true) =>
            new EngineInputs { Online = false, Tier = tier, WhisperModelReady = model, BuiltInAvailable = builtIn };

        [Fact]
        public void OnlineUsesGemini()
        {
            Assert.Equal(SpeechEngine.Gemini, EnginePolicy.Choose(new EngineInputs { Online = true, Tier = DeviceTier.Strong, WhisperModelReady = true }).Engine);
        }

        [Fact]
        public void OfflinePicksWhatTheLaptopCanRun()
        {
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(Offline(DeviceTier.Low, model: true)).Engine);      // Low never runs Whisper
            Assert.Equal(SpeechEngine.Whisper, EnginePolicy.Choose(Offline(DeviceTier.Medium, model: true)).Engine);
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(Offline(DeviceTier.Strong, model: false)).Engine);  // not downloaded
            var blocked = Offline(DeviceTier.Strong, model: true); blocked.WhisperBlocked = true;                       // S mode
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(blocked).Engine);
            var full = Offline(DeviceTier.Strong, model: true); full.WhisperFitsMemory = false;
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(full).Engine);
            var none = EnginePolicy.Choose(Offline(DeviceTier.Low, builtIn: false));
            Assert.False(none.Ok); Assert.Equal(NoEngineReason.NoRecognizer, none.Why);
            var off = Offline(DeviceTier.Strong, model: true); off.OfflineFallback = false;
            Assert.Equal(NoEngineReason.OfflineFallbackOff, EnginePolicy.Choose(off).Why);
        }

        [Fact]
        public void OnlyLosingTheInternetSwitches()
        {
            Assert.True(EnginePolicy.SwitchesOffline(LiveErrorKind.NoInternet));
            Assert.True(EnginePolicy.SwitchesOffline(LiveErrorKind.ConnectionLost));
            Assert.False(EnginePolicy.SwitchesOffline(LiveErrorKind.BadKey));
            Assert.False(EnginePolicy.SwitchesOffline(LiveErrorKind.MissingKey));
            Assert.False(EnginePolicy.SwitchesOffline(LiveErrorKind.Server));
        }

        [Fact]
        public void SwitchKeepsCapturedWordsAndReplaysUnconfirmedAudio()
        {
            var online = new List<byte[]>();
            var offline = new List<byte[]>();
            var r = new DictationRouter(b => online.Add(b));
            r.Feed(new byte[] { 1, 1 });
            r.Feed(new byte[] { 2, 2 });
            r.OnOnlineWords("hello there");          // Gemini showed words for the audio so far
            r.Feed(new byte[] { 3, 3 });
            r.Feed(new byte[] { 4, 4 });
            Assert.Equal(4, r.ReplayBytes);
            Assert.True(r.SwitchToOffline(b => offline.Add(b)));
            r.Feed(new byte[] { 5, 5 });
            Assert.Equal(4, online.Count);
            Assert.Equal(new byte[] { 3, 4, 5 }, offline.Select(b => b[0]));   // replayed in order, then live audio
            Assert.True(r.IsOffline);
            Assert.Equal("hello there", r.Captured);
            Assert.False(r.SwitchToOffline(b => { }));                         // only once
            r.OnOnlineWords("late words");                                     // ignored after the switch
            Assert.Equal("hello there", r.Captured);
            Assert.Equal("Hello there. And then offline.", EnginePolicy.Join(RulePolish.Clean(r.Captured), RulePolish.Clean("and then offline")));
        }

        [Fact]
        public void ReplayIsCappedAtThirtySeconds()
        {
            var r = new DictationRouter(b => { });
            for (int i = 0; i < 400; i++) r.Feed(new byte[3200]);   // 40 s
            Assert.InRange(r.ReplayBytes, DictationRouter.ReplayMaxBytes - 3200, DictationRouter.ReplayMaxBytes);
        }

        [Fact]
        public void StartingOfflineSendsEverythingOffline()
        {
            int got = 0;
            var r = DictationRouter.Offline(b => got += b.Length);
            r.Feed(new byte[10]);
            Assert.True(r.IsOffline);
            Assert.Equal(10, got);
            Assert.Equal(0, r.ReplayBytes);
        }

        [Fact]
        public void JoinText()
        {
            Assert.Equal("one", EnginePolicy.Join("one", ""));
            Assert.Equal("two", EnginePolicy.Join("", "two"));
            Assert.Equal("One. Two", EnginePolicy.Join("One", "two"));
            Assert.Equal("One! Two", EnginePolicy.Join("One!", "Two"));
        }
    }

    public class V16PolishTests
    {
        [Theory]
        [InlineData("um so i think we should uh go", "So I think we should go.")]
        [InlineData("hello world. this is rapcha", "Hello world. This is rapcha.")]
        [InlineData("  ummm,  okay   then ,  fine  ", "Okay then, fine.")]
        [InlineData("is it ready?", "Is it ready?")]
        [InlineData("i'm sure i'll be there", "I'm sure I'll be there.")]
        [InlineData("the price is 3.5 dollars", "The price is 3.5 dollars.")]
        [InlineData("uh", "")]
        [InlineData("", "")]
        [InlineData("hmm, well, er, yes", "Well, yes.")]
        [InlineData("Already clean.", "Already clean.")]
        [InlineData("summer is here", "Summer is here.")]          // "um" inside a word stays
        [InlineData("first part.second part", "First part. Second part.")]
        public void CleansWithoutAModel(string input, string want)
        {
            Assert.Equal(want, RulePolish.Clean(input));
        }

        [Fact]
        public void AppliesCustomVocabulary()
        {
            var vocab = new List<string> { "Rapcha", "Nii Thompson", "GitHub", "Kotlin" };
            Assert.Equal("Open Rapcha on GitHub.", RulePolish.Clean("open rap cha on github", vocab));
            Assert.Equal("Rapcha by Nii Thompson.", RulePolish.Clean("rapchar by nii thompson", vocab));
            Assert.Equal("Rapcha and Kotlin.", RulePolish.Clean("RAPCHA and kotlin", vocab));
            Assert.Equal("Mondays are long.", RulePolish.Clean("mondays are long", new List<string> { "Monday" }));   // plural kept
            Assert.Equal("Rapchas are fun.", RulePolish.Clean("rapchas are fun", vocab));                          // longer form kept
            Assert.Equal("Rock and roll.", RulePolish.Clean("rock and roll", vocab));
        }

        [Fact]
        public void EditDistance()
        {
            Assert.Equal(1, RulePolish.Edits("rapchar", "rapcha", 2));
            Assert.Equal(3, RulePolish.Edits("abc", "xyzab", 2));
        }
    }
}
