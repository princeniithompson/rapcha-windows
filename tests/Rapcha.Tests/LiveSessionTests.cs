using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    /// <summary>Runs the real LiveSession code against a local fake Gemini Live server.</summary>
    public class LiveSessionTests
    {
        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static byte[] Tone(int amplitude)
        {
            var b = new byte[3200];
            for (int i = 0; i < 1600; i++)
            {
                short s = (short)(amplitude * Math.Sin(i * 2 * Math.PI * 220 / 16000));
                b[i * 2] = (byte)s; b[i * 2 + 1] = (byte)(s >> 8);
            }
            return b;
        }

        private static async Task<string> Receive(WebSocket ws)
        {
            var buf = new byte[1 << 20];
            int total = 0;
            while (true)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                total += r.Count;
                if (r.EndOfMessage) return Encoding.UTF8.GetString(buf, 0, total);
            }
        }

        private static Task Send(WebSocket ws, string text) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);

        private sealed class FakeServer : IDisposable
        {
            public readonly HttpListener Listener = new HttpListener();
            public readonly List<string> Received = new List<string>();
            public string ApiKeyHeader;
            public Uri Uri;

            public FakeServer()
            {
                int port = FreePort();
                Listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                Listener.Start();
                Uri = new Uri($"ws://127.0.0.1:{port}/");
            }

            public async Task<WebSocket> Accept()
            {
                var ctx = await Listener.GetContextAsync();
                ApiKeyHeader = ctx.Request.Headers["x-goog-api-key"];
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                return wsCtx.WebSocket;
            }

            public void Dispose() { try { Listener.Stop(); } catch { } }
        }

        [Fact]
        public async Task FullDictationFlowMatchesAndroidOrder()
        {
            using var server = new FakeServer();
            var serverTask = Task.Run(async () =>
            {
                var ws = await server.Accept();
                string msg;
                while ((msg = await Receive(ws)) != null)
                {
                    lock (server.Received) server.Received.Add(msg);
                    if (msg.Contains("\"setup\"")) await Send(ws, "{\"setupComplete\":{}}");
                    if (msg.Contains("activityEnd"))
                    {
                        await Send(ws, "{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"Hello wor\"}}}");
                        await Send(ws, "{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hello world, Prince Nii.\"}}}");
                        await Send(ws, "{\"serverContent\":{\"turnComplete\":true}}");
                    }
                }
            });

            var session = new LiveSession("AIzaTestKey123", new List<string> { "Prince Nii" }) { Endpoint = server.Uri };
            LiveError failure = null;
            session.Failed += e => failure = e;
            // Audio captured before the socket is ready must be held and sent in order.
            for (int i = 0; i < 5; i++) session.SendAudio(Tone(5000));
            await session.ConnectAsync();
            for (int i = 0; i < 10; i++) { session.SendAudio(Tone(5000)); await Task.Delay(5); }
            var text = await session.FinishAsync();
            session.Dispose();

            Assert.Null(failure);
            Assert.Equal("Hello world, Prince Nii.", text);
            Assert.Equal("AIzaTestKey123", server.ApiKeyHeader);
            List<string> got;
            lock (server.Received) got = server.Received.ToList();
            Assert.StartsWith("{\"setup\":{\"model\":\"models/gemini-3.5-transcribe-live\"", got[0]);
            Assert.Contains("\"mode\":\"SMART\"", got[0]);
            Assert.Contains("\"customVocabulary\":[\"Prince Nii\"]", got[0]);
            Assert.Equal(LiveProtocol.ActivityStartMessage, got[1]);
            Assert.Equal(15, got.Count(m => m.Contains("\"audio\"")));
            Assert.Equal(LiveProtocol.ActivityEndMessage, got.Last());
            Assert.DoesNotContain(got, m => m.Contains("audioStreamEnd"));
        }

        [Fact]
        public async Task BadKeyErrorIsReported()
        {
            using var server = new FakeServer();
            _ = Task.Run(async () =>
            {
                var ws = await server.Accept();
                await Receive(ws);
                await ws.CloseAsync((WebSocketCloseStatus)1007, "API key not valid. Please pass a valid API key.", CancellationToken.None);
            });
            var session = new LiveSession("AIzaWrong", new List<string>()) { Endpoint = server.Uri };
            var failed = new TaskCompletionSource<LiveError>();
            session.Failed += e => failed.TrySetResult(e);
            await session.ConnectAsync();
            var done = await Task.WhenAny(failed.Task, Task.Delay(5000));
            Assert.Same(failed.Task, done);
            Assert.Equal(LiveErrorKind.BadKey, failed.Task.Result.Kind);
            session.Dispose();
        }

        [Fact]
        public async Task NoInternetIsReported()
        {
            var session = new LiveSession("AIzaKey", new List<string>()) { Endpoint = new Uri($"ws://127.0.0.1:{FreePort()}/") };
            LiveError err = null;
            session.Failed += e => err = e;
            await session.ConnectAsync();
            Assert.NotNull(err);
            Assert.Equal(LiveErrorKind.NoInternet, err.Kind);
        }

        [Fact]
        public async Task MissingKeyIsReported()
        {
            var session = new LiveSession("  ", new List<string>());
            LiveError err = null;
            session.Failed += e => err = e;
            await session.ConnectAsync();
            Assert.Equal(LiveErrorKind.MissingKey, err.Kind);
        }

        [Fact]
        public async Task SilenceIsNotSentForAFinal()
        {
            using var server = new FakeServer();
            _ = Task.Run(async () =>
            {
                var ws = await server.Accept();
                string msg;
                while ((msg = await Receive(ws)) != null)
                {
                    lock (server.Received) server.Received.Add(msg);
                    if (msg.Contains("\"setup\"")) await Send(ws, "{\"setupComplete\":{}}");
                }
            });
            var session = new LiveSession("AIzaKey", new List<string>()) { Endpoint = server.Uri };
            await session.ConnectAsync();
            for (int i = 0; i < 5; i++) session.SendAudio(new byte[3200]);
            var text = await session.FinishAsync();
            session.Dispose();
            Assert.Equal("", text);
            lock (server.Received) Assert.DoesNotContain(server.Received, m => m.Contains("activityEnd"));
        }
    }
}
