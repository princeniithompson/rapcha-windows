using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Whisper models in %AppData%\Rapcha\models, downloaded from the official whisper.cpp ggml models on Hugging Face
    /// with progress and cancel, checked against the published SHA-256 before use, and deletable.
    /// </summary>
    public static class ModelStore
    {
        /// <summary>Self-test: keep models somewhere else.</summary>
        public static string FolderOverride;
        public static string Folder => FolderOverride ?? Path.Combine(AppStorage.Folder, "models");

        public static event Action Changed;

        public static string PathOf(WhisperModel m) => Path.Combine(Folder, m.File);
        private static string MarkerOf(WhisperModel m) => PathOf(m) + ".sha256";
        private static string PartOf(WhisperModel m) => PathOf(m) + ".part";

        /// <summary>Downloaded completely and its SHA-256 was checked.</summary>
        public static bool IsInstalled(WhisperModel m)
        {
            if (m == null) return false;
            try
            {
                var fi = new FileInfo(PathOf(m));
                return fi.Exists && fi.Length == m.Bytes && File.Exists(MarkerOf(m)) &&
                       string.Equals(File.ReadAllText(MarkerOf(m)).Trim(), m.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Delete(WhisperModel m)
        {
            foreach (var p in new[] { PathOf(m), MarkerOf(m), PartOf(m) })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            AppStorage.Log("Whisper model deleted: " + m.Id);
            Changed?.Invoke();
        }

        public enum Outcome { Done, Cancelled, BadChecksum, NoDisk, Failed }

        /// <summary>Downloads to a .part file while hashing, checks size and SHA-256, then moves it into place.</summary>
        /// <param name="progress">bytes so far, total bytes</param>
        public static async Task<Outcome> DownloadAsync(WhisperModel m, Action<long, long> progress, CancellationToken ct, string urlOverride = null)
        {
            Directory.CreateDirectory(Folder);
            long free = DeviceProbe.FreeDiskMb();
            if (free >= 0 && !WhisperModels.FitsOnDisk(m, free)) { AppStorage.Log("Whisper download: not enough disk (" + free + " MB free, needs " + m.Mb + ")"); return Outcome.NoDisk; }
            var part = PartOf(m);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AppStorage.Log("Whisper download started: " + m.Id + " (" + m.Mb + " MB)");
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(urlOverride ?? m.Url);
                req.AllowAutoRedirect = true;
                req.UserAgent = "Rapcha/" + typeof(ModelStore).Assembly.GetName().Version;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                using (ct.Register(() => { try { req.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var sha = SHA256.Create())
                {
                    long total = resp.ContentLength > 0 ? resp.ContentLength : m.Bytes, got = 0;
                    var buf = new byte[1 << 16];
                    long lastReport = -1;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        got += n;
                        if (got - lastReport >= 256 * 1024 || got == total) { lastReport = got; try { progress?.Invoke(got, total); } catch { } }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    dst.Flush();
                    dst.Close();
                    var hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (got != m.Bytes || hex != m.Sha256)
                    {
                        AppStorage.Log("Whisper download: checksum mismatch for " + m.Id + " (bytes=" + got + ", want " + m.Bytes + "); deleted");
                        TryDelete(part);
                        return Outcome.BadChecksum;
                    }
                }
                TryDelete(PathOf(m));
                File.Move(part, PathOf(m));
                File.WriteAllText(MarkerOf(m), m.Sha256);
                AppStorage.Log("Whisper download finished: " + m.Id + " in " + sw.ElapsedMilliseconds + " ms, SHA-256 ok");
                Changed?.Invoke();
                return Outcome.Done;
            }
            catch (Exception e) when (ct.IsCancellationRequested || e is OperationCanceledException)
            {
                TryDelete(part);
                AppStorage.Log("Whisper download cancelled: " + m.Id);
                return Outcome.Cancelled;
            }
            catch (Exception e)
            {
                TryDelete(part);
                AppStorage.Log("Whisper download failed: " + m.Id + " " + e.GetType().Name + (e is WebException we ? " " + we.Status : ""));
                return Outcome.Failed;
            }
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}
