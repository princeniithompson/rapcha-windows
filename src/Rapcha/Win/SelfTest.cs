using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Rapcha.Win
{
    /// <summary>CI-only checks. Nothing here runs in normal use.</summary>
    internal static class SelfTest
    {
        /// <summary>
        /// --selftest-paste "text": pastes into an open Notepad through the real delivery path, then reads
        /// Notepad's text back. 0 = typed and found, 3 = only copied, 1 = failed / not found.
        /// </summary>
        public static int Paste(string text)
        {
            IntPtr notepad = IntPtr.Zero;
            foreach (var p in Process.GetProcessesByName("notepad"))
                if (p.MainWindowHandle != IntPtr.Zero) { notepad = p.MainWindowHandle; break; }
            if (notepad == IntPtr.Zero) { AppStorage.Log("Selftest paste: no Notepad window"); return 1; }

            IntPtr edit = FindEditor(notepad);
            var result = TextDelivery.Deliver(text, notepad);
            Thread.Sleep(800);
            string back = edit != IntPtr.Zero ? GetText(edit) : "";
            bool found = back.Contains(text);
            AppStorage.Log("Selftest paste: result=" + result + " editor=" + (edit != IntPtr.Zero ? TextDelivery.ClassOf(edit) : "none") +
                           " found=" + found + " reason=" + TextDelivery.LastReason);
            if (result == TextDelivery.Result.Typed && found) return 0;
            return result == TextDelivery.Result.Copied ? 3 : 1;
        }

        /// <summary>
        /// --selftest-piper "text": downloads and verifies Piper + the voice (pinned SHA-256), starts the engine,
        /// synthesizes two phrases and checks for real PCM. 0 = ok, 1 = failed.
        /// </summary>
        public static int Piper(string text)
        {
            var sw = Stopwatch.StartNew();
            int lastPct = -10;
            PiperVoice.StatusChanged += () =>
            {
                if (PiperVoice.Status == PiperVoice.State.Downloading && PiperVoice.Percent >= lastPct + 10) { lastPct = PiperVoice.Percent; AppStorage.Log("Selftest piper: download " + lastPct + "%"); }
            };
            PiperVoice.EnsureInstalledAsync().GetAwaiter().GetResult();
            AppStorage.Log("Selftest piper: status=" + PiperVoice.Status + " installed=" + PiperVoice.IsInstalled + " seconds=" + (int)sw.Elapsed.TotalSeconds +
                           (PiperVoice.Problem.Length > 0 ? " problem=" + PiperVoice.Problem : ""));
            if (!PiperVoice.IsInstalled) return 1;
            using (var engine = new PiperEngine())
            {
                engine.Start(PiperVoice.Speed);
                var plan = Rapcha.Core.ReadingPlan.Create(text);
                int ok = 0;
                for (int ph = 0; ph < Math.Min(2, plan.Phrases.Count); ph++)
                {
                    var t0 = Stopwatch.StartNew();
                    var task = engine.SynthesizeAsync(plan.PhraseText(ph));
                    if (!task.Wait(60000)) { AppStorage.Log("Selftest piper: synth timed out"); return 1; }
                    var wav = task.Result;
                    double secs = wav.Pcm.Length / 2.0 / wav.SampleRate;
                    int peak = 0;
                    for (int i = 0; i + 1 < wav.Pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav.Pcm, i)));
                    AppStorage.Log("Selftest piper: phrase " + (ph + 1) + " rate=" + wav.SampleRate + " pcm_bytes=" + wav.Pcm.Length + " audio_s=" + secs.ToString("0.00") +
                                   " synth_ms=" + t0.ElapsedMilliseconds + " peak=" + peak);
                    if (wav.Pcm.Length > 4000 && peak > 1000) ok++;
                }
                AppStorage.Log("Selftest piper: piper_peak_working_set_mb=" + (engine.PeakWorkingSetBytes / 1048576));
                return ok >= 1 && ok == Math.Min(2, plan.Phrases.Count) ? 0 : 1;
            }
        }

        private static IntPtr FindEditor(IntPtr top)
        {
            foreach (var cls in new[] { "Edit", "RichEditD2DPT", "RICHEDIT50W" })
            {
                var h = Native.FindWindowEx(top, IntPtr.Zero, cls, null);
                if (h != IntPtr.Zero) return h;
            }
            // Windows 11 Notepad nests the editor one level down.
            for (IntPtr child = Native.FindWindowEx(top, IntPtr.Zero, null, null); child != IntPtr.Zero; child = Native.FindWindowEx(top, child, null, null))
            {
                var h = Native.FindWindowEx(child, IntPtr.Zero, "RichEditD2DPT", null);
                if (h != IntPtr.Zero) return h;
            }
            return IntPtr.Zero;
        }

        private static string GetText(IntPtr hwnd)
        {
            int len = (int)Native.SendMessage(hwnd, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            var sb = new StringBuilder(Math.Max(1, len + 1));
            Native.SendMessage(hwnd, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }
    }
}
