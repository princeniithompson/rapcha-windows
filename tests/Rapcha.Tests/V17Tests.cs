using System;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class V17SnoozeHoldTests
    {
        private static readonly IntRect Work = new IntRect(0, 0, 1920, 1040);

        [Fact]
        public void HoldingStillInTheMiddleFor55SecondsArms()
        {
            var h = new SnoozeHold(6);
            double t = 0;
            bool armedAt = false;
            for (; t <= 6000; t += 100)
            {
                bool now = h.Update(true, 960 + (int)(t / 100) % 3, 520, t);   // tiny 0..2 px jitter
                if (now) { armedAt = true; break; }
            }
            Assert.True(armedAt);
            Assert.True(h.Armed);
            Assert.InRange(t, SnoozeHold.HoldMs, SnoozeHold.HoldMs + 100);
        }

        [Fact]
        public void NotBefore55Seconds()
        {
            var h = new SnoozeHold(6);
            for (double t = 0; t < SnoozeHold.HoldMs; t += 100) Assert.False(h.Update(true, 960, 520, t));
            Assert.False(h.Armed);
        }

        [Fact]
        public void MovingMoreThanTheToleranceStartsAgain()
        {
            var h = new SnoozeHold(6);
            for (double t = 0; t <= 5000; t += 100) h.Update(true, 960, 520, t);
            h.Update(true, 980, 520, 5100);                   // moved 20 px
            for (double t = 5200; t < 5100 + SnoozeHold.HoldMs; t += 100) Assert.False(h.Update(true, 980, 520, t));
            Assert.True(h.Update(true, 982, 521, 5100 + SnoozeHold.HoldMs));
        }

        [Fact]
        public void AQuickDragAcrossTheMiddleNeverArms()
        {
            var h = new SnoozeHold(6);
            // 1.2 s from the left edge to the right edge, sampled every 16 ms, then a second slow-ish pass.
            for (int i = 0; i <= 75; i++)
            {
                int x = i * 1920 / 75, y = 520;
                Assert.False(h.Update(SnoozeZone.InMiddle(Work, x, y), x, y, i * 16));
            }
            for (int i = 0; i <= 300; i++)   // 30 s of a drag moving 8 px every 100 ms
            {
                int x = 500 + (i * 8) % 900;
                Assert.False(h.Update(SnoozeZone.InMiddle(Work, x, 520), x, 520, 2000 + i * 100));
            }
            Assert.False(h.Armed);
        }

        [Fact]
        public void LeavingTheMiddleStartsAgain()
        {
            var h = new SnoozeHold(6);
            for (double t = 0; t <= 5000; t += 100) h.Update(true, 960, 520, t);
            h.Update(false, 960, 520, 5100);
            Assert.False(h.Update(true, 960, 520, 5600));
            Assert.False(h.Armed);
        }

        [Fact]
        public void StaysArmedUntilReset()
        {
            var h = new SnoozeHold(6);
            for (double t = 0; t <= 5600; t += 100) h.Update(true, 960, 520, t);
            Assert.True(h.Armed);
            h.Update(true, 1500, 900, 6000);
            Assert.True(h.Armed);
            h.Reset();
            Assert.False(h.Armed);
        }

        [Fact]
        public void MiddleIsAwayFromEveryEdge()
        {
            Assert.True(SnoozeZone.InMiddle(Work, 960, 520));
            Assert.False(SnoozeZone.InMiddle(Work, 100, 520));     // near the left edge (where it docks)
            Assert.False(SnoozeZone.InMiddle(Work, 1850, 520));
            Assert.False(SnoozeZone.InMiddle(Work, 960, 60));
            Assert.False(SnoozeZone.InMiddle(Work, 960, 1000));
            var m = SnoozeZone.Middle(Work);
            Assert.Equal(422, m.X); Assert.Equal(229, m.Y);
        }

        [Fact]
        public void DropHitTest()
        {
            // circle 112 px at (960, 520); ring 56 px
            Assert.True(SnoozeZone.Hit(960, 520, 112, 960, 520, 56));
            Assert.True(SnoozeZone.Hit(960, 520, 112, 960 + 60, 520, 56));    // 56 + 14 = 70 px radius
            Assert.False(SnoozeZone.Hit(960, 520, 112, 960 + 80, 520, 56));
            Assert.False(SnoozeZone.Hit(960, 520, 112, 960 + 55, 520 + 55, 56));
        }

        [Fact]
        public void CircleGoesInTheCentreButNotUnderTheHeldRing()
        {
            SnoozeZone.PlaceCircle(Work, 700, 400, 56, 112, out var x, out var y);
            Assert.Equal(960, x); Assert.Equal(520, y);
            SnoozeZone.PlaceCircle(Work, 960, 520, 56, 112, out x, out y);       // held right on the centre
            Assert.False(SnoozeZone.Hit(x, y, 112, 960, 520, 56));               // not already "dropped on it"
            Assert.True(y > 520);
            Assert.True(SnoozeZone.InMiddle(Work, x, y) || y < Work.Bottom);
        }
    }

    public class V17SnoozeDurationTests
    {
        [Fact]
        public void DefaultIs19Minutes()
        {
            Assert.Equal(19, SnoozeDuration.DefaultMinutes);
            Assert.Equal(19, SnoozeDuration.Parse(null));
            Assert.Equal(19, SnoozeDuration.Parse(""));
            Assert.Equal(19, SnoozeDuration.Parse("abc"));
            Assert.Equal(19, SnoozeDuration.Parse("-5"));
            Assert.Equal(new[] { 19, 60, 120 }, SnoozeDuration.Presets);
        }

        [Theory]
        [InlineData(19)] [InlineData(60)] [InlineData(120)] [InlineData(45)] [InlineData(1440)]
        public void RoundTrips(int minutes)
        {
            Assert.Equal(minutes, SnoozeDuration.Parse(SnoozeDuration.Serialize(minutes)));
        }

        [Fact]
        public void CustomIsClamped()
        {
            Assert.Equal(1440, SnoozeDuration.Parse("99999"));
            Assert.Equal(1, SnoozeDuration.Clamp(0));
            Assert.False(SnoozeDuration.IsPreset(45));
            Assert.True(SnoozeDuration.IsPreset(120));
        }

        [Fact]
        public void Labels()
        {
            Assert.Equal("19 minutes", SnoozeDuration.Label(19));
            Assert.Equal("1 hour", SnoozeDuration.Label(60));
            Assert.Equal("2 hours", SnoozeDuration.Label(120));
            Assert.Equal("1 hour 30 minutes", SnoozeDuration.Label(90));
            Assert.Equal("1 minute", SnoozeDuration.Label(1));
        }
    }

    public class V17SnoozeClockTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void RestoresAutomaticallyWhenTheTimeIsUp()
        {
            var c = SnoozeClock.Start(Now, 19);
            Assert.True(c.Active(Now));
            Assert.False(c.Due(Now.AddMinutes(18.9)));
            Assert.True(c.Due(Now.AddMinutes(19)));
            Assert.True(c.Due(Now.AddHours(5)));              // the laptop slept past it
            Assert.False(c.Active(Now.AddMinutes(19)));
            Assert.False(new SnoozeClock().Due(Now));          // never snoozed
        }

        [Fact]
        public void TwoHourMovie()
        {
            var c = SnoozeClock.Start(Now, 120);
            Assert.Equal(TimeSpan.FromHours(2), c.Remaining(Now));
            Assert.Equal("2 h left", SnoozeClock.Left(c.Remaining(Now)));
            Assert.Equal("1 h 5 min left", SnoozeClock.Left(c.Remaining(Now.AddMinutes(55))));
        }

        [Fact]
        public void LeftText()
        {
            Assert.Equal("19 min left", SnoozeClock.Left(TimeSpan.FromMinutes(19)));
            Assert.Equal("18 min left", SnoozeClock.Left(TimeSpan.FromMinutes(17.5)));
            Assert.Equal("less than a minute left", SnoozeClock.Left(TimeSpan.FromSeconds(30)));
        }

        [Fact]
        public void SavedEndTimeRoundTrips()
        {
            var c = SnoozeClock.Start(Now, 60);
            var back = SnoozeClock.Parse(SnoozeClock.Serialize(c.UntilUtc));
            Assert.Equal(c.UntilUtc, back.UntilUtc);
            Assert.False(SnoozeClock.Parse("garbage").Due(Now));
        }
    }

    public class V17ShellTargetTests
    {
        [Theory]
        [InlineData("Windows.UI.Core.CoreWindow", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", ShellSurface.StartSearch)]
        [InlineData("Windows.UI.Core.CoreWindow", @"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe", ShellSurface.StartSearch)]
        [InlineData("Windows.UI.Core.CoreWindow", "SearchApp.exe", ShellSurface.StartSearch)]   // Windows 10
        [InlineData("Windows.UI.Core.CoreWindow", "SearchUI.exe", ShellSurface.StartSearch)]    // older Windows 10
        [InlineData("Windows.UI.Core.CoreWindow", "ShellExperienceHost.exe", ShellSurface.OtherShell)]  // Action Center etc.
        [InlineData("Shell_TrayWnd", "explorer.exe", ShellSurface.OtherShell)]
        [InlineData("Progman", "explorer.exe", ShellSurface.OtherShell)]
        [InlineData("CabinetWClass", "explorer.exe", ShellSurface.None)]                        // File Explorer stays a normal app
        [InlineData("Notepad", "notepad.exe", ShellSurface.None)]
        [InlineData("Chrome_WidgetWin_1", "chrome.exe", ShellSurface.None)]
        public void Classifies(string cls, string exe, ShellSurface want) => Assert.Equal(want, ShellTargets.Classify(cls, exe));

        [Fact]
        public void StartSearchGetsOneLine()
        {
            Assert.Equal("Open the settings app", ShellTargets.OneLine("Open the settings\r\n\r\napp\n"));
            Assert.Equal("", ShellTargets.OneLine(""));
        }

        [Fact]
        public void AvoidsOnlyWhenThereIsRoom()
        {
            var wa = new IntRect(0, 0, 1920, 1040);
            Assert.True(ShellTargets.CanAvoid(wa, new IntRect(640, 300, 640, 740)));     // Windows 11 Start, centred
            Assert.False(ShellTargets.CanAvoid(wa, new IntRect(0, 0, 1920, 1040)));      // full screen Start
            Assert.False(ShellTargets.CanAvoid(wa, new IntRect(3000, 0, 100, 100)));     // another monitor
        }
    }

    public class V17LowWhisperTests
    {
        [Fact]
        public void LowMayUseTheLightestModelAnyway()
        {
            Assert.Empty(WhisperModels.ForTier(DeviceTier.Low));                       // still not recommended
            Assert.Equal("tiny.en", WhisperModels.Lightest.Id);
            Assert.Equal(new[] { "tiny.en" }, WhisperModels.Usable(DeviceTier.Low).Select(m => m.Id));
            Assert.Equal("tiny.en", WhisperModels.Choose(DeviceTier.Low, "small.en").Id);
            Assert.Equal(new[] { "base.en", "small.en" }, WhisperModels.Usable(DeviceTier.Strong).Select(m => m.Id));
        }

        [Fact]
        public void LowWhisperCapIsTwoMinutes()
        {
            Assert.Equal(120, SessionCaps.MaxSeconds(SpeechEngine.Whisper, DeviceTier.Low));
            Assert.Equal(300, SessionCaps.MaxSeconds(SpeechEngine.BuiltIn, DeviceTier.Low));
            // v18: the free-memory guard is now MemoryGuard (Rapcha's own memory; Windows' only under 100 MB for 5 s)
            Assert.False(SessionCaps.ShouldStop(30, 120, SpeechEngine.Whisper, 250, out _));
            var g = new MemoryGuard();
            Assert.False(g.ShouldStop(30, SpeechEngine.Whisper, 500, 90, 0, out _));
            Assert.True(g.ShouldStop(36, SpeechEngine.Whisper, 500, 90, 0, out var why));
            Assert.Equal("system_memory_floor", why);
        }

        [Fact]
        public void OnlineStaysGeminiAndDeleteFallsBack()
        {
            var i = new EngineInputs { Online = true, Tier = DeviceTier.Low, WhisperModelReady = true, BuiltInAvailable = true };
            Assert.Equal(SpeechEngine.Gemini, EnginePolicy.Choose(i).Engine);
            i.Online = false;
            Assert.Equal(SpeechEngine.Whisper, EnginePolicy.Choose(i).Engine);
            i.WhisperModelReady = false;                                               // deleted
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(i).Engine);
            i.WhisperModelReady = true; i.WhisperBlocked = true;                       // S mode
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(i).Engine);
            i.WhisperBlocked = false; i.WhisperFitsMemory = false;                     // not enough free memory right now
            Assert.Equal(SpeechEngine.BuiltIn, EnginePolicy.Choose(i).Engine);
        }
    }
}
