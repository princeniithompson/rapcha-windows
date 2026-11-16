using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>One reading voice: a modern Windows ("OneCore") voice or an older SAPI desktop voice.</summary>
    public sealed class VoiceEntry
    {
        public string Id;          // "onecore:<id>" or "sapi:<name>"
        public bool Desktop;       // SAPI (System.Speech)
        public string FullName;    // as Windows names it
        public string Gender;
        public string Language;    // BCP-47 tag, e.g. en-GB
        public string Label;       // "Zira, female, English (US)"
        public override string ToString() => Label;
    }

    /// <summary>All voices on this PC, the saved choice (voice.txt), and the reader for it.</summary>
    public static class Voices
    {
        private static string ChoicePath => Path.Combine(AppStorage.Folder, "voice.txt");

        public static List<VoiceEntry> All()
        {
            var list = new List<VoiceEntry>();
            try
            {
                foreach (var v in Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices)
                    list.Add(new VoiceEntry
                    {
                        Id = "onecore:" + v.Id, Desktop = false, FullName = v.DisplayName, Gender = v.Gender.ToString(), Language = v.Language,
                        Label = VoiceLabels.Label(v.DisplayName, v.Gender.ToString(), v.Language, false)
                    });
            }
            catch (Exception e) { AppStorage.Log("Voices: OneCore list failed (" + e.GetType().Name + ")"); }
            try
            {
                using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
                    foreach (var iv in s.GetInstalledVoices())
                    {
                        if (!iv.Enabled) continue;
                        var v = iv.VoiceInfo;
                        list.Add(new VoiceEntry
                        {
                            Id = "sapi:" + v.Name, Desktop = true, FullName = v.Name, Gender = v.Gender.ToString(), Language = v.Culture?.Name ?? "",
                            Label = VoiceLabels.Label(v.Name, v.Gender.ToString(), v.Culture?.Name, true)
                        });
                    }
            }
            catch (Exception e) { AppStorage.Log("Voices: SAPI list failed (" + e.GetType().Name + ")"); }
            var pref = CultureInfo.CurrentUICulture.Name;
            return list.OrderBy(v => VoiceLabels.SortKey(v.Language, v.Desktop, v.FullName, pref), StringComparer.Ordinal).ToList();
        }

        /// <summary>The saved voice id, or "" for the Windows default.</summary>
        public static string SavedId
        {
            get { try { return File.Exists(ChoicePath) ? File.ReadAllText(ChoicePath, Encoding.UTF8).Trim() : ""; } catch { return ""; } }
            set
            {
                try { File.WriteAllText(ChoicePath, value ?? "", new UTF8Encoding(false)); } catch (Exception e) { AppStorage.Log("Voices: save failed (" + e.GetType().Name + ")"); }
            }
        }

        /// <summary>The SAPI default voice (what Rapcha used before there was a choice).</summary>
        public static string DefaultSapiId()
        {
            try { using (var s = new System.Speech.Synthesis.SpeechSynthesizer()) return s.Voice != null ? "sapi:" + s.Voice.Name : ""; }
            catch { return ""; }
        }

        /// <summary>
        /// A reader for the chosen (or given) voice. A voice that is gone falls back to the Windows default.
        /// </summary>
        private static int _warm;

        /// <summary>
        /// Loads the chosen voice's engine on a background thread (a throwaway synthesizer that speaks nothing
        /// audible), so the reader created later on the UI thread starts at once. Once per process.
        /// </summary>
        public static void WarmUp()
        {
            if (System.Threading.Interlocked.Exchange(ref _warm, 1) == 1) return;
            var id = SavedId;
            System.Threading.Tasks.Task.Run(async () =>
            {
                var t0 = DateTime.UtcNow;
                try
                {
                    if (id.StartsWith("onecore:", StringComparison.Ordinal))
                    {
                        var key = id.Substring(8);
                        var v = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(x => x.Id == key);
                        if (v != null)
                            using (var s = new Windows.Media.SpeechSynthesis.SpeechSynthesizer { Voice = v })
                            using (var st = await s.SynthesizeTextToStreamAsync(".").AsTask()) { }
                    }
                    using (var sapi = new System.Speech.Synthesis.SpeechSynthesizer())
                    {
                        sapi.SetOutputToNull();
                        if (id.StartsWith("sapi:", StringComparison.Ordinal)) { try { sapi.SelectVoice(id.Substring(5)); } catch { } }
                        sapi.Speak(".");
                    }
                    AppStorage.Log("Voices: warmed up in " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms");
                }
                catch (Exception e) { AppStorage.Log("Voices: warm-up failed (" + e.GetType().Name + ")"); }
            });
        }

        public static IReadAloud CreateReader(bool silent, string id = null)
        {
            id = id ?? SavedId;
            if (id.StartsWith("onecore:", StringComparison.Ordinal))
            {
                try
                {
                    var key = id.Substring(8);
                    var v = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(x => x.Id == key);
                    if (v != null) return new OneCoreReader(v, silent);
                    AppStorage.Log("Voices: the chosen voice is not installed any more; using the default");
                }
                catch (Exception e) { AppStorage.Log("Voices: OneCore unavailable (" + e.GetType().Name + "); using the default"); }
                return new SpeechReader(silent);
            }
            if (id.StartsWith("sapi:", StringComparison.Ordinal)) return new SpeechReader(silent, id.Substring(5));
            return new SpeechReader(silent);
        }
    }
}
