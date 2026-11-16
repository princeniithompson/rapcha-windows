using System;

namespace Rapcha.Core
{
    /// <summary>
    /// The select-to-speak gesture state machine (mouse and touchpad, including tap-to-click). Three modes that
    /// never collide:
    /// - A, two corners, free move: click and release without moving, then move the pointer more than
    ///   <see cref="MoveThresholdPx"/> within <see cref="TapWindowMs"/> of the release. Corner 1 stays where the click
    ///   was and a live box follows the pointer with no button held, for as long as it takes (pausing never reads).
    ///   A second click sets corner 2 and the box is read. Esc or a right click cancels.
    /// - B, drag: press, move more than the threshold, release: the box is read at once.
    /// - C, tap: click and release, and the pointer stays within the threshold for <see cref="TapWindowMs"/>: the
    ///   paragraph under the click is read. The overlay may prepare that speech at the release (<see cref="Kind.TapPending"/>)
    ///   and drops it if the pointer then moves away (mode A) or a drag starts.
    /// Jitter under the threshold is ignored everywhere (a touchpad tap often moves the pointer a pixel or two).
    /// A second press close to a pending tap (a double click, or a touchpad tap-and-drag) does not read twice:
    /// it either becomes a drag (B) or, released in place, the same single tap.
    /// Times are milliseconds from any monotonic clock, so tests can feed synthetic sequences.
    /// </summary>
    public sealed class BoxSelector
    {
        public const int MoveThresholdPx = 10;   // at 100% scaling; the overlay scales it with the DPI
        public const int TapWindowMs = 280;

        public enum Kind { None, Box, Tap, TapPending, CornerSet, Cancelled }

        public enum State { Idle, Pressing, Dragging, TapPending, CornerSet, SecondPress }

        public struct Result
        {
            public Kind Kind;
            public Box Box;
            public double X, Y;
        }

        private readonly double _threshold;
        private State _state = State.Idle;
        private double _ax, _ay, _cx, _cy;     // anchor (corner 1 / press point) and current point
        private double _tx, _ty;               // the pending tap point
        private long _upAt;

        public BoxSelector(double thresholdPx = MoveThresholdPx) { _threshold = Math.Max(2, thresholdPx); }

        public State Current => _state;
        public bool HasLiveBox => _state == State.Dragging || _state == State.CornerSet || _state == State.SecondPress;
        public Box LiveBox => HasLiveBox ? Box.FromCorners(_ax, _ay, _cx, _cy) : new Box();
        /// <summary>Mode A: corner 1 is set and the box follows the pointer until the second click.</summary>
        public bool WaitingForSecondCorner => _state == State.CornerSet || _state == State.SecondPress;
        public bool Busy => _state != State.Idle;
        public double TapX => _tx;
        public double TapY => _ty;

        public Result OnDown(double x, double y, long ms)
        {
            var r = new Result { Kind = Kind.None, X = x, Y = y };
            switch (_state)
            {
                case State.CornerSet:
                    _state = State.SecondPress; _cx = x; _cy = y;
                    return r;
                case State.TapPending:
                    if (Far(x, y, _tx, _ty))
                    {
                        // Moved and clicked again before any move event arrived: that is corner 2 of mode A.
                        _state = State.SecondPress; _ax = _tx; _ay = _ty; _cx = x; _cy = y;
                        r.Kind = Kind.CornerSet; r.X = _tx; r.Y = _ty;
                        return r;
                    }
                    // A double click or a touchpad tap-and-drag: a drag from here, or (released in place) the same tap.
                    _state = State.Pressing; _ax = x; _ay = y; _cx = x; _cy = y;
                    return r;
                default:
                    _state = State.Pressing; _ax = x; _ay = y; _cx = x; _cy = y;
                    return r;
            }
        }

        public Result OnMove(double x, double y, long ms)
        {
            var r = new Result { Kind = Kind.None, X = x, Y = y };
            switch (_state)
            {
                case State.Pressing:
                    if (Far(x, y, _ax, _ay)) { _state = State.Dragging; _cx = x; _cy = y; }
                    break;
                case State.TapPending:
                    if (ms - _upAt > TapWindowMs) return Tick(ms);          // the tap is due (the timer was late)
                    if (Far(x, y, _tx, _ty))
                    {
                        _state = State.CornerSet; _ax = _tx; _ay = _ty; _cx = x; _cy = y;
                        r.Kind = Kind.CornerSet; r.X = _tx; r.Y = _ty;
                    }
                    break;
                case State.Dragging:
                case State.CornerSet:
                case State.SecondPress:
                    _cx = x; _cy = y;
                    break;
            }
            return r;
        }

        public Result OnUp(double x, double y, long ms)
        {
            var r = new Result { Kind = Kind.None, X = x, Y = y };
            switch (_state)
            {
                case State.Pressing:
                    // The tap point is where the button went down: jitter while it was held doesn't move it.
                    _state = State.TapPending; _tx = _ax; _ty = _ay; _upAt = ms;
                    r.Kind = Kind.TapPending; r.X = _tx; r.Y = _ty;
                    return r;
                case State.Dragging:
                    _state = State.Idle;
                    r.Box = Box.FromCorners(_ax, _ay, x, y);
                    r.Kind = Kind.Box;
                    return r;
                case State.SecondPress:
                    _state = State.Idle;
                    if (!Far(x, y, _ax, _ay)) { r.Kind = Kind.Cancelled; return r; } // back on corner 1: nothing to read
                    r.Box = Box.FromCorners(_ax, _ay, x, y);
                    r.Kind = Kind.Box;
                    return r;
                default:
                    return r;
            }
        }

        /// <summary>Call often (every ~15-30 ms) while a tap is pending: returns <see cref="Kind.Tap"/> when it is due.</summary>
        public Result Tick(long ms)
        {
            var r = new Result { Kind = Kind.None, X = _tx, Y = _ty };
            if (_state == State.TapPending && ms - _upAt >= TapWindowMs)
            {
                _state = State.Idle;
                r.Kind = Kind.Tap;
            }
            return r;
        }

        /// <summary>Esc or a right click: forget any pending tap, corner or drag.</summary>
        public Result Cancel()
        {
            bool was = _state != State.Idle;
            _state = State.Idle;
            return new Result { Kind = was ? Kind.Cancelled : Kind.None };
        }

        private bool Far(double x, double y, double x0, double y0)
        {
            double dx = x - x0, dy = y - y0;
            return dx * dx + dy * dy > _threshold * _threshold;
        }
    }
}
