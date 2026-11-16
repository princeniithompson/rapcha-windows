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
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 30 };
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
        public event Action Changed;

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

        public void Read(string text, int startWord = 0)
        {
            Plan = ReadingPlan.Create(text);
            CurrentWord = 0;
            ProgressEvents = 0;
            if (Plan.IsEmpty) { Stop(); return; }
            _ = StartAt(Plan.Skip(0, startWord));
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
        }

        private async Task StartAt(int word)
        {
            int gen = ++_gen;
            StopPlayer();
            CurrentWord = word;
            _baseChar = Plan.Words[word].Start;
            Status = ReadState.Preparing;
            Changed?.Invoke();
            string text = Plan.TextFrom(word);
            byte[] wav;
            var cueMs = new List<double>();
            var cueChar = new List<int>();
            try
            {
                var synth = Synth();
                try { synth.Options.SpeakingRate = VoiceSettings.Speed; } catch { }   // Windows 10 1803+
                using (var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask())
                {
                    if (gen != _gen) return;
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
                    wav = new byte[(int)stream.Size];
                    using (var input = stream.GetInputStreamAt(0))
                    using (var reader = new DataReader(input))
                    {
                        await reader.LoadAsync((uint)stream.Size).AsTask();
                        reader.ReadBytes(wav);
                    }
                }
            }
            catch (Exception e)
            {
                if (gen != _gen) return;
                AppStorage.Log("OneCore voice failed: " + e.GetType().Name + " 0x" + e.HResult.ToString("X8"));
                Status = ReadState.Idle;
                Changed?.Invoke();
                return;
            }
            if (gen != _gen) return;

            try { _player = new WaveOutPlayer(wav, _silent); }
            catch (Exception e)
            {
                AppStorage.Log("OneCore playback failed: " + e.Message);
                Status = ReadState.Idle;
                Changed?.Invoke();
                return;
            }
            UsedWordCues = cueMs.Count > 0;
            LastCueCount = cueMs.Count;
            if (!UsedWordCues)
            {
                // No word marks from this voice: spread the audio over the words by their length.
                var words = Plan.Words.Skip(word).ToList();
                var starts = VoiceLabels.EstimateStarts(words.Select(w => w.Length).ToList(), _player.DurationMs);
                cueMs = starts.ToList();
                cueChar = words.Select(w => w.Start - _baseChar).ToList();
            }
            _cueMs = cueMs.ToArray();
            _cueChar = cueChar.ToArray();
            _player.Play();
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
            if (_player.Done)
            {
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
