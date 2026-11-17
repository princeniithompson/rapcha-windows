using System;
using System.Collections.Generic;

namespace Rapcha.Core
{
    /// <summary>
    /// Cuts 16 kHz mono 16-bit PCM into pieces of about 30 seconds for an offline recogniser, so memory stays flat:
    /// only the current piece is held. A piece ends at the quietest moment in its last 5 seconds (never in the middle
    /// of a word when there is a pause), or at the hard limit.
    /// </summary>
    public sealed class OfflineChunker
    {
        public const int Rate = 16000;
        private const int FrameSamples = Rate / 50;   // 20 ms

        private readonly int _target, _window, _overlap;
        private short[] _buf;
        private int _count;
        private int _fresh;   // samples not yet in any piece

        public int ChunksMade { get; private set; }
        public int BufferedSamples => _count;
        /// <summary>The most audio ever held at once (in samples): stays at about one piece.</summary>
        public int PeakSamples { get; private set; }

        /// <param name="overlapMs">v18: each new piece starts with this much of the end of the previous one, so a word cut
        /// at the join is heard whole once (the repeated words are removed with WhisperText.Dedupe).</param>
        public OfflineChunker(int targetSeconds = SessionCaps.ChunkSeconds, int windowSeconds = 5, int overlapMs = 0)
        {
            _target = Math.Max(1, targetSeconds) * Rate;
            _window = Math.Min(_target / 2, Math.Max(1, windowSeconds) * Rate);
            _overlap = Math.Max(0, Math.Min(_target / 4, overlapMs * Rate / 1000));
            _buf = new short[_target + Rate];
        }

        /// <summary>Adds little-endian 16-bit samples; returns the finished pieces (usually none).</summary>
        public List<short[]> Add(byte[] pcm) => Add(pcm, 0, pcm?.Length ?? 0);

        public List<short[]> Add(byte[] pcm, int offset, int length)
        {
            var done = new List<short[]>();
            if (pcm == null) return done;
            int n = length / 2;
            for (int i = 0; i < n; i++)
            {
                if (_count == _buf.Length) Array.Resize(ref _buf, _buf.Length + Rate);
                _buf[_count++] = (short)(pcm[offset + 2 * i] | (pcm[offset + 2 * i + 1] << 8));
                _fresh++;
                if (_count >= _target)
                {
                    PeakSamples = Math.Max(PeakSamples, _count);
                    done.Add(Cut(QuietestCut(), _overlap));
                }
            }
            PeakSamples = Math.Max(PeakSamples, _count);
            return done;
        }

        /// <summary>The rest (at the end of the dictation); null when nothing is left.</summary>
        public short[] Flush() => _count == 0 || _fresh == 0 ? null : Cut(_count);

        /// <summary>Where to cut: the start of the quietest 20 ms frame in the last window before the target.</summary>
        public int QuietestCut()
        {
            int from = Math.Max(FrameSamples, _count - _window), best = _count;
            long bestEnergy = long.MaxValue;
            for (int s = from; s + FrameSamples <= _count; s += FrameSamples / 2)
            {
                long e = 0;
                for (int i = s; i < s + FrameSamples; i++) e += (long)_buf[i] * _buf[i];
                if (e < bestEnergy) { bestEnergy = e; best = s + FrameSamples / 2; }
            }
            return best;
        }

        private short[] Cut(int at, int overlap = 0)
        {
            at = Math.Max(1, Math.Min(at, _count));
            var piece = new short[at];
            Array.Copy(_buf, piece, at);
            int from = Math.Max(0, at - overlap);
            Array.Copy(_buf, from, _buf, 0, _count - from);
            _count -= from;
            _fresh = Math.Max(0, _count - (at - from));
            ChunksMade++;
            return piece;
        }

        /// <summary>Whisper wants floats in -1..1.</summary>
        public static float[] ToFloat(short[] s)
        {
            var f = new float[s.Length];
            for (int i = 0; i < s.Length; i++) f[i] = s[i] / 32768f;
            return f;
        }
    }
}
