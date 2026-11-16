using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Rapcha.Win
{
    /// <summary>
    /// The natural offline voice: the Piper engine (rhasspy/piper 2023.11.14-2, Windows x64) and the
    /// en_US "lessac" medium voice. Downloaded once to %AppData%\Rapcha\piper on first use, every file
    /// checked against the SHA-256 pinned below before it is used. Nothing is bundled in Rapcha.exe.
    /// </summary>
    public static class PiperVoice
    {
        public enum State { NotInstalled, Downloading, Ready, Failed, Unsupported }

        public sealed class Pinned
        {
            public string Name, Url, Sha256; public long Size;
            public Pinned(string name, string url, string sha, long size) { Name = name; Url = url; Sha256 = sha; Size = size; }
        }

        public const string EngineVersion = "2023.11.14-2";
        public const string VoiceId = "en_US-lessac-medium";
        public const string VoiceLabel = "Lessac (US English, natural)";

        public static readonly Pinned Engine = new Pinned("piper_windows_amd64.zip",
            "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea", 22477236);
        public static readonly Pinned Model = new Pinned(VoiceId + ".onnx",
            "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx",
            "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f", 63201294);
        public static readonly Pinned ModelConfig = new Pinned(VoiceId + ".onnx.json",
            "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json",
            "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0", 4885);

        public static long DownloadBytes => Engine.Size + Model.Size + ModelConfig.Size;
        public static string DownloadSizeText => "~" + Math.Round(DownloadBytes / 1e6) + " MB";

        public static string Folder => Path.Combine(AppStorage.Folder, "piper");
        public static string ExePath => Path.Combine(Folder, "piper", "piper.exe");
        public static string ModelPath => Path.Combine(Folder, Model.Name);
        private static string MarkerPath => Path.Combine(Folder, "installed.txt");
        private static string MarkerText => EngineVersion + " " + Model.Sha256 + " " + Engine.Sha256;

        public static State Status { get; private set; } = State.NotInstalled;
        public static int Percent { get; private set; }
        public static string Problem { get; private set; } = "";
        public static event Action StatusChanged;

        private static Task _install;
        private static double _speed = double.NaN;

        /// <summary>Speaking speed, 0.7x to 1.5x (Piper length_scale = 1 / speed).</summary>
        public static double Speed
        {
            get
            {
                if (double.IsNaN(_speed))
                {
                    _speed = 1.0;
                    try
                    {
                        var p = Path.Combine(AppStorage.Folder, "voice-speed.txt");
                        if (File.Exists(p) && double.TryParse(File.ReadAllText(p).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _speed = Clamp(v);
                    }
                    catch { }
                }
                return _speed;
            }
            set
            {
                _speed = Clamp(value);
                try { File.WriteAllText(Path.Combine(AppStorage.Folder, "voice-speed.txt"), _speed.ToString("0.00", CultureInfo.InvariantCulture)); } catch { }
            }
        }

        private static double Clamp(double v) => Math.Max(0.7, Math.Min(1.5, v));

        public static void Refresh()
        {
            if (Status == State.Downloading) return;
            var why = UnsupportedReason();
            if (why != null) { Set(State.Unsupported, why); return; }
            Set(IsInstalled ? State.Ready : (Status == State.Failed ? State.Failed : State.NotInstalled), Status == State.Failed ? Problem : "");
        }

        public static bool IsInstalled
        {
            get
            {
                try
                {
                    return File.Exists(MarkerPath) && File.ReadAllText(MarkerPath).Trim() == MarkerText &&
                           File.Exists(ExePath) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == Model.Size &&
                           File.Exists(ModelPath + ".json");
                }
                catch { return false; }
            }
        }

        /// <summary>Why Piper cannot run on this PC (null when it can).</summary>
        public static string UnsupportedReason()
        {
            if (!Environment.Is64BitOperatingSystem) return "The natural voice needs 64-bit Windows.";
            if (!HasVcRuntime()) return "The natural voice needs the Microsoft Visual C++ Runtime (x64).";
            return null;
        }

        public static bool HasVcRuntime()
        {
            // Rapcha is a 32-bit process: the 64-bit system folder is reached through "Sysnative".
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys = Path.Combine(win, Environment.Is64BitProcess ? "System32" : "Sysnative");
            foreach (var dll in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                if (!File.Exists(Path.Combine(sys, dll))) return false;
            return true;
        }

        public const string VcRuntimeUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        /// <summary>Starts the one-time download if it is needed and not already running.</summary>
        public static Task EnsureInstalledAsync()
        {
            Refresh();
            if (Status == State.Ready || Status == State.Unsupported) return Task.FromResult(0);
            if (_install != null && !_install.IsCompleted) return _install;
            _install = InstallAsync();
            return _install;
        }

        private static async Task InstallAsync()
        {
            Set(State.Downloading, "");
            Percent = 0;
            var started = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(Folder);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder)));
                if (drive.IsReady && drive.AvailableFreeSpace < 300L * 1024 * 1024) throw new IOException("Not enough free disk space (needs about 200 MB).");
                AppStorage.Log("Voice download: start engine=" + EngineVersion + " voice=" + VoiceId + " bytes=" + DownloadBytes);
                long doneBefore = 0;
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Rapcha-Windows");
                    foreach (var f in new[] { ModelConfig, Engine, Model })
                    {
                        string target = Path.Combine(Folder, f.Name);
                        await DownloadVerified(http, f, target, doneBefore);
                        doneBefore += f.Size;
                    }
                }
                // Unpack the engine next to the voice (piper\piper.exe, its DLLs and espeak-ng-data).
                string zip = Path.Combine(Folder, Engine.Name);
                string temp = Path.Combine(Folder, "unpack");
                await Task.Run(() =>
                {
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    ZipFile.ExtractToDirectory(zip, temp);
                    string dest = Path.Combine(Folder, "piper");
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(Path.Combine(temp, "piper"), dest);
                    Directory.Delete(temp, true);
                    File.Delete(zip);
                });
                if (!File.Exists(ExePath)) throw new IOException("piper.exe missing after unpacking");
                File.WriteAllText(MarkerPath, MarkerText);
                AppStorage.Log("Voice download: ready in " + (int)(DateTime.UtcNow - started).TotalSeconds + " s");
                Percent = 100;
                Set(State.Ready, "");
            }
            catch (Exception e)
            {
                string msg = e is HttpRequestException || e is TaskCanceledException || e is System.Net.WebException
                    ? "Couldn't download the natural voice. Check your internet connection."
                    : e is InvalidDataException ? "The voice download was damaged. It will be tried again." : "Couldn't set up the natural voice (" + e.Message + ").";
                AppStorage.Log("Voice download failed: " + e.GetType().Name + " " + e.Message);
                Set(State.Failed, msg);
            }
        }

        private static async Task DownloadVerified(HttpClient http, Pinned f, string target, long doneBefore)
        {
            if (File.Exists(target) && new FileInfo(target).Length == f.Size && await Task.Run(() => Sha256Of(target)) == f.Sha256) return;
            string part = target + ".part";
            using (var resp = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[81920];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        sha.TransformBlock(buf, 0, n, null, 0);
                        await dst.WriteAsync(buf, 0, n);
                        got += n;
                        if (got > f.Size + 1024 * 1024) throw new InvalidDataException(f.Name + " is larger than expected");
                        int pct = (int)(100 * (doneBefore + got) / DownloadBytes);
                        if (pct != Percent) { Percent = Math.Min(99, pct); StatusChanged?.Invoke(); }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    string hash = Hex(sha.Hash);
                    if (hash != f.Sha256)
                    {
                        dst.Dispose();
                        File.Delete(part);
                        throw new InvalidDataException(f.Name + " SHA-256 mismatch");
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            AppStorage.Log("Voice download: verified " + f.Name);
        }

        public static string Sha256Of(string path)
        {
            using (var s = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(s));
        }

        private static string Hex(byte[] b)
        {
            var sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static void Set(State s, string problem)
        {
            Status = s;
            Problem = problem ?? "";
            StatusChanged?.Invoke();
        }

        /// <summary>Plain-English status for Settings and the reading card.</summary>
        public static string Describe()
        {
            switch (Status)
            {
                case State.Ready: return "Natural voice ready: " + VoiceLabel + ". Works offline.";
                case State.Downloading: return "Downloading voice, one time, " + DownloadSizeText + "… " + Percent + "%";
                case State.Failed: return Problem + " Using the Windows voice for now.";
                case State.Unsupported: return Problem + " Using the Windows voice.";
                default: return "Natural voice not downloaded yet (one time, " + DownloadSizeText + "). It downloads the first time you use select to speak.";
            }
        }
    }
}
