using System;
using System.Collections.Generic;

namespace Rapcha.Core
{
    /// <summary>
    /// Picks what to read for a click at a point, from the UI Automation element under the cursor (chain[0])
    /// and a few of its parents, plus the paragraph/line of text found by TextPattern.RangeFromPoint (if any).
    /// Small named things (an icon, a button, a list item, a link) read their name; documents and web pages
    /// read the paragraph under the click; big containers (the desktop, a window, a pane) never read their
    /// own title, so a click on empty space reads nothing.
    /// </summary>
    public static class PointText
    {
        public const string NoText = "No text found at that location.";
        public const int MaxChars = 4000;
        public const double SmallParentArea = 320.0 * 120.0; // a parent bigger than this is "a container"

        public sealed class Node
        {
            public string ControlType = "";
            public string Name = "";
            public string Value = "";
            public bool IsPassword;
            public double Area;
        }

        public struct Choice
        {
            public string Text;
            public string Source; // "name", "value", "range", "parent_name", "none", "password"
        }

        private static readonly HashSet<string> NamedLeaf = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Button", "SplitButton", "ListItem", "MenuItem", "TabItem", "TreeItem", "Hyperlink", "Text", "CheckBox",
            "RadioButton", "DataItem", "HeaderItem", "Image", "TitleBar", "ComboBox", "Thumb", "Header"
        };

        private static readonly HashSet<string> Container = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Window", "Pane", "List", "Tree", "Table", "DataGrid", "Tab", "ToolBar", "MenuBar", "Menu", "StatusBar",
            "Group", "Document", "ScrollBar", "Calendar", "SemanticZoom", "AppBar"
        };

        public static bool IsContainer(string controlType) => Container.Contains(controlType ?? "");

        public static Choice Choose(IList<Node> chain, string rangeText)
        {
            if (chain == null || chain.Count == 0)
                return Pick(Clean(rangeText), "range");
            var leaf = chain[0];
            if (leaf.IsPassword) return new Choice { Text = "", Source = "password" };

            // 1. A small named control reads its name ("VLC media player", "OK", "Sign in").
            if (NamedLeaf.Contains(leaf.ControlType) && Clean(leaf.Name).Length > 0) return Pick(Clean(leaf.Name), "name");

            // 2. Documents, edits and web pages: the paragraph (or line) under the click.
            var range = Clean(rangeText);
            if (range.Length > 0) return Pick(range, "range");

            // 3. The element itself, unless it is a big container (desktop, window, pane...).
            if (!IsContainer(leaf.ControlType))
            {
                if (Clean(leaf.Name).Length > 0) return Pick(Clean(leaf.Name), "name");
                if (Clean(leaf.Value).Length > 0) return Pick(Clean(leaf.Value), "value");
            }

            // 4. Walk up only while the element had nothing and the parent is still small.
            double childArea = Math.Max(1, leaf.Area);
            for (int i = 1; i < chain.Count && i <= 3; i++)
            {
                var p = chain[i];
                if (p.IsPassword || IsContainer(p.ControlType)) break;
                if (p.Area > Math.Max(SmallParentArea, childArea * 4)) break;
                if (Clean(p.Name).Length > 0) return Pick(Clean(p.Name), "parent_name");
                if (Clean(p.Value).Length > 0) return Pick(Clean(p.Value), "parent_value");
                childArea = Math.Max(1, p.Area);
            }
            return new Choice { Text = "", Source = "none" };
        }

        private static Choice Pick(string text, string source)
        {
            if (text.Length == 0) return new Choice { Text = "", Source = "none" };
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            return new Choice { Text = text, Source = source };
        }

        /// <summary>Trims, folds whitespace and drops invisible characters (UIA names often carry U+200E etc).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (c == '\u200E' || c == '\u200F' || c == '\u200B' || c == '\uFEFF' || c == '\u202A' || c == '\u202C') continue;
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
