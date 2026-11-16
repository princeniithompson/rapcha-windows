using System;
using System.Drawing;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// --testform: helper windows for the v11 self-test, in their own process (Rapcha never treats its own
    /// windows as a paste target). "text": a focused TextBox; "pane": a big focusable panel with no caret
    /// (about 80% of the screen); "list": a ListBox.
    /// </summary>
    internal sealed class FieldTestForms : ApplicationContext
    {
        public const string Prefix = "Rapcha field test - ";

        public FieldTestForms()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var pane = new Form { Text = Prefix + "pane", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(wa.X + wa.Width / 10, wa.Y + wa.Height / 10, wa.Width * 8 / 10, wa.Height * 8 / 10), BackColor = Color.FromArgb(225, 232, 240) };
            pane.Controls.Add(new FocusPane { Dock = DockStyle.Fill });
            var text = new Form { Text = Prefix + "text", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(wa.Right - 520, wa.Y + 40, 480, 260) };
            var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12f) };
            text.Controls.Add(box);
            text.Shown += (s, e) => box.Focus();
            var list = new Form { Text = Prefix + "list", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(wa.Right - 520, wa.Y + 340, 480, 260) };
            var lb = new ListBox { Dock = DockStyle.Fill };
            lb.Items.AddRange(new object[] { "Kelewele", "Waakye", "Banku", "Fufu", "Jollof" });
            list.Controls.Add(lb);
            list.Shown += (s, e) => lb.Focus();
            foreach (var f in new[] { pane, list, text }) { f.FormClosed += (s, e) => ExitThread(); f.Show(); }
        }

        private sealed class FocusPane : Control
        {
            public FocusPane() { SetStyle(ControlStyles.Selectable, true); TabStop = true; BackColor = Color.FromArgb(225, 232, 240); }
            protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        }
    }
}
