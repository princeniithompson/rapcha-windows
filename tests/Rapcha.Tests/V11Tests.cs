using System.Collections.Generic;
using System.Linq;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class FieldRulesTests
    {
        private static readonly IntRect Screen = new IntRect(0, 0, 1920, 1080);

        private static FieldFacts Facts(string type, IntRect bounds, bool caret = true) => new FieldFacts
        {
            HasFocus = true, HasKeyboardFocus = true, ControlType = type, FrameworkId = "Win32", Bounds = bounds, Screen = Screen, Win32Caret = caret,
        };

        [Fact]
        public void AcceptsNotepadEdit()
        {
            Assert.True(FieldRules.Decide(Facts("Edit", new IntRect(10, 60, 900, 600)), out var why));
            Assert.Equal("edit_caret", why);
        }

        [Theory]
        [InlineData("Pane")]
        [InlineData("Window")]
        [InlineData("List")]
        [InlineData("ListItem")]
        [InlineData("Tree")]
        public void RejectsContainersEvenWithCaret(string type)
        {
            var f = Facts(type, new IntRect(0, 0, 1900, 1000));
            f.HasValue = true; // Explorer's folder view can report a value
            Assert.False(FieldRules.Decide(f, out var why));
            Assert.StartsWith("not_text", why);
        }

        [Fact]
        public void RejectsDesktopNoFocusNoKeyboardFocusAndPassword()
        {
            var f = Facts("Edit", new IntRect(0, 0, 100, 20)); f.IsDesktop = true;
            Assert.False(FieldRules.Decide(f, out var why)); Assert.Equal("desktop", why);
            f = Facts("Edit", new IntRect(0, 0, 100, 20)); f.HasKeyboardFocus = false;
            Assert.False(FieldRules.Decide(f, out why)); Assert.Equal("no_keyboard_focus", why);
            f = Facts("Edit", new IntRect(0, 0, 100, 20)); f.IsPassword = true;
            Assert.False(FieldRules.Decide(f, out why)); Assert.Equal("password", why);
            Assert.False(FieldRules.Decide(new FieldFacts(), out why)); Assert.Equal("no_focus", why);
        }

        [Fact]
        public void NeedsACaretExceptInChromium()
        {
            var f = Facts("Edit", new IntRect(10, 10, 300, 30), caret: false);
            Assert.False(FieldRules.Decide(f, out var why)); Assert.Equal("edit_no_caret", why);
            f.TextCaret = true;
            Assert.True(FieldRules.Decide(f, out why)); Assert.Equal("edit_textcaret", why);
            f = Facts("Edit", new IntRect(10, 10, 300, 30), caret: false); f.FrameworkId = "Chrome";
            Assert.True(FieldRules.Decide(f, out why)); Assert.Equal("edit_chromium", why);
        }

        [Fact]
        public void RejectsHugeRectanglesUnlessADocumentWithCaret()
        {
            var big = new IntRect(0, 0, 1900, 1000);
            Assert.False(FieldRules.Decide(Facts("Edit", big), out var why)); Assert.Equal("edit_too_big", why);
            var doc = Facts("Document", big); doc.HasText = true;
            Assert.True(FieldRules.Decide(doc, out why));
            var chromeDoc = Facts("Document", big, caret: false); chromeDoc.HasText = true; chromeDoc.FrameworkId = "Chrome";
            Assert.False(FieldRules.Decide(chromeDoc, out why)); Assert.Equal("document_too_big", why);
        }

        [Fact]
        public void ReadOnlyValuesAndDocumentsAreNotFields()
        {
            var page = Facts("Document", new IntRect(0, 0, 800, 600)); page.HasText = true; page.HasValue = true; page.ValueReadOnly = true;
            Assert.False(FieldRules.Decide(page, out var why)); Assert.Equal("document_readonly", why);
            var combo = Facts("ComboBox", new IntRect(0, 0, 200, 24)); combo.HasValue = true;
            Assert.True(FieldRules.Decide(combo, out why)); Assert.Equal("value_writable_caret", why);
            var group = Facts("Group", new IntRect(0, 0, 200, 24));
            Assert.False(FieldRules.Decide(group, out why));
        }
    }

    public class ShortcutTests
    {
        [Theory]
        [InlineData("Ctrl+Alt+V")]
        [InlineData("Space+V")]
        [InlineData("Ctrl+Shift+F9")]
        [InlineData("Alt+;")]
        public void RoundTrips(string text)
        {
            Assert.True(Shortcut.TryParse(text, out var s));
            Assert.Equal(text, s.ToString());
        }

        [Theory]
        [InlineData("V")]
        [InlineData("Ctrl")]
        [InlineData("Ctrl+Space+V")]
        [InlineData("Bogus+V")]
        public void RejectsBadCombos(string text) => Assert.False(Shortcut.TryParse(text, out _));

        [Fact]
        public void DefaultsAndPersistence()
        {
            var d = ShortcutSet.Defaults();
            Assert.Equal("Ctrl+Alt+V", d.Tap.ToString());
            Assert.Equal("Ctrl+Alt+R", d.Read.ToString());
            Assert.True(d.Hold.IsEmpty);
            Shortcut.TryParse("Space+B", out var hold);
            d.Set(ShortcutAction.HoldToTalk, hold);
            d.Set(ShortcutAction.SelectToSpeak, new Shortcut());
            var back = ShortcutSet.FromText(d.ToText());
            Assert.Equal("Space+B", back.Hold.ToString());
            Assert.True(back.Read.IsEmpty);
            Assert.Equal("Ctrl+Alt+V", back.Tap.ToString());
            Assert.Equal(ShortcutAction.TapToTalk, back.Clash(ShortcutAction.SelectToSpeak, back.Tap));
        }

        [Fact]
        public void BuildsFromRecordedKeys()
        {
            Assert.Equal("Ctrl+Alt+V", ShortcutRules.FromKeys(new[] { 0xA2, 0xA4, 'V' }, out _).ToString());
            Assert.Equal("Space+V", ShortcutRules.FromKeys(new[] { 0x20, 'V' }, out _).ToString());
            Assert.Null(ShortcutRules.FromKeys(new[] { (int)'V' }, out var e1)); Assert.NotNull(e1);
            Assert.Null(ShortcutRules.FromKeys(new[] { 0xA2, 0xA0 }, out var e2)); Assert.NotNull(e2);
            Assert.Null(ShortcutRules.FromKeys(new[] { 0xA2, 0x20, 'V' }, out var e3)); Assert.NotNull(e3);
        }

        [Theory]
        [InlineData("Ctrl+C", true)]
        [InlineData("Ctrl+V", true)]
        [InlineData("Ctrl+Z", true)]
        [InlineData("Alt+Tab", true)]
        [InlineData("Alt+F4", true)]
        [InlineData("Win+V", true)]
        [InlineData("Ctrl+Alt+Delete", true)]
        [InlineData("Ctrl+Shift+Esc", true)]
        [InlineData("Shift+A", true)]
        [InlineData("Ctrl+Alt+V", false)]
        [InlineData("Ctrl+Alt+R", false)]
        [InlineData("Space+V", false)]
        [InlineData("Ctrl+Shift+F9", false)]
        public void WarnsAboutCommonShortcuts(string text, bool warn)
        {
            Assert.True(Shortcut.TryParse(text, out var s));
            Assert.Equal(warn, ShortcutRules.Warning(s) != null);
        }

        [Fact]
        public void SpaceAloneIsRefused()
        {
            Assert.False(Shortcut.TryParse("Space", out _));
            Assert.Null(ShortcutRules.FromKeys(new[] { 0x20 }, out var err)); Assert.NotNull(err);
            Assert.NotNull(ShortcutRules.Warning(new Shortcut { Key = 0x20 }));
        }
    }

    public class ChordMachineTests
    {
        private const int Space = 0x20, V = 'V', B = 'B', A = 'A';

        private static ChordMachine Machine()
        {
            var m = new ChordMachine();
            m.SetChords(new[] { new[] { Space, V, 1 }, new[] { Space, B, 2 } });
            return m;
        }

        [Fact]
        public void SpaceThenVTriggersAndSwallowsBoth()
        {
            var m = Machine();
            var r = m.OnKey(Space, true, 0, false); Assert.True(r.Swallow); Assert.Empty(r.Inject);
            r = m.OnKey(V, true, 200, false); Assert.True(r.Swallow); Assert.Equal(1, r.Trigger); Assert.True(r.TriggerDown);
            r = m.OnKey(V, true, 260, false); Assert.True(r.Swallow); Assert.Equal(-1, r.Trigger); // repeat
            r = m.OnKey(V, false, 400, false); Assert.True(r.Swallow); Assert.Equal(1, r.Trigger); Assert.False(r.TriggerDown);
            r = m.OnKey(Space, false, 420, false); Assert.True(r.Swallow); Assert.Equal(-1, r.Trigger); Assert.Empty(r.Inject);
            r = m.OnKey(A, true, 500, false); Assert.False(r.Swallow); // back to normal
        }

        [Fact]
        public void HoldEndsWhenEitherKeyIsReleased()
        {
            var m = Machine();
            m.OnKey(Space, true, 0, false);
            Assert.Equal(2, m.OnKey(B, true, 300, false).Trigger);
            var r = m.OnKey(Space, false, 900, false);
            Assert.Equal(2, r.Trigger); Assert.False(r.TriggerDown); Assert.True(r.Swallow);
            r = m.OnKey(B, false, 950, false);
            Assert.True(r.Swallow); Assert.Equal(-1, r.Trigger);
        }

        [Fact]
        public void LoneSpaceIsReinjectedOnRelease()
        {
            var m = Machine();
            Assert.True(m.OnKey(Space, true, 0, false).Swallow);
            var r = m.OnKey(Space, false, 80, false);
            Assert.True(r.Swallow);
            Assert.Equal(new[] { "Space↓", "Space↑" }, r.Inject.Select(k => k.ToString()).ToArray());
        }

        [Fact]
        public void FastTypingRollOverKeepsTheOrder()
        {
            var m = Machine();
            m.OnKey(Space, true, 0, false);
            var r = m.OnKey(V, true, 40, false); // " v" typed fast: not a chord
            Assert.True(r.Swallow); Assert.Equal(-1, r.Trigger);
            Assert.Equal(new[] { "Space↓", "V↓" }, r.Inject.Select(k => k.ToString()).ToArray());
            Assert.False(m.OnKey(Space, false, 60, false).Swallow);
            Assert.False(m.OnKey(V, false, 90, false).Swallow);
        }

        [Fact]
        public void OtherKeyWhileSpaceHeldIsTypedNormally()
        {
            var m = Machine();
            m.OnKey(Space, true, 0, false);
            var r = m.OnKey(A, true, 300, false);
            Assert.Equal(new[] { "Space↓", "A↓" }, r.Inject.Select(k => k.ToString()).ToArray());
            Assert.False(m.OnKey(A, false, 350, false).Swallow);
        }

        [Fact]
        public void HoldingSpaceLongRepeatsNormallyAndModifiersPassThrough()
        {
            var m = Machine();
            m.OnKey(Space, true, 0, false);
            Assert.Empty(m.OnKey(Space, true, 600, false).Inject);         // repeat inside the window: still waiting
            var r = m.OnKey(Space, true, 1300, false);                      // held past the window
            Assert.Equal(new[] { "Space↓" }, r.Inject.Select(k => k.ToString()).ToArray());
            Assert.False(m.OnKey(Space, true, 1333, false).Swallow);
            Assert.False(m.OnKey(Space, false, 1400, false).Swallow);
            Assert.False(m.OnKey(Space, true, 2000, true).Swallow);        // Ctrl+Space: not ours
        }
    }

    public class BubbleLayoutTests
    {
        [Theory]
        [InlineData(96, 1.0)]
        [InlineData(144, 1.0)]
        [InlineData(192, 1.0)]
        [InlineData(96, 1.5)]
        [InlineData(144, 2.25)]
        [InlineData(192, 2.25)]
        [InlineData(120, 1.25)]
        public void FourLinesAndTheButtonsAlwaysFit(int dpi, double textScale)
        {
            var l = BubbleLayout.Compute(dpi, textScale);
            double s = dpi / 96.0;
            Assert.Equal((int)System.Math.Round(420 * s), l.Width);
            Assert.Equal((int)System.Math.Round(120 * s), l.Height);
            Assert.True(BubbleLayout.Lines * l.LineHeight <= l.TextHeight + 0.5, "4 lines fit");
            Assert.True(l.RowY + l.RowHeight <= l.Height, "button row inside");
            Assert.True(l.RowHeight >= l.ButtonPx * 1.6, "button text fits the row");
            Assert.True(l.TextPx >= BubbleLayout.PtToPx(7.5, dpi), "text not tiny");
            Assert.True(l.TextPx <= BubbleLayout.PtToPx(11 * textScale, dpi) + 0.01);
        }

        [Fact]
        public void UsesElevenPointTextAtNormalSettings()
        {
            var l = BubbleLayout.Compute(96, 1.0);
            Assert.InRange(l.TextPx, 14.0f, 14.7f);
        }
    }
}

namespace Rapcha.Tests
{
    using System.Linq;
    using Rapcha.Core;
    using Xunit;

    public class VoiceLabelTests
    {
        [Theory]
        [InlineData("Microsoft Zira Desktop", "Zira")]
        [InlineData("Microsoft Hazel Desktop - English (Great Britain)", "Hazel")]
        [InlineData("Microsoft George", "George")]
        [InlineData("Microsoft David", "David")]
        [InlineData("Custom Voice", "Custom Voice")]
        public void ShortNames(string full, string expected) => Assert.Equal(expected, VoiceLabels.ShortName(full));

        [Fact]
        public void Labels()
        {
            Assert.Equal("Zira, female, English (US)", VoiceLabels.Label("Microsoft Zira", "Female", "en-US", false));
            Assert.Equal("David (desktop), male, English (US)", VoiceLabels.Label("Microsoft David Desktop", "Male", "en-US", true));
            Assert.Equal("Hazel, female, English (UK)", VoiceLabels.Label("Microsoft Hazel", "Female", "en-GB", false));
            Assert.Equal("Heera, female, English (India)", VoiceLabels.Label("Microsoft Heera", "Female", "en-IN", false));
            Assert.Equal("Mark, male", VoiceLabels.Label("Microsoft Mark", "Male", "", false));
        }

        [Fact]
        public void PreferredLanguageSortsFirst()
        {
            var keys = new[]
            {
                VoiceLabels.SortKey("en-GB", false, "Microsoft George", "en-US"),
                VoiceLabels.SortKey("en-US", true, "Microsoft David Desktop", "en-US"),
                VoiceLabels.SortKey("en-US", false, "Microsoft Zira", "en-US"),
                VoiceLabels.SortKey("fr-FR", false, "Microsoft Julie", "en-US"),
            };
            var order = keys.Select((k, i) => (k, i)).OrderBy(x => x.k, System.StringComparer.Ordinal).Select(x => x.i).ToArray();
            Assert.Equal(new[] { 2, 1, 0, 3 }, order);
        }

        [Fact]
        public void EstimatedTimingIsIncreasingAndInsideTheAudio()
        {
            var s = VoiceLabels.EstimateStarts(new[] { 3, 5, 2, 8, 4 }, 2000);
            Assert.Equal(5, s.Length);
            for (int i = 1; i < s.Length; i++) Assert.True(s[i] > s[i - 1]);
            Assert.True(s[0] >= 0 && s[s.Length - 1] < 2000);
        }
    }
}
