using System;
using System.IO;

namespace Rapcha.Win
{
    /// <summary>Settings → Offline voice typing: the "Offline fallback" switch (on by default) and the chosen Whisper model.</summary>
    public static class OfflineSettings
    {
        public const bool DefaultFallback = true;
        private static bool? _fallback;
        private static string FallbackPath => Path.Combine(AppStorage.Folder, "offline-fallback.txt");
        private static string ModelPath => Path.Combine(AppStorage.Folder, "whisper-model.txt");

        public static event Action Changed;

        public static bool Fallback
        {
            get
            {
                if (_fallback.HasValue) return _fallback.Value;
                bool v = DefaultFallback;
                try { if (File.Exists(FallbackPath)) v = File.ReadAllText(FallbackPath).Trim() != "0"; } catch { }
                _fallback = v;
                return v;
            }
            set
            {
                _fallback = value;
                try { File.WriteAllText(FallbackPath, value ? "1" : "0"); } catch { }
                AppStorage.Log("Settings: offline fallback " + (value ? "on" : "off"));
                Changed?.Invoke();
            }
        }

        internal static void Reload() => _fallback = null;

        /// <summary>The Whisper model the user picked on a Strong laptop ("base.en" or "small.en"); "" = recommended.</summary>
        public static string ChosenModel
        {
            get { try { return File.Exists(ModelPath) ? File.ReadAllText(ModelPath).Trim() : ""; } catch { return ""; } }
            set { try { File.WriteAllText(ModelPath, value ?? ""); } catch { } Changed?.Invoke(); }
        }
    }
}
