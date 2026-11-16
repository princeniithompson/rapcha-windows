using System;
using System.Drawing;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// v17: the warning before a Low laptop downloads the lightest Whisper model anyway. Plain words, two buttons:
    /// "Download anyway" and "Cancel" (the default; Esc and closing the window also cancel).
    /// </summary>
    internal sealed class LowMemoryWarning : Form
    {
        public const string Title = "Download Whisper on this laptop?";
        public static string Body(DeviceFacts f) =>
            "This laptop has " + DeviceTiers.Describe(f) + ". That is below what Whisper is made for.\n\n" +
            "If you download the lightest model anyway, your laptop may get hot, run slowly, and use a lot of memory and battery while you talk offline. " +
            "Rapcha keeps each offline recording to 2 minutes and stops early (keeping your words) if memory runs low.\n\n" +
            "Online voice typing still uses Gemini. If Whisper doesn't work well for you, press Delete model in Settings and Rapcha goes straight back to built-in Windows speech.";

        /// <summary>Self-test: called once the dialog is on screen (to take a screenshot and press a button).</summary>
        internal static Action<LowMemoryWarning> TestHook;

        private readonly PillButton _yes = new PillButton { Text = "Download anyway (74 MB)", Primary = true };
        private readonly PillButton _no = new PillButton { Text = "Cancel" };
        internal PillButton YesButton => _yes;
        internal PillButton NoButton => _no;

        public LowMemoryWarning(DeviceFacts facts)
        {
            Text = Title;
            Icon = Program.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font(Theme.Sans, 10.5f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(22, 18, 22, 16);

            var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.White, Dock = DockStyle.Fill };
            var head = new Label { Text = "⚠  " + Title, AutoSize = true, Font = new Font(Theme.Serif, 14f), ForeColor = Color.FromArgb(150, 90, 40), Margin = new Padding(0, 0, 0, 10) };
            var body = new Label { Text = Body(facts), AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, 14), UseMnemonic = false };
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.White, WrapContents = false };
            _no.Margin = new Padding(10, 0, 0, 0);
            _yes.Margin = new Padding(0);
            _no.BackColor = _yes.BackColor = Color.White;
            row.Controls.Add(_no);
            row.Controls.Add(_yes);
            layout.Controls.Add(head);
            layout.Controls.Add(body);
            layout.Controls.Add(row);
            Controls.Add(layout);
            _yes.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            _no.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = _no;   // Enter doesn't download by accident
            CancelButton = _no;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = _no;
            var hook = TestHook;
            if (hook != null) BeginInvoke(new Action(() => hook(this)));
        }

        /// <summary>Shows the warning; true only when the user pressed "Download anyway".</summary>
        public static bool Confirm(IWin32Window owner, DeviceFacts facts)
        {
            using (var d = new LowMemoryWarning(facts))
            {
                var r = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
                return r == DialogResult.OK;
            }
        }

        public void SavePng(string path)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
