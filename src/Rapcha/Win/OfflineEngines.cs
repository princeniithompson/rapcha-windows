using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>An offline recogniser for one dictation: 16 kHz mono 16-bit PCM in, text out.</summary>
    public interface IOfflineEngine : IDisposable
    {
        SpeechEngine Kind { get; }
        /// <summary>Thread-safe; called on the recorder thread.</summary>
        void Feed(byte[] pcm);
        /// <summary>All text so far (raw, before clean-up).</summary>
        event Action<string> Preview;
        /// <summary>Ends the audio and returns all the raw text.</summary>
        Task<string> FinishAsync();
        string Summary { get; }
        /// <summary>v18: audio Rapcha holds for this engine (bytes), for the memory guard.</summary>
        long HeldAudioBytes { get; }
    }

    /// <summary>
    /// Built-in Windows speech: the desktop speech recogniser (SAPI, System.Speech in .NET 4.8) with free dictation.
    /// It runs entirely on the PC (no internet), is part of Windows 10/11 for languages with "Speech recognition"
    /// installed, and works in Windows S mode because it is Microsoft's own signed code. (The newer
    /// Windows.Media.SpeechRecognition dictation is not used: its free dictation needs Microsoft's online service.)
    /// </summary>
    public static class BuiltInSpeech
    {
        private static bool? _available;
        private static string _name = "";
        /// <summary>Self-test: pretend there is (true) or isn't (false) a recogniser.</summary>
        public static bool? Override;

        public const string SettingsUri = "ms-settings:speech";
        public const string LanguageUri = "ms-settings:regionlanguage";
        public const string Instructions =
            "Windows opens Speech settings. Under \"Manage voices\" or \"Speech language\", pick your language and download its speech pack " +
            "(or go to Time & Language > Language, choose your language, Options, and download Speech). Then come back to Rapcha.";

        public static bool Available
        {
            get
            {
                if (Override.HasValue) return Override.Value;
                if (!_available.HasValue) Refresh();
                return _available ?? false;
            }
        }

        public static string RecognizerName { get { var _ = Available; return _name; } }

        public static void Refresh()
        {
            try
            {
                var info = Pick();
                _available = info != null;
                _name = info?.Culture.DisplayName ?? "";
                AppStorage.Log("Built-in speech: recognizers=" + SpeechRecognitionEngine.InstalledRecognizers().Count + " chosen=" + (info?.Culture.Name ?? "none"));
            }
            catch (Exception e)
            {
                _available = false;
                _name = "";
                AppStorage.Log("Built-in speech: unavailable (" + e.GetType().Name + ")");
            }
        }

        /// <summary>Your Windows language if it has a recogniser, else English (US), else any English, else the first.</summary>
        public static RecognizerInfo Pick()
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            if (all.Count == 0) return null;
            var ui = CultureInfo.CurrentUICulture;
            return all.FirstOrDefault(r => r.Culture.Name == ui.Name) ??
                   all.FirstOrDefault(r => r.Culture.Name == "en-US") ??
                   all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en") ??
                   all[0];
        }
    }

    /// <summary>A stream SAPI reads from while the microphone writes into it; Read waits for audio until Complete.</summary>
    internal sealed class LiveAudioStream : Stream
    {
        private readonly Queue<byte[]> _q = new Queue<byte[]>();
        private byte[] _cur;
        private int _pos;
        private bool _done;
        private readonly object _lock = new object();
        public long Written { get; private set; }
        public long ReadBytes { get; private set; }
        public long Pending { get { lock (_lock) return Written - ReadBytes; } }

        public void Write(byte[] b)
        {
            lock (_lock) { if (_done) return; _q.Enqueue(b); Written += b.Length; Monitor.PulseAll(_lock); }
        }

        public void Complete() { lock (_lock) { _done = true; Monitor.PulseAll(_lock); } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int got = 0;
            lock (_lock)
            {
                while (got < count)
                {
                    if (_cur == null || _pos >= _cur.Length)
                    {
                        while (_q.Count == 0 && !_done) Monitor.Wait(_lock, 200);
                        if (_q.Count == 0) break;   // finished
                        _cur = _q.Dequeue(); _pos = 0;
                    }
                    int n = Math.Min(count - got, _cur.Length - _pos);
                    Buffer.BlockCopy(_cur, _pos, buffer, offset + got, n);
                    _pos += n; got += n;
                }
                ReadBytes += got;
            }
            return got;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => -1;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class BuiltInEngine : IOfflineEngine
    {
        private readonly SpeechRecognitionEngine _engine;
        private readonly LiveAudioStream _stream = new LiveAudioStream();
        private readonly List<string> _parts = new List<string>();
        private readonly TaskCompletionSource<bool> _completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _hypothesis = "";
        private int _recognized, _hypotheses;
        private bool _disposed;

        public SpeechEngine Kind => SpeechEngine.BuiltIn;
        public long HeldAudioBytes => _stream.Pending;
        public event Action<string> Preview;
        public string Summary
        {
            get
            {
                string problems;
                lock (_problems) problems = _problems.Count == 0 ? "none" : string.Join(",", _problems.Select(p => p.Key + ":" + p.Value));
                return "engine=builtin input=" + (OwnMicrophone ? "windows_default_mic" : "rapcha_audio") + " phrases=" + _recognized + " hypotheses=" + _hypotheses +
                       " bytes=" + _stream.Written + " max_gain=" + _gain.CurrentGain.ToString("0.0", CultureInfo.InvariantCulture) + " sapi_level_max=" + _maxLevel + " signal_problems=" + problems;
            }
        }

        /// <summary>v18: built-in speech listens to the Windows default microphone itself (true) or to Rapcha's audio (false).</summary>
        public bool OwnMicrophone { get; private set; }
        private readonly SpeechGain _gain = new SpeechGain();
        private readonly Dictionary<string, int> _problems = new Dictionary<string, int>();
        private int _maxLevel;

        /// <param name="useDefaultMicrophone">v18: let Windows speech open the Windows default microphone itself
        /// (SetInputToDefaultAudioDevice), the way Microsoft's own apps feed it. Rapcha's audio is used instead when you
        /// picked another microphone in Settings, when the dictation switched from Gemini part-way (the audio
        /// before the switch has to be replayed), or when Windows refuses.</param>
        public BuiltInEngine(IList<string> vocabulary, bool useDefaultMicrophone = false)
        {
            var info = BuiltInSpeech.Pick() ?? throw new InvalidOperationException("No Windows speech recogniser");
            _engine = new SpeechRecognitionEngine(info);
            _engine.LoadGrammar(new DictationGrammar { Name = "dictation" });
            var terms = (vocabulary ?? new List<string>()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
            if (terms.Length > 0)
            {
                // Custom Vocabulary: the recogniser may also hear any of your words on their own...
                // v18: a low weight, so a short sentence is never forced into one of your words ("9 minutes").
                try { _engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(terms)) { Culture = info.Culture }) { Name = "vocabulary", Weight = 0.2f, Priority = -1 }); } catch { }
                // ...or inside a sentence (dictation, one of your words, dictation).
                try
                {
                    var gb = new GrammarBuilder { Culture = info.Culture };
                    gb.AppendDictation();
                    gb.Append(new Choices(terms));
                    gb.AppendDictation();
                    _engine.LoadGrammar(new Grammar(gb) { Name = "vocabulary-in-dictation", Weight = 0.3f, Priority = -1 });
                }
                catch { }
            }
            if (useDefaultMicrophone)
            {
                try { _engine.SetInputToDefaultAudioDevice(); OwnMicrophone = true; }
                catch (Exception e) { AppStorage.Log("Built-in speech: default microphone refused (" + e.GetType().Name + "), using Rapcha's audio"); }
            }
            if (!OwnMicrophone)
                _engine.SetInputToAudioStream(_stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _engine.AudioSignalProblemOccurred += (s, e) => { lock (_problems) { var k = e.AudioSignalProblem.ToString(); _problems[k] = (_problems.TryGetValue(k, out var n) ? n : 0) + 1; } };
            _engine.AudioLevelUpdated += (s, e) => { if (e.AudioLevel > _maxLevel) _maxLevel = e.AudioLevel; };
            _engine.SpeechRecognized += (s, e) =>
            {
                lock (_parts) { _parts.Add(e.Result.Text); _hypothesis = ""; _recognized++; }
                Raise();
            };
            _engine.SpeechHypothesized += (s, e) =>
            {
                lock (_parts) { _hypothesis = e.Result.Text; _hypotheses++; }
                Raise();
            };
            _engine.RecognizeCompleted += (s, e) => _completed.TrySetResult(true);
            _engine.RecognizeAsync(RecognizeMode.Multiple);
        }

        private string Text(bool withHypothesis)
        {
            lock (_parts)
            {
                var all = string.Join(" ", _parts);
                if (withHypothesis && _hypothesis.Length > 0) all = (all + " " + _hypothesis).Trim();
                return all;
            }
        }

        private void Raise() { try { Preview?.Invoke(Text(true)); } catch { } }

        /// <summary>Rapcha's audio, with the same light gain Gemini gets (quiet laptop microphones). Ignored when Windows
        /// speech listens to the microphone itself.</summary>
        public void Feed(byte[] pcm)
        {
            if (_disposed || pcm == null || pcm.Length == 0 || OwnMicrophone) return;
            _stream.Write(_gain.Process(pcm, true));
        }

        public async Task<string> FinishAsync()
        {
            _stream.Complete();
            if (OwnMicrophone) { try { _engine.RecognizeAsyncStop(); } catch { } }   // finish the phrase in progress, then stop
            var done = await Task.WhenAny(_completed.Task, Task.Delay(8000)).ConfigureAwait(false);
            if (done != _completed.Task) { try { _engine.RecognizeAsyncCancel(); } catch { } }
            return Text(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Complete();
            try { _engine.RecognizeAsyncCancel(); } catch { }
            try { _engine.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Whisper (whisper.cpp) on this laptop: the model is loaded only while you talk and freed right after. Audio is
    /// cut into ~30 s pieces at pauses; each piece is turned into text while you keep talking, so only one piece of
    /// audio is ever held. Your Custom Vocabulary and the previous piece's words are given to Whisper as its prompt.
    /// </summary>
    public sealed class WhisperEngine : IOfflineEngine
    {
        /// <summary>v18: pieces overlap by one second; the repeated words are removed when the texts are joined.</summary>
        public const int OverlapMs = 1000;
        private readonly OfflineChunker _chunker = new OfflineChunker(overlapMs: OverlapMs);
        private readonly object _lock = new object();
        private readonly Task<IntPtr> _model;
        private readonly List<string> _parts = new List<string>();
        private readonly string _vocabPrompt;
        private readonly int _threads;
        private Task _work = Task.CompletedTask;
        private bool _disposed, _freed;
        private int _pieces, _skipped, _inventions;
        private double _maxGain = 1;
        private long _bytes, _transcribeMs;

        public SpeechEngine Kind => SpeechEngine.Whisper;
        public event Action<string> Preview;
        public WhisperModel Model { get; }
        public long LoadMs { get; private set; } = -1;
        public bool LoadFailed { get; private set; }
        public int PeakAudioSamples => _chunker.PeakSamples;
        public int Pieces => _pieces;
        public string Summary => "engine=whisper model=" + Model.Id + " load_ms=" + LoadMs + " pieces=" + _pieces + " skipped_no_speech=" + _skipped + " dropped_inventions=" + _inventions + " max_gain=" + _maxGain.ToString("0.0", CultureInfo.InvariantCulture) +
                                 " transcribe_ms=" + _transcribeMs + " bytes=" + _bytes + " peak_audio_s=" + (_chunker.PeakSamples / 16000);

        public WhisperEngine(WhisperModel model, IList<string> vocabulary, int cores)
        {
            Model = model;
            _threads = Math.Max(1, Math.Min(cores - (cores > 2 ? 1 : 0), 6));
            var terms = (vocabulary ?? new List<string>()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).ToList();
            var p = string.Join(", ", terms);
            _vocabPrompt = p.Length > 300 ? p.Substring(0, 300) : p;
            var path = ModelStore.PathOf(model);
            _model = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ctx = WhisperNative.LoadModel(path, _threads);
                LoadMs = sw.ElapsedMilliseconds;
                if (ctx == IntPtr.Zero) LoadFailed = true;
                return ctx;
            });
        }

        /// <summary>Waits until the model is in memory (true) or failed to load (false).</summary>
        public bool WaitLoaded(int timeoutMs) => _model.Wait(timeoutMs) && _model.Result != IntPtr.Zero;

        public void Feed(byte[] pcm)
        {
            if (_disposed || pcm == null) return;
            lock (_lock)
            {
                _bytes += pcm.Length;
                foreach (var piece in _chunker.Add(pcm)) Queue(piece);
            }
        }

        private void Queue(short[] piece)
        {
            _work = _work.ContinueWith(_ => Transcribe(piece), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private static readonly Regex Noise = new Regex(@"\[[^\]]*\]|\((?:[^)]*(?:music|silence|blank|applause|laugh|noise|inaudible)[^)]*)\)|\*[^*]*\*", RegexOptions.IgnoreCase);

        private void Transcribe(short[] piece)
        {
            if (_disposed) return;
            var ctx = _model.Result;
            if (ctx == IntPtr.Zero) return;
            // v18: only the speech goes to Whisper. A piece with no speech (silence, hum, a fan) makes Whisper invent
            // words ("Thank you.", "99 V. Lanes"), so it is skipped; quiet speech is brought up to a normal level.
            var speech = SpeechVad.Trim(piece, out var vad);
            if (speech == null) { _skipped++; return; }
            speech = SpeechVad.Normalize(speech, vad.SpeechRms, out var gain);
            if (gain > _maxGain) _maxGain = gain;
            speech = SpeechVad.PadForWhisper(speech);
            // The prompt is only your Custom Vocabulary (spellings). v18: the previous piece's words are no longer
            // given, because a wrong piece then pulls the next one off course too.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = WhisperNative.Transcribe(ctx, OfflineChunker.ToFloat(speech), _threads, _vocabPrompt);
            _transcribeMs += sw.ElapsedMilliseconds;
            text = Regex.Replace(Noise.Replace(text ?? "", " "), @"\s+", " ").Trim();
            _pieces++;
            if (WhisperText.IsLikelyInvention(text, vad.SpeechSeconds)) { if (text.Length > 0) _inventions++; return; }
            string all;
            lock (_parts)
            {
                var prev = _parts.Count > 0 ? _parts[_parts.Count - 1] : "";
                text = WhisperText.Dedupe(prev, text);   // the 1 s overlap with the previous piece
                if (text.Length == 0) return;
                _parts.Add(text);
                all = string.Join(" ", _parts);
            }
            try { Preview?.Invoke(all); } catch { }
        }

        /// <summary>Audio waiting in the chunker (bytes), for the memory guard.</summary>
        public long HeldAudioBytes { get { lock (_lock) return _chunker.BufferedSamples * 2L; } }

        public async Task<string> FinishAsync()
        {
            Task work;
            lock (_lock)
            {
                var rest = _chunker.Flush();
                if (rest != null && rest.Length >= 16000 / 4) Queue(rest);   // short ends are padded for Whisper
                work = _work;
            }
            try { await work.ConfigureAwait(false); } catch { }
            FreeModel();
            lock (_parts) return string.Join(" ", _parts);
        }

        private void FreeModel()
        {
            lock (_lock)
            {
                if (_freed) return;
                _freed = true;
            }
            try { var ctx = _model.Result; if (ctx != IntPtr.Zero) WhisperNative.FreeModel(ctx); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var work = _work;
            Task.Run(async () => { try { await work.ConfigureAwait(false); } catch { } try { await _model.ConfigureAwait(false); } catch { } FreeModel(); });
        }
    }

    /// <summary>Picks and starts the offline recogniser for this laptop.</summary>
    public static class OfflineEngines
    {
        /// <summary>What Rapcha knows right now about offline choices (cheap).</summary>
        public static EngineInputs Inputs(bool online)
        {
            var tier = DeviceProbe.Tier;
            var model = WhisperModels.Choose(tier, OfflineSettings.ChosenModel);
            var st = WhisperNative.Status;
            return new EngineInputs
            {
                Online = online,
                OfflineFallback = OfflineSettings.Fallback,
                Tier = tier,
                WhisperModelReady = model != null && ModelStore.IsInstalled(model),
                WhisperBlocked = st == WhisperNative.State.Blocked || st == WhisperNative.State.NotBundled || st == WhisperNative.State.Failed ||
                                 WhisperNative.SimulateBlocked || !WhisperNative.IsBundled,
                WhisperFitsMemory = model != null && WhisperModels.FitsInFreeMemory(model, DeviceProbe.FreeRamMb()),
                BuiltInAvailable = BuiltInSpeech.Available,
            };
        }

        /// <summary>Starts the chosen offline engine; falls back from Whisper to built-in if Whisper can't load here.</summary>
        public static IOfflineEngine Start(EngineChoice choice, IList<string> vocab, out string note, bool builtInOwnMic = false)
        {
            note = "";
            if (choice.Ok && choice.Engine == SpeechEngine.Whisper)
            {
                var state = WhisperNative.EnsureLoaded();
                if (state == WhisperNative.State.Ready)
                {
                    try { return new WhisperEngine(WhisperModels.Choose(DeviceProbe.Tier, OfflineSettings.ChosenModel), vocab, DeviceProbe.Facts.Cores); }
                    catch (Exception e) { AppStorage.Log("Offline: Whisper start failed " + e.GetType().Name); }
                }
                if (state == WhisperNative.State.Blocked) note = WhisperNative.BlockedMessage;
                if (!BuiltInSpeech.Available) return null;
                choice = new EngineChoice { Ok = true, Engine = SpeechEngine.BuiltIn };
            }
            if (choice.Ok && choice.Engine == SpeechEngine.BuiltIn)
            {
                try { return new BuiltInEngine(vocab, builtInOwnMic); }
                catch (Exception e) { AppStorage.Log("Offline: built-in speech start failed " + e.GetType().Name); }
            }
            return null;
        }
    }
}
