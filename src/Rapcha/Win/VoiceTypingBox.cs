using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// v21: offline voice typing through Windows Voice Typing (Win + H). A small words box with a real, focused,
    /// editable text box (it takes activation on purpose, so Windows sees a text box), then Win + H types into it.
    /// Complete returns the words; Cancel returns null. Whisper / System.Speech stay in the code but are not used offline.
    /// </summary>
    internal sealed class VoiceTypingBox : Form
    {
        private readonly TextBox _box = new TextBox();
        private readonly Button _done = new Button(), _cancel = new Button();
        private readonly TaskCompletionSource<string> _result = new TaskCompletionSource<string>();
        private bool _closing;

        public Task<string> Result => _result.Task;

        public VoiceTypingBox(Rectangle near)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.CardBorder;
            Padding = new Padding(1);
            Size = new Size(380, 150);
            Text = "Rapcha voice typing";
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Cream, Padding = new Padding(10, 8, 10, 8) };
            var hint = new Label { Dock = DockStyle.Top, Height = 20, Text = "Speak now (Windows voice typing)", ForeColor = Theme.Muted, Font = new Font(Theme.Sans, 9f) };
            _box.Multiline = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Dock = DockStyle.Fill;
            _box.Font = new Font(Theme.Sans, 11f);
            _box.ForeColor = Theme.Ink;
            _box.BackColor = Color.White;
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Cream, Padding = new Padding(0, 4, 0, 0) };
            foreach (var b in new[] { _done, _cancel })
            {
                b.FlatStyle = FlatStyle.Flat; b.AutoSize = true; b.Font = new Font(Theme.Sans, 9.5f, FontStyle.Bold);
                b.FlatAppearance.BorderColor = Theme.ButtonBorder; b.ForeColor = Theme.Ink; b.Margin = new Padding(6, 0, 0, 0);
                b.TabStop = false;
            }
            _done.Text = "Complete"; _done.BackColor = Theme.Pill;
            _cancel.Text = "Cancel"; _cancel.BackColor = Color.White;
            _done.Click += (s, e) => Finish(true);
            _cancel.Click += (s, e) => Finish(false);
            row.Controls.Add(_done); row.Controls.Add(_cancel);
            inner.Controls.Add(_box); inner.Controls.Add(row); inner.Controls.Add(hint);
            Controls.Add(inner);
            var wa = Screen.FromRectangle(near.Width > 0 ? near : Cursor.Position.IsEmpty ? Rectangle.Empty : new Rectangle(Cursor.Position, Size.Empty)).WorkingArea;
            int x = near.Width > 0 ? near.Left + near.Width / 2 - Width / 2 : wa.Left + (wa.Width - Width) / 2;
            int y = near.Width > 0 ? near.Bottom + 8 : wa.Bottom - Height - 40;
            if (y + Height > wa.Bottom) y = near.Top - Height - 8;
            Location = new Point(Math.Max(wa.Left, Math.Min(x, wa.Right - Width)), Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height)));
        }

        /// <summary>Shows the box, takes the foreground, focuses the text box, then presses Win + H.</summary>
        public async Task<bool> OpenAsync()
        {
            Show();
            ForceForeground(Handle);
            _box.Focus();
            for (int i = 0; i < 10 && !_box.Focused; i++) { await Task.Delay(50); ForceForeground(Handle); _box.Focus(); }
            await Task.Delay(250);
            if (!_box.Focused || Native.GetForegroundWindow() != Handle) { AppStorage.Log("Voice typing: Rapcha box could not take focus"); }
            WinH();
            return _box.Focused;
        }

        private async void Finish(bool ok)
        {
            if (_closing) return;
            _closing = true;
            // Stop Windows voice typing while Rapcha's own box is still in front, so the Esc can't reach another app.
            ForceForeground(Handle);
            _box.Focus();
            await Task.Delay(60);
            if (Native.GetForegroundWindow() == Handle) Key(0x1B, false, false);
            await Task.Delay(250);
            var text = ok ? (_box.Text ?? "").Trim() : null;
            Hide();
            _result.TrySetResult(text);
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) return true;          // Esc only stops voice typing; Cancel is the button
            if (keyData == (Keys.Enter | Keys.Control)) { Finish(true); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _result.TrySetResult(null);
            base.OnFormClosed(e);
        }

        // ---------------- input helpers ----------------

        internal static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                var fg = Native.GetForegroundWindow();
                if (fg == hwnd) return;
                uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                try
                {
                    Native.AllowSetForegroundWindow(-1);
                    Native.ShowWindow(hwnd, 5);
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
