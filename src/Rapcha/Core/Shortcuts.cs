using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Rapcha.Core
{
    /// <summary>Modifier flags; the values match RegisterHotKey's MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN.</summary>
    [Flags]
    public enum Mods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

    /// <summary>
    /// One keyboard shortcut: modifiers + a key ("Ctrl+Alt+V"), or a chord of two ordinary keys where the first
    /// is held while the second is pressed ("Space+V").
    /// </summary>
    public sealed class Shortcut : IEquatable<Shortcut>
    {
        public Mods Mods;
        public int Key;        // virtual-key code of the main key
        public int ChordKey;   // the held first key of a chord (e.g. VK_SPACE), 0 for a modifier shortcut

        public bool IsEmpty => Key == 0;
        public bool IsChord => ChordKey != 0;

        public static Shortcut Modifier(Mods m, int key) => new Shortcut { Mods = m, Key = key };
        public static Shortcut Chord(int first, int key) => new Shortcut { ChordKey = first, Key = key };

        public override string ToString()
        {
            if (IsEmpty) return "";
            var parts = new List<string>();
            if (IsChord) parts.Add(KeyNames.Name(ChordKey));
            else
            {
                if ((Mods & Mods.Ctrl) != 0) parts.Add("Ctrl");
                if ((Mods & Mods.Alt) != 0) parts.Add("Alt");
                if ((Mods & Mods.Shift) != 0) parts.Add("Shift");
                if ((Mods & Mods.Win) != 0) parts.Add("Win");
            }
            parts.Add(KeyNames.Name(Key));
            return string.Join("+", parts);
        }

        public static bool TryParse(string text, out Shortcut s)
        {
            s = new Shortcut();
            text = (text ?? "").Trim();
            if (text.Length == 0) return true; // empty = not set
            var parts = text.Split('+').Select(p => p.Trim()).ToList();
            // "Ctrl++" style is not used; '+' is never a key name here ("Plus" is).
            if (parts.Any(p => p.Length == 0)) return false;
            var keys = new List<int>();
            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": s.Mods |= Mods.Ctrl; continue;
                    case "alt": s.Mods |= Mods.Alt; continue;
                    case "shift": s.Mods |= Mods.Shift; continue;
                    case "win": case "windows": s.Mods |= Mods.Win; continue;
                }
                int vk = KeyNames.Parse(p);
                if (vk == 0) return false;
                keys.Add(vk);
            }
            if (keys.Count == 1) { s.Key = keys[0]; return s.Mods != Mods.None; }
            if (keys.Count == 2 && s.Mods == Mods.None && keys[0] != keys[1]) { s.ChordKey = keys[0]; s.Key = keys[1]; return true; }
            return false;
        }

        public bool Equals(Shortcut o) => o != null && o.Mods == Mods && o.Key == Key && o.ChordKey == ChordKey;
        public override bool Equals(object obj) => Equals(obj as Shortcut);
        public override int GetHashCode() => ((int)Mods * 397) ^ (Key * 31) ^ ChordKey;
    }

    public static class KeyNames
    {
        public const int Space = 0x20;
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        private static readonly Dictionary<string, int> Codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static KeyNames()
        {
            void Add(int vk, string name) { Names[vk] = name; Codes[name] = vk; }
            for (int c = 'A'; c <= 'Z'; c++) Add(c, ((char)c).ToString());
            for (int c = '0'; c <= '9'; c++) Add(c, ((char)c).ToString());
            for (int i = 1; i <= 24; i++) Add(0x6F + i, "F" + i);
            for (int i = 0; i <= 9; i++) Add(0x60 + i, "Num" + i);
            Add(0x20, "Space"); Add(0x09, "Tab"); Add(0x0D, "Enter"); Add(0x1B, "Esc"); Add(0x08, "Backspace");
            Add(0x2E, "Delete"); Add(0x2D, "Insert"); Add(0x24, "Home"); Add(0x23, "End"); Add(0x21, "PageUp"); Add(0x22, "PageDown");
            Add(0x25, "Left"); Add(0x26, "Up"); Add(0x27, "Right"); Add(0x28, "Down"); Add(0x2C, "PrintScreen"); Add(0x13, "Pause");
            Add(0x14, "CapsLock"); Add(0x91, "ScrollLock"); Add(0x6A, "NumMultiply"); Add(0x6B, "NumPlus"); Add(0x6D, "NumMinus");
            Add(0x6E, "NumDot"); Add(0x6F, "NumDivide"); Add(0x5D, "Menu");
            Add(0xBA, ";"); Add(0xBB, "="); Add(0xBC, ","); Add(0xBD, "-"); Add(0xBE, "."); Add(0xBF, "/"); Add(0xC0, "`");
            Add(0xDB, "["); Add(0xDC, "\\"); Add(0xDD, "]"); Add(0xDE, "'");
            Codes["Escape"] = 0x1B; Codes["Del"] = 0x2E; Codes["Return"] = 0x0D;
        }

        public static string Name(int vk) => Names.TryGetValue(vk, out var n) ? n : "Key" + vk.ToString(CultureInfo.InvariantCulture);

        public static int Parse(string name)
        {
            if (Codes.TryGetValue(name, out int vk)) return vk;
            if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(3), out vk) && vk > 0 && vk < 256) return vk;
            return 0;
        }

        /// <summary>Shift, Ctrl, Alt (left/right/generic) and the Windows keys.</summary>
        public static bool IsModifier(int vk) =>
            vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;

        public static Mods ModOf(int vk)
        {
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1: return Mods.Shift;
                case 0x11: case 0xA2: case 0xA3: return Mods.Ctrl;
                case 0x12: case 0xA4: case 0xA5: return Mods.Alt;
                case 0x5B: case 0x5C: return Mods.Win;
            }
            return Mods.None;
        }
    }

    public enum ShortcutAction { HoldToTalk, TapToTalk, SelectToSpeak }

    /// <summary>The three bindable actions, saved one per line in shortcuts.txt ("tap=Ctrl+Alt+V").</summary>
    public sealed class ShortcutSet
    {
        public Shortcut Hold = new Shortcut(), Tap = new Shortcut(), Read = new Shortcut();

        public static ShortcutSet Defaults()
        {
            var s = new ShortcutSet();
            Shortcut.TryParse("Ctrl+Alt+V", out s.Tap);
            Shortcut.TryParse("Ctrl+Alt+R", out s.Read);
            return s;
        }

        public Shortcut Get(ShortcutAction a) => a == ShortcutAction.HoldToTalk ? Hold : a == ShortcutAction.TapToTalk ? Tap : Read;

        public void Set(ShortcutAction a, Shortcut s)
        {
            s = s ?? new Shortcut();
            if (a == ShortcutAction.HoldToTalk) Hold = s; else if (a == ShortcutAction.TapToTalk) Tap = s; else Read = s;
        }

        public string ToText() => "hold=" + Hold + "\r\ntap=" + Tap + "\r\nread=" + Read + "\r\n";

        public static ShortcutSet FromText(string text)
        {
            var s = Defaults();
            if (text == null) return s;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                if (!Shortcut.TryParse(raw.Substring(eq + 1), out var sc)) continue;
                if (key == "hold") s.Hold = sc; else if (key == "tap") s.Tap = sc; else if (key == "read") s.Read = sc;
            }
            return s;
        }

        /// <summary>Another action already using the same combo, or null.</summary>
        public ShortcutAction? Clash(ShortcutAction a, Shortcut s)
        {
            if (s == null || s.IsEmpty) return null;
            foreach (ShortcutAction other in Enum.GetValues(typeof(ShortcutAction)))
                if (other != a && Get(other).Equals(s)) return other;
            return null;
        }
    }

    public static class ShortcutRules
    {
        private const int VK_TAB = 0x09, VK_ESC = 0x1B, VK_F4 = 0x73, VK_DELETE = 0x2E, VK_SPACE = 0x20;

        /// <summary>
        /// Builds a shortcut from the keys held at the fullest moment of a recording, in the order pressed.
        /// Returns null and a message for combos that can't work (a lone letter, only modifiers, three keys).
        /// </summary>
        public static Shortcut FromKeys(IList<int> keysInOrder, out string error)
        {
            error = null;
            var mods = Mods.None;
            var plain = new List<int>();
            foreach (var vk in keysInOrder)
            {
                if (KeyNames.IsModifier(vk)) mods |= KeyNames.ModOf(vk);
                else if (!plain.Contains(vk)) plain.Add(vk);
            }
            if (plain.Count == 0) { error = "Add a key to the modifiers, like Ctrl+Alt+V."; return null; }
            if (plain.Count == 1)
            {
                if (mods == Mods.None) { error = "A single key would fire while you type. Add Ctrl, Alt, Shift or Win, or hold a key like Space first (Space+V)."; return null; }
                return Shortcut.Modifier(mods, plain[0]);
            }
            if (plain.Count == 2 && mods == Mods.None) return Shortcut.Chord(plain[0], plain[1]);
            error = plain.Count == 2 ? "Use either modifiers (Ctrl+Alt+V) or two keys (Space+V), not both." : "Use at most two ordinary keys.";
            return null;
        }

        /// <summary>A warning (not a block) when the combo is likely to clash with Windows or common apps; null if fine.</summary>
        public static string Warning(Shortcut s)
        {
            if (s == null || s.IsEmpty) return null;
            if (s.IsChord)
            {
                if (s.ChordKey == VK_SPACE && s.Key == VK_SPACE) return "Space alone can't be a shortcut.";
                if (s.ChordKey == VK_ESC || s.Key == VK_ESC) return "Esc is used to cancel; pick another key.";
                return null;
            }
            var m = s.Mods; int k = s.Key;
            if ((m & Mods.Win) != 0) return "Windows uses most Win+key shortcuts (Win+V is clipboard history, Win+D the desktop…). It may not reach Rapcha.";
            if (m == (Mods.Ctrl | Mods.Alt) && k == VK_DELETE) return "Ctrl+Alt+Del is reserved by Windows.";
            if (m == (Mods.Ctrl | Mods.Shift) && k == VK_ESC) return "Ctrl+Shift+Esc opens Task Manager.";
            if (m == Mods.Ctrl && k == VK_ESC) return "Ctrl+Esc opens the Start menu.";
            if (m == Mods.Alt && (k == VK_TAB || k == VK_F4 || k == VK_ESC || k == VK_SPACE)) return "Alt+" + KeyNames.Name(k) + " is a Windows shortcut.";
            if (m == Mods.Ctrl && "CVXZYASRTWNPFOQ".IndexOf((char)k) >= 0 && k >= 'A' && k <= 'Z')
                return "Ctrl+" + (char)k + " is used by almost every app (" + CommonName((char)k) + ").";
            if (m == (Mods.Ctrl | Mods.Shift) && (k == 'T' || k == 'N' || k == 'Z' || k == 'V'))
                return "Ctrl+Shift+" + (char)k + " is used by browsers and many apps.";
            if (m == Mods.Shift && ((k >= 'A' && k <= 'Z') || (k >= '0' && k <= '9') || k == VK_SPACE || k >= 0xBA))
                return "Shift+" + KeyNames.Name(k) + " types a character; it would fire while you type.";
            if (m == Mods.Ctrl && (k == VK_SPACE || k == VK_TAB)) return "Ctrl+" + KeyNames.Name(k) + " is used by many apps.";
            return null;
        }

        private static string CommonName(char k)
        {
            switch (k)
            {
                case 'C': return "copy"; case 'V': return "paste"; case 'X': return "cut"; case 'Z': return "undo"; case 'Y': return "redo";
                case 'A': return "select all"; case 'S': return "save"; case 'R': return "reload"; case 'T': return "new tab"; case 'W': return "close tab";
                case 'N': return "new window"; case 'P': return "print"; case 'F': return "find"; case 'O': return "open"; default: return "quit";
            }
        }
    }

    /// <summary>
    /// The decision logic of the low-level keyboard hook for two-key chords like Space+V. The first key is held
    /// back when pressed. If the second key comes at least <see cref="MinHoldMs"/> later (and within
    /// <see cref="WindowMs"/>) both are swallowed and the action fires; on any other key, on releasing the first
    /// key alone, or after the window, the held-back key is re-injected in the original order so typing is unchanged.
    /// </summary>
    public sealed class ChordMachine
    {
        public struct Key { public int Vk; public bool Down; public Key(int vk, bool down) { Vk = vk; Down = down; } public override string ToString() => KeyNames.Name(Vk) + (Down ? "↓" : "↑"); }

        public sealed class Result
        {
            public bool Swallow;
            public readonly List<Key> Inject = new List<Key>();
            public int Trigger = -1;      // chord id
            public bool TriggerDown;      // true = pressed, false = released (hold to talk ends)
        }

        private enum State { Idle, Armed, Passthrough, Chorded }

        public int MinHoldMs = 110;   // faster overlaps are typing roll-over (" v" typed quickly), not a chord
        public int WindowMs = 1200;

        private readonly List<int[]> _chords = new List<int[]>(); // {first, second, id}
        private State _state = State.Idle;
        private int _first, _second, _id;
        private long _downAt;
        private bool _firstDown, _secondDown, _released;

        public void SetChords(IEnumerable<int[]> chords) { _chords.Clear(); _chords.AddRange(chords); Reset(); }
        public bool HasChords => _chords.Count > 0;
        public void Reset() { _state = State.Idle; _firstDown = _secondDown = _released = false; }

        private bool IsFirst(int vk) => _chords.Any(c => c[0] == vk);
        private int ChordId(int first, int second) { foreach (var c in _chords) if (c[0] == first && c[1] == second) return c[2]; return -1; }

        public Result OnKey(int vk, bool down, long timeMs, bool modifiersDown)
        {
            var r = new Result();
            switch (_state)
            {
                case State.Idle:
                    if (down && !modifiersDown && IsFirst(vk))
                    {
                        _state = State.Armed; _first = vk; _downAt = timeMs; r.Swallow = true;
                    }
                    return r;

                case State.Armed:
                    if (vk == _first)
                    {
                        r.Swallow = true;
                        if (down)
                        {
                            // auto-repeat: still waiting, unless the user is clearly just holding the key
                            if (timeMs - _downAt > WindowMs) { r.Inject.Add(new Key(_first, true)); _state = State.Passthrough; }
                        }
                        else
                        {
                            r.Inject.Add(new Key(_first, true)); r.Inject.Add(new Key(_first, false)); _state = State.Idle;
                        }
                        return r;
                    }
                    if (!down) return r; // a key held from before is let go: not ours
                    {
                        long held = timeMs - _downAt;
                        int id = ChordId(_first, vk);
                        r.Swallow = true;
                        if (id >= 0 && held >= MinHoldMs && held <= WindowMs && !modifiersDown)
                        {
                            _state = State.Chorded; _second = vk; _id = id; _firstDown = _secondDown = true; _released = false;
                            r.Trigger = id; r.TriggerDown = true;
                        }
                        else
                        {
                            r.Inject.Add(new Key(_first, true)); r.Inject.Add(new Key(vk, true)); _state = State.Passthrough;
                        }
                        return r;
                    }

                case State.Passthrough:
                    if (vk == _first && !down) _state = State.Idle;
                    return r;

                case State.Chorded:
                    if (vk != _first && vk != _second) return r;
                    r.Swallow = true;
                    if (down) return r; // repeats
                    if (vk == _second) _secondDown = false; else _firstDown = false;
                    if (!_released) { _released = true; r.Trigger = _id; r.TriggerDown = false; }
                    if (!_firstDown && !_secondDown) _state = State.Idle;
                    return r;
            }
            return r;
        }
    }
}
