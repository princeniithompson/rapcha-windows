using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rapcha.Core
{
    /// <summary>
    /// v18: energy-based voice activity detection for 16 kHz mono 16-bit audio. The noise floor is the quiet end of
    /// the piece (20th percentile of 20 ms frame levels); speech is a frame clearly above it (3x, about +10 dB) and
    /// above a small absolute minimum. A piece needs at least 160 ms of speech, including one run of 60 ms, or it is
    /// treated as silence/noise and never sent to Whisper (Whisper invents words for silence and hum).
    /// </summary>
    public static class SpeechVad
    {
        public const int Rate = 16000;
        public const int FrameSamples = 320;           // 20 ms
        public const double MinSpeechRms = 90;         // about -51 dBFS: far below any real speech
        public const double OverNoise = 3.0;
        public const int MinSpeechFrames = 8;          // 160 ms
        public const int MinRunFrames = 3;             // 60 ms in a row
        public const int PadBeforeFrames = 15;         // keep 300 ms before the first speech
        public const int PadAfterFrames = 20;          // and 400 ms after the last

        public struct Result
        {
            public bool HasSpeech;
            public int Start, End;      // samples to keep [Start, End)
            public int SpeechFrames, Frames;
            public double NoiseRms, SpeechRms, Threshold;
            public double SpeechSeconds => SpeechFrames * FrameSamples / (double)Rate;
        }

        public static double Rms(short[] s, int at, int n)
        {
            double sum = 0;
            int end = Math.Min(s.Length, at + n);
            for (int i = at; i < end; i++) sum += (double)s[i] * s[i];
            return Math.Sqrt(sum / Math.Max(1, end - at));
        }

        public static Result Analyze(short[] pcm)
        {
            var r = new Result();
            if (pcm == null || pcm.Length < FrameSamples) return r;
            int frames = pcm.Length / FrameSamples;
            var levels = new double[frames];
            for (int f = 0; f < frames; f++) levels[f] = Rms(pcm, f * FrameSamples, FrameSamples);
            var sorted = (double[])levels.Clone();
            Array.Sort(sorted);
            double noise = Math.Max(1.0, sorted[(int)(frames * 0.2)]);
            double threshold = Math.Max(MinSpeechRms, noise * OverNoise);
            r.Frames = frames; r.NoiseRms = noise; r.Threshold = threshold;
            int first = -1, last = -1, count = 0, run = 0, bestRun = 0;
            double speechSum = 0;
            for (int f = 0; f < frames; f++)
            {
                if (levels[f] >= threshold)
                {
                    count++; run++; speechSum += levels[f];
                    if (run > bestRun) bestRun = run;
                    if (first < 0) first = f;
                    last = f;
                }
                else run = 0;
            }
            r.SpeechFrames = count;
            r.SpeechRms = count > 0 ? speechSum / count : 0;
            r.HasSpeech = count >= MinSpeechFrames && bestRun >= MinRunFrames;
            if (!r.HasSpeech) return r;
            r.Start = Math.Max(0, (first - PadBeforeFrames) * FrameSamples);
            r.End = Math.Min(pcm.Length, (last + 1 + PadAfterFrames) * FrameSamples);
            return r;
        }

        /// <summary>The speech part of the piece (with a little padding), or null when there is no speech.</summary>
        public static short[] Trim(short[] pcm, out Result r)
        {
            r = Analyze(pcm);
            if (!r.HasSpeech) return null;
            var o = new short[r.End - r.Start];
            Array.Copy(pcm, r.Start, o, 0, o.Length);
            return o;
        }

        /// <summary>
        /// Brings quiet speech up to a normal level (speech RMS about 3000, at most 10x, peaks kept under 0.9).
        /// Laptop microphones are often 20-30 dB quieter than a headset; Gemini already got a gain like this.
        /// </summary>
        public static short[] Normalize(short[] pcm, double speechRms, out double gain)
        {
            gain = 1.0;
            if (pcm == null || pcm.Length == 0) return pcm;
            int peak = 0;
            foreach (var s in pcm) { int a = Math.Abs((int)s); if (a > peak) peak = a; }
            if (speechRms <= 1 || peak <= 0) return pcm;
            gain = Math.Min(10.0, Math.Min(3000.0 / speechRms, 29500.0 / peak));
            if (gain <= 1.05) { gain = 1.0; return pcm; }
            var o = new short[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                double v = pcm[i] * gain;
                o[i] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(v)));
            }
            return o;
        }

        /// <summary>whisper.cpp refuses anything under 1 s: pad short speech with silence to 1.5 s.</summary>
        public static short[] PadForWhisper(short[] pcm, int minSamples = Rate * 3 / 2)
        {
            if (pcm == null || pcm.Length >= minSamples) return pcm;
            var o = new short[minSamples];
            int lead = Math.Min(Rate / 5, minSamples - pcm.Length);   // 200 ms of silence first
            Array.Copy(pcm, 0, o, lead, pcm.Length);
            return o;
        }
    }

    /// <summary>v18: joins Whisper pieces that overlap by about a second, and drops Whisper's well-known inventions.</summary>
    public static class WhisperText
    {
        private static readonly Regex Word = new Regex(@"[\p{L}\p{N}']+");

        private static string Key(string w) => new string((w ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>
        /// The new piece without the words that repeat the end of the previous text (the overlap). Looks for the
        /// longest run of 1-8 words that ends the previous text and starts the new one.
        /// </summary>
        public static string Dedupe(string previous, string next)
        {
            next = (next ?? "").Trim();
            if (string.IsNullOrEmpty(previous) || next.Length == 0) return next;
            var prev = Word.Matches(previous).Cast<Match>().Select(m => Key(m.Value)).Where(k => k.Length > 0).ToList();
            var tokens = Regex.Split(next, @"\s+").Where(t => t.Length > 0).ToList();
            var nk = tokens.Select(Key).ToList();
            int best = 0;
            for (int k = Math.Min(8, Math.Min(prev.Count, nk.Count)); k >= 1; k--)
            {
                bool same = true;
                for (int i = 0; i < k && same; i++) same = prev[prev.Count - k + i] == nk[i] && nk[i].Length > 0;
                if (same) { best = k; break; }
            }
            // a single short word matching is often chance ("the", "a"): only drop it when it is 4+ letters
            if (best == 1 && nk[0].Length < 4) best = 0;
            return string.Join(" ", tokens.Skip(best)).Trim();
        }

        // Phrases Whisper is known to invent for noise (from YouTube captions in its training data).
        private static readonly string[] Inventions =
        {
            "thanks for watching", "thank you for watching", "thanks for listening", "please subscribe", "like and subscribe",
        };
        // Never real dictation: dropped whatever the length.
        private static readonly string[] Tags = { "subtitles by the amaraorg community", "transcribed by", "music", "applause", "silence", "blank audio", "inaudible" };
        // Real words people do say, but also Whisper's favourite inventions: only dropped when there was hardly any speech.
        private static readonly string[] ShortInventions = { "thank you", "you", "bye", "thank you so much" };

        /// <summary>True when the whole piece is one of Whisper's stock phrases and there was little speech in it.</summary>
        public static bool IsLikelyInvention(string text, double speechSeconds)
        {
            var k = string.Join(" ", Word.Matches(text ?? "").Cast<Match>().Select(m => Key(m.Value)).Where(x => x.Length > 0));
            if (k.Length == 0 || Tags.Contains(k)) return true;
            if (speechSeconds < 2.5 && Inventions.Contains(k)) return true;
            return speechSeconds < 0.5 && ShortInventions.Contains(k);
        }
    }

    /// <summary>
    /// v18 memory guard for one dictation. Before v18 it stopped as soon as Windows reported less than 300 MB
    /// (Whisper) or 200 MB (built-in) free, which a 4 GB laptop does all the time, and Whisper itself uses ~400 MB
    /// right after it starts, so the check tripped about a second in. Now it watches Rapcha's own memory: its
    /// private bytes and the audio it holds. Windows' free memory only counts as a last resort for Whisper (under
    /// 100 MB for 5 seconds in a row). Built-in Windows speech is never stopped for Windows' free memory.
    /// </summary>
    public sealed class MemoryGuard
    {
        public const long BuiltInProcessLimitMb = 1000;
        public const long WhisperProcessLimitMb = 1600;   // a 32-bit process; Tiny/Base use ~400-500 MB
        public const long SystemFloorMb = 100;
        public const double SystemFloorSeconds = 5;
        public const long HeldAudioLimitBytes = 2L * 31 * 16000 * 2 * 2; // two pieces of 31 s, twice over

        private double _lowSince = -1;

        public static long ProcessLimitMb(SpeechEngine e) => e == SpeechEngine.Whisper ? WhisperProcessLimitMb : BuiltInProcessLimitMb;

        /// <param name="now">seconds since the dictation started</param>
        /// <param name="privateMb">Rapcha's private bytes in MB (-1 = unknown)</param>
        /// <param name="systemFreeMb">Windows' available memory in MB (-1 = unknown)</param>
        /// <param name="heldAudioBytes">audio Rapcha is holding for the engine (-1 = unknown)</param>
        public bool ShouldStop(double now, SpeechEngine engine, long privateMb, long systemFreeMb, long heldAudioBytes, out string reason)
        {
            reason = "";
            if (engine == SpeechEngine.Gemini) return false;   // online holds no model and almost no audio
            if (privateMb >= 0 && privateMb > ProcessLimitMb(engine)) { reason = "rapcha_memory"; return true; }
            if (heldAudioBytes > HeldAudioLimitBytes) { reason = "audio_buffer"; return true; }
            if (engine != SpeechEngine.Whisper || systemFreeMb < 0) { _lowSince = -1; return false; }
            if (systemFreeMb >= SystemFloorMb) { _lowSince = -1; return false; }
            if (_lowSince < 0) _lowSince = now;
            if (now - _lowSince >= SystemFloorSeconds) { reason = "system_memory_floor"; return true; }
            return false;
        }
    }

    /// <summary>
    /// v18: a stop never turns into a new dictation by itself. Right after a dictation stops on its own (time
    /// limit, memory, connection) a tap on the ring or a shortcut press is very likely the user's own "stop" arriving
    /// a moment late, so start requests are ignored for a short while after the session is back to idle.
    /// </summary>
    public sealed class StartGate
    {
        public const int AfterAutoStopMs = 3000;
        public const int AfterUserStopMs = 0;   // a stop you made yourself: the next tap starts at once
        private DateTime _until = DateTime.MinValue;
        private bool _lastAuto;
        public int Ignored { get; private set; }

        /// <summary>The dictation stopped (automatic = Rapcha stopped it, not the user).</summary>
        public void Stopped(bool automatic, DateTime now)
        {
            _lastAuto = automatic;
            var t = now.AddMilliseconds(automatic ? AfterAutoStopMs : AfterUserStopMs);
            if (t > _until) _until = t;
        }

        /// <summary>The words are in and Rapcha is idle again: the quiet time counts from here too.</summary>
        public void BackToIdle(DateTime now)
        {
            var t = now.AddMilliseconds(_lastAuto ? AfterAutoStopMs : AfterUserStopMs);
            if (t > _until) _until = t;
        }

        public bool Allow(DateTime now)
        {
            if (now >= _until) return true;
            Ignored++;
            return false;
        }

        public bool LastWasAutomatic => _lastAuto;
    }

    /// <summary>A plain 16-bit PCM WAV file (the Test microphone "Save" button and the playback).</summary>
    public static class WavFile
    {
        public static byte[] Build(byte[] pcm, int rate = 16000, int channels = 1)
        {
            pcm = pcm ?? new byte[0];
            var o = new byte[44 + pcm.Length];
            void Str(int at, string s) { for (int i = 0; i < 4; i++) o[at + i] = (byte)s[i]; }
            void U32(int at, int v) { o[at] = (byte)v; o[at + 1] = (byte)(v >> 8); o[at + 2] = (byte)(v >> 16); o[at + 3] = (byte)(v >> 24); }
            void U16(int at, int v) { o[at] = (byte)v; o[at + 1] = (byte)(v >> 8); }
            Str(0, "RIFF"); U32(4, 36 + pcm.Length); Str(8, "WAVE");
            Str(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, channels); U32(24, rate); U32(28, rate * channels * 2); U16(32, channels * 2); U16(34, 16);
            Str(36, "data"); U32(40, pcm.Length);
            Buffer.BlockCopy(pcm, 0, o, 44, pcm.Length);
            return o;
        }

        public static short[] ToSamples(byte[] pcm)
        {
            var s = new short[(pcm?.Length ?? 0) / 2];
            for (int i = 0; i < s.Length; i++) s[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
            return s;
        }

        public static byte[] ToBytes(short[] s)
        {
            var b = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++) { b[2 * i] = (byte)(s[i] & 0xFF); b[2 * i + 1] = (byte)((s[i] >> 8) & 0xFF); }
            return b;
        }
    }

    /// <summary>What the Test microphone check found, in plain words (no audio content, just levels).</summary>
    public struct MicCheck
    {
        public double Seconds, PeakDb, SpeechSeconds, NoiseDb, LostMs;
        public bool Clipped;

        public static double Db(double v) => v <= 0 ? -96 : 20 * Math.Log10(v / 32768.0);

        public static MicCheck Of(short[] s, double lostMs)
        {
            var c = new MicCheck { Seconds = s.Length / 16000.0, LostMs = lostMs };
            int peak = 0, clip = 0;
            foreach (var v in s) { int a = Math.Abs((int)v); if (a > peak) peak = a; if (a >= 32700) clip++; }
            var vad = SpeechVad.Analyze(s);
            c.PeakDb = Db(peak);
            c.NoiseDb = Db(vad.NoiseRms);
            c.SpeechSeconds = vad.HasSpeech ? vad.SpeechSeconds : 0;
            c.Clipped = clip > 16;
            return c;
        }

        public string Verdict
        {
            get
            {
                if (Seconds < 0.5) return "No sound came from the microphone.";
                if (PeakDb < -50) return "Silence: Windows gave Rapcha no sound. Check the microphone privacy setting and that the right microphone is the Windows default.";
                if (SpeechSeconds <= 0) return "Rapcha heard sound but no clear speech. Speak normally, close to the laptop, and try again.";
                var sb = new StringBuilder();
                if (Clipped) sb.Append("Too loud (it distorts): lower the microphone level in Windows Sound settings. ");
                else if (PeakDb < -26) sb.Append("Speech heard, but quiet: Rapcha boosts it, but moving closer or raising the microphone level helps. ");
                else sb.Append("Speech heard clearly ✓ ");
                if (LostMs >= 200) sb.Append("Some audio went missing (" + (int)LostMs + " ms): the laptop was very busy. ");
                return sb.ToString().Trim();
            }
        }
    }
}
