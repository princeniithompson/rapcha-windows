using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    public sealed class Transcript
    {
        public string Id;
        public string Text;
        public DateTime TimeUtc;
        public int Words => TranscriptHistory.CountWords(Text);
    }

    /// <summary>
    /// The finished dictations shown on the Home page, newest first, saved as plain JSON
    /// (%AppData%\Rapcha\history.json). The text is never logged.
    /// </summary>
    public sealed class TranscriptHistory
    {
        public const int MaxItems = 2000;
        public readonly List<Transcript> Items = new List<Transcript>();

        public int TotalWords => Items.Sum(t => t.Words);

        public static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int n = 0; bool inWord = false;
            foreach (char c in text)
            {
                bool w = !char.IsWhiteSpace(c);
                if (w && !inWord) n++;
                inWord = w;
            }
            return n;
        }

        public Transcript Add(string text, DateTime utc)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            var t = new Transcript { Id = Guid.NewGuid().ToString("N"), Text = text, TimeUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc) };
            Items.Insert(0, t);
            if (Items.Count > MaxItems) Items.RemoveRange(MaxItems, Items.Count - MaxItems);
            return t;
        }

        public bool Remove(string id) => Items.RemoveAll(t => t.Id == id) > 0;

        public string ToJson()
        {
            var sb = new StringBuilder("{\"version\":1,\"items\":[");
            for (int i = 0; i < Items.Count; i++)
            {
                var t = Items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(MiniJson.Quote(t.Id))
                  .Append(",\"time\":").Append(MiniJson.Quote(t.TimeUtc.ToString("o", CultureInfo.InvariantCulture)))
                  .Append(",\"text\":").Append(MiniJson.Quote(t.Text)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static TranscriptHistory FromJson(string json)
        {
            var h = new TranscriptHistory();
            if (string.IsNullOrWhiteSpace(json)) return h;
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("items", out var items) || !(items is List<object> list)) return h;
            foreach (var o in list)
            {
                var text = MiniJson.Str(o, "text");
                if (text.Trim().Length == 0) continue;
                DateTime.TryParse(MiniJson.Str(o, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time);
                var id = MiniJson.Str(o, "id");
                h.Items.Add(new Transcript { Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N"), Text = text, TimeUtc = DateTime.SpecifyKind(time, DateTimeKind.Utc) });
            }
            h.Items.Sort((a, b) => b.TimeUtc.CompareTo(a.TimeUtc));
            return h;
        }

        /// <summary>Day groups, newest first: "Today", "Yesterday", then dates like "Friday, 13 November" (with the year if not this year).</summary>
        public static List<KeyValuePair<string, List<Transcript>>> GroupByDay(IEnumerable<Transcript> items, DateTime nowLocal, Func<DateTime, DateTime> toLocal)
        {
            var result = new List<KeyValuePair<string, List<Transcript>>>();
            foreach (var g in items.OrderByDescending(t => t.TimeUtc).GroupBy(t => toLocal(t.TimeUtc).Date))
            {
                var day = g.Key;
                string title = day == nowLocal.Date ? "Today"
                    : day == nowLocal.Date.AddDays(-1) ? "Yesterday"
                    : day.ToString(day.Year == nowLocal.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
                result.Add(new KeyValuePair<string, List<Transcript>>(title, g.ToList()));
            }
            return result;
        }
    }
}
