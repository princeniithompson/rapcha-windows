using System;

namespace Rapcha.Core
{
    /// <summary>What UI Automation and Win32 said about the focused element (gathered in Win/Uia.cs).</summary>
    public sealed class FieldFacts
    {
        public bool HasFocus;            // UIA returned a focused element
        public bool HasKeyboardFocus;    // UIA HasKeyboardFocus
        public bool IsPassword;
        public string ControlType = "";  // "Edit", "Document", "Pane", ...
        public string FrameworkId = "";  // "Win32", "WPF", "XAML", "DirectUI", "Chrome", ...
        public string ClassName = "";
        public bool HasValue, ValueReadOnly;
        public bool HasText;             // TextPattern
        public bool TextCaret;           // TextPattern selection (a caret range) is available
        public bool Win32Caret;          // GetGUIThreadInfo reported a caret window / rect
        public bool IsDesktop;           // the desktop / taskbar / Start flyouts
        public IntRect Bounds;
        public IntRect Screen;
    }

    /// <summary>
    /// Only a real editable text field with keyboard focus and a caret gets the outline (and the paste):
    /// an Edit, a Document with TextPattern that is not read-only, or anything with a writable ValuePattern.
    /// Panes, windows, lists, the desktop and huge rectangles without a document caret are rejected.
    /// </summary>
    public static class FieldRules
    {
        public const double MaxScreenShare = 0.70;

        private static readonly string[] NeverText =
        {
            "Pane", "Window", "List", "ListItem", "Tree", "TreeItem", "Table", "DataGrid", "DataItem", "Menu", "MenuItem",
            "MenuBar", "ToolBar", "TitleBar", "Button", "SplitButton", "TabItem", "Tab", "Image", "Hyperlink", "Text",
            "CheckBox", "RadioButton", "ScrollBar", "Slider", "StatusBar", "Header", "HeaderItem", "Calendar", "ToolTip", "ProgressBar",
        };

        public static bool IsChromium(FieldFacts f) => string.Equals(f.FrameworkId, "Chrome", StringComparison.OrdinalIgnoreCase);

        public static bool Decide(FieldFacts f, out string reason)
        {
            if (!f.HasFocus) { reason = "no_focus"; return false; }
            if (f.IsDesktop) { reason = "desktop"; return false; }
            if (f.IsPassword) { reason = "password"; return false; }
            if (!f.HasKeyboardFocus) { reason = "no_keyboard_focus"; return false; }
            var type = f.ControlType ?? "";
            if (Array.IndexOf(NeverText, type) >= 0) { reason = "not_text_" + type.ToLowerInvariant(); return false; }

            bool writable = f.HasValue && !f.ValueReadOnly;
            string kind;
            if (type == "Edit")
            {
                if (f.HasValue && f.ValueReadOnly) { reason = "edit_readonly"; return false; }
                kind = "edit";
            }
            else if (type == "Document")
            {
                if (!f.HasText) { reason = "document_no_text"; return false; }
                if (f.HasValue && f.ValueReadOnly) { reason = "document_readonly"; return false; }
                kind = "document";
            }
            else if (writable) kind = "value_writable";
            else { reason = "not_text" + (type.Length > 0 ? "_" + type.ToLowerInvariant() : ""); return false; }

            bool realCaret = f.Win32Caret || f.TextCaret;
            if (!realCaret && !IsChromium(f)) { reason = kind + "_no_caret"; return false; }

            long area = (long)Math.Max(0, f.Bounds.Width) * Math.Max(0, f.Bounds.Height);
            long screen = (long)Math.Max(1, f.Screen.Width) * Math.Max(1, f.Screen.Height);
            if (area > screen * MaxScreenShare && !(type == "Document" && realCaret)) { reason = kind + "_too_big"; return false; }

            reason = kind + (f.Win32Caret ? "_caret" : f.TextCaret ? "_textcaret" : "_chromium");
            return true;
        }
    }
}
