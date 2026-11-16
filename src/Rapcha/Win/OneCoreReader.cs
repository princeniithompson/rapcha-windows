using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rapcha.Core;
using Windows.Media.Core;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Rapcha.Win
{
    /// <summary>
    /// Reads aloud with a Windows "OneCore" voice (Windows.Media.SpeechSynthesis: the voices in Settings → Speech,
    /// including language packs). The text is synthesized to a WAV stream with word-boundary cues, then played
    /// with waveOut; the cues (or, without them, timing estimated from word lengths) move the green word box.
    /// Created and used on the UI thread.
    /// </summary>
    public sealed class OneCoreReader : IReadAloud
    {
        private readonly VoiceInformation _voice;
        private readonly bool _silent;
        private SpeechSynthesizer _synth;
        private WaveOutPlayer _player;
        private WaveOutPlayer _nextPlayer;   // the next piece, already opened, so it starts the moment this one ends
        private Chunk _nextChunk;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 15 };
        private int _gen;
        private int _baseChar;
        private double[] _cueMs = new double[0];
        private int[] _cueChar = new int[0];

        public ReadingPlan Plan { get; private set; }
        public int CurrentWord { get; private set; }
        public ReadState Status { get; private set; } = ReadState.Idle;
        public int ProgressEvents { get; private set; }
        public string VoiceName { get; }
        public bool UsedWordCues { get; private set; }
        public int LastCueCount { get; private set; }
        public DateTime AudioStartedAt { get; private set; } = DateTime.MinValue;
        /// <summary>Gap between the end of one piece and the start of the next, ms (largest in the current read).</summary>
        public int MaxPieceGapMs { get; private set; }
        public event Action Changed;

        // A piece prepared ahead (Prepare) for text that may be read next.
        private string _prepText;
        private ReadingPlan _prepPlan;
        private Task<Chunk> _prepTask;
        private bool _primed;

        public OneCoreReader(VoiceInformation voice, bool silentOutput = false)
        {
            _voice = voice;
            _silent = silentOutput;
            VoiceName = voice.DisplayName;
            _tick.Tick += (s, e) => OnTick();
        }

        private SpeechSynthesizer Synth()
        {
            if (_synth != null) return _synth;
            _synth = new SpeechSynthesizer { Voice = _voice };
            try { _synth.Options.IncludeWordBoundaryMetadata = true; } catch { }   // Windows 10 1709+
            return _synth;
        }

        public void Prime()
        {
            var synth = Synth();
            if (_primed) return;
            _primed = true;
            // The first synthesis loads the voice data: do it now, quietly, and throw the audio away.
            synth.SynthesizeTextToStreamAsync(".").AsTask().ContinueWith(t => { try { t.Result?.Dispose(); } catch { } });
        }

        public void Prepare(string text)
        {
            if (string.IsNullOrEmpty(text)) { _prepText = null; _prepTask = null; return; }
            if (text == _prepText && _prepTask != null) return;
            var plan = ReadingPlan.Create(text);
            if (plan.IsEmpty) return;
            _prepText = text;
            _prepPlan = plan;
            _prepTask = Synthesize(plan, 0, ChunkEnd(plan, 0, FirstChunkWords));
            _prepTask.ContinueWith(t => { var x = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Read(string text, int startWord = 0)
        {
            Task<Chunk> ready = null;
            if (text == _prepText && _prepTask != null && startWord == 0 && !_prepTask.IsFaulted) { Plan = _prepPlan; ready = _prepTask; }
            else Plan = ReadingPlan.Create(text);
            _prepText = null; _prepTask = null; _prepPlan = null;
            CurrentWord = 0;
            ProgressEvents = 0;
            if (Plan.IsEmpty) { Stop(); return; }
            _ = StartAt(Plan.Skip(0, startWord), ready);
        }

        public void TogglePause()
        {
            if (Plan == null || Plan.IsEmpty) return;
            if (Status == ReadState.Playing && _player != null) { _player.Pause(); Status = ReadState.Paused; Changed?.Invoke(); }
            else if (Status == ReadState.Paused)
            {
                if (_player != null) { _player.Resume(); Status = ReadState.Playing; Changed?.Invoke(); }
                else _ = StartAt(CurrentWord);
            }
            else if (Status != ReadState.Preparing) _ = StartAt(Status == ReadState.Finished ? 0 : CurrentWord);
        }

        public void Skip(int delta)
        {
            if (Plan == null || Plan.IsEmpty) return;
            int target = Plan.Skip(CurrentWord, delta);
            if (Status == ReadState.Paused)
            {
                StopPlayer();
                CurrentWord = target;
                Changed?.Invoke();
                return;
            }
            _ = StartAt(target);
        }

        public void Stop()
        {
            _gen++;
            StopPlayer();
            Status = ReadState.Idle;
            Changed?.Invoke();
        }

        private void StopPlayer()
        {
            _tick.Stop();
            if (_player != null) { try { _player.Dispose(); } catch { } _player = null; }
            if (_nextPlayer != null) { try { _nextPlayer.Dispose(); } catch { } _nextPlayer = null; }
            _nextChunk = null;
        }

        private sealed class Chunk
        {
            public int First, End;          // plan words [First, End)
            public int BaseChar;
            public byte[] Wav;
            public double[] CueMs;
            public int[] CueChar;           // relative to BaseChar
            public bool Cues;
            public bool Last;
        }

        /// <summary>The first piece is short so speech starts fast; later pieces are longer.</summary>
        public const int FirstChunkWords = 6, ChunkWords = 24;
        private Task<Chunk> _next;
        private int _chunkEnd;

        /// <summary>Plan words [first, end) for the piece starting at first: whole phrases, at least minWords.</summary>
        private int ChunkEnd(int first, int minWords) => ChunkEnd(Plan, first, minWords);

        /// <summary>Pieces end at a phrase end (sentence or line), so no word is ever cut or said twice.</summary>
        private static int ChunkEnd(ReadingPlan plan, int first, int minWords)
        {
            int end = first;
            foreach (var ph in plan.Phrases)
            {
                if (ph.EndWord <= first) continue;
                end = ph.EndWord;
                if (end - first >= minWords) break;
            }
            return Math.Max(first + 1, Math.Min(plan.Words.Count, end));
        }

        private Task<Chunk> Synthesize(int first, int end) => Synthesize(Plan, first, end);

        private async Task<Chunk> Synthesize(ReadingPlan plan, int first, int end)
        {
            var c = new Chunk { First = first, End = end, BaseChar = plan.Words[first].Start, Last = end >= plan.Words.Count };
            int endChar = plan.Words[end - 1].End;
            string text = plan.Text.Substring(c.BaseChar, endChar - c.BaseChar);
            var cueMs = new List<double>();
            var cueChar = new List<int>();
            var synth = Synth();
            try { synth.Options.SpeakingRate = VoiceSettings.Speed; } catch { }   // Windows 10 1803+
            using (var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask())
            {
                try
                {
                    foreach (var track in stream.TimedMetadataTracks)
                    {
                        if (track.Id != "SpeechWord") continue;
                        foreach (var cue in track.Cues.OfType<SpeechCue>())
                        {
                            if (!cue.StartPositionInInput.HasValue) continue;
                            cueMs.Add(cue.StartTime.TotalMilliseconds);
                            cueChar.Add(cue.StartPositionInInput.Value);
                        }
                    }
                }
                catch { cueMs.Clear(); cueChar.Clear(); }
                c.Wav = new byte[(int)stream.Size];
                using (var input = stream.GetInputStreamAt(0))
                using (var reader = new DataReader(input))
                {
                    await reader.LoadAsync((uint)stream.Size).AsTask();
                    reader.ReadBytes(c.Wav);
                }
            }
            // Trim the engine's silence: less wait before the first word, and no gap between pieces.
            c.Wav = WavTrim.Trim(c.Wav, true, 15, c.Last ? 250 : 40, out double lead);
            if (lead > 0) for (int i = 0; i < cueMs.Count; i++) cueMs[i] = Math.Max(0, cueMs[i] - lead);
            c.Cues = cueMs.Count > 0;
            c.CueMs = cueMs.ToArray();
            c.CueChar = cueChar.ToArray();
            return c;
        }

        private async Task StartAt(int word, Task<Chunk> ready = null)
        {
            int gen = ++_gen;
            StopPlayer();
            _next = null;
            CurrentWord = word;
            AudioStartedAt = DateTime.MinValue;
            MaxPieceGapMs = 0;
            Status = ReadState.Preparing;
            Changed?.Invoke();
            Chunk first;
            try { first = await (ready ?? Synthesize(word, ChunkEnd(word, FirstChunkWords))); }
            catch (Exception e)
            {
                if (gen != _gen) return;
                AppStorage.Log("OneCore voice failed: " + e.GetType().Name + " 0x" + e.HResult.ToString("X8"));
                Status = ReadState.Idle;
                Changed?.Invoke();
                return;
            }
            if (gen != _gen) return;
            Play(first);
        }

        private void Play(Chunk c, WaveOutPlayer opened = null)
        {
            try { _player = opened ?? new WaveOutPlayer(c.Wav, _silent); }
            catch (Exception e)
            {
                AppStorage.Log("OneCore playback failed: " + e.Message);
                Status = ReadState.Idle;
                Changed?.Invoke();
                return;
            }
            _baseChar = c.BaseChar;
            _chunkEnd = c.End;
            UsedWordCues = c.Cues;
            LastCueCount = c.CueMs.Length;
            if (c.Cues) { _cueMs = c.CueMs; _cueChar = c.CueChar; }
            else
            {
                // No word marks from this voice: spread the audio over the words by their length.
                var words = Plan.Words.Skip(c.First).Take(c.End - c.First).ToList();
                _cueMs = VoiceLabels.EstimateStarts(words.Select(w => w.Length).ToList(), _player.DurationMs).ToArray();
                _cueChar = words.Select(w => w.Start - _baseChar).ToArray();
            }
            // Prepare the next piece while this one plays.
            if (c.End < Plan.Words.Count)
            {
                int gen = _gen;
                int nf = c.End, ne = ChunkEnd(c.End, ChunkWords);
                _next = Synthesize(nf, ne);
                _next.ContinueWith(t => { if (t.IsFaulted) AppStorage.Log("OneCore voice failed (next piece): " + t.Exception?.InnerException?.GetType().Name); }, TaskContinuationOptions.OnlyOnFaulted);
            }
            else _next = null;
            _player.Play();
            if (AudioStartedAt == DateTime.MinValue) AudioStartedAt = DateTime.UtcNow;
            Status = ReadState.Playing;
            Changed?.Invoke();
            _tick.Start();
        }

        private void OnTick()
        {
            if (_player == null || Status != ReadState.Playing) return;
            double pos = _player.PositionMs;
            int i = Array.BinarySearch(_cueMs, pos);
            if (i < 0) i = ~i - 1;
            if (i >= 0 && i < _cueChar.Length)
            {
                int w = Plan.WordAtChar(_baseChar + _cueChar[i]);
                if (w != CurrentWord || ProgressEvents == 0) { CurrentWord = w; ProgressEvents++; Changed?.Invoke(); }
            }
            // Open the next piece as soon as it is synthesized, so switching costs nothing.
            if (_next != null && _nextPlayer == null && _next.IsCompleted)
            {
                if (_next.Status == TaskStatus.RanToCompletion)
                {
                    try { _nextChunk = _next.Result; _nextPlayer = new WaveOutPlayer(_nextChunk.Wav, _silent); }
                    catch (Exception e) { AppStorage.Log("OneCore playback failed (next piece): " + e.GetType().Name); _nextChunk = null; _nextPlayer = null; _next = null; }
                }
                else _next = null;
            }
            bool ending = _player.Done || _player.PositionMs >= _player.DurationMs - 8;
            if (ending && _nextPlayer != null)
            {
                var c = _nextChunk; var p = _nextPlayer;
                _nextChunk = null; _nextPlayer = null; _next = null;
                if (_player.Done) MaxPieceGapMs = Math.Max(MaxPieceGapMs, _tick.Interval); // at most one tick late
                try { _player.Dispose(); } catch { }
                _player = null;
                Play(c, p);
                return;
            }
            if (_player.Done)
            {
                if (_next != null) return;                        // the next piece is nearly ready: keep waiting
                StopPlayer();
                CurrentWord = Plan.Words.Count - 1;
                Status = ReadState.Finished;
                Changed?.Invoke();
            }
        }

        public void Dispose()
        {
            _gen++;
            StopPlayer();
            _tick.Dispose();
            try { _synth?.Dispose(); } catch { }
            _synth = null;
            Status = ReadState.Idle;
        }
    }
}
