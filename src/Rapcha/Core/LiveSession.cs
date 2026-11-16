using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rapcha.Core
{
    public enum LiveErrorKind { MissingKey, BadKey, NoInternet, Server, ConnectionLost }

    public sealed class LiveError
    {
        public LiveErrorKind Kind;
        public string Detail;
        public LiveError(LiveErrorKind kind, string detail) { Kind = kind; Detail = detail ?? ""; }
        public override string ToString() => Kind + ": " + Detail;
    }

    /// <summary>
    /// One dictation over ONE Gemini Live WebSocket, mirroring the Android flow
    /// (GeminiLiveWebSocketClient.kt + FloatingDictationSessionManager.kt):
    /// connect with x-goog-api-key, send setup, wait for setupComplete, send activityStart,
    /// stream 100 ms PCM chunks in capture order (held while connecting, max 150), and on Complete
    /// send activityEnd and wait up to 8 s for the authoritative inputTranscription.
    /// audioStreamEnd is never sent (manual VAD).
    /// </summary>
    public sealed class LiveSession : IDisposable
    {
        public const int MaxAudioQueueChunks = 150; // FloatingDictationSessionManager.MAX_AUDIO_QUEUE_CHUNKS
        public const int SetupWaitOnFinishMs = 10000;

        private readonly string _apiKey;
        private readonly string _model;
        private readonly bool _smartMode;
        private readonly List<string> _vocabulary;
        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _queueLock = new object();
        private readonly Queue<byte[]> _audioQueue = new Queue<byte[]>();
        private readonly SpeechGain _gain = new SpeechGain();
        private readonly LiveTurnState _turn;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private string _lastPreview = "";
        private readonly TaskCompletionSource<bool> _setupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _finalTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _pump = Task.CompletedTask;
        private bool _pumping;
        private volatile bool _setupComplete;
        private volatile bool _activityStarted;
        private volatile bool _closing;
        private int _failedRaised;

        /// <summary>Raised once, on a thread-pool thread.</summary>
        public event Action<LiveError> Failed;
        /// <summary>Gemini says this connection ends soon (Live API GoAway): finish now and keep the words.</summary>
        public event Action GoAway;
        public event Action<string> Log;

        /// <summary>Live words for the Words bubble (finalized + interim preview), on a thread-pool thread.</summary>
        public event Action<string> Preview;

        // Diagnostics (no words).
        public int ChunksSent { get; private set; }
        public long SetupMs { get; private set; } = -1;
        public double MaxGain { get; private set; } = 1.0;
        public string FinishReason { get; private set; } = "";

        public bool IsSetupComplete => _setupComplete;

        /// <summary>The Live endpoint. Only tests change this (to a local fake server).</summary>
        public Uri Endpoint { get; set; } = new Uri(LiveProtocol.WsBaseUrl);
        public LiveTurnState Turn => _turn;

        public LiveSession(string apiKey, IList<string> vocabulary, string model = LiveProtocol.DefaultModel, bool smartMode = true,
            SilenceLevels levels = null)
        {
            _turn = new LiveTurnState(levels ?? SilenceLevels.Android);
            _apiKey = (apiKey ?? "").Trim();
            _model = string.IsNullOrWhiteSpace(model) ? LiveProtocol.DefaultModel : model.Trim();
            _smartMode = smartMode;
            _vocabulary = new List<string>(vocabulary ?? new List<string>());
        }

        public static bool IsPlaceholder(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return true;
            var u = apiKey.Trim().ToUpperInvariant();
            return u == "MY_GEMINI_API_KEY" || u.Contains("PLACEHOLDER") || u.Contains("YOUR_") || u.Contains("MY_");
        }

        public async Task ConnectAsync()
        {
            if (IsPlaceholder(_apiKey)) { RaiseFailed(new LiveError(LiveErrorKind.MissingKey, "No API key")); return; }
            try
            {
                _ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(LiveProtocol.PingIntervalSeconds);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await _ws.ConnectAsync(Endpoint, connectTimeout.Token).ConfigureAwait(false);
                }
                LogLine("WebSocket open");
                await SendTextAsync(LiveProtocol.BuildSetupMessage(_model, _smartMode, _vocabulary)).ConfigureAwait(false);
                LogLine("Setup sent (SMART=" + _smartMode + ", vocab=" + _vocabulary.Count + ")");
                _ = Task.Run(ReceiveLoop);
            }
            catch (Exception e)
            {
                if (_closing) return;
                RaiseFailed(ClassifyConnectError(e));
            }
        }

        /// <summary>Thread-safe. Chunks go out strictly in capture order.</summary>
        public void SendAudio(byte[] rawPcm)
        {
            if (_closing || _turn.ActivityEnded) return;
            lock (_queueLock)
            {
                _audioQueue.Enqueue(rawPcm);
                while (_audioQueue.Count > MaxAudioQueueChunks) _audioQueue.Dequeue();
                StartPumpLocked();
            }
        }

        /// <summary>
        /// Complete: flush queued audio, send activityEnd and wait for the final transcript.
        /// Returns the accepted text ("" when nothing usable was heard).
        /// </summary>
        public async Task<string> FinishAsync()
        {
            if (!_setupComplete)
            {
                var done = await Task.WhenAny(_setupTcs.Task, Task.Delay(SetupWaitOnFinishMs)).ConfigureAwait(false);
                if (done != _setupTcs.Task || !_setupComplete)
                {
                    FinishReason = "setup_not_complete";
                    return SettleLocked(_turn.LastInterimText, true, _vocabulary);
                }
            }
            // Drain every queued chunk, in order, before activityEnd.
            for (int guard = 0; guard < 1000; guard++)
            {
                if (_closing || _ws.State != WebSocketState.Open) break;
                Task pump;
                lock (_queueLock)
                {
                    StartPumpLocked();
                    pump = _pump;
                    if (!_pumping && _audioQueue.Count == 0) break;
                }
                await pump.ConfigureAwait(false);
                if (_closing) break;
            }

            if (_turn.RmsSamples == 0)
            {
                FinishReason = "no_audio_sent";
                _turn.MarkActivityEnded();
                return SettleLocked("", false, _vocabulary);
            }
            if (_turn.IsNearSilent)
            {
                FinishReason = "near_silent";
                _turn.MarkActivityEnded();
                return SettleLocked("", false, _vocabulary);
            }
            _turn.MarkActivityEnded();
            try
            {
                await SendTextAsync(LiveProtocol.ActivityEndMessage).ConfigureAwait(false);
                LogLine("Sent activityEnd");
            }
            catch (Exception)
            {
                FinishReason = "activity_end_send_failed";
                return SettleLocked(_turn.LastInterimText, true, _vocabulary);
            }
            var finished = await Task.WhenAny(_finalTcs.Task, Task.Delay(LiveProtocol.FinalTranscriptTimeoutMs)).ConfigureAwait(false);
            if (finished == _finalTcs.Task)
            {
                if (FinishReason.Length == 0) FinishReason = "final_received";
                return _finalTcs.Task.Result;
            }
            FinishReason = "final_timeout";
            LogLine("Final transcript timed out; using last interim");
            return SettleLocked(_turn.LastInterimText, true, _vocabulary);
        }

        public void Cancel() => Dispose();

        public void Dispose()
        {
            if (_closing) return;
            _closing = true;
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    var closeTask = _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                    closeTask.Wait(1500);
                }
            }
            catch { }
            try { _cts.Cancel(); } catch { }
            try { _ws.Abort(); } catch { }
            try { _ws.Dispose(); } catch { }
        }

        private void StartPumpLocked()
        {
            if (!_setupComplete || _pumping || _closing) return;
            if (_activityStarted && _audioQueue.Count == 0) return;
            _pumping = true;
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                // activityStart opens the manual activity right after setupComplete, before any audio.
                if (!_activityStarted && !_turn.ActivityEnded)
                {
                    await SendTextAsync(LiveProtocol.ActivityStartMessage).ConfigureAwait(false);
                    _activityStarted = true;
                    LogLine("Sent activityStart");
                }
                while (!_closing)
                {
                    byte[] chunk;
                    lock (_queueLock)
                    {
                        if (_audioQueue.Count == 0) { _pumping = false; return; }
                        chunk = _audioQueue.Dequeue();
                    }
                    var output = _gain.Process(chunk, true);
                    await SendTextAsync(LiveProtocol.BuildAudioMessage(output, 0, output.Length)).ConfigureAwait(false);
                    lock (_turn) _turn.OnAudioSent(chunk);
                    ChunksSent++;
                    if (_gain.CurrentGain > MaxGain) MaxGain = _gain.CurrentGain;
                }
            }
            catch (Exception e)
            {
                if (!_closing) LogLine("Audio send failed: " + e.Message);
            }
            finally
            {
                lock (_queueLock) _pumping = false;
            }
        }

        private async Task SendTextAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!_closing && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnRemoteClosed((int?)_ws.CloseStatus ?? 0, _ws.CloseStatusDescription ?? "");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    HandleMessage(text);
                }
            }
            catch (Exception e)
            {
                if (_closing) return;
                OnRemoteClosed(0, e.Message);
            }
        }

        private void OnRemoteClosed(int code, string reason)
        {
            LogLine("WebSocket closed (code " + code + (string.IsNullOrEmpty(reason) ? "" : ", " + (reason.Length > 120 ? reason.Substring(0, 120) : reason)) + ")");
            if (_closing) return;
            if (_turn.ActivityEnded)
            {
                // Complete was already pressed: use what we have instead of reporting an error.
                if (!_finalTcs.Task.IsCompleted)
                {
                    FinishReason = "socket_closed_after_end";
                    _finalTcs.TrySetResult(SettleLocked(_turn.LastInterimText, true, _vocabulary));
                }
                return;
            }
            if (LiveProtocol.IsApiKeyError(0, reason))
                RaiseFailed(new LiveError(LiveErrorKind.BadKey, reason));
            else if (!_setupComplete && code != 0)
                RaiseFailed(new LiveError(LiveErrorKind.Server, "Closed (" + code + "): " + reason));
            else
                RaiseFailed(new LiveError(LiveErrorKind.ConnectionLost, reason));
        }

        private void HandleMessage(string raw)
        {
            ServerMessage m;
            try { m = LiveProtocol.Parse(raw); }
            catch (Exception e) { LogLine("JSON parse error: " + e.Message); return; }

            if (m.SetupComplete)
            {
                _setupComplete = true;
                SetupMs = _clock.ElapsedMilliseconds;
                LogLine("setupComplete after " + SetupMs + " ms");
                _setupTcs.TrySetResult(true);
                lock (_queueLock) StartPumpLocked();
                return;
            }
            if (m.HasError)
            {
                var desc = m.ErrorDescription;
                LogLine(desc);
                bool keyError = LiveProtocol.IsApiKeyError(m.ErrorCode, m.ErrorStatus + " " + m.ErrorMessage);
                if (_turn.ActivityEnded) _finalTcs.TrySetResult(SettleLocked(_turn.LastInterimText, true, _vocabulary));
                RaiseFailed(new LiveError(keyError ? LiveErrorKind.BadKey : LiveErrorKind.Server, desc));
                return;
            }
            if (m.GoAway)
            {
                LogLine("goAway received (the connection will end soon)");
                try { GoAway?.Invoke(); } catch { }
            }
            if (!m.HasServerContent) return;
            string preview;
            lock (_turn)
            {
                var completed = _turn.OnServerContent(m);
                if (completed != null && !_finalTcs.Task.IsCompleted)
                    _finalTcs.TrySetResult(SettleLocked(completed, false, _vocabulary));
                preview = _turn.LastInterimText;
            }
            if (!string.IsNullOrEmpty(preview) && preview != _lastPreview)
            {
                _lastPreview = preview;
                try { Preview?.Invoke(preview); } catch { }
            }
        }

        private string SettleLocked(string text, bool timedOut, IList<string> vocabulary)
        {
            lock (_turn) return _turn.Settle(text, timedOut, vocabulary);
        }

        private void RaiseFailed(LiveError error)
        {
            if (Interlocked.Exchange(ref _failedRaised, 1) != 0) return;
            LogLine("Failed: " + error.Kind);
            _setupTcs.TrySetResult(false);
            try { Failed?.Invoke(error); } catch { }
        }

        private void LogLine(string s) { try { Log?.Invoke(s); } catch { } }

        public static LiveError ClassifyConnectError(Exception e)
        {
            var all = new StringBuilder();
            for (var x = e; x != null; x = x.InnerException)
            {
                all.Append(x.Message).Append(" | ");
                if (x is WebException we)
                {
                    if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure ||
                        we.Status == WebExceptionStatus.Timeout || we.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                        return new LiveError(LiveErrorKind.NoInternet, x.Message);
                    if (we.Response is HttpWebResponse resp && ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403))
                        return new LiveError(LiveErrorKind.BadKey, x.Message);
                }
                if (x is SocketException || x is OperationCanceledException)
                    return new LiveError(LiveErrorKind.NoInternet, x.Message);
            }
            var text = all.ToString();
            if (text.Contains("401") || text.Contains("403") || LiveProtocol.IsApiKeyError(0, text))
                return new LiveError(LiveErrorKind.BadKey, text);
            var lower = text.ToLowerInvariant();
            if (lower.Contains("remote name could not be resolved") || lower.Contains("unable to connect") ||
                lower.Contains("name or service not known") || lower.Contains("no such host") || lower.Contains("network"))
                return new LiveError(LiveErrorKind.NoInternet, text);
            return new LiveError(LiveErrorKind.Server, text);
        }
    }
}
