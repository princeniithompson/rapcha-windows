using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// Gets ONLY the selected text from the foreground app: UI Automation TextPattern.GetSelection first,
    /// then (if that gives nothing) save the clipboard, send Ctrl+C, read it, and put the clipboard back.
    /// Never reads the whole page.
    /// </summary>
    public static class SelectionReader
    {
        public const int MaxChars = 20000;

        public static async Task<string> GetAsync(IntPtr foreground)
        {
            var why = new StringBuilder();
            string text = await Uia.SelectedTextAsync(1500, why);
            string source = "uia";
            if (string.IsNullOrWhiteSpace(text))
            {
                var cls = TextDelivery.ClassOf(Native.GetAncestor(foreground, Native.GA_ROOT));
                if (cls == "ConsoleWindowClass" || cls == "CASCADIA_HOSTING_WINDOW_CLASS" || cls == "PseudoConsoleWindow")
                {
                    AppStorage.Log("Selection: console window, Ctrl+C skipped (" + why + ")");
                    return "";
                }
                text = await CopyViaClipboardAsync(why);
                source = "clipboard";
            }
            text = (text ?? "").Trim();
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            AppStorage.Log("Selection: source=" + source + " chars=" + text.Length + " " + why);
            return text;
        }

        private static async Task<string> CopyViaClipboardAsync(StringBuilder why)
        {
            IDataObject saved = null;
            bool hadData = false;
            try
            {
                var current = Clipboard.GetDataObject();
                if (current != null)
                {
                    var copy = new DataObject();
                    foreach (var f in current.GetFormats(false))
                    {
                        try { var d = current.GetData(f, false); if (d != null) { copy.SetData(f, false, d); hadData = true; } } catch { }
                    }
                    saved = copy;
                }
            }
            catch (Exception e) { why.Append(" save_failed=" + e.GetType().Name); }

            uint before = Native.GetClipboardSequenceNumber();
            // Wait without blocking the UI thread (the input hooks live on it), then lift anything still stuck.
            for (int i = 0; i < 20 && TextDelivery.ModifiersDown(); i++) await Task.Delay(25);
            TextDelivery.ReleaseModifiers(why);
            if (!TextDelivery.SendChord(0x1D, 0x2E)) { why.Append(" ctrl_c_blocked"); return ""; } // Ctrl + C (scancodes)

            string text = "";
            for (int i = 0; i < 24; i++) // up to ~600 ms for the app to copy
            {
                await Task.Delay(25);
                if (Native.GetClipboardSequenceNumber() != before) break;
            }
            if (Native.GetClipboardSequenceNumber() == before)
            {
                why.Append(" nothing_copied");
                return "";
            }
            await Task.Delay(30);
            for (int i = 0; i < 5 && text.Length == 0; i++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; break; }
                catch { await Task.Delay(40); }
            }
            // Put the user's clipboard back.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (saved != null && hadData) Clipboard.SetDataObject(saved, true, 3, 40);
                    else Clipboard.Clear();
                    break;
                }
                catch { await Task.Delay(40); }
            }
            return text;
        }
    }
}
