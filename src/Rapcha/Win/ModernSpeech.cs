using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace Rapcha.Win
{
    /// <summary>
    /// v20: what Windows' newer speech recogniser (Windows.Media.SpeechRecognition) offers on this PC, checked without
    /// opening the microphone. Free-form dictation in that API is Microsoft's online service (it needs Settings → Privacy →
    /// Speech → Online speech recognition, and the internet); offline it only recognises fixed phrase lists, and it always
    /// opens the microphone itself, so it can't use Rapcha's single boosted stream. It is therefore only reported here,
    /// not used for offline voice typing. (Win + H voice typing uses a different on-device model Windows does not offer to apps.)
    /// </summary>
    public static class ModernSpeech
    {
        public static bool OnlineSpeechAllowed()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy"))
                    return k?.GetValue("HasAccepted") is int v && v == 1;
            }
            catch { return false; }
        }

        /// <summary>One line for Diagnostics. Never opens the microphone (no recognition is started).</summary>
        public static string Probe(int timeoutMs = 5000)
        {
            try
            {
                var t = Task.Run(() =>
                {
                    string lang = "?", topics = "?", grammars = "?", dictation = "?", list = "?";
                    try { lang = SpeechRecognizer.SystemSpeechLanguage?.LanguageTag ?? "none"; } catch (Exception e) { lang = "error " + e.GetType().Name; }
                    try { topics = string.Join(",", SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag)); } catch { }
                    try { grammars = string.Join(",", SpeechRecognizer.SupportedGrammarLanguages.Select(l => l.LanguageTag)); } catch { }
                    try
                    {
                        using (var r = new SpeechRecognizer())
                        {
                            r.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
                            dictation = r.CompileConstraintsAsync().AsTask().GetAwaiter().GetResult().Status.ToString();
                        }
                    }
                    catch (Exception e) { dictation = "error 0x" + e.HResult.ToString("X8"); }
                    try
                    {
                        using (var r = new SpeechRecognizer())
                        {
                            r.Constraints.Add(new SpeechRecognitionListConstraint(new[] { "hello rapcha" }, "list"));
                            list = r.CompileConstraintsAsync().AsTask().GetAwaiter().GetResult().Status.ToString();
                        }
                    }
                    catch (Exception e) { list = "error 0x" + e.HResult.ToString("X8"); }
                    return "Windows modern speech (Windows.Media.SpeechRecognition): language=" + lang + " dictation_languages=[" + topics + "] phrase_list_languages=[" + grammars +
                           "] dictation_setup=" + dictation + " phrase_list_setup=" + list + " online_speech_recognition_setting=" + (OnlineSpeechAllowed() ? "on" : "off") +
                           " -> free-form dictation there is Microsoft's online service and opens the mic itself, so Rapcha does not use it offline";
                });
                return t.Wait(timeoutMs) ? t.Result : "Windows modern speech: no answer in " + timeoutMs / 1000 + " s";
            }
            catch (Exception e) { return "Windows modern speech: " + e.GetType().Name; }
        }
    }
}
