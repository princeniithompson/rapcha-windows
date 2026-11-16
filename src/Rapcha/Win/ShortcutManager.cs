using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Rapcha.Core;
using Shortcut = Rapcha.Core.Shortcut;

namespace Rapcha.Win
{
    /// <summary>
    /// The three keyboard shortcuts (Settings → Shortcuts). Modifier combos (Ctrl+Alt+V) use RegisterHotKey;
    /// hold-to-talk then watches the keys with GetAsyncKeyState until one is let go. Two-key chords (Space+V) use
    /// a low-level keyboard hook on its own thread, installed only while such a chord is set (see Core/ChordMachine).
    /// </summary>
    public sealed class ShortcutManager : IDisposable
    {
        public static ShortcutManager Current { get; private set; }

        private const int IdBase = 0x5300;
        private readonly HotkeyWindow _hotkeys = new HotkeyWindow();
        private readonly System.Windows.Forms.Timer _holdPoll = new System.Windows.Forms.Timer { Interval = 25 };
        private readonly SynchronizationContext _ui;
        private readonly Dictionary<ShortcutAction, string> _problems = new Dictionary<ShortcutAction, string>();
        private ShortcutSet _set = new ShortcutSet();
        private KeyboardHook _hook;
        private bool _suspended, _holding;
        private Shortcut _heldCombo;

        /// <summary>(action, pressed). Tap and select-to-speak only report presses; hold-to-talk reports press and release.</summary>
        public event Action<ShortcutAction, bool> Triggered;

        public ShortcutManager(bool makeCurrent = true)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _hotkeys.HotkeyPressed += OnHotkey;
            _holdPoll.Tick += (s, e) => PollHold();
            if (makeCurrent) Current = this;
        }

        public ShortcutSet Set => _set;
        public bool HookActive => _hook != null;
        public long MaxHookMicros => _hook?.MaxMicros ?? 0;

        /// <summary>Why a shortcut could not be registered (another app owns it), or null.</summary>
        public string ProblemFor(ShortcutAction a) => _problems.TryGetValue(a, out var p) ? p : null;

        public void Apply(ShortcutSet set)
        {
            _set = set ?? new ShortcutSet();
            Unregister();
            if (_suspended) return;
            var chords = new List<int[]>();
            foreach (ShortcutAction a in Enum.GetValues(typeof(ShortcutAction)))
            {
                var s = _set.Get(a);
                if (s.IsEmpty) continue;
                if (s.IsChord) { chords.Add(new[] { s.ChordKey, s.Key, (int)a }); continue; }
                if (!_hotkeys.Register(IdBase + (int)a, (uint)s.Mods | Native.MOD_NOREPEAT, (uint)s.Key))
                {
                    int err = Marshal.GetLastWin32Error();
                    _problems[a] = err == 1409 ? "Another app already uses " + s + ". Pick another combo." : "Windows refused " + s + " (error " + err + ").";
                }
            }
            if (chords.Count > 0)
            {
                if (_hook == null) _hook = new KeyboardHook(this);
                _hook.SetChords(chords);
            }
            else if (_hook != null) { _hook.Dispose(); _hook = null; }
            AppStorage.Log("Shortcuts: hold=" + Show(_set.Hold) + " tap=" + Show(_set.Tap) + " read=" + Show(_set.Read) +
                           " hook=" + (_hook != null) + (_problems.Count > 0 ? " problems=" + string.Join(",", _problems.Keys) : ""));
        }

        private static string Show(Shortcut s) => s.IsEmpty ? "none" : s.ToString();

        /// <summary>While Settings records a new combo: nothing fires and keys reach the window.</summary>
        public void Suspend() { _suspended = true; Unregister(); }
        public void Resume() { _suspended = false; Apply(_set); }

        private void Unregister()
        {
            _problems.Clear();
            _hotkeys.UnregisterAll();
            if (_hook != null) { _hook.Dispose(); _hook = null; }
            if (_holding) { _holding = false; _holdPoll.Stop(); }
        }

        private void OnHotkey(int id)
        {
            var a = (ShortcutAction)(id - IdBase);
            if (a == ShortcutAction.HoldToTalk)
            {
                if (_holding) return;
                _holding = true;
                _heldCombo = _set.Hold;
                _holdPoll.Start();
                Fire(a, true);
                return;
            }
            Fire(a, true);
        }

        private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

        private void PollHold()
        {
            var s = _heldCombo;
            bool still = s != null && Down(s.Key);
            if (still && (s.Mods & Mods.Ctrl) != 0) still = Down(0x11);
            if (still && (s.Mods & Mods.Alt) != 0) still = Down(0x12);
            if (still && (s.Mods & Mods.Shift) != 0) still = Down(0x10);
            if (still && (s.Mods & Mods.Win) != 0) still = Down(0x5B) || Down(0x5C);
            if (still) return;
            _holdPoll.Stop();
            _holding = false;
            Fire(ShortcutAction.HoldToTalk, false);
        }

        private void Fire(ShortcutAction a, bool down)
        {
            AppStorage.Log("Shortcut: " + a + (down ? " pressed" : " released"));
            Triggered?.Invoke(a, down);
        }

        internal void FireFromHook(int id, bool down) => _ui.Post(_ => Fire((ShortcutAction)id, down), null);

        public void Dispose()
        {
            Unregister();
            _holdPoll.Dispose();
            _hotkeys.Dispose();
            if (Current == this) Current = null;
        }

        private sealed class HotkeyWindow : NativeWindow, IDisposable
        {
            private readonly HashSet<int> _ids = new HashSet<int>();
            public event Action<int> HotkeyPressed;
            public HotkeyWindow() { CreateHandle(new CreateParams()); }

            public bool Register(int id, uint mods, uint vk)
            {
                if (!Native.RegisterHotKey(Handle, id, mods, vk)) return false;
                _ids.Add(id);
                return true;
            }

            public void UnregisterAll()
            {
                foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
                _ids.Clear();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_HOTKEY) HotkeyPressed?.Invoke((int)m.WParam);
                base.WndProc(ref m);
            }

            public void Dispose() { UnregisterAll(); DestroyHandle(); }
        }

        /// <summary>WH_KEYBOARD_LL on a dedicated thread with its own message loop (never blocked by the UI).</summary>
        private sealed class KeyboardHook : IDisposable
        {
            public static readonly IntPtr Magic = (IntPtr)0x52504348; // "RPCH": our own re-injected keys
            private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
            private readonly ChordMachine _machine = new ChordMachine();
            private readonly object _lock = new object();
            private readonly ShortcutManager _owner;
            private readonly Thread _thread;
            private Native.HookProc _proc;   // kept alive
            private IntPtr _hook;
            private uint _threadId;
            private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
            public long MaxMicros;

            public KeyboardHook(ShortcutManager owner)
            {
                _owner = owner;
                _thread = new Thread(Run) { IsBackground = true, Name = "Rapcha keyboard hook", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
                _ready.Wait(2000);
            }

            public void SetChords(List<int[]> chords) { lock (_lock) _machine.SetChords(chords); }

            private void Run()
            {
                _threadId = Native.GetCurrentThreadId();
                _proc = Proc;
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) AppStorage.Log("Shortcuts: keyboard hook failed (" + Marshal.GetLastWin32Error() + ")");
                _ready.Set();
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
                if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            }

            private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                long start = Stopwatch.GetTimestamp();
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo == Magic) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                int msg = (int)wParam;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (!down && msg != WM_KEYUP && msg != WM_SYSKEYUP) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                bool mods = Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C) || Down(0x10);
                ChordMachine.Result r;
                lock (_lock) r = _machine.OnKey((int)k.vkCode, down, k.time, mods && !KeyNames.IsModifier((int)k.vkCode));
                if (r.Inject.Count > 0) Inject(r.Inject);
                if (r.Trigger >= 0) _owner.FireFromHook(r.Trigger, r.TriggerDown);
                long us = (Stopwatch.GetTimestamp() - start) * 1000000 / Stopwatch.Frequency;
                if (us > MaxMicros) MaxMicros = us;
                return r.Swallow ? (IntPtr)1 : Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            private static bool IsExtended(int vk) =>
                (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x6F || vk == 0xA3 || vk == 0xA5 || vk == 0x5B || vk == 0x5C || vk == 0x5D;

            private static void Inject(List<ChordMachine.Key> keys)
            {
                var inputs = new Native.INPUT[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    int vk = keys[i].Vk;
                    uint flags = (keys[i].Down ? 0 : Native.KEYEVENTF_KEYUP) | (IsExtended(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0);
                    inputs[i] = new Native.INPUT
                    {
                        type = Native.INPUT_KEYBOARD,
                        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)Native.MapVirtualKey((uint)vk, 0), dwFlags = flags, dwExtraInfo = Magic } }
                    };
                }
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            }

            public void Dispose()
            {
                if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(1000);
            }
        }
    }
}
