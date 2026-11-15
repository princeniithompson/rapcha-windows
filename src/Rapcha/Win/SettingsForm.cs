using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>Only two things: the Gemini API key and Custom Vocabulary.</summary>
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _key = new TextBox();
        private readonly CheckBox _show = new CheckBox();
        private readonly TextBox _newWord = new TextBox();
        private readonly ListBox _words = new ListBox();
        private readonly Button _add = new Button();
        private readonly Button _remove = new Button();
        private readonly Button _save = new Button();
        private readonly Label _status = new Label();
        private readonly List<string> _terms;

        public event Action Saved;

        public SettingsForm(string message = null)
        {
            Text = "Rapcha settings";
            Icon = Program.AppIcon;
            Font = new Font("Segoe UI", 11f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(520, 560);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 9, AutoSize = false
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var keyTitle = Title("Gemini API key");
            var keyHelp = Help("Paste your key from aistudio.google.com/apikey. It is saved encrypted on this PC only.");
            _key.UseSystemPasswordChar = true;
            _key.Dock = DockStyle.Fill;
            _key.Text = AppStorage.LoadApiKey();
            _show.Text = "Show key";
            _show.AutoSize = true;
            _show.CheckedChanged += (s, e) => _key.UseSystemPasswordChar = !_show.Checked;

            var vocabTitle = Title("Custom Vocabulary");
            var vocabHelp = Help("Add names and words you want spelled your way, like \"Prince Nii\".");

            var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _newWord.Dock = DockStyle.Fill;
            _add.Text = "Add";
            _add.AutoSize = true;
            _add.Click += (s, e) => AddWord();
            _newWord.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddWord(); } };
            addRow.Controls.Add(_newWord, 0, 0);
            addRow.Controls.Add(_add, 1, 0);

            _terms = AppStorage.LoadVocabulary();
            _words.Dock = DockStyle.Fill;
            _words.IntegralHeight = false;
            _words.Height = 160;
            foreach (var t in _terms) _words.Items.Add(t);
            _words.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveWord(); };

            _remove.Text = "Remove selected";
            _remove.AutoSize = true;
            _remove.Click += (s, e) => RemoveWord();

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            _save.Text = "Save";
            _save.AutoSize = true;
            _save.Padding = new Padding(18, 4, 18, 4);
            _save.Click += async (s, e) => await SaveAsync();
            bottom.Controls.Add(_save);

            _status.AutoSize = true;
            _status.MaximumSize = new Size(480, 0);
            _status.ForeColor = Color.FromArgb(120, 30, 30);
            _status.Text = message ?? "";

            layout.Controls.Add(keyTitle);
            layout.Controls.Add(keyHelp);
            layout.Controls.Add(_key);
            layout.Controls.Add(_show);
            layout.Controls.Add(vocabTitle);
            layout.Controls.Add(vocabHelp);
            layout.Controls.Add(addRow);
            layout.Controls.Add(_words);
            layout.Controls.Add(_remove);
            layout.Controls.Add(_status);
            layout.Controls.Add(bottom);
            layout.RowCount = layout.Controls.Count;
            for (int i = 0; i < layout.RowCount; i++)
                layout.RowStyles.Add(i == 7 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            AcceptButton = null;
        }

        public void ShowMessage(string text, bool good = false)
        {
            _status.ForeColor = good ? Color.FromArgb(30, 100, 40) : Color.FromArgb(150, 30, 30);
            _status.Text = text ?? "";
        }

        private static Label Title(string text) => new Label
        {
            Text = text, AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2)
        };

        private static Label Help(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 6)
        };

        private void AddWord()
        {
            if (VocabularyList.TryAdd(_terms, _newWord.Text))
            {
                _words.Items.Insert(0, _terms[0]);
                _newWord.Clear();
                ShowMessage("");
            }
            else if (_newWord.Text.Trim().Length > 0)
            {
                ShowMessage("That word is already in the list.");
            }
            _newWord.Focus();
        }

        private void RemoveWord()
        {
            int i = _words.SelectedIndex;
            if (i < 0) return;
            var term = (string)_words.Items[i];
            _terms.RemoveAll(t => t == term);
            _words.Items.RemoveAt(i);
            if (_words.Items.Count > 0) _words.SelectedIndex = Math.Min(i, _words.Items.Count - 1);
        }

        private async Task SaveAsync()
        {
            if (_newWord.Text.Trim().Length > 0) AddWord();
            var key = _key.Text.Trim();
            try
            {
                AppStorage.SaveApiKey(key);
                AppStorage.SaveVocabulary(_terms);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not save settings: " + ex.Message);
                return;
            }
            Saved?.Invoke();
            if (LiveSession.IsPlaceholder(key))
            {
                ShowMessage("Saved. Rapcha needs a Gemini API key before it can type for you.");
                return;
            }
            ShowMessage("Saved. Checking your key…", true);
            _save.Enabled = false;
            var problem = await KeyCheck.RunAsync(key, _terms);
            _save.Enabled = true;
            if (IsDisposed) return;
            if (problem == null) ShowMessage("Saved. Your key works ✓  Click the ring and start talking.", true);
            else ShowMessage("Saved, but: " + Messages.For(problem));
        }
    }

    /// <summary>Opens one Live session and waits for setupComplete, without sending audio.</summary>
    public static class KeyCheck
    {
        public static async Task<LiveError> RunAsync(string key, IList<string> vocab)
        {
            var session = new LiveSession(key, vocab);
            LiveError failure = null;
            var failed = new TaskCompletionSource<bool>();
            session.Failed += e => { failure = e; failed.TrySetResult(true); };
            try
            {
                await session.ConnectAsync();
                var deadline = Task.Delay(12000);
                while (!session.IsSetupComplete && failure == null)
                {
                    var done = await Task.WhenAny(failed.Task, deadline, Task.Delay(100));
                    if (done == deadline) return failure ?? new LiveError(LiveErrorKind.NoInternet, "Timed out waiting for Gemini");
                }
                return failure;
            }
            finally { session.Dispose(); }
        }
    }
}
