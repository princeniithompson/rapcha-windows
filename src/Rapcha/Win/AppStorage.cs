using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Everything lives in %AppData%\Rapcha. The API key is encrypted with DPAPI (CurrentUser),
    /// so only this Windows user on this PC can read it. Nothing is ever sent anywhere except to Gemini.
    /// </summary>
    public static class AppStorage
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Rapcha.Windows.ApiKey.v1");

        public static string Folder
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rapcha");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string KeyPath => Path.Combine(Folder, "apikey.dat");
        private static string VocabPath => Path.Combine(Folder, "vocabulary.txt");
        private static string RingPath => Path.Combine(Folder, "ring.txt");
        public static string LogPath => Path.Combine(Folder, "rapcha.log");
        private static string MicPath => Path.Combine(Folder, "microphone.txt");

        /// <summary>Saved microphone name; "" means the Windows default.</summary>
        public static string LoadMicDevice()
        {
            try { return File.Exists(MicPath) ? File.ReadAllText(MicPath, Encoding.UTF8).Trim() : ""; }
            catch { return ""; }
        }

        public static void SaveMicDevice(string name)
        {
            try { WriteAtomic(MicPath, new UTF8Encoding(false).GetBytes((name ?? "").Trim())); } catch { }
        }

        private static string BoostPath => Path.Combine(Folder, "mic-boost.txt");

        /// <summary>v20: Settings → Microphone → Mic boost for offline typing: "auto" (default), "off" or a number of dB.</summary>
        public static string LoadMicBoost()
        {
            try { return File.Exists(BoostPath) ? File.ReadAllText(BoostPath, Encoding.UTF8).Trim() : "auto"; }
            catch { return "auto"; }
        }

        public static void SaveMicBoost(string value)
        {
            try { WriteAtomic(BoostPath, new UTF8Encoding(false).GetBytes((value ?? "auto").Trim())); } catch { }
        }

        /// <summary>The last part of the log, for the Copy log button.</summary>
        public static string ReadLogTail(int maxLines = 400)
        {
            try
            {
                if (!File.Exists(LogPath)) return "(no log yet)";
                var lines = File.ReadAllLines(LogPath);
                int start = Math.Max(0, lines.Length - maxLines);
                var header = "Rapcha for Windows " + typeof(AppStorage).Assembly.GetName().Version + " on " + Environment.OSVersion + Environment.NewLine;
                return header + string.Join(Environment.NewLine, lines, start, lines.Length - start);
            }
            catch (Exception e) { return "(could not read log: " + e.Message + ")"; }
        }

        public static string LoadApiKey()
        {
            try
            {
                if (!File.Exists(KeyPath)) return "";
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain).Trim();
            }
            catch { return ""; }
        }

        public static void SaveApiKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) { if (File.Exists(KeyPath)) File.Delete(KeyPath); return; }
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            WriteAtomic(KeyPath, enc);
        }

        public static List<string> LoadVocabulary()
        {
            try { return File.Exists(VocabPath) ? VocabularyList.FromText(File.ReadAllText(VocabPath, Encoding.UTF8)) : new List<string>(); }
            catch { return new List<string>(); }
        }

        public static void SaveVocabulary(IEnumerable<string> terms)
        {
            WriteAtomic(VocabPath, new UTF8Encoding(false).GetBytes(VocabularyList.ToText(terms)));
        }

        // ---- Home page history (plain JSON; the text is never logged) ----
        private static string HistoryPath => Path.Combine(Folder, "history.json");
        private static readonly object HistoryLock = new object();

        /// <summary>Raised (on the saving thread) after the history file changed.</summary>
        public static event Action HistoryChanged;

        public static TranscriptHistory LoadHistory()
        {
            lock (HistoryLock)
            {
                try { return File.Exists(HistoryPath) ? TranscriptHistory.FromJson(File.ReadAllText(HistoryPath, Encoding.UTF8)) : new TranscriptHistory(); }
                catch (Exception e) { Log("History: could not read history.json (" + e.GetType().Name + "); starting empty"); return new TranscriptHistory(); }
            }
        }

        public static void SaveHistory(TranscriptHistory history)
        {
            lock (HistoryLock)
            {
                try { WriteAtomic(HistoryPath, new UTF8Encoding(false).GetBytes(history.ToJson())); }
                catch (Exception e) { Log("History: save failed (" + e.GetType().Name + ")"); return; }
            }
            HistoryChanged?.Invoke();
        }

        /// <summary>Adds one finished transcript (newest first) and saves.</summary>
        public static void AddToHistory(string text)
        {
            TranscriptHistory h;
            lock (HistoryLock)
            {
                h = LoadHistory();
                if (h.Add(text, DateTime.UtcNow) == null) return;
            }
            SaveHistory(h);
            Log("History: saved a transcript (" + h.Items.Count + " in history)");
        }

        // ---- Keyboard shortcuts ----
        private static string ShortcutsPath => Path.Combine(Folder, "shortcuts.txt");
        public static event Action ShortcutsChanged;

        public static ShortcutSet LoadShortcuts()
        {
            try { return File.Exists(ShortcutsPath) ? ShortcutSet.FromText(File.ReadAllText(ShortcutsPath, Encoding.UTF8)) : ShortcutSet.Defaults(); }
            catch { return ShortcutSet.Defaults(); }
        }

        public static void SaveShortcuts(ShortcutSet set)
        {
            try { WriteAtomic(ShortcutsPath, new UTF8Encoding(false).GetBytes(set.ToText())); }
            catch (Exception e) { Log("Shortcuts: save failed (" + e.GetType().Name + ")"); }
            ShortcutsChanged?.Invoke();
        }

        // ---- Glow colour ----
        private static string GlowPath => Path.Combine(Folder, "glow.txt");
        private static int? _glow;
        public static event Action GlowChanged;

        public static int LoadGlowColor()
        {
            if (_glow.HasValue) return _glow.Value;
            int v = GlowColors.Default;
            try { if (File.Exists(GlowPath)) v = GlowColors.Parse(File.ReadAllText(GlowPath)); } catch { }
            _glow = v;
            return v;
        }

        public static void SaveGlowColor(int rgb)
        {
            rgb &= 0xFFFFFF;
            try { WriteAtomic(GlowPath, Encoding.ASCII.GetBytes(GlowColors.Format(rgb))); }
            catch (Exception e) { Log("Glow colour: save failed (" + e.GetType().Name + ")"); }
            _glow = rgb;
            Log("Glow colour: " + GlowColors.NameOf(rgb) + " " + GlowColors.Format(rgb));
            GlowChanged?.Invoke();
        }

        /// <summary>Forgets the cached value (self-test: re-read from disk).</summary>
        public static void ReloadGlowColor() => _glow = null;

        public static DockPosition LoadRing(out string deviceName)
        {
            deviceName = "";
            try
            {
                if (File.Exists(RingPath))
                {
                    var parts = File.ReadAllText(RingPath).Trim().Split('|');
                    if (parts.Length >= 2 && Enum.TryParse(parts[0], out DockEdge edge) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    {
                        if (parts.Length >= 3) deviceName = parts[2];
                        return new DockPosition(edge, f);
                    }
                }
            }
            catch { }
            return EdgeSnap.Default;
        }

        public static void SaveRing(DockPosition pos, string deviceName)
        {
            try
            {
                var text = pos.Edge + "|" + pos.Fraction.ToString("0.0000", CultureInfo.InvariantCulture) + "|" + (deviceName ?? "");
                WriteAtomic(RingPath, Encoding.UTF8.GetBytes(text));
            }
            catch { }
        }

        /// <summary>Small local log for troubleshooting. Never contains the key or the spoken words.</summary>
        public static void Log(string line)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                lock (LogLock) File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + line + Environment.NewLine);
            }
            catch { }
            Rapcha.Core.DiagLog.Add("log", line);   // v19: every log line is also on the Diagnostics timeline
        }

        private static readonly object LogLock = new object();

        private static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
