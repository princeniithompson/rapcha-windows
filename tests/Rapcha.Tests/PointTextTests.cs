using System.Collections.Generic;
using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class PointTextTests
    {
        private static PointText.Node N(string type, string name = "", double area = 5000, string value = "", bool pwd = false)
            => new PointText.Node { ControlType = type, Name = name, Area = area, Value = value, IsPassword = pwd };

        [Fact]
        public void DesktopIconReadsItsName()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("ListItem", "VLC media player", 7000), N("List", "Desktop", 2e6), N("Pane", "Program Manager", 2e6) }, null);
            Assert.Equal("VLC media player", c.Text);
            Assert.Equal("name", c.Source);
        }

        [Fact]
        public void BlankDesktopHasNoText()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("List", "Desktop", 2e6), N("Pane", "Desktop", 2e6), N("Pane", "Program Manager", 2e6) }, null);
            Assert.Equal("", c.Text);
            Assert.Equal("none", c.Source);
        }

        [Fact]
        public void DocumentReadsTheParagraphUnderTheClick()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Edit", "Text Editor", 9e5), N("Window", "notes.txt - Notepad", 1e6) },
                "The quick brown fox jumps over the lazy dog.\r\n");
            Assert.Equal("The quick brown fox jumps over the lazy dog.", c.Text);
            Assert.Equal("range", c.Source);
        }

        [Fact]
        public void LinkInsideAWebPageReadsTheLinkName()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Hyperlink", "Sign in", 2000), N("Document", "Example", 2e6) }, "Welcome back. Sign in to continue.");
            Assert.Equal("Sign in", c.Text);
        }

        [Fact]
        public void UnnamedIconInsideASmallNamedButtonReadsTheButton()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Image", "", 400), N("Button", "Play", 1600), N("ToolBar", "Player controls", 60000) }, null);
            Assert.Equal("Play", c.Text);
            Assert.Equal("parent_name", c.Source);
        }

        [Fact]
        public void NeverWalksUpIntoABigWindowTitle()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Custom", "", 900), N("Custom", "Some huge panel", 900000) }, null);
            Assert.Equal("", c.Text);
            c = PointText.Choose(new List<PointText.Node> { N("Custom", "", 900), N("Window", "Untitled - Paint", 5000) }, null);
            Assert.Equal("", c.Text);
        }

        [Fact]
        public void EmptyFieldReadsItsLabelAndValueIsUsed()
        {
            Assert.Equal("Search", PointText.Choose(new List<PointText.Node> { N("Edit", "Search", 6000) }, "").Text);
            Assert.Equal("42", PointText.Choose(new List<PointText.Node> { N("Spinner", "", 3000, "42") }, null).Text);
        }

        [Fact]
        public void PasswordsAreNeverRead()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Edit", "Password", 6000, "hunter2", pwd: true) }, "hunter2");
            Assert.Equal("", c.Text);
            Assert.Equal("password", c.Source);
        }

        [Fact]
        public void InvisibleMarksAndWhitespaceAreCleaned()
        {
            Assert.Equal("Recycle Bin", PointText.Choose(new List<PointText.Node> { N("ListItem", "\u200E Recycle \n  Bin\u200F ") }, null).Text);
            Assert.Equal("", PointText.Clean(" \u200B\t"));
        }

        [Fact]
        public void LongTextIsCapped()
        {
            var c = PointText.Choose(new List<PointText.Node> { N("Document", "", 2e6) }, new string('a', 9000));
            Assert.Equal(PointText.MaxChars, c.Text.Length);
        }
    }
}

namespace Rapcha.Tests
{
    public class ClickArbiterTests
    {
        [Fact]
        public void ClickElsewhereIsSwallowedBothWaysAndReadsAtPoint()
        {
            var a = new ClickArbiter(8);
            Assert.Equal(ClickArbiter.Action.Swallow, a.OnDown(300, 300, false, false));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnMove(303, 302)); // jitter: still a click
            Assert.Equal(ClickArbiter.Action.Swallow, a.OnUp(303, 302, false, out var outcome, out _));
            Assert.Equal(ClickArbiter.Outcome.ReadAtPoint, outcome);
        }

        [Fact]
        public void ClicksOnRapchaPassThrough()
        {
            var a = new ClickArbiter(8);
            Assert.Equal(ClickArbiter.Action.Pass, a.OnDown(10, 10, true, false));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnMove(80, 10));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnUp(80, 10, false, out var outcome, out _));
            Assert.Equal(ClickArbiter.Outcome.None, outcome);
        }

        [Fact]
        public void DragReplaysThePressOnceThenPassesAndReportsTheDrag()
        {
            var a = new ClickArbiter(8);
            a.OnDown(100, 100, false, true);
            Assert.Equal(ClickArbiter.Action.Pass, a.OnMove(104, 100));
            Assert.Equal(ClickArbiter.Action.SwallowAndReplayPress, a.OnMove(112, 100));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnMove(200, 101));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnUp(260, 101, false, out var outcome, out var drag));
            Assert.Equal(ClickArbiter.Outcome.Drag, outcome);
            Assert.Equal(100, drag.DownX);
            Assert.True(drag.TextCursorAtDown);
        }

        [Fact]
        public void TitleBarDragPassesButIsNotRead()
        {
            var a = new ClickArbiter(8);
            a.OnDown(100, 5, false, false);
            a.OnMove(300, 200);
            Assert.Equal(ClickArbiter.Action.Pass, a.OnUp(300, 200, true, out var outcome, out _));
            Assert.Equal(ClickArbiter.Outcome.None, outcome);
        }

        [Fact]
        public void MovesWithoutAPressAndStrayReleasesPass()
        {
            var a = new ClickArbiter(8);
            Assert.Equal(ClickArbiter.Action.Pass, a.OnMove(500, 500));
            Assert.Equal(ClickArbiter.Action.Pass, a.OnUp(500, 500, false, out var outcome, out _));
            Assert.Equal(ClickArbiter.Outcome.None, outcome);
        }

        [Fact]
        public void EachClickReadsAgain()
        {
            var a = new ClickArbiter(8);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(ClickArbiter.Action.Swallow, a.OnDown(50, 50, false, false));
                a.OnUp(50, 50, false, out var outcome, out _);
                Assert.Equal(ClickArbiter.Outcome.ReadAtPoint, outcome);
            }
        }
    }
}
