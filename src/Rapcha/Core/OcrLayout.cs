using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    public struct Box
    {
        public double X, Y, W, H;
        public Box(double x, double y, double w, double h) { X = x; Y = y; W = Math.Max(0, w); H = Math.Max(0, h); }
        public double Right => X + W;
        public double Bottom => Y + H;
        public double Area => W * H;
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
        public bool IsEmpty => W <= 0 || H <= 0;

        public static Box FromCorners(double x1, double y1, double x2, double y2)
            => new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

        public Box Intersect(Box o)
        {
            double x = Math.Max(X, o.X), y = Math.Max(Y, o.Y);
            double r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
            return r <= x || b <= y ? new Box(x, y, 0, 0) : new Box(x, y, r - x, b - y);
        }

        public Box Union(Box o)
        {
            if (IsEmpty) return o;
            if (o.IsEmpty) return this;
            double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
            return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
        }

        public bool Contains(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;
        public Box Inflate(double d) => new Box(X - d, Y - d, W + 2 * d, H + 2 * d);
        public override string ToString() => $"{X:0},{Y:0} {W:0}x{H:0}";
    }

    public sealed class OcrWord
    {
        public string Text;
        public Box Box;
        public int Line;      // visual line id (OcrLayout line index, or PageLayout line id after analysis)
        public int Block = -1; // PageLayout block (reading order), -1 if not analysed
        public Box LineBox;   // the visual line's box (the highlight uses its height)
        public OcrWord(string text, Box box) { Text = text; Box = box; }
    }

    public sealed class OcrLine
    {
        public readonly List<OcrWord> Words = new List<OcrWord>();
        public Box Box;
    }

    /// <summary>
    /// Words found by OCR on the frozen screen, grouped into lines, with the selection rules:
    /// a word is selected when most of it lies inside the box (or its centre does); the result is in
    /// reading order (lines top to bottom, then left to right; words left to right).
    /// </summary>
    public sealed class OcrLayout
    {
        public const double MinOverlap = 0.5;
        public readonly List<OcrLine> Lines = new List<OcrLine>();

        public int WordCount => Lines.Sum(l => l.Words.Count);

        /// <summary>Adds one OCR line (word texts are cleaned: no inner whitespace, empty words dropped).</summary>
        public void AddLine(IEnumerable<OcrWord> words)
        {
            var line = new OcrLine();
            foreach (var w in words)
            {
                var t = Clean(w.Text);
                if (t.Length == 0 || w.Box.IsEmpty) continue;
                var copy = new OcrWord(t, w.Box) { Line = Lines.Count };
                line.Words.Add(copy);
                line.Box = line.Box.Union(copy.Box);
            }
            if (line.Words.Count == 0) return;
            line.Words.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
            Lines.Add(line);
        }

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c) && !char.IsControl(c) && c != '\u200B' && c != '\uFEFF') sb.Append(c);
            return sb.ToString();
        }

        public static bool IsSelected(Box word, Box rect)
        {
            if (word.IsEmpty || rect.IsEmpty) return false;
            if (rect.Contains(word.CenterX, word.CenterY)) return true;
            return word.Intersect(rect).Area >= MinOverlap * word.Area;
        }

        /// <summary>Selected words in reading order.</summary>
        public List<OcrWord> Select(Box rect)
        {
            var lines = new List<List<OcrWord>>();
            foreach (var l in Lines)
            {
                var picked = l.Words.Where(w => IsSelected(w.Box, rect)).ToList();
                if (picked.Count > 0) lines.Add(picked);
            }
            return ReadingOrder(lines);
        }

        /// <summary>
        /// Orders line fragments: by vertical position, treating lines whose middles are within half a line
        /// height of each other as one row (read left to right), then words left to right.
        /// </summary>
        public static List<OcrWord> ReadingOrder(List<List<OcrWord>> lines)
        {
            var items = lines.Select(ws => new { Words = ws.OrderBy(w => w.Box.X).ToList(), Box = ws.Aggregate(new Box(), (b, w) => b.Union(w.Box)) })
                             .OrderBy(x => x.Box.CenterY).ToList();
            var rows = new List<List<int>>();
            for (int i = 0; i < items.Count; i++)
            {
                var last = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (last != null)
                {
                    var head = items[last[0]].Box;
                    double tol = Math.Min(head.H, items[i].Box.H) * 0.5;
                    if (Math.Abs(items[i].Box.CenterY - head.CenterY) <= tol) { last.Add(i); continue; }
                }
                rows.Add(new List<int> { i });
            }
            var result = new List<OcrWord>();
            foreach (var row in rows)
                foreach (var i in row.OrderBy(i => items[i].Box.X))
                    result.AddRange(items[i].Words);
            return result;
        }

        /// <summary>The whole OCR line under a point (for a tap on a word), or null.</summary>
        public OcrLine LineAt(double x, double y, double slop = 3)
        {
            foreach (var l in Lines)
                foreach (var w in l.Words)
                    if (w.Box.Inflate(slop).Contains(x, y)) return l;
            return null;
        }
    }

    /// <summary>
    /// The text handed to the voice for a list of OCR words (words joined by spaces, a line break between
    /// OCR lines), plus the map back from a spoken character offset to the word's index.
    /// </summary>
    public sealed class SpokenText
    {
        public string Text { get; }
        public IReadOnlyList<OcrWord> Words { get; }
        private readonly int[] _starts;

        public SpokenText(IList<OcrWord> words)
        {
            Words = words.ToList();
            _starts = new int[words.Count];
            var sb = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0) sb.Append(words[i].Line != words[i - 1].Line ? "\n" : " ");
                _starts[i] = sb.Length;
                sb.Append(words[i].Text);
                // End of a heading, list item or card text: add a full stop (inside the same word) so the voice pauses.
                bool blockEnd = i == words.Count - 1 || words[i + 1].Block != words[i].Block;
                if (blockEnd && words[i].Block >= 0 && i < words.Count - 1 && !EndsSentence(words[i].Text)) sb.Append('.');
            }
            Text = sb.ToString();
        }

        private static bool EndsSentence(string w)
        {
            char c = w.Length > 0 ? w[w.Length - 1] : ' ';
            return c == '.' || c == '!' || c == '?' || c == ':' || c == ';' || c == '…';
        }

        public int Count => _starts.Length;
        public int StartOf(int word) => _starts[word];

        /// <summary>Word containing (or just before) a character offset; 0 when empty.</summary>
        public int WordAtChar(int offset)
        {
            if (_starts.Length == 0) return 0;
            int i = Array.BinarySearch(_starts, offset);
            if (i < 0) i = ~i - 1;
            return Math.Max(0, Math.Min(_starts.Length - 1, i));
        }

        /// <summary>
        /// The box to highlight for a word: the word's own width, with its visual line's top and height so the
        /// green box sits evenly on mixed text (card titles, capitals, descenders).
        /// </summary>
        public Box HighlightFor(int word)
        {
            if (Words.Count == 0) return new Box();
            var w = Words[Math.Max(0, Math.Min(Words.Count - 1, word))];
            if (w.LineBox.IsEmpty || w.LineBox.H > 2.5 * w.Box.H) return w.Box;
            return new Box(w.Box.X, w.LineBox.Y, w.Box.W, w.LineBox.H);
        }
    }
}
