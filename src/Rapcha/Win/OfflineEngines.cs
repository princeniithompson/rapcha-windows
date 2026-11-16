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
        public event Action<string> Preview;
        public string Summary => "engine=builtin phrases=" + _recognized + " hypotheses=" + _hypotheses + " bytes=" + _stream.Written;

        public BuiltInEngine(IList<string> vocabulary)
        {
            var info = BuiltInSpeech.Pick() ?? throw new InvalidOperationException("No Windows speech recogniser");
            _engine = new SpeechRecognitionEngine(info);
            _engine.LoadGrammar(new DictationGrammar { Name = "dictation" });
            var terms = (vocabulary ?? new List<string>()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
            if (terms.Length > 0)
            {
                // Custom Vocabulary: the recogniser may also hear any of your words on their own...
                try { _engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(terms)) { Culture = info.Culture }) { Name = "vocabulary" }); } catch { }
                // ...or inside a sentence (dictation, one of your words, dictation).
                try
                {
                    var gb = new GrammarBuilder { Culture = info.Culture };
                    gb.AppendDictation();
                    gb.Append(new Choices(terms));
                    gb.AppendDictation();
                    _engine.LoadGrammar(new Grammar(gb) { Name = "vocabulary-in-dictation" });
                }
                catch { }
            }
            _engine.SetInputToAudioStream(_stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
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

        public void Feed(byte[] pcm) { if (!_disposed && pcm != null && pcm.Length > 0) _stream.Write(pcm); }

        public async Task<string> FinishAsync()
        {
            _stream.Complete();
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
        private readonly OfflineChunker _chunker = new OfflineChunker();
        private readonly object _lock = new object();
        private readonly Task<IntPtr> _model;
        private readonly List<string> _parts = new List<string>();
        private readonly string _vocabPrompt;
        private readonly int _threads;
        private Task _work = Task.CompletedTask;
        private bool _disposed, _freed;
        private int _pieces, _skipped;
        private long _bytes, _transcribeMs;

        public SpeechEngine Kind => SpeechEngine.Whisper;
        public event Action<string> Preview;
        public WhisperModel Model { get; }
        public long LoadMs { get; private set; } = -1;
        public bool LoadFailed { get; private set; }
        public int PeakAudioSamples => _chunker.PeakSamples;
        public int Pieces => _pieces;
        public string Summary => "engine=whisper model=" + Model.Id + " load_ms=" + LoadMs + " pieces=" + _pieces + " skipped_quiet=" + _skipped +
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
            // A piece with no speech makes Whisper invent words ("Thank you."): skip it.
            double sum = 0; int peak = 0;
            foreach (var s in piece) { sum += (double)s * s; int a = Math.Abs((int)s); if (a > peak) peak = a; }
            double rms = Math.Sqrt(sum / Math.Max(1, piece.Length));
            if (rms < 120 && peak < 1500) { _skipped++; return; }
            string prev;
            lock (_parts) prev = _parts.Count > 0 ? _parts[_parts.Count - 1] : "";
            if (prev.Length > 150) prev = prev.Substring(prev.Length - 150);
            var prompt = (_vocabPrompt + (prev.Length > 0 ? (_vocabPrompt.Length > 0 ? ". " : "") + prev : "")).Trim();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = WhisperNative.Transcribe(ctx, OfflineChunker.ToFloat(piece), _threads, prompt);
            _transcribeMs += sw.ElapsedMilliseconds;
            text = Regex.Replace(Noise.Replace(text ?? "", " "), @"\s+", " ").Trim();
            _pieces++;
            if (text.Length == 0) return;
            string all;
            lock (_parts) { _parts.Add(text); all = string.Join(" ", _parts); }
            try { Preview?.Invoke(all); } catch { }
        }

        public async Task<string> FinishAsync()
        {
            Task work;
            lock (_lock)
            {
                var rest = _chunker.Flush();
                if (rest != null && rest.Length >= 16000 / 4) Queue(rest);
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
        public static IOfflineEngine Start(EngineChoice choice, IList<string> vocab, out string note)
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
                try { return new BuiltInEngine(vocab); }
                catch (Exception e) { AppStorage.Log("Offline: built-in speech start failed " + e.GetType().Name); }
            }
            return null;
        }
    }
}
