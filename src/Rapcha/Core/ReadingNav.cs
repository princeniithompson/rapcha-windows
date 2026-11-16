using System;
using System.Collections.Generic;

namespace Rapcha.Core
{
    /// <summary>One stretch that Previous / Next jump between: words [First, End) of the spoken list.</summary>
    public struct Segment
    {
        public int First, End;   // End is exclusive
        public Segment(int first, int end) { First = first; End = end; }
        public int Count => End - First;
    }

    /// <summary>
    /// Splits the words being read into the units Previous / Next jump between (v15): one per paragraph, list item
    /// or heading (the page layout's blocks). Words without a block (a lone line) split by line instead.
    /// </summary>
    public static class Segments
    {
        public static List<Segment> Build(IList<OcrWord> words)
        {
            var list = new List<Segment>();
            if (words == null || words.Count == 0) return list;
            int first = 0;
            for (int i = 1; i <= words.Count; i++)
            {
                bool cut = i == words.Count;
                if (!cut)
                {
                    var a = words[i - 1]; var b = words[i];
                    cut = a.Block >= 0 || b.Block >= 0 ? a.Block != b.Block : a.Line != b.Line;
                }
                if (cut) { list.Add(new Segment(first, i)); first = i; }
            }
            return list;
        }

        /// <summary>Index of the segment that holds a word (the last one before it if none does).</summary>
        public static int IndexOf(List<Segment> segs, int word)
        {
            if (segs.Count == 0) return -1;
            for (int i = segs.Count - 1; i >= 0; i--) if (word >= segs[i].First) return i;
            return 0;
        }
    }

    /// <summary>
    /// Previous / Next like Android Select to Speak, on a clock the tests can drive.
    /// Next: the start of the next segment; on the last one, the next block on the screen when the reading
    /// may continue (a tap), otherwise Finished. Previous: back to the start of the current segment, unless it
    /// started less than <see cref="RestartWindowMs"/> ago, then the segment before (or the block before on the
    /// screen); on the first segment it restarts.
    /// </summary>
    public sealed class ReadingNav
    {
        public const int RestartWindowMs = 1500;

        public enum Kind { None, Jump, NextBlock, PreviousBlock, Finished }

        public struct Move
        {
            public Kind Kind;
            public int Word;      // Jump: the first word of the segment to read from
            public int Segment;
            public override string ToString() => Kind + (Kind == Kind.Jump ? " " + Word : "");
        }

        private readonly Func<long> _clock;
        private List<Segment> _segs = new List<Segment>();
        private int _current = -1;
        private long _startedAt;

        public ReadingNav(Func<long> clockMs) { _clock = clockMs; }

        public IReadOnlyList<Segment> List => _segs;
        public int Current => _current;
        /// <summary>A tap: Next / Previous may go on into the neighbouring blocks on the screen.</summary>
        public bool CanLeave { get; private set; }
        public bool HasNextBlock { get; set; }
        public bool HasPreviousBlock { get; set; }

        /// <summary>A new reading starts (its first segment begins now).</summary>
        public void Reset(List<Segment> segs, bool canLeave, int startWord = 0)
        {
            _segs = segs ?? new List<Segment>();
            CanLeave = canLeave;
            _current = _segs.Count == 0 ? -1 : Segments.IndexOf(_segs, startWord);
            _startedAt = _clock();
        }

        /// <summary>The voice reached a word: entering a new segment starts its clock.</summary>
        public void OnWord(int word)
        {
            int i = Segments.IndexOf(_segs, word);
            if (i != _current) { _current = i; _startedAt = _clock(); }
        }

        public long SinceStartMs => _clock() - _startedAt;

        public Move Next()
        {
            if (_segs.Count == 0) return new Move { Kind = Kind.None };
            int cur = Math.Max(0, _current);
            if (cur + 1 < _segs.Count) return Go(cur + 1);
            if (CanLeave && HasNextBlock) return new Move { Kind = Kind.NextBlock, Segment = cur };
            return new Move { Kind = Kind.Finished, Segment = cur };
        }

        public Move Previous()
        {
            if (_segs.Count == 0) return new Move { Kind = Kind.None };
            int cur = Math.Max(0, _current);
            bool early = SinceStartMs < RestartWindowMs;
            if (early && cur > 0) return Go(cur - 1);
            if (early && cur == 0 && CanLeave && HasPreviousBlock) return new Move { Kind = Kind.PreviousBlock, Segment = 0 };
            return Go(cur);   // restart the current one (the first one always restarts)
        }

        private Move Go(int seg)
        {
            _current = seg;
            _startedAt = _clock();
            return new Move { Kind = Kind.Jump, Word = _segs[seg].First, Segment = seg };
        }
    }
}
