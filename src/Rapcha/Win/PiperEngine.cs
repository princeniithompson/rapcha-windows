using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// One warm piper.exe process (only while select to speak is on). Each request is one JSON line on stdin
    /// with its own output WAV; piper prints the file path when that phrase is done, so phrases come back
    /// one by one, in order. The WAV is read into memory and deleted at once.
    /// </summary>
    public sealed class PiperEngine : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Queue<(TaskCompletionSource<WavData> tcs, string file)> _pending = new Queue<(TaskCompletionSource<WavData>, string)>();
        private Process _p;
        private int _counter;
        private string _outDir;

        public double LengthScale { get; private set; }
        public bool Running => _p != null && !_p.HasExited;
        public int Requests { get; private set; }

        public long PeakWorkingSetBytes
        {
            get { try { _p.Refresh(); return _p.PeakWorkingSet64; } catch { return 0; } }
        }

        public void Start(double speed)
        {
            LengthScale = Math.Round(1.0 / Math.Max(0.5, speed), 2);
            string folder = PiperVoice.Folder;
            _outDir = Path.Combine(folder, "out");
            Directory.CreateDirectory(_outDir);
            foreach (var f in Directory.GetFiles(_outDir, "*.wav")) { try { File.Delete(f); } catch { } }

            // Relative paths only (working directory = the piper folder), so a non-English Windows user name
            // in the path can never confuse piper's ANSI file handling.
            var psi = new ProcessStartInfo(PiperVoice.ExePath)
            {
                Arguments = "--model " + PiperVoice.Model.Name + " --config " + PiperVoice.ModelConfig.Name +
                            " --espeak_data piper\\espeak-ng-data --json-input --sentence_silence 0.15 --length_scale " +
                            LengthScale.ToString("0.00", CultureInfo.InvariantCulture),
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(); };
            p.ErrorDataReceived += (s, e) => { }; // piper's own log; may contain text, so it is not kept
            p.Exited += (s, e) => FailAll("piper exited (code " + SafeExitCode(p) + ")");
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _p = p;
            AppStorage.Log("Piper started: pid=" + p.Id + " length_scale=" + psi.Arguments.Substring(psi.Arguments.LastIndexOf(' ') + 1));
        }

        private static string SafeExitCode(Process p)
        {
            try { return "0x" + p.ExitCode.ToString("X"); } catch { return "?"; }
        }

        /// <summary>Synthesizes one phrase. Completes with 16-bit PCM (or faults if piper fails).</summary>
        public Task<WavData> SynthesizeAsync(string text)
        {
            var tcs = new TaskCompletionSource<WavData>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (!Running) { tcs.SetException(new IOException("piper is not running")); return tcs.Task; }
                string name = "seg" + (++_counter) + ".wav";
                string line = "{\"text\": " + JsonAscii(Clean(text)) + ", \"output_file\": " + JsonAscii("out\\" + name) + "}\n";
                _pending.Enqueue((tcs, Path.Combine(_outDir, name)));
                Requests++;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    _p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    _p.StandardInput.BaseStream.Flush();
                }
                catch (Exception e)
                {
                    FailAllLocked("write failed: " + e.GetType().Name);
                }
            }
            return tcs.Task;
        }

        private void OnLine()
        {
            (TaskCompletionSource<WavData> tcs, string file) item;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                item = _pending.Dequeue();
            }
            try
            {
                byte[] bytes = null;
                for (int i = 0; i < 10 && bytes == null; i++)
                {
                    try { bytes = File.ReadAllBytes(item.file); }
                    catch (IOException) { Thread.Sleep(20); }
                }
                try { File.Delete(item.file); } catch { }
                if (bytes != null && WavData.TryParse(bytes, out var wav)) item.tcs.TrySetResult(wav);
                else item.tcs.TrySetException(new InvalidDataException("piper wrote no audio"));
            }
            catch (Exception e) { item.tcs.TrySetException(e); }
        }

        private void FailAll(string why) { lock (_lock) FailAllLocked(why); }

        private void FailAllLocked(string why)
        {
            if (_pending.Count > 0) AppStorage.Log("Piper: " + why + ", pending=" + _pending.Count);
            while (_pending.Count > 0) _pending.Dequeue().tcs.TrySetException(new IOException(why));
        }

        private static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
                bool bad = char.IsControl(c) || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029';
                sb.Append(bad ? ' ' : c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>JSON string with everything outside printable ASCII escaped (safe for any code page).</summary>
        public static string JsonAscii(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public void Dispose()
        {
            var p = _p;
            _p = null;
            FailAll("stopped");
            if (p == null) return;
            try { p.StandardInput.Close(); } catch { }
            try { if (!p.WaitForExit(800)) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
            try { foreach (var f in Directory.GetFiles(_outDir, "*.wav")) File.Delete(f); } catch { }
            AppStorage.Log("Piper stopped: requests=" + Requests);
        }
    }
}
