using System;
using System.IO;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>v19: hold-to-snooze is 3 s and survives hand shake; the diagnostics building blocks.</summary>
    public class V19SnoozeTests
    {
        [Fact]
        public void HoldIsThreeSeconds()
        {
            Assert.Equal(3000, SnoozeHold.HoldMs);
            Assert.Equal(10, SnoozeHold.ToleranceDip);
        }

        [Fact]
        public void ShakeOfEightPixelsDoesNotRestartTheWait()
        {
            var h = new SnoozeHold(10);
            var rnd = new Random(1);
            double armedAt = -1;
            for (double t = 0; t <= 4000; t += 100)
                if (h.Update(true, 960 + rnd.Next(-4, 5), 520 + rnd.Next(-4, 5), t)) { armedAt = t; break; }
            Assert.Equal(3000, armedAt);
        }

        [Fact]
        public void TimerStartsWhenTheDragStops()
        {
            var h = new SnoozeHold(10);
            // dragging across the middle: 40 px every 100 ms for 2 s
            for (int i = 0; i < 20; i++) Assert.False(h.Update(true, 600 + i * 40, 500, i * 100));
            // stopped at t=2000 (the last move above was at 1900, x=1360): circle 3 s after the ring stopped
            Assert.False(h.Update(true, 1362, 501, 2000));
            Assert.InRange(h.HeldMs(2000), 0, 101);
            for (double t = 2100; t < 1900 + 3000; t += 100) Assert.False(h.Update(true, 1360 + (int)(t / 100) % 3, 500, t));
            Assert.True(h.Update(true, 1361, 502, 4900));
        }

        [Fact]
        public void ToleranceIsFromTheAnchorNotTheLastSample()
        {
            // a slow creep of 3 px per sample would never reset a "last sample" rule; from the anchor it does after 4 samples
            var h = new SnoozeHold(10);
            for (int i = 0; i < 60; i++) Assert.False(h.Update(true, 500 + i * 3, 500, i * 100));
        }

        [Fact]
        public void LeavingTheMiddleRestarts()
        {
            var h = new SnoozeHold(10);
            h.Update(true, 500, 500, 0);
            h.Update(true, 500, 500, 2500);
            h.Update(false, 500, 500, 2600);
            Assert.False(h.Update(true, 500, 500, 3100));
            Assert.False(h.Update(true, 500, 500, 6000));
            Assert.True(h.Update(true, 500, 500, 6100));
        }
    }

    [Collection("diag")]
    public class V19DiagnosticsTests
    {
        [Fact]
        public void KeysAreRemoved()
        {
            var fake = "AIza" + new string('x', 35);
            Assert.DoesNotContain(fake, DiagLog.RedactKeys("key " + fake + " end"));
            Assert.Equal("https://x/y?key=[removed]&a=1", DiagLog.RedactKeys("https://x/y?key=abc123&a=1"));
            DiagLog.Add("test", "url ?key=" + fake);
            Assert.DoesNotContain(DiagLog.Snapshot(), l => l.Contains(fake));
        }

        [Fact]
        public void WordsOnlyWhenOptedInAndNeverInTheFile()
        {
            string toFile = "";
            var oldSink = DiagLog.Sink;
            DiagLog.Sink = l => toFile += l + "\n";
            try
            {
                DiagLog.IncludeText = false;
                DiagLog.Words("piece 1", "the boy is coming");
                Assert.DoesNotContain(DiagLog.Snapshot(), l => l.Contains("the boy is coming"));
                DiagLog.IncludeText = true;
                DiagLog.Words("piece 2", "the boy is coming");
                Assert.Contains(DiagLog.Snapshot(), l => l.Contains(" [text] piece 2"));
                Assert.DoesNotContain("the boy", toFile);
                Assert.Contains(DiagLog.ForExport(true), l => l.Contains("piece 2"));
                Assert.DoesNotContain(DiagLog.ForExport(false), l => l.Contains(" [text] "));
            }
            finally { DiagLog.IncludeText = false; DiagLog.Sink = oldSink; }
        }

        [Fact]
        public void MicTrackerKnowsWhoHoldsTheMicrophone()
        {
            MicTracker.ResetForTest();
            Assert.Equal("Microphone open now: no", MicTracker.NowText());
            int a = MicTracker.Opened("Settings level meter");
            int b = MicTracker.Opened("voice typing");
            Assert.Equal(2, MicTracker.Count);
            Assert.Contains("by Settings level meter", MicTracker.NowText());
            MicTracker.Closed(a, "page hidden");
            MicTracker.Closed(a, "twice");   // ignored
            Assert.Equal(1, MicTracker.Count);
            MicTracker.Closed(b);
            Assert.Equal(0, MicTracker.Count);
            Assert.Equal(2, MicTracker.Opens);
            Assert.Equal(2, MicTracker.Closes);
            Assert.Contains(DiagLog.Snapshot(), l => l.Contains("[mic] CLOSE #1 by Settings level meter") && l.Contains("page hidden"));
            MicTracker.ResetForTest();
        }
    }

    public class V19AudioTextTests
    {
        [Fact]
        public void AudioStatsLevelsAndClipping()
        {
            var s = new short[16000];
            for (int i = 0; i < s.Length; i++) s[i] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 16384);
            var a = AudioStats.Of(s);
            Assert.Equal(16000, a.Samples);
            Assert.InRange(a.PeakDb, -6.5, -5.5);
            Assert.InRange(a.RmsDb, -9.6, -8.6);
            Assert.Equal(0, a.ClipPercent);
            for (int i = 0; i < 160; i++) s[i] = short.MaxValue;
            Assert.InRange(AudioStats.Of(s).ClipPercent, 0.99, 1.01);
            Assert.Equal(-96, AudioStats.Of(new short[100]).RmsDb);
            Assert.Equal(-96, AudioStats.Of(null).PeakDb);
        }

        [Fact]
        public void CompressionRatioFlagsRepetition()
        {
            Assert.Equal(0, TextStats.CompressionRatio(""));
            Assert.True(TextStats.CompressionRatio("the boy is coming home from school") < 1.5);
            Assert.True(TextStats.CompressionRatio(string.Concat(Enumerable.Repeat("thank you. ", 20))) > 2.4);
            Assert.Equal(4, TextStats.WordCount(" the boy  is coming "));
            Assert.Equal(0, TextStats.WordCount(null));
        }

        [Fact]
        public void RollingFileKeepsThreeFilesUnderTheCap()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rapcha-roll-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new RollingFile(dir, "diagnostics", 1000, 3);
                for (int i = 0; i < 200; i++) f.Append("line " + i.ToString("000") + " " + new string('x', 40));
                var files = f.Existing();
                Assert.Equal(3, files.Count);
                Assert.All(files, p => Assert.True(new FileInfo(p).Length <= 1000));
                Assert.Contains("line 199", File.ReadAllText(f.PathOf(0)));
                Assert.False(File.Exists(Path.Combine(dir, "diagnostics.3.log")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void LastAudioKeepsTheMostRecentThreeMinutes()
        {
            var la = new LastAudio();
            Assert.Equal(0, la.Bytes);
            for (int i = 0; i < 2000; i++) la.Add(new byte[3200]);   // 200 s of 100 ms packets
            Assert.InRange(la.Bytes, LastAudio.MaxBytes - 3200, LastAudio.MaxBytes);
            Assert.Equal(la.Bytes, la.ToArray().Length);
            la.Clear();
            Assert.Empty(la.ToArray());
        }
    }
}
