using System.Drawing;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// CI only (--selftest-ocr): a two-column page like the AI Studio home screen. Left: a heading, a paragraph
    /// and four list items. Right: two cards whose text sits level with the list items, which used to make
    /// reading jump between the columns.
    /// </summary>
    internal sealed class ColumnsTestForm : Form
    {
        /// <summary>Words that must be read in this order (column by column, like a book).</summary>
        public static readonly string[] OrderTokens = { "Build", "Start", "Review", "hospital", "bill", "Should", "Spot", "Interactive", "remix", "Ecommerce", "Sell" };

        public ColumnsTestForm()
        {
            Text = "Rapcha columns test";
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = new Rectangle(30, 40, 960, 620);
            BackColor = Color.White;
            TopMost = true;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedSingle;

            var body = new Font("Segoe UI", 12f);
            Add("Build with Gemini", new Font("Segoe UI", 22f, FontStyle.Bold), 24, 24, 420);
            Add("Start building with our newest models and tools. Try one of the ideas below to get going quickly today.", body, 24, 90, 400);
            Add("Review my code before I ship it", body, 24, 220, 400);
            Add("Find the errors in my hospital bill", body, 24, 290, 400);
            Add("Should my next car be an EV or a hybrid", body, 24, 360, 400);
            Add("Spot the red flags in my apartment lease", body, 24, 430, 400);

            Card("Interactive data visualizations", "built to remix and share", 520, 210);
            Card("Ecommerce Landing Page", "Sell your products online", 520, 350);
        }

        private void Add(string text, Font font, int x, int y, int width)
        {
            Controls.Add(new Label { Text = text, Font = font, Location = new Point(x, y), MaximumSize = new Size(width, 0), AutoSize = true, ForeColor = Color.FromArgb(30, 30, 30), BackColor = Color.Transparent });
        }

        private void Card(string title, string line, int x, int y)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(400, 110), BackColor = Color.FromArgb(238, 241, 246) };
            p.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Location = new Point(16, 18), AutoSize = true, ForeColor = Color.FromArgb(25, 25, 25) });
            p.Controls.Add(new Label { Text = line, Font = new Font("Segoe UI", 12f), Location = new Point(16, 58), AutoSize = true, ForeColor = Color.FromArgb(60, 60, 60) });
            Controls.Add(p);
        }
    }
}
