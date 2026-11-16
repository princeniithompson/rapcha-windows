using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Reads a selection with Piper, one phrase at a time: the phrase is synthesized (the next one is prepared
    /// while this one plays), played through waveOut, and the spoken word is estimated from how much of the
    /// phrase's audio has played. Back/Forward and Play after Stop restart from a word index.
    /// </summary>
    public sealed class PiperReader : IReadAloud
    {
        private readonly IPcmPlayer _player;
        private readonly Timer _tick = new Timer { Interval = 30 };
        private PiperEngine _engine;
        private int _gen;
        private int _segStart, _segEnd;
        private bool _segPlaying;
        private int _nextStart = -1;
        private Task<WavData> _next;
        private bool _resumeFromWord; // paused after a skip: Play restarts from CurrentWord

        public ReadingPlan Plan { get; private set; }
        public int CurrentWord { get; private set; }
        public ReadState Status { get; private set; } = ReadState.Idle;
        public string VoiceName => PiperVoice.VoiceLabel;
        public int ProgressEvents { get; private set; }
        public int Segments { get; private set; }
        public long PcmBytes { get; private set; }

        public event Action Changed;
        /// <summary>Piper could not speak; the app switches to the Windows voice from this word.</summary>
        public event Action<string, int> Failed;

        public PiperReader(IPcmPlayer player = null)
        {
            _player = player ?? new WaveOutPlayer();
            _tick.Tick += (s, e) => OnTick();
        }

        private PiperEngine Engine()
        {
            double speed = PiperVoice.Speed;
            if (_engine != null && (!_engine.Running || Math.Abs(_engine.LengthScale - Math.Round(1.0 / speed, 2)) > 0.001))
            {
                _engine.Dispose();
                _engine = null;
                _next = null; _nextStart = -1;
            }
            if (_engine == null)
            {
                _engine = new PiperEngine();
                _engine.Start(speed);
            }
            return _engine;
        }

        public void Read(string text, int startWord = 0)
        {
            Plan = ReadingPlan.Create(text);
            ProgressEvents = 0;
            CurrentWord = 0;
            if (Plan.IsEmpty) { Stop(); return; }
            StartAt(Plan.Skip(0, startWord));
        }

        private async void StartAt(int word)
        {
            int gen = ++_gen;
            _player.Reset();
            _segPlaying = false;
            _resumeFromWord = false;
            CurrentWord = word;
            _segStart = word;
            _segEnd = Plan.SegmentEnd(word);
            if (Status != ReadState.Paused) Status = ReadState.Preparing;
            _tick.Start();
            Changed?.Invoke();

            WavData wav;
            try
            {
                var engine = Engine(); // may restart piper (speed changed): that drops a prepared phrase
                var task = _nextStart == word && _next != null ? _next : engine.SynthesizeAsync(Plan.TextRange(_segStart, _segEnd));
                _next = null; _nextStart = -1;
                wav = await task;
            }
            catch (Exception e)
            {
                if (gen != _gen) return;
                Fail("synth_failed " + e.GetType().Name + ": " + e.Message);
                return;
            }
            if (gen != _gen) return;
            if (!_player.Open(wav.SampleRate, out var err)) { Fail("no_audio_output " + err); return; }

            var pcm = WavData.TrimSilence(wav.Pcm, wav.SampleRate);
            Segments++;
            PcmBytes += pcm.Length;
            _player.Play(pcm);
            _segPlaying = true;
            if (Status == ReadState.Preparing) Status = ReadState.Playing;
            Changed?.Invoke();

            // Prepare the next phrase while this one plays.
            if (_segEnd < Plan.Words.Count)
            {
                _nextStart = _segEnd;
                try { _next = Engine().SynthesizeAsync(Plan.TextRange(_segEnd, Plan.SegmentEnd(_segEnd))); }
                catch { _next = null; _nextStart = -1; }
            }
        }

        private void OnTick()
        {
            if (!_segPlaying || Plan == null) return;
            int gen = _gen;
            int w = PhraseTiming.WordAt(Plan, _segStart, _segEnd, _player.Fraction);
            if (w != CurrentWord) { CurrentWord = w; ProgressEvents++; Changed?.Invoke(); }
            if (gen != _gen || !_segPlaying) return; // a button press restarted reading meanwhile
            if (!_player.Done || Status == ReadState.Paused) return;
            _segPlaying = false;
            if (_segEnd >= Plan.Words.Count)
            {
                _tick.Stop();
                CurrentWord = Plan.Words.Count - 1;
                Status = ReadState.Finished;
                Changed?.Invoke();
                return;
            }
            StartAt(_segEnd);
        }

        public void TogglePause()
        {
            if (Plan == null || Plan.IsEmpty) return;
            switch (Status)
            {
                case ReadState.Playing:
                case ReadState.Preparing:
                    _player.Pause();
                    Status = ReadState.Paused;
                    Changed?.Invoke();
                    break;
                case ReadState.Paused:
                    _player.Resume();
                    if (_resumeFromWord || !_segPlaying && _next == null && !_tick.Enabled)
                    {
                        Status = ReadState.Preparing;
                        StartAt(CurrentWord);
                    }
                    else
                    {
                        Status = _segPlaying ? ReadState.Playing : ReadState.Preparing;
                        Changed?.Invoke();
                    }
                    break;
                case ReadState.Finished:
                    Status = ReadState.Preparing;
                    StartAt(0);
                    break;
                default:
                    Status = ReadState.Preparing;
                    StartAt(CurrentWord);
                    break;
            }
        }

        public void Skip(int delta)
        {
            if (Plan == null || Plan.IsEmpty) return;
            int target = Plan.Skip(CurrentWord, delta);
            if (Status == ReadState.Paused)
            {
                _gen++;
                _player.Reset();
                _segPlaying = false;
                CurrentWord = target;
                _resumeFromWord = true;
                Changed?.Invoke();
                return;
            }
            if (Status == ReadState.Finished || Status == ReadState.Idle) Status = ReadState.Preparing;
            StartAt(target);
        }

        public void Stop()
        {
            _gen++;
            _tick.Stop();
            _player.Reset();
            _segPlaying = false;
            _next = null; _nextStart = -1;
            Status = ReadState.Idle;
            Changed?.Invoke();
        }

        private void Fail(string why)
        {
            AppStorage.Log("Piper reading failed: " + why);
            int at = CurrentWord;
            Stop();
            Failed?.Invoke(why, at);
        }

        public void Dispose()
        {
            Stop();
            _tick.Dispose();
            _engine?.Dispose();
            _engine = null;
            _player.Dispose();
        }
    }
}
