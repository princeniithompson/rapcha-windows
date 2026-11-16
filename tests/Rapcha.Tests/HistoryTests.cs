using System;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class HistoryTests
    {
        [Fact]
        public void CountsWords()
        {
            Assert.Equal(0, TranscriptHistory.CountWords("  "));
            Assert.Equal(5, TranscriptHistory.CountWords("Put the food\nin  the"));
        }

        [Fact]
        public void SavesAndLoadsNewestFirstWithTotals()
        {
            var h = new TranscriptHistory();
            h.Add("First one here", new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc));
            h.Add("Second \"quoted\" line\nwith a break", new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc));
            h.Add("   ", DateTime.UtcNow); // blank is ignored
            var back = TranscriptHistory.FromJson(h.ToJson());
            Assert.Equal(2, back.Items.Count);
            Assert.Equal("Second \"quoted\" line\nwith a break", back.Items[0].Text);
            Assert.Equal(new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc), back.Items[0].TimeUtc);
            Assert.Equal(3 + 6, back.TotalWords);
            Assert.True(back.Remove(back.Items[1].Id));
            Assert.Equal(6, back.TotalWords);
        }

        [Fact]
        public void BrokenOrEmptyFileGivesEmptyHistory()
        {
            Assert.Empty(TranscriptHistory.FromJson("").Items);
            Assert.Empty(TranscriptHistory.FromJson("{\"items\":5}").Items);
        }

        [Fact]
        public void GroupsByTodayYesterdayAndDate()
        {
            var h = new TranscriptHistory();
            var now = new DateTime(2026, 11, 16, 10, 0, 0);
            h.Add("a", new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));
            h.Add("b", new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc));
            h.Add("c", new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
            h.Add("d", new DateTime(2025, 12, 31, 12, 0, 0, DateTimeKind.Utc));
            var g = TranscriptHistory.GroupByDay(h.Items, now, t => t);
            Assert.Equal(new[] { "Today", "Yesterday", "Friday, 13 November", "Wednesday, 31 December 2025" }, g.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void KeepsAtMostTheNewestItems()
        {
            var h = new TranscriptHistory();
            for (int i = 0; i < TranscriptHistory.MaxItems + 5; i++) h.Add("w" + i, DateTime.UtcNow.AddSeconds(i));
            Assert.Equal(TranscriptHistory.MaxItems, h.Items.Count);
            Assert.Equal("w" + (TranscriptHistory.MaxItems + 4), h.Items[0].Text);
        }
    }
}

namespace Rapcha.Tests
{
    public class GlowColorTests
    {
        [Fact]
        public void RoundTripsAndFallsBackToOlive()
        {
            Assert.Equal("#F0559B", GlowColors.Format(0xF0559B));
            Assert.Equal(0xF0559B, GlowColors.Parse(" #f0559b\r\n"));
            Assert.Equal(GlowColors.Default, GlowColors.Parse("rainbow"));
            Assert.Equal(GlowColors.Default, GlowColors.Parse(null));
            Assert.Equal("Olive", GlowColors.NameOf(GlowColors.Default));
            Assert.Equal("Custom", GlowColors.NameOf(0x123456));
            Assert.Equal(6, GlowColors.Presets.Count);
        }
    }
}
