using System;
using System.IO;
using System.Speech.Synthesis;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Reads a selection aloud with the built-in offline Windows voices (System.Speech / SAPI).
    /// Tracks the spoken word with SpeakProgress so the card can highlight the phrase and word.
    /// Created on the UI thread so its events arrive on the UI thread.
    /// </summary>
    public sealed class SpeechReader : IReadAloud
    {
        private SpeechSynthesizer _synth;
        private Prompt _prompt;
        private int _baseChar;
        private readonly bool _silentOutput;
        private readonly string _voiceName;

        public ReadingPlan Plan { get; private set; }
        public int CurrentWord { get; private set; }
        public ReadState Status { get; private set; } = ReadState.Idle;
        public int ProgressEvents { get; private set; }
        public string VoiceName { get; private set; } = "";
        public DateTime AudioStartedAt { get; private set; } = DateTime.MinValue;
        private bool _primed;

        public event Action Changed;

        /// <param name="silentOutput">Self-test only: speak into memory instead of the speakers.</param>
        /// <param name="voiceName">An installed SAPI voice; null or missing = the Windows default.</param>
        public SpeechReader(bool silentOutput = false, string voiceName = null) { _silentOutput = silentOutput; _voiceName = voiceName; }

        public static int CountVoices()
        {
            try { using (var s = new SpeechSynthesizer()) return s.GetInstalledVoices().Count; } catch { return 0; }
        }

        /// <summary>
        /// Self-test only: the silent output takes the audio at the speakers' pace (instead of as fast as the voice
        /// can make it), so a reading lasts as long as it would out loud and what happens while it plays can be seen.
        /// </summary>
        public static bool PaceSilent;

        private sealed class PacedSink : Stream
        {
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private double _dueMs;   // when the audio written so far would have finished playing
            public override void Write(byte[] buffer, int offset, int count)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                if (_dueMs < now) _dueMs = now;   // a new reading starts now
                _dueMs += count / 32.0;           // 16 kHz, 16-bit, mono: 32 bytes a millisecond
                int wait = (int)(_dueMs - now) - 60;
                if (wait > 0) System.Threading.Thread.Sleep(wait);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
        }

        private SpeechSynthesizer Synth()
        {
            if (_synth != null) return _synth;
            _synth = new SpeechSynthesizer();
            if (_silentOutput && PaceSilent)
                _synth.SetOutputToAudioStream(new PacedSink(), new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            else if (_silentOutput) _synth.SetOutputToWaveStream(new MemoryStream());
            else _synth.SetOutputToDefaultAudioDevice();
            if (!string.IsNullOrEmpty(_voiceName))
            {
                try { _synth.SelectVoice(_voiceName); }
                catch { AppStorage.Log("Voices: the chosen desktop voice is not installed any more; using the default"); }
            }
            VoiceName = _synth.Voice?.Name ?? "";
            _synth.Rate = VoiceSettings.SapiRate;
            _synth.SpeakProgress += OnProgress;
            _synth.SpeakCompleted += OnCompleted;
            return _synth;
        }

        /// <summary>Creates the synthesizer and opens the output with a silent 1 ms prompt (loads the voice too).</summary>
        public void Prime()
        {
            var synth = Synth();
            if (_primed) return;
            _primed = true;
            try
            {
                var pb = new PromptBuilder();
                pb.AppendBreak(TimeSpan.FromMilliseconds(1));
                synth.SpeakAsync(new Prompt(pb));   // not _prompt: its events are ignored
            }
            catch (Exception e) { AppStorage.Log("Voice prime failed: " + e.GetType().Name); }
        }

        /// <summary>SAPI starts speaking within a few ms once primed: nothing to prepare ahead.</summary>
        public void Prepare(string text) { }

        /// <summary>Replaces whatever is being read with new text, from its first word.</summary>
        public void Read(string text, int startWord = 0)
        {
            Plan = ReadingPlan.Create(text);
            CurrentWord = 0;
            ProgressEvents = 0;
            if (Plan.IsEmpty) { Stop(); return; }
            StartAt(Plan.Skip(0, startWord));
        }

        public void TogglePause()
        {
            if (Plan == null || Plan.IsEmpty) return;
            if (Status == ReadState.Playing) { Synth().Pause(); Status = ReadState.Paused; Changed?.Invoke(); }
            else if (Status == ReadState.Paused)
            {
                if (_prompt != null) { Synth().Resume(); Status = ReadState.Playing; Changed?.Invoke(); }
                else StartAt(CurrentWord);
            }
            else StartAt(Status == ReadState.Finished ? 0 : CurrentWord);
        }

        public void Skip(int delta)
        {
            if (Plan == null || Plan.IsEmpty) return;
            int target = Plan.Skip(CurrentWord, delta);
            if (Status == ReadState.Paused)
            {
                CancelPrompt();
                CurrentWord = target;
                Changed?.Invoke(); // stays paused at the new place; Play continues from there
                return;
            }
            StartAt(target);
        }

        public void JumpTo(int word)
        {
            if (Plan == null || Plan.IsEmpty) return;
            Skip(Math.Max(0, Math.Min(Plan.Words.Count - 1, word)) - CurrentWord);
        }

        /// <summary>SAPI starts speaking within a few tens of milliseconds: nothing to prepare.</summary>
        public void PrepareAt(int word) { }

        public void Stop()
        {
            CancelPrompt();
            Status = ReadState.Idle;
            Changed?.Invoke();
        }

        private void StartAt(int word)
        {
            CancelPrompt();
            CurrentWord = word;
            _baseChar = Plan.Words[word].Start;
            AudioStartedAt = DateTime.MinValue;
            var synth = Synth();
            synth.Rate = VoiceSettings.SapiRate;
            _prompt = synth.SpeakAsync(Plan.TextFrom(word));
            Status = ReadState.Playing;
            Changed?.Invoke();
        }

        private void CancelPrompt()
        {
            if (_synth == null || _prompt == null) { _prompt = null; return; }
            var p = _prompt;
            _prompt = null;
            try
            {
                if (_synth.State == SynthesizerState.Paused) _synth.Resume(); // cancelling while paused can hang
                _synth.SpeakAsyncCancel(p);
            }
            catch { }
        }

        private void OnProgress(object sender, SpeakProgressEventArgs e)
        {
            if (e.Prompt != _prompt || Plan == null) return;
            if (AudioStartedAt == DateTime.MinValue) AudioStartedAt = DateTime.UtcNow; // the first word is being spoken
            ProgressEvents++;
            CurrentWord = Plan.WordAtChar(_baseChar + e.CharacterPosition);
            Changed?.Invoke();
        }

        private void OnCompleted(object sender, SpeakCompletedEventArgs e)
        {
            if (e.Prompt != _prompt) return;
            _prompt = null;
            if (e.Cancelled) return;
            if (e.Error != null) AppStorage.Log("Speech error: " + e.Error.Message);
            if (Plan != null && !Plan.IsEmpty) CurrentWord = Plan.Words.Count - 1;
            Status = ReadState.Finished;
            Changed?.Invoke();
        }

        public void Dispose()
        {
            CancelPrompt();
            if (_synth != null)
            {
                _synth.SpeakProgress -= OnProgress;
                _synth.SpeakCompleted -= OnCompleted;
                try { _synth.Dispose(); } catch { }
                _synth = null;
            }
            Status = ReadState.Idle;
        }
    }
}
