using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// Gets ONLY the selected text from the foreground app after a drag-selection.
    /// 1. UI Automation TextPattern.GetSelection.
    /// 2. Only if that gave nothing, and only when the drag was over text (I-beam cursor), save the
    ///    clipboard, send Ctrl+C, read it if the clipboard actually changed, and put the clipboard back.
    /// Never reads the whole page.
    /// </summary>
    public static class SelectionReader
    {
        public const int MaxChars = 20000;

        public static async Task<string> GetAsync(IntPtr foreground, bool allowClipboard)
        {
            var why = new StringBuilder();
            var uia = await Uia.SelectedTextAsync(1500, why);
            string text = uia.Text;
            string source = "uia";
            if (uia.HasTextPattern) why.Append(" uia_textpattern");
            if (string.IsNullOrWhiteSpace(text))
            {
                var cls = TextDelivery.ClassOf(Native.GetAncestor(foreground, Native.GA_ROOT));
                if (!allowClipboard) { why.Append(" ctrl_c_skipped_not_text_cursor"); text = ""; }
                else if (cls == "ConsoleWindowClass" || cls == "CASCADIA_HOSTING_WINDOW_CLASS" || cls == "PseudoConsoleWindow")
                { why.Append(" ctrl_c_skipped_console"); text = ""; }
                else { text = await CopyViaClipboardAsync(why); source = "clipboard"; }
            }
            text = (text ?? "").Trim();
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            AppStorage.Log("Selection: source=" + source + " chars=" + text.Length + " " + why.ToString().Trim());
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

            for (int i = 0; i < 20 && TextDelivery.ModifiersDown(); i++) await Task.Delay(25);
            TextDelivery.ReleaseModifiers(why);
            uint before = Native.GetClipboardSequenceNumber();
            if (!TextDelivery.SendChord(0x1D, 0x2E)) { why.Append(" ctrl_c_blocked"); return ""; } // Ctrl + C (scancodes)

            for (int i = 0; i < 24 && Native.GetClipboardSequenceNumber() == before; i++) await Task.Delay(25);
            if (Native.GetClipboardSequenceNumber() == before) { why.Append(" nothing_copied"); return ""; }

            string text = "";
            await Task.Delay(30);
            for (int i = 0; i < 5; i++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; break; }
                catch { await Task.Delay(40); }
            }
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
