using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// v21: offline voice typing through Windows Voice Typing (Win + H). A borderless, real Win32 EDIT box laid exactly over
    /// the words area of the normal bubble (same dark card, same font, white text), in a small window that CAN be activated
    /// (only this one; every other Rapcha window stays no-activate). Windows voice typing types into it, including its grey
    /// draft text (IME/TSF composition): Rapcha never sets its text, selection or caret while it is open. Losing focus never
    /// ends anything; only Complete or Cancel do.
    /// </summary>
    public sealed class VoiceTypingEditor : Form
    {
        private readonly TextBox _box = new TextBox();
        private Rectangle _at;
        private DateTime _lastWinH = DateTime.MinValue;
        private bool _away;

        public VoiceTypingEditor(float textPx)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Rapcha";
            var card = BubblePainter.CardColor;
            BackColor = card;
            _box.Multiline = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.None;
            _box.BorderStyle = BorderStyle.None;
            _box.Dock = DockStyle.Fill;
            _box.Font = new Font(Theme.Sans, Math.Max(8f, textPx), FontStyle.Regular, GraphicsUnit.Pixel);
            _box.BackColor = card;
            _box.ForeColor = Color.White;
            _box.ShortcutsEnabled = true;
            // A click back into the box after Windows paused voice typing: take focus and start voice typing again (rate-limited).
            Deactivate += (s, e) => _away = true;   // nothing ends here; we only remember to restart voice typing on the next click
            _box.MouseDown += (s, e) => { if (_away) { _away = false; _ = FocusAndListenAsync(); } };
            Controls.Add(_box);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; }
        }

        /// <summary>Keeps the box over the bubble's words area. Never touches the text.</summary>
        public void Follow(Rectangle r)
        {
            if (r == _at || r.Width <= 0) return;
            _at = r;
            if (IsHandleCreated) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
            else Bounds = r;
        }

        /// <summary>Keeps the box above the bubble (both are top-most). No activation, no text change.</summary>
        public void Raise()
        {
            if (IsHandleCreated && Visible) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Brings the box to the front, focuses it, then presses Win + H (at most once every 2 s).</summary>
        public async Task FocusAndListenAsync()
        {
            if (!Visible) Show();
            ForceForeground(Handle);
            _box.Focus();
            for (int i = 0; i < 10 && !_box.Focused; i++) { await Task.Delay(40); ForceForeground(Handle); _box.Focus(); }
            await Task.Delay(220);
            if ((DateTime.UtcNow - _lastWinH).TotalSeconds < 2) return;
            if (!_box.Focused || Native.GetForegroundWindow() != Handle) { AppStorage.Log("Voice typing: Rapcha box could not take focus"); return; }
            _lastWinH = DateTime.UtcNow;
            _away = false;
            WinH();
        }

        /// <summary>Commits any draft (composition) text, stops voice typing while this box is in front, and returns the words.</summary>
        public async Task<string> FinishAsync(bool keep)
        {
            ForceForeground(Handle);
            _box.Focus();
            CommitComposition();
            await Task.Delay(300);
            if (Native.GetForegroundWindow() == Handle && _box.Focused) Key(0x1B, false, false);   // Esc stops Windows voice typing; only Rapcha's box sees it
            await Task.Delay(200);
            CommitComposition();
            var text = keep ? (_box.Text ?? "") : "";
            Hide();
            return text;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) return true;   // Esc never cancels; Cancel is the button
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CommitComposition()
        {
            try
            {
                IntPtr himc = ImmGetContext(_box.Handle);
                if (himc == IntPtr.Zero) return;
                ImmNotifyIME(himc, 0x0015 /* NI_COMPOSITIONSTR */, 0x0001 /* CPS_COMPLETE */, 0);
                ImmReleaseContext(_box.Handle, himc);
            }
            catch { }
        }

        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll")] private static extern bool ImmNotifyIME(IntPtr hIMC, int dwAction, int dwIndex, int dwValue);

        // ---------------- input helpers ----------------

        internal static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                var fg = Native.GetForegroundWindow();
                if (fg == hwnd || hwnd == IntPtr.Zero) return;
                uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                try
                {
                    Native.AllowSetForegroundWindow(-1);
                    Native.BringWindowToTop(hwnd);
                    Native.SetForegroundWindow(hwnd);
                }
                finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
                if (Native.GetForegroundWindow() != hwnd)
                {
                    Key(0x12, false, false); Key(0x12, true, false);   // a lone Alt press unlocks SetForegroundWindow
                    Native.SetForegroundWindow(hwnd);
                }
            }
            catch { }
        }

        private static void WinH()
        {
            Key(0x5B, false, true);
            Key(0x48, false, false);
            Key(0x48, true, false);
            Key(0x5B, true, true);
        }

        private static void Key(ushort vk, bool up, bool extended)
        {
            var i = new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? 1u : 0u) } }
            };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }
}
