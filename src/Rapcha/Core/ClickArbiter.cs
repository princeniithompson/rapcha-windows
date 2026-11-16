using System;

namespace Rapcha.Core
{
    /// <summary>
    /// The select-to-speak "upper hand" rules for the left button, decided instantly inside the mouse hook:
    /// - On Rapcha's own windows everything passes through (ring, reading card, Settings).
    /// - Elsewhere the press is held back (swallowed). Released without moving = a read-at-point click: the
    ///   release is swallowed too, so the app underneath never sees the click.
    /// - If the pointer moves past the threshold while held, it is a drag: the held press is replayed to the
    ///   app at the original point (so text selection works) and the rest of the drag passes through; the
    ///   release then reports the drag so the selection can be read.
    /// </summary>
    public sealed class ClickArbiter
    {
        public enum Action { Pass, Swallow, SwallowAndReplayPress }
        public enum Outcome { None, ReadAtPoint, Drag }

        private enum Press { None, Own, Held, Replayed }

        private readonly SelectionGesture _gesture;
        private readonly int _threshold;
        private Press _press = Press.None;
        private int _x, _y;

        public ClickArbiter(int thresholdPx = SelectionGesture.DefaultThresholdPx)
        {
            _threshold = Math.Max(3, thresholdPx);
            _gesture = new SelectionGesture(_threshold);
        }

        public int DownX => _x;
        public int DownY => _y;
        public bool Holding => _press == Press.Held;

        public Action OnDown(int x, int y, bool onOurWindow, bool textCursor)
        {
            _x = x; _y = y;
            _gesture.OnDown(x, y, onOurWindow, textCursor);
            if (onOurWindow) { _press = Press.Own; return Action.Pass; }
            _press = Press.Held;
            return Action.Swallow;
        }

        public Action OnMove(int x, int y)
        {
            if (_press == Press.None || _press == Press.Own) return Action.Pass;
            _gesture.OnMove(x, y);
            if (_press == Press.Held && Math.Max(Math.Abs(x - _x), Math.Abs(y - _y)) >= _threshold)
            {
                _press = Press.Replayed;
                return Action.SwallowAndReplayPress; // this move is re-sent after the replayed press
            }
            return Action.Pass;
        }

        public Action OnUp(int x, int y, bool windowMovedOrResized, out Outcome outcome, out SelectionGesture.Result drag)
        {
            var was = _press;
            _press = Press.None;
            drag = _gesture.OnUp(x, y, windowMovedOrResized);
            outcome = Outcome.None;
            switch (was)
            {
                case Press.Held:
                    outcome = Outcome.ReadAtPoint;
                    return Action.Swallow;
                case Press.Replayed:
                    if (drag.Kind == SelectionGesture.Kind.Drag) outcome = Outcome.Drag;
                    return Action.Pass;
                default:
                    return Action.Pass;
            }
        }

        /// <summary>The mode was turned off mid-press: forget it (the app may get a lone release; harmless).</summary>
        public void Reset() => _press = Press.None;
    }
}
