using System;

namespace Rapcha.Core
{
    /// <summary>
    /// Decides whether a left-button press/release in another app was a text drag-selection.
    /// Only a real drag (press, move beyond the threshold, release) counts. Plain clicks and
    /// double/triple clicks never count, and neither does a drag that moved or resized a window
    /// (a title-bar or border drag) or one that started on Rapcha's own windows.
    /// </summary>
    public sealed class SelectionGesture
    {
        public const int DefaultThresholdPx = 8;

        public enum Kind { None, Drag }

        public struct Result
        {
            public Kind Kind;
            public int DownX, DownY, UpX, UpY;
            public bool TextCursorAtDown;
            public string Why;
        }

        private readonly int _threshold;
        private bool _down;
        private bool _downOurs;
        private bool _textCursor;
        private int _dx, _dy, _maxDistance;

        public SelectionGesture(int thresholdPx = DefaultThresholdPx) { _threshold = Math.Max(3, thresholdPx); }

        public void OnDown(int x, int y, bool onOurWindow, bool textCursor)
        {
            _down = true;
            _downOurs = onOurWindow;
            _textCursor = textCursor;
            _dx = x; _dy = y;
            _maxDistance = 0;
        }

        /// <summary>Mouse moves between press and release (optional; release distance alone also counts).</summary>
        public void OnMove(int x, int y)
        {
            if (!_down) return;
            _maxDistance = Math.Max(_maxDistance, Distance(x, y));
        }

        public Result OnUp(int x, int y, bool windowMovedOrResized)
        {
            var r = new Result { Kind = Kind.None, DownX = _dx, DownY = _dy, UpX = x, UpY = y, TextCursorAtDown = _textCursor };
            if (!_down) { r.Why = "up_without_down"; return r; }
            _down = false;
            int dist = Math.Max(_maxDistance, Distance(x, y));
            if (_downOurs) r.Why = "own_window";
            else if (dist < _threshold) r.Why = "click";            // single, double and triple clicks all end here
            else if (windowMovedOrResized) r.Why = "window_drag";
            else { r.Kind = Kind.Drag; r.Why = "drag"; }
            return r;
        }

        private int Distance(int x, int y) => Math.Max(Math.Abs(x - _dx), Math.Abs(y - _dy));
    }
}
