using Rapcha.Core;
using Xunit;

namespace Rapcha.Tests
{
    public class BubblePlacementTests
    {
        private static readonly IntRect Work = new IntRect(0, 0, 1366, 728);

        [Fact]
        public void LeftDockedRingHasTheBubbleOnItsRight()
        {
            BubblePlacement.Beside(new IntRect(6, 300, 56, 56), DockEdge.Left, 420, 120, Work, 12, out int x, out int y);
            Assert.Equal(6 + 56 + 12, x);
            Assert.Equal(300 + 28 - 60, y);
        }

        [Fact]
        public void RightDockedRingHasTheBubbleOnItsLeft()
        {
            BubblePlacement.Beside(new IntRect(1304, 300, 56, 56), DockEdge.Right, 420, 120, Work, 12, out int x, out _);
            Assert.Equal(1304 - 12 - 420, x);
        }

        [Fact]
        public void FlipsWhenTheScreenSideHasNoRoom()
        {
            // A narrow screen part: a left-docked ring with no room on its right puts the bubble on the other side, kept on screen.
            var narrow = new IntRect(0, 0, 400, 728);
            BubblePlacement.Beside(new IntRect(6, 300, 56, 56), DockEdge.Left, 420, 120, narrow, 12, out int x, out _);
            Assert.InRange(x, 0, 0); // clamped inside the work area
        }

        [Fact]
        public void StaysOnScreenNearTheBottom()
        {
            BubblePlacement.Beside(new IntRect(6, 700, 56, 56), DockEdge.Left, 420, 120, Work, 12, out _, out int y);
            Assert.Equal(728 - 120, y);
        }
    }
}
